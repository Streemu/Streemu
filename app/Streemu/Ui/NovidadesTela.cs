using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// A página "Novidades" (plano 027, D5): cabeçalho fixo, a lista de versões, a leitura desenhada e o cartão "Sua versão" com a Mumu. As peças mudam com a
/// largura da área da página (sem a barra lateral), medida em dp para contar o DPI e o Ctrl + / −:
/// <list type="bullet">
/// <item><b>Larga</b> (≥ 1180 dp): índice 216 · leitura (até 640) · cartão 260, o conjunto centralizado;</item>
/// <item><b>Média</b> (880 a 1179): índice · leitura, e a faixa "Você está na ..." em cima da leitura;</item>
/// <item><b>Estreita</b> (&lt; 880): fileira de pílulas (as que não cabem vão para "Mais ▾"), a faixa e a leitura, numa coluna.</item>
/// </list>
/// Só a leitura rola (e o índice, se a altura não der); a versão que estava no topo continua no topo ao mudar de disposição. Só arruma quando está visível,
/// como o <see cref="GruposTela"/>. Quem decide o conteúdo é o dono: <see cref="Mostrar"/>, <see cref="RotuloVersao"/> e o evento <see cref="ProcurarAtualizacao"/>.
/// </summary>
public sealed class NovidadesTela : UserControl
{
    public const string TextoTitulo = "Novidades";
    public const string TextoApoio = "O que mudou em cada versão do Streemu. Tudo aqui já está no seu app.";

    public enum Disposicao { Larga, Media, Estreita }

    /// <summary>Pontos de quebra pela largura da área da página, em dp.</summary>
    public const int PontoLarga = 1180, PontoMedia = 880;
    /// <summary>Medidas das colunas, em dp: a leitura nunca passa de 640 (cerca de 80 caracteres por linha).</summary>
    public const int LeituraMax = 640, ColunaIndice = NovidadesIndice.LarguraDp, ColunaCartao = 260;
    const int ArtMin = 96, LeituraMinima = 240;

    readonly NovidadesCabecalho _cabecalho = new();
    readonly NovidadesIndice _indice = new();
    readonly NovidadesPilulas _pilulas = new();
    readonly NovidadesFaixa _faixa = new();
    readonly NovidadesCartao _cartao = new();
    readonly NovidadesLeitura _leitura = new();
    readonly ArtView _mumu = new(UiBrand.PopcornMumu);
    IReadOnlyList<VersaoNovidade> _versoes = [];
    string _rotulo;
    Disposicao _disposicao = Disposicao.Estreita;
    double _k = 1;
    bool _arrumando, _faixaEscondida;

    public NovidadesTela()
    {
        Dock = DockStyle.Fill; BackColor = Theme.Surface; ForeColor = Theme.Text;
        _rotulo = Updater.CurrentLabel;
        _faixa.Rotulo = _cartao.Rotulo = _rotulo;
        _pilulas.Visible = _faixa.Visible = _cartao.Visible = _mumu.Visible = false;
        // a ordem das peças é a ordem do Tab: cabeçalho, versões, faixa/cartão, leitura
        Controls.AddRange([_cabecalho, _indice, _pilulas, _faixa, _cartao, _leitura, _mumu]);
        _indice.Escolheu += Escolher;
        _pilulas.Escolheu += Escolher;
        _leitura.VersaoNoTopoMudou += i => { _indice.Selecionada = i; _pilulas.Selecionada = i; };
        _faixa.BotaoClicado += () => ProcurarAtualizacao?.Invoke();
        _cartao.BotaoClicado += () => ProcurarAtualizacao?.Invoke();
        // fonte de cada peça muda sozinha com o Ctrl + / − (UiScale.WatchFont); a página só refaz as contas
        foreach (var c in new Control[] { _cabecalho, _indice, _pilulas, _faixa, _cartao, _leitura }) c.FontChanged += (_, _) => Arrumar();
        VisibleChanged += (_, _) => Arrumar();
        ClientSizeChanged += (_, _) => Arrumar();
        Theme.Changed += OnTema;
    }

    /// <summary>A pessoa pediu para procurar atualização (o botão do cartão ou da faixa).</summary>
    public event Action? ProcurarAtualizacao;

    /// <summary>"0.5.1 · build de 20/11 10:54": o que aparece no cartão e na faixa. Começa com <see cref="Updater.CurrentLabel"/>; o teste põe um texto fixo.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string RotuloVersao
    {
        get => _rotulo;
        set { _rotulo = value ?? ""; _faixa.Rotulo = _cartao.Rotulo = _rotulo; Arrumar(); }
    }

    public Disposicao DisposicaoAtual => _disposicao;
    /// <summary>A faixa da versão saiu porque faltava altura para a leitura. Para o teste.</summary>
    public bool FaixaEscondida => _faixaEscondida;
    public NovidadesLeitura Leitura => _leitura;
    public NovidadesIndice Indice => _indice;
    public NovidadesPilulas Pilulas => _pilulas;
    public NovidadesCartao Cartao => _cartao;
    public NovidadesFaixa Faixa => _faixa;
    public NovidadesCabecalho Cabecalho => _cabecalho;
    public ArtView Mumu => _mumu;

    /// <summary>A disposição para uma largura de área da página, em dp.</summary>
    public static Disposicao Calcular(double larguraDp) => larguraDp >= PontoLarga ? Disposicao.Larga : larguraDp >= PontoMedia ? Disposicao.Media : Disposicao.Estreita;

    /// <summary>Troca o conteúdo e volta à primeira versão. <paramref name="vistas"/> nulo = nada é marcado como novo.</summary>
    public void Mostrar(IReadOnlyList<VersaoNovidade> versoes, string versaoInstalada, IReadOnlyCollection<string>? vistas)
    {
        _versoes = versoes ?? [];
        _indice.Mostrar(_versoes, versaoInstalada, vistas);
        _pilulas.Mostrar(_versoes);
        _leitura.Mostrar(_versoes, versaoInstalada, vistas);
        Arrumar();
    }

    void Escolher(int indice)
    {
        _indice.Selecionada = indice; _pilulas.Selecionada = indice;
        _leitura.IrPara(indice);
    }

    int D(int dp) => (int)Math.Round(dp * _k);

    // Escondida, o layout mediria tudo errado (os filhos dizem Visible=false): arruma quando aparece, como o GruposTela.
    void Arrumar()
    {
        if (_arrumando || IsDisposed || !Visible) return;
        int w = ClientSize.Width, h = ClientSize.Height;
        if (w <= 0 || h <= 0) return;
        _arrumando = true;
        try
        {
            _k = UiScale.Dp(this, 10000) / 10000.0;
            int topoAntes = _leitura.VersaoNoTopo;
            var disposicao = Calcular(w / _k);
            // sem altura para a leitura, o cabeçalho perde a linha de apoio (o título fica)
            // sem altura para a leitura (menos de 240 dp): primeiro some a faixa da versão (ela e o botão continuam nas Configurações); se ainda faltar,
            // o cabeçalho perde a linha de apoio e o espaço de baixo encolhe
            _faixaEscondida = false;
            int sobra = Colocar(disposicao, w, h, false, false);
            if (sobra < D(LeituraMinima) && disposicao != Disposicao.Larga) { _faixaEscondida = true; sobra = Colocar(disposicao, w, h, false, true); }
            if (sobra < D(LeituraMinima)) Colocar(disposicao, w, h, true, _faixaEscondida);
            _disposicao = disposicao;
            if (_leitura.VersaoNoTopo != topoAntes && _versoes.Count > 0) _leitura.IrPara(topoAntes);
        }
        finally { _arrumando = false; }
    }

    /// <summary>Posiciona tudo para a disposição e devolve a altura que sobrou para a leitura.</summary>
    int Colocar(Disposicao d, int w, int h, bool soTitulo, bool semFaixa)
    {
        int pad = D(UiMetrics.PagePadding), gap = D(UiMetrics.Gap * 2), folga = D(UiMetrics.Gap);
        int indiceW = D(ColunaIndice), cartaoW = D(ColunaCartao), leituraMax = D(LeituraMax);
        int leituraW, total;
        switch (d)
        {
            case Disposicao.Larga:
                leituraW = Math.Max(1, Math.Min(leituraMax, w - 2 * pad - indiceW - cartaoW - 2 * gap));
                total = indiceW + gap + leituraW + gap + cartaoW;
                break;
            case Disposicao.Media:
                leituraW = Math.Max(1, Math.Min(leituraMax, w - 2 * pad - indiceW - gap));
                total = indiceW + gap + leituraW;
                break;
            default:
                leituraW = Math.Max(1, Math.Min(leituraMax, w - 2 * pad));
                total = leituraW;
                break;
        }
        int x0 = Math.Max(0, (w - total) / 2);
        int topo = _cabecalho.Definir(x0, total, soTitulo);
        int baixo = Math.Max(topo + 1, h - pad), livre = baixo - topo;

        SuspendLayout();
        _cabecalho.SetBounds(0, 0, w, topo);
        _indice.Visible = d != Disposicao.Estreita;
        _cartao.Visible = d == Disposicao.Larga;
        _faixa.Visible = d != Disposicao.Larga && !semFaixa;
        _pilulas.Visible = d == Disposicao.Estreita;
        int leituraX = d == Disposicao.Estreita ? x0 : x0 + indiceW + gap, leituraY = topo, leituraH;
        bool mumu = false;
        switch (d)
        {
            case Disposicao.Larga:
            {
                _indice.SetBounds(x0, topo, indiceW, livre);
                int cx = leituraX + leituraW + gap;
                int cartaoH = Math.Min(_cartao.AlturaPara(cartaoW), livre);
                _cartao.SetBounds(cx, topo, cartaoW, cartaoH);
                int arte = Math.Min(cartaoW, baixo - (_cartao.Bottom + folga));
                if (arte >= D(ArtMin)) { _mumu.SetBounds(cx + (cartaoW - arte) / 2, _cartao.Bottom + folga, arte, arte); mumu = true; }
                leituraH = livre;
                break;
            }
            case Disposicao.Media:
            {
                _indice.SetBounds(x0, topo, indiceW, livre);
                int faixaH = semFaixa ? 0 : Math.Min(_faixa.AlturaPara(leituraW), livre);
                if (!semFaixa) _faixa.SetBounds(leituraX, topo, leituraW, faixaH);
                leituraY = topo + (semFaixa ? 0 : faixaH + folga);
                leituraH = baixo - leituraY;
                break;
            }
            default:
            {
                int pilH = _pilulas.AlturaPedida;
                _pilulas.SetBounds(x0, topo, leituraW, pilH);
                int faixaH = semFaixa ? 0 : _faixa.AlturaPara(leituraW);
                if (!semFaixa) _faixa.SetBounds(x0, topo + pilH + folga, leituraW, faixaH);
                leituraY = topo + pilH + folga + (semFaixa ? 0 : faixaH + folga);
                leituraH = baixo - leituraY;
                break;
            }
        }
        _mumu.Visible = mumu;
        leituraH = Math.Max(1, leituraH);
        _leitura.SetBounds(leituraX, leituraY, leituraW, leituraH);
        ResumeLayout(false);
        return leituraH;
    }

    void OnTema() { if (!IsDisposed) Invalidate(true); }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Arrumar(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Arrumar(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnTema;
        base.Dispose(disposing);
    }
}
