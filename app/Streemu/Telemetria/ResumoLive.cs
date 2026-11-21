using System.Text.Json.Nodes;

namespace Streemu.Telemetria;

/// <summary>
/// Acumulador por minuto da carga e da sincronia de uma live, para o evento <c>live_resumo</c> (a linha do tempo do host e dos viewers no painel).
/// Não mede nada: só agrega valores que já passam por <c>SerieQuadros.Segundo</c> (host) e pela amostra de sincronia (viewer).
/// Sem leitura = campo ausente, nunca 0. Guarda no máximo <see cref="MaxMinutos"/> minutos por ligação; o que passar disso só conta em <c>minutos_descartados</c>.
/// Seguro entre threads.
/// </summary>
public sealed class ResumoLive
{
    public const int MaxMinutos = 180;
    public const string QuadrosNovos = "quadros_novos_s", Espectadores = "espectadores", Descartes = "descartes", AvMs = "av_ms";

    public static readonly ResumoLive Host = new(CaminhoMidia.Host), Viewer = new(CaminhoMidia.Viewer);
    public static ResumoLive? Do(string lado) => lado == CaminhoMidia.Host ? Host : lado == CaminhoMidia.Viewer ? Viewer : null;

    sealed class Minuto { public readonly Dictionary<string, List<int>> V = new(); public int Segundos; }
    sealed class Ligacao { public long InicioMs; public DateTime InicioUtc; public readonly SortedDictionary<int, Minuto> Min = new(); public readonly HashSet<int> Descartados = new(); }

    readonly string _lado;
    readonly Func<long> _agoraMs;
    readonly Func<DateTime> _agoraUtc;
    readonly object _g = new();
    readonly Dictionary<string, Ligacao> _lig = new();

    public ResumoLive(string lado, Func<long>? agoraMs = null, Func<DateTime>? agoraUtc = null)
    { _lado = lado; _agoraMs = agoraMs ?? (() => Environment.TickCount64); _agoraUtc = agoraUtc ?? (() => DateTime.UtcNow); }

    Ligacao Pegar(string lig)
    {
        if (!_lig.TryGetValue(lig, out var l)) _lig[lig] = l = new Ligacao { InicioMs = _agoraMs(), InicioUtc = _agoraUtc() };
        return l;
    }

    /// <summary>Marca o começo da live (host: ligação ""; viewer: o id da ligação). Recomeça se já havia.</summary>
    public void Iniciar(string lig = "") { lock (_g) _lig.Remove(lig); lock (_g) Pegar(lig); }

    /// <summary>Soma um segundo com amostra no minuto atual.</summary>
    public void Marcar(string lig = "") { lock (_g) { var m = Minuto_(Pegar(lig)); if (m != null) m.Segundos++; } }

    /// <summary>Guarda uma leitura no minuto atual; nulo é ignorado (sem leitura).</summary>
    public void Registrar(string metrica, int? valor, string lig = "")
    {
        if (valor is not { } v) return;
        lock (_g)
        {
            var m = Minuto_(Pegar(lig));
            if (m == null) return;
            if (!m.V.TryGetValue(metrica, out var l)) m.V[metrica] = l = new List<int>();
            l.Add(v);
        }
    }

    // minuto atual; além do limite só conta como descartado (nulo)
    Minuto? Minuto_(Ligacao l)
    {
        int idx = (int)Math.Max(0, (_agoraMs() - l.InicioMs) / 60000);
        if (idx >= MaxMinutos) { l.Descartados.Add(idx); return null; }
        if (!l.Min.TryGetValue(idx, out var m)) l.Min[idx] = m = new Minuto();
        return m;
    }

    /// <summary>Número do JSON como inteiro, qualquer que seja o tipo gravado (int, long, double); ausente ou não numérico = nulo.</summary>
    public static int? Inteiro(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        if (v.TryGetValue<double>(out var d) && !double.IsNaN(d)) return (int)Math.Round(d);
        return null;
    }

    static int Mediana(List<int> v)
    {
        var s = v.OrderBy(x => x).ToList(); int m = s.Count / 2;
        double d = s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2.0;
        return (int)Math.Round(d, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Fecha a ligação e devolve o evento <c>live_resumo</c> (sem <c>lives</c>, que o chamador junta); nulo se não houve amostra nenhuma.
    /// Todos os números são inteiros.
    /// </summary>
    public JsonObject? Fechar(string lig = "")
    {
        Ligacao? l;
        lock (_g) { if (!_lig.Remove(lig, out l)) return null; }
        if (l.Min.Count == 0 && l.Descartados.Count == 0) return null;
        var minutos = new JsonArray();
        foreach (var (idx, m) in l.Min)
        {
            var o = new JsonObject { ["m"] = idx, ["s"] = m.Segundos };
            var metricas = new JsonObject();
            foreach (var (nome, v) in m.V)
            {
                switch (nome)
                {
                    case QuadrosNovos: o["quadros_novos_min"] = v.Min(); o["quadros_novos_mediana"] = Mediana(v); break;
                    case Espectadores: o["espectadores_max"] = v.Max(); break;
                    case Descartes: o["descartes"] = v.Max(); break;
                    case AvMs: o["av_pior_ms"] = v.Select(Math.Abs).Max(); break;
                    default: metricas[nome] = new JsonObject { ["mediana"] = Mediana(v), ["max"] = v.Max() }; break;
                }
            }
            if (metricas.Count > 0) o["metricas"] = metricas;
            minutos.Add(o);
        }
        var r = new JsonObject
        {
            ["lado"] = _lado, ["inicio_utc"] = l.InicioUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"), ["fim_utc"] = _agoraUtc().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["minutos"] = minutos, ["minutos_descartados"] = l.Descartados.Count,
        };
        try { if (CaminhoMidia.Lives?.Invoke() is { } lives) r["lives"] = lives; } catch { }
        if (lig != "") r["ligacao"] = lig;
        return r;
    }
}
