using System.Diagnostics;
using Streemu.Arquivos;
using Streemu.Ui;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// A tela do Enviar arquivo (spec 020, T6) ligada ao fluxo de MainForm.Arquivos.cs: a seção na barra lateral, os cards no Início, o
/// aviso de pedido novo e as ações dos botões. Os textos ficam em Arquivos/TextoDoPedido.cs. Só existe com STREEMU_ARQUIVOS=1.
/// </summary>
public partial class MainForm
{
    readonly Panel _arquivosHost = new() { Dock = DockStyle.Fill, Visible = false };
    EnviarArquivoView? _arquivosTela;
    bool _arquivosView, _arquivosSujo = true;
    readonly Dictionary<string, PedidoArquivoCard> _cardsArquivo = new();
    readonly HashSet<string> _pedidosAvisados = new();
    IReadOnlyList<ItemHistorico> _historicoRecebidos = [], _historicoEnviados = [];
    // o download avisa a cada pedaço de 1 MB (dezenas de vezes por segundo no loopback): a tela redesenha no máximo 4 vezes por segundo
    readonly System.Windows.Forms.Timer _arquivosRedesenho = new() { Interval = 250 };

    void InitArquivosTela()
    {
        _side.ArquivosVisivel = ArquivosLigado;
        if (!ArquivosLigado) return;
        _side.ArquivosClicked += () => MostrarArquivos();
        ArquivosMudaram += () => _arquivosSujo = true;
        _arquivosRedesenho.Tick += (_, _) => AtualizarTelaArquivos();
        _arquivosRedesenho.Start();
    }

    /// <summary>
    /// A tela nasce no primeiro clique, como a do Cinema: o Theme.Apply da janela repinta tudo o que já existe (os botões de destaque
    /// viravam neutros e o fundo mudava), então criada antes dele ela perdia o desenho.
    /// </summary>
    EnviarArquivoView TelaDeArquivos()
    {
        if (_arquivosTela != null) return _arquivosTela;
        var tela = _arquivosTela = new EnviarArquivoView();
        _arquivosHost.BackColor = Theme.Surface;
        _arquivosHost.Controls.Add(tela);
        tela.EnviarPedido += (amigo, arquivos) => _ = EnviarDaTelaAsync(amigo, arquivos);
        tela.AcaoNoPedido += (pedido, codigo) => _ = AcaoNoPedidoAsync(pedido, codigo);
        tela.TrocarPasta += TrocarPastaDeRecebidos;
        tela.Historico.Acao += AcaoNoHistorico;
        tela.Historico.LimparPedido += LimparHistoricoArquivos;
        return tela;
    }

    /// <summary>Abre a seção; com <paramref name="amigoId"/> (menu da foto), já com ele escolhido.</summary>
    void MostrarArquivos(string? amigoId = null)
    {
        if (!ArquivosLigado) return;
        var tela = TelaDeArquivos();
        HideFlyout();
        if (_preparationBusy) return;
        _preparationView = _stageView = _settingsView = _filmeView = false;
        _arquivosView = true;
        _gruposView = false;
        if (amigoId != null) { tela.AbrirAba(0); tela.EscolherAmigo(amigoId); }
        _arquivosSujo = true;
        AtualizarTelaArquivos();
        ApplyView();
    }

    // ------------------------------------------------------------------ redesenho

    int _arquivosTicks;

    void AtualizarTelaArquivos()
    {
        bool amigos = ++_arquivosTicks % 4 == 0; // a lista de amigos muda devagar: 1 vez por segundo basta
        if (!_arquivosSujo && !amigos) return;
        _arquivosSujo = false;
        var agora = DateTime.Now;
        var pedidos = PedidosArquivo.OrderBy(p => p.Criado).ToList();
        if (_arquivosTela is { } tela) // os cards do Início e a barra valem mesmo antes de a tela existir
        {
            tela.MostrarAmigos((_room?.Friends ?? []).OrderBy(f => NomeNaTela(f)).Select(f => new AmigoParaArquivo(f.Id, NomeNaTela(f), TextoDoPedido.Cor(f.Id))).ToList());
            tela.MostrarAndamento(pedidos.Select(p => new PedidoNaTela(p.Pedido, TextoDoPedido.Linha(p, agora))).ToList());
            tela.MostrarPasta(HistoricoArquivos.PastaRecebidos);
            AtualizarHistoricoNaTela(tela, agora);
        }
        AtualizarCardsArquivo(pedidos, agora);
        AvisarPedidosNovos(pedidos);
        int pendentes = pedidos.Count(TextoDoPedido.EsperaPorMim);
        _side.ArquivosPendentes = pendentes;
        if (_flyout != null) _flyout.Panel.ArquivosPendentes = pendentes;
    }

    void AtualizarHistoricoNaTela(EnviarArquivoView tela, DateTime agora)
    {
        var itens = HistoricoArquivos.Itens;
        _historicoRecebidos = itens.Where(i => i.Sentido == Sentido.Recebido).ToList();
        _historicoEnviados = itens.Where(i => i.Sentido == Sentido.Enviado).ToList();
        tela.Historico.Mostrar(_historicoRecebidos.Select(i => TextoDoPedido.LinhaDoHistorico(i, agora)).ToList(),
            _historicoEnviados.Select(i => TextoDoPedido.LinhaDoHistorico(i, agora)).ToList());
    }

    /// <summary>Um card no Início para cada pedido que espera por mim (aceitar, mandar de novo, cota do relay), depois dos convites.</summary>
    void AtualizarCardsArquivo(IReadOnlyList<PedidoEmCurso> pedidos, DateTime agora)
    {
        string pasta = HistoricoArquivos.PastaRecebidos;
        var cards = new List<Control>();
        foreach (var p in pedidos)
        {
            if (TextoDoPedido.Card(p, pasta, agora) is not { } dados) continue;
            if (!_cardsArquivo.TryGetValue(p.Pedido, out var card)) _cardsArquivo[p.Pedido] = card = NovoCardDeArquivo();
            card.Mostrar(dados);
            cards.Add(card);
        }
        foreach (var velho in _cardsArquivo.Where(kv => !cards.Contains(kv.Value)).ToList()) { _cardsArquivo.Remove(velho.Key); velho.Value.Dispose(); }
        _conviteCards.MostrarExtras(cards);
    }

    PedidoArquivoCard NovoCardDeArquivo()
    {
        var card = new PedidoArquivoCard();
        card.Acao += (pedido, codigo) => _ = AcaoNoPedidoAsync(pedido, codigo);
        card.TrocarPasta += TrocarPastaDeRecebidos;
        return card;
    }

    /// <summary>Pedido novo esperando por mim: balão e som, que o silêncio cala (D7); o card no Início aparece de qualquer jeito.</summary>
    void AvisarPedidosNovos(IReadOnlyList<PedidoEmCurso> pedidos)
    {
        foreach (var p in pedidos.Where(TextoDoPedido.EsperaPorMim))
        {
            if (!_pedidosAvisados.Add(p.Pedido + p.Fase)) continue;
            MostrarBalao(6000, UiBrand.Name, TextoDoPedido.Aviso(p), ToolTipIcon.Info);
            TocarSom(Sons.Som.ConviteRecebido);
        }
        _pedidosAvisados.RemoveWhere(k => !pedidos.Any(p => k == p.Pedido + p.Fase));
    }

    // ------------------------------------------------------------------ ações

    async Task EnviarDaTelaAsync(string amigoId, IReadOnlyList<string> arquivos)
    {
        string? erro = await OferecerArquivosAsync(amigoId, arquivos);
        _arquivosTela?.Avisar(erro);
        if (erro == null) _arquivosTela?.LimparEscolhidos();
    }

    async Task AcaoNoPedidoAsync(string pedido, string codigo)
    {
        string? erro = null;
        switch (codigo)
        {
            case TextoDoPedido.Aceitar: erro = await AceitarPedidoArquivoAsync(pedido); break;
            case TextoDoPedido.Mandar: erro = await MandarDeNovoAsync(pedido); break;
            case TextoDoPedido.Recusar: RecusarPedidoArquivo(pedido); break;
            case TextoDoPedido.Cancelar: CancelarPedidoArquivo(pedido); break;
            case TextoDoPedido.RelaySim or TextoDoPedido.RelayNao: ResponderRelayArquivo(pedido, codigo == TextoDoPedido.RelaySim); break;
            case TextoDoPedido.Abrir or TextoDoPedido.Pasta:
                if (_pedidosArquivo.TryGetValue(pedido, out var p) && p.Caminhos.Values.FirstOrDefault() is string c) AbrirOuMostrar(c, codigo == TextoDoPedido.Pasta);
                break;
        }
        if (erro != null) { _arquivosTela?.Avisar(erro); Notify(erro); }
        _arquivosSujo = true;
    }

    void AcaoNoHistorico(bool recebido, int indice, string codigo)
    {
        var lista = recebido ? _historicoRecebidos : _historicoEnviados;
        if (indice < 0 || indice >= lista.Count) return;
        var item = lista[indice];
        if (codigo == TextoDoPedido.DeNovo) { _ = PedirDeNovoDaTelaAsync(item); return; }
        if (item.Caminho != null) AbrirOuMostrar(item.Caminho, codigo == TextoDoPedido.Pasta);
    }

    async Task PedirDeNovoDaTelaAsync(ItemHistorico item)
    {
        if (await PedirArquivoDeNovoAsync(item) is string erro) Notify(erro);
        else Notify($"Pedido de novo a {item.Apelido}. Quando ele mandar, chega sozinho.");
    }

    /// <summary>Abrir o arquivo (o Windows pergunta pelo SmartScreen se for um programa: ele tem a marca da internet) ou mostrá-lo na pasta.</summary>
    void AbrirOuMostrar(string caminho, bool naPasta)
    {
        if (!File.Exists(caminho)) { Notify("Esse arquivo saiu da pasta ou foi apagado."); return; }
        try
        {
            if (naPasta) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{caminho}\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(caminho) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception e) { Notify($"O Windows não abriu o arquivo ({e.Message})."); }
    }

    void TrocarPastaDeRecebidos()
    {
        using var dlg = new FolderBrowserDialog { Description = "Onde guardar os arquivos recebidos?", SelectedPath = HistoricoArquivos.PastaRecebidos, UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        HistoricoArquivos.PastaRecebidos = dlg.SelectedPath;
        _arquivosSujo = true;
    }

    void LimparHistoricoArquivos()
    {
        var r = MessageBox.Show(this, "Limpar o histórico de arquivos? Os arquivos recebidos continuam na pasta.", UiBrand.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;
        HistoricoArquivos.Limpar();
        _arquivosSujo = true;
    }
}
