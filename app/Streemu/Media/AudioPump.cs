using System.Diagnostics;
using System.Runtime.InteropServices;
using Streemu.Util;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Streemu.Media;

/// <summary>
/// Captura o áudio de um processo (ou de tudo menos um processo) e entrega PCM 48 kHz float 32 estéreo
/// em tempo real para o stdin do ffmpeg, preenchendo com silêncio quando nada toca
/// (o loopback do Windows para de mandar pacotes no silêncio, o que travaria o vídeo no muxer).
/// </summary>
/// <remarks>
/// Float e não 16 bits: o Windows mistura em float e o jogo passa de 0 dBFS nos golpes; pedindo s16, a conversão cortava seco tudo acima
/// do teto e o grave estourava para quem assistia (Dragon's Dogma 2, 08/11 e de novo 12/11, num PC do grupo; o perfil dele mistura em 32 bits).
/// Em float o que passa de 1,0 chega inteiro ao ffmpeg, e o limitador antes do Opus (<see cref="StreamSender.AudioEncoderArgs"/>) abaixa.
/// </remarks>
public sealed class AudioPump : IDisposable
{
    public const int SampleRate = 48000, Channels = 2, BytesPerSample = 4, BlockAlign = Channels * BytesPerSample, BytesPerSecond = SampleRate * BlockAlign;
    /// <summary>O formato que vai pelo pipe ao ffmpeg (<c>-f f32le</c>), qualquer que seja o que o Windows aceitou.</summary>
    public static WaveFormat PipeFormat => WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
    readonly WasapiRecorder _recorder;
    readonly LiveAudioPacer _pacer = new();
    long _callbackAt, _maxCallbackGapMs, _timestampErrors, _discontinuities;
    int _captureAgeMs = int.MinValue;
    readonly AudioDump? _dump;   // só com STREEMU_AUDIO_DUMP (diagnóstico do áudio que estoura); nulo no uso normal
    readonly WasapiRecorder? _appRecorder;          // "PC todo menos Discord": segunda captura só do Streemu, para tirar o som do player dos amigos
    readonly AppAudioSubtractor? _subtractor;
    /// <summary>O som do próprio Streemu (player da live de um amigo) está sendo tirado desta captura.</summary>
    public bool RemovesAppAudio => _subtractor != null;
    bool _subtractorReported;
    byte[] _converted = Array.Empty<byte>();   // só no plano B (Windows não abriu em float): s16 convertido para float
    public string Description { get; }

    AudioPump(WasapiRecorder recorder, string description, AudioDump? dump, bool fromPcm16, WasapiRecorder? appRecorder = null)
    {
        _recorder = recorder;
        _dump = dump;
        _appRecorder = appRecorder;
        Description = description;
        if (appRecorder != null)
        {
            _subtractor = new AppAudioSubtractor(pcm => Push(pcm));
            _recorder.DataAvailable += (buffer, flags, devicePosition, qpcPosition) => { CaptureTiming(flags, qpcPosition); _subtractor.AddAll(qpcPosition, buffer); };
            appRecorder.DataAvailable += (buffer, flags, devicePosition, qpcPosition) => _subtractor.AddApp(qpcPosition, buffer);
            appRecorder.StartRecording();
        }
        else if (fromPcm16)
            _recorder.DataAvailable += (buffer, flags, devicePosition, qpcPosition) => { CaptureTiming(flags, qpcPosition); Push(ToFloat(buffer)); };
        else
            _recorder.DataAvailable += (buffer, flags, devicePosition, qpcPosition) => { CaptureTiming(flags, qpcPosition); Push(buffer); };
        _recorder.RecordingStopped += (_, e) => Log.Info("Captura de áudio parou" + (e.Exception != null ? ": " + e.Exception.Message : "."));
        _recorder.StartRecording();
    }

    void Push(ReadOnlySpan<byte> pcmFloat) { _dump?.OnPipe(pcmFloat); _pacer.Push(pcmFloat); }

    void CaptureTiming(AudioClientBufferFlags flags, long qpc)
    {
        long now = Stopwatch.GetTimestamp(), before = Interlocked.Exchange(ref _callbackAt, now);
        if (before != 0)
        {
            long gap = (long)Stopwatch.GetElapsedTime(before, now).TotalMilliseconds;
            if (gap > Interlocked.Read(ref _maxCallbackGapMs)) Interlocked.Exchange(ref _maxCallbackGapMs, gap);
        }
        if (((int)flags & 1) != 0) Interlocked.Increment(ref _discontinuities);
        // Drivers podem devolver zero ou QPC inválido. Serve para diagnóstico, nunca para inserir/cortar som automaticamente.
        double age = (now * (10_000_000.0 / Stopwatch.Frequency) - qpc) / 10_000;
        bool valid = qpc > 0 && ((int)flags & 4) == 0 && age >= -50 && age <= 10_000;
        Volatile.Write(ref _captureAgeMs, valid ? (int)Math.Round(age) : int.MinValue);
        if (!valid) Interlocked.Increment(ref _timestampErrors);
    }

    public System.Text.Json.Nodes.JsonObject SyncSnapshot()
    {
        var o = _pacer.Snapshot();
        int age = Volatile.Read(ref _captureAgeMs);
        double? callbackAge = TsAvMeter.Age(Interlocked.Read(ref _callbackAt));
        o["idade_callback_ms"] = callbackAge;
        o["idade_captura_ms"] = age != int.MinValue && callbackAge < 1000 ? age : null;
        o["maior_intervalo_callback_ms"] = Interlocked.Read(ref _maxCallbackGapMs);
        o["tempos_invalidos"] = Interlocked.Read(ref _timestampErrors);
        o["descontinuidades_windows"] = Interlocked.Read(ref _discontinuities);
        return o;
    }

    ReadOnlySpan<byte> ToFloat(ReadOnlySpan<byte> pcm16)
    {
        var src = MemoryMarshal.Cast<byte, short>(pcm16);
        if (_converted.Length < src.Length * 4) _converted = new byte[src.Length * 4];
        var dst = MemoryMarshal.Cast<byte, float>(_converted.AsSpan(0, src.Length * 4));
        for (int i = 0; i < src.Length; i++) dst[i] = src[i] / 32768f;
        return _converted.AsSpan(0, src.Length * 4);
    }

    public static async Task<AudioPump> CreateAsync(int processId, bool include, string description)
    {
        var mode = include ? ProcessLoopbackMode.IncludeTargetProcessTree : ProcessLoopbackMode.ExcludeTargetProcessTree;
        WasapiRecorder recorder;
        bool fromPcm16 = false;
        try
        {
            recorder = await Build(processId, mode, PipeFormat);
            if (recorder.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat || recorder.WaveFormat.BitsPerSample != 32)
            {
                string got = recorder.WaveFormat.ToString();
                recorder.Dispose();
                throw new NotSupportedException($"o Windows devolveu {got}");
            }
        }
        catch (Exception e)
        {
            // Plano B: o áudio de antes (s16, que corta acima de 0 dBFS) é melhor que a live sem som.
            Log.Info($"Áudio: não abriu em float 32 ({e.Message}); vou em 16 bits (o grave pode estourar nos sons mais altos).");
            recorder = await Build(processId, mode, new WaveFormat(SampleRate, 16, Channels));
            fromPcm16 = true;
        }
        Log.Info($"Áudio: {description} (pid {processId}, {(include ? "incluir" : "excluir")} árvore), formato {recorder.WaveFormat}");
        var dump = await AudioDump.StartAsync(processId, mode);
        var appRecorder = !include && !fromPcm16 ? await TryOpenAppRecorderAsync(processId) : null;
        return new AudioPump(recorder, description, dump, fromPcm16, appRecorder);
    }

    /// <summary>
    /// "PC todo menos Discord" só exclui o Discord; o player da live de um amigo (filho do Streemu) entrava na live de quem assiste e transmite ao mesmo tempo.
    /// Abre uma segunda captura só do Streemu para o <see cref="AppAudioSubtractor"/> tirar esse som. Sem ela (o Windows recusou, ou STREEMU_SOM_AMIGO=vaza para
    /// comparar), a live segue como antes e o app avisa na tela. Quando o Discord não está aberto, a captura já exclui o próprio streemu: não precisa.
    /// </summary>
    static async Task<WasapiRecorder?> TryOpenAppRecorderAsync(int excludedProcessId)
    {
        if (excludedProcessId == Environment.ProcessId || Environment.GetEnvironmentVariable("STREEMU_SOM_AMIGO") == "vaza") return null;
        try
        {
            var recorder = await Build(Environment.ProcessId, ProcessLoopbackMode.IncludeTargetProcessTree, PipeFormat);
            if (recorder.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat && recorder.WaveFormat.BitsPerSample == 32) return recorder;
            recorder.Dispose();
            throw new NotSupportedException($"o Windows devolveu {recorder.WaveFormat}");
        }
        catch (Exception e)
        {
            Log.Info($"Áudio: não consegui separar o som do Streemu ({e.Message}); o som da live que você assiste pode entrar na sua.");
            return null;
        }
    }

    static Task<WasapiRecorder> Build(int processId, ProcessLoopbackMode mode, WaveFormat format) => new WasapiRecorderBuilder()
        .WithProcessLoopback((uint)processId, mode)
        .WithFormat(format)
        .WithBufferLength(20)
        .BuildAsync();

    /// <summary>Liga a saída a um novo ffmpeg. Descarta o que estava acumulado e recomeça o relógio (o ffmpeg novo conta o tempo do zero).</summary>
    public void Attach(Stream? sink)
    {
        _pacer.Attach(sink);
    }

    public void Dispose()
    {
        _pacer.Dispose();
        try { _recorder.StopRecording(); } catch { }
        try { _recorder.Dispose(); } catch { }
        try { _appRecorder?.StopRecording(); } catch { }
        try { _appRecorder?.Dispose(); } catch { }
        if (_subtractor != null && !_subtractorReported) { _subtractorReported = true; Log.Info($"Áudio: som do Streemu tirado de {_subtractor.Counts.Paired} pacotes ({_subtractor.Counts.Passed} passaram sem par); {_subtractor.Detail}."); }
        _subtractor?.Dispose();
        _dump?.Dispose();
    }
}
