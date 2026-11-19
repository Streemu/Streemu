using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Streemu.Media;

/// <summary>Tempos de mídia, nunca uma promessa de que o conteúdo no fone está sincronizado.</summary>
sealed class TsAvMeter
{
    const long Wrap = 1L << 33;
    readonly object _gate = new();
    long _video = -1, _audio = -1, _videoAt, _audioAt, _epochAt;
    int _videoAdvances, _audioAdvances, _epochs, _max;

    public static long Difference(long a, long b) => ((a - b + Wrap / 2) & (Wrap - 1)) - Wrap / 2;

    public void BeginEpoch()
    {
        lock (_gate)
        {
            _video = _audio = -1; _videoAt = _audioAt = 0;
            _videoAdvances = _audioAdvances = 0;
            _epochAt = Stopwatch.GetTimestamp(); _epochs++;
        }
    }

    internal static bool TryPts(ReadOnlySpan<byte> packet, out int pid, out long pts)
    {
        pid = 0; pts = 0;
        if (packet.Length != 188 || packet[0] != 0x47 || (packet[1] & 0xC0) != 0x40 || (packet[3] & 0xC0) != 0) return false;
        pid = ((packet[1] & 0x1f) << 8) | packet[2];
        if (pid != 0x100 && pid != 0x101) return false;
        int afc = (packet[3] >> 4) & 3;
        if (afc is 0 or 2) return false;
        int p = 4 + (afc == 3 ? 1 + packet[4] : 0);
        if (p + 14 > 188 || packet[p] != 0 || packet[p + 1] != 0 || packet[p + 2] != 1 || (packet[p + 6] & 0xc0) != 0x80
            || (packet[p + 9] & 0xf0) is not (0x20 or 0x30) || (packet[p + 7] & 0x80) == 0
            || packet[p + 8] < 5 || (packet[p + 9] & 1) == 0 || (packet[p + 11] & 1) == 0 || (packet[p + 13] & 1) == 0) return false;
        pts = ((long)(packet[p + 9] >> 1 & 7) << 30) | ((long)packet[p + 10] << 22) | ((long)(packet[p + 11] >> 1) << 15)
            | ((long)packet[p + 12] << 7) | (long)(packet[p + 13] >> 1);
        return true;
    }

    // Uma trava por lote de leitura, sem alocação por pacote; não decodifica, copia ou modifica o vídeo.
    public void Observe(ReadOnlySpan<byte> packets)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            for (int i = 0; i + 188 <= packets.Length; i += 188)
            {
                if (!TryPts(packets.Slice(i, 188), out int pid, out long pts)) continue;
                if (pid == 0x100)
                {
                    if (_video < 0 || Difference(pts, _video) > 0) { _videoAdvances++; _videoAt = now; }
                    else if (Difference(pts, _video) < 0) { _videoAdvances = 0; _videoAt = 0; }
                    _video = pts;
                }
                else
                {
                    if (_audio < 0 || Difference(pts, _audio) > 0) { _audioAdvances++; _audioAt = now; }
                    else if (Difference(pts, _audio) < 0) { _audioAdvances = 0; _audioAt = 0; }
                    _audio = pts;
                }
            }
        }
    }

    public JsonObject Snapshot()
    {
        lock (_gate)
        {
            double? ageV = Age(_videoAt), ageA = Age(_audioAt);
            bool fresh = ageV < 1000 && ageA < 1000 && _videoAdvances >= 2 && _audioAdvances >= 2;
            int? delta = fresh ? (int)(Difference(_audio, _video) / 90) : null;
            if (delta is int d && Math.Abs(d) > Math.Abs(_max)) _max = d;
            return new JsonObject
            {
                ["epoca"] = _epochs, ["audio_menos_video_ms"] = delta, ["pior_ms"] = _max,
                ["idade_video_ms"] = ageV, ["idade_audio_ms"] = ageA,
                ["transicao"] = _epochAt == 0 || Stopwatch.GetElapsedTime(_epochAt).TotalSeconds < 6,
                ["medida"] = fresh ? "tempos_dos_pacotes" : "sem_medida_recente",
            };
        }
    }

    internal static double? Age(long at) => at == 0 ? null : Math.Round(Stopwatch.GetElapsedTime(at).TotalMilliseconds);
}

/// <summary>Consome a linha que o ffplay já escreve; no máximo uma medida/s, sem consultas ao Windows.</summary>
public sealed partial class ViewerSync
{
    readonly object _gate = new();
    readonly Func<long> _now;
    public ViewerSync() : this(Stopwatch.GetTimestamp) { }
    internal ViewerSync(Func<long> now) => _now = now;
    long _sampleAt, _advanceAt;
    double _clock = double.NaN;
    int? _delta;
    int _audioKb, _videoKb, _drops, _max, _bad, _good, _incidents, _recoveries;
    bool _incident;

    [GeneratedRegex(@"(?<clock>[-+\d.]+)\s+A-V:\s*(?<delta>[-+\d.]+).*?fd=\s*(?<drop>\d+).*?aq=\s*(?<aq>\d+)KB.*?vq=\s*(?<vq>\d+)KB", RegexOptions.CultureInvariant, 25)]
    private static partial Regex StatusRx();

    public void Reset()
    {
        lock (_gate)
        {
            _sampleAt = _advanceAt = 0; _clock = double.NaN; _delta = null;
            _audioKb = _videoKb = _drops = _max = _bad = _good = _incidents = _recoveries = 0; _incident = false;
        }
    }

    public void Observe(string line)
    {
        if (!line.Contains("A-V:", StringComparison.Ordinal)) return;
        long now = _now();
        lock (_gate)
        {
            if (_sampleAt != 0 && Stopwatch.GetElapsedTime(_sampleAt, now).TotalSeconds < 1) return;
            if (!TryStatus(line, out double clock, out int delta, out int aq, out int vq, out int drops)) return;
            bool continuous = _sampleAt != 0 && Stopwatch.GetElapsedTime(_sampleAt, now).TotalSeconds < 2.5;
            bool advancing = double.IsNaN(_clock) || clock > _clock + 0.05;
            if (advancing) _advanceAt = now;
            _sampleAt = now; _clock = clock; _delta = delta; _audioKb = aq; _videoKb = vq; _drops = drops;
            if (Math.Abs(delta) > Math.Abs(_max)) _max = delta;
            if (!continuous || !advancing) { _bad = _good = 0; return; }
            if (Math.Abs(delta) >= 600)
            {
                _good = 0;
                if (++_bad >= 5 && !_incident) { _incident = true; _incidents++; }
            }
            else
            {
                _bad = 0;
                if (Math.Abs(delta) > 120) _good = 0;
                else if (++_good >= 3 && _incident) { _incident = false; _recoveries++; }
            }
        }
    }

    internal static bool TryStatus(string line, out double clock, out int delta, out int aq, out int vq, out int drops)
    {
        clock = 0; delta = aq = vq = drops = 0;
        Match m;
        try { m = StatusRx().Match(line); } catch (RegexMatchTimeoutException) { return false; }
        if (!m.Success || !double.TryParse(m.Groups["clock"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out clock)
            || !double.TryParse(m.Groups["delta"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            || !double.IsFinite(clock) || !double.IsFinite(d) || Math.Abs(d) > 10) return false;
        delta = (int)Math.Round(d * 1000);
        return int.TryParse(m.Groups["aq"].Value, out aq) && int.TryParse(m.Groups["vq"].Value, out vq) && int.TryParse(m.Groups["drop"].Value, out drops);
    }

    public JsonObject Snapshot()
    {
        lock (_gate)
        {
            long now = _now();
            double? age = _sampleAt == 0 ? null : Math.Round(Stopwatch.GetElapsedTime(_sampleAt, now).TotalMilliseconds);
            bool recent = age < 3000, advancing = _advanceAt != 0 && Stopwatch.GetElapsedTime(_advanceAt, now).TotalMilliseconds < 2000;
            return new JsonObject
            {
                ["medida"] = !recent ? "sem_medida_recente" : !advancing ? "pausado_ou_sem_avanco" : "relogios_do_player",
                ["audio_menos_video_ms"] = recent && advancing ? _delta : null, ["idade_ms"] = age,
                ["pior_ms"] = _max, ["fila_audio_kb"] = _audioKb, ["fila_video_kb"] = _videoKb, ["quadros_descartados"] = _drops,
                ["incidentes_persistentes"] = _incidents, ["recuperacoes_observadas"] = _recoveries,
            };
        }
    }
}
