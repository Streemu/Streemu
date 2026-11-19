using Streemu.Net;
using Streemu.Ui;

namespace Streemu;

/// <summary>
/// Atividades recentes do grupo, no painel do ícone de pulso da barra lateral (plano 019, etapa 9; saiu do Início em 2026-11-18 porque 20 linhas poluíam a tela). O servidor da sala com conta decide e guarda quase tudo (live, live_fim, entrou, saiu); o app só conta
/// ao servidor o que mais ninguém sabe: que passou a ASSISTIR uma live pública (uma vez por ligação) e que abriu um filme para o grupo (sem o nome).
/// <b>Live privada nunca gera atividade</b>: nem a live (ela não passa por <c>SetLive</c>), nem o "assistindo" de quem é convidado. Na sala antiga o painel fica vazio.
/// </summary>
public sealed partial class MainForm
{
    /// <summary>
    /// A ligação é de uma transmissão privada? Não depende da telemetria (o <c>MarcarSePrivada</c> só roda com ela ligada): o convidado tem convite do anfitrião,
    /// e o anfitrião de uma privada nunca tem live pública na sala. Na dúvida, é privada: o custo de errar para esse lado é uma linha a menos no painel.
    /// </summary>
    bool LigacaoDeLivePrivada(PeerLink link) =>
        LigacaoPrivada(link) || _convites.ContainsKey(link.PeerId) || _room?.Find(link.PeerId)?.Live == null;

    /// <summary>Uma ligação de assistir acabou de conectar pela primeira vez (<see cref="OnLinkState"/>): conta ao grupo em que o amigo está ao vivo.</summary>
    void ContarQueComeceiAAssistir(PeerLink link)
    {
        if (link.IAmSender || _room == null || LigacaoDeLivePrivada(link)) return;
        _room.EnviarAtividade("assistindo", link.PeerId);
    }

    /// <summary>Abri um filme para o grupo: conta ao servidor (sem o nome do filme; o servidor ainda segura repetições).</summary>
    void ContarQueAbriUmFilme() => _room?.EnviarAtividade("filme");

    AtividadesPopup? _atividadesPopup;
    /// <summary>Assinatura do último estado mandado ao ícone, para só mexer na barra quando muda.</summary>
    string _ultimaAtividade = "";

    /// <summary>
    /// O que acende o ponto do ícone: alguém (não eu) entrou ao vivo, abriu um filme ou começou a me assistir, depois da última vez que abri o painel.
    /// Entrar, sair e o fim de live ficam no painel mas não chamam atenção: com o grupo cheio acenderiam o ponto o tempo todo.
    /// </summary>
    static bool Relevante(Atividade a, string meuId) =>
        a.Conta != meuId && (a.Tipo is "live" or "filme" || a.Tipo == "assistindo" && a.Campo("alvo") == meuId);

    (IReadOnlyList<LinhaDeAtividade> linhas, IReadOnlyList<string>? grupos) LinhasDeAtividade()
    {
        if (!_salaDeConta || _room == null) return (Array.Empty<LinhaDeAtividade>(), null); // a sala antiga não tem atividades: o painel mostra o texto de vazio
        var linhas = FrasesDeAtividade.Montar(_room.Atividades, (conta, apelido) => NomeNaTela(conta, apelido), DateTimeOffset.Now, meuId: _room.MyId);
        IReadOnlyList<string>? grupos = null;
        if (_salaUnida is { Quantidade: > 1 } unida)
        {
            var nomes = unida.Grupos.ToDictionary(g => g.Id, g => g.Nome);
            grupos = linhas.Select(l => nomes.GetValueOrDefault(l.GrupoId, "")).ToList(); // com mais de um grupo, cada linha diz de qual
        }
        return (linhas, grupos);
    }

    /// <summary>Roda a cada segundo (<c>KeepLiveCards</c>): acende ou apaga o ponto do ícone e, com o painel aberto, atualiza as linhas.</summary>
    void AtualizarAtividades()
    {
        if (_atividadesPopup is { Visible: true } aberto)
        {
            var (linhas, grupos) = LinhasDeAtividade();
            aberto.Atualizar(linhas, grupos);
            MarcarAtividadesVistas(); // o que chega com o painel aberto já foi visto
        }
        var novas = !_salaDeConta || _room == null ? [] : _room.Atividades.Where(a => a.Hora > _state.AtividadeVista && Relevante(a, _room.MyId)).ToList();
        string dica = novas.Count switch
        {
            0 => "Atividade recente do grupo",
            1 => "1 novidade no grupo. Clique para ver.",
            _ => $"{novas.Count} novidades no grupo. Clique para ver.",
        };
        string assinatura = $"{novas.Count > 0}|{dica}";
        if (assinatura == _ultimaAtividade) return;
        _ultimaAtividade = assinatura;
        _side.SetAtividade(novas.Count > 0, dica);
        _flyout?.Panel.SetAtividade(novas.Count > 0, dica);
    }

    void MarcarAtividadesVistas()
    {
        if (_room == null) return;
        long ultima = _room.Atividades.Select(a => a.Hora).DefaultIfEmpty(0).Max();
        if (ultima <= _state.AtividadeVista) return;
        _state.AtividadeVista = ultima;
        if (!Automated) _state.Save();
    }

    /// <summary>Clique no ícone de pulso: abre o painel ao lado (ou fecha, se ele acabou de fechar por esse mesmo clique).</summary>
    void MostrarAtividades(Rectangle itemNaTela)
    {
        _atividadesPopup ??= new AtividadesPopup();
        if (_atividadesPopup.AcabouDeFechar) return;
        var (linhas, grupos) = LinhasDeAtividade();
        _atividadesPopup.Mostrar(this, itemNaTela, linhas, grupos);
        MarcarAtividadesVistas();
        _ultimaAtividade = "";
        AtualizarAtividades();
    }
}
