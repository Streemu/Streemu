using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// A leitura da tela Novidades (plano 027, D6): uma lista de cartões, um por versão, desenhada à mão num controle só. São umas 25 versões e 200 itens; um
/// Label por item pesaria e piscaria. O controle ocupa a largura que o pai der (quem limita a 640 dp é a página), mede cada cartão uma vez por
/// (largura, fonte, escala) e pinta só os cartões que cruzam a área visível. Nunca há rolagem horizontal: o texto quebra palavra por palavra e, se uma
/// palavra não couber, no caractere. Ao mudar a largura ou a fonte a versão que estava no topo continua no topo.
/// As cores são lidas de <see cref="Theme"/> na hora de pintar: a troca de tema só precisa repintar.
/// </summary>
public sealed class NovidadesLeitura : Panel
{
    const string TituloNaoLancado = "Chegou nas últimas atualizações", ChipSuaVersao = "Sua versão", ChipNaoLancado = "Nesta versão do app", ChipObrigatoria = "Atualização obrigatória";
    const string VazioTitulo = "Nada de novo por aqui", VazioDica = "As novidades de cada versão aparecem aqui.";
    // medidas em dp (96 DPI); o controle converte uma vez por medição
    const int PadCartao = 18, EntreCartoes = 16, IndentItem = 20, Ponto = 7, Halo = 13, ArtMax = 180, ArtMin = 96, EntreChips = 6, HaloAlpha = 70;
    const TextFormatFlags Quebra = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;
    const TextFormatFlags UmaLinha = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
    const int WM_VSCROLL = 0x115, WM_MOUSEWHEEL = 0x20A;
    static readonly CultureInfo Cultura = CultureInfo.GetCultureInfo("pt-BR");

    enum Fonte { Titulo, TituloMono, Corpo, Item, Detalhe, Legenda, Chip, Vazio }
    enum Cor { Texto, Suave, Legenda }
    enum Tipo { Texto, Chip, Ponto }
    enum EstiloChip { Ok, Neutro, Aviso, Destaque }

    sealed class Peca
    {
        public Tipo Tipo; public Rectangle R; public string Texto = ""; public Fonte Fonte; public Cor Cor; public EstiloChip Estilo; public bool Novo;
    }

    sealed class Cartao
    {
        public int Top, Height, Indice, Novas;
        public bool Atual;
        public readonly List<Peca> Pecas = new();
        public int Bottom => Top + Height;
    }

    IReadOnlyList<VersaoNovidade> _versoes = [];
    string _instalada = "";
    IReadOnlyCollection<string>? _vistas;
    ISet<string>? _vistasSet;

    readonly Dictionary<Fonte, Font> _fontes = new();
    readonly List<Cartao> _cartoes = new();
    (int Largura, float Fonte, int Escala)? _chave;
    double _k = 1;
    int _largura, _linha;
    bool _arrumando;

    // âncora de rolagem: a versão do topo e a fração dela já rolada; sobrevive às remedidas (o ScrollableControl pode limitar a posição no meio)
    int _ancoraIndice;
    double _ancoraFracao;
    int _ultimoTopo = -1;
    (int Indice, int Y)? _fixada;

    Rectangle _vazioArt, _vazioTitulo, _vazioDica;
    readonly ArtView _mumu = new(UiBrand.SleepingMumu);
    List<ItemAcessivel> _acessiveis = new();

    public NovidadesLeitura()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Surface;
        TabStop = true;
        AutoScroll = true;
        AccessibleName = "Novidades";
        AccessibleRole = AccessibleRole.List;
        _mumu.Visible = false;
        Controls.Add(_mumu);
        ScrollSkin.Attach(this);
        UiScale.WatchFont(this);
        Theme.Changed += OnTema;
    }

    /// <summary>As versões mostradas agora.</summary>
    public IReadOnlyList<VersaoNovidade> Versoes => _versoes;

    /// <summary>Índice da versão cujo cartão está no topo da área visível (0 se não há nenhuma).</summary>
    public int VersaoNoTopo
    {
        get
        {
            if (_cartoes.Count == 0) return 0;
            int y = ScrollY;
            if (_fixada is { } f && f.Y == y) return f.Indice;
            return IndiceEm(y, out _);
        }
    }

    /// <summary>Avisa que outra versão passou a ser a do topo (rolagem do mouse, da barra, do teclado ou <see cref="IrPara"/>).</summary>
    public event Action<int>? VersaoNoTopoMudou;

    /// <summary>
    /// Troca o conteúdo e volta ao topo. <paramref name="vistas"/> nulo = nada é marcado como novo; os itens fora dela ganham a bolinha de destaque
    /// e a versão ganha o chip "N novidades".
    /// </summary>
    public void Mostrar(IReadOnlyList<VersaoNovidade> versoes, string versaoInstalada, IReadOnlyCollection<string>? vistas)
    {
        _versoes = versoes ?? [];
        _instalada = versaoInstalada ?? "";
        _vistas = vistas;
        _vistasSet = vistas as ISet<string> ?? (vistas == null ? null : new HashSet<string>(vistas));
        _cartoes.Clear();
        _chave = null;
        _ancoraIndice = 0; _ancoraFracao = 0; _fixada = null; _ultimoTopo = -1;
        _acessiveis = _versoes.Select((_, i) => new ItemAcessivel(this, i)).ToList();
        Arrumar();
        if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
        Invalidate();
    }

    /// <summary>Rola até o topo do cartão da versão <paramref name="indice"/>, sem animação.</summary>
    public void IrPara(int indice)
    {
        if (_versoes.Count == 0) return;
        indice = Math.Clamp(indice, 0, _versoes.Count - 1);
        _ancoraIndice = indice; _ancoraFracao = 0;
        if (_cartoes.Count == 0) { Arrumar(); if (_cartoes.Count == 0) return; }
        RolarPara(_cartoes[indice].Top);
        _fixada = (indice, ScrollY);
        Notificar(indice);
    }

    /// <summary>O texto da versão para a área de transferência e para o leitor de tela (versão, data, resumo e os itens por grupo).</summary>
    public static string TextoDaVersao(VersaoNovidade v)
    {
        var sb = new StringBuilder();
        string data = DataPorExtenso(v.Data);
        sb.Append(v.NaoLancado ? TituloNaoLancado : "Versão " + v.Versao);
        if (data.Length > 0) sb.Append(", ").Append(data);
        sb.AppendLine();
        if (v.Resumo.Length > 0) sb.AppendLine(v.Resumo);
        if (v.Obrigatoria) sb.AppendLine(ChipObrigatoria);
        sb.Append(TextoDosItens(v));
        return sb.ToString().TrimEnd();
    }

    static string TextoDosItens(VersaoNovidade v)
    {
        var sb = new StringBuilder();
        string grupo = "";
        foreach (var i in v.Itens)
        {
            if (i.Grupo.Length > 0 && i.Grupo != grupo) { sb.AppendLine().AppendLine(i.Grupo.ToUpper(Cultura)); grupo = i.Grupo; }
            sb.Append("- ").AppendLine(i.Titulo);
            if (i.Detalhe.Length > 0) sb.Append("  ").AppendLine(Maiuscula(i.Detalhe));
        }
        return sb.ToString().Trim();
    }

    /// <summary>Os retângulos pintados (coordenadas do conteúdo, medidas para a largura de agora): cada cartão seguido dos blocos de texto e chips dele. Para o teste.</summary>
    public IReadOnlyList<Rectangle> RetangulosPintados()
    {
        Arrumar();
        var lista = new List<Rectangle>();
        foreach (var c in _cartoes)
        {
            lista.Add(new Rectangle(0, c.Top, _largura, c.Height));
            foreach (var p in c.Pecas) lista.Add(p.R);
        }
        return lista;
    }

    /// <summary>Só os retângulos dos cartões (coordenadas do conteúdo). Para o teste.</summary>
    public IReadOnlyList<Rectangle> RetangulosDosCartoes()
    {
        Arrumar();
        return _cartoes.Select(c => new Rectangle(0, c.Top, _largura, c.Height)).ToList();
    }

    /// <summary>A Mumu do estado vazio está à mostra. Para o teste.</summary>
    public bool MumuVisivel => _mumu.Visible && _mumu.Width > 0 && _mumu.Height > 0;

    // ---------- medida ----------

    /// <summary>No arquivo o detalhe vem depois de "Título:", então começa em minúscula; na tela ele abre a própria linha.</summary>
    static string Maiuscula(string texto) => texto.Length > 0 && char.IsLower(texto[0]) ? char.ToUpper(texto[0], Cultura) + texto[1..] : texto;

    static string DataPorExtenso(DateOnly? data) => data is { } d ? d.ToString("d 'de' MMMM 'de' yyyy", Cultura) : "";

    int D(int dp) => (int)Math.Round(dp * _k);

    int ScrollY => -AutoScrollPosition.Y;

    int LarguraDeLayout() => Math.Max(0, Width - SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi));

    void RecriarFontes()
    {
        foreach (var f in _fontes.Values) f.Dispose();
        _fontes.Clear();
        float r = Font.Size / UiTypography.Body;
        _fontes[Fonte.Titulo] = UiTypography.CreateSemibold(UiTypography.Heading * r);
        _fontes[Fonte.TituloMono] = UiTypography.CreateMono(UiTypography.Heading * r);
        _fontes[Fonte.Corpo] = (Font)Font.Clone();
        _fontes[Fonte.Item] = UiTypography.CreateSemibold(Font.Size);
        _fontes[Fonte.Detalhe] = UiTypography.Create(UiTypography.Caption * r);
        _fontes[Fonte.Legenda] = UiTypography.Create(UiTypography.Note * r, FontStyle.Bold);
        _fontes[Fonte.Chip] = UiTypography.Create(UiTypography.Note * r, FontStyle.Bold);
        _fontes[Fonte.Vazio] = UiTypography.CreateSemibold(UiTypography.EmptyState * r);
    }

    Font F(Fonte f) => _fontes[f];

    /// <summary>Mede tudo de novo quando mudou a largura, a fonte ou a escala; guarda a âncora de rolagem antes e volta a ela depois.</summary>
    void Arrumar()
    {
        if (_arrumando || IsDisposed) return;
        int largura = LarguraDeLayout();
        if (largura < 40 || !IsHandleCreated && Width <= 0) return;
        _k = UiScale.Dp(this, 10000) / 10000.0;
        var chave = (largura, Font.Size, (int)Math.Round(_k * 1000));
        if (_chave == chave && (_cartoes.Count == _versoes.Count)) { ArrumarVazio(); return; }
        _arrumando = true;
        try
        {
            if (_fontes.Count == 0 || _chave?.Fonte != chave.Item2) RecriarFontes();
            _chave = chave;
            _largura = largura;
            _linha = Math.Max(1, Font.Height * 3);
            _cartoes.Clear();
            int y = 0;
            bool atualMarcado = false;
            for (int i = 0; i < _versoes.Count; i++)
            {
                var v = _versoes[i];
                var c = Medir(v, i, y, largura);
                if (!atualMarcado && (v.NaoLancado || (_instalada.Length > 0 && v.Versao == _instalada))) { c.Atual = true; atualMarcado = true; }
                _cartoes.Add(c);
                y = c.Bottom + D(EntreCartoes);
            }
            int total = _cartoes.Count == 0 ? 0 : y;
            AutoScrollMinSize = new Size(0, total);
            if (total > 0)
            {
                VerticalScroll.SmallChange = _linha;
                VerticalScroll.LargeChange = Math.Max(1, ClientSize.Height - _linha);
            }
            ArrumarVazio();
            if (_cartoes.Count > 0)
            {
                var a = _cartoes[Math.Clamp(_ancoraIndice, 0, _cartoes.Count - 1)];
                RolarPara(a.Top + (int)Math.Round(_ancoraFracao * a.Height));
            }
            else RolarPara(0);
            _fixada = null;
        }
        finally { _arrumando = false; }
        if (_cartoes.Count > 0) Notificar(VersaoNoTopo);
        Invalidate();
    }

    void ArrumarVazio()
    {
        bool vazio = _versoes.Count == 0;
        if (!vazio) { _mumu.Visible = false; return; }
        int w = Math.Max(1, ClientSize.Width), h = ClientSize.Height;
        if (h <= 0) return;
        int gap = D(UiMetrics.Gap) * 2, largTexto = Math.Max(D(60), w - 2 * D(UiMetrics.PagePadding));
        var titulo = Medir(VazioTitulo, F(Fonte.Vazio), largTexto);
        var dica = Medir(VazioDica, F(Fonte.Corpo), largTexto);
        int textoH = titulo.Height + D(UiMetrics.Gap) + dica.Height;
        int art = Math.Min(Math.Min(D(ArtMax), w), h - textoH - gap);
        bool mostra = art >= D(ArtMin);
        int bloco = (mostra ? art + gap : 0) + textoH, topo = Math.Max(0, (h - bloco) / 2);
        if (mostra)
        {
            _vazioArt = new Rectangle((w - art) / 2, topo, art, art);
            _mumu.Bounds = _vazioArt;
            _mumu.Visible = true;
        }
        else { _vazioArt = Rectangle.Empty; _mumu.Visible = false; }
        int yTexto = topo + (mostra ? art + gap : 0);
        _vazioTitulo = new Rectangle((w - largTexto) / 2, yTexto, largTexto, titulo.Height);
        _vazioDica = new Rectangle((w - largTexto) / 2, _vazioTitulo.Bottom + D(UiMetrics.Gap), largTexto, dica.Height);
    }

    Size Medir(string texto, Font fonte, int largura) =>
        TextRenderer.MeasureText(texto, fonte, new Size(Math.Max(1, largura), int.MaxValue), Quebra);

    Cartao Medir(VersaoNovidade v, int indice, int top, int largura)
    {
        var c = new Cartao { Top = top, Indice = indice };
        int pad = D(PadCartao), x = pad, iw = Math.Max(D(60), largura - 2 * pad), y = top + pad;

        void Texto(string t, Fonte f, Cor cor, int px, int largTexto)
        {
            var s = Medir(t, F(f), largTexto);
            c.Pecas.Add(new Peca { Tipo = Tipo.Texto, R = new Rectangle(px, y, largTexto, s.Height), Texto = t, Fonte = f, Cor = cor });
            y += s.Height;
        }

        Texto(v.NaoLancado ? TituloNaoLancado : (v.Versao.Length > 0 ? v.Versao : "Sem número"), v.NaoLancado ? Fonte.Titulo : Fonte.TituloMono, Cor.Texto, x, iw);
        string data = DataPorExtenso(v.Data);
        if (data.Length > 0) { y += D(2); Texto(data, Fonte.Detalhe, Cor.Suave, x, iw); }

        c.Novas = _vistasSet == null ? 0 : v.Itens.Count(i => !_vistasSet.Contains(i.Chave));
        var chips = new List<(string Texto, EstiloChip Estilo)>();
        if (v.NaoLancado) chips.Add((ChipNaoLancado, EstiloChip.Neutro));
        else if (_instalada.Length > 0 && v.Versao == _instalada) chips.Add((ChipSuaVersao, EstiloChip.Ok));
        if (v.Obrigatoria) chips.Add((ChipObrigatoria, EstiloChip.Aviso));
        if (c.Novas > 0) chips.Add((c.Novas == 1 ? "1 novidade" : $"{c.Novas} novidades", EstiloChip.Destaque));
        if (chips.Count > 0)
        {
            y += D(10);
            int cx = x, linhaH = 0, hPad = D(10), vPad = D(3);
            foreach (var (t, estilo) in chips)
            {
                var s = TextRenderer.MeasureText(t, F(Fonte.Chip), Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                int w = Math.Min(iw, s.Width + 2 * hPad), h = s.Height + 2 * vPad;
                if (cx > x && cx + w > x + iw) { cx = x; y += linhaH + D(EntreChips); linhaH = 0; }
                c.Pecas.Add(new Peca { Tipo = Tipo.Chip, R = new Rectangle(cx, y, w, h), Texto = t, Fonte = Fonte.Chip, Estilo = estilo });
                cx += w + D(EntreChips);
                linhaH = Math.Max(linhaH, h);
            }
            y += linhaH;
        }

        if (v.Resumo.Length > 0) { y += D(10); Texto(v.Resumo, Fonte.Corpo, Cor.Suave, x, iw); }

        string grupo = "";
        int indent = D(IndentItem), tw = Math.Max(D(40), iw - indent);
        foreach (var item in v.Itens)
        {
            if (item.Grupo.Length > 0 && item.Grupo != grupo)
            {
                grupo = item.Grupo;
                y += D(16);
                Texto(item.Grupo.ToUpper(Cultura), Fonte.Legenda, Cor.Legenda, x, iw);
                y += D(2);
            }
            y += D(10);
            bool novo = _vistasSet != null && !_vistasSet.Contains(item.Chave);
            int linhaTitulo = TextRenderer.MeasureText("Ag", F(Fonte.Item), Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Height;
            int halo = D(Halo);
            c.Pecas.Add(new Peca { Tipo = Tipo.Ponto, Novo = novo, R = new Rectangle(x + (indent - halo) / 2 - D(2), y + (linhaTitulo - halo) / 2, halo, halo) });
            Texto(item.Titulo, Fonte.Item, Cor.Texto, x + indent, tw);
            if (item.Detalhe.Length > 0) { y += D(2); Texto(Maiuscula(item.Detalhe), Fonte.Detalhe, Cor.Suave, x + indent, tw); }
        }

        y += pad;
        c.Height = y - top;
        return c;
    }

    // ---------- rolagem ----------

    int IndiceEm(int y, out double fracao)
    {
        int lo = 0, hi = _cartoes.Count - 1, achou = 0;
        while (lo <= hi)
        {
            int meio = (lo + hi) / 2;
            if (_cartoes[meio].Top <= y) { achou = meio; lo = meio + 1; } else hi = meio - 1;
        }
        var c = _cartoes[achou];
        if (y >= c.Bottom && achou + 1 < _cartoes.Count) { achou++; c = _cartoes[achou]; }
        fracao = c.Height <= 0 ? 0 : Math.Clamp((y - c.Top) / (double)c.Height, 0, 1);
        return achou;
    }

    void RolarPara(int y)
    {
        int max = Math.Max(0, AutoScrollMinSize.Height - ClientSize.Height);
        AutoScrollPosition = new Point(0, Math.Clamp(y, 0, max));
    }

    /// <summary>Depois de qualquer rolagem: guarda a âncora, avisa se mudou a versão do topo e, com o foco do teclado à mostra, repinta a moldura (o Windows só repinta a faixa nova).</summary>
    void AoRolar()
    {
        if (_arrumando || _cartoes.Count == 0) return;
        int y = ScrollY;
        int indice = IndiceEm(y, out double fracao);
        if (_fixada is { } f && f.Y != y) _fixada = null;
        if (_fixada == null) { _ancoraIndice = indice; _ancoraFracao = fracao; }
        else { _ancoraIndice = _fixada.Value.Indice; _ancoraFracao = 0; }
        if (Focused && ShowFocusCues) Invalidate();
        Notificar(VersaoNoTopo);
    }

    void Notificar(int indice)
    {
        if (indice == _ultimoTopo) return;
        _ultimoTopo = indice;
        VersaoNoTopoMudou?.Invoke(indice);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg is WM_VSCROLL or WM_MOUSEWHEEL) AoRolar();
    }

    protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); AoRolar(); }

    // ---------- pintura ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        if (_versoes.Count == 0)
        {
            if (_vazioTitulo.Width > 0)
            {
                const TextFormatFlags centro = Quebra | TextFormatFlags.HorizontalCenter;
                TextRenderer.DrawText(g, VazioTitulo, F(Fonte.Vazio), _vazioTitulo, Theme.Text, centro);
                TextRenderer.DrawText(g, VazioDica, F(Fonte.Corpo), _vazioDica, Theme.TextMuted, centro);
            }
        }
        else
        {
            int dy = AutoScrollPosition.Y; // conteúdo -> cliente
            int de = e.ClipRectangle.Top - dy, ate = e.ClipRectangle.Bottom - dy;
            foreach (var c in _cartoes)
            {
                if (c.Bottom < de) continue;
                if (c.Top > ate) break;
                PintarCartao(g, c, dy, de, ate);
            }
        }
        if (Focused && ShowFocusCues && _versoes.Count > 0)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Theme.Accent, Math.Max(1, D(2)));
            var r = ClientRectangle;
            r.Inflate(-(int)pen.Width / 2 - 1, -(int)pen.Width / 2 - 1);
            using var path = Theme.RoundedPath(r, D(UiMetrics.Radius));
            g.DrawPath(pen, path);
        }
    }

    void PintarCartao(Graphics g, Cartao c, int dy, int de, int ate)
    {
        var card = new Rectangle(0, c.Top + dy, _largura, c.Height);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int raio = D(UiMetrics.Radius);
        using (var path = Theme.RoundedPath(card, raio))
        {
            using var fundo = new SolidBrush(Theme.SurfaceRaised);
            g.FillPath(fundo, path);
        }
        if (c.Atual)
        {
            using var path = Theme.RoundedPath(new Rectangle(card.X, card.Y, card.Width - 1, card.Height - 1), raio);
            using var borda = new Pen(Theme.CardSelectedBorder, 1);
            g.DrawPath(borda, path);
        }
        foreach (var p in c.Pecas)
        {
            if (p.R.Bottom < de || p.R.Top > ate) continue;
            var r = new Rectangle(p.R.X, p.R.Y + dy, p.R.Width, p.R.Height);
            switch (p.Tipo)
            {
                case Tipo.Texto:
                    TextRenderer.DrawText(g, p.Texto, F(p.Fonte), r, p.Cor switch { Cor.Texto => Theme.Text, Cor.Legenda => Theme.AccentSoft, _ => Theme.TextMuted }, Quebra | TextFormatFlags.Left);
                    break;
                case Tipo.Chip:
                    PintarChip(g, p, r);
                    break;
                case Tipo.Ponto:
                    PintarPonto(g, p, r);
                    break;
            }
        }
    }

    void PintarChip(Graphics g, Peca p, Rectangle r)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var caixa = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1);
        using var path = Theme.RoundedPath(caixa, caixa.Height);
        Color texto;
        switch (p.Estilo)
        {
            case EstiloChip.Destaque:
                using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, path);
                texto = Theme.AccentText;
                break;
            case EstiloChip.Aviso:
                using (var b = new SolidBrush(Theme.WarnSurface)) g.FillPath(b, path);
                using (var pen = new Pen(Theme.WarnBorder, 1)) g.DrawPath(pen, path);
                texto = Theme.Warn;
                break;
            case EstiloChip.Ok:
                using (var pen = new Pen(Theme.Ok, 1)) g.DrawPath(pen, path);
                texto = Theme.Ok;
                break;
            default:
                using (var pen = new Pen(Theme.AccentSoft, 1)) g.DrawPath(pen, path);
                texto = Theme.AccentSoft;
                break;
        }
        TextRenderer.DrawText(g, p.Texto, F(p.Fonte), r, texto, UmaLinha);
    }

    void PintarPonto(Graphics g, Peca p, Rectangle r)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int d = D(Ponto);
        var miolo = new Rectangle(r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
        if (p.Novo)
        {
            using var halo = new SolidBrush(Color.FromArgb(HaloAlpha, Theme.Accent));
            g.FillEllipse(halo, r);
        }
        using var b = new SolidBrush(p.Novo ? Theme.Accent : Theme.TextMuted);
        g.FillEllipse(b, miolo);
    }

    // ---------- eventos ----------

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _chave = null;
        Arrumar();
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Arrumar(); }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        Arrumar();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); _chave = null; Arrumar(); }

    void OnTema() { if (!IsDisposed) Invalidate(true); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    // ---------- teclado ----------

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_cartoes.Count == 0) return;
        int pagina = Math.Max(_linha, ClientSize.Height - _linha);
        bool ctrl = e.Control;
        switch (e.KeyCode)
        {
            case Keys.Up when ctrl: IrPara(VersaoNoTopo - 1); break;
            case Keys.Down when ctrl: IrPara(VersaoNoTopo + 1); break;
            case Keys.Up: Deslocar(-_linha); break;
            case Keys.Down: Deslocar(_linha); break;
            case Keys.PageUp: Deslocar(-pagina); break;
            case Keys.PageDown: Deslocar(pagina); break;
            case Keys.Home: RolarPara(0); AoRolar(); break;
            case Keys.End: RolarPara(int.MaxValue / 2); AoRolar(); break;
            case Keys.C when ctrl: Copiar(); break;
            default: return;
        }
        e.Handled = e.SuppressKeyPress = true;
    }

    void Deslocar(int delta)
    {
        RolarPara(ScrollY + delta);
        AoRolar();
    }

    void Copiar()
    {
        if (VersaoNoTopo >= _versoes.Count) return;
        try { Clipboard.SetText(TextoDaVersao(_versoes[VersaoNoTopo])); }
        catch (Exception ex) { Log.Info($"Novidades: não copiou para a área de transferência: {ex.Message}"); }
    }

    // ---------- acessibilidade ----------

    protected override AccessibleObject CreateAccessibilityInstance() => new LeituraAcessivel(this);

    sealed class LeituraAcessivel(NovidadesLeitura dono) : ControlAccessibleObject(dono)
    {
        public override int GetChildCount() => dono._acessiveis.Count;
        public override AccessibleObject? GetChild(int index) => index >= 0 && index < dono._acessiveis.Count ? dono._acessiveis[index] : null;
    }

    sealed class ItemAcessivel(NovidadesLeitura dono, int indice) : AccessibleObject
    {
        VersaoNovidade V => dono._versoes[indice];
        public override AccessibleObject Parent => dono.AccessibilityObject;
        public override AccessibleRole Role => AccessibleRole.ListItem;
        public override AccessibleStates State => AccessibleStates.Selectable | AccessibleStates.Focusable | (indice == dono.VersaoNoTopo ? AccessibleStates.Selected : 0);
        public override string? Value { get => TextoDosItens(V); set { _ = value; } }
        public override string? Name
        {
            get
            {
                var v = V;
                var partes = new List<string> { v.NaoLancado ? "Nesta versão do app, " + TituloNaoLancado.ToLower(Cultura) : "Versão " + v.Versao };
                string data = DataPorExtenso(v.Data);
                if (data.Length > 0) partes.Add(data);
                if (!v.NaoLancado && dono._instalada.Length > 0 && v.Versao == dono._instalada) partes.Add("sua versão");
                if (v.Obrigatoria) partes.Add("atualização obrigatória");
                int novas = dono._vistasSet == null ? 0 : v.Itens.Count(i => !dono._vistasSet.Contains(i.Chave));
                if (novas > 0) partes.Add(novas == 1 ? "1 novidade" : $"{novas} novidades");
                return string.Join(", ", partes);
            }
            set { _ = value; }
        }
        public override Rectangle Bounds
        {
            get
            {
                if (indice >= dono._cartoes.Count) return Rectangle.Empty;
                var c = dono._cartoes[indice];
                var cliente = new Rectangle(0, c.Top + dono.AutoScrollPosition.Y, dono._largura, c.Height);
                cliente.Intersect(dono.ClientRectangle);
                return cliente.IsEmpty ? Rectangle.Empty : dono.RectangleToScreen(cliente);
            }
        }
        public override AccessibleObject? Navigate(AccessibleNavigation direction) => direction switch
        {
            AccessibleNavigation.Next or AccessibleNavigation.Down => indice + 1 < dono._acessiveis.Count ? dono._acessiveis[indice + 1] : null,
            AccessibleNavigation.Previous or AccessibleNavigation.Up => indice > 0 ? dono._acessiveis[indice - 1] : null,
            _ => base.Navigate(direction),
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnTema;
            foreach (var f in _fontes.Values) f.Dispose();
            _fontes.Clear();
        }
        base.Dispose(disposing);
    }
}
