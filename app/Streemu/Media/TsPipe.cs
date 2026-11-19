using System.Diagnostics;
using Streemu.Util;

namespace Streemu.Media;

/// <summary>
/// Lê o mpegts do ffmpeg da CAPTURA (stdout) e reparte para um <see cref="TsPipe"/> por espectador.
/// Motivo (teste 0.1.9, num PC do grupo no Cyberpunk): depois de um load a captura do monitor passou a entregar ~2 quadros/s
/// até ser reaberta. Com a captura num processo à parte, dá para reabrir só ela; os envios SRT continuam e o player
/// dos amigos não fecha (só fica ~1–2 s parado). E com vários amigos assistindo, o encoder roda uma vez só.
/// Só passa pacotes TS inteiros (188 bytes); ao trocar de processo, o pedaço que sobrou do anterior é descartado.
/// </summary>
sealed class TsSplitter : IDisposable
{
    const int Ts = 188;
    readonly object _gate = new();
    TsPipe[] _sinks = Array.Empty<TsPipe>();
    int _generation;
    volatile bool _closed;

    /// <summary>
    /// Segura o vídeo (o áudio passa): o jogo saiu da frente e a captura do monitor mostraria o Alt+Tab ou a área de
    /// trabalho. Quem assiste fica com a última imagem do jogo; ao soltar, o vídeo volta no próximo quadro-chave.
    /// </summary>
    public volatile bool HoldVideo;
    public long HeldVideoPackets;

    // Medidor de alinhamento áudio/vídeo da captura (tempo de apresentação dos PES, 90 kHz). Motivo (campo 18:49):
    // depois de reabrir a captura o ffmpeg do envio corrigia "timestamp discontinuity" a cada pacote e o player caía;
    // isso acontece quando áudio e vídeo da captura se afastam mais de 1 s.
    readonly TsAvMeter _meter = new();
    /// <summary>Áudio menos vídeo, em ms, na última medida (positivo = áudio na frente). Nulo sem os dois.</summary>
    public int? AvOffsetMs => _meter.Snapshot()["audio_menos_video_ms"]?.GetValue<int>();
    public int MaxAvOffsetMs => _meter.Snapshot()["pior_ms"]!.GetValue<int>();
    public System.Text.Json.Nodes.JsonObject SyncSnapshot() => _meter.Snapshot();

    public void Add(TsPipe pipe) { lock (_gate) _sinks = _sinks.Append(pipe).ToArray(); }
    public void Remove(TsPipe? pipe) { if (pipe != null) lock (_gate) _sinks = _sinks.Where(p => p != pipe).ToArray(); }

    /// <summary>Passa a ler deste stdout; o processo anterior deixa de ser ouvido.</summary>
    public void Feed(Stream source)
    {
        int gen;
        lock (_gate) { gen = Interlocked.Increment(ref _generation); _meter.BeginEpoch(); }
        new Thread(() => ReadLoop(source, gen)) { IsBackground = true, Name = "ts-split-read" }.Start();
    }

    void ReadLoop(Stream src, int gen)
    {
        var buf = new byte[Ts * 700];
        int have = 0;
        while (!_closed && gen == Volatile.Read(ref _generation))
        {
            int n;
            try { n = src.Read(buf, have, buf.Length - have); } catch { break; }
            if (n <= 0) break;
            have += n;
            int start = 0;
            // realinha se algo vier fora do 0x47 (não deveria: o muxer escreve pacotes inteiros desde o início)
            while (start + Ts <= have && buf[start] != 0x47) start++;
            int whole = start + (have - start) / Ts * Ts;
            if (whole > start && gen == Volatile.Read(ref _generation))
            {
                lock (_gate)
                {
                    if (gen != Volatile.Read(ref _generation)) break;
                    _meter.Observe(buf.AsSpan(start, whole - start));
                }
                bool hold = HoldVideo;
                if (hold) HeldVideoPackets += (whole - start) / Ts;
                foreach (var sink in _sinks) sink.Push(buf, start, whole, hold);
            }
            Buffer.BlockCopy(buf, whole, buf, 0, have - whole);
            have -= whole;
        }
    }

    public void Dispose()
    {
        _closed = true;
        Interlocked.Increment(ref _generation);
    }
}

/// <summary>
/// Leva o mpegts ao ffmpeg do ENVIO de um espectador (stdin, fala SRT com o player dele).
/// Nunca acumula atraso: se o envio parar de ler (esperando o amigo conectar, rede travada) a fila é jogada fora e
/// a passagem recomeça no próximo quadro-chave do vídeo (PAT/PMT passam sempre). Cada espectador tem a sua fila,
/// então um amigo com a rede ruim não atrasa os outros.
/// </summary>
sealed class TsPipe : IDisposable
{
    const int Ts = 188;
    const int VideoPid = 0x100, PatPid = 0x000, PmtPid = 0x1000; // PIDs padrão do muxer mpegts do ffmpeg (vídeo é o 1º stream)
    const int MaxQueued = 3 * 1024 * 1024;                       // ~1,3 s a 19 Mbps
    static readonly TimeSpan BlockedWrite = TimeSpan.FromMilliseconds(400);

    readonly Stream _sink;
    readonly object _gate = new();
    readonly Queue<(byte[] Data, long At)> _queue = new();
    readonly AutoResetEvent _signal = new(false);
    int _queued;
    bool _waitKey;
    bool _audioWhileWaiting; // esperando o quadro-chave por causa da trava de foco: o áudio do jogo continua
    volatile bool _closed;

    public long DroppedPackets, Resyncs;
    long _lastWriteAt, _maxWriteMs;
    public System.Text.Json.Nodes.JsonObject SyncSnapshot()
    {
        lock (_gate) return new()
        {
            ["fila_bytes"] = _queued, ["idade_fila_ms"] = _queue.TryPeek(out var first) ? TsAvMeter.Age(first.At) : 0,
            ["esperando_quadro_chave"] = _waitKey, ["ressincronizacoes_fila"] = Resyncs, ["pacotes_descartados"] = DroppedPackets,
            ["idade_ultima_escrita_ms"] = TsAvMeter.Age(Interlocked.Read(ref _lastWriteAt)), ["pior_escrita_ms"] = Interlocked.Read(ref _maxWriteMs),
        };
    }
    /// <summary>O envio parou de aceitar dados (o ffmpeg do SRT saiu).</summary>
    public event Action? Broken;

    /// <param name="startAtKey">Quem entra com a transmissão já rodando começa no próximo quadro-chave (o meio de um GOP não decodifica).</param>
    public TsPipe(Stream sink, bool startAtKey = false)
    {
        _sink = sink;
        _waitKey = startAtKey;
        new Thread(WriteLoop) { IsBackground = true, Name = "ts-pipe-write" }.Start();
    }

    /// <param name="holdVideo">Trava de foco: descarta o vídeo, deixa o áudio passar e, ao soltar, recomeça no quadro-chave.</param>
    public void Push(byte[] buf, int start, int end, bool holdVideo = false)
    {
        if (_closed) return;
        lock (_gate)
        {
            if (holdVideo && !(_waitKey && _audioWhileWaiting)) { _waitKey = true; _audioWhileWaiting = true; }
            byte[] chunk;
            if (_waitKey)
            {
                var keep = new MemoryStream();
                for (int i = start; i + Ts <= end; i += Ts)
                {
                    int pid = ((buf[i + 1] & 0x1F) << 8) | buf[i + 2];
                    if (_waitKey && !holdVideo && pid == VideoPid && IsKeyStart(buf, i)) { _waitKey = false; _audioWhileWaiting = false; }
                    if (!_waitKey || pid == PatPid || pid == PmtPid || (_audioWhileWaiting && pid != VideoPid)) keep.Write(buf, i, Ts);
                    else if (!_audioWhileWaiting) DroppedPackets++; // vídeo segurado pela trava de foco não é "atraso"
                }
                chunk = keep.ToArray();
            }
            else chunk = buf.AsSpan(start, end - start).ToArray();
            if (chunk.Length == 0) return;
            _queue.Enqueue((chunk, Stopwatch.GetTimestamp()));
            _queued += chunk.Length;
            if (_queued > MaxQueued) DropAll();
        }
        _signal.Set();
    }

    /// <summary>Início de quadro-chave: payload_unit_start + campo de adaptação com random_access_indicator.</summary>
    static bool IsKeyStart(byte[] b, int i) =>
        (b[i + 1] & 0x40) != 0 && (b[i + 3] & 0x20) != 0 && b[i + 4] > 0 && (b[i + 5] & 0x40) != 0;

    void DropAll()
    {
        foreach (var c in _queue) DroppedPackets += c.Data.Length / Ts;
        _queue.Clear();
        _queued = 0;
        _waitKey = true;
        _audioWhileWaiting = false;
        Resyncs++;
    }

    void WriteLoop()
    {
        while (!_closed)
        {
            byte[]? chunk = null;
            lock (_gate)
            {
                if (_queue.Count > 0) { chunk = _queue.Dequeue().Data; _queued -= chunk.Length; }
            }
            if (chunk == null) { _signal.WaitOne(200); continue; }
            var sw = Stopwatch.StartNew();
            try { _sink.Write(chunk, 0, chunk.Length); _sink.Flush(); }
            catch
            {
                if (!_closed) { _closed = true; Broken?.Invoke(); }
                return;
            }
            Interlocked.Exchange(ref _lastWriteAt, Stopwatch.GetTimestamp());
            long ms = (long)sw.Elapsed.TotalMilliseconds;
            if (ms > Interlocked.Read(ref _maxWriteMs)) Interlocked.Exchange(ref _maxWriteMs, ms);
            // ficou parado (esperando o amigo conectar ou a rede): o que está na fila é velho; recomeça no próximo quadro-chave
            if (sw.Elapsed > BlockedWrite) lock (_gate) DropAll();
        }
    }

    public void Dispose()
    {
        _closed = true;
        _signal.Set();
        try { _sink.Dispose(); } catch { }
    }
}
