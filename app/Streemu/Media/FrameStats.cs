using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Streemu.Media;

/// <summary>
/// Lê as linhas de progresso do ffmpeg ("frame= ... dup= ... time=", uma por segundo) e mede, pelo RELÓGIO do app,
/// quantos quadros novos a captura entregou em cada intervalo e quantos o encoder soltou.
/// Motivo (teste 0.1.8): "5 quadros novos/s de 104" num intervalo de 1 s quer dizer que o ffmpeg ficou parado e
/// depois despejou em rajada; a média sozinha esconde isso, então guardamos todos os intervalos.
/// </summary>
public sealed partial class FrameStats
{
    readonly int _fps;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly object _gate = new();
    readonly List<double> _newPerSec = new(), _outPerSec = new();
    long _lastFrame = -1, _lastDup;
    double _lastT, _firstMedia = -1, _firstMediaAt, _lastMedia, _lastMediaAt;
    long _baseFrames, _baseDups, _totalFrames, _totalDups;
    int _bursts, _stalledSeconds, _dupWarnings;
    double? _sensorNew;   // quadros novos/s medidos pelo FrameSensor (nulo = vale o dup= do ffmpeg)

    public FrameStats(int fps) => _fps = fps;

    public TimeSpan Elapsed => _clock.Elapsed;

    [GeneratedRegex(@"frame=\s*(\d+)")] private static partial Regex FrameRx();
    [GeneratedRegex(@"dup=\s*(\d+)")] private static partial Regex DupRx();
    [GeneratedRegex(@"time=\s*(-?)(\d+):(\d+):([\d.]+)")] private static partial Regex TimeRx();

    /// <summary>Um ffmpeg novo começou (depois que o espectador sai o ffmpeg é reiniciado e o contador zera).</summary>
    public void NewProcess()
    {
        lock (_gate)
        {
            _baseFrames = _totalFrames; _baseDups = _totalDups;
            _lastFrame = -1; _lastDup = 0;
            _firstMedia = -1;
            _sensorNew = null;
        }
    }

    /// <summary>O sensor mediu quantos quadros novos a tela produziu no último segundo; vale no lugar do <c>dup=</c> do ffmpeg (que não vê os quadros que o ddagrab repete).</summary>
    public void SetNewRate(double framesPerSecond) { lock (_gate) _sensorNew = framesPerSecond; }

    /// <summary>Processa uma linha do stderr. Devolve (novos/s, saindo/s) do intervalo, ou nulo se não era progresso.</summary>
    public (double New, double Out)? Feed(string line)
    {
        if (line.Contains("frames duplicated", StringComparison.Ordinal)) { lock (_gate) _dupWarnings++; return null; }
        if (!line.StartsWith("frame=", StringComparison.Ordinal)) return null;
        var fm = FrameRx().Match(line);
        if (!fm.Success) return null;
        long f = long.Parse(fm.Groups[1].Value);
        var dm = DupRx().Match(line);
        long d = dm.Success ? long.Parse(dm.Groups[1].Value) : 0;
        double now = _clock.Elapsed.TotalSeconds;
        var tm = TimeRx().Match(line);
        lock (_gate)
        {
            if (tm.Success && tm.Groups[1].Value != "-")
            {
                double media = int.Parse(tm.Groups[2].Value) * 3600 + int.Parse(tm.Groups[3].Value) * 60 + double.Parse(tm.Groups[4].Value, CultureInfo.InvariantCulture);
                if (_firstMedia < 0) { _firstMedia = media; _firstMediaAt = now; }
                _lastMedia = media; _lastMediaAt = now;
            }
            _totalFrames = _baseFrames + f; _totalDups = _baseDups + d;
            (double, double)? result = null;
            if (_lastFrame >= 0)
            {
                double dt = Math.Max(0.2, now - _lastT);
                long encoded = f - _lastFrame, dup = d - _lastDup;
                double newRate = _sensorNew ?? Math.Max(0, encoded - dup) / dt, outRate = encoded / dt;
                _newPerSec.Add(newRate);
                _outPerSec.Add(outRate);
                if (outRate > _fps + 10) _bursts++;
                if (encoded == 0) _stalledSeconds += (int)Math.Round(dt);
                result = (newRate, outRate);
            }
            _lastFrame = f; _lastDup = d; _lastT = now;
            return result;
        }
    }

    public int Intervals { get { lock (_gate) return _newPerSec.Count; } }

    public (double Min, double Median, double Avg, double Max) NewFrames()
    {
        lock (_gate) return Describe(_newPerSec);
    }

    static (double, double, double, double) Describe(List<double> v)
    {
        if (v.Count == 0) return (0, 0, 0, 0);
        var s = v.OrderBy(x => x).ToList();
        double median = s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
        return (s[0], median, s.Average(), s[^1]);
    }

    public int Bursts { get { lock (_gate) return _bursts; } }

    /// <summary>Resumo para o relatório e para o capture-test.txt.</summary>
    public JsonObject Summary()
    {
        lock (_gate)
        {
            var (min, med, avg, max) = Describe(_newPerSec);
            var (_, outMed, _, outMax) = Describe(_outPerSec);
            var o = new JsonObject
            {
                ["duracao_s"] = Math.Round(_clock.Elapsed.TotalSeconds, 1),
                ["intervalos"] = _newPerSec.Count,
                ["quadros_novos_s_min"] = Math.Round(min),
                ["quadros_novos_s_mediana"] = Math.Round(med),
                ["quadros_novos_s_media"] = Math.Round(avg, 1),
                ["quadros_novos_s_max"] = Math.Round(max),
                ["quadros_saindo_s_mediana"] = Math.Round(outMed),
                ["quadros_saindo_s_max"] = Math.Round(outMax),
                ["rajadas"] = _bursts,
                ["segundos_parado"] = _stalledSeconds,
                ["quadros_duplicados"] = _totalDups,
                ["avisos_frames_duplicated"] = _dupWarnings,
            };
            // tempo de mídia andando mais devagar que o relógio = o ffmpeg está atrasando (fila crescendo)
            double wall = _lastMediaAt - _firstMediaAt;
            if (_firstMedia >= 0 && wall > 2) o["tempo_midia_por_relogio"] = Math.Round((_lastMedia - _firstMedia) / wall, 2);
            return o;
        }
    }

    public string OneLine()
    {
        var (min, med, _, max) = NewFrames();
        return $"quadros novos/s mín {min:0} / mediana {med:0} / máx {max:0}, {Bursts} rajada(s)";
    }
}
