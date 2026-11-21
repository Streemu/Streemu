using System.Text.Json.Nodes;

namespace Streemu.Telemetria;

/// <summary>
/// Buffer circular pequeno com a carga do PC nos últimos segundos, para o <c>midia_falha</c> dizer quão carregado o PC estava quando falhou.
/// Não mede nada: só guarda valores que <c>SerieQuadros</c> (host, 1 s) e a amostra de sincronia (viewer, 2 s) já calculam.
/// Leitura ausente (nulo) não entra: sem leitura nunca vira 0%. Sob carga é pista, não causa. Seguro entre threads.
/// </summary>
public sealed class CargaRecente
{
    public const int JanelaHostS = 10, JanelaViewerS = 12;
    const int MaxPorMetrica = 16; // 10 s a 1 amostra por segundo, com folga

    public static readonly CargaRecente Host = new(JanelaHostS), Viewer = new(JanelaViewerS);
    /// <summary>O buffer do lado ("host" ou "viewer"); outro valor → nulo.</summary>
    public static CargaRecente? Do(string lado) => lado == CaminhoMidia.Host ? Host : lado == CaminhoMidia.Viewer ? Viewer : null;

    readonly int _janelaS;
    readonly Func<long> _agoraMs;
    readonly object _g = new();
    readonly Dictionary<string, (long Ms, int V)[]> _buf = new();
    readonly Dictionary<string, int> _n = new(), _cabeca = new();

    public CargaRecente(int janelaS, Func<long>? agoraMs = null) { _janelaS = janelaS; _agoraMs = agoraMs ?? (() => Environment.TickCount64); }

    public int JanelaS => _janelaS;

    /// <summary>Guarda uma leitura; <paramref name="valor"/> nulo é ignorado (sem leitura).</summary>
    public void Registrar(string metrica, int? valor)
    {
        if (valor is not { } v) return;
        long agora = _agoraMs();
        lock (_g)
        {
            if (!_buf.TryGetValue(metrica, out var b)) { _buf[metrica] = b = new (long, int)[MaxPorMetrica]; _n[metrica] = 0; _cabeca[metrica] = 0; }
            int h = _cabeca[metrica];
            b[h] = (agora, v);
            _cabeca[metrica] = (h + 1) % MaxPorMetrica;
            if (_n[metrica] < MaxPorMetrica) _n[metrica]++;
        }
    }

    /// <summary>
    /// <c>{ janela_s, metricas: { nome: { mediana, max, n_amostras } } }</c> com o que está dentro da janela; nulo se não há nenhuma leitura.
    /// Métrica sem leitura na janela fica fora.
    /// </summary>
    public JsonObject? Resumo()
    {
        long agora = _agoraMs(), corte = agora - _janelaS * 1000L;
        var metricas = new JsonObject();
        lock (_g)
        {
            foreach (var (nome, b) in _buf)
            {
                var vals = new List<int>();
                for (int i = 0; i < _n[nome]; i++) if (b[i].Ms >= corte && b[i].Ms <= agora) vals.Add(b[i].V);
                if (vals.Count == 0) continue;
                vals.Sort();
                int m = vals.Count / 2;
                double mediana = vals.Count % 2 == 1 ? vals[m] : (vals[m - 1] + vals[m]) / 2.0;
                metricas[nome] = new JsonObject { ["mediana"] = (int)Math.Round(mediana, MidpointRounding.AwayFromZero), ["max"] = vals[^1], ["n_amostras"] = vals.Count };
            }
        }
        return metricas.Count == 0 ? null : new JsonObject { ["janela_s"] = _janelaS, ["metricas"] = metricas };
    }
}
