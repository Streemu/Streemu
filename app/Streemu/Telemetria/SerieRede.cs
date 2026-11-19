using System.Text.Json.Nodes;

namespace Streemu.Telemetria;

/// <summary>O que se lê de uma ligação num instante (só números e o caminho). Existe para o cálculo da série não depender de um túnel de verdade.</summary>
public readonly record struct LeituraRede(string Estado, string Caminho, double RttMs, long TxBytes, long RxBytes, long Recebidos, double PerdaTotalPct, bool AssistoEla);

/// <summary>
/// Rede de cada ligação (cada par transmissor-espectador), uma linha a cada 5 s em <c>rede.ndjson</c>, só enquanto a ligação está conectada:
/// estado, caminho (direto, relay do amigo ou meu relay), RTT, bytes e pacotes no intervalo e perda no intervalo. Responde "a live do X para o Y
/// ficou ruim por causa da rede dele, da minha ou do caminho?" sem depender de o relatório pegar o momento. O <c>amigo</c> é o apelido que o
/// relatório e a sala já usam; nada de IP. Quem chama é o timer da interface e a conta é só aritmética sobre contadores que já existem:
/// sem socket, sem WMI e sem disco (o <see cref="SessaoLocal.Acrescentar"/> só enfileira).
/// </summary>
public sealed class SerieRede
{
    public const string Arquivo = "rede.ndjson";
    public static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(5);

    sealed class Anterior { public long Tx, Rx, Recebidos, Perdidos; public long AtMs; public string? Estado, Caminho; }

    readonly SessaoLocal _s;
    readonly Dictionary<string, Anterior> _ant = new();
    long _ultimaMs;
    bool _primeira = true;

    public SerieRede(SessaoLocal s) { _s = s; }

    /// <summary>Quantas ligações já têm linha de base (testes).</summary>
    public int Ligacoes => _ant.Count;

    /// <summary>
    /// Registra uma amostra por ligação se já passou o intervalo. <paramref name="leituras"/> só é chamado quando vai amostrar (é onde se
    /// tocam os túneis). Devolve quantas linhas gravou.
    /// </summary>
    public int Amostrar(long agoraMs, Func<IEnumerable<(string Id, string Live, string Amigo, string Papel, LeituraRede Leitura)>> leituras)
    {
        if (!_primeira && agoraMs - _ultimaMs < Intervalo.TotalMilliseconds - 200) return 0;
        _primeira = false; _ultimaMs = agoraMs;
        int n = 0;
        var vistas = new HashSet<string>();
        foreach (var (id, live, amigo, papel, l) in leituras())
        {
            vistas.Add(id);
            bool conectado = l.Estado == "Connected";
            long perdidos = l.PerdaTotalPct >= 100 ? 0 : (long)Math.Round(l.Recebidos * l.PerdaTotalPct / (100 - l.PerdaTotalPct));
            if (!_ant.TryGetValue(id, out var a)) { a = new Anterior { Tx = l.TxBytes, Rx = l.RxBytes, Recebidos = l.Recebidos, Perdidos = perdidos, AtMs = agoraMs }; _ant[id] = a; continue; } // primeira vez: só a linha de base, sem inventar taxa
            if (!conectado) { a.Tx = l.TxBytes; a.Rx = l.RxBytes; a.Recebidos = l.Recebidos; a.Perdidos = perdidos; a.AtMs = agoraMs; continue; }
            double seg = Math.Max(0.5, (agoraMs - a.AtMs) / 1000.0);
            long dRec = Math.Max(0, l.Recebidos - a.Recebidos), dPerd = Math.Max(0, perdidos - a.Perdidos);
            var o = new JsonObject
            {
                ["hora_utc"] = DateTime.UtcNow.ToString("O"), ["transmissao"] = string.IsNullOrEmpty(live) ? null : live, ["ligacao"] = id, ["amigo"] = amigo, ["papel"] = papel,
                ["caminho"] = l.Caminho, ["rtt_ms"] = l.RttMs < 0 ? null : Math.Round(l.RttMs, 1),
                ["intervalo_s"] = Math.Round(seg, 1),
                ["enviado_kbps"] = Math.Round(Math.Max(0, l.TxBytes - a.Tx) * 8 / 1000.0 / seg, 0),
                ["recebido_kbps"] = Math.Round(Math.Max(0, l.RxBytes - a.Rx) * 8 / 1000.0 / seg, 0),
            };
            if (l.AssistoEla)
            {
                o["pacotes_recebidos"] = dRec; o["pacotes_perdidos"] = dPerd;
                o["perda_pct"] = dRec + dPerd == 0 ? 0 : Math.Round(100.0 * dPerd / (dRec + dPerd), 2);
            }
            _s.Acrescentar(Arquivo, o.ToJsonString());
            n++;
            a.Tx = l.TxBytes; a.Rx = l.RxBytes; a.Recebidos = l.Recebidos; a.Perdidos = perdidos; a.AtMs = agoraMs;
        }
        foreach (var k in _ant.Keys.Where(k => !vistas.Contains(k)).ToList()) _ant.Remove(k); // ligação que acabou
        return n;
    }

    /// <summary>
    /// Nome estável do caminho que o túnel está usando. <paramref name="pelaOperadora"/>: o destino direto é o endereço do amigo dentro da operadora (CGNAT), o
    /// que a descoberta da abertura (MainForm.Abertura.cs) existe para conseguir; em campo é o que diz se ela funcionou. Continua sendo caminho direto (sem relay).
    /// </summary>
    public static string NomeDoCaminho(int? tipo, bool pelaOperadora = false) => tipo switch { 0 => pelaOperadora ? "direto_pela_operadora" : "direto", 1 => "relay_do_amigo", 2 => "meu_relay", _ => "sem_caminho" };
}
