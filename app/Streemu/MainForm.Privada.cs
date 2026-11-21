using System.Text.Json.Nodes;
using Streemu.Net;
using Streemu.Ui;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// Stream privada do lado de quem transmite (spec 012, parte A). Fica fora do MainForm.Grupo.cs para dar menos conflito.
/// A lista e os códigos de convite vivem em <see cref="SalaPrivada"/>; aqui só se liga isso às ligações, à sala, à miniatura e à telemetria.
/// O lado de quem é convidado está em MainForm.PrivadaConvidado.cs.
/// </summary>
public sealed partial class MainForm
{
    SalaPrivada? _privada;
    /// <summary>Pedido da próxima live: o interruptor "Privada" da janela Transmitir liga; vale uma vez só (D6: sempre começa pública).</summary>
    bool _comecarPrivada;
    List<string> _convidarAoComecar = new();
    readonly Button _convidadosBtn = new() { Text = "Convidados", AutoSize = true, Visible = false };
    ConvidadosForm? _convidadosForm;

    // Criada sob demanda: o inicializador de campo não pode usar _room (ele troca quando o app reconecta à sala).
    SalaPrivada Privada => _privada ??= new SalaPrivada(id => _room?.IsPresent(id) ?? false);

    /// <summary>Chamado quando a live começa, antes do primeiro UpdateLive: decide se esta live é privada e manda os primeiros convites.</summary>
    void PrivadaAoComecar()
    {
        bool privada = _comecarPrivada || TestePrivadaAnfitriao;
        _comecarPrivada = false;
        if (!privada || _streamId == null) { Privada.Encerrar(); return; }
        Privada.Iniciar(_streamId);
        Log.Info("Transmissão privada: não aviso a sala nem o Discord; só entra quem eu convidar.");
        var ids = _convidarAoComecar;
        _convidarAoComecar = new List<string>();
        _ = ConvidarVariosAsync(ids);
    }

    /// <summary>A live acabou: os convidados que ainda não entraram perdem o card, e os convites e códigos morrem.</summary>
    void PrivadaAoParar()
    {
        _comecarPrivada = false;
        _convidarAoComecar.Clear();
        if (Privada.Ligada && _room != null)
            foreach (var c in Privada.Convidados) _ = _room.SendTo(c.Id, Privada.MensagemRemocao());
        Privada.Encerrar();
        _convidadosForm?.Close();
    }

    /// <summary>O que a sala e o Discord podem saber da minha live: nada, se ela é privada.</summary>
    LiveInfo? InfoPublica(LiveInfo? info) => Privada.Ligada ? null : info;

    /// <summary>Verdadeiro se este pedido de assistir deve ser recusado: privada ligada e o pedinte não é convidado com o código dele.</summary>
    bool PrivadaRecusa(string from, string? codigoConvite) => Privada.Ligada && !Privada.Permite(from, codigoConvite);

    /// <summary>Convida um amigo online. Devolve o motivo (frase pronta) se não deu; nulo se o convite saiu.</summary>
    async Task<string?> ConvidarAsync(string id, string apelido)
    {
        if (_room is not { Connected: true }) return "Sem conexão com a sala do grupo. Tente de novo em instantes.";
        var r = Privada.Convidar(id, apelido);
        if (!r.Ok) return r.Motivo;
        await _room.SendTo(id, Privada.MensagemConvite(r.Convidado!, Nick));
        string versao = _room.Find(id)?.Version ?? "";
        if (versao != "" && versao != Program.Version)
            Notify($"{apelido} está na versão {versao} e você na {Program.Version}: ele pode não ver o convite até atualizar.");
        return null;
    }

    async Task ConvidarVariosAsync(IReadOnlyList<string> ids)
    {
        var motivos = new List<string>();
        foreach (string id in ids)
        {
            if (_room?.Find(id) is not { } amigo) continue;
            if (await ConvidarAsync(id, amigo.Nick) is { } motivo) motivos.Add(motivo);
        }
        if (motivos.Count > 0) Notify(string.Join(" ", motivos));
    }

    /// <summary>O anfitrião tirou um convidado: sai da lista (o código morre), ele perde o card e a ligação dele cai na hora.</summary>
    void RemoverConvidado(string id)
    {
        if (Privada.Ligada) _ = _room?.SendTo(id, Privada.MensagemRemocao());
        Privada.Remover(id);
        foreach (var (sid, link) in _links.ToList())
            if (link.IAmSender && link.PeerId == id) EndLink(sid, tellPeer: true, why: null);
    }

    /// <summary>O convidado disse que não quer: sai da lista para abrir vaga.</summary>
    void OnRespostaConvite(string from, string nick, JsonObject msg)
    {
        if (!Privada.Ligada || msg["sala"]?.GetValue<string>() != _streamId || msg["aceito"]?.GetValue<bool>() != false) return;
        if (Privada.Remover(from)) Notify($"{NomeNaTela(from, nick)} recusou o convite.");
    }

    void AbrirConvidados()
    {
        if (!Privada.Ligada) return;
        if (_convidadosForm is { IsDisposed: false }) { _convidadosForm.Activate(); return; }
        _convidadosForm = new ConvidadosForm(ListarPessoasParaConvite, ConvidarAsync, RemoverConvidado, SalaPrivada.Limite);
        _convidadosForm.Show(this);
    }

    IReadOnlyList<PessoaConvite> ListarPessoasParaConvite() =>
        _room?.Friends.Select(f => new PessoaConvite(f.Id, NomeNaTela(f), Privada.EstaNaLista(f.Id))).ToList() ?? new List<PessoaConvite>();

    // ------------------------------------------------------------------ telemetria (D5: só dados técnicos)

    /// <summary>As ligações (sessões) que fazem parte de uma privada; para esconder quem estava com quem no que sobe.</summary>
    readonly HashSet<string> _ligacoesPrivadas = new();

    bool LigacaoPrivada(PeerLink link) => _ligacoesPrivadas.Contains(link.SessionId);

    void MarcarSePrivada(PeerLink link)
    {
        if (link.IAmSender ? Privada.Ligada : _convites.ContainsKey(link.PeerId)) _ligacoesPrivadas.Add(link.SessionId);
    }

    string NomeParaTelemetria(PeerLink link) => LigacaoPrivada(link) ? "convidado" : link.PeerNick;

    /// <summary>
    /// Relatório que sobe para o servidor: as ligações de uma privada perdem o apelido da outra ponta (achadas pelo id da ligação, não pelo apelido: dois amigos podem ter o mesmo).
    /// O "Copiar relatório" do próprio usuário não passa por aqui.
    /// </summary>
    string SemQuemEstavaComQuem(string relatorioJson)
    {
        if (_ligacoesPrivadas.Count == 0 || JsonNode.Parse(relatorioJson) is not JsonObject relatorio) return relatorioJson;
        if (relatorio["sala"]?["ligacoes"] is not JsonArray ligacoes) return relatorioJson;
        foreach (var item in ligacoes)
            if (item is JsonObject l && l["ligacao"]?.GetValue<string>() is { } sid && _ligacoesPrivadas.Contains(sid)) l["amigo"] = "convidado";
        return relatorio.ToJsonString();
    }
}
