using System.Text.Json.Nodes;
using Streemu.Media;
using Streemu.Net;
using Streemu.Ui;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// Sons da live do lado de quem assiste (spec 014): o anfitrião manda "entrou" ou "saiu" (<see cref="SomNaLive"/>) e este app toca o som no PC de cada
/// espectador, pelo mesmo porteiro de sons dos outros avisos (<see cref="TocarSom"/>: o sino silenciado e o teste automático o calam).
/// </summary>
public sealed partial class MainForm
{
    /// <summary>Só toca se a mensagem vem de um amigo cuja live eu estou assistindo e já conectei: mensagem de quem não é meu anfitrião é ignorada.</summary>
    void OnSomNaLive(string from, JsonObject msg)
    {
        if (!SomNaLive.TentarLer(msg, out var tipo, out _)) return;
        if (!AssistindoLiveDe(from)) return;
        TocarSom(tipo == TipoSomNaLive.Entrou ? Sons.Som.EntrouNaLive : Sons.Som.SaiuDaLive);
    }

    bool AssistindoLiveDe(string anfitriaoId) =>
        _links.Values.Any(l => !l.IAmSender && l.PeerId == anfitriaoId && _linkEverConnected.Contains(l.SessionId));

    // ------------------------------------------------------------------ anfitrião: quando mandar

    SomNaLive? _somNaLive;
    SomNaLive SomDaLive => _somNaLive ??= new SomNaLive(() => DateTime.Now);

    /// <summary>Não manda som se a opção de Ajustes está desligada ou se a live é "Sem áudio" (a pessoa pediu silêncio). A live de mentira dos testes conta como com áudio.</summary>
    bool SomNaLivePermitido => !_state.SomNaLiveDesligado && (_fakeLive || _sender.Current is { Audio: not AudioMode.None });

    void IniciarSomNaLive()
    {
        SomDaLive.Encerrar();
        SomDaLive.Ligado = SomNaLivePermitido;
    }

    void EncerrarSomNaLive()
    {
        SomDaLive.Ligado = false; // descarta as saídas pendentes
        SomDaLive.Encerrar();
    }

    List<string> EspectadoresConectados() => _links.Values.Where(l => l.IAmSender && l.Connected).Select(l => l.PeerId).ToList();

    /// <summary>Um espectador conectou pela primeira vez (depois do som local de "entrou").</summary>
    void AnunciarEntradaNaLive(string idDoEspectador)
    {
        if (SomDaLive.Entrou(idDoEspectador, EspectadoresConectados()) is { } som) EnviarSomNaLive(som);
    }

    /// <summary>Um espectador que tinha conectado saiu: a saída só toca depois da tolerância, se ele não voltar (<see cref="EnviarSonsPendentes"/>).</summary>
    void AnunciarSaidaDaLive(string idDoEspectador) => SomDaLive.Saiu(idDoEspectador);

    /// <summary>A cada segundo: manda as saídas cuja tolerância acabou.</summary>
    void EnviarSonsPendentes()
    {
        foreach (var som in SomDaLive.Coletar(EspectadoresConectados())) EnviarSomNaLive(som);
    }

    /// <summary>Uma mensagem para cada ouvinte (um JsonObject não pode ter dois donos); sem rede ou sem live, não manda nada.</summary>
    void EnviarSomNaLive(SomParaEnviar som)
    {
        if (_room == null || _streamId == null) return;
        foreach (string id in som.Destinatarios) _ = _room.SendTo(id, SomNaLive.Mensagem(som.Tipo, _streamId));
        Log.Info($"Som na live: {som.Tipo} para {som.Destinatarios.Count} espectador(es).");
    }
}
