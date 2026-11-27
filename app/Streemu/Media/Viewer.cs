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
    /// <summary>Quanto esperar pelo mpv ao abrir a live direto pelo Direct3D 11 (o teste troca por um valor curto).</summary>
    internal static TimeSpan Direct3DPreparationTimeout = TimeSpan.FromSeconds(5);
    /// <summary>
    /// Quanto esperar pelo mpv na RECUPERAÇÃO de uma falha do Vulkan. Maior que o da abertura porque a live já está aberta e sem imagem, e cair na CPU
    /// custa mais que esperar: num PC AMD RDNA3 (2026-11-19) o mpv ainda baixava, terminou 1 s depois dos 5 s e a live inteira ficou na CPU.
    /// </summary>
    internal static TimeSpan Direct3DRecoveryPreparationTimeout = TimeSpan.FromSeconds(12);
    /// <summary>Quanto o ffplay com hardware precisa rodar sem falha, depois do vídeo confirmado, para <see cref="VulkanWorked"/> valer (o teste troca por um valor curto).</summary>
    internal static TimeSpan VulkanWorkedAfter = TimeSpan.FromSeconds(30);
    /// <summary>Quanto a espera pelo mpv precisa passar para o palco avisar "Ajustando o player..." (esperas curtas, com o mpv já instalado, não piscam texto).</summary>
    internal static TimeSpan Direct3DPreparingNoticeAfter = TimeSpan.FromMilliseconds(300);
    /// <summary>
    /// Depois da falta de memória do Vulkan o ffplay só é encerrado depois deste tempo: a linha <c>get_buffer() failed</c> (a assinatura das RDNA3) chega
    /// logo depois da do OUT_OF_DEVICE_MEMORY, e matar o ffplay na primeira a perderia.
    /// </summary>
    internal static TimeSpan MemoryFailureKillDelay = TimeSpan.FromMilliseconds(300);
    /// <summary>
    /// Depois de um reset do driver (<see cref="MotivoMidia.DispositivoRemovido"/>), quanto esperar antes da segunda tentativa do Direct3D 11. LIV-140
    /// (2026-11-23): o mpv abria ~1,4 s depois do reset, e a captura do mesmo PC ainda falhava 2,3 s depois e só voltou em 5,3 s; o D3D11 batia no driver
    /// ainda voltando e a live caía na CPU. O teste troca por um valor curto.
    /// </summary>
    internal static TimeSpan Direct3DRetryDelay = TimeSpan.FromMilliseconds(1500);
    /// <summary>Até quanto depois do reset do driver a primeira falha do Direct3D 11 ainda vale uma segunda tentativa (passado isso, não foi o driver voltando).</summary>
    internal static TimeSpan Direct3DRetryWindow = TimeSpan.FromSeconds(12);
    /// <summary>
    /// Depois que o mpv sai sozinho de uma live que ele já estava mostrando, quanto esperar antes de chamar isso de queda (<see cref="Drop"/>). Quando
    /// quem transmite encerra a live, o SRT fecha e o mpv sai alguns instantes antes de a mensagem "parou de transmitir" chegar pela sala; o Stop()
    /// dessa mensagem, dentro deste prazo, tira o aviso e o evento de telemetria (não é queda). O teste troca por um valor curto.
    /// </summary>
    internal static TimeSpan DropGrace = TimeSpan.FromSeconds(3);
    /// <summary>De quanto em quanto tempo a sincronia do mpv vai para o log (a linha chega ~20 vezes por segundo).</summary>
    internal static readonly TimeSpan StatusLogEvery = TimeSpan.FromSeconds(10);
    static readonly TimeSpan Direct3DPlaybackTimeout = TimeSpan.FromSeconds(8);
    Process? _player;
    volatile bool _stopRequested, _reopening;
    // a segunda tentativa do Direct3D 11 depois do reset do driver vale uma vez por recuperação
    int _retryDirect3DAfterReset;
    long _deviceLostAtMs;
    bool _direct3DActive;
    sealed record PlayerFailure(string Reason, Telemetria.MotivoMidia Motivo);
    /// <summary>A última linha de estado do mpv (<c>--term-status-msg</c>): áudio menos vídeo, descartes e os FPS que o mpv lê do fluxo (nulo se não disse).</summary>
    internal sealed record Direct3DSample(int? AudioVideoMs, int RenderDrops, int DecoderDrops, int? Fps = null);
    volatile Direct3DSample? _direct3DSample;

    /// <summary>
    /// O mpv terminou ("End of file") numa live que já estava mostrando, sem a pessoa fechar a janela, sem o app pedir e sem o transmissor encerrar a live.
    /// <paramref name="PlayerBehind"/>: o player é que não acompanhou (o SRT estourou e o mpv estava com atraso grande ou descartando quadros); senão
    /// a conexão caiu por outro motivo (internet de quem transmite ou de quem assiste, PC do transmissor), que daqui não dá para separar.
    /// LIV-178 (2026-11-25): numa live de 3440x1370 a 120 FPS o buffer do SRT enchia em 1m52s, o mpv saía e a janela sumia sem aviso, 7 vezes.
    /// </summary>
    public sealed record PlayerDrop(bool PlayerBehind, int? Fps, int WatchedSeconds)
    {
        /// <summary>O aviso para quem assiste: o motivo provável e o que fazer. Os FPS só entram na dica quando passam de 60.</summary>
        public string Notice() => PlayerBehind
            ? "O player não conseguiu acompanhar a live e a conexão caiu. Entre de novo; se repetir, "
                + (Fps is > 60 ? "peça ao amigo para transmitir a 60 FPS." : "peça ao amigo para baixar a qualidade da transmissão.")
            : "A conexão caiu e a live fechou por aqui. Entre de novo; se repetir, veja se a internet de quem transmite está estável.";
    }

    /// <summary>Áudio menos vídeo (em módulo) a partir do qual o mpv conta como atrasado, para separar "o player não acompanhou" de "a conexão caiu".</summary>
    internal const int PlayerBehindAvMs = 2000;
    /// <summary>Quadros descartados (exibição + decodificador) nos últimos 10 a 20 s a partir dos quais o mpv conta como não acompanhando.</summary>
    internal const int PlayerBehindDrops = 10;

    /// <summary>
    /// O player não acompanhou a live: o SRT do ffmpeg estourou (<paramref name="srtSignal"/>) <b>e</b> a última amostra do mpv mostrava atraso grande
    /// (<see cref="PlayerBehindAvMs"/>, para qualquer lado) ou os descartes vinham subindo (<see cref="PlayerBehindDrops"/>). O SRT estourando sozinho também
    /// acontece quando a rede do transmissor cai, então ele sozinho não acusa o player.
    /// </summary>
    internal static bool PlayerFellBehind(bool srtSignal, Direct3DSample? last, int dropsGrowth) =>
        srtSignal && (last?.AudioVideoMs is { } av && Math.Abs((long)av) > PlayerBehindAvMs || dropsGrowth >= PlayerBehindDrops);

    /// <summary>
    /// Preenchido antes de <see cref="Exited"/> quando o player saiu sozinho (ver <see cref="PlayerDrop"/>); nulo quando alguém pediu para parar, a pessoa
    /// fechou a janela ou o player já saiu de outro jeito. Quem trata o <see cref="Exited"/> mostra o aviso só se isto não for nulo.
    /// </summary>
    public volatile PlayerDrop? Drop;

    /// <summary>
    /// O que se aprende das linhas do mpv (stdout e stderr) numa abertura: o SRT do ffmpeg que estoura e o motivo que o mpv dá ao sair
    /// ("Exiting... (Quit)" quando a janela é fechada, "(End of file)" quando o vídeo acaba ou a conexão cai).
    /// </summary>
    sealed class Direct3DRun
    {
        public readonly Stopwatch Playing = new();
        public volatile bool SrtFull, SrtSequence;
        public volatile string? ExitReason;
        public long LastStatusLogMs;
        // descartes (exibição + decodificador) vistos há 10 a 20 s e agora, para saber se vinham subindo quando o mpv saiu
        long _checkpointMs;
        int _checkpointDrops, _previousCheckpointDrops, _lastDrops;

        public void Track(Direct3DSample sample, long nowMs)
        {
            _lastDrops = sample.RenderDrops + sample.DecoderDrops;
            if (_checkpointMs == 0) { _checkpointMs = nowMs; _checkpointDrops = _previousCheckpointDrops = _lastDrops; }
            else if (nowMs - _checkpointMs >= (long)StatusLogEvery.TotalMilliseconds)
            {
                _previousCheckpointDrops = _checkpointDrops;
                _checkpointDrops = _lastDrops;
                _checkpointMs = nowMs;
            }
        }

        /// <summary>Quadros descartados desde a medida de 10 a 20 s atrás.</summary>
        public int DropsGrowth => _lastDrops - _previousCheckpointDrops;

        public void Observe(string line)
        {
            if (line.Contains("No room to store", StringComparison.OrdinalIgnoreCase)) SrtFull = true;
            if (line.Contains("SEQUENCE DISCREPANCY", StringComparison.OrdinalIgnoreCase)) SrtSequence = true;
            if (ParseMpvExitReason(line) is { } reason) ExitReason = reason;
        }
    }

    /// <summary>"Exiting... (End of file)" → "End of file"; "Exiting... (Quit)" → "Quit". Nulo se a linha não é essa.</summary>
    internal static string? ParseMpvExitReason(string line)
    {
        const string prefix = "Exiting... (";
        var l = line.Trim();
        return l.StartsWith(prefix, StringComparison.Ordinal) && l.EndsWith(')') && l.Length > prefix.Length + 1 ? l[prefix.Length..^1] : null;
    }

    /// <summary>
    /// A linha <c>STREEMU_MPV_STATUS av descartes_exibicao descartes_decodificador [fps]</c> (ver <see cref="Direct3DArgs"/>); "nan" no áudio-vídeo
    /// (ainda sem áudio) vira sem medida. Aceita a linha de 4 campos de antes (sem os FPS). Nulo se não é essa linha.
    /// </summary>
    internal static Direct3DSample? ParseMpvStatus(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length is not (4 or 5) || fields[0] != "STREEMU_MPV_STATUS"
            || !int.TryParse(fields[2], out int renderDrops) || !int.TryParse(fields[3], out int decoderDrops))
            return null;
        int? avMs = double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double av) && double.IsFinite(av)
            ? (int)Math.Clamp(Math.Round(av * 1000), int.MinValue, int.MaxValue) : null;
        int? fps = fields.Length == 5 && double.TryParse(fields[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double f) && f is > 0 and < 1000
            ? (int)Math.Round(f) : null;
        return new Direct3DSample(avMs, renderDrops, decoderDrops, fps);
    }

    /// <summary>A linha que vai para o log, de tempos em tempos, com a última sincronia do mpv (a live de 120 FPS do LIV-178 chegou com 0 descartes e o vídeo na metade da velocidade).</summary>
    internal static string StatusLogText(Direct3DSample s) =>
        $"Player (mpv): áudio menos vídeo {(s.AudioVideoMs is { } av ? $"{av} ms" : "sem medida")}, quadros descartados {s.RenderDrops} na exibição e {s.DecoderDrops} no decodificador"
        + (s.Fps is { } fps ? $", {fps} FPS no fluxo." : ".");

    /// <summary>Verdadeiro uma vez a cada <paramref name="every"/>; <paramref name="lastMs"/> começa em 0 (a primeira amostra sempre vai para o log).</summary>
    internal static bool StatusLogDue(ref long lastMs, long nowMs, TimeSpan every)
    {
        if (lastMs != 0 && nowMs - lastMs < (long)every.TotalMilliseconds) return false;
        lastMs = nowMs;
        return true;
    }
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
    /// <summary>
    /// O ffplay com Vulkan falhou de forma explícita (também na prévia da própria live). Leva o motivo (quem decide lembrar a falha só conta memória e
    /// Vulkan, não janela) e se a falha tem a assinatura da decodificação: falta de memória seguida de <c>get_buffer() failed</c> no mesmo ffplay. VRAM cheia
    /// de verdade dá falta de memória no renderizador, sem essa linha.
    /// </summary>
    public event Action<Telemetria.MotivoMidia, bool>? VulkanFailed;
    /// <summary>
    /// O app está esperando o mpv (Direct3D 11) ficar pronto, por mais que <see cref="Direct3DPreparingNoticeAfter"/>: liga ao passar do tempo e desliga
    /// quando a espera acaba (o palco mostra "Ajustando o player para a sua placa de vídeo..." no lugar do vídeo, que ainda não abriu).
    /// </summary>
    public event Action<bool>? PreparingDirect3D;
    volatile bool _preparingDirect3D;
    /// <summary>Verdadeiro enquanto <see cref="PreparingDirect3D"/> está ligado (o palco lê a cada 250 ms).</summary>
    public bool IsPreparingDirect3D => _preparingDirect3D;
    void SetPreparingDirect3D(bool on)
    {
        if (_preparingDirect3D == on) return;
        _preparingDirect3D = on;
        PreparingDirect3D?.Invoke(on);
    }
    /// <summary>O Vulkan deu vídeo e seguiu rodando sem falha por <see cref="VulkanWorkedAfter"/>: nesta placa ele funciona. Dispara uma vez por abertura do ffplay.</summary>
    public event Action? VulkanWorked;
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
        Drop = null;
        VideoSize = null; VideoAspect = null;
        lock (_errors) _errors.Clear();
        _direct3DExecutable = null;
        Volatile.Write(ref _retryDirect3DAfterReset, 0);
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
        // antes do "Vulkan ... ailed": a primeira linha do reset ("[vk @ ...] Unable to submit command buffer: VK_ERROR_DEVICE_LOST") não tem nenhum dos dois
        if (l.Contains("VK_ERROR_DEVICE_LOST")) return "placa_reiniciou";
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
        // Reset do driver (LIV-140, 2026-11-23: a captura do mesmo PC deu 887a0026 no mesmo milissegundo): não é defeito do Vulkan nesta placa. Recupera
        // como as outras falhas, mas com DispositivoRemovido, que a VulkanMemoria não grava no estado.json.
        if (kind == "placa_reiniciou") return new("a placa de vídeo reiniciou (o driver se recuperou de um erro)", Telemetria.MotivoMidia.DispositivoRemovido);
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
        // Duas marcas deste ffplay com hardware, lidas só depois do WaitForExitAsync (que drena o stderr): o ffplay é encerrado na primeira falha e as
        // linhas seguintes ainda chegam a tempo (ver MemoryFailureKillDelay).
        // decodificacao: "get_buffer() failed" depois da falta de memória é a assinatura das RDNA3 (alocação dos quadros da decodificação, com a VRAM
        // livre); a VRAM cheia de verdade dá falta de memória no renderizador (Failed creating LUT texture, No slab to serve), sem essa linha.
        // semHardware: o ffplay escreve que o hwaccel não iniciou e segue sozinho na CPU, sem sair; é decodificação de software, não "o Vulkan funcionou".
        int decodificacao = 0, semHardware = 0;
        // As placas AMD RDNA3 com falha mostravam o primeiro quadro e só então davam OUT_OF_DEVICE_MEMORY: o vídeo confirmado sozinho não prova nada,
        // por isso o Vulkan só vale como "funcionou" se este ffplay segue rodando, sem falha nem reabertura, depois de um tempo.
        async Task ConfirmVulkanWorkedAsync()
        {
            await Task.Delay(VulkanWorkedAfter).ConfigureAwait(false);
            var atual = p;
            if (atual == null || _stopRequested || _reopening || fallback != null || Volatile.Read(ref semHardware) != 0 || !ReferenceEquals(_player, atual)) return;
            try { if (atual.HasExited) return; }
            catch (InvalidOperationException) { return; }
            VulkanWorked?.Invoke();
        }
        p = Tools.Start(Tools.Ffplay, args, "ffplay", redirectStdin: _localTap != null, hideWindow: true, onStderr: l =>
        {
            // vídeo confirmado no ffplay: a descrição do vídeo e depois a linha de estado com vídeo (A-V/M-V), que só vem com o laço de exibição rodando
            if (!tentativa.TemVideo && !_stopRequested)
            {
                if (achado == null && ParseVideoLine(l) is { } v) achado = v.Size;
                else if (achado is { } t && l.Contains(" aq=") && (l.Contains("A-V") || l.Contains("M-V")))
                {
                    tentativa.VideoOk(() => new JsonObject { ["resolucao"] = Telemetria.CaminhoMidia.Resolucao(t.Width, t.Height) });
                    if (hardware) _ = ConfirmVulkanWorkedAsync();
                }
            }
            if (hardware)
            {
                if (l.Contains("get_buffer() failed", StringComparison.OrdinalIgnoreCase)) Volatile.Write(ref decodificacao, 1);
                if (l.Contains("Failed setup for format vulkan", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("hwaccel initialisation returned error", StringComparison.OrdinalIgnoreCase)) Volatile.Write(ref semHardware, 1);
            }
            var failure = OnStderr(l);
            if (hardware && !_stopRequested && failure != null && Interlocked.CompareExchange(ref fallback, failure, null) == null)
                KillAfterFailure(failure);
        });
        // Falta de memória: dá um instante para a linha get_buffer() failed (a assinatura da decodificação) chegar antes de matar o ffplay.
        void KillAfterFailure(PlayerFailure f)
        {
            if (f.Motivo == Telemetria.MotivoMidia.SemMemoria) _ = Task.Delay(MemoryFailureKillDelay).ContinueWith(_ => Tools.KillQuietly(p), TaskScheduler.Default);
            else Tools.KillQuietly(p);
        }
        _player = p;
        if (_stopRequested) Tools.KillQuietly(p);
        // O stderr pode chegar antes de Tools.Start devolver o processo: não perder nem o erro nem a ordem de encerramento.
        if (fallback != null) KillAfterFailure(fallback);
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
                if (fallback != null)
                {
                    // também na prévia da própria live: a falha dela vale como a de uma live de amigo (mesma placa, mesmo driver)
                    // lido aqui, depois do WaitForExitAsync: o get_buffer() failed chega logo depois da linha da falta de memória
                    VulkanFailed?.Invoke(fallback.Motivo, Volatile.Read(ref decodificacao) != 0);
                    // o Direct3D 11 só abre live de amigo: a prévia lê o vídeo pelo pipe e o mpv do app só aceita SRT
                    if (!IsPreview && PrepareDirect3D != null)
                    {
                        // reset do driver: se o Direct3D 11 também falhar logo em seguida, vale uma segunda tentativa (TryRetryDirect3DAfterReset)
                        _deviceLostAtMs = Environment.TickCount64;
                        Volatile.Write(ref _retryDirect3DAfterReset, fallback.Motivo == Telemetria.MotivoMidia.DispositivoRemovido ? 1 : 0);
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
        try { await TryDirect3DCoreAsync(vulkanFailure, restartSender).ConfigureAwait(false); }
        finally { SetPreparingDirect3D(false); } // o aviso do palco some quando o vídeo abre (Direct3D 11 ou CPU), a live fecha ou a espera falha
    }

    async Task TryDirect3DCoreAsync(string? vulkanFailure, bool restartSender)
    {
        try
        {
            // A instalação pode terminar para a próxima live mesmo se esta precisar seguir na CPU.
            // na recuperação (o Vulkan acabou de falhar) a espera é maior que na abertura direta
            var timeout = vulkanFailure != null ? Direct3DRecoveryPreparationTimeout : Direct3DPreparationTimeout;
            var preparo = PrepareDirect3D!();
            // mpv já instalado: a tarefa termina na hora e nada pisca. Se a espera passa do aviso, o palco diz o que está acontecendo.
            if (!preparo.IsCompleted && Direct3DPreparingNoticeAfter < timeout
                && await Task.WhenAny(preparo, Task.Delay(Direct3DPreparingNoticeAfter)).ConfigureAwait(false) != preparo && !_stopRequested)
            {
                SetPreparingDirect3D(true);
                timeout -= Direct3DPreparingNoticeAfter; // o prazo total continua sendo o mesmo
            }
            _direct3DExecutable = await preparo.WaitAsync(timeout).ConfigureAwait(false);
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
        var run = new Direct3DRun();
        try
        {
            player = Tools.Start(_direct3DExecutable!, Direct3DArgs(), "mpv-live", redirectStdin: false,
                onStderr: run.Observe,
                onStdout: line =>
                {
                    const string prefix = "STREEMU_D3D11 ";
                    if (line.StartsWith("STREEMU_MPV_STATUS ", StringComparison.Ordinal))
                    {
                        // o mpv escreve esta linha ~20 vezes por segundo e ela nunca ia para o log (só para a amostra da telemetria): 1 a cada 10 s
                        if (ParseMpvStatus(line) is { } sample)
                        {
                            _direct3DSample = sample;
                            run.Track(sample, Environment.TickCount64);
                            // sem o áudio ainda (avsync "nan") a linha não diz nada: a primeira amostra com medida abre a conta dos 10 s
                            if (sample.AudioVideoMs != null && StatusLogDue(ref run.LastStatusLogMs, Environment.TickCount64, StatusLogEvery)) Log.Info(StatusLogText(sample));
                        }
                        return;
                    }
                    run.Observe(line);
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
                        run.Playing.Restart(); // o "tempo assistido" do evento de queda conta daqui
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
            tentativa.Falhou(Telemetria.EtapaMidia.Renderizacao, Telemetria.MotivoMidia.D3d11NaoAbriu);
            if (TryRetryDirect3DAfterReset()) return;
            Direct3DRejected?.Invoke();
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
                // sem hardware ou sem vídeo em 8 s: não decodificou; com vídeo e depois saída com erro: caiu já desenhando
                var amostra = _direct3DSample;
                tentativa.Falhou(outcome == 1 ? Telemetria.EtapaMidia.Renderizacao : Telemetria.EtapaMidia.Decodificacao,
                    outcome == 2 ? Telemetria.MotivoMidia.D3d11SemHardware : Telemetria.MotivoMidia.D3d11NaoManteve,
                    medidas: () => amostra == null ? null : new JsonObject
                    {
                        ["descartes_exibicao"] = amostra.RenderDrops, ["descartes_decodificador"] = amostra.DecoderDrops,
                    });
                // só a falha de abrir (sem hardware ou sem vídeo): quem já desenhou e caiu depois não foi o driver ainda voltando
                if (outcome != 1 && TryRetryDirect3DAfterReset()) return;
                if (outcome == 2) Direct3DRejected?.Invoke();
                RecoverOnCpu("o player Direct3D 11 não conseguiu manter a reprodução");
            }
            else
            {
                // saiu com código 0 depois de mostrar vídeo, sem ninguém pedir e sem a pessoa fechar a janela ("Quit"): "End of file" é a conexão que acabou
                if (!_stopRequested && outcome == 1 && !string.Equals(run.ExitReason, "Quit", StringComparison.Ordinal))
                    await DeclareDropAsync(run, tentativa).ConfigureAwait(false);
                Exited?.Invoke();
            }
        }
    }

    /// <summary>
    /// O mpv saiu sozinho de uma live que ele mostrava. Espera <see cref="DropGrace"/> (se o transmissor encerrou a live, o Stop() da mensagem dele chega
    /// antes e isto não é queda) e então guarda o <see cref="Drop"/> e registra <c>midia_falha</c> fatal (a live fecha para quem assiste e nada tenta de
    /// novo): tempo assistido, se o SRT estourou, a última sincronia do mpv, os FPS e a resolução. Só códigos e números; sem IP nem nome.
    /// O motivo é <c>srt_estourou</c> só quando o player não acompanhou (<see cref="PlayerFellBehind"/>); nos outros casos é <c>conexao_caiu</c>, porque o
    /// mpv terminar com "End of file" também é a internet ou o PC de quem transmite caindo.
    /// </summary>
    async Task DeclareDropAsync(Direct3DRun run, Telemetria.CaminhoMidia.Tentativa tentativa)
    {
        await Task.Delay(DropGrace).ConfigureAwait(false);
        if (_stopRequested) return;
        var amostra = _direct3DSample;
        var tamanho = VideoSize;
        int assistiu = (int)run.Playing.Elapsed.TotalSeconds;
        bool srtFull = run.SrtFull, srtSequence = run.SrtSequence;
        int descartesRecentes = run.DropsGrowth;
        bool atrasado = PlayerFellBehind(srtFull || srtSequence, amostra, descartesRecentes);
        Drop = new PlayerDrop(atrasado, amostra?.Fps, assistiu);
        Log.Info($"[aviso] Player: o mpv terminou sozinho depois de {assistiu} s ({run.ExitReason ?? "sem motivo"}"
            + $"{(srtFull ? "; o buffer do SRT encheu" : "")}{(srtSequence ? "; o SRT perdeu a sequência" : "")}"
            + $"; {(atrasado ? "o player não acompanhou" : "tratado como conexão caída")}); a live fecha por aqui.");
        tentativa.Falhou(Telemetria.EtapaMidia.Recepcao, atrasado ? Telemetria.MotivoMidia.SrtEstourou : Telemetria.MotivoMidia.ConexaoCaiu, fatal: true,
            medidas: () =>
            {
                var o = new JsonObject
                {
                    ["assistiu_s"] = assistiu, ["srt_sem_espaco"] = srtFull, ["srt_sequencia"] = srtSequence, ["descartes_recentes"] = descartesRecentes,
                };
                if (amostra != null)
                {
                    o["av_ms"] = amostra.AudioVideoMs;
                    o["descartes_exibicao"] = amostra.RenderDrops;
                    o["descartes_decodificador"] = amostra.DecoderDrops;
                    o["fps"] = amostra.Fps;
                }
                if (tamanho is { } t) o["resolucao"] = Telemetria.CaminhoMidia.Resolucao(t.Width, t.Height);
                return o;
            });
    }

    /// <summary>
    /// A primeira tentativa do Direct3D 11 de uma recuperação de reset do driver falhou ao abrir, dentro de <see cref="Direct3DRetryWindow"/> do reset:
    /// o driver provavelmente ainda estava voltando. Espera <see cref="Direct3DRetryDelay"/> e tenta o Direct3D 11 mais uma vez, refazendo o SRT do
    /// transmissor (<see cref="Relaunch"/>). Nessa primeira falha o chamador não dispara <see cref="Direct3DRejected"/> (o app bloquearia o Direct3D 11
    /// por 2 min) nem cai na CPU. Vale uma vez por recuperação; a segunda falha segue o caminho de sempre. Devolve verdadeiro se agendou a nova tentativa.
    /// </summary>
    bool TryRetryDirect3DAfterReset()
    {
        if (_stopRequested || Interlocked.Exchange(ref _retryDirect3DAfterReset, 0) != 1) return false;
        if (Environment.TickCount64 - _deviceLostAtMs > (long)Direct3DRetryWindow.TotalMilliseconds) return false;
        Log.Info("Player: o Direct3D 11 falhou logo depois do reset da placa de vídeo; tentando de novo em instantes.");
        _midia.Preparar(Telemetria.GatilhoMidia.Recuperacao);
        _ = Task.Run(async () =>
        {
            await Task.Delay(Direct3DRetryDelay).ConfigureAwait(false);
            if (_stopRequested || _reopening) return;
            Relaunch(LaunchDirect3D);
        });
        return true;
    }

    string[] Direct3DArgs()
    {
        var size = OpenAt?.Size ?? new Size(1280, 720);
        return new[]
        {
            "--no-config", "--load-scripts=no", "--load-auto-profiles=no", "--ytdl=no",
            "--access-references=no", "--autoload-files=no", "--sid=no", "--no-osc", "--drag-and-drop=no",
            "--profile=low-latency", "--video-sync=audio",
            // LIV-178 (campo, 2026-11-25): live de 3440x1370 a 120 FPS: o mpv mostrava o vídeo a cerca de metade da velocidade (áudio menos vídeo
            // crescendo ~0,5 s por segundo) com os dois contadores de descarte em 0, até o buffer do SRT encher e o mpv sair. O modo "vo" já era o padrão
            // do perfil e não descartou nada, então este argumento só deixa a escolha explícita; NÃO é a correção e não se sabe a causa. Não use "decoder":
            // o manual do mpv não o recomenda, e descartar na decodificação do H.264 pode estragar a imagem até o próximo quadro-chave, com uma oscilação
            // de rede já disparando isso por causa do video-sync=audio com latência baixa. A causa fica para confirmar em campo pela linha
            // "Player (mpv): ..." do log (descartes e áudio menos vídeo ao longo da live); mexer na sincronia (ex.: video-latency-hacks) só depois desse dado.
            "--framedrop=vo",
            "--vo=gpu-next", "--gpu-api=d3d11",
            "--gpu-context=d3d11", "--hwdec=d3d11va", "--hwdec-software-fallback=no",
            // A entrada continua restrita ao SRT local e aos formatos que o transmissor usa.
            "--demuxer=lavf", "--demuxer-lavf-format=mpegts", "--vd=h264", "--ad=opus,libopus",
            "--demuxer-lavf-probesize=3000000", "--demuxer-lavf-analyzeduration=2.5",
            "--stream-lavf-o=protocol_whitelist=srt",
            "--demuxer-lavf-o=protocol_whitelist=srt,format_whitelist=mpegts,codec_whitelist=%17%h264,opus,libopus",
            $"--title={_title}", $"--geometry={size.Width}x{size.Height}", Borderless ? "--no-border" : "--border",
            "--msg-color=no", "--term-osd=force",
            "--term-playing-msg=STREEMU_D3D11 ${hwdec-current} ${width} ${height} ${video-params/aspect}",
            // os ":" são o valor de quando a propriedade ainda não existe (sem áudio o avsync fica vazio): sem eles a linha perde um campo e os outros deslizam
            "--term-status-msg=STREEMU_MPV_STATUS ${=avsync:nan} ${=frame-drop-count:0} ${=decoder-frame-drop-count:0} ${=container-fps:0}",
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
        SetPreparingDirect3D(false);
        _midia.Encerrar();
        Tools.KillQuietly(_player);
        _tap?.Dispose();
        _tap = null;
    }
}
