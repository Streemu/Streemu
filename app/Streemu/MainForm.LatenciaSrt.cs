using Streemu.Media;
using Streemu.Net;
using Streemu.Util;
using System.Text.Json.Nodes;

namespace Streemu;

public sealed partial class MainForm
{
    /// <summary>Quem assiste escolhe a latência do próprio SRT (o transmissor segue com a do combo, 80 ms na Automática): ver <see cref="LatenciaSrt"/>.</summary>
    bool LatenciaAutomatica => _latency.SelectedIndex == 0;

    readonly Dictionary<string, EscaladaLatencia> _escaladaLatencia = new();

    /// <summary>Latência SRT do player que vai abrir para esta ligação: a do combo se a pessoa escolheu uma, senão a calculada pela ida e volta medida no túnel.</summary>
    int LatenciaDoEspectador(PeerLink link)
    {
        double rtt = link.Tunnel.RttMs;
        bool relay = link.Tunnel.Target is { Kind: not CandKind.Direct };
        string caminho = CaminhoDaLigacao(link);
        int ms; string motivo;
        if (LatenciaAutomatica) { ms = LatenciaSrt.Calcular(rtt, relay, link.Tunnel.ConnectMs); motivo = "inicio"; }
        else { ms = LatencyMs; motivo = "manual"; }
        var esc = new EscaladaLatencia(); esc.Iniciar(ms);
        if (LatenciaAutomatica) _escaladaLatencia[link.SessionId] = esc;
        string quando = rtt > 0 ? $"ida e volta {rtt:0} ms" : $"ida e volta ainda não medida, presumi {LatenciaSrt.RttPresumido(link.Tunnel.ConnectMs):0} ms";
        Log.Info(motivo == "manual"
            ? $"Latência do SRT: {ms} ms (a que você escolheu em Latência SRT)."
            : $"Latência do SRT: {ms} ms ({quando}, {(relay ? "pelo relay" : "direto")}).");
        EventoLatenciaSrt(link, ms, rtt, caminho, motivo);
        return ms;
    }

    void EventoLatenciaSrt(PeerLink link, int ms, double rtt, string caminho, string motivo) =>
        _teleSessao?.Evento("srt_latencia", new JsonObject
        {
            ["latencia_ms"] = ms, ["rtt_ms"] = rtt > 0 ? Math.Round(rtt, 1) : null, ["caminho"] = caminho, ["motivo"] = motivo, ["ligacao"] = link.SessionId,
        });

    /// <summary>
    /// A cada ~1 s: perda do túnel de quem eu assisto acima de 2% por ~6 s sobe a latência da PRÓXIMA abertura do player daquela ligação (x1,5, até 1000 ms, no máximo 4 vezes).
    /// Não reabre o player para isso: a reabertura corta a imagem de todos os espectadores da live (o transmissor reinicia o envio), e o custo é maior que o ganho.
    /// </summary>
    void AcompanharLatenciaSrt()
    {
        if (_escaladaLatencia.Count == 0) return;
        long agora = Environment.TickCount64;
        foreach (var sid in _escaladaLatencia.Keys.ToList())
        {
            if (!_links.TryGetValue(sid, out var link) || !_linkViewers.TryGetValue(sid, out var v)) { _escaladaLatencia.Remove(sid); continue; }
            var rx = link.Tunnel.RxViewer;
            if (!_escaladaLatencia[sid].Observar(agora, rx.Received, rx.Lost)) continue;
            var esc = _escaladaLatencia[sid];
            v.LatencyMs = esc.ProximaMs;
            Log.Info($"Latência do SRT: sobe para {esc.ProximaMs} ms na próxima vez que o player abrir (perda de pacotes acima de {EscaladaLatencia.LimitePerdaPct:0}% por {EscaladaLatencia.SustentadaMs / 1000} s; subida {esc.Subidas} de {LatenciaSrt.MaximoSubidas}).");
            EventoLatenciaSrt(link, esc.ProximaMs, link.Tunnel.RttMs, CaminhoDaLigacao(link), "escalada");
        }
    }
}
