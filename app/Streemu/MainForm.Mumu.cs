using Streemu.Net;
using Streemu.Ui;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// A aba da Mumu na janela principal (spec 031): o item da barra lateral e a vista <c>_mumuView</c> com o seu host, no mesmo jeito da tela Novidades.
/// Mostra o histórico de recados dos responsáveis (pedido ao servidor ao abrir a aba; sem resposta, os últimos conhecidos ou o padrão), o feedback (texto + "Mandar com meu nome",
/// enviado pela conta ao Worker do grupo) e o espaço reservado das redes sociais. O selo "NOVO" da barra (e do trilho) aparece enquanto o recado mais novo não foi visto
/// e some ao abrir a aba. O app também pede os recados em segundo plano, sem a aba aberta (ao iniciar e a cada 30 min), para o selo chegar sem a pessoa abrir nada.
/// </summary>
public sealed partial class MainForm
{
    readonly BufferedPanel _mumuHost = new() { Dock = DockStyle.Fill, Visible = false };
    MumuTela? _mumuTela;
    /// <summary>A aba da Mumu está aberta (vista da janela, como _novidadesView).</summary>
    bool _mumuView;
    /// <summary>Já há um pedido de recado a caminho (o da abertura da aba e o de segundo plano não se sobrepõem). Só a thread da tela mexe.</summary>
    bool _mumuBuscando;
    /// <summary>Espera depois de abrir o app, para a busca não pesar na abertura.</summary>
    static readonly TimeSpan AtrasoDosRecados = TimeSpan.FromSeconds(8);
    /// <summary>De quanto em quanto tempo o app confere se tem recado novo, com a aba fechada.</summary>
    static readonly TimeSpan IntervaloDosRecados = TimeSpan.FromMinutes(30);

    /// <summary>Chamado no fim do BuildShell, depois da tela Novidades: a aba nasce depois do Theme.Apply da janela.</summary>
    void AdicionarMumu()
    {
        if (_mumuTela != null) return;
        var tela = _mumuTela = new MumuTela();
        tela.Feedback.EnviarClicado += () => _ = EnviarFeedbackDaMumuAsync(tela.Feedback);
        _mumuHost.BackColor = Theme.Surface;
        _mumuHost.Controls.Add(tela);
        Controls.Add(_mumuHost);
        CorrigirOrdemDoEncaixe(); // o host entrou depois da barra lateral, e o encaixe do WinForms vai do último para o primeiro
        MostrarRecadosDaMumu(RecadosSalvos(), marcarVisto: false); // os últimos conhecidos, até o servidor responder
        _side.MumuClicked += MostrarMumu;
        _side.MumuVisivel = true;
        if (_flyout != null) _flyout.Panel.MumuVisivel = true;
        AjustarAlturaMinima(); // um item a mais na navegação: a janela não pode ficar mais baixa que a barra
        // teste automático não pede nada ao servidor (o LinkRelay é nulo); só com recado de teste na variável é que o laço roda
        if (!Automated || Environment.GetEnvironmentVariable(RecadoDaMumu.VariavelDeTeste) is { Length: > 0 }) _ = LerRecadosEmSegundoPlanoAsync(_closing.Token);
    }

    /// <summary>
    /// Com a aba fechada: espera <see cref="AtrasoDosRecados"/> e confere os recados; repete a cada <see cref="IntervaloDosRecados"/> até o app fechar (o <c>_closing</c> cancela).
    /// Um de cada vez, sem janela de erro (só o log) e sem marcar como visto: o selo fica até a pessoa abrir a aba.
    /// </summary>
    async Task LerRecadosEmSegundoPlanoAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(AtrasoDosRecados, ct);
            while (!ct.IsCancellationRequested)
            {
                await AtualizarRecadoDaMumuAsync(ct);
                await Task.Delay(IntervaloDosRecados, ct);
            }
        }
        catch (OperationCanceledException) { } // o app fechou
    }

    /// <summary>O item da barra lateral: abre a aba no lugar do Início (mesmo jeito do Grupos e das Novidades).</summary>
    void MostrarMumu()
    {
        if (_mumuTela == null) return;
        HideFlyout();
        if (_preparationBusy) return;
        if (ContaBloqueia()) return;
        _preparationView = _stageView = _settingsView = _filmeView = _arquivosView = _gruposView = _novidadesView = false;
        _mumuView = true;
        ApplyView();
        MostrarRecadosDaMumu(RecadosSalvos(), marcarVisto: true);
        _ = AtualizarRecadoDaMumuAsync(_closing.Token);
    }

    /// <summary>
    /// Pergunta os recados ao servidor (ao abrir a aba e em segundo plano). Sem servidor, sem conta ou sem internet, fica o último recado e não reclama (só o log).
    /// Se outro pedido já está a caminho, não faz nada: o resultado dele atualiza a tela e o selo. Só marca como visto se a aba está aberta.
    /// </summary>
    async Task AtualizarRecadoDaMumuAsync(CancellationToken ct = default)
    {
        if (_mumuTela == null || _mumuBuscando) return;
        _mumuBuscando = true;
        try
        {
            IReadOnlyList<RecadoDaMumu> recados;
            if (Environment.GetEnvironmentVariable(RecadoDaMumu.VariavelDeTeste) is { Length: > 0 } teste) recados = RecadoDaMumu.LerLista(teste); // só teste: sem servidor
            else if (LinkRelay is { } servidor) recados = await RecadoDaMumu.BuscarAsync(servidor, ct);
            else return;
            if (IsDisposed) return;
            _state.MumuRecadosJson = RecadoDaMumu.Guardar(recados);
            _state.MumuRecadoId = recados.FirstOrDefault()?.Id; _state.MumuRecadoTexto = recados.FirstOrDefault()?.Texto;
            MostrarRecadosDaMumu(recados, marcarVisto: _mumuView);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { } // o app fechou
        catch (Exception ex) { Log.Info($"Aba da Mumu: sem recado novo ({ex.GetType().Name}: {ex.Message})"); }
        finally { _mumuBuscando = false; }
    }

    /// <summary>Os recados da última resposta guardada; do estado antigo (só um texto), um recado sem data.</summary>
    IReadOnlyList<RecadoDaMumu> RecadosSalvos()
    {
        if (!string.IsNullOrEmpty(_state.MumuRecadosJson))
        {
            try { return RecadoDaMumu.LerLista(_state.MumuRecadosJson); }
            catch (InvalidOperationException) { }
        }
        return string.IsNullOrWhiteSpace(_state.MumuRecadoId) || string.IsNullOrWhiteSpace(_state.MumuRecadoTexto) ? [] : [new RecadoDaMumu(_state.MumuRecadoId, _state.MumuRecadoTexto)];
    }

    /// <summary>
    /// Mostra a lista: o mais novo é "novo" se a pessoa ainda não o viu (a Mumu acorda e ele ganha destaque). Com <paramref name="marcarVisto"/> (a aba está aberta),
    /// ele passa a visto, e na próxima vez que a aba abrir a Mumu já está dormindo de novo.
    /// </summary>
    void MostrarRecadosDaMumu(IReadOnlyList<RecadoDaMumu> recados, bool marcarVisto)
    {
        string? maisNovo = recados.FirstOrDefault()?.Id;
        bool novo = RecadoDaMumu.TemNovo(recados, _state.MumuRecadoVisto);
        string? pose = null;
        if (novo)
        {
            var usadas = _state.MumuPosesUsadas ??= [];
            pose = MumuPoses.Escolher(recados[0], _state.MumuPoseRecado, _state.MumuPose, usadas, Random.Shared);
            if (recados[0].Sprite == null || MumuPoses.Arquivo(recados[0].Sprite) == null) { _state.MumuPoseRecado = recados[0].Id; _state.MumuPose = pose; }
        }
        _mumuTela?.MostrarRecados(recados, novo, pose);
        if (marcarVisto && novo) _state.MumuRecadoVisto = maisNovo;
        if (!Automated) _state.Save();
        AtualizarSeloMumu(recados);
    }

    /// <summary>O selo "NOVO" (barra aberta) ou a bolinha (trilho) nas duas barras: aceso enquanto o recado mais novo não foi visto, apagado quando a aba o marca como visto.</summary>
    void AtualizarSeloMumu(IReadOnlyList<RecadoDaMumu> recados)
    {
        bool novo = RecadoDaMumu.TemNovo(recados, _state.MumuRecadoVisto);
        _side.MumuNovo = novo;
        if (_flyout != null) _flyout.Panel.MumuNovo = novo;
    }

    /// <summary>
    /// Manda o feedback pela conta (spec 031, D5). A resposta aparece dentro do cartão, em frase curta com o motivo e a próxima ação; nunca vira janela de erro.
    /// O texto da pessoa não vai para o log nem para a telemetria (só o resultado: ok ou o código do erro).
    /// </summary>
    async Task EnviarFeedbackDaMumuAsync(MumuFeedbackCartao cartao)
    {
        if (_conta is not { } conta) { cartao.MostrarResultado(false, "Entre na sua conta para mandar sua mensagem."); return; }
        cartao.Ocupado(true);
        ResultadoConta r;
        try { r = await conta.EnviarFeedbackAsync(cartao.Mensagem, cartao.ComNome, Program.Version); }
        catch (Exception ex)
        {
            Log.Info($"Aba da Mumu: o feedback não saiu ({ex.GetType().Name})");
            r = new(false, "erro", FeedbackDaMumu.Frase(null));
        }
        if (IsDisposed) return;
        cartao.Ocupado(false);
        cartao.MostrarResultado(r.Ok, r.Texto ?? (r.Ok ? FeedbackDaMumu.Enviado : FeedbackDaMumu.Frase(r.Erro)));
        Log.Info(r.Ok ? "Aba da Mumu: feedback enviado." : $"Aba da Mumu: o feedback não saiu ({r.Erro}).");
    }
}
