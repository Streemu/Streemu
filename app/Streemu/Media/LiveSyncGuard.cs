using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Streemu.Media;

/// <summary>Proteção conservadora da ORIGEM. Não decide a partir de um aviso, de ausência de dado nem de um único espectador.</summary>
sealed class LiveSyncGuard
{
    long _lastSample, _badSince, _lastAttempt;
    int _sign, _good, _requests, _verified;
    bool _checking;
    readonly Queue<long> _attempts = new();

    public bool Observe(int? deltaMs, bool eligible, long now)
    {
        if (!eligible || deltaMs == null || Math.Abs((long)deltaMs.Value) >= 10_000)
        {
            _badSince = 0; _sign = _good = 0; _lastSample = now; return false;
        }
        if (_lastSample != 0 && Stopwatch.GetElapsedTime(_lastSample, now).TotalMilliseconds < 900) return false;
        if (_lastSample != 0 && Stopwatch.GetElapsedTime(_lastSample, now).TotalSeconds > 2.5) _badSince = 0;
        _lastSample = now;
        int delta = deltaMs.Value;
        if (Math.Abs(delta) <= 120)
        {
            _badSince = 0; _sign = 0;
            if (++_good >= 3 && _checking) { _verified++; _checking = false; }
            return false;
        }
        _good = 0;
        if (Math.Abs(delta) < 700) { _badSince = 0; return false; }
        int sign = Math.Sign(delta);
        if (_badSince == 0 || sign != _sign) { _badSince = now; _sign = sign; return false; }
        if (Stopwatch.GetElapsedTime(_badSince, now).TotalSeconds < 5) return false;
        if (_lastAttempt != 0 && Stopwatch.GetElapsedTime(_lastAttempt, now).TotalSeconds < 60) return false;
        while (_attempts.TryPeek(out long at) && Stopwatch.GetElapsedTime(at, now).TotalMinutes >= 10) _attempts.Dequeue();
        if (_attempts.Count >= 2) return false;
        _attempts.Enqueue(now); _lastAttempt = now; _badSince = 0; _requests++; _checking = true;
        return true;
    }

    public JsonObject Snapshot() => new()
    {
        ["pedidos_recuperacao"] = _requests, ["recuperacoes_verificadas"] = _verified,
        ["verificando_resultado"] = _checking, ["limite"] = "2 em 10 min, intervalo minimo 60 s",
    };
}
