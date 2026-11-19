using System.Diagnostics;
using Streemu.Util;

namespace Streemu.Media;

/// <summary>
/// Gera a miniatura de quem transmite a partir do vídeo JÁ CODIFICADO (spec 006): é mais um consumidor do
/// <see cref="TsSplitter"/> (um <see cref="TsPipe"/> como o de cada espectador) que alimenta um ffmpeg à parte, só com os quadros-chave
/// (<c>-skip_frame nokey</c>), na CPU, com uma thread e prioridade baixa. Não toca na captura nem nos encoders, então é igual na AMD,
/// NVIDIA e CPU, e se travar a transmissão não sente (o TsPipe descarta a fila de quem não lê).
/// Só roda enquanto alguém pede: <see cref="Want"/> vale por 45 s.
/// Uso: <c>feeder.Want(); // a cada pedido de miniatura</c>; <c>feeder.Ready += jpeg => envia(jpeg);</c>
/// </summary>
sealed class ThumbnailFeeder : IDisposable
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(45);
    const int IntervalSeconds = 8, MaxJpegBytes = 12 * 1024;

    readonly TsSplitter _splitter;
    readonly object _gate = new();
    Process? _ffmpeg;
    TsPipe? _pipe;
    WindowedStream? _window;
    DateTime _until = DateTime.MinValue;
    System.Threading.Timer? _expiry;
    volatile bool _disposed;

    public ThumbnailFeeder(TsSplitter splitter) => _splitter = splitter;

    /// <summary>A miniatura mais recente (nula antes da primeira). Quem pede logo depois de outro recebe esta sem esperar.</summary>
    public byte[]? Latest { get; private set; }
    /// <summary>Saiu uma miniatura nova (thread de fundo).</summary>
    public event Action<byte[]>? Ready;
    public bool Running { get { lock (_gate) return _ffmpeg != null; } }

    /// <summary>Alguém quer a miniatura: liga o ffmpeg se estiver desligado e renova o prazo de 45 s.</summary>
    public void Want()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _until = DateTime.UtcNow + Lifetime;
            if (_ffmpeg == null) StartLocked();
            _expiry ??= new System.Threading.Timer(_ => StopIfExpired(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
    }

    void StartLocked()
    {
        var args = new[]
        {
            "-hide_banner", "-loglevel", "error", "-threads", "1", "-probesize", "300000", "-analyzeduration", "500000", "-skip_frame", "nokey", "-f", "mpegts", "-i", "pipe:0",
            "-an", "-vf", $"fps=1/{IntervalSeconds},scale=192:108:flags=bilinear:force_original_aspect_ratio=decrease,pad=192:108:(ow-iw)/2:(oh-ih)/2", "-q:v", "6", "-f", "image2pipe", "-c:v", "mjpeg", "pipe:1",
        };
        try
        {
            var p = Tools.Start(Tools.Ffmpeg, args, "ffmpeg-miniatura", redirectStdin: true, binaryStdout: true);
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { /* já saiu */ }
            _ffmpeg = p;
            _window = new WindowedStream(p.StandardInput.BaseStream, TimeSpan.FromSeconds(IntervalSeconds), TimeSpan.FromSeconds(2.4));
            _pipe = new TsPipe(_window, startAtKey: true);
            _splitter.Add(_pipe);
            new Thread(() => ReadJpegs(p)) { IsBackground = true, Name = "miniatura-leitura", Priority = ThreadPriority.BelowNormal }.Start();
        }
        catch (Exception e)
        {
            Log.Info("Não consegui abrir o ffmpeg da miniatura (a transmissão segue normal): " + e.Message);
            StopLocked();
        }
    }

    void ReadJpegs(Process p)
    {
        try
        {
            var stdout = p.StandardOutput.BaseStream;
            var buffer = new byte[64 * 1024];
            int have = 0, read;
            while ((read = stdout.Read(buffer, have, buffer.Length - have)) > 0)
            {
                have += read;
                have = ExtractJpegs(buffer, have);
                if (have == buffer.Length) have = 0; // sem JPEG válido num bloco cheio: recomeça
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException) { /* o ffmpeg foi encerrado */ }
    }

    /// <summary>Tira do começo do buffer cada JPEG completo (FFD8 ... FFD9), publica e devolve quantos bytes sobraram (o início do próximo).</summary>
    int ExtractJpegs(byte[] buffer, int have)
    {
        int consumed = 0;
        while (true)
        {
            int start = IndexOf(buffer, consumed, have, 0xFF, 0xD8);
            if (start < 0) { consumed = have > 0 ? have - 1 : 0; break; }
            int end = IndexOf(buffer, start + 2, have, 0xFF, 0xD9);
            if (end < 0) { consumed = start; break; }
            Publish(buffer.AsSpan(start, end + 2 - start).ToArray());
            consumed = end + 2;
        }
        Buffer.BlockCopy(buffer, consumed, buffer, 0, have - consumed);
        return have - consumed;
    }

    static int IndexOf(byte[] b, int from, int to, byte first, byte second)
    {
        for (int i = from; i + 1 < to; i++) if (b[i] == first && b[i + 1] == second) return i;
        return -1;
    }

    void Publish(byte[] jpeg)
    {
        if (jpeg.Length > MaxJpegBytes) { Log.Info($"Miniatura grande demais ({jpeg.Length} bytes); ignorada."); return; }
        Latest = jpeg;
        Log.Info($"Miniatura gerada ({jpeg.Length} bytes).");
        Ready?.Invoke(jpeg);
    }

    void StopIfExpired()
    {
        lock (_gate) if (_ffmpeg != null && DateTime.UtcNow >= _until) StopLocked();
    }

    void StopLocked()
    {
        _splitter.Remove(_pipe);
        _pipe?.Dispose(); _pipe = null; _window = null;
        Tools.KillQuietly(_ffmpeg); _ffmpeg = null;
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _expiry?.Dispose(); _expiry = null; StopLocked(); }
    }
}
