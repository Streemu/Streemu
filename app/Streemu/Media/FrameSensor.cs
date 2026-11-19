using System.Diagnostics;
using System.Text.RegularExpressions;
using Streemu.Util;

namespace Streemu.Media;

/// <summary>
/// Conta quantos quadros NOVOS a tela produz, por segundo, com um ffmpeg à parte que só captura (sem encoder, sem rede, sem baixar a imagem: custo desprezível).
/// Motivo (2026-11-08, bug de pausar e voltar vídeo no mpv/Jellyfin): a captura que vai para o encoder passou a repetir quadros no próprio ddagrab
/// (<c>dup_frames=1</c>) para a saída nunca ficar muda; com isso os contadores do ffmpeg principal (<c>dup=</c>) não enxergam mais quadros repetidos
/// e o vigia da captura ficaria cego. Este sensor é uma sessão de captura de longa duração, como a principal, então trava junto com ela (o caso do Cyberpunk, 0.1.9),
/// e o diagnóstico continua comparando com uma captura NOVA (<see cref="Capture.Probe"/>).
/// Uso: <c>using var sensor = FrameSensor.Start(cap, unique =&gt; Log.Info($"{unique} quadros novos/s"));</c>
/// </summary>
public sealed partial class FrameSensor : IDisposable
{
    readonly Process _process;
    readonly Action<int> _onSecond;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    long _lastFrame = -1;
    double _lastAt;
    volatile bool _disposed;

    [GeneratedRegex(@"frame=\s*(\d+)")] private static partial Regex FrameRx();

    FrameSensor(CaptureSpec cap, Action<int> onSecond)
    {
        _onSecond = onSecond;
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-stats", "-stats_period", "1" }; // "error": o muxer nulo reclama de dts repetido a cada quadro e isso não importa aqui
        var outputs = new List<string>();
        cap.ForCounting().Args(null, args, outputs);
        args.AddRange(outputs);
        args.AddRange(new[] { "-fps_mode", "passthrough", "-c:v", "wrapped_avframe", "-f", "null", "-" });
        _process = Tools.Start(Tools.Ffmpeg, args, "sensor", redirectStdin: false, onStderr: Feed);
    }

    /// <summary>Abre o sensor para esta captura. Nulo se o ffmpeg não abrir (o vigia segue com as estatísticas do ffmpeg principal).</summary>
    public static FrameSensor? Start(CaptureSpec cap, Action<int> onSecond)
    {
        try { return new FrameSensor(cap, onSecond); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Info("Não consegui abrir o sensor de quadros novos (o vigia usa as estatísticas do ffmpeg principal): " + e.Message);
            return null;
        }
    }

    void Feed(string line)
    {
        if (_disposed || !line.StartsWith("frame=", StringComparison.Ordinal)) return;
        var m = FrameRx().Match(line);
        if (!m.Success) return;
        long frame = long.Parse(m.Groups[1].Value);
        double now = _clock.Elapsed.TotalSeconds;
        long previous = _lastFrame; double before = _lastAt;
        _lastFrame = frame; _lastAt = now;
        if (previous < 0) return;
        _onSecond((int)Math.Round((frame - previous) / Math.Max(0.2, now - before)));
    }

    /// <summary>O ffmpeg do sensor fechou (caiu ou foi morto)? Ninguém reabria: o vigia e o quadros.csv ficavam sem segundos até o fim da captura.</summary>
    public bool HasExited { get { try { return _process.HasExited; } catch { return true; } } }

    /// <summary>Só para teste: mata o ffmpeg do sensor sem marcar como descartado (simula o sensor caindo sozinho).</summary>
    internal void Kill() => Tools.KillQuietly(_process);

    public void Dispose()
    {
        _disposed = true;
        Tools.KillQuietly(_process);
    }
}
