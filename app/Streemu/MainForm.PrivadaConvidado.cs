using System.Text.Json.Nodes;
using Streemu.Net;
using Streemu.Ui;
using Streemu.Ui.Multistream;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// Stream privada do lado de quem é convidado (spec 012): o card de convite no Início, Aceitar e Recusar.
/// O convite vive só na memória (fechar o app o perde) e some quando o anfitrião para ou remove a pessoa.
/// </summary>
public sealed partial class MainForm
{
    sealed record ConviteRecebido(string StreamId, string Anfitriao, string Codigo);

    readonly Dictionary<string, ConviteRecebido> _convites = new(); // id do anfitrião -> convite
    readonly Dictionary<string, DateTime> _convitesAusentes = new(); // id do anfitrião -> desde quando saiu da sala
    readonly ConviteCards _conviteCards = new() { Width = 360 };
    static readonly TimeSpan ConviteSemAnfitriao = TimeSpan.FromSeconds(30);
    // Convites aceitos esperando o player abrir (id do anfitrião). O card fica na tela, desativado, até a janela existir ou a tentativa acabar.
    sealed class Conectando { public DateTime Desde = DateTime.Now; public bool ViuLigacao; }
    readonly Dictionary<string, Conectando> _convitesConectando = new();
    static readonly TimeSpan ConviteConectandoMax = TimeSpan.FromSeconds(20);

    void LigarEventosPrivada()
    {
        _conviteCards.Aceitou += AceitarConvite;
        _conviteCards.Recusou += RecusarConvite;
        _convidadosBtn.Click += (_, _) => AbrirConvidados();
        Theme.StyleButton(_convidadosBtn, false);
    }

    /// <summary>Chegou um convite: guarda e mostra o card no próximo segundo. Sem som nem toast (D7).</summary>
    void OnConvitePrivada(string from, string nick, JsonObject msg)
    {
        string? sala = msg["sala"]?.GetValue<string>(), codigo = msg["codigo_convite"]?.GetValue<string>();
        if (string.IsNullOrEmpty(sala) || string.IsNullOrEmpty(codigo)) return;
        bool jaTinha = _convites.TryGetValue(from, out var antigo) && antigo.StreamId == sala && antigo.Codigo == codigo;
        string anfitriao = msg["anfitriao"]?.GetValue<string>() ?? nick;
        _convites[from] = new ConviteRecebido(sala, anfitriao, codigo);
        Log.Info($"{nick} me convidou para uma transmissão privada.");
        AtualizarConvites();
        if (!jaTinha) AvisarConvite(anfitriao);
    }

    /// <summary>
    /// Som e aviso do convite (spec 013; muda a D7 da 012). Passam pelo porteiro, então o sino silenciado cala os dois e sobra só o card.
    /// Em foco basta a linha de status; fora de foco (jogando) o balão da bandeja, como nos avisos de live.
    /// </summary>
    void AvisarConvite(string anfitriao)
    {
        string texto = $"{anfitriao} convidou você para uma transmissão privada. Aceite no Início.";
        TocarSom(Sons.Som.ConviteRecebido);
        _groupStatus.Text = texto;
        if (Form.ActiveForm == null) MostrarBalao(6000, UiBrand.Name, texto, ToolTipIcon.None);
    }

    /// <summary>O anfitrião parou a live ou me tirou da lista.</summary>
    void OnRemocaoPrivada(string from, JsonObject msg)
    {
        if (_convites.TryGetValue(from, out var c) && c.StreamId == msg["sala"]?.GetValue<string>()) _convites.Remove(from);
        AtualizarConvites();
    }

    /// <summary>O código do convite que vale para esta live deste anfitrião; nulo se não fui convidado (ou o convite é de outra live).</summary>
    string? CodigoConviteDe(string anfitriaoId, string streamId) =>
        _convites.TryGetValue(anfitriaoId, out var c) && c.StreamId == streamId ? c.Codigo : null;

    /// <summary>Pede para assistir uma live privada a que fui convidado (sem passar pela lista "Ao vivo", onde ela não aparece).</summary>
    Task AssistirConvidadoAsync(string anfitriaoId, string anfitriaoApelido, string streamId) =>
        WatchAsync(new Member(anfitriaoId, anfitriaoApelido, "", new LiveInfo(streamId, "privada", "", 0, 0)), "convite da privada");

    void AceitarConvite(string anfitriaoId)
    {
        if (!_convites.TryGetValue(anfitriaoId, out var c)) return;
        if (_playerStage is { Full: true }) { Notify($"Já são {PlayerStage.MaxStreams} streams na janela. Feche uma para abrir outra."); return; }
        // Segundo clique (relato de 2026-11-15): o WatchAsync só vê a ligação depois do await, então dois cliques rápidos passariam os dois.
        if (_convitesConectando.ContainsKey(anfitriaoId) || _links.Values.Any(l => !l.IAmSender && l.PeerId == anfitriaoId)) return;
        _convitesConectando[anfitriaoId] = new Conectando();
        AtualizarConvites(); // o card já vira "Conectando" no mesmo lugar, antes de qualquer coisa sair do lugar
        _ = AssistirConvidadoAsync(anfitriaoId, c.Anfitriao, c.StreamId);
    }

    void RecusarConvite(string anfitriaoId)
    {
        if (!_convites.Remove(anfitriaoId, out var c)) return;
        _ = _room?.SendTo(anfitriaoId, SalaPrivada.MensagemRecusa(c.StreamId));
        AtualizarConvites();
    }

    /// <summary>A cada segundo: esquece convites de quem saiu da sala (ou parou), mostra os cards e o botão Convidados do anfitrião.</summary>
    void AtualizarConvites()
    {
        _convidadosBtn.Visible = Privada.Ligada;
        if (_room is { Connected: true }) EsquecerConvitesSemAnfitriao();
        AtualizarConvitesConectando();
        var visiveis = _convites
            // já assistindo: o card sai, mas só depois que o player tem janela (_convitesConectando): sumir no clique fazia o resto do Início subir sob o mouse
            .Where(kv => _convitesConectando.ContainsKey(kv.Key) || !_links.Values.Any(l => !l.IAmSender && l.PeerId == kv.Key))
            .Select(kv => new ConviteVisto(kv.Key, kv.Value.Anfitriao, _convitesConectando.ContainsKey(kv.Key)))
            .OrderBy(c => c.Apelido, StringComparer.CurrentCultureIgnoreCase).ToList();
        _conviteCards.Mostrar(visiveis);
    }

    /// <summary>Tira da espera o convite cujo player já tem janela, cuja ligação acabou ou que passou de ~20 s sem janela.</summary>
    void AtualizarConvitesConectando()
    {
        foreach (var (id, estado) in _convitesConectando.ToList())
        {
            var link = _links.Values.FirstOrDefault(l => !l.IAmSender && l.PeerId == id);
            if (link != null) estado.ViuLigacao = true;
            bool janela = link != null && _linkViewers.TryGetValue(link.SessionId, out var v) && v.Window.TryGetBounds(out _);
            bool acabou = (estado.ViuLigacao && link == null) || !_convites.ContainsKey(id) || DateTime.Now - estado.Desde > ConviteConectandoMax;
            if (janela || acabou) _convitesConectando.Remove(id);
        }
    }

    void EsquecerConvitesSemAnfitriao()
    {
        var agora = DateTime.Now;
        foreach (string id in _convites.Keys.ToList())
        {
            if (_room!.IsPresent(id)) { _convitesAusentes.Remove(id); continue; }
            if (agora - _convitesAusentes.GetValueOrDefault(id, agora) > ConviteSemAnfitriao) { _convites.Remove(id); _convitesAusentes.Remove(id); }
            else _convitesAusentes.TryAdd(id, agora);
        }
    }
}
