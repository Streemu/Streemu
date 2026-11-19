using System.Diagnostics;
using Streemu.Util;

namespace Streemu.Media;

/// <summary>
/// Player: ffplay em modo SRT caller, ligado ao relay local que leva os pacotes pelo túnel.
/// Tenta primeiro decodificar na placa de vídeo (renderizador Vulkan + -hwaccel vulkan); se o ffplay
/// não abrir assim, ou se a placa ficar sem memória de vídeo, reabre sozinho decodificando na CPU.
///
/// Não há degrau intermediário com d3d11va: neste ffplay o "-hwaccel" exige o renderizador Vulkan
/// ("Vulkan renderer is not available" sem -enable_vulkan), e é o renderizador (libplacebo) que fica sem memória.
/// </summary>
public sealed class Viewer
{
    Process? _ffplay;
    bool _stopRequested, _reopening;
    int _relayPort, _latencyMs;
    string _passphrase = "", _title = "";
    readonly Dictionary<string, int> _errors = new();
    public ViewerSync Sync { get; } = new();

    public Viewer()
    {
        int? PlayerPid() => _ffplay is { HasExited: false } p ? p.Id : null;
        Window = new PlayerWindow(PlayerPid);
        Audio = new PlayerAudio(PlayerPid);
    }

    /// <summary>O ffplay está sendo reaberto agora (a janela atual vai sumir).</summary>
    public bool Reopening => _reopening;
    public bool Running => _ffplay is { HasExited: false };
    public bool UseHardware { get; set; } = true;
    /// <summary>Janela do ffplay para mover e fixar no topo.</summary>
    public PlayerWindow Window { get; }
    /// <summary>Volume e mudo só do player.</summary>
    public PlayerAudio Audio { get; }
    /// <summary>Abre o ffplay sem moldura; só faz sentido com os controles do Streemu desenhados ao lado.</summary>
    public bool Borderless { get; set; }
    public bool UsingHardware { get; private set; }
    /// <summary>Por que caiu para a CPU nesta sessão do player (nulo se não caiu).</summary>
    public string? FellBackToCpu { get; private set; }
    public event Action? Exited;
    /// <summary>
    /// O player vai reabrir sozinho (ex.: caiu para a CPU). Quem transmite precisa refazer o envio ANTES: no teste da
    /// sala (PC do grupo, MH Wilds, 2026-11-06) o player novo batia no envio que ainda servia a conexão morta e ficava
    /// 8–10 s sem nada, até o SRT perceber.
    /// </summary>
    public event Action? Restarting;
    public DateTime? StartedAt { get; private set; }
    public string[] ExtraArgs { get; set; } = Array.Empty<string>();
    /// <summary>É a prévia da PRÓPRIA live (<see cref="StartLocal"/>): lê o vídeo já codificado em vez de uma conexão SRT, e é sem som (o jogo já toca).</summary>
    public bool IsPreview => _localTap != null;
    /// <summary>Se devolver um texto, a prévia não pode ir para a janela pequena (o texto diz o motivo). Só a prévia da própria live usa.</summary>
    public Func<string?>? SmallWindowGuard { get; set; }
    Func<Stream, IDisposable?>? _localTap;
    IDisposable? _tap;
    /// <summary>Onde o ffplay abre (pixels de tela). Nulo = 1280x720 onde o SDL quiser. Muda com <see cref="Reopen"/>.</summary>
    public Rectangle? OpenAt { get; set; }

    /// <summary>Tamanho do vídeo que chega (o ffplay diz ao achar o vídeo). Nulo até lá.</summary>
    public Size? VideoSize { get; private set; }
    /// <summary>
    /// Proporção do vídeo que chega (largura/altura, já com o SAR). Nulo até o ffplay achar o vídeo. Quem posiciona a janela do ffplay tem que seguir
    /// esta proporção: decodificando na placa (Vulkan/libplacebo) o ffplay ESTICA a imagem até preencher a janela, sem faixas pretas. A live de um PC do grupo
    /// (monitor 3440x1440, 21:9) aparecia esticada para quem tem monitor 16:9 porque o palco sempre dava ao vídeo uma janela 16:9 (2026-11-11).
    /// </summary>
    public double? VideoAspect { get; private set; }

    /// <summary>Erros do ffplay por tipo, desde que o player abriu (para o relatório).</summary>
    public System.Text.Json.Nodes.JsonObject ErrorCounts()
    {
        var o = new System.Text.Json.Nodes.JsonObject();
        lock (_errors) foreach (var (k, v) in _errors) o[k] = v;
        return o;
    }

    public void Start(int relayPort, string passphrase, int latencyMs, string title)
    {
        if (Running) return;
        _relayPort = relayPort; _passphrase = passphrase; _latencyMs = latencyMs; _title = title;
        _stopRequested = false;
        FellBackToCpu = null;
        VideoSize = null; VideoAspect = null;
        lock (_errors) _errors.Clear();
        Launch(UseHardware);
    }

    /// <summary>
    /// Abre o player lendo o vídeo da PRÓPRIA transmissão, sem conexão nem túnel: <paramref name="attach"/> recebe a entrada do ffplay e liga um ouvinte no
    /// vídeo já codificado (<see cref="StreamSender.AttachPreview"/>). Sem som: o áudio do jogo já está tocando no PC e repetiria com atraso.
    /// Uso: <c>viewer.StartLocal(sink => sender.AttachPreview(sink), "Streemu — minha live");</c>
    /// </summary>
    public void StartLocal(Func<Stream, IDisposable?> attach, string title)
    {
        if (Running) return;
        _localTap = attach; _title = title;
        _stopRequested = false;
        FellBackToCpu = null;
        VideoSize = null; VideoAspect = null;
        lock (_errors) _errors.Clear();
        Launch(UseHardware);
    }

    /// <summary>Classifica uma linha do stderr do ffplay. Nulo = não é erro que interessa.</summary>
    static string? Classify(string l)
    {
        if (l.Contains("VK_ERROR_OUT_OF_DEVICE_MEMORY") || l.Contains("No slab to serve") || l.Contains("Failed creating")) return "sem_memoria_de_video";
        if (l.Contains("Vulkan") && (l.Contains("rror") || l.Contains("ailed"))) return "vulkan_outro";
        if (l.Contains("error while decoding") || l.Contains("concealing") || l.Contains("non-existing PPS") || l.Contains("decode_slice")) return "decodificacao";
        if (l.Contains("PES packet size mismatch") || l.Contains("Packet corrupt") || l.Contains("continuity check")) return "pacote_perdido_ou_corrompido";
        if (l.Contains("rror") || l.Contains("ailed")) return "outros";
        return null;
    }

    /// <summary>
    /// Lê o tamanho na linha que o ffplay escreve ao achar o vídeo, ex.:
    /// <c>Stream #0:0[0x100]: Video: h264 (Main) ([27][0][0][0] / 0x001B), yuv420p(progressive), 3440x1440 [SAR 1:1 DAR 43:18], 60 fps</c>.
    /// Nulo se a linha não é essa. O SAR entra na conta (pixel não quadrado); proporção fora de 1:3..5:1 é ignorada.
    /// </summary>
    internal static (Size Size, double Aspect)? ParseVideoLine(string line)
    {
        if (!line.Contains("Stream #") || !line.Contains("Video:")) return null;
        var m = System.Text.RegularExpressions.Regex.Match(line, @", (\d{2,5})x(\d{2,5})(?: \[SAR (\d+):(\d+))?");
        if (!m.Success) return null;
        int w = int.Parse(m.Groups[1].Value), h = int.Parse(m.Groups[2].Value);
        double sar = 1;
        if (m.Groups[3].Success && int.Parse(m.Groups[3].Value) is var n and > 0 && int.Parse(m.Groups[4].Value) is var d and > 0) sar = (double)n / d;
        double aspect = w * sar / h;
        if (aspect < 1.0 / 3 || aspect > 5) return null;
        return (new Size(w, h), aspect);
    }

    string? OnStderr(string line)
    {
        Sync.Observe(line);
        if (line.Contains(" aq=")) return null; // linha de estado do ffplay (~30 por segundo): não é erro nem o tamanho
        if (ParseVideoLine(line) is { } video)
        {
            if (VideoSize != video.Size || VideoAspect is not { } a || Math.Abs(a - video.Aspect) > 0.001)
                Log.Info($"Player: o vídeo chega em {video.Size.Width}x{video.Size.Height} (proporção {video.Aspect:0.00}:1); a janela do player segue essa proporção.");
            VideoSize = video.Size;
            VideoAspect = video.Aspect;
            return null;
        }
        var kind = Classify(line);
        if (kind == null) return null;
        lock (_errors) _errors[kind] = _errors.GetValueOrDefault(kind) + 1;
        // Teste 0.1.8 (PC do grupo, Cyberpunk aberto): o renderizador Vulkan ficou sem VRAM e repetiu o erro em loop sem sair,
        // então o "saiu em menos de 4 s" nunca disparava. Outro PC (11/11): VK_KHR_surface ausente fecha com código ZERO.
        // O erro explícito do renderizador vale mesmo sem código de falha; pacote perdido/erro de rede não pede CPU.
        if (kind == "sem_memoria_de_video") return "placa de vídeo sem memória livre";
        if (kind == "vulkan_outro" || line.Contains("Failed to create window")) return "o player não conseguiu abrir o vídeo pelo Vulkan";
        return null;
    }

    void Launch(bool hardware)
    {
        Sync.Reset();
        Tools.PassphraseHint = _passphrase;
        long latencyUs = _latencyMs * 1000L;
        var args = new List<string>
        {
            // "info" e SEM -nostats: só assim o ffplay escreve a linha "Stream #0:0 ... Video: ... 3440x1440 [SAR 1:1 ...]", de onde sai a
            // proporção do vídeo (VideoAspect). Com -nostats ele não escreve essa linha. O "level+" marca cada linha com o nível: as "[info]"
            // (essa descrição, ~15 linhas do Vulkan por abertura e a linha de estado, ~30/s) não vão para o log (Tools.IsFfplayChatter).
            "-hide_banner", "-loglevel", "level+info",
            "-window_title", _title,
            "-fflags", "nobuffer", "-flags", "low_delay", "-framedrop",
            // probesize 64 KB não cabia um quadro-chave de 1440p a 13,5 Mbps: o ffplay desistia de achar o vídeo
            // ("Could not find codec parameters ... unspecified size") e ficava sem imagem (teste da sala, um amigo
            // assistindo outro). O teto sobe; quem limita a espera continua sendo o analyzeduration de 0,2 s.
            // 2026-11-09 (PC do grupo, RTX 5070, entrando numa live já aberta): 1 MB / 0,2 s também não bastam. O SPS/PPS só vem
            // a cada 2 s (um por quadro-chave), então quem entra logo depois de um IDR gasta o probe antes do próximo e o ffplay
            // fica sem achar o vídeo (38 s, depois o app derrubou o player). Medido com ffmpeg em cortes no meio do GOP a 8 Mbps:
            // 1 MB/0,2 s falha em 6 de 6, 4 MB/0,2 s e 1 MB/3 s também; 3 MB/2,5 s não falhou em nenhum. O probe para assim que
            // acha o SPS: só quem entra no meio do GOP espera até ~2 s.
            "-probesize", "3000000", "-analyzeduration", "2500000",
            "-sync", "audio", "-autoexit",
        };
        args.AddRange(WindowArgs(OpenAt));
        if (Borderless) args.Add("-noborder");
        if (hardware) args.AddRange(new[] { "-enable_vulkan", "-hwaccel", "vulkan" });
        if (_localTap != null) args.AddRange(new[] { "-an", "-f", "mpegts" });
        args.AddRange(ExtraArgs);
        args.Add(_localTap != null ? "pipe:0" : $"srt://127.0.0.1:{_relayPort}?mode=caller&latency={latencyUs}&passphrase={_passphrase}&pbkeylen=16&transtype=live&connect_timeout=10000");
        UsingHardware = hardware;
        StartedAt = DateTime.Now;
        var lifetime = Stopwatch.StartNew();
        string? fallbackReason = null;
        Process? p = null;
        p = Tools.Start(Tools.Ffplay, args, "ffplay", redirectStdin: _localTap != null, hideWindow: true, onStderr: l =>
        {
            var reason = OnStderr(l);
            if (hardware && !_stopRequested && reason != null && Interlocked.CompareExchange(ref fallbackReason, reason, null) == null)
                Tools.KillQuietly(p);
        });
        _ffplay = p;
        // O stderr pode chegar antes de Tools.Start devolver o processo: não perder nem o erro nem a ordem de encerramento.
        if (fallbackReason != null) Tools.KillQuietly(p);
        _tap?.Dispose();
        _tap = _localTap?.Invoke(p.StandardInput.BaseStream);
        if (_localTap != null && _tap == null) { Log.Info("Prévia da própria live: a transmissão não está rodando; fechando o player."); Tools.KillQuietly(p); }
        Audio.AttachWhenReady();
        _ = Task.Run(async () =>
        {
            // Exited pode chegar ANTES da última linha do stderr (visto em duas tentativas de outro PC).
            // WaitForExitAsync drena as leituras assíncronas e também cobre um processo que já saiu antes de registrar o observador.
            await p.WaitForExitAsync().ConfigureAwait(false);
            if (!ReferenceEquals(_ffplay, p)) return;
            if (!_reopening && ReferenceEquals(_ffplay, p)) { _tap?.Dispose(); _tap = null; } // a prévia solta o ouvinte do vídeo (se o player reabre, liga outro)
            if (_reopening) return; // fechei este ffplay de propósito para abrir outro em outro tamanho: o player não acabou
            int code = SafeExit(p);
            if (hardware && !_stopRequested && (fallbackReason != null || lifetime.Elapsed.TotalSeconds < 4 && code != 0))
            {
                FellBackToCpu = fallbackReason ?? $"ffplay não abriu na placa de vídeo (código {code})";
                Log.Info($"[aviso] Player: {FellBackToCpu}; abrindo de novo decodificando na CPU.");
                Relaunch();
                return;
            }
            Log.Info($"Player fechado (código {code}).");
            Exited?.Invoke();
        });
    }

    static string[] WindowArgs(Rectangle? at) => at is { } r
        ? new[] { "-x", r.Width.ToString(), "-y", r.Height.ToString(), "-left", r.X.ToString(), "-top", r.Y.ToString() }
        : new[] { "-x", "1280", "-y", "720" };

    /// <summary>
    /// Reabre o ffplay em outra posição e tamanho (a janela do ffplay não escala por fora: spike de 2026-11-06). Avisa quem transmite
    /// ANTES, como na volta para a CPU (<see cref="Restarting"/>), mantém o mesmo modo de decodificação e NÃO dispara
    /// <see cref="Exited"/>. A imagem some por alguns segundos, até o próximo quadro-chave. Nulo = tamanho normal.
    /// Uso: <c>viewer.Reopen(new Rectangle(1400, 780, 480, 270));</c>
    /// </summary>
    public void Reopen(Rectangle? at)
    {
        var old = _ffplay;
        if (old is not { HasExited: false } || _reopening) return;
        _reopening = true;
        OpenAt = at;
        Task.Run(() =>
        {
            try
            {
                Restarting?.Invoke();
                Tools.KillQuietly(old);
                Thread.Sleep(1200); // dá tempo do envio novo ficar pronto
                if (!_stopRequested) Launch(UsingHardware);
            }
            finally { _reopening = false; }
        });
    }

    /// <summary>Reabre na CPU depois de avisar quem transmite e dar tempo do envio novo ficar pronto.</summary>
    void Relaunch()
    {
        Restarting?.Invoke();
        if (Restarting != null) Thread.Sleep(1200);
        if (!_stopRequested) Launch(false);
    }

    static int SafeExit(Process? p) { try { return p?.ExitCode ?? -1; } catch { return -1; } }

    public void Stop()
    {
        _stopRequested = true;
        Tools.KillQuietly(_ffplay);
        _tap?.Dispose();
        _tap = null;
    }
}
