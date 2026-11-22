using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Streemu.Util;

namespace Streemu.Media;

/// <summary>
/// Player de lives com ffplay e recuperação da decodificação por hardware via mpv/Direct3D 11.
/// </summary>
public sealed class Viewer
{
    static readonly TimeSpan Direct3DPreparationTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan Direct3DPlaybackTimeout = TimeSpan.FromSeconds(8);
    Process? _player;
    volatile bool _stopRequested, _reopening;
    bool _direct3DActive;
    sealed record PlayerFailure(string Reason, Telemetria.MotivoMidia Motivo);
    sealed record Direct3DSample(int? AudioVideoMs, int RenderDrops, int DecoderDrops);
    volatile Direct3DSample? _direct3DSample;
    Process? _lastCpuProcess;
    TimeSpan _lastPlayerCpu;
    long _lastPlayerCpuAt;
    int? _playerCpuPerCore;
    // telemetria do caminho de mídia (Telemetria/CaminhoMidia.cs): só observa; a recuperação abaixo continua decidindo sozinha
    readonly Telemetria.CaminhoMidia.Tentativas _midia = new(Telemetria.CaminhoMidia.Viewer);

    // Chamado a cada amostra da telemetria. Reabrir o player começa uma medida nova.
    public int? SampleCpuPerCore()
    {
        var player = _player;
        try
        {
            if (player is not { HasExited: false }) return null;
            var cpu = player.TotalProcessorTime;
            long now = Environment.TickCount64;
            long elapsed = now - _lastPlayerCpuAt;
            if (!ReferenceEquals(_lastCpuProcess, player)) _playerCpuPerCore = null;
            else if (elapsed > 0) _playerCpuPerCore = (int)Math.Round((cpu - _lastPlayerCpu).TotalMilliseconds * 100 / elapsed);
            _lastCpuProcess = player;
            _lastPlayerCpuAt = now;
            _lastPlayerCpu = cpu;
            return _playerCpuPerCore;
        }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    public JsonObject PlaybackSnapshot()
    {
        if (!_direct3DActive) return Sync.Snapshot();
        var sample = _direct3DSample;
        return new JsonObject
        {
            ["medida"] = sample == null ? "sem_medida_recente" : "relogios_do_player",
            ["decodificador"] = Decoder,
            ["audio_menos_video_ms"] = sample?.AudioVideoMs,
            ["quadros_descartados_exibicao"] = sample?.RenderDrops,
            ["quadros_descartados_decodificador"] = sample?.DecoderDrops,
        };
    }
    int _relayPort, _latencyMs;
    /// <summary>Latência do SRT deste player (ms). Ler dá a que está em uso; trocar vale na PRÓXIMA abertura (<see cref="LiveUrl"/> lê a cada <c>Launch</c>), sem reabrir.</summary>
    public int LatencyMs { get => _latencyMs; set => _latencyMs = value; }
    string _passphrase = "", _title = "";
    readonly Dictionary<string, int> _errors = new();
    public ViewerSync Sync { get; } = new();

    public Viewer()
    {
        int? PlayerPid() => _player is { HasExited: false } p ? p.Id : null;
        Window = new PlayerWindow(PlayerPid);
        Audio = new PlayerAudio(PlayerPid);
    }

    /// <summary>O ffplay está sendo reaberto agora (a janela atual vai sumir).</summary>
    public bool Reopening => _reopening;
    public bool Running => _player is { HasExited: false };
    public bool UseHardware { get; set; } = true;
    public bool AvoidVulkan { get; init; }
    public Func<Task<string?>>? PrepareDirect3D { get; init; }
    string? _direct3DExecutable;
    public string Decoder => _direct3DActive ? (UsingHardware ? "d3d11va" : "inicializando_d3d11") : (UsingHardware ? "vulkan_solicitado" : "cpu");
    public event Action? VulkanFailed;
    public event Action? Direct3DRejected;
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
        _direct3DExecutable = null;
        _midia.NovaLive();
        _midia.Preparar(Telemetria.GatilhoMidia.Inicio);
        if (UseHardware && AvoidVulkan && PrepareDirect3D != null)
            _ = TryDirect3DAsync(null, restartSender: false);
        else
            Launch(UseHardware && !AvoidVulkan);
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
        _midia.NovaLive();
        _midia.Preparar(Telemetria.GatilhoMidia.Inicio);
        Launch(UseHardware);
    }

    /// <summary>Classifica uma linha do stderr do ffplay. Nulo = não é erro que interessa.</summary>
    static string? Classify(string l)
    {
        if (l.Contains("VK_ERROR_OUT_OF_DEVICE_MEMORY") || l.Contains("No slab to serve")) return "sem_memoria_de_video";
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

    /// <summary>
    /// Lê o tamanho na linha "[verbose]" que o ffplay escreve ao (re)montar os filtros do vídeo, na abertura e quando o tamanho muda no meio, ex.:
    /// <c>[ffplay_buffer @ 0x..] [verbose] w:1888 h:848 pixfmt:yuv420p tb:1/90000 fr:60/1 sar:1/1 ...</c>. Nulo se a linha não é essa.
    /// </summary>
    internal static (Size Size, double Aspect)? ParseFilterInputLine(string line)
    {
        if (!line.Contains("ffplay_buffer ") && !line.Contains("ffplay_buffer @")) return null;
        var m = System.Text.RegularExpressions.Regex.Match(line, @"\bw:(\d{2,5}) h:(\d{2,5})\b.*?\bsar:(\d+)/(\d+)");
        if (!m.Success) return null;
        int w = int.Parse(m.Groups[1].Value), h = int.Parse(m.Groups[2].Value);
        double sar = int.Parse(m.Groups[3].Value) is var n and > 0 && int.Parse(m.Groups[4].Value) is var d and > 0 ? (double)n / d : 1;
        double aspect = w * sar / h;
        if (aspect < 1.0 / 3 || aspect > 5) return null;
        return (new Size(w, h), aspect);
    }

    PlayerFailure? OnStderr(string line)
    {
        Sync.Observe(line);
        if (line.Contains(" aq=")) return null; // linha de estado do ffplay (~30 por segundo): não é erro nem o tamanho
        // "[verbose]" só serve para o tamanho: nunca é erro ("failed to ..." de filtro opcional contaria como "outros")
        if (line.Contains("[verbose] ")) { if (ParseFilterInputLine(line) is { } resized) TakeVideoSize(resized); return null; }
        if (ParseVideoLine(line) is { } video) { TakeVideoSize(video); return null; }
        var kind = Classify(line);
        if (kind == null) return null;
        lock (_errors) _errors[kind] = _errors.GetValueOrDefault(kind) + 1;
        // Teste 0.1.8 (PC do grupo, Cyberpunk aberto): o renderizador Vulkan ficou sem VRAM e repetiu o erro em loop sem sair,
        // então o "saiu em menos de 4 s" nunca disparava. Outro PC (11/11): VK_KHR_surface ausente fecha com código ZERO.
        // O erro explícito do renderizador vale mesmo sem código de falha; pacote perdido/erro de rede não pede CPU.
        if (kind == "sem_memoria_de_video") return new("falha ao alocar memória de vídeo no Vulkan", Telemetria.MotivoMidia.SemMemoria);
        if (kind == "vulkan_outro" || line.Contains("Failed to create window"))
            return new("o player não conseguiu abrir o vídeo pelo Vulkan", line.Contains("Failed to create window") ? Telemetria.MotivoMidia.JanelaNaoAbriu : Telemetria.MotivoMidia.VulkanFalhou);
        return null;
    }

    /// <summary>
    /// Guarda o tamanho e a proporção que o palco usa para a janela do ffplay (<see cref="VideoAspect"/>). Também no meio da live: o celular que gira
    /// passa de 848x1888 para 1888x848, e com a janela presa na proporção antiga o ffplay na placa esticava a imagem (2026-11-21).
    /// </summary>
    void TakeVideoSize((Size Size, double Aspect) video)
    {
        if (VideoSize == video.Size && VideoAspect is { } a && Math.Abs(a - video.Aspect) <= 0.001) return;
        string verbo = VideoSize == null ? "chega em" : "mudou para";
        Log.Info($"Player: o vídeo {verbo} {video.Size.Width}x{video.Size.Height} (proporção {video.Aspect:0.00}:1); a janela do player segue essa proporção.");
        VideoSize = video.Size;
        VideoAspect = video.Aspect;
    }

    void Launch(bool hardware)
    {
        _direct3DActive = false;
        Sync.Reset();
        Tools.PassphraseHint = _passphrase;
        var args = new List<string>
        {
            // "info" e SEM -nostats: só assim o ffplay escreve a linha "Stream #0:0 ... Video: ... 3440x1440 [SAR 1:1 ...]", de onde sai a
            // proporção do vídeo (VideoAspect). Com -nostats ele não escreve essa linha. O "level+" marca cada linha com o nível: as "[info]"
            // (essa descrição, ~15 linhas do Vulkan por abertura e a linha de estado, ~30/s) não vão para o log (Tools.IsFfplayChatter).
            // "verbose" porque a troca de tamanho NO MEIO (celular que gira) só aparece nesse nível ("ffplay_buffer ... w: h:"); medido 2026-11-21:
            // ~20 linhas a mais, só na abertura e na troca, nenhuma por quadro.
            "-hide_banner", "-loglevel", "level+verbose",
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
        if (_localTap != null) args.Add("-an");
        // O que chega do amigo é só MPEG-TS com H.264 + Opus (o que o StreamSender gera). Um app adulterado no PC dele poderia mandar bytes
        // de outro formato para explorar demuxer/decodificador do ffmpeg (o build tem centenas, incluindo hls/concat/image2, que abrem outros
        // arquivos e URLs). Por isso formato, protocolo e decodificadores são fixados ANTES da entrada (valem só para o -i seguinte).
        // codec_whitelist confere o nome do DECODIFICADOR: h264 (com -hwaccel vulkan continua sendo o "h264") e opus/libopus (os dois que o
        // build tem para Opus). Medido com ffplay (CPU, Vulkan, pipe) e com SRT em loopback: o protocolo srt sozinho basta.
        args.AddRange(new[] { "-f", "mpegts", "-format_whitelist", "mpegts",
            "-protocol_whitelist", _localTap != null ? "pipe" : "srt", "-codec_whitelist", "h264,opus,libopus" });
        args.AddRange(ExtraArgs);
        args.Add(IsPreview ? "pipe:0" : LiveUrl());
        UsingHardware = hardware;
        StartedAt = DateTime.Now;
        var lifetime = Stopwatch.StartNew();
        PlayerFailure? fallback = null;
        Process? p = null;
        var tentativa = _midia.Comecar(Telemetria.GatilhoMidia.Outro,
            Telemetria.CaminhoMidia.CaminhoDoPlayer(hardware ? "vulkan" : "cpu", hardware ? "vulkan" : "sdl", IsPreview, OpenAt));
        Size? achado = null;
        p = Tools.Start(Tools.Ffplay, args, "ffplay", redirectStdin: _localTap != null, hideWindow: true, onStderr: l =>
        {
            // vídeo confirmado no ffplay: a descrição do vídeo e depois a linha de estado com vídeo (A-V/M-V), que só vem com o laço de exibição rodando
            if (!tentativa.TemVideo && !_stopRequested)
            {
                if (achado == null && ParseVideoLine(l) is { } v) achado = v.Size;
                else if (achado is { } t && l.Contains(" aq=") && (l.Contains("A-V") || l.Contains("M-V")))
                    tentativa.VideoOk(() => new JsonObject { ["resolucao"] = Telemetria.CaminhoMidia.Resolucao(t.Width, t.Height) });
            }
            var failure = OnStderr(l);
            if (hardware && !_stopRequested && failure != null && Interlocked.CompareExchange(ref fallback, failure, null) == null)
                Tools.KillQuietly(p);
        });
        _player = p;
        if (_stopRequested) Tools.KillQuietly(p);
        // O stderr pode chegar antes de Tools.Start devolver o processo: não perder nem o erro nem a ordem de encerramento.
        if (fallback != null) Tools.KillQuietly(p);
        _tap?.Dispose();
        _tap = _localTap?.Invoke(p.StandardInput.BaseStream);
        if (_localTap != null && _tap == null) { Log.Info("Prévia da própria live: a transmissão não está rodando; fechando o player."); Tools.KillQuietly(p); }
        Audio.AttachWhenReady();
        _ = Task.Run(async () =>
        {
            // Exited pode chegar ANTES da última linha do stderr (visto em duas tentativas de outro PC).
            // WaitForExitAsync drena as leituras assíncronas e também cobre um processo que já saiu antes de registrar o observador.
            await p.WaitForExitAsync().ConfigureAwait(false);
            if (!ReferenceEquals(_player, p)) return;
            if (!_reopening && ReferenceEquals(_player, p)) { _tap?.Dispose(); _tap = null; } // a prévia solta o ouvinte do vídeo (se o player reabre, liga outro)
            if (_reopening) return; // fechei este ffplay de propósito para abrir outro em outro tamanho: o player não acabou
            int code = SafeExit(p);
            if (hardware && !_stopRequested && (fallback != null || lifetime.Elapsed.TotalSeconds < 4 && code != 0))
            {
                // o Vulkan do ffplay decodifica e desenha: o erro que derruba (memória, superfície, janela) é do lado que desenha
                var erros = ErrorCounts();
                tentativa.Falhou(Telemetria.EtapaMidia.Renderizacao, fallback?.Motivo ?? Telemetria.MotivoMidia.SaiuCedo, medidas: () => new JsonObject { ["erros"] = erros });
                if (fallback != null && !IsPreview)
                {
                    VulkanFailed?.Invoke();
                    if (PrepareDirect3D != null)
                    {
                        _midia.Preparar(Telemetria.GatilhoMidia.Recuperacao);
                        await TryDirect3DAsync(fallback.Reason, restartSender: true).ConfigureAwait(false);
                        return;
                    }
                }
                RecoverOnCpu(fallback?.Reason ?? $"ffplay não abriu na placa de vídeo (código {code})");
                return;
            }
            Log.Info($"Player fechado (código {code}).");
            Exited?.Invoke();
        });
    }

    string LiveUrl() => $"srt://127.0.0.1:{_relayPort}?mode=caller&latency={_latencyMs * 1000L}&passphrase={_passphrase}&pbkeylen=16&transtype=live&connect_timeout=10000";

    async Task TryDirect3DAsync(string? vulkanFailure, bool restartSender)
    {
        try
        {
            // A instalação pode terminar para a próxima live mesmo se esta precisar seguir na CPU.
            _direct3DExecutable = await PrepareDirect3D!().WaitAsync(Direct3DPreparationTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Info("Player: o Direct3D 11 ainda está sendo preparado.");
        }
        if (_stopRequested) return;
        if (_direct3DExecutable == null)
        {
            _midia.Comecar(Telemetria.GatilhoMidia.Outro, Telemetria.CaminhoMidia.CaminhoDoPlayer("d3d11va", "d3d11", IsPreview, OpenAt))
                .Falhou(Telemetria.EtapaMidia.Decodificacao, Telemetria.MotivoMidia.D3d11Indisponivel);
            RecoverOnCpu(vulkanFailure == null ? "Direct3D 11 indisponível" : $"{vulkanFailure}; Direct3D 11 indisponível", restartSender);
            return;
        }
        Log.Info(vulkanFailure == null ? "Player: abrindo a live com Direct3D 11."
            : $"Player: {vulkanFailure}; tentando Direct3D 11 nesta live.");
        if (restartSender) Relaunch(LaunchDirect3D);
        else LaunchDirect3D();
    }

    void LaunchDirect3D()
    {
        _direct3DActive = true;
        _direct3DSample = null;
        UsingHardware = false;
        StartedAt = DateTime.Now;
        Sync.Reset();

        Process player;
        int result = 0; // 0: aguardando vídeo; 1: D3D11 confirmado; 2: sem decodificação por hardware.
        var tentativa = _midia.Comecar(Telemetria.GatilhoMidia.Outro, Telemetria.CaminhoMidia.CaminhoDoPlayer("d3d11va", "d3d11", IsPreview, OpenAt));
        Process? opening = null;
        try
        {
            player = Tools.Start(_direct3DExecutable!, Direct3DArgs(), "mpv-live", redirectStdin: false,
                onStdout: line =>
                {
                    const string prefix = "STREEMU_D3D11 ";
                    if (line.StartsWith("STREEMU_MPV_STATUS ", StringComparison.Ordinal))
                    {
                        ObserveDirect3DStatus(line);
                        return;
                    }
                    if (!line.StartsWith(prefix, StringComparison.Ordinal)) return;
                    var fields = line[prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length == 4 && fields[0] == "d3d11va")
                    {
                        if (int.TryParse(fields[1], out int w) && int.TryParse(fields[2], out int h) && w > 0 && h > 0)
                        {
                            VideoSize = new Size(w, h);
                            if (double.TryParse(fields[3], CultureInfo.InvariantCulture, out double aspect)
                                && aspect >= 1.0 / 3 && aspect <= 5)
                                VideoAspect = aspect;
                        }
                        UsingHardware = true;
                        Volatile.Write(ref result, 1);
                        Log.Info("Player: decodificação por hardware confirmada pelo Direct3D 11.");
                        var tamanho = VideoSize;
                        tentativa.VideoOk(() => tamanho is { } t ? new JsonObject { ["resolucao"] = Telemetria.CaminhoMidia.Resolucao(t.Width, t.Height) } : null);
                    }
                    else
                    {
                        Volatile.Write(ref result, 2);
                        Tools.KillQuietly(opening);
                    }
                });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Direct3DRejected?.Invoke();
            tentativa.Falhou(Telemetria.EtapaMidia.Renderizacao, Telemetria.MotivoMidia.D3d11NaoAbriu);
            RecoverOnCpu("não consegui abrir o player Direct3D 11", restartSender: false);
            return;
        }

        opening = player;
        _player = player;
        if (_stopRequested || Volatile.Read(ref result) == 2) Tools.KillQuietly(player);
        Audio.AttachWhenReady();
        _ = ObserveDirect3DAsync();

        async Task ObserveDirect3DAsync()
        {
            var exit = player.WaitForExitAsync();
            if (await Task.WhenAny(exit, Task.Delay(Direct3DPlaybackTimeout)).ConfigureAwait(false) != exit
                && ReferenceEquals(_player, player) && Volatile.Read(ref result) == 0)
                Tools.KillQuietly(player);
            await exit.ConfigureAwait(false);
            if (!ReferenceEquals(_player, player) || _reopening) return;
            int outcome = Volatile.Read(ref result);
            if (!_stopRequested && (outcome != 1 || SafeExit(player) != 0))
            {
                if (outcome == 2) Direct3DRejected?.Invoke();
                // sem hardware ou sem vídeo em 8 s: não decodificou; com vídeo e depois saída com erro: caiu já desenhando
                var amostra = _direct3DSample;
                tentativa.Falhou(outcome == 1 ? Telemetria.EtapaMidia.Renderizacao : Telemetria.EtapaMidia.Decodificacao,
                    outcome == 2 ? Telemetria.MotivoMidia.D3d11SemHardware : Telemetria.MotivoMidia.D3d11NaoManteve,
                    medidas: () => amostra == null ? null : new JsonObject
                    {
                        ["descartes_exibicao"] = amostra.RenderDrops, ["descartes_decodificador"] = amostra.DecoderDrops,
                    });
                RecoverOnCpu("o player Direct3D 11 não conseguiu manter a reprodução");
            }
            else
                Exited?.Invoke();
        }
    }

    void ObserveDirect3DStatus(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 4 || !int.TryParse(fields[2], out int renderDrops) || !int.TryParse(fields[3], out int decoderDrops))
            return;
        int? avMs = double.TryParse(fields[1], CultureInfo.InvariantCulture, out double av) && double.IsFinite(av)
            ? (int)Math.Clamp(Math.Round(av * 1000), int.MinValue, int.MaxValue) : null;
        _direct3DSample = new Direct3DSample(avMs, renderDrops, decoderDrops);
    }

    string[] Direct3DArgs()
    {
        var size = OpenAt?.Size ?? new Size(1280, 720);
        return new[]
        {
            "--no-config", "--load-scripts=no", "--load-auto-profiles=no", "--ytdl=no",
            "--access-references=no", "--autoload-files=no", "--sid=no", "--no-osc", "--drag-and-drop=no",
            "--profile=low-latency", "--video-sync=audio", "--vo=gpu-next", "--gpu-api=d3d11",
            "--gpu-context=d3d11", "--hwdec=d3d11va", "--hwdec-software-fallback=no",
            // A entrada continua restrita ao SRT local e aos formatos que o transmissor usa.
            "--demuxer=lavf", "--demuxer-lavf-format=mpegts", "--vd=h264", "--ad=opus,libopus",
            "--demuxer-lavf-probesize=3000000", "--demuxer-lavf-analyzeduration=2.5",
            "--stream-lavf-o=protocol_whitelist=srt",
            "--demuxer-lavf-o=protocol_whitelist=srt,format_whitelist=mpegts,codec_whitelist=%17%h264,opus,libopus",
            $"--title={_title}", $"--geometry={size.Width}x{size.Height}", Borderless ? "--no-border" : "--border",
            "--msg-color=no", "--term-osd=force",
            "--term-playing-msg=STREEMU_D3D11 ${hwdec-current} ${width} ${height} ${video-params/aspect}",
            "--term-status-msg=STREEMU_MPV_STATUS ${=avsync} ${=frame-drop-count} ${=decoder-frame-drop-count}",
            "--", LiveUrl(),
        };
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
        var old = _player;
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
                _player = null;
                _reopening = false;
                if (!_stopRequested)
                {
                    _midia.Preparar(Telemetria.GatilhoMidia.ReaberturaJanela);
                    if (_direct3DActive) LaunchDirect3D();
                    else Launch(UsingHardware);
                }
            }
            finally { _reopening = false; }
        });
    }

    void RecoverOnCpu(string reason, bool restartSender = true)
    {
        FellBackToCpu = reason;
        Log.Info($"[aviso] Player: {reason}; abrindo de novo decodificando na CPU.");
        _midia.Preparar(Telemetria.GatilhoMidia.Recuperacao);
        if (restartSender) Relaunch(() => Launch(false));
        else if (!_stopRequested) Launch(false);
    }

    void Relaunch(Action launch)
    {
        if (_stopRequested) return;
        Restarting?.Invoke();
        if (Restarting != null) Thread.Sleep(1200);
        if (!_stopRequested) launch();
    }

    static int SafeExit(Process? p) { try { return p?.ExitCode ?? -1; } catch { return -1; } }

    public void Stop()
    {
        _stopRequested = true;
        _midia.Encerrar();
        Tools.KillQuietly(_player);
        _tap?.Dispose();
        _tap = null;
    }
}
