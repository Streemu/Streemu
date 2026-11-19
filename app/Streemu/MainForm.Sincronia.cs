using Streemu.Telemetria;
using Streemu.Net;
using System.Text.Json.Nodes;

namespace Streemu;

public sealed partial class MainForm
{
    SerieSincronia? _serieSincronia;
    SerieRede? _serieRede;
    long _ultimaSincronia;

    void GravarSincronia()
    {
        if (_serieSincronia == null || Environment.TickCount64 - _ultimaSincronia < 2000) return;
        _ultimaSincronia = Environment.TickCount64;
        if (_sender.Running) _serieSincronia.Add(_streamId, "propria", "transmite", _sender.SyncSnapshot());
        foreach (var (id, viewer) in _linkViewers)
            if (viewer.Running && !viewer.IsPreview)
                _serieSincronia.Add(_teleLiveDaLigacao.GetValueOrDefault(id), id, "assiste", viewer.Sync.Snapshot());
    }

    /// <summary>Rede de cada ligação (etapa 10 da spec 009): uma linha a cada 5 s por ligação conectada, só contadores que o túnel já mantém.</summary>
    void GravarRede()
    {
        _serieRede?.Amostrar(Environment.TickCount64, () => _links.Values.Select(l =>
        {

            string? live = l.IAmSender ? _streamId : _teleLiveDaLigacao.GetValueOrDefault(l.SessionId);
            return (l.SessionId, live ?? "", NomeParaTelemetria(l), l.IAmSender ? "transmite" : "assiste", LerRede(l));
        }).ToList());
    }

    /// <summary>O caminho confirmado da ligação, com o "direto pela operadora" separado do direto comum (decisão em <see cref="Tunnel.TargetIsCarrierAlt"/>).</summary>
    static string CaminhoDaLigacao(PeerLink l) => SerieRede.NomeDoCaminho(l.Tunnel.Target is { } c ? (int)c.Kind : null, l.Tunnel.TargetIsCarrierAlt);

    static LeituraRede LerRede(PeerLink l)
    {
        var t = l.Tunnel;
        return new LeituraRede(t.State.ToString(), CaminhoDaLigacao(l), t.RttMs, t.TxBytes, t.RxBytes, t.RxViewer.Received, t.RxViewer.TotalLossPct, !l.IAmSender);
    }

    /// <summary>
    /// Conexão só de ida (ou a volta dela) vira evento: era só um aviso na tela e uma linha de log, e é a pista de campo para NAT, firewall e CGNAT.
    /// Só o tipo e quem é o amigo; o texto do aviso fica de fora.
    /// </summary>
    void TelemetriaAvisoLigacao(PeerLink l, bool soDeIda)
    {
        _teleSessao?.Evento("ligacao_aviso", new JsonObject
        {
            ["ligacao"] = l.SessionId, ["amigo"] = NomeParaTelemetria(l), ["papel"] = l.IAmSender ? "transmite" : "assiste", ["tipo"] = soDeIda ? "so_de_ida" : "voltou",
        });
    }

    /// <summary>Mudança de estado da ligação vira evento (rara; o caminho e o tempo para conectar explicam "demorou" ou "caiu para relay").</summary>
    void TelemetriaEstadoLigacao(PeerLink l, TunnelState s)
    {
        _teleSessao?.Evento("ligacao_estado", new JsonObject
        {
            ["ligacao"] = l.SessionId, ["amigo"] = NomeParaTelemetria(l), ["papel"] = l.IAmSender ? "transmite" : "assiste", ["estado"] = s.ToString(),
            ["caminho"] = CaminhoDaLigacao(l), ["tempo_para_conectar_ms"] = l.Tunnel.ConnectMs,
        });
    }
}
