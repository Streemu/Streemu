using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>Um amigo online que pode receber: o id da sala, o apelido e o índice da cor do avatar.</summary>
public sealed record AmigoParaArquivo(string Id, string Apelido, int Cor);

/// <summary>Uma linha de "Em andamento": o pedido e o que a linha mostra.</summary>
public sealed record PedidoNaTela(string Pedido, DadosDaLinha Dados);

/// <summary>
/// A seção "Enviar arquivo" (spec 020, mockups 1 e 3): aba Enviar (para quem, arquivos, enviar, em andamento) e aba Histórico.
/// Só tela: não abre ligação nem lê o histórico. Recebe os dados prontos e devolve as escolhas pelos eventos.
/// Uso: <c>view.EnviarPedido += (amigo, arquivos) => ...; view.MostrarAmigos(amigos); view.MostrarAndamento(pedidos);</c>
/// </summary>
public sealed class EnviarArquivoView : UserControl
{
    const int MaxLargura = 880, MaxArquivos = 20;
    readonly Label _titulo = new() { Text = "Enviar arquivo", AutoSize = true };
    readonly Segmented _abas = new("Enviar", "Histórico");
    readonly Panel _pagina = new() { AutoScroll = true };
    readonly Label _paraTitulo = Secao("Para quem"), _arquivosTitulo = Secao("Arquivos"), _andamentoTitulo = Secao("Em andamento");
    readonly SeletorDeAmigo _seletor = new();
    readonly AreaDeSoltar _soltar = new();
    readonly Label _total = new() { AutoSize = true, ForeColor = Theme.TextMuted, Visible = false };
    readonly Button _enviar = new() { AutoSize = true, Text = "Enviar arquivos", AccessibleName = "Enviar arquivos" };
    readonly Label _aviso = new() { AutoSize = true, ForeColor = Theme.Warn, Visible = false };
    readonly Panel _linha = new() { AccessibleRole = AccessibleRole.Separator, BackColor = Theme.Border }; // o filete entre a escolha e a pasta de recebidos
    // pintada na hora com reticências: um caminho longo empurrava "Alterar pasta" para fora e abria a rolagem horizontal
    readonly PastaDeRecebidos _pasta = new();
    readonly Button _trocarPasta = new() { Text = "Alterar pasta", AutoSize = true, AccessibleName = "Alterar a pasta de recebidos" };
    readonly List<LinhaDeArquivo> _escolhidos = new(), _andamento = new();
    readonly List<string> _caminhos = new();
    readonly Font _fonteTitulo = UiTypography.TitleFont();
    IReadOnlyList<AmigoParaArquivo> _listaAmigos = [];
    IReadOnlyList<PedidoNaTela> _pedidos = [];
    string? _amigo;

    public HistoricoArquivosView Historico { get; } = new() { Visible = false };
    public event Action<string, IReadOnlyList<string>>? EnviarPedido;
    public event Action<string, string>? AcaoNoPedido;
    public event Action? TrocarPasta;

    public EnviarArquivoView()
    {
        Dock = DockStyle.Fill; BackColor = Theme.Surface; ForeColor = Theme.Text; AllowDrop = true;
        _titulo.Font = _fonteTitulo;
        EstilizarBotoes();
        _pagina.Controls.AddRange([_paraTitulo, _seletor, _arquivosTitulo, _soltar, _total, _enviar, _aviso, _andamentoTitulo, _linha, _pasta, _trocarPasta]);
        ScrollSkin.Attach(_pagina);
        Controls.AddRange([_titulo, _abas, _pagina, Historico]);
        Theme.Changed += EstilizarBotoes;
        // idem: o Theme.Apply da janela refaz os botões como neutros
        _enviar.BackColorChanged += (_, _) => { if (_enviar.BackColor != (_enviar.Enabled ? Theme.Accent : Theme.Neutral)) EstilizarBotoes(); };
        _enviar.ForeColorChanged += (_, _) => { if (_enviar.ForeColor != (_enviar.Enabled ? Theme.AccentText : Theme.NeutralText)) EstilizarBotoes(); };
        Ligar();
    }

    /// <summary>"Enviar arquivos" só é roxo quando dá para enviar (como no desenho); apagado, fica neutro. Refeito quando o tema muda.</summary>
    void EstilizarBotoes()
    {
        Theme.StyleButton(_trocarPasta, false);
        Theme.StyleButton(_enviar, _enviar.Enabled);
        _linha.BackColor = Theme.Border;
    }

    static Label Secao(string texto) => new() { Text = texto, AutoSize = true, Font = UiTypography.Create(UiTypography.Section, FontStyle.Bold), ForeColor = Theme.Text };

    void Ligar()
    {
        _abas.SelectedChanged += _ => { _pagina.Visible = _abas.Selected == 0; Historico.Visible = _abas.Selected == 1; Arrumar(); };
        _soltar.Escolher.Click += (_, _) => EscolherPeloDialogo();
        _seletor.Escolheu += id => { _amigo = id; MontarAmigos(); AtualizarEnvio(); };
        _enviar.Click += (_, _) => { if (_amigo != null && _caminhos.Count > 0) EnviarPedido?.Invoke(_amigo, _caminhos.ToList()); };
        _trocarPasta.Click += (_, _) => TrocarPasta?.Invoke();
        foreach (Control alvo in new Control[] { this, _soltar, _pagina })
        {
            alvo.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
            alvo.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] soltos) AdicionarArquivos(soltos); };
        }
        Resize += (_, _) => Arrumar();
        _pagina.Resize += (_, _) => Arrumar();
        _pagina.ClientSizeChanged += (_, _) => Arrumar(); // a barra vertical aparecendo muda só a área útil
    }

    /// <summary>Abre já na aba pedida (0 = Enviar, 1 = Histórico).</summary>
    public void AbrirAba(int aba) => _abas.Selected = aba;

    /// <summary>"Enviar arquivo" no menu da foto: abre com esse amigo escolhido.</summary>
    public void EscolherAmigo(string id) { _amigo = id; MontarAmigos(); AtualizarEnvio(); }

    public void MostrarAmigos(IReadOnlyList<AmigoParaArquivo> amigos)
    {
        if (amigos.SequenceEqual(_listaAmigos)) return;
        _listaAmigos = amigos;
        if (_amigo != null && amigos.All(a => a.Id != _amigo)) _amigo = null;
        MontarAmigos(); AtualizarEnvio();
    }

    void MontarAmigos()
    {
        _seletor.Mostrar(_listaAmigos, _amigo);
        // sem ninguém online o seletor fica como está ("Escolher amigo", sem lista): o texto avulso embaixo era ruído
        Arrumar();
    }

    public void AdicionarArquivos(IEnumerable<string> caminhos)
    {
        var pastas = caminhos.Where(Directory.Exists).ToList();
        foreach (var c in caminhos.Where(File.Exists).Where(c => !_caminhos.Contains(c, StringComparer.OrdinalIgnoreCase)))
            if (_caminhos.Count < MaxArquivos) _caminhos.Add(c);
        Avisar(pastas.Count > 0 ? "Pastas inteiras ainda não vão: compacte a pasta (.zip) e mande o arquivo." : null);
        MontarEscolhidos(); AtualizarEnvio();
    }

    /// <summary>A oferta saiu: a lista volta vazia para o próximo envio.</summary>
    public void LimparEscolhidos() { _caminhos.Clear(); MontarEscolhidos(); AtualizarEnvio(); }

    /// <summary>Motivo de algo não ter saído (amigo saiu, arquivo vazio...); nulo esconde.</summary>
    public void Avisar(string? texto) { _aviso.Text = texto ?? ""; _aviso.Visible = texto != null; Arrumar(); }

    public void MostrarPasta(string pasta) => _pasta.Mostrar(pasta);

    void EscolherPeloDialogo()
    {
        using var dlg = new OpenFileDialog { Multiselect = true, Title = "Escolher arquivos para enviar" };
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK) AdicionarArquivos(dlg.FileNames);
    }

    void MontarEscolhidos()
    {
        AjustarLinhas(_escolhidos, _caminhos.Count, i => _caminhos.RemoveAt(i), (linha, i) => linha.Mostrar(new DadosDaLinha(null, 0,
            Path.GetFileName(_caminhos[i]), TextoDoTamanho(TamanhoDe(_caminhos[i])), null, TomDoSelo.Nenhum, null, [new AcaoDaLinha("Tirar", false, "tirar")])));
        Arrumar();
    }

    void AtualizarEnvio()
    {
        _enviar.Enabled = _listaAmigos.Any(a => a.Id == _amigo) && _caminhos.Count > 0;
        Theme.StyleButton(_enviar, _enviar.Enabled);
        _total.Visible = _caminhos.Count > 0; // sem arquivo o desenho não leva texto nenhum ao lado do botão
        _total.Text = $"{_caminhos.Count} arquivo{(_caminhos.Count > 1 ? "s" : "")} · {TextoDoTamanho(_caminhos.Sum(TamanhoDe))}";
        Arrumar();
    }

    public void MostrarAndamento(IReadOnlyList<PedidoNaTela> pedidos)
    {
        _pedidos = pedidos;
        AjustarLinhas(_andamento, pedidos.Count, null, (linha, i) => linha.Mostrar(pedidos[i].Dados));
        _andamentoTitulo.Visible = pedidos.Count > 0;
        Arrumar();
    }

    /// <summary>Deixa a lista com <paramref name="quantas"/> linhas (reaproveitando as que existem) e preenche cada uma.</summary>
    void AjustarLinhas(List<LinhaDeArquivo> linhas, int quantas, Action<int>? tirar, Action<LinhaDeArquivo, int> preencher)
    {
        while (linhas.Count > quantas) { var l = linhas[^1]; linhas.RemoveAt(linhas.Count - 1); _pagina.Controls.Remove(l); l.Dispose(); }
        while (linhas.Count < quantas)
        {
            var linha = new LinhaDeArquivo();
            int indice = linhas.Count;
            if (tirar != null) linha.Acao += _ => { tirar(indice); MontarEscolhidos(); AtualizarEnvio(); };
            else linha.Acao += codigo => { if (indice < _pedidos.Count) AcaoNoPedido?.Invoke(_pedidos[indice].Pedido, codigo); };
            linhas.Add(linha); _pagina.Controls.Add(linha);
        }
        for (int i = 0; i < quantas; i++) preencher(linhas[i], i);
    }

    static long TamanhoDe(string caminho) { try { return new FileInfo(caminho).Length; } catch (IOException) { return 0; } }

    /// <summary>MB e GB decimais, como o resto do app. Ex.: <c>TextoDoTamanho(1_200_000_000)</c> → "1,2 GB".</summary>
    public static string TextoDoTamanho(long bytes) => bytes >= 1_000_000_000 ? $"{bytes / 1e9:N1} GB" : bytes >= 1_000_000 ? $"{bytes / 1e6:N1} MB" : $"{Math.Max(1, bytes / 1000)} KB";

    bool _arrumando;

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) Arrumar();
    }

    // mudar a posição dos controles muda a área útil, que chama o Arrumar de novo: uma volta só por vez
    void Arrumar()
    {
        // Escondida, os filhos dizem Visible=false e o layout pularia quase todos (a pasta e o botão iam para o topo, sobre o "Para quem", e ficavam
        // assim ao abrir a aba, porque o tamanho não muda). Escondida não arruma: o OnVisibleChanged arruma quando a tela aparece. (vídeo de 2026-11-18)
        if (_arrumando || IsDisposed || !Visible) return;
        _arrumando = true;
        try { ArrumarTudo(); } finally { _arrumando = false; }
    }

    void ArrumarTudo()
    {
        int pad = UiScale.Dp(this, UiMetrics.PagePadding), gap = UiScale.Dp(this, UiMetrics.Gap);
        _titulo.Location = new Point(pad, pad);
        var abas = _abas.GetPreferredSize(Size.Empty);
        _abas.SetBounds(Math.Max(pad, ClientSize.Width - pad - abas.Width), pad + (_titulo.PreferredHeight - abas.Height) / 2, abas.Width, abas.Height);
        int topo = pad + Math.Max(_titulo.PreferredHeight, abas.Height) + gap;
        var area = new Rectangle(0, topo, ClientSize.Width, Math.Max(1, ClientSize.Height - topo));
        _pagina.Bounds = area; Historico.Bounds = area;
        ArrumarPagina(pad, gap);
    }

    void ArrumarPagina(int pad, int gap)
    {
        // reserva a barra vertical antes de ela aparecer: senão ela rouba a largura depois do layout e abre a rolagem horizontal
        int reserva = _pagina.VerticalScroll.Visible ? 0 : SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi);
        int largura = Math.Min(UiScale.Dp(this, MaxLargura), Math.Max(1, _pagina.ClientSize.Width - 2 * pad - reserva)), y = _pagina.AutoScrollPosition.Y;
        void Por(Control c, int altura) { if (!c.Visible) return; c.SetBounds(pad, y, largura, altura); y += altura + gap; }
        void Texto(Control c) { if (!c.Visible) return; c.Location = new Point(pad, y); y += c.PreferredSize.Height + gap / 2; }
        Texto(_paraTitulo);
        _seletor.Size = _seletor.GetPreferredSize(Size.Empty); _seletor.Location = new Point(pad, y); y += _seletor.Height + gap;
        Texto(_arquivosTitulo);
        Por(_soltar, _soltar.AlturaPadrao);
        foreach (var l in _escolhidos) Por(l, l.Height);
        Por(_linha, 1);
        y = ArrumarRodape(pad, gap, largura, y);
        Texto(_aviso);
        Texto(_andamentoTitulo);
        foreach (var l in _andamento) Por(l, l.Height);
        _pagina.AutoScrollMinSize = new Size(0, y + pad - _pagina.AutoScrollPosition.Y);
    }

    /// <summary>A pasta de recebidos com "Alterar pasta" à direita e, embaixo, o botão de enviar (com o total dos arquivos à esquerda quando há).</summary>
    int ArrumarRodape(int pad, int gap, int largura, int y)
    {
        int direita = pad + largura;
        _trocarPasta.Location = new Point(direita - _trocarPasta.Width, y + (_pasta.AlturaPadrao - _trocarPasta.Height) / 2);
        _pasta.SetBounds(pad, y, Math.Max(1, _trocarPasta.Left - gap - pad), _pasta.AlturaPadrao);
        y += _pasta.AlturaPadrao + gap;
        _enviar.MinimumSize = new Size(UiScale.Dp(this, 170), UiScale.Dp(this, 46));
        _enviar.Location = new Point(direita - _enviar.Width, y);
        _total.Location = new Point(pad, y + (_enviar.Height - _total.PreferredHeight) / 2);
        return y + _enviar.Height + gap;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Theme.Changed -= EstilizarBotoes; _fonteTitulo.Dispose(); }
        base.Dispose(disposing);
    }
}
