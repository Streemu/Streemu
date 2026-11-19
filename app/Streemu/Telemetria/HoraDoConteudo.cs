using System.Text.Json.Nodes;

namespace Streemu.Telemetria;

/// <summary>
/// Até que hora vai o CONTEÚDO de um pedaço de arquivo (a hora da última linha), em vez da hora em que o pedaço foi enviado. Com a internet
/// caída por uma hora, o backlog sobe de uma vez: pela hora do envio o servidor diria "o log cobre até agora", e o log da hora que faltou
/// pareceria "sem erro". Só o log.txt (hora local no começo de cada linha) e o eventos.ndjson (campo "hora") têm hora própria; os outros
/// arquivos ficam sem (nulo), nunca com a hora do envio.
/// </summary>
public static class HoraDoConteudo
{
    static readonly TimeSpan Meio = TimeSpan.FromHours(12);

    /// <summary>Hora (UTC, ms) da primeira e da última linha com hora do pedaço, ou nulos.</summary>
    /// <param name="refUtcMs">A última hora de conteúdo já confirmada desse arquivo (ou o início da sessão): resolve a virada do dia, porque a linha só traz HH:mm:ss.</param>
    /// <param name="doZero">O pedaço começa no byte 0: o log pode ter começado um pouco antes do relógio da sessão, até do dia anterior.</param>
    public static (long? Ini, long? Fim) De(string nome, string texto, long refUtcMs, bool doZero)
    {
        try
        {
            if (nome == SessaoLocal.Log) return Log(texto, refUtcMs, doZero);
            if (nome == SessaoLocal.Eventos) return Eventos(texto);
        }
        catch { }
        return (null, null);
    }

    static (long?, long?) Log(string texto, long refUtcMs, bool doZero)
    {
        var tz = TimeZoneInfo.Local;
        var prev = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(refUtcMs).UtcDateTime, tz);
        var dia = prev.Date;
        long? ini = null, fim = null;
        bool primeira = true;
        foreach (var linha in texto.Split('\n'))
        {
            if (!TentarHora(linha, out var t)) continue;
            var cand = dia + t;
            if (cand < prev - Meio) { dia = dia.AddDays(1); cand = dia + t; } // virou o dia
            else if (primeira && doZero && cand - prev > Meio) { dia = dia.AddDays(-1); cand = dia + t; } // o log começou antes da meia-noite, a sessão depois
            prev = cand; primeira = false;
            long utc = new DateTimeOffset(cand, tz.GetUtcOffset(cand)).ToUnixTimeMilliseconds();
            ini ??= utc; fim = utc;
        }
        return (ini, fim);
    }

    /// <summary>"HH:mm:ss.fff algo", com ou sem BOM no começo.</summary>
    static bool TentarHora(string l, out TimeSpan t)
    {
        t = default;
        if (l.Length > 0 && l[0] == '\uFEFF') l = l[1..];
        if (l.Length < 12 || l[2] != ':' || l[5] != ':' || l[8] != '.') return false;
        for (int i = 0; i < 12; i++) if (i != 2 && i != 5 && i != 8 && !char.IsAsciiDigit(l[i])) return false;
        int h = (l[0] - '0') * 10 + l[1] - '0', m = (l[3] - '0') * 10 + l[4] - '0', s = (l[6] - '0') * 10 + l[7] - '0', ms = (l[9] - '0') * 100 + (l[10] - '0') * 10 + l[11] - '0';
        if (h > 23 || m > 59 || s > 59) return false;
        t = new TimeSpan(0, h, m, s, ms);
        return true;
    }

    static (long?, long?) Eventos(string texto)
    {
        long? ini = null, fim = null;
        foreach (var linha in texto.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(linha)) continue;
            long? h = null;
            try { if (JsonNode.Parse(linha) is JsonObject o && o["hora"] is JsonValue v && v.TryGetValue<long>(out var x) && x > 0) h = x; } catch { }
            if (h == null) continue;
            ini ??= h; fim = h;
        }
        return (ini, fim);
    }

    public static string? Iso(long? ms) => ms is long v ? DateTimeOffset.FromUnixTimeMilliseconds(v).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") : null;
}
