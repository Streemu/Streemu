using Streemu.Acompanhar;
using System.Text.Json.Nodes;
using Streemu.Net;
using Streemu.Ui;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// "Continuar assistindo" (spec 038): liga a regra de <see cref="AcompanharAmigos"/> à sala e à janela. Olha a sala a cada mudança e a cada segundo
/// (o prazo da espera acaba sem a sala mudar). A regra decide; aqui só se entra na live e se avisa a pessoa.
/// </summary>
public sealed partial class MainForm
{
    AcompanharAmigos? _acompanhar;
    /// <summary>Amigo em quem acabei de entrar sozinho, e quando: o aviso "começou uma live" da sala não repete o que o "continuar assistindo" já fez.</summary>
    readonly Dictionary<string, DateTime> _acompanharEntrouEm = new();

    AcompanharAmigos Acompanhando => _acompanhar ??= new AcompanharAmigos(() => DateTime.Now, _state.AmigosAcompanhados, () => _state.AcompanharPrazoPadraoMinutos);

    /// <summary>Entrei sozinho na live deste amigo faz pouco? (usado pelo aviso de live, para não anunciar o que já aconteceu.)</summary>
    bool AcompanharEntrouAgora(string amigoId) =>
        _acompanharEntrouEm.TryGetValue(amigoId, out var quando) && DateTime.Now - quando < TimeSpan.FromMinutes(2);

    /// <summary>
    /// A cada mudança da sala e a cada segundo. Com a sala fora do ar não decide nada: a sala cai ~2 s de vez em quando e voltar não pode contar como
    /// "o amigo fechou a live" (ver DESCOBERTAS, 2026-11-10).
    /// </summary>
    void AcompanharOlharSala()
    {
        // sem trava de Automated: a lista vazia já faz tudo ser no-op nos testes de hoje, e um teste de duas instâncias precisa que isto rode
        if (_state.AmigosAcompanhados.Count == 0 || _room is not { Connected: true } room) return;
        var vistos = room.Friends.Select(m => new AmigoVisto(m.Id, m.Live != null || FilmeParaAssistir(m.Id) != null, EstouAssistindoAmigo(m.Id))).ToList();
        foreach (var acao in Acompanhando.Atualizar(vistos, PossoEntrarSozinho()))
            ExecutarAcaoDeAcompanhar(acao, room);
        AvisarEsperasNovas(room);
    }

    /// <summary>Amigos cuja espera já foi anunciada: o aviso "Esperando fulano voltar" sai uma vez por espera.</summary>
    readonly HashSet<string> _acompanharEsperando = new();

    void AvisarEsperasNovas(ISala room)
    {
        foreach (var id in _state.AmigosAcompanhados.Keys.ToList())
        {
            if (Acompanhando.EsperaDe(id) is not { } espera) { _acompanharEsperando.Remove(id); continue; }
            if (!_acompanharEsperando.Add(id)) continue;
            _acompanharEsperas++;
            string nome = room.Find(id) is { } m ? NomeNaTela(m) : "o amigo";
            string ate = espera.Ate is { } fim ? $"até {(int)Math.Round((fim - espera.Desde).TotalMinutes)} min" : "até ele voltar";
            Notify($"Esperando {nome} voltar ({ate}). Para parar: clique na foto dele > Parar de esperar.");
        }
    }

    /// <summary>O que aparece sob o nome do amigo na barra: a situação do "continuar assistindo", se ele é acompanhado.</summary>
    string DetalheComAcompanhar(string amigoId, string detalhe)
    {
        if (!_state.AmigosAcompanhados.ContainsKey(amigoId)) return detalhe;
        return Acompanhando.EsperaDe(amigoId) != null ? "esperando voltar" : detalhe + " · acompanhando";
    }

    /// <summary>"Continuar assistindo" no menu da foto: escolhe o prazo da espera ou desmarca. "Parar de esperar" só aparece com espera em andamento.</summary>
    IEnumerable<ToolStripItem> ItensDeAcompanhar(SideFriend amigo)
    {
        var raiz = new ToolStripMenuItem("Continuar assistindo");
        bool marcado = _state.AmigosAcompanhados.TryGetValue(amigo.Id, out int minutosDele);
        raiz.DropDownItems.Add(OpcaoDeAcompanhar(amigo, "Não acompanhar", null, !marcado));
        foreach (var (texto, minutos) in new[] { ("5 minutos", 5), ("10 minutos", 10), ("20 minutos", 20), ("Até o amigo voltar", 0) })
            raiz.DropDownItems.Add(OpcaoDeAcompanhar(amigo, texto, minutos, marcado && minutosDele == minutos));
        yield return raiz;
        if (Acompanhando.EsperaDe(amigo.Id) != null)
            yield return new ToolStripMenuItem("Parar de esperar", null, (_, _) => { Acompanhando.Parar(amigo.Id); Notify($"Parei de esperar {amigo.Nick}."); });
    }

    ToolStripMenuItem OpcaoDeAcompanhar(SideFriend amigo, string texto, int? minutos, bool marcada) =>
        new(texto, null, (_, _) => DefinirAcompanhar(amigo, minutos)) { Checked = marcada };

    /// <summary><paramref name="minutos"/> nulo desmarca; 0 espera até o amigo voltar (spec 038).</summary>
    void DefinirAcompanhar(SideFriend amigo, int? minutos)
    {
        if (minutos is null) { _state.AmigosAcompanhados.Remove(amigo.Id); Acompanhando.Parar(amigo.Id); }
        else _state.AmigosAcompanhados[amigo.Id] = minutos.Value;
        if (!Automated) _state.Save();
        Notify(minutos is null ? $"Não vou mais acompanhar {amigo.Nick}." : $"Vou continuar assistindo {amigo.Nick}: quando ele fechar a live, espero {TextoDoPrazo(minutos.Value)}.");
    }

    static string TextoDoPrazo(int minutos) => minutos == 0 ? "até ele voltar" : $"até {minutos} min";

    /// <summary>
    /// Assisto este amigo agora, em live ou no filme dele pela transmissão (spec 038, T6). O filme só entra aqui pelo "Assistir agora": o anúncio do
    /// anfitrião precisa dizer que ele transmite, senão a pessoa teria de ter o arquivo, e isso não se decide sozinho.
    /// </summary>
    bool EstouAssistindoAmigo(string amigoId) =>
        _links.Values.Any(l => !l.IAmSender && l.PeerId == amigoId) || EstouNoFilmeDe(amigoId);

    bool EstouNoFilmeDe(string amigoId) =>
        _streamTela is { IsDisposed: false } && _streamSid != null && _filmeLinks.TryGetValue(_streamSid, out var l) && l.PeerId == amigoId;

    /// <summary>O anúncio do filme que este amigo tem aberto e transmite (o mesmo que o botão "Assistir agora" usa), ou nulo. O anúncio expira sozinho em 20 s.</summary>
    JsonObject? FilmeParaAssistir(string amigoId) =>
        _filmeAbertos.TryGetValue(amigoId, out var aberto) && aberto.Msg["stream"]?.GetValue<bool>() == true ? aberto.Msg : null;

    /// <summary>Não troca o que a pessoa está fazendo (spec 038, D3): assistindo outra live ou transmitindo (jogando) não deixa a entrada automática acontecer.</summary>
    bool PossoEntrarSozinho() => !_links.Values.Any(l => !l.IAmSender) && !(_sender.Running || _fakeLive) && !(_playerStage is { Full: true }) && !(_streamTela is { IsDisposed: false });

    void ExecutarAcaoDeAcompanhar(AcaoAcompanhar acao, ISala room)
    {
        var amigo = room.Find(acao.AmigoId);
        string nome = amigo is null ? "o amigo" : NomeNaTela(amigo);
        switch (acao.Tipo)
        {
            case TipoAcaoAcompanhar.Entrar when amigo?.Live != null:
                _acompanharEntrouEm[acao.AmigoId] = DateTime.Now;
                _acompanharEntradas++;
                Notify($"Entrei na live de {nome} (continuar assistindo). Para sair: clique na foto dele > Fechar a live.");
                _ = WatchAsync(amigo, "continuar assistindo");
                break;
            case TipoAcaoAcompanhar.Entrar when FilmeParaAssistir(acao.AmigoId) is { } filme:
                _acompanharEntrouEm[acao.AmigoId] = DateTime.Now;
                _acompanharEntradas++;
                Notify($"Entrei no filme de {nome} (continuar assistindo).");
                _ = AssistirStreamAsync(filme);
                break;
            case TipoAcaoAcompanhar.AvisarLiveAberta:
                Notify($"{nome} abriu uma live ou um filme. Você está assistindo outra coisa: abra pelo menu da foto dele para entrar.");
                break;
            case TipoAcaoAcompanhar.DesistiuDeEsperar:
                _acompanharDesistencias++;
                Notify($"Parei de esperar {nome}.");
                break;
        }
    }
}
