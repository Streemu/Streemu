using System.Drawing.Drawing2D;
using System.Globalization;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// A lista de versões ao lado da leitura da tela Novidades (plano 027, D5): um controle desenhado à mão, uma linha por versão (número em mono, data curta e,
/// à direita, "você" na versão instalada e uma bolinha na que tem item não visto). A linha escolhida leva o fundo e o contorno dos cartões selecionados.
/// Rola sozinha (<see cref="ScrollSkin"/>) só se a altura não der. As cores são lidas de <see cref="Theme"/> na hora de pintar.
/// Teclado: ↑ e ↓ movem a seleção; Enter e Espaço escolhem (<see cref="Escolheu"/>). Atribuir <see cref="Selecionada"/> por código não dispara o evento.
/// </summary>
public sealed class NovidadesIndice : Panel
{
    public const string Legenda = "VERSÕES", TextoAgora = "Agora", TextoVoce = "você";
    /// <summary>Largura da coluna, em dp.</summary>
    public const int LarguraDp = 216;
    const int AlturaLinha = 36, Entre = 2, PadX = 12, TopoLegenda = 4, AlturaLegenda = 28, Bolinha = 8, EntreItens = 10, PadBaixo = 8;
    const TextFormatFlags UmaLinha = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
    const int WM_VSCROLL = 0x115, WM_MOUSEWHEEL = 0x20A;
    /// <summary>Medir com as reticências ligadas devolve a largura do "...", não a do texto: a medida é sem elas.</summary>
    const TextFormatFlags Medida = UmaLinha & ~TextFormatFlags.EndEllipsis;
    static readonly CultureInfo Cultura = CultureInfo.GetCultureInfo("pt-BR");

    IReadOnlyList<VersaoNovidade> _versoes = [];
    string _instalada = "";
    bool[] _novas = [];
    int _selecionada, _hover = -1;
    double _k = 1;
    Font? _fMono, _fCorpo, _fData, _fLegenda, _fAgora;
    float _fonteBase;
    List<LinhaAcessivel> _acessiveis = new();

    public NovidadesIndice()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Surface;
        TabStop = true;
        AutoScroll = true;
        AccessibleName = "Versões";
        AccessibleRole = AccessibleRole.List;
        ScrollSkin.Attach(this);
        UiScale.WatchFont(this);
        Theme.Changed += OnTema;
    }

    /// <summary>O usuário escolheu uma versão (clique, Enter ou Espaço): o índice da versão.</summary>
    public event Action<int>? Escolheu;

    /// <summary>Linha selecionada. Atribuir não dispara <see cref="Escolheu"/>; a linha escolhida fica à vista.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Selecionada
    {
        get => _selecionada;
        set
        {
            if (_versoes.Count == 0) { _selecionada = 0; return; }
            value = Math.Clamp(value, 0, _versoes.Count - 1);
            if (value == _selecionada) { return; }
            _selecionada = value;
            Invalidate();
            GarantirVisivel(value);
            if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.Selection, value);
        }
    }

    /// <summary>Quantas versões a lista mostra. Para o teste.</summary>
    public int Quantidade => _versoes.Count;

    /// <summary>Troca o conteúdo; a primeira versão fica selecionada. <paramref name="vistas"/> nulo = nenhuma bolinha.</summary>
    public void Mostrar(IReadOnlyList<VersaoNovidade> versoes, string versaoInstalada, IReadOnlyCollection<string>? vistas)
    {
        _versoes = versoes ?? [];
        _instalada = versaoInstalada ?? "";
        var conjunto = vistas as ISet<string> ?? (vistas == null ? null : new HashSet<string>(vistas));
        _novas = _versoes.Select(v => conjunto != null && v.Itens.Any(i => !conjunto.Contains(i.Chave))).ToArray();
        _selecionada = 0; _hover = -1;
        _acessiveis = _versoes.Select((_, i) => new LinhaAcessivel(this, i)).ToList();
        AtualizarRolagem();
        RolarPara(0);
        if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
        Invalidate();
    }

    /// <summary>"20 nov" (sem o ponto que a cultura pt-BR põe na abreviação do mês); vazio sem data.</summary>
    internal static string DataCurta(DateOnly? data) => data is { } d ? $"{d.Day} {d.ToString("MMM", Cultura).TrimEnd('.')}" : "";

    string Rotulo(int i) => _versoes[i].NaoLancado ? TextoAgora : _versoes[i].Versao.Length > 0 ? _versoes[i].Versao : "?";
    bool EhInstalada(int i) => !_versoes[i].NaoLancado && _instalada.Length > 0 && _versoes[i].Versao == _instalada;

    // ---------- medida ----------

    int D(int dp) => (int)Math.Round(dp * _k);

    void Medidas()
    {
        _k = UiScale.Dp(this, 10000) / 10000.0;
        if (_fMono != null && _fonteBase == Font.Size) return;
        DescartarFontes();
        _fonteBase = Font.Size;
        float r = Font.Size / UiTypography.Body;
        _fMono = UiTypography.CreateMono(UiTypography.MonoBody * r);
        _fCorpo = UiTypography.Create(UiTypography.Caption * r, FontStyle.Bold);
        _fData = UiTypography.Create(UiTypography.Caption * r);
        _fLegenda = UiTypography.Create(UiTypography.Note * r, FontStyle.Bold);
        _fAgora = UiTypography.CreateSemibold(Font.Size);
    }

    void DescartarFontes()
    {
        foreach (var f in new[] { _fMono, _fCorpo, _fData, _fLegenda, _fAgora }) f?.Dispose();
        _fMono = _fCorpo = _fData = _fLegenda = _fAgora = null;
    }

    int TopoDaLinha(int i) => D(AlturaLegenda) + i * (D(AlturaLinha) + D(Entre));

    int AlturaTotal() => _versoes.Count == 0 ? 0 : TopoDaLinha(_versoes.Count) + D(PadBaixo);

    void AtualizarRolagem()
    {
        Medidas();
        AutoScrollMinSize = new Size(0, AlturaTotal());
        VerticalScroll.SmallChange = D(AlturaLinha) + D(Entre);
    }

    int ScrollY => -AutoScrollPosition.Y;

    void RolarPara(int y)
    {
        int max = Math.Max(0, AutoScrollMinSize.Height - ClientSize.Height);
        AutoScrollPosition = new Point(0, Math.Clamp(y, 0, max));
    }

    void GarantirVisivel(int i)
    {
        if (_versoes.Count == 0 || ClientSize.Height <= 0) return;
        Medidas();
        int topo = TopoDaLinha(i), baixo = topo + D(AlturaLinha);
        if (i == 0) topo = 0;
        if (topo < ScrollY) RolarPara(topo);
        else if (baixo > ScrollY + ClientSize.Height) RolarPara(baixo - ClientSize.Height + D(PadBaixo));
    }

    Rectangle RetanguloDaLinha(int i) => new(0, TopoDaLinha(i), Math.Max(1, ClientSize.Width), D(AlturaLinha));

    int LinhaEm(Point cliente)
    {
        if (_versoes.Count == 0) return -1;
        Medidas();
        int y = cliente.Y + ScrollY - D(AlturaLegenda);
        if (y < 0) return -1;
        int passo = D(AlturaLinha) + D(Entre), i = y / passo;
        return i < _versoes.Count && y % passo < D(AlturaLinha) ? i : -1;
    }

    /// <summary>O retângulo (coordenadas do cliente) de cada linha. Para o teste.</summary>
    public IReadOnlyList<Rectangle> RetangulosDasLinhas()
    {
        Medidas();
        return Enumerable.Range(0, _versoes.Count).Select(i => { var r = RetanguloDaLinha(i); r.Y -= ScrollY; return r; }).ToList();
    }

    // ---------- pintura ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var fundo = new SolidBrush(Theme.Surface)) g.FillRectangle(fundo, e.ClipRectangle);
        Medidas();
        int dy = AutoScrollPosition.Y;
        TextRenderer.DrawText(g, Legenda, _fLegenda!, new Rectangle(D(PadX), D(TopoLegenda) + dy, Math.Max(1, ClientSize.Width - 2 * D(PadX)), D(AlturaLegenda) - D(TopoLegenda)), Theme.AccentSoft,
            UmaLinha | TextFormatFlags.Left);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < _versoes.Count; i++)
        {
            var r = RetanguloDaLinha(i);
            r.Y += dy;
            if (r.Bottom < e.ClipRectangle.Top || r.Top > e.ClipRectangle.Bottom) continue;
            PintarLinha(g, i, r);
        }
        if (Focused && ShowFocusCues && _versoes.Count > 0)
        {
            using var pen = new Pen(Theme.Accent, Math.Max(1, D(2)));
            var r = ClientRectangle;
            r.Inflate(-(int)pen.Width / 2 - 1, -(int)pen.Width / 2 - 1);
            using var path = Theme.RoundedPath(r, D(UiMetrics.Radius));
            g.DrawPath(pen, path);
        }
    }

    void PintarLinha(Graphics g, int i, Rectangle r)
    {
        int raio = D(UiMetrics.SmallRadius), pad = D(PadX);
        var caixa = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1);
        if (i == _selecionada)
        {
            using var path = Theme.RoundedPath(caixa, raio);
            using (var b = new SolidBrush(Theme.CardSelected)) g.FillPath(b, path);
            using var pen = new Pen(Theme.CardSelectedBorder, 1);
            g.DrawPath(pen, path);
        }
        else if (i == _hover)
        {
            using var path = Theme.RoundedPath(caixa, raio);
            using var b = new SolidBrush(Theme.SurfaceRaised);
            g.FillPath(b, path);
        }
        var v = _versoes[i];
        // da direita para a esquerda: "você" e/ou a bolinha de item não visto; o resto do espaço é do número e da data
        int direita = r.Right - pad;
        if (EhInstalada(i))
        {
            int w = TextRenderer.MeasureText(TextoVoce, _fCorpo!, Size.Empty, Medida).Width;
            TextRenderer.DrawText(g, TextoVoce, _fCorpo!, new Rectangle(direita - w, r.Y, w, r.Height), Theme.Ok, UmaLinha | TextFormatFlags.Left);
            direita -= w + D(EntreItens);
        }
        if (_novas[i])
        {
            int d = D(Bolinha);
            using var b = new SolidBrush(Theme.Accent);
            g.FillEllipse(b, direita - d, r.Y + (r.Height - d) / 2, d, d);
            direita -= d + D(EntreItens);
        }
        var fonte = v.NaoLancado ? _fAgora! : _fMono!;
        string numero = Rotulo(i);
        int largNumero = Math.Min(TextRenderer.MeasureText(numero, fonte, Size.Empty, Medida).Width, Math.Max(1, direita - (r.X + pad)));
        TextRenderer.DrawText(g, numero, fonte, new Rectangle(r.X + pad, r.Y, largNumero, r.Height), Theme.Text, UmaLinha | TextFormatFlags.Left);
        string data = v.NaoLancado ? "" : DataCurta(v.Data);
        int xData = r.X + pad + largNumero + D(EntreItens);
        if (data.Length > 0 && direita - xData > D(12))
            TextRenderer.DrawText(g, data, _fData!, new Rectangle(xData, r.Y, direita - xData, r.Height), Theme.TextMuted, UmaLinha | TextFormatFlags.Left);
    }

    // ---------- eventos ----------

    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); _fonteBase = 0; AtualizarRolagem(); Invalidate(); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); AtualizarRolagem(); }
    protected override void OnResize(EventArgs eventargs) { base.OnResize(eventargs); AtualizarRolagem(); Invalidate(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); _fonteBase = 0; AtualizarRolagem(); Invalidate(); }
    void OnTema() { if (!IsDisposed) Invalidate(true); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg is WM_VSCROLL or WM_MOUSEWHEEL) { Invalidate(); AtualizarHover(PointToClient(MousePosition)); }
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); AtualizarHover(e.Location); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); AtualizarHover(new Point(-1, -1)); }

    void AtualizarHover(Point p)
    {
        int i = ClientRectangle.Contains(p) ? LinhaEm(p) : -1;
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
        if (i != _hover) { _hover = i; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left) return;
        int i = LinhaEm(e.Location);
        if (i >= 0) Escolher(i);
    }

    /// <summary>Seleciona a versão e avisa (o mesmo caminho do clique). Para o clique, o teclado e o teste.</summary>
    public void Escolher(int i)
    {
        if (i < 0 || i >= _versoes.Count) return;
        Selecionada = i;
        Escolheu?.Invoke(i);
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_versoes.Count == 0) return;
        switch (e.KeyCode)
        {
            case Keys.Up: Selecionada = _selecionada - 1; break;
            case Keys.Down: Selecionada = _selecionada + 1; break;
            case Keys.Home: Selecionada = 0; break;
            case Keys.End: Selecionada = _versoes.Count - 1; break;
            case Keys.Enter or Keys.Space: Escolher(_selecionada); break;
            default: return;
        }
        e.Handled = e.SuppressKeyPress = true;
    }

    // ---------- acessibilidade ----------

    protected override AccessibleObject CreateAccessibilityInstance() => new ListaAcessivel(this);

    sealed class ListaAcessivel(NovidadesIndice dono) : ControlAccessibleObject(dono)
    {
        public override int GetChildCount() => dono._acessiveis.Count;
        public override AccessibleObject? GetChild(int index) => index >= 0 && index < dono._acessiveis.Count ? dono._acessiveis[index] : null;
    }

    sealed class LinhaAcessivel(NovidadesIndice dono, int indice) : AccessibleObject
    {
        public override AccessibleObject Parent => dono.AccessibilityObject;
        public override AccessibleRole Role => AccessibleRole.ListItem;
        public override AccessibleStates State => AccessibleStates.Selectable | AccessibleStates.Focusable | (indice == dono._selecionada ? AccessibleStates.Selected : 0);
        public override string? DefaultAction => "Ir para esta versão";
        public override void DoDefaultAction() => dono.Escolher(indice);
        public override string? Name
        {
            get
            {
                var v = dono._versoes[indice];
                var partes = new List<string> { v.NaoLancado ? "Nesta versão do app" : "Versão " + v.Versao };
                string data = DataCurta(v.Data);
                if (data.Length > 0) partes.Add(data);
                if (dono.EhInstalada(indice)) partes.Add("sua versão");
                if (dono._novas[indice]) partes.Add("com novidades que você ainda não viu");
                return string.Join(", ", partes);
            }
            set { _ = value; }
        }
        public override Rectangle Bounds
        {
            get
            {
                dono.Medidas();
                var r = dono.RetanguloDaLinha(indice);
                r.Y -= dono.ScrollY;
                r.Intersect(dono.ClientRectangle);
                return r.IsEmpty ? Rectangle.Empty : dono.RectangleToScreen(r);
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
        if (disposing) { Theme.Changed -= OnTema; DescartarFontes(); }
        base.Dispose(disposing);
    }
}
