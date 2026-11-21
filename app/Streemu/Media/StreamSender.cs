using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Streemu.Net;
using Streemu.Util;

namespace Streemu.Media;

/// <summary>
/// Transmissão: ffmpeg captura (gfxcapture ou ddagrab, ver <see cref="Capture"/>) na resolução nativa, reduz se precisar,
/// codifica e serve SRT em modo listener numa porta local. O <see cref="LocalRelay"/> leva os pacotes
/// pelo túnel até o player do amigo. Cada vez que um espectador sai, o ffmpeg é reiniciado.
///
/// Redução de resolução: a captura do Windows não reduz (o width/height dela CORTA a imagem), e nesta
/// versão do ffmpeg não dá para reduzir as texturas dela na GPU da NVIDIA. Então:
///   - resolução nativa: quadros ficam na GPU até o encoder (sem cópia);
///   - resolução menor: AMD tenta vpp_amf (na GPU); senão, copia para a CPU e reduz lá (~1 núcleo).
/// </summary>
public sealed class StreamSender : IDisposable
{
    public sealed record Settings(CaptureSource Source, Preset Preset, AudioMode Audio, EncoderChoice Encoder, int LatencyMs);

    public enum Path { Gpu, AmfMap, AmfVpp, Cpu }

    public sealed record Pipeline(string Encoder, Path Path)
    {
        public override string ToString() => $"{Encoder} ({Path switch
        {
            Path.Gpu => "quadros na GPU",
            Path.AmfMap => "quadros na GPU, mapeados para o AMF",
            Path.AmfVpp => "redução na GPU AMD",
            _ => "quadros copiados para a CPU"
        }})";
    }

    /// <summary>
    /// Um espectador: o relay local que leva os pacotes pelo túnel dele e o ffmpeg de envio SRT (só cópia, barato).
    /// Cada amigo que clica em Assistir pela sala ganha o seu.
    /// </summary>
    sealed class Output
    {
        public required string Key, Name;
        public required LocalRelay Relay;
        public required Func<string> Passphrase;
        public Process? Srt;
        public TsPipe? Pipe;
        public volatile bool Removed;
        public bool ThreadRunning;
        public long LastPacketTicks;
        public long TimestampJumps, NonMonotonic, CorruptPackets;
        public System.Text.Json.Nodes.JsonObject? LastPipeSync;
    }

    readonly PipelineMemory? _memory;
    readonly object _outGate = new();
    readonly Dictionary<string, Output> _outputs = new();
    volatile bool _running;
    Process? _ffmpeg;   // captura + encoder, solta mpegts no stdout
    TsSplitter? _splitter;
    ThumbnailFeeder? _thumbs;   // miniatura para o painel "Ao vivo agora" (spec 006); só liga enquanto alguém pede
    long _droppedByRemovedPipes;
    AudioPump? _audio;
    System.Text.Json.Nodes.JsonObject? _lastAudioSync, _lastTsSync;
    readonly Stopwatch _mediaClock = new();
    int _mediaEpoch;
    LiveSyncGuard _syncGuard = new();
    long _syncCheckedAt;
    /// <summary>O som do Streemu (player dos amigos) está fora da captura de áudio desta live (MainForm.AvisoSom.cs usa para avisar só quando vaza).</summary>
    public bool AppAudioRemoved => _audio?.RemovesAppAudio == true;
    volatile Settings? _reconfigure;
    long _lowCaptureSeconds;
    bool _lowCaptureWarned;
    FrameStats? _session;
    FrameSensor? _sensor;      // conta quadros NOVOS quando a captura repete quadros (ddagrab); ver FrameSensor
    long _lastEncoded;
    double? _lastMbps;
    readonly object _observeGate = new();

    // vigia da captura (ver Watchdog)
    volatile string? _restartPending;
    bool? _preferDda;                 // null = padrão de Capture.For; true/false = o diagnóstico trocou o método
    int _stuckSeconds, _lowSeconds, _healthySeconds, _restarts, _lastUnique = -1;
    // batida de 1 s (ver Heartbeat): quando chegou o último segundo medido, quantos seguidos foram de sensor mudo, e quando o ffmpeg principal falou
    DateTime _lastSecondAt, _lastEncodedAt;
    int _silentStreak;
    // últimos 8 s (quadros novos, quadros saindo) desta captura e quando o vigia reabriu por "encoder aos trancos" (ver EncoderStutters)
    readonly Queue<(int New, int Out)> _outRing = new();
    DateTime _stutterRestartAt = DateTime.MinValue;
    // os últimos segundos medidos (hora, quadros novos): o diagnóstico olha a mediana do que a live recebeu durante a sonda, não um segundo só
    readonly Queue<(DateTime At, int Unique)> _recent = new();
    volatile bool _diagnosing;
    DateTime _captureStartedAt, _nextDiagnosisAt;
    TimeSpan _backoff = TimeSpan.FromSeconds(10);
    // nesta live o diagnóstico já viu a captura TRAVAR (a nova pegava, a da live não): a próxima queda reabre sem a sonda (ver CanFastReopen)
    bool _staleConfirmed;
    DateTime _fastReopenAt = DateTime.MinValue;
    readonly List<string> _diagnoses = new();
    // telemetria do caminho de mídia (Telemetria/CaminhoMidia.cs): só observa; nada daqui muda a escolha, a prioridade ou a recuperação
    readonly Telemetria.CaminhoMidia.Tentativas _midia = new(Telemetria.CaminhoMidia.Host);
    bool _lembrado;
    // a última linha do ffmpeg principal que não é de progresso: quando ele sai sozinho, é ela que diz em que etapa caiu (só o código sai do PC)
    volatile string? _ultimoErro;
    /// <summary>Teste: finge que a captura travou N s depois de abrir (-1 = nunca). Padrão: STREEMU_TEST_STUCK_AFTER.</summary>
    internal int TestStuckAfter { get; set; } = int.TryParse(Environment.GetEnvironmentVariable("STREEMU_TEST_STUCK_AFTER"), out var t) ? t : -1;
    /// <summary>Teste (--capture-test h): fecha o ffmpeg do sensor N s depois de a captura abrir, uma vez, para conferir a batida de 1 s e a reabertura do sensor.</summary>
    internal int TestKillSensorAfter { get; set; } = -1;
    bool _testSensorKilled;
    /// <summary>Teste: quantas vezes fingir o travamento (cada reabertura conta uma). Padrão 1; STREEMU_TEST_STUCK_TIMES=2 exercita a reabertura direta.</summary>
    internal int TestStuckTimes { get; set; } = int.TryParse(Environment.GetEnvironmentVariable("STREEMU_TEST_STUCK_TIMES"), out var n) && n > 0 ? n : 1;
    int _testStuckCount;
    bool _testStuckDone => _testStuckCount >= TestStuckTimes;

    public Settings? Current { get; private set; }
    Preset? _requestedPreset;   // o preset escolhido pelo usuário, antes de FitBitrateToOutput reduzir o bitrate
    public Pipeline? Chosen { get; private set; }
    public CaptureSpec? CaptureUsed { get; private set; }
    public (int W, int H) SourceSize { get; private set; }
    public (int W, int H) OutSize { get; private set; }
    public string AudioDescription { get; private set; } = "sem áudio";
    /// <summary>Aviso para quem transmite quando o monitor está em HDR (nulo se não está).</summary>
    public string? HdrWarning { get; private set; }
    public string LastStats { get; private set; } = "";
    public bool Running => _running;
    /// <summary>Dados locais, sem rede/disco e sem nomes de pessoas; cada envio tem sua própria fila.</summary>
    public System.Text.Json.Nodes.JsonObject SyncSnapshot() => new()
    {
        ["estado"] = !_running ? "parado" : _paused || _wantPause ? "pausa_de_privacidade" : "captura",
        ["audio"] = _audio?.SyncSnapshot() ?? _lastAudioSync?.DeepClone(),
        ["pacotes"] = _splitter?.SyncSnapshot() ?? _lastTsSync?.DeepClone(),
        ["protecao_origem"] = _syncGuard.Snapshot(),
        ["envios"] = new System.Text.Json.Nodes.JsonArray(Outputs().Select(o => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
        {
            ["ligacao"] = o.Key, ["fila"] = o.Pipe?.SyncSnapshot() ?? o.LastPipeSync?.DeepClone(), ["saltos_de_tempo"] = Interlocked.Read(ref o.TimestampJumps),
            ["tempos_nao_monotonicos"] = Interlocked.Read(ref o.NonMonotonic), ["pacotes_corrompidos_entrada"] = Interlocked.Read(ref o.CorruptPackets),
        }).ToArray()),
    };
    public bool ViewerActive => _running && Outputs().Any(o => Stopwatch.GetElapsedTime(Interlocked.Read(ref o.LastPacketTicks)).TotalSeconds < 2);
    public event Action<string>? Status;
    public event Action? Stopped;
    /// <summary>A troca de qualidade/janela durante a transmissão terminou (com sucesso ou não); a tela atualiza o que mostra.</summary>
    public event Action? Reconfigured;
    /// <summary>
    /// Um segundo medido (quadros novos da captura, quadros que o encoder soltou). A telemetria guarda a série com a hora:
    /// o resumo do fim da live (mín/mediana/máx) não dizia QUANDO a captura caiu (live num PC do grupo no Hookshot, 10/11).
    /// Chamado na thread da captura: quem ouve só anota, não pode demorar. O terceiro valor diz de onde veio o segundo: "sensor", "mudo" (o contador
    /// não falou por mais de 1,5 s e o segundo vale 0 quadros novos), "sem_medida" (o contador fechou; quadros novos = -1) ou "pausa" (jogo fora da frente).
    /// </summary>
    public event Action<long, long, string>? SecondMeasured;
    /// <summary>O jogo escolhido está com o foco agora (sempre true para tela inteira). Atualizado a cada 40 ms com a transmissão rodando.</summary>
    public bool GameInFront { get; private set; } = true;
    /// <summary>Trava de foco: true = o jogo saiu da frente e a imagem está parada para quem assiste.</summary>
    public event Action<bool>? FocusHeld;

    /// <summary>Saiu uma miniatura nova da transmissão (JPEG 192x108, thread de fundo). Só acontece enquanto alguém pede (<see cref="WantThumbnail"/>).</summary>
    public event Action<byte[]>? ThumbnailReady;
    /// <summary>A última miniatura gerada, ou nula.</summary>
    public byte[]? LatestThumbnail => _thumbs?.Latest;
    /// <summary>Alguém abriu o painel "Ao vivo agora": liga (ou mantém) a miniatura por mais 45 s. Sem transmissão rodando não faz nada.</summary>
    public void WantThumbnail() => _thumbs?.Want();

    public StreamSender(PipelineMemory? memory = null) => _memory = memory;

    Output[] Outputs() { lock (_outGate) return _outputs.Values.ToArray(); }

    /// <summary>
    /// Mais um espectador. Pode ser chamado com a transmissão parada (passa a valer quando ela começar) ou rodando
    /// (o envio dele abre na hora e começa no próximo quadro-chave, sem mexer em quem já está assistindo).
    /// </summary>
    public void AddOutput(string key, string name, LocalRelay relay, Func<string> passphrase)
    {
        var o = new Output { Key = key, Name = name, Relay = relay, Passphrase = passphrase };
        relay.PacketFromProcess += () => Interlocked.Exchange(ref o.LastPacketTicks, Stopwatch.GetTimestamp());
        lock (_outGate)
        {
            if (_outputs.TryGetValue(key, out var old)) { old.Removed = true; Tools.KillQuietly(old.Srt); }
            _outputs[key] = o;
        }
        if (_running) StartOutputThread(o);
    }

    public void RemoveOutput(string key)
    {
        Output? o;
        lock (_outGate) { if (!_outputs.Remove(key, out o)) return; }
        o.Removed = true;
        Tools.KillQuietly(o.Srt);
    }

    public int OutputCount { get { lock (_outGate) return _outputs.Count; } }

    /// <summary>
    /// Mais um ouvinte do vídeo JÁ codificado, para o usuário ver a própria live: o mesmo vídeo que sai para os amigos, repartido pelo
    /// <see cref="TsSplitter"/> (como cada espectador e a miniatura). Não abre conexão, não captura de novo e não codifica de novo: o único custo é
    /// quem lê (o player local) decodificar. Se o leitor travar o TsPipe descarta a fila, então a transmissão não sente. Nulo sem transmissão rodando.
    /// Uso: <c>var tap = sender.AttachPreview(ffplay.StandardInput.BaseStream); ... tap.Dispose();</c>
    /// </summary>
    public IDisposable? AttachPreview(Stream sink)
    {
        var split = _splitter;
        if (split == null || !_running) return null;
        var pipe = new TsPipe(sink, startAtKey: true);
        split.Add(pipe);
        return new PreviewTap(split, pipe);
    }

    sealed class PreviewTap(TsSplitter splitter, TsPipe pipe) : IDisposable
    {
        public void Dispose() { splitter.Remove(pipe); pipe.Dispose(); }
    }

    public async Task StartAsync(Settings s)
    {
        if (_running) return;
        // fora da janela e antes de qualquer coisa: o ffmpeg tem que ser o conferido (SHA-256), e a conferência pode ainda estar rodando (BinarioIndisponivelException diz o que fazer)
        await Task.Run(() => Binarios.Exigir(Binario.Ffmpeg));
        if (!s.Source.StillExists()) throw new InvalidOperationException("a janela escolhida não existe mais; atualize a lista");
        if (s.Source.IsMinimized)
        {
            Log.Info($"[captura] a janela escolhida está minimizada: restaurando ({s.Source.Label})");
            if (!await Task.Run(s.Source.RestoreIfMinimized))
                throw new InvalidOperationException($"não consegui restaurar a janela minimizada \"{s.Source.Label}\"; abra o jogo na frente e tente de novo");
        }
        var (cap, rememberedWindow) = await Task.Run(() => InitialCapture(s));
        CaptureUsed = cap;
        SourceSize = (cap.Width, cap.Height);
        OutSize = s.Preset.OutputSize(cap.Width, cap.Height);
        _requestedPreset = s.Preset;
        s = FitBitrateToOutput(s);
        Current = s;
        Log.Info($"Captura: {cap}");
        if (cap.CapturesMonitor && s.Source.IsWindow)
            Log.Info($"Captura pelo monitor onde o jogo está ({cap.Name}), com trava de foco: se o jogo sair da frente (Alt+Tab), o amigo fica na última imagem do jogo.");
        var mon = s.Source.IsWindow ? Display.MonitorOf(s.Source.Handle) : s.Source.Handle;
        if (Display.OutputFor(mon) is { Hdr: true })
        {
            HdrWarning = "O HDR do Windows está ligado nesse monitor: a stream pode sair bem mais escura que a sua tela. Para transmitir, desligue em Configurações > Sistema > Tela > HDR.";
            Log.Info("[aviso] " + HdrWarning);
        }
        else HdrWarning = null;
        Status?.Invoke($"Testando o encoder para {OutSize.W}x{OutSize.H}@{s.Preset.Fps} (fonte {SourceSize.W}x{SourceSize.H})...");
        _midia.NovaLive();
        _midia.Preparar(Telemetria.GatilhoMidia.Inicio);
        Chosen = await Task.Run(() => ChooseAndRemember(s, cap, fatal: true));
        Log.Info($"Pipeline escolhido: {Chosen}, fonte {SourceSize.W}x{SourceSize.H} -> saída {OutSize.W}x{OutSize.H}@{s.Preset.Fps}, {s.Preset.Kbps} kbps");
        _session = new FrameStats(s.Preset.Fps);
        _lowCaptureWarned = false;
        _preferDda = rememberedWindow ? false : null; _restarts = 0; _backoff = TimeSpan.FromSeconds(10); _nextDiagnosisAt = DateTime.MinValue;
        _staleConfirmed = false; _fastReopenAt = DateTime.MinValue;
        _testStuckCount = 0;
        _reconfigure = null;
        _droppedByRemovedPipes = 0;
        lock (_diagnoses) _diagnoses.Clear();

        (_audio, AudioDescription) = await CreateAudioAsync(s);

        _running = true;
        new Thread(Loop) { IsBackground = true, Name = "sender-loop" }.Start();
    }

    /// <summary>
    /// A captura do começo da live (e de uma troca de janela/qualidade). Jogo em tela cheia vai pelo monitor (ddagrab), a não ser que o vigia já tenha
    /// visto, com ESSE jogo, a captura do monitor quase não receber quadros e trocado para a da janela. Motivo (num PC do grupo, recomp do Ocarina of Time
    /// "Hookshot", 10/11): o ddagrab, até numa captura nova, recebia ~1 quadro/s com o jogo a ~51/s; a cada live (e a cada volta do Alt+Tab) o app
    /// abria pelo monitor de novo e quem assistia via ~24 s de slideshow até o vigia trocar. Devolve true quando abriu pela janela por causa da memória.
    /// </summary>
    (CaptureSpec Cap, bool RememberedWindow) InitialCapture(Settings s)
    {
        var src = s.Source;
        if (src.IsWindow && Environment.GetEnvironmentVariable("STREEMU_CAPTURE") == null && Capture.PreferDda(src)
            && RememberedWindowSince(_memory?.Recall(CaptureMemoryKey(src)), DateTime.Now) is DateTime since)
        {
            Log.Info($"Captura pela janela de {src.ProcessName}: em {since:dd/MM} a captura do monitor quase não recebia quadros desse jogo e o vigia trocou para a janela. " +
                     "Se a janela também ficar ruim, o vigia troca de novo.");
            return (Capture.Window(src, s.Preset.Fps), true);
        }
        return (Capture.For(src, s.Preset.Fps), false);
    }

    static string CaptureMemoryKey(CaptureSource src) => "captura|" + src.ProcessName.ToLowerInvariant();

    /// <summary>Quanto tempo vale a memória "esse jogo vai pela janela": driver e jogo mudam, e a captura do monitor costuma ser a melhor para jogo em tela cheia.</summary>
    internal static readonly TimeSpan CaptureMemoryLife = TimeSpan.FromDays(14);

    /// <summary>O valor guardado ("janela|2026-11-10") manda abrir pela janela? Devolve a data em que o vigia trocou, ou nulo (sem memória, "monitor" ou vencida).</summary>
    internal static DateTime? RememberedWindowSince(string? value, DateTime now)
    {
        if (value == null || !value.StartsWith("janela|", StringComparison.Ordinal)) return null;
        if (!DateTime.TryParseExact(value[7..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var since)) return null;
        return now - since <= CaptureMemoryLife ? since : null;
    }

    /// <summary>O vigia trocou o método de um jogo: lembra para a próxima live (e a volta do Alt+Tab) já abrir pelo método que funcionou.</summary>
    void RememberCaptureMethod(CaptureSource src, bool monitor)
    {
        if (!src.IsWindow || _memory == null) return;
        try { _memory.Remember(CaptureMemoryKey(src), monitor ? "monitor" : $"janela|{DateTime.Now:yyyy-MM-dd}"); }
        catch (Exception e) { Log.Info("[captura] não consegui guardar o método da captura: " + e.Message); }
    }

    Pipeline ChooseAndRemember(Settings s, CaptureSpec cap, bool fatal)
    {
        string memoryKey = MemoryKey(s, cap);
        var remembered = ParsePipeline(_memory?.Recall(memoryKey));
        var (src, dst) = (SourceSize, OutSize);
        var p = ChoosePipeline(s.Encoder, s.Preset, cap, src, dst, remembered, (cand, linha, demorou) => FalhaNoTeste(s, cap, src, dst, cand, linha, demorou, fatal));
        _lembrado = remembered != null && p == remembered;
        _memory?.Remember(memoryKey, FormatPipeline(p));
        return p;
    }

    // ------------------------------------------------------------------ telemetria do caminho de mídia (só observa)

    static string CodigoDoCaminho(Path p) => p switch { Path.Gpu => "gpu", Path.AmfMap => "amf_map", Path.AmfVpp => "amf_vpp", _ => "cpu" };

    /// <summary>O caminho para a telemetria: só números e códigos aqui (thread do laço); a placa é resolvida na fila do CaminhoMidia.</summary>
    static Func<System.Text.Json.Nodes.JsonObject?> CaminhoParaTelemetria(Settings s, CaptureSpec cap, Pipeline p, (int W, int H) src, (int W, int H) dst, bool lembrado)
    {
        var mon = cap.Dda ? IntPtr.Zero : s.Source.IsWindow ? Display.MonitorOf(s.Source.Handle) : s.Source.Handle;
        return Telemetria.ContextoMidia.Envio(cap.Name, cap.Dda ? cap.Adapter : -1, mon, p.Encoder, CodigoDoCaminho(p.Path), src, dst,
            s.Preset.Fps, s.Preset.Kbps, s.Preset.Name, s.Encoder.ToString().ToLowerInvariant(), lembrado);
    }

    /// <summary>Um candidato reprovou no teste de 3 quadros (ou nenhum passou: <paramref name="cand"/> nulo). Fatal só no começo da live, que não abre.</summary>
    void FalhaNoTeste(Settings s, CaptureSpec cap, (int W, int H) src, (int W, int H) dst, Pipeline? cand, string? linha, bool demorou, bool fatal)
    {
        if (!Telemetria.CaminhoMidia.Ligado || _midia.PreparadaOuAtual is not { } t) return;
        if (cand == null) { t.Falhou(Telemetria.EtapaMidia.Encoder, Telemetria.MotivoMidia.NenhumEncoder, fatal: fatal, teste: !fatal); return; }
        var (etapa, motivo) = Telemetria.CaminhoMidia.ClassificarFfmpeg(linha, demorou, Telemetria.EtapaMidia.Encoder, Telemetria.MotivoMidia.SaiuComErro);
        t.Falhou(etapa, motivo, teste: true, caminho: CaminhoParaTelemetria(s, cap, cand, src, dst, lembrado: false));
    }

    /// <summary>Medidas que já existem na hora da falha (nada novo é coletado): quadros do último segundo, reaberturas e pacotes descartados no envio.</summary>
    Func<System.Text.Json.Nodes.JsonObject?> MedidasDaFalha()
    {
        int novos = _lastUnique; long saindo = 0; int reaberturas = _restarts; long descartes = 0;
        try { saindo = EncodedNow(); descartes = Interlocked.Read(ref _droppedByRemovedPipes) + Outputs().Sum(x => x.Pipe?.DroppedPackets ?? 0); }
        catch { return () => null; } // sem as medidas, a falha sai mesmo assim
        return () => new System.Text.Json.Nodes.JsonObject
        {
            ["quadros_novos_s"] = novos, ["quadros_saindo_s"] = saindo, ["reaberturas"] = reaberturas, ["pacotes_descartados_envio"] = descartes,
        };
    }

    static async Task<(AudioPump?, string)> CreateAudioAsync(Settings s)
    {
        if (s.Audio == AudioMode.None) return (null, "sem áudio");
        try
        {
            AudioPump pump;
            if (s.Audio == AudioMode.OnlyApp && s.Source.IsWindow)
            {
                pump = await AudioPump.CreateAsync(s.Source.ProcessId, include: true, $"só o app {s.Source.ProcessName}");
            }
            else
            {
                if (s.Audio == AudioMode.OnlyApp) Log.Info("\"Só o app\" precisa de uma janela; com monitor, uso \"PC todo menos Discord\".");
                var discord = Sources.FindDiscordRoot();
                if (discord != null) pump = await AudioPump.CreateAsync(discord.Id, include: false, $"PC todo menos Discord (pid {discord.Id})");
                else pump = await AudioPump.CreateAsync(Environment.ProcessId, include: false, "PC todo (Discord não está aberto)");
            }
            return (pump, pump.Description);
        }
        catch (Exception e)
        {
            Log.Info("Não consegui capturar o áudio, sigo sem áudio: " + e.Message);
            return (null, "sem áudio (falhou: " + e.Message + ")");
        }
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        KillCapture();
        foreach (var o in Outputs()) Tools.KillQuietly(o.Srt);
        _lastAudioSync = _audio?.SyncSnapshot() ?? _lastAudioSync;
        _audio?.Dispose();
        _audio = null;
        _midia.Encerrar();
        Log.Info("Transmissão parada." + (_session is { Intervals: > 0 } ? " Resumo: " + _session.OneLine() + (_restarts > 0 ? $", captura reaberta {_restarts} vez(es)" : "") + "." : ""));
    }

    /// <summary>Resumo da sessão de transmissão para o relatório (quadros novos/s, rajadas, duplicados, reinícios).</summary>
    public System.Text.Json.Nodes.JsonObject? SessionSummary()
    {
        var o = _session?.Summary();
        if (o == null) return null;
        o["reinicios_da_captura"] = _restarts;
        lock (_diagnoses) o["diagnosticos"] = new System.Text.Json.Nodes.JsonArray(_diagnoses.Select(d => (System.Text.Json.Nodes.JsonNode)d!).ToArray());
        o["pacotes_descartados_para_nao_atrasar"] = Interlocked.Read(ref _droppedByRemovedPipes) + Outputs().Sum(x => x.Pipe?.DroppedPackets ?? 0);
        o["espectadores"] = OutputCount;
        if (_splitter is { } sp) o["audio_menos_video_ms"] = $"agora {sp.AvOffsetMs?.ToString() ?? "-"}, pior {sp.MaxAvOffsetMs}";
        double heldNow = _focusHeldSince == DateTime.MinValue ? 0 : (DateTime.Now - _focusHeldSince).TotalSeconds;
        o["imagem_segurada_por_foco"] = _focusHolds == 0 ? "nunca"
            : $"{_focusHolds} vez(es), {Math.Round(_focusHeldSeconds + heldNow)} s no total" + (heldNow > 0 ? " (segurada agora)" : "");
        return o;
    }

    /// <summary>Pede para reabrir a captura (o envio SRT e o player do amigo continuam).</summary>
    public void RequestRestart(string reason) => _restartPending = reason;

    /// <summary>
    /// Troca qualidade, FPS ou janela sem parar a transmissão: só a captura é reaberta (como no vigia), então quem
    /// assiste vê alguns segundos parado e o player não fecha. Termina com <see cref="Reconfigured"/>.
    /// </summary>
    public void Reconfigure(Settings s)
    {
        if (_running) _reconfigure = s;
    }

    /// <summary>O amigo avisou que fechou o player: refaz o envio já, para ele poder abrir de novo sem esperar o SRT.</summary>
    public void ViewerLeft(string key)
    {
        if (!_running) return;
        Output? o;
        lock (_outGate) _outputs.TryGetValue(key, out o);
        Tools.KillQuietly(o?.Srt);
    }

    // ------------------------------------------------------------------ trava de foco (só o jogo aparece)

    int _focusHolds;
    double _focusHeldSeconds;
    DateTime _focusHeldSince = DateTime.MinValue;
    volatile bool _wantPause;   // o jogo está fora da frente (decidido pelo FocusLoop)
    bool _paused;               // o processo no ar é o da tela "Transmissão pausada" (só o Loop mexe)
    readonly AutoResetEvent _loopWake = new(false);
    /// <summary>Texto da tela de pausa (ex.: "Ana saiu do jogo por um instante").</summary>
    public string PauseCaption { get; set; } = "Volta já.";

    /// <summary>
    /// Jogo em tela cheia/sem bordas é capturado pelo MONITOR (ddagrab: 60 quadros/s, contra 5–40 da captura da janela
    /// em jogo pesado; ver DESCOBERTAS). Como no Discord, quem escolheu um aplicativo transmite SÓ ele: quando o jogo sai
    /// da frente (Alt+Tab, outra janela com foco, jogo minimizado) a captura dá lugar a uma tela "Transmissão pausada",
    /// com o áudio do jogo tocando; quando ele volta, a captura volta.
    /// Por que não só "segurar o vídeo" (primeira versão, teste de 2026-11-06 18h): vídeo parado com áudio andando
    /// desalinhava os tempos; o ffmpeg do envio "corrigia" a cada pacote (timestamp discontinuity ×878), o player do
    /// amigo perdia quadros, enchia o buffer do SRT e caía. E quem entrava com a stream pausada nunca recebia vídeo.
    /// Não pausa: notificações e overlays que aparecem por cima SEM tirar o foco do jogo.
    /// </summary>
    void FocusLoop()
    {
        bool held = false;
        var minute = DateTime.Now;
        int logged = 0, unlogged = 0;
        DateTime frontSince = DateTime.MinValue;
        bool seenFront = true; DateTime seenSince = DateTime.Now;
        while (_running)
        {
            Thread.Sleep(40);
            var s = Current; var cap = CaptureUsed;
            bool guard = s is { Source.IsWindow: true } && cap is { CapturesMonitor: true };
            bool inFront = s is not { Source.IsWindow: true } || InFront(s.Source);
            GameInFront = inFront;
            // Capturando pela JANELA a live não pausa, mas o Alt+Tab ainda importa: num jogo em tela cheia ele recria a imagem do
            // jogo e a captura aberta pode parar de receber quadros (PC do grupo, Hookshot, 10/11: travou 3 vezes e não havia registro
            // de foco para cruzar). Só registra, depois de 0,4 s estável (o Alt+Tab passa por várias janelas).
            if (inFront != seenFront && !guard)
            {
                if (DateTime.Now - seenSince > TimeSpan.FromMilliseconds(400))
                {
                    seenFront = inFront;
                    if (DateTime.Now - minute > TimeSpan.FromMinutes(1)) { minute = DateTime.Now; logged = 0; }
                    if (++logged > 6) unlogged++;
                    else
                    {
                        Log.Info((inFront ? "[foco] O jogo voltou para a frente" : $"[foco] O jogo saiu da frente ({ForegroundName()} ficou na frente)")
                            + $"; a captura pela janela continua{(unlogged > 0 ? $" ({unlogged} trocas sem registro antes desta)" : "")}.");
                        unlogged = 0;
                    }
                }
            }
            else seenSince = DateTime.Now;
            if (guard) seenFront = inFront;
            bool front = !guard || inFront;
            if (front && frontSince == DateTime.MinValue) frontSince = DateTime.Now;
            if (!front) frontSince = DateTime.MinValue;
            // pausa na hora; só volta depois de 0,4 s com o jogo na frente (o Alt+Tab passa por várias janelas)
            bool hold = held ? !(front && DateTime.Now - frontSince > TimeSpan.FromMilliseconds(400)) : !front;
            if (hold == held) continue;
            held = hold;
            // até o processo da pausa entrar, nada novo do monitor sai (senão vaza um pedaço do Alt+Tab)
            if (hold && _splitter is { } split) split.HoldVideo = true;
            _wantPause = hold;
            _loopWake.Set();
            FocusHeld?.Invoke(hold);
            if (hold) { _focusHolds++; _focusHeldSince = DateTime.Now; }
            else { _focusHeldSeconds += (DateTime.Now - _focusHeldSince).TotalSeconds; _focusHeldSince = DateTime.MinValue; }
            // Alt+Tab vai e volta várias vezes seguidas: no máximo 6 linhas por minuto
            if (DateTime.Now - minute > TimeSpan.FromMinutes(1)) { minute = DateTime.Now; logged = 0; }
            if (++logged > 6) { unlogged++; continue; }
            Log.Info((hold
                ? $"O jogo saiu da frente ({ForegroundName()} ficou na frente): quem assiste vê \"Transmissão pausada\"; o áudio do jogo continua."
                : "O jogo voltou para a frente: voltando a captura.") + (unlogged > 0 ? $" ({unlogged} trocas sem registro antes desta)" : ""));
            unlogged = 0;
        }
        if (_focusHeldSince != DateTime.MinValue) { _focusHeldSeconds += (DateTime.Now - _focusHeldSince).TotalSeconds; _focusHeldSince = DateTime.MinValue; }
        _wantPause = false;
        GameInFront = true;
        if (held) FocusHeld?.Invoke(false);
    }

    /// <summary>Troca entre a captura e a tela de pausa (thread do Loop).</summary>
    void ApplyPause(bool pause)
    {
        _audio?.Attach(null);
        KillCapture();
        _paused = pause;
        if (pause) StartPause(); else StartCapture(novaTentativa: false); // a volta da pausa reabre o mesmo caminho: não conta como tentativa
    }

    /// <summary>
    /// Tela "Transmissão pausada": imagem fixa no tamanho da saída, codificada com o mesmo encoder (quadros vindos da
    /// CPU, o que custa quase nada com a imagem parada), mais o áudio do jogo. A imagem é lida 2 vezes por segundo e os
    /// filtros "fps" e "realtime" completam os 60 quadros/s: ela não é lida 60 vezes por segundo (ver PauseArgs).
    /// </summary>
    void StartPause()
    {
        var s = Current!;
        string img = PauseImage(OutSize.W, OutSize.H, PauseCaption);
        bool withAudio = _audio != null;
        var a = PauseArgs(s, Chosen!, img, withAudio);
        ContinueMediaClock(a);
        var p = Tools.Start(Tools.Ffmpeg, a, "ffmpeg-pausa", redirectStdin: true, binaryStdout: true);
        _captureStartedAt = DateTime.Now;
        _ffmpeg = p;
        _splitter!.Feed(p.StandardOutput.BaseStream);
        _splitter.HoldVideo = false;
        _audio?.Attach(p.StandardInput.BaseStream);
    }

    internal static List<string> PauseArgs(Settings s, Pipeline chosen, string img, bool withAudio)
    {
        var a = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostats" };
        if (withAudio) a.AddRange(AudioInputArgs());
        int imgInput = withAudio ? 1 : 0;
        // Sem "-re" na imagem: com "-re -loop 1 -framerate 2" o ffmpeg só começava a ler o stdin do áudio ~7,5 s depois de
        // abrir (medido 2026-11-17 em 3440x1440, nvenc e libx264), e o som da pausa era descartado. O ritmo vem do "realtime".
        // A imagem é lida UMA vez e repetida pelo filtro "loop" (a cor é convertida antes, uma vez só). Com "-loop 1 -framerate 2"
        // a entrada mandava um quadro a cada 500 ms, e o agendador do ffmpeg 8 segurava o som até o próximo: na tela de pausa o
        // som saía em blocos de ~500 ms, com ~10 faltas a cada 2 s no celular (2026-11-21). Medido com a linha do app: maior
        // intervalo do som de 497-512 ms para 46 ms.
        a.AddRange(new[] { "-i", img });
        string pix = chosen.Encoder == "libx264" ? "format=yuv420p" : "format=nv12";
        a.AddRange(new[] { "-map", $"{imgInput}:v:0", "-vf", $"{pix},loop=loop=-1:size=1:start=0,setpts=N/({s.Preset.Fps}*TB),realtime" });
        if (withAudio) a.AddRange(new[] { "-map", "0:a:0" });
        a.AddRange(EncoderArgs(new Pipeline(chosen.Encoder, Path.Cpu), s.Preset));
        if (withAudio) a.AddRange(AudioEncoderArgs());
        a.AddRange(new[] { "-f", "mpegts", "-flush_packets", "1", "-muxdelay", "0", "-muxpreload", "0", "pipe:1" });
        return a;
    }

    /// <summary>PNG da tela de pausa no tamanho da saída (tema escuro do app), guardado na pasta temporária.</summary>
    static string PauseImage(int w, int h, string caption)
    {
        string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"streemu-pausa-{Environment.ProcessId}-{w}x{h}.png");
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Theme.SurfaceInput);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            float k = h / 1080f;
            using var big = new Font("Segoe UI", 54 * k, FontStyle.Bold, GraphicsUnit.Pixel);
            using var small = new Font("Segoe UI", 30 * k, GraphicsUnit.Pixel);
            using var accent = new SolidBrush(Theme.Accent);
            int bar = (int)(10 * k);
            g.FillRectangle(accent, w / 2 - 60 * k, h / 2 - 110 * k, 120 * k, bar);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var text = new SolidBrush(Theme.Text);
            using var muted = new SolidBrush(Theme.TextMuted);
            g.DrawString("Transmissão pausada", big, text, new RectangleF(0, h / 2 - 70 * k, w, 90 * k), fmt);
            g.DrawString(caption, small, muted, new RectangleF(0, h / 2 + 25 * k, w, 60 * k), fmt);
        }
        bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        return file;
    }

    /// <summary>Quem tirou o jogo da frente (para o log: "explorer" = Alt+Tab/área de trabalho, "Discord", etc.).</summary>
    static string ForegroundName()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return "nenhuma janela";
        Native.GetWindowThreadProcessId(fg, out uint pid);
        try { return Process.GetProcessById((int)pid).ProcessName; } catch { return "outra janela"; }
    }

    /// <summary>
    /// O monitor transmitido mostra o jogo: a janela (ou outra do mesmo processo) tem o foco, ou o foco está numa janela de OUTRO monitor e o jogo segue
    /// cobrindo o monitor dele. Antes só valia o foco: um amigo (12/11) jogava na TV e abria a prévia no Streemu do outro monitor; a TV continuava
    /// mostrando o jogo, mas a live pausava ("Transmissão pausada") a cada olhada no app, e voltava só quando ele clicava no jogo de novo.
    /// </summary>
    static bool InFront(CaptureSource src)
    {
        if (!Native.IsWindow(src.Handle)) return false;
        bool iconic = Native.IsIconic(src.Handle);
        var fg = Native.GetForegroundWindow();
        bool focused = false, otherMonitor = false;
        if (fg != IntPtr.Zero)
        {
            Native.GetWindowThreadProcessId(fg, out uint pid);
            focused = fg == src.Handle || pid == src.ProcessId;
            otherMonitor = !focused && Display.MonitorOf(fg) != Display.MonitorOf(src.Handle);
        }
        return ShowsGame(iconic, focused, otherMonitor, !iconic && otherMonitor && src.CoversMonitorNow());
    }

    /// <summary>A regra do <see cref="InFront"/>, sem Win32 (para o --vigia-check).</summary>
    internal static bool ShowsGame(bool gameMinimized, bool gameFocused, bool focusOnOtherMonitor, bool gameCoversItsMonitor) =>
        !gameMinimized && (gameFocused || (focusOnOtherMonitor && gameCoversItsMonitor));

    DateTime _modeChangedSince = DateTime.MinValue;

    /// <summary>
    /// O jogo trocou de modo depois de a transmissão abrir (janela 720p → tela cheia 1080p, ou o contrário)? A captura por janela de um jogo em tela cheia
    /// entrega só ~17 a 22 quadros por segundo enquanto uma captura nova do monitor pega 60 (campo 2026-11-08, Dragon's Dogma 2 no PC do grupo: o app
    /// escolheu "janela" quando o jogo estava em 1280x720 e nunca reavaliou). Reabre a captura pelo método certo quando a mudança dura 3 s (o Alt+Tab passa
    /// por estados no meio). Não briga com o vigia (que força um método) nem com STREEMU_CAPTURE.
    /// </summary>
    void CheckGameMode()
    {
        var s = Current; var cap = CaptureUsed;
        // _preferDda == false: o vigia viu o monitor travar e escolheu a janela; respeita. Já a troca do vigia PARA o monitor (true) só vale enquanto a
        // janela cobre o monitor: se o jogo sai da tela cheia, volta para a janela (antes ficava no monitor até o fim e mostrava a área de trabalho).
        if (_paused || _wantPause || _preferDda == false || _restartPending != null || _diagnosing) return;
        if (s is not { Source.IsWindow: true } || cap == null || Environment.GetEnvironmentVariable("STREEMU_CAPTURE") != null) return;
        bool wantMonitor = WantMonitor(_preferDda, Capture.PreferDda(s.Source), _preferDda == true && s.Source.CoversMonitorNow());
        if (wantMonitor == cap.CapturesMonitor) { _modeChangedSince = DateTime.MinValue; return; }
        if (_modeChangedSince == DateTime.MinValue) { _modeChangedSince = DateTime.Now; return; }
        if (DateTime.Now - _modeChangedSince < TimeSpan.FromSeconds(3)) return;
        _modeChangedSince = DateTime.MinValue;
        if (!wantMonitor) _preferDda = null;
        Log.Info(wantMonitor
            ? "O jogo passou para tela cheia: troco a captura da janela pela do monitor (a da janela entrega bem menos quadros)."
            : "O jogo saiu da tela cheia: troco a captura do monitor pela da janela.");
        _restartPending = wantMonitor ? "o jogo foi para tela cheia" : "o jogo saiu da tela cheia";
    }

    /// <summary>
    /// A captura de uma janela deve ser pelo monitor? Pelo modo do jogo (<paramref name="fullScreenNow"/>, a regra do PR #62) ou porque o vigia trocou para o
    /// monitor (<paramref name="preferDda"/> true) e a janela ainda cobre o monitor inteiro (<paramref name="coversMonitor"/>).
    /// </summary>
    internal static bool WantMonitor(bool? preferDda, bool fullScreenNow, bool coversMonitor) =>
        fullScreenNow || (preferDda == true && coversMonitor);

    void Loop()
    {
        _lastAudioSync = _lastTsSync = null;
        _mediaEpoch = 0; _mediaClock.Reset();
        _syncGuard = new LiveSyncGuard(); _syncCheckedAt = 0;
        _splitter = new TsSplitter();
        _thumbs = new ThumbnailFeeder(_splitter);
        _thumbs.Ready += jpeg => ThumbnailReady?.Invoke(jpeg);
        _focusHolds = 0; _focusHeldSeconds = 0; _focusHeldSince = DateTime.MinValue;
        _wantPause = false; _paused = false;
        new Thread(FocusLoop) { IsBackground = true, Name = "sender-foco" }.Start();
        foreach (var o in Outputs()) StartOutputThread(o);
        Status?.Invoke("Transmitindo. Esperando alguém clicar em Assistir...");
        int captureFailures = 0;
        try { StartCapture(); }
        catch (Exception e)
        {
            Log.Info("Erro ao abrir a captura: " + e.Message);
            _midia.PreparadaOuAtual?.Falhou(Telemetria.EtapaMidia.Captura, Telemetria.MotivoMidia.CapturaNaoAbriu);
        }
        var modeCheck = new Stopwatch(); modeCheck.Start();
        while (_running)
        {
            _loopWake.WaitOne(250);
            if (!_running) break;
            try
            {
                if (modeCheck.ElapsedMilliseconds >= 1000) { modeCheck.Restart(); CheckGameMode(); }
                Heartbeat();
                // o jogo fechou (ou travou e sumiu): encerra com o motivo em vez de tentar reabrir uma janela que não existe
                // (campo 18:49: o vigia tentou reabrir a captura do Bloodborne já fechado e todos os encoders falharam)
                if (Current is { Source.IsWindow: true } cur && !cur.Source.StillExists())
                {
                    Log.Info($"A janela do jogo fechou ({cur.Source.ProcessName}). Encerrando a transmissão.");
                    Status?.Invoke("O jogo fechou; a transmissão foi encerrada.");
                    _running = false;
                    break;
                }
                if (_wantPause != _paused) { ApplyPause(_wantPause); continue; }
                if (_reconfigure is Settings next) { _reconfigure = null; ApplyReconfigure(next); continue; }
                CheckSync();
                if (_restartPending is string why) { _restartPending = null; if (!_paused) RestartCapture(why); continue; }
                if (_ffmpeg is null or { HasExited: true })
                {
                    if (_paused)
                    {
                        Log.Info($"A tela de pausa saiu sozinha (código {SafeExitCode(_ffmpeg)}); abrindo de novo.");
                        StartPause();
                        continue;
                    }
                    // a captura caiu com amigos conectados: reabre só ela (os players não fecham)
                    bool quick = (DateTime.Now - _captureStartedAt).TotalSeconds < 5;
                    captureFailures = quick ? captureFailures + 1 : 0;
                    if (captureFailures >= 5)
                    {
                        Log.Info("A captura caiu 5 vezes seguidas logo ao abrir. Parando a transmissão; veja o log acima.");
                        _midia.Atual?.Falhou(Telemetria.EtapaMidia.Captura, Telemetria.MotivoMidia.CaiuRepetidas, fatal: true, medidas: MedidasDaFalha());
                        Status?.Invoke("Erro: o ffmpeg não consegue capturar (veja o log).");
                        _running = false;
                        break;
                    }
                    RestartCapture($"o ffmpeg da captura saiu sozinho (código {SafeExitCode(_ffmpeg)})");
                }
            }
            catch (Exception e) { Log.Info("Erro ao rodar ffmpeg: " + e.Message); }
        }
        _audio?.Attach(null);
        KillCapture();
        foreach (var o in Outputs()) Tools.KillQuietly(o.Srt);
        _thumbs?.Dispose(); _thumbs = null;
        _lastTsSync = _splitter.SyncSnapshot();
        _splitter.Dispose();
        _lastAudioSync = _audio?.SyncSnapshot() ?? _lastAudioSync;
        _audio?.Dispose();
        _audio = null;
        _midia.Encerrar();
        Stopped?.Invoke();
    }

    void StartOutputThread(Output o)
    {
        lock (o) { if (o.ThreadRunning) return; o.ThreadRunning = true; }
        new Thread(() =>
        {
            try { OutputLoop(o); }
            finally { lock (o) o.ThreadRunning = false; }
        }) { IsBackground = true, Name = "sender-out-" + o.Key }.Start();
    }

    /// <summary>
    /// Envio para um espectador: ffmpeg copiando o mpegts para SRT (listener), ligado ao relay local do túnel dele.
    /// Quando o player dele fecha o ffmpeg sai, e um novo fica esperando ele abrir de novo.
    /// </summary>
    void OutputLoop(Output o)
    {
        int quickFailures = 0;
        while (_running && !o.Removed)
        {
            // sem senha = o túnel desse espectador ainda não conectou (a saída já foi aberta, mas a ligação da sala ainda não conectou)
            string pass = o.Passphrase();
            if (pass == "") { Thread.Sleep(500); continue; }
            int port = LocalRelay.FreeUdpPort();
            o.Relay.FixedTarget = new IPEndPoint(IPAddress.Loopback, port);
            var started = Stopwatch.StartNew();
            try
            {
                var srt = Tools.Start(Tools.Ffmpeg, SrtArgs(Current!, port, pass), "ffmpeg-srt", redirectStdin: true, onStderr: line =>
                {
                    if (line.Contains("timestamp discontinuity")) Interlocked.Increment(ref o.TimestampJumps);
                    if (line.Contains("Non-monotonic DTS") || line.Contains("out of order")) Interlocked.Increment(ref o.NonMonotonic);
                    if (line.Contains("Packet corrupt")) Interlocked.Increment(ref o.CorruptPackets);
                });
                o.Srt = srt;
                // quem entra com a captura já rodando começa no próximo quadro-chave
                o.Pipe = new TsPipe(srt.StandardInput.BaseStream, startAtKey: true);
                _splitter?.Add(o.Pipe);
                while (_running && !o.Removed && !srt.WaitForExit(250)) { }
                if (_running && !o.Removed)
                    Log.Info($"Envio para {o.Name} terminou (código {SafeExitCode(srt)}) depois de {started.Elapsed.TotalSeconds:0.0} s; o player dele provavelmente fechou.");
            }
            catch (Exception e) { Log.Info($"Erro no envio para {o.Name}: " + e.Message); }
            _splitter?.Remove(o.Pipe);
            Tools.KillQuietly(o.Srt);
            if (o.Pipe != null) { o.LastPipeSync = o.Pipe.SyncSnapshot(); Interlocked.Add(ref _droppedByRemovedPipes, o.Pipe.DroppedPackets); o.Pipe.Dispose(); o.Pipe = null; }
            quickFailures = started.Elapsed.TotalSeconds < 3 ? quickFailures + 1 : 0;
            // um espectador com problema não derruba os outros: espera mais entre as tentativas dele
            if (quickFailures == 5)
            {
                Log.Info($"O envio para {o.Name} falhou 5 vezes seguidas logo ao abrir; tentando a cada 5 s (os outros espectadores seguem normais).");
                _midia.Atual?.Falhou(Telemetria.EtapaMidia.Envio, Telemetria.MotivoMidia.EnvioNaoAbre, interrompe: false); // o vídeo dos outros não para
            }
            Thread.Sleep(quickFailures >= 5 ? 5000 : 300);
        }
    }

    /// <summary>Aplica a troca pedida em <see cref="Reconfigure"/> (thread do loop). Se o encoder não aceitar, volta ao que estava.</summary>
    void ApplyReconfigure(Settings next)
    {
        var before = Current!;
        Log.Info($"Trocando a transmissão para {next.Source.Label} em {next.Preset.Name} (quem assiste vê alguns segundos parado).");
        Status?.Invoke("Trocando a qualidade/janela...");
        _audio?.Attach(null);
        KillCapture();
        var oldCap = CaptureUsed; var oldChosen = Chosen; var oldSrc = SourceSize; var oldOut = OutSize;
        try
        {
            if (!next.Source.StillExists()) throw new InvalidOperationException("a janela escolhida não existe mais");
            var (cap, rememberedWindow) = InitialCapture(next);
            SourceSize = (cap.Width, cap.Height);
            OutSize = next.Preset.OutputSize(cap.Width, cap.Height);
            _requestedPreset = next.Preset;
            next = FitBitrateToOutput(next);
            _midia.Preparar(Telemetria.GatilhoMidia.TrocaConfig);
            Chosen = ChooseAndRemember(next, cap, fatal: false);
            CaptureUsed = cap;
            bool audioChanged = next.Audio != before.Audio || next.Source.ProcessId != before.Source.ProcessId || next.Source.IsWindow != before.Source.IsWindow;
            if (audioChanged)
            {
                _audio?.Dispose();
                (_audio, AudioDescription) = CreateAudioAsync(next).GetAwaiter().GetResult();
            }
            Current = next;
            _preferDda = rememberedWindow ? false : null;
            _staleConfirmed = false;
            _session = new FrameStats(next.Preset.Fps);
            Log.Info($"Captura: {cap}; pipeline {Chosen}, saída {OutSize.W}x{OutSize.H}@{next.Preset.Fps}, {next.Preset.Kbps} kbps");
        }
        catch (Exception e)
        {
            Log.Info("Não deu para trocar (" + e.Message + "); a transmissão continua como estava.");
            Current = before; CaptureUsed = oldCap; Chosen = oldChosen; SourceSize = oldSrc; OutSize = oldOut;
        }
        if (_paused) StartPause(); else StartCapture();
        Status?.Invoke("Transmitindo.");
        Reconfigured?.Invoke();
    }

    void KillCapture()
    {
        var sensor = _sensor; _sensor = null;
        sensor?.Dispose();
        Tools.KillQuietly(_ffmpeg);
    }

    /// <summary>Lê a linha de progresso do ffmpeg e calcula quantos quadros por segundo a captura entrega de verdade.</summary>
    void ParseStats(string line)
    {
        var r = _session?.Feed(line);
        if (r == null) { if (!line.Contains("frame=")) _ultimoErro = line; return; }
        long encoded = (long)Math.Round(r.Value.Out);
        _lastEncoded = encoded; _lastEncodedAt = DateTime.Now;
        var br = Regex.Match(line, @"bitrate=\s*([\d.]+)kbits");
        if (br.Success) _lastMbps = double.Parse(br.Groups[1].Value, CultureInfo.InvariantCulture) / 1000;
        // com o sensor ligado (ddagrab) o ffmpeg principal não vê quadros repetidos; quem conta os novos é o sensor
        if (_sensor == null) Observe((long)Math.Round(r.Value.New), encoded, "sensor");
    }

    void OnSensorSecond(int unique)
    {
        _session?.SetNewRate(unique);
        Observe(unique, EncodedNow(), "sensor");
    }

    /// <summary>
    /// Quadros que o encoder soltou no último segundo, ou 0 se o ffmpeg principal não fala há mais de 1,5 s. Antes era o último valor lido, para sempre:
    /// com o ffmpeg principal parado o quadros.csv repetia o número antigo (126, 126, 136, 136 numa live de 12/11) e escondia a parada.
    /// </summary>
    long EncodedNow() => (DateTime.Now - _lastEncodedAt).TotalSeconds <= 1.5 ? _lastEncoded : 0;

    /// <summary>Um segundo de medida (quadros novos que a captura entrega, quadros que o encoder soltou): alimenta o vigia, o aviso de "câmera lenta" e o texto de estado.</summary>
    void Observe(long unique, long encoded, string medida)
    {
        lock (_observeGate) ObserveLocked(unique, encoded, medida);
    }

    /// <summary>
    /// Batida de 1 s pelo relógio do app (laço da transmissão, a cada 250 ms). Antes o "segundo" do vigia e do quadros.csv só existia quando o contador
    /// de quadros novos (sensor) imprimia uma linha: numa live de 12/11 às 12:19 a captura do monitor parou de vez (o ffmpeg principal ficou 139 s
    /// sem soltar quadro, o áudio terminou 129 s à frente) e o sensor emudeceu junto. O vigia ficou cego até o fim e o quadros.csv simplesmente acabou.
    /// Agora o silêncio vale 0 quadros novos (o vigia age) e o sensor morto ou mudo é reaberto.
    /// </summary>
    void Heartbeat()
    {
        var now = DateTime.Now;
        if (_paused || _wantPause)
        {
            // jogo fora da frente: a série continua com a hora, marcando a pausa (antes eram buracos que não diziam nada)
            if ((now - _lastSecondAt).TotalSeconds >= 1)
            {
                _lastSecondAt = now;
                try { SecondMeasured?.Invoke(0, 0, "pausa"); } catch { }
            }
            return;
        }
        if (_ffmpeg is null or { HasExited: true } || _restartPending != null || (now - _captureStartedAt).TotalSeconds < 3) return;
        if (TestKillSensorAfter >= 0 && !_testSensorKilled && _sensor is { } doomed && (now - _captureStartedAt).TotalSeconds >= TestKillSensorAfter)
        {
            _testSensorKilled = true;
            Log.Info("[sensor] teste: fechando o contador de quadros novos.");
            doomed.Kill();
        }
        double quiet = (now - _lastSecondAt).TotalSeconds;
        if (quiet < (_silentStreak > 0 ? 1.0 : 1.5)) return;
        _silentStreak++;
        if (_silentStreak == 3) Log.Info($"[sensor] o contador de quadros novos não fala há {quiet + 2:0} s; conto 0 quadros novos por segundo enquanto ele estiver mudo.");
        bool sensorDied = _sensor is { HasExited: true };
        if (_sensor is { } sensor && (sensorDied || _silentStreak >= 5) && CaptureUsed is { CountGraph: not null } cap)
        {
            Log.Info(sensorDied ? "[sensor] o contador de quadros novos fechou sozinho; abrindo de novo." : "[sensor] o contador de quadros novos está mudo há 5 s; abrindo de novo.");
            _sensor = null;
            sensor.Dispose();
            _sensor = FrameSensor.Start(cap, OnSensorSecond);
            _silentStreak = 0;
        }
        if (sensorDied)
        {
            // o contador FECHOU: não diz nada sobre a captura (reabrir a captura por isso deixaria quem assiste ~1–2 s parado à toa). Grava o segundo sem medida.
            _lastSecondAt = now;
            try { SecondMeasured?.Invoke(-1, EncodedNow(), "sem_medida"); } catch { }
            return;
        }
        // o contador está vivo e calado: é a captura que não entrega nada (o caso da live das 12:19); vale 0 quadros novos e o vigia age
        Observe(0, EncodedNow(), "mudo");
    }

    /// <summary>
    /// O encoder está soltando aos trancos com a captura em dia? Nos últimos 8 s medidos: pelo menos 3 segundos com a captura recebendo 75% da meta e o
    /// encoder soltando menos de 30%, e pelo menos 1 rajada (mais de 150% da meta). É o desenho de áudio e vídeo da captura desalinhados: o ffmpeg segura o
    /// vídeo esperando o áudio e depois tapa o buraco com quadros repetidos (RoR2 4K60, 12/11 13:24: "0 0 0 132 12 82 148 47" saindo com 60 novos).
    /// </summary>
    internal static bool EncoderStutters(IReadOnlyCollection<(int New, int Out)> last, int fps) =>
        last.Count >= 6
        && last.Count(x => x.New >= fps * 0.75 && x.Out < fps * 0.3) >= 3
        && last.Any(x => x.Out > fps * 1.5);

    void ObserveLocked(long unique, long encoded, string medida)
    {
        var now = DateTime.Now;
        _lastSecondAt = now;
        if (medida != "mudo") _silentStreak = 0;
        int target = Current?.Preset.Fps ?? 60;
        if (medida == "sensor" && !_paused && !_wantPause)
        {
            _outRing.Enqueue(((int)unique, (int)encoded));
            while (_outRing.Count > 8) _outRing.Dequeue();
            // rede de segurança: reabrir a captura recomeça o ffmpeg e o relógio do áudio, e os dois voltam alinhados; no máximo uma vez por minuto
            if (_restartPending == null && !_diagnosing && (now - _captureStartedAt).TotalSeconds >= 6 && (now - _stutterRestartAt).TotalMinutes >= 1
                && EncoderStutters(_outRing, target))
            {
                _stutterRestartAt = now;
                string msg = $"a captura recebe ~{unique} quadros novos/s, mas o encoder solta aos trancos ({string.Join(" ", _outRing.Select(x => x.Out))} por segundo): " +
                             "áudio e vídeo da captura desalinharam. Reabrindo a captura para realinhar.";
                Log.Info("[captura] " + msg);
                lock (_diagnoses) { _diagnoses.Add($"{now:HH:mm:ss} {msg}"); if (_diagnoses.Count > 20) _diagnoses.RemoveAt(0); }
                _restartPending = "encoder aos trancos";
            }
        }
        // teste: finge que a captura travou N s depois de abrir (exercita o vigia sem precisar de um jogo dando load)
        if (TestStuckAfter >= 0 && !_testStuckDone && (DateTime.Now - _captureStartedAt).TotalSeconds >= TestStuckAfter) unique = 1;
        Watchdog((int)unique, target);
        if (unique < target * 0.75 && encoded >= target * 0.9) _lowCaptureSeconds++; else _lowCaptureSeconds = 0;
        if (_lowCaptureSeconds >= 5 && !_lowCaptureWarned)
        {
            _lowCaptureWarned = true;
            var cap = CaptureUsed;
            string advice = cap is { Dda: true }
                ? "Mesmo capturando o monitor direto da placa (ddagrab) chegam poucos quadros: o jogo está entregando menos que isso ou a GPU está a 100%. Limite o FPS do jogo (ex.: 60) ou escolha 30 fps."
                : cap?.Name == "gfxcapture-monitor"
                    ? "Você está capturando o MONITOR pela captura do Windows. Para jogo, em Transmitir escolha o jogo na aba Aplicativos (não a aba Telas)."
                    : "Se a tela está parada isso é normal. Com jogo rodando: limite o FPS do jogo (ex.: 60); jogo em janela comum vai pela captura do Windows, tente tela cheia/sem bordas.";
            Log.Info($"[aviso] A captura está entregando só ~{unique} quadros novos por segundo (o resto é repetido, e o amigo vê isso como \"câmera lenta\"). " + advice);
        }
        if (encoded > 0 && !_paused && !_wantPause && _midia.Atual is { TemVideo: false } tentativa) tentativa.VideoOk();
        LastStats = $"{unique} quadros novos/s de {encoded}" + (_lastMbps is { } mbps ? $", {mbps:0.0} Mbps médio" : "");
        try { SecondMeasured?.Invoke(unique, encoded, medida); } catch { }
    }

    // ------------------------------------------------------------------ captura: abrir, reabrir, vigiar

    static int SafeExitCode(Process? p) { try { return p?.ExitCode ?? -1; } catch { return -1; } }

    void StartCapture(bool novaTentativa = true)
    {
        var args = CaptureArgs(Current!, CaptureUsed!, Chosen!, _audio != null);
        _ultimoErro = null;
        ContinueMediaClock(args);
        var p = Tools.Start(Tools.Ffmpeg, args, "ffmpeg",
            redirectStdin: true, onStderr: ParseStats, binaryStdout: true);
        _captureStartedAt = DateTime.Now;
        _stuckSeconds = 0; _lowSeconds = 0; _healthySeconds = 0; _lowCaptureSeconds = 0; _lastUnique = -1;
        _lastSecondAt = _captureStartedAt; _silentStreak = 0;
        lock (_observeGate) _outRing.Clear();
        lock (_recent) _recent.Clear();
        _session?.NewProcess();
        _ffmpeg = p;
        _splitter!.Feed(p.StandardOutput.BaseStream);
        _splitter.HoldVideo = false;
        _audio?.Attach(p.StandardInput.BaseStream);
        _sensor = CaptureUsed!.CountGraph != null ? FrameSensor.Start(CaptureUsed, OnSensorSecond) : null;
        if (Telemetria.CaminhoMidia.Ligado && (novaTentativa || _midia.TemPreparada))
            try { _midia.Comecar(Telemetria.GatilhoMidia.Outro, CaminhoParaTelemetria(Current!, CaptureUsed, Chosen!, SourceSize, OutSize, _lembrado)); }
            catch { } // o ffmpeg já abriu: a telemetria nunca interrompe a abertura, a volta da pausa ou a troca de qualidade
    }

    internal void ContinueMediaClock(List<string> args)
    {
        // A captura e a pausa antes voltavam ambas a zero: a entrada MPEG-TS do envio às vezes lia isso como wrap de 26 h.
        // Um deslocamento COMUM mantém a relação áudio/vídeo e o mesmo SRT; não toca em frames, encoder, cor ou bitrate.
        if (_mediaEpoch++ == 0) { _mediaClock.Restart(); return; }
        args.InsertRange(args.Count - 1, new[] { "-output_ts_offset", _mediaClock.Elapsed.TotalSeconds.ToString("0.000000", CultureInfo.InvariantCulture) });
    }

    void CheckSync()
    {
        long now = Stopwatch.GetTimestamp();
        if (_syncCheckedAt != 0 && Stopwatch.GetElapsedTime(_syncCheckedAt, now).TotalMilliseconds < 950) return;
        _syncCheckedAt = now;
        var s = _splitter?.SyncSnapshot();
        bool eligible = _running && _audio != null && !_paused && !_wantPause && _reconfigure == null && _restartPending == null
            && !_diagnosing && s?["transicao"]?.GetValue<bool>() == false;
        int? delta = s?["audio_menos_video_ms"]?.GetValue<int>();
        if (!_syncGuard.Observe(delta, eligible, now)) return;
        // Só a captura. Nenhum túnel, envio SRT ou player é fechado, e os parâmetros do encoder continuam os mesmos.
        _restartPending = "tempos de áudio e vídeo da origem afastados por mais de 5 s";
        Log.Info($"[sincronia] origem com áudio menos vídeo em {delta} ms de forma persistente; realinhando a captura sem encerrar a live.");
    }

    /// <summary>Fecha só a captura e abre de novo (refazendo a escolha do monitor e, se o tamanho mudou, do encoder).</summary>
    void RestartCapture(string reason)
    {
        _restarts++;
        if (TestStuckAfter >= 0) _testStuckCount++;
        Log.Info($"Reabrindo a captura ({reason}). O amigo vê ~1–2 s parado; o player dele não fecha.");
        if (Telemetria.CaminhoMidia.FalhaDaReabertura(reason) is { } falha)
        {
            var (etapa, motivo) = falha.Motivo == Telemetria.MotivoMidia.ProcessoSaiu
                ? Telemetria.CaminhoMidia.ClassificarFfmpeg(_ultimoErro, false, falha.Etapa, falha.Motivo) : falha;
            _midia.Atual?.Falhou(etapa, motivo, medidas: MedidasDaFalha());
        }
        _midia.Preparar(Telemetria.GatilhoMidia.Reabertura);
        _audio?.Attach(null);
        KillCapture();
        var s = Current!;
        // a troca do vigia para o monitor não vale mais se a janela deixou de cobrir o monitor (mostraria a área de trabalho)
        if (_preferDda == true && s.Source.IsWindow && !WantMonitor(_preferDda, Capture.PreferDda(s.Source), s.Source.CoversMonitorNow())) _preferDda = null;
        try
        {
            var mon = s.Source.IsWindow ? Display.MonitorOf(s.Source.Handle) : s.Source.Handle;
            var cap = _preferDda switch
            {
                true => Capture.Dda(mon, s.Preset.Fps, s.Source.Label) ?? Capture.For(s.Source, s.Preset.Fps),
                false when s.Source.IsWindow && s.Source.StillExists() => Capture.Window(s.Source, s.Preset.Fps),
                // monitor: o vigia viu o ddagrab quase sem quadros e o gfxcapture do mesmo monitor pegando bem mais
                false when !s.Source.IsWindow => Capture.Monitor(s.Source.Handle, s.Preset.Fps, s.Source.Label),
                _ => Capture.For(s.Source, s.Preset.Fps)
            };
            bool changed = (cap.Width, cap.Height) != SourceSize || cap.Dda != CaptureUsed!.Dda;
            if (changed)
            {
                var src = (cap.Width, cap.Height);
                var dst = s.Preset.OutputSize(cap.Width, cap.Height);
                Log.Info($"A fonte mudou ({SourceSize.W}x{SourceSize.H} → {cap.Width}x{cap.Height}, {cap.Name}); testando o encoder de novo.");
                s = RefitBitrate(s, dst);
                // o pipeline que já funcionava vai primeiro: sem isso a troca janela ↔ tela cheia retestava todos os encoders (~9 s com a imagem parada; medido em 2026-11-08)
                var antes = Chosen;
                Chosen = ChoosePipeline(s.Encoder, s.Preset, cap, src, dst, Chosen, (cand, linha, demorou) => FalhaNoTeste(s, cap, src, dst, cand, linha, demorou, fatal: false));
                _lembrado = Chosen == antes;
                SourceSize = src; OutSize = dst;
            }
            CaptureUsed = cap;
            Log.Info($"Captura: {cap}");
        }
        catch (Exception e) { Log.Info("Não consegui refazer a captura com outra configuração; reabrindo a mesma: " + e.Message); }
        StartCapture();
    }

    /// <summary>
    /// Vigia (teste 0.1.9, num PC do grupo no Cyberpunk): depois de um load a captura do monitor passou a entregar ~2 quadros
    /// novos/s e só voltou ao normal reabrindo. Quando os quadros novos despencam (ver <see cref="ShouldProbe"/>), o diagnóstico abre uma captura
    /// de teste à parte e decide: captura travada (reabre), outro método funciona (troca) ou é o jogo (só avisa).
    /// </summary>
    void Watchdog(int unique, int fps)
    {
        _lastUnique = unique;
        var now = DateTime.Now;
        lock (_recent) { _recent.Enqueue((now, unique)); while (_recent.Count > 20) _recent.Dequeue(); }
        // jogo fora da frente: ele pode desenhar menos ou parar (minimizado); isso não é captura travada
        // (campo 18:49: o vigia reabriu a captura com o Bloodborne atrás e a transmissão caiu)
        if (_paused || _wantPause) { _stuckSeconds = 0; _lowSeconds = 0; return; }
        if (unique >= fps * 0.75)
        {
            _stuckSeconds = 0; _lowSeconds = 0;
            if (++_healthySeconds >= 10) _backoff = TimeSpan.FromSeconds(10);
            return;
        }
        _healthySeconds = 0;
        // abaixo de 75%: conta como "ruim" até de 30% para cima. Antes só abaixo de 30% contava, e uma captura entregando 20–40 de 60 (a da janela num
        // jogo em tela cheia dá ~17–37/s, campo 2026-11-08 e capture-test de um PC do grupo) nunca passava pela sonda: quem assistia via a live engasgada até o fim
        _lowSeconds++;
        _stuckSeconds = unique >= fps * 0.3 ? 0 : _stuckSeconds + 1;
        if (_stuckSeconds > 0 && !_diagnosing && _restartPending == null
            && CanFastReopen(_staleConfirmed, _stuckSeconds, (now - _captureStartedAt).TotalSeconds, (now - _fastReopenAt).TotalSeconds))
        {
            _fastReopenAt = now;
            string msg = $"a captura caiu de novo para ~{unique} quadros novos/s; nesta live ela já travou e voltou reabrindo, então reabro direto, sem o teste de ~4 s.";
            Log.Info("[captura] " + msg);
            lock (_diagnoses) { _diagnoses.Add($"{now:HH:mm:ss} {msg}"); if (_diagnoses.Count > 20) _diagnoses.RemoveAt(0); }
            _restartPending = "captura travada de novo";
            return;
        }
        if (!ShouldProbe(_stuckSeconds, _lowSeconds) || _diagnosing || now < _nextDiagnosisAt || (now - _captureStartedAt).TotalSeconds < 4 || _restartPending != null) return;
        _diagnosing = true;
        _ = Task.Run(() =>
        {
            try { Diagnose(fps); }
            catch (Exception e) { Log.Info("[captura] diagnóstico falhou: " + e.Message); }
            finally { _diagnosing = false; }
        });
    }

    /// <summary>
    /// Segundos de cada sonda. Eram 3 (com a abertura, ~5 s por sonda e ~10 s quando testava os dois métodos um depois do outro: no Hookshot de um PC do grupo,
    /// 10/11, foram 9,4 s entre a 1ª sonda e a troca). 2 s dão 4 medidas de meio segundo, o bastante para separar ~1/s de ~50/s.
    /// </summary>
    const double ProbeSeconds = 2;

    void Diagnose(int fps)
    {
        var s = Current!;
        var cap = CaptureUsed!;
        int before = _lastUnique;
        // o outro método (monitor ↔ janela) é testado JUNTO com o atual, e não depois: quando o atual está ruim, a troca sai uns 4 s antes.
        // Janela → monitor só com a janela cobrindo o monitor inteiro (campo 2026-11-10, Spotify em janela num PC do grupo): uma janela parada entrega poucos
        // quadros (e está certo) enquanto o monitor, com outras janelas e o mouse mexendo, entrega muitos; o vigia concluía "a captura da janela travou",
        // trocava para o ddagrab e quem assistia via a ÁREA DE TRABALHO inteira. Num jogo em tela cheia/sem bordas a janela cobre o monitor e a troca segue.
        CaptureSpec? alt = null;
        string note = "";
        if (s.Source.IsWindow && s.Source.StillExists())
        {
            if (cap.CapturesMonitor) alt = Capture.Window(s.Source, fps);
            else if (s.Source.CoversMonitorNow()) alt = Capture.Dda(Display.MonitorOf(s.Source.Handle), fps, s.Source.Label);
            else note = " A janela não cobre o monitor inteiro: não troco para a captura do monitor (mostraria a área de trabalho).";
        }
        else if (!s.Source.IsWindow)
        {
            // Monitor escolhido na aba Telas: o outro método é a outra captura do MESMO monitor (a imagem é a mesma, então trocar nunca mostra outra coisa).
            // Antes não havia alternativa e o vigia concluía "o jogo manda pouco" com o RoR2 rodando liso (2026-11-12: gfxcapture do monitor a ~1/s).
            alt = cap.Dda ? Capture.Monitor(s.Source.Handle, fps, s.Source.Label) : Capture.Dda(s.Source.Handle, fps, s.Source.Label);
            if (alt != null && alt.Name == cap.Name) alt = null; // cor avançada: o Dda devolve o próprio gfxcapture
        }
        var probeStart = DateTime.Now;
        var altProbe = alt != null ? Task.Run(() => Capture.Probe(alt, ProbeSeconds)) : null;
        double probe = Capture.Probe(cap, ProbeSeconds);
        int now = LiveMedianSince(probeStart);
        string msg;
        // só é "travada" se a da transmissão AINDA estiver lá embaixo e a nova pegar bem mais
        // (sem isso, uma área de trabalho que parou e voltou a mexer durante a sonda virava reabertura à toa)
        if (Recovered(fps, now))
            msg = $"a captura caiu para ~{before} quadros novos/s e voltou sozinha ({now}/s).";
        else if (IsStale(fps, now, probe))
        {
            msg = $"a captura TRAVOU: o jogo está mandando ~{probe:0} quadros/s, mas a captura da transmissão recebe só ~{now}/s (acontece depois de load/troca de modo do jogo). Reabrindo a captura.";
            _staleConfirmed = true;
            Schedule("captura travada");
        }
        else
        {
            // o mesmo método numa captura nova também pega pouco: o outro método já foi testado junto
            double altFps = altProbe != null ? altProbe.GetAwaiter().GetResult() : -1;
            // as sondas levam ~3 s: confere de novo se a janela ainda cobre o monitor (o jogo pode ter saído da tela cheia no meio)
            // fonte monitor: as duas capturas são do monitor inteiro, então "a janela cobre o monitor" não se aplica
            bool sameImage = !s.Source.IsWindow || (alt?.CapturesMonitor == true && s.Source.CoversMonitorNow());
            if (alt != null && ShouldSwitchMethod(fps, probe, altFps, alt.CapturesMonitor, sameImage))
            {
                msg = $"pelo {cap.Name} chegam só ~{probe:0} quadros/s, mas pelo {alt.Name} chegam ~{altFps:0}/s. Trocando a captura para {alt.Name}" +
                      (s.Source.IsWindow ? $" (e da próxima vez {s.Source.ProcessName} já abre por ela)." : ".");
                _preferDda = alt.Dda;
                _staleConfirmed = false;
                RememberCaptureMethod(s.Source, monitor: alt.Dda);
                Schedule($"trocando para {alt.Name}");
            }
            else
            {
                // é o jogo mandando pouco (load, menu): a próxima queda passa pela sonda de novo, sem reabrir direto
                _staleConfirmed = false;
                double best = Math.Max(probe, altFps);
                msg = $"o próprio jogo está mandando poucos quadros agora (~{best:0}/s numa captura nova): tela de carregamento, menu parado ou GPU a 100%. " +
                      $"Nada para reabrir; a transmissão segue e confiro de novo em {_backoff.TotalSeconds:0} s." + note;
                _nextDiagnosisAt = DateTime.Now + _backoff;
                _backoff = NextBackoff(_backoff, best, fps);
            }
        }
        Log.Info("[captura] " + msg);
        lock (_diagnoses) { _diagnoses.Add($"{DateTime.Now:HH:mm:ss} {msg}"); if (_diagnoses.Count > 20) _diagnoses.RemoveAt(0); }
    }

    /// <summary>
    /// O diagnóstico troca de método (janela ↔ monitor)? Só quando o outro método pega bem mais quadros E, se o outro é o monitor, a janela cobre o monitor
    /// inteiro. Sem a segunda condição, um app em janela parado (Spotify) virava "captura travada" e a transmissão passava a mostrar a área de trabalho.
    /// </summary>
    /// <summary>
    /// Hora de passar pela sonda? 2 s quase parada (abaixo de 30%; eram 3 s) ou 6 s engasgada (abaixo de 75%). Cada segundo a mais aqui é um segundo de
    /// slideshow para quem assiste na PRIMEIRA live: a troca de método acontece com a live rodando e o player de quem assiste não fecha, então não
    /// tem por que esperar a pessoa parar e abrir de novo.
    /// </summary>
    internal static bool ShouldProbe(int stuckSeconds, int lowSeconds) => stuckSeconds >= 2 || lowSeconds >= 6;

    /// <summary>
    /// A live se recuperou sozinha durante a sonda? Pela mediana do que ela recebeu nesses ~3 s, e só se voltou a 75% (o mesmo "saudável" do vigia).
    /// Antes era o último segundo acima de 30%: numa live de 2026-11-12 (captura do monitor a ~1/s) um único segundo de 44/s, quando uma janela
    /// apareceu por cima, virou "voltou sozinha" e a sonda seguinte só veio depois.
    /// </summary>
    internal static bool Recovered(int fps, int liveMedian) => liveMedian >= fps * 0.75;

    /// <summary>
    /// Espera até a próxima sonda depois de "é o jogo mandando pouco". Quase parado (load, menu) costuma passar logo: no máximo 20 s entre sondas. Um jogo
    /// estável abaixo da meta (GPU a 100% dando 35–45 de 60) é o ritmo dele de verdade: aí espaça até 60 s para a sonda não pesar na placa a live inteira.
    /// Antes era sempre até 60 s, e numa live de 2026-11-12 foram 5 min de ~1/s com sondas a cada 10, 20, 40 e 60 s.
    /// </summary>
    internal static TimeSpan NextBackoff(TimeSpan current, double bestProbe, int fps) =>
        TimeSpan.FromSeconds(Math.Min(bestProbe >= fps * 0.3 ? 60 : 20, current.TotalSeconds * 2));

    /// <summary>
    /// A captura da live travou (uma nova do mesmo método pega bem mais)? Era "2x o que a live recebe", o que numa live engasgada a ~35/s pedia 70 de 60
    /// e nunca reabria. Com a mediana da live (não um segundo só), 1,5x basta, o mesmo critério da troca de método.
    /// </summary>
    internal static bool IsStale(int fps, int liveMedian, double probe) => probe >= fps * 0.6 && probe >= Math.Max(1, liveMedian) * 1.5;

    /// <summary>Mediana dos quadros novos/s que a live recebeu desde <paramref name="since"/> (o último segundo, se nada foi medido nesse tempo).</summary>
    int LiveMedianSince(DateTime since)
    {
        int[] v;
        lock (_recent) v = _recent.Where(x => x.At >= since).Select(x => x.Unique).OrderBy(x => x).ToArray();
        return v.Length == 0 ? _lastUnique : v[v.Length / 2];
    }

    internal static bool ShouldSwitchMethod(int fps, double probe, double altFps, bool altCapturesMonitor, bool windowCoversMonitor) =>
        (!altCapturesMonitor || windowCoversMonitor) && altFps >= fps * 0.6 && altFps > probe * 1.5;

    /// <summary>
    /// Reabre na hora, sem sonda? Só depois de o diagnóstico já ter visto, NESTA live e neste método, a captura travar enquanto uma nova pegava (o jogo
    /// mandando ~49/s, a live recebendo 0–16/s). Motivo (PC do grupo, Hookshot, 10/11): a captura da janela travou 3 vezes em 4 min e cada vez levou ~8 s
    /// (3 s caída + ~5 s de sonda) até reabrir, e reabrir resolvia sempre. Com 2 s caída já reabre; no máximo uma vez a cada 30 s, para um trecho do jogo
    /// com poucos quadros de verdade (load) não virar reabertura em série. Se depois disso continuar caída, a sonda volta a decidir.
    /// </summary>
    internal static bool CanFastReopen(bool staleConfirmed, int stuckSeconds, double secondsSinceCaptureStart, double secondsSinceLastFastReopen) =>
        staleConfirmed && stuckSeconds >= 2 && secondsSinceCaptureStart >= 3 && secondsSinceLastFastReopen >= 30;

    void Schedule(string reason)
    {
        // depois de reabrir, espera um pouco antes de diagnosticar de novo; se continuar travando, espera mais, até 20 s (era até 60: numa live de
        // 12/11, depois de duas reaberturas a sonda seguinte disse "confiro de novo em 40 s" e a live ficou ruim até o fim, 35 s depois)
        _nextDiagnosisAt = DateTime.Now + _backoff;
        _backoff = TimeSpan.FromSeconds(Math.Min(20, _backoff.TotalSeconds * 2));
        _restartPending = reason;
    }

    // ------------------------------------------------------------------ comandos

    internal static string? VideoFilter(Pipeline p, (int W, int H) src, (int W, int H) dst)
    {
        bool scale = src != dst;
        string fmt = p.Encoder == "libx264" ? "yuv420p" : "nv12";
        return p.Path switch
        {
            Path.Gpu => null,
            // o AMF recusava a textura da captura (erro 18: outro dispositivo D3D11); mapeando para AMF ele usa o mesmo
            Path.AmfMap => scale ? $"hwmap=derive_device=amf,vpp_amf=w={dst.W}:h={dst.H}:format=nv12" : "hwmap=derive_device=amf",
            Path.AmfVpp => $"vpp_amf=w={dst.W}:h={dst.H}:format=nv12",
            _ => scale
                ? $"hwdownload,format=bgra,scale={dst.W}:{dst.H}:flags=fast_bilinear,format={fmt}"
                : $"hwdownload,format=bgra,format={fmt}"
        };
    }

    /// <summary>Preset com o bitrate que cabe na saída (ver <see cref="Preset.ForOutput"/>); registra no log quando muda.</summary>
    Settings FitBitrateToOutput(Settings s)
    {
        var fitted = s.Preset.ForOutput(OutSize.W, OutSize.H);
        if (fitted.Kbps == s.Preset.Kbps) return s;
        Log.Info($"Bitrate ajustado: o preset {s.Preset.Name} pede {s.Preset.Kbps} kbps, mas a imagem sai em {OutSize.W}x{OutSize.H} (a fonte é {SourceSize.W}x{SourceSize.H}); uso {fitted.Kbps} kbps.");
        return s with { Preset = fitted };
    }

    /// <summary>
    /// Recalcula o bitrate a partir do preset que o usuário pediu, não do já reduzido: a janela 720p baixa o preset 1080p60 de 8000 para 3500 kbps e, quando o jogo
    /// vai para tela cheia 1080p, a imagem de 1080p60 saía com 3500 kbps (campo 2026-11-09, PC do grupo: stream borrada depois da troca janela → tela cheia).
    /// </summary>
    Settings RefitBitrate(Settings s, (int W, int H) dst)
    {
        var wanted = (_requestedPreset ?? s.Preset).ForOutput(dst.W, dst.H);
        if (wanted.Kbps == s.Preset.Kbps) return s;
        Log.Info($"Bitrate ajustado: a imagem agora sai em {dst.W}x{dst.H}; {s.Preset.Kbps} → {wanted.Kbps} kbps.");
        s = s with { Preset = wanted };
        Current = s;
        return s;
    }

    static string MemoryKey(Settings s, CaptureSpec cap) =>
        $"{string.Join("+", SystemInfo.Gpus())}|{(cap.Dda ? "dda" : "wgc")}|{cap.Width}x{cap.Height}->{s.Preset.OutputSize(cap.Width, cap.Height).W}x{s.Preset.OutputSize(cap.Width, cap.Height).H}@{s.Preset.Fps}|{s.Encoder}";

    static string FormatPipeline(Pipeline p) => $"{p.Encoder}|{p.Path}";

    static Pipeline? ParsePipeline(string? text)
    {
        var parts = text?.Split('|');
        return parts is { Length: 2 } && Enum.TryParse<Path>(parts[1], out var path) ? new Pipeline(parts[0], path) : null;
    }

    /// <summary>Argumentos do encoder. O filtro de vídeo não entra aqui: vai em <see cref="CaptureSpec.Args"/>.</summary>
    internal static List<string> EncoderArgs(Pipeline p, Preset preset, bool cfr = true)
    {
        string k = preset.Kbps + "k", half = (preset.Kbps / 2) + "k", gop = (preset.Fps * 2).ToString();
        var a = new List<string>();
        switch (p.Encoder)
        {
            case "h264_nvenc":
                a.AddRange(new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "ll", "-rc", "cbr", "-zerolatency", "1" });
                break;
            case "h264_amf":
                // Medido na RX 6750 XT (PSNR por quadro, 20 s de 1080p60, docs/DESCOBERTAS.md 2026-11-09): com "speed" + buffer
                // de 0,5 s o quadro I (~240 KB) esvazia o buffer e os P seguintes saem em blocos (-10 dB nos 5 quadros depois
                // do IDR, a cada 2 s = a "pulsação" que todos veem). "balanced" + vbaq + buffer de 1 s + teto de tamanho do
                // quadro I corta essa queda para ~3,5 dB com o mesmo bitrate e a mesma velocidade de codificação.
                a.AddRange(new[] { "-c:v", "h264_amf", "-usage", "ultralowlatency", "-quality", "balanced", "-rc", "cbr",
                    "-vbaq", "true", "-max_au_size", (preset.Kbps * 150).ToString() });
                break;
            case "h264_qsv":
                a.AddRange(new[] { "-c:v", "h264_qsv", "-preset", "veryfast", "-low_power", "0" });
                break;
            case "h264_mf":
                // encoder do próprio Windows (Media Foundation): usa o hardware da placa (AMD/Intel/NVIDIA)
                // e aceita a textura da captura direto, sem precisar de Adrenalin
                a.AddRange(new[] { "-c:v", "h264_mf", "-hw_encoding", "1", "-scenario", "display_remoting", "-rate_control", "cbr" });
                break;
            default:
                a.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency" });
                break;
        }
        string buffer = p.Encoder == "h264_amf" ? k : half;   // AMF: 1 s de buffer (ver o comentário do case acima)
        a.AddRange(new[] { "-b:v", k, "-maxrate", k, "-bufsize", buffer, "-g", gop, "-bf", "0" });
        // A captura só entrega quadro quando a tela muda: fps constante + quadro-chave a cada 2 s,
        // senão a imagem congela no amigo quando a tela fica parada ou se perde um pacote.
        if (cfr) a.AddRange(new[] { "-fps_mode", "cfr", "-r", preset.Fps.ToString() });
        else a.AddRange(new[] { "-fps_mode", "passthrough" });
        a.AddRange(new[] { "-force_key_frames", "expr:gte(t,n_forced*2)" });
        if (p.Encoder == "h264_nvenc") a.AddRange(new[] { "-forced-idr", "1" });
        // Sem isso o h264_amf gera UM único IDR (o primeiro): os quadros-chave seguintes saem como quadros I comuns, sem
        // SPS/PPS, e quem entra depois do início (segundo player, reabertura, player que abre alguns segundos depois)
        // nunca consegue decodificar: o ffplay toca só o áudio e não abre a janela ("non-existing PPS 0 referenced").
        // Medido na RX 6750 XT em 8 s de vídeo: 1 IDR sem a opção, 4 IDR (cada um com SPS/PPS) com ela.
        if (p.Encoder == "h264_amf") a.AddRange(new[] { "-forced_idr", "1" });
        // O h264_qsv tem o mesmo defeito: sem -forced_idr os quadros-chave forçados saem como I comuns e só o primeiro é IDR.
        // Notebook do grupo (UHD 620, 720p30, 10/11): o envio de cada espectador espera o próximo quadro-chave (TsPipe) e ele
        // nunca veio; 4 tentativas de assistir terminaram com o ffplay desistindo do SRT em 10 s ("I/O error").
        if (p.Encoder == "h264_qsv") a.AddRange(new[] { "-forced_idr", "1" });
        return a;
    }

    /// <summary>Captura + encoder; o mpegts sai pelo stdout para o <see cref="TsPipe"/>.</summary>
    internal List<string> CaptureArgs(Settings s, CaptureSpec cap, Pipeline p, bool withAudio)
    {
        var a = new List<string> { "-hide_banner", "-loglevel", "warning", "-stats", "-stats_period", "1" };
        var outputs = new List<string>();
        int audioInput = cap.Args(VideoFilter(p, SourceSize, OutSize), a, outputs);
        if (withAudio) a.AddRange(AudioInputArgs());
        a.AddRange(outputs);
        if (withAudio) a.AddRange(new[] { "-map", $"{audioInput}:a:0" });
        a.AddRange(EncoderArgs(p, s.Preset));
        if (withAudio) a.AddRange(AudioEncoderArgs());
        a.AddRange(new[] { "-f", "mpegts", "-flush_packets", "1", "-muxdelay", "0", "-muxpreload", "0", "pipe:1" });
        return a;
    }

    /// <summary>
    /// Envio: copia o TS para o mesmo SRT. Capture/pausa usam um relógio comum (ContinueMediaClock); copyts preserva esse relógio.
    /// O corretor global dts_delta_threshold alternava o deslocamento entre áudio e vídeo após uma descontinuidade de apenas um deles:
    /// no ensaio de 13/11 continuou corrigindo a cada pacote mesmo com a origem já recuperada; dois players terminaram ~860 ms fora.
    /// copyts + start_at_zero recuperou os dois sem reabrir nada. Não recodifica ou muda o conteúdo dos streams.
    /// </summary>
    internal static List<string> SrtArgs(Settings s, int port, string pass)
    {
        Tools.PassphraseHint = pass;
        long latencyUs = s.LatencyMs * 1000L;
        return new List<string> { "-hide_banner", "-loglevel", "warning", "-nostats", "-copyts", "-start_at_zero",
            "-fflags", "+nobuffer", "-probesize", "500000", "-analyzeduration", "500000", "-f", "mpegts", "-i", "pipe:0",
            "-map", "0", "-c", "copy",
            "-f", "mpegts", "-flush_packets", "1", "-muxdelay", "0", "-muxpreload", "0",
            $"srt://127.0.0.1:{port}?mode=listener&latency={latencyUs}&passphrase={pass}&pbkeylen=16&transtype=live&pkt_size=1316" };
    }

    /// <summary>
    /// O som do <see cref="AudioPump"/> pelo stdin. Em WAV com -max_size 3840 (10 ms) quando o ffmpeg aceita: o PCM cru (-f f32le) entra em pacotes
    /// de 4.096 amostras (85 ms) e o som saía da live em rajadas, ~100 ms sem som a cada 2 s no celular (2026-11-21; ver <see cref="Tools.WavAudioInput"/>).
    /// O cabeçalho WAV é escrito pelo <see cref="AudioPump.BeginPipe"/>.
    /// </summary>
    internal static string[] AudioInputArgs() => Tools.InputQueueArgs.Concat(new[] { "-probesize", "32", "-analyzeduration", "0", "-fflags", "nobuffer" })
        .Concat(Tools.WavAudioInput
            ? new[] { "-f", "wav", "-ignore_length", "1", "-max_size", (AudioPump.BytesPerSecond / 100).ToString() }
            : new[] { "-f", "f32le", "-ar", AudioPump.SampleRate.ToString(), "-ac", AudioPump.Channels.ToString() })
        .Concat(new[] { "-i", "pipe:0" }).ToArray();

    /// <summary>
    /// Limitador antes do Opus: o jogo passa de 0 dBFS nos golpes (o <see cref="AudioPump"/> agora entrega em float, sem cortar) e o Opus
    /// colado no teto ainda devolve picos acima de 1,0, que o ffplay de quem assiste corta ao tocar em 16 bits: era o grave estourando na live
    /// de um PC do grupo (Dragon's Dogma 2). limit 0,89 ≈ −1 dB de folga; level=0 porque o padrão (auto level) aumenta tudo de volta até o teto.
    /// attack 5 ms: com 1 ms o grave de 55 Hz distorce no próprio limitador. Medido (golpe simulado com pico +6,8 dBFS, Opus 128k): antes
    /// 4.021 amostras acima de 1,0 depois de decodificar; com o limitador nenhuma, pico −0,7 dBFS e o grave tão limpo quanto a origem.
    /// Sem golpe acima do teto o som sai igual. Custo: ~0,05% de um núcleo; atrasa o áudio ~5 ms. Não passa pela placa de vídeo.
    /// </summary>
    internal static string[] AudioEncoderArgs() => new[] { "-af", AudioFilter(),
        "-c:a", "libopus", "-b:a", "128k", "-application", "lowdelay", "-frame_duration", "10" };

    /// <summary>
    /// Antes do limitador, um compressor só abaixo de 120 Hz. Certos golpes do Dragon's Dogma 2 (live de 12/11) trazem um
    /// subgrave mono de ~60 Hz uns 40 dB acima do grave normal do jogo, e o "Alcance dinâmico" do jogo não muda isso (medido em Amplo
    /// e em Pequeno). Quem transmite ouve fraco (o fone dele quase não toca 60 Hz, ou o Windows comprime depois do ponto da captura);
    /// quem assiste sobe o volume para ouvir o jogo e o impacto estoura o fone. Medido no som do player de quem assistia: nos impactos
    /// o subgrave cai ~9 dB (até 11) e o resto quase não muda (−0,6 dB); fora deles, 0,0 dB. O limiar é fixo: num jogo com grave forte e
    /// volume alto ele também segura um pouco o grave fora dos impactos. Custo ~0,2% de um núcleo; não passa pela placa de vídeo.
    /// STREEMU_GRAVE=antigo tira o compressor (para comparar o antes e o depois); STREEMU_GRAVE=leve volta ao limiar de −26 dB que valia até 13/11.
    /// O limiar é −36 dB porque, na captura do DD2 de 13/11 (origem, sem clipping: pico 0,42), os −26 dB mal agiam (mediana −0,1 dB nos
    /// 151 impactos, −2,1 dB nos 15 mais fortes); com −36 dB os 15 mais fortes caem 6,7 dB e o grave de música/ambiente, 1,3 a 1,5 dB.
    /// Não está provado que isso resolve o estouro para quem assiste (falta medir o lado do player). Ver docs/DESCOBERTAS.md.
    /// </summary>
    internal static string AudioFilter()
    {
        const string limiter = "alimiter=limit=0.89:attack=5:release=50:level=0";
        string modo = Environment.GetEnvironmentVariable("STREEMU_GRAVE") ?? "";
        if (modo == "antigo") return limiter;
        int limiarDb = modo == "leve" ? -26 : -36;
        return $"acrossover=split=120:order=4th[lo][hi];[lo]acompressor=threshold={limiarDb}dB:ratio=4:attack=10:release=250:knee=6[loc];"
            + "[loc][hi]amix=inputs=2:normalize=0," + limiter;
    }

    /// <summary>Alguma placa AMD no PC? (nome da placa como o Windows lista; sem lista, assume que sim para não esconder o AMF)</summary>
    internal static bool HasAmdGpu(IReadOnlyCollection<string> gpus) =>
        gpus.Count == 0 || gpus.Any(g => g.Contains("AMD", StringComparison.OrdinalIgnoreCase) || g.Contains("Radeon", StringComparison.OrdinalIgnoreCase));

    /// <summary>Testa encoders/caminhos em ordem e fica com o primeiro que codifica de verdade alguns quadros desta fonte.</summary>
    /// <param name="aoFalhar">Só observa (telemetria): o candidato que reprovou, a última linha do ffmpeg e se demorou demais; nulo no fim, quando nenhum passou.</param>
    internal static Pipeline ChoosePipeline(EncoderChoice choice, Preset preset, CaptureSpec cap, (int W, int H) src, (int W, int H) dst, Pipeline? remembered = null,
        Action<Pipeline?, string?, bool>? aoFalhar = null)
    {
        bool scale = src != dst;
        var encoders = choice switch
        {
            EncoderChoice.Nvenc => new[] { "h264_nvenc" },
            EncoderChoice.Amf => new[] { "h264_amf" },
            EncoderChoice.Qsv => new[] { "h264_qsv" },
            EncoderChoice.X264 => new[] { "libx264" },
            EncoderChoice.Mf => new[] { "h264_mf" },
            _ => new[] { "h264_nvenc", "h264_amf", "h264_mf", "h264_qsv", "libx264" }
        };
        // Sem placa AMD o AMF nem abre ("amfrt64.dll failed to open"): os dois testes dele custavam ~2,4 s no começo de cada live
        // (PC do grupo, RTX 5070, 12/11, com redução 3440x1440 → 2580x1080, em que os caminhos do AMF vêm antes do NVENC).
        if (choice == EncoderChoice.Auto && !HasAmdGpu(SystemInfo.Gpus())) encoders = encoders.Where(e => e != "h264_amf").ToArray();
        // Primeiro todos os caminhos que deixam o quadro na GPU (de todos os encoders), depois os que copiam para a CPU:
        // numa AMD do grupo a cópia para a CPU travava em ~47 fps, então Media Foundation na GPU vem antes dela.
        var candidates = new List<Pipeline>();
        foreach (var enc in encoders)
        {
            if (enc == "h264_amf") { candidates.Add(new Pipeline(enc, Path.AmfMap)); if (scale) candidates.Add(new Pipeline(enc, Path.AmfVpp)); }
            if (!scale && enc != "libx264") candidates.Add(new Pipeline(enc, Path.Gpu));
        }
        foreach (var enc in encoders)
            if (enc != "h264_mf" || encoders.Length == 1) candidates.Add(new Pipeline(enc, Path.Cpu));
        if (remembered != null && candidates.Remove(remembered))
        {
            candidates.Insert(0, remembered);
            Log.Info($"Tentando primeiro o pipeline que funcionou da última vez: {remembered}.");
        }
        var errors = new List<string>();
        foreach (var p in candidates)
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error" };
            var outputs = new List<string>();
            cap.Args(VideoFilter(p, src, dst), args, outputs);
            args.AddRange(outputs);
            args.AddRange(EncoderArgs(p, preset));
            args.AddRange(new[] { "-frames:v", "3", "-f", "null", "-" });
            var lines = new List<string>();
            using var proc = Tools.Start(Tools.Ffmpeg, args, "teste-encoder", redirectStdin: false, onStderr: l => { lock (lines) lines.Add(l); });
            bool exited = proc.WaitForExit(8000);
            if (!exited) Tools.KillQuietly(proc);
            proc.WaitForExit(1000);
            if (exited && proc.ExitCode == 0) { Log.Info($"Encoder OK: {p}"); return p; }
            string? last;
            lock (lines) last = lines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
            string why = last ?? (exited ? $"código {proc.ExitCode}" : "demorou demais (a janela está minimizada?)");
            errors.Add($"{p}: {why}");
            Log.Info($"Encoder falhou: {p}: {why}");
            try { aoFalhar?.Invoke(p, last, !exited); } catch { }
        }
        try { aoFalhar?.Invoke(null, null, false); } catch { }
        throw new InvalidOperationException("nenhum encoder funcionou com essa fonte. " + string.Join(" | ", errors));
    }

    public void Dispose() => Stop();
}
