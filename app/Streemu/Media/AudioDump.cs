using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Streemu.Util;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Streemu.Media;

/// <summary>
/// Diagnóstico do áudio que estoura no grave (2026-11-08, Dragon's Dogma 2 num PC do grupo): com STREEMU_AUDIO_DUMP=&lt;pasta&gt; o app grava o PCM que o
/// <see cref="AudioPump"/> manda ao ffmpeg (48 kHz float 32, antes do limitador) e, em paralelo, uma segunda captura do MESMO processo em s16 (o que o app
/// recebia até 12/11), e mede por segundo o pico e quantas amostras bateram no teto (acima de 1,0 no float; ±32767 no s16). Float acima de 1,0 = golpe que
/// o limitador antes do Opus segura; s16 no teto = o corte que estourava o grave. Só para investigar: nada disso roda sem a variável.
/// Grava na pasta: audio-f32-*.wav (o que vai ao ffmpeg), audio-s16-*.wav (comparação) e audio-por-segundo-*.csv; para sozinho em STREEMU_AUDIO_DUMP_SECONDS (padrão 120). Os .wav NUNCA vão para o git.
/// Uso: <c>set STREEMU_AUDIO_DUMP=C:\temp\audio</c> e transmita o jogo; no log aparecem as linhas "[audio-dump]".
/// </summary>
sealed class AudioDump : IDisposable
{
    const string EnvDir = "STREEMU_AUDIO_DUMP", EnvSeconds = "STREEMU_AUDIO_DUMP_SECONDS";
    const double NearFullScale = 0.98;

    readonly WaveFileWriter _s16Writer, _floatWriter;
    readonly StreamWriter _csv;
    readonly WasapiRecorder _s16Recorder;
    readonly System.Threading.Timer _tick;
    readonly int _seconds;
    readonly object _gate = new();
    int _elapsed;
    bool _closed;
    // janela do segundo corrente + totais
    int _s16Peak; long _s16Clipped, _s16Near; float _f32Peak; long _f32Over, _f32Near;
    int _totalS16Peak; long _totalS16Clipped; float _totalF32Peak; long _totalF32Over;

    AudioDump(WasapiRecorder s16Recorder, string folder, int seconds)
    {
        _s16Recorder = s16Recorder;
        _seconds = seconds;
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        _s16Writer = new WaveFileWriter(Path.Combine(folder, $"audio-s16-{stamp}.wav"), new WaveFormat(AudioPump.SampleRate, 16, AudioPump.Channels));
        _floatWriter = new WaveFileWriter(Path.Combine(folder, $"audio-f32-{stamp}.wav"), WaveFormat.CreateIeeeFloatWaveFormat(AudioPump.SampleRate, AudioPump.Channels));
        _csv = new StreamWriter(Path.Combine(folder, $"audio-por-segundo-{stamp}.csv"), false, new UTF8Encoding(false));
        _csv.WriteLine("segundo,s16_pico_dbfs,s16_amostras_no_teto,s16_amostras_perto_do_teto,f32_pico_linear,f32_amostras_acima_de_1,f32_amostras_perto_de_1");
        _s16Recorder.DataAvailable += (buffer, _, _, _) => OnPcm16(buffer);
        _s16Recorder.StartRecording();
        _tick = new System.Threading.Timer(_ => Second(), null, 1000, 1000);
    }

    /// <summary>Liga o diagnóstico se STREEMU_AUDIO_DUMP estiver definida; nulo caso contrário (ou se a segunda captura não abrir).</summary>
    public static async Task<AudioDump?> StartAsync(int processId, ProcessLoopbackMode mode)
    {
        string? folder = Environment.GetEnvironmentVariable(EnvDir);
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try
        {
            Directory.CreateDirectory(folder);
            int seconds = int.TryParse(Environment.GetEnvironmentVariable(EnvSeconds), out var s) && s > 0 ? s : 120;
            var recorder = await new WasapiRecorderBuilder()
                .WithProcessLoopback((uint)processId, mode)
                .WithFormat(new WaveFormat(AudioPump.SampleRate, 16, AudioPump.Channels))
                .WithBufferLength(20)
                .BuildAsync();
            Log.Info($"[audio-dump] gravando {seconds} s em {folder} (float 32 como vai ao ffmpeg + s16 do mesmo processo, como era antes). Dispositivo de saída padrão: {DefaultOutputName()}.");
            return new AudioDump(recorder, folder, seconds);
        }
        catch (Exception e)
        {
            Log.Info("[audio-dump] não consegui ligar o diagnóstico: " + e.Message);
            return null;
        }
    }

    static string DefaultOutputName()
    {
        try { using var e = new MMDeviceEnumerator(); return e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).FriendlyName; }
        catch (Exception e) { return "(não consegui ler: " + e.Message + ")"; }
    }

    /// <summary>O PCM s16 da segunda captura (o que o app recebia antes de capturar em float).</summary>
    void OnPcm16(ReadOnlySpan<byte> buffer)
    {
        var samples = MemoryMarshal.Cast<byte, short>(buffer);
        int peak = 0; long clipped = 0, near = 0;
        foreach (short v in samples)
        {
            int a = v == short.MinValue ? 32768 : Math.Abs(v);
            if (a > peak) peak = a;
            if (a >= 32767) clipped++;
            if (a >= (int)(32768 * NearFullScale)) near++;
        }
        lock (_gate)
        {
            if (_closed) return;
            _s16Writer.Write(buffer);
            if (peak > _s16Peak) _s16Peak = peak;
            _s16Clipped += clipped; _s16Near += near;
        }
    }

    /// <summary>O PCM float 32 que o <see cref="AudioPump"/> manda ao ffmpeg.</summary>
    public void OnPipe(ReadOnlySpan<byte> buffer)
    {
        var samples = MemoryMarshal.Cast<byte, float>(buffer);
        float peak = 0; long over = 0, near = 0;
        foreach (float v in samples)
        {
            float a = Math.Abs(v);
            if (a > peak) peak = a;
            if (a > 1.0f) over++;
            if (a >= NearFullScale) near++;
        }
        lock (_gate)
        {
            if (_closed) return;
            _floatWriter.Write(buffer);
            if (peak > _f32Peak) _f32Peak = peak;
            _f32Over += over; _f32Near += near;
        }
    }

    static double Db(double linear) => linear <= 0 ? -120 : 20 * Math.Log10(linear);

    void Second()
    {
        int s16Peak; long s16Clipped, s16Near; float f32Peak; long f32Over, f32Near; int t;
        lock (_gate)
        {
            if (_closed) return;
            t = ++_elapsed;
            (s16Peak, s16Clipped, s16Near, f32Peak, f32Over, f32Near) = (_s16Peak, _s16Clipped, _s16Near, _f32Peak, _f32Over, _f32Near);
            (_s16Peak, _s16Clipped, _s16Near, _f32Peak, _f32Over, _f32Near) = (0, 0, 0, 0f, 0, 0);
            _totalS16Peak = Math.Max(_totalS16Peak, s16Peak); _totalS16Clipped += s16Clipped;
            _totalF32Peak = Math.Max(_totalF32Peak, f32Peak); _totalF32Over += f32Over;
            _csv.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{t},{Db(s16Peak / 32768.0):0.0},{s16Clipped},{s16Near},{f32Peak:0.0000},{f32Over},{f32Near}"));
            _csv.Flush();
        }
        // só fala quando algo tocou; o jogo calado não enche o log
        if (s16Peak > 0 || f32Peak > 0)
            Log.Info(string.Create(CultureInfo.InvariantCulture,
                $"[audio-dump] {t,3} s: s16 pico {Db(s16Peak / 32768.0):0.0} dBFS, {s16Clipped} no teto, {s16Near} perto | float pico {f32Peak:0.000} ({Db(f32Peak):0.0} dBFS), {f32Over} acima de 1,0"));
        if (t >= _seconds) Finish();
    }

    void Finish()
    {
        Dispose();
        Log.Info(string.Create(CultureInfo.InvariantCulture,
            $"[audio-dump] fim de {_seconds} s. s16: pico {Db(_totalS16Peak / 32768.0):0.0} dBFS, {_totalS16Clipped} amostras no teto | float: pico {_totalF32Peak:0.000}, {_totalF32Over} amostras acima de 1,0."));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _tick.Dispose();
            try { _s16Recorder.StopRecording(); } catch { }
            try { _s16Recorder.Dispose(); } catch { }
            _s16Writer.Dispose(); _floatWriter.Dispose(); _csv.Dispose();
        }
    }
}
