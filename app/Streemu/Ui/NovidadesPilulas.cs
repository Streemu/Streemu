using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// A fileira de versões da tela Novidades quando a página é estreita (plano 027, D5): uma pílula por versão (número em mono), a escolhida em destaque.
/// As que não cabem numa linha vão para o botão "Mais ▾", que abre um menu do estilo do app; se a escolhida estiver lá dentro, o "Mais" é que fica
/// em destaque. Nunca quebra para uma segunda linha e nunca rola. Desenhada à mão num controle só; as cores são lidas de <see cref="Theme"/> ao pintar.
/// </summary>
public sealed class NovidadesPilulas : Control
{
    public const string TextoMais = "Mais ▾", TextoAgora = "Agora";
    const int Altura = 32, PadX = 12, Entre = 6;
    const TextFormatFlags UmaLinha = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;

    IReadOnlyList<VersaoNovidade> _versoes = [];
    int _selecionada, _visiveis, _hover = -2, _larguraMais;
    int[] _larguras = [];
    double _k = 1;
    Font? _fMono, _fMais;
    float _fonteBase;
    bool _menuAberto;

    public NovidadesPilulas()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Surface;
        TabStop = true;
        AccessibleName = "Versões";
        AccessibleRole = AccessibleRole.ToolBar;
        UiScale.WatchFont(this);
        Theme.Changed += OnTema;
    }

    public event Action<int>? Escolheu;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Selecionada
    {
        get => _selecionada;
        set
        {
            if (_versoes.Count == 0) { _selecionada = 0; return; }
            value = Math.Clamp(value, 0, _versoes.Count - 1);
            if (value == _selecionada) return;
            _selecionada = value;
            Invalidate();
        }
    }

    /// <summary>Quantas pílulas de versão aparecem (as demais estão no "Mais").</summary>
    public int Visiveis { get { Organizar(); return _visiveis; } }

    /// <summary>Se o botão "Mais" está à mostra (alguma versão não coube).</summary>
    public bool TemMais { get { Organizar(); return _visiveis < _versoes.Count; } }

    /// <summary>A altura que a fileira pede, em pixels.</summary>
    public int AlturaPedida { get { Medidas(); return D(Altura); } }

    public void Mostrar(IReadOnlyList<VersaoNovidade> versoes)
    {
        _versoes = versoes ?? [];
        _selecionada = 0; _hover = -2;
        _larguras = [];
        Invalidate();
    }

    string Rotulo(int i) => _versoes[i].NaoLancado ? TextoAgora : _versoes[i].Versao.Length > 0 ? _versoes[i].Versao : "?";

    int D(int dp) => (int)Math.Round(dp * _k);

    void Medidas()
    {
        _k = UiScale.Dp(this, 10000) / 10000.0;
        if (_fMono != null && _fonteBase == Font.Size) return;
        DescartarFontes();
        _fonteBase = Font.Size;
        _fMono = UiTypography.CreateMono(UiTypography.MonoBody * (Font.Size / UiTypography.Body));
        _fMais = (Font)Font.Clone();
        _larguras = [];
    }

    void DescartarFontes() { _fMono?.Dispose(); _fMais?.Dispose(); _fMono = _fMais = null; }

    /// <summary>Quantas pílulas cabem na largura de agora, junto com o "Mais" quando sobra versão.</summary>
    void Organizar()
    {
        Medidas();
        if (_larguras.Length != _versoes.Count || _larguraMais == 0)
        {
            _larguras = Enumerable.Range(0, _versoes.Count)
                .Select(i => TextRenderer.MeasureText(Rotulo(i), _fMono!, Size.Empty, UmaLinha).Width + 2 * D(PadX)).ToArray();
            _larguraMais = TextRenderer.MeasureText(TextoMais, _fMais!, Size.Empty, UmaLinha).Width + 2 * D(PadX);
        }
        int disponivel = Math.Max(0, ClientSize.Width), gap = D(Entre);
        int total = _larguras.Sum() + Math.Max(0, _larguras.Length - 1) * gap;
        if (total <= disponivel) { _visiveis = _versoes.Count; return; }
        int usado = _larguraMais, n = 0;
        foreach (int w in _larguras)
        {
            if (usado + gap + w > disponivel) break;
            usado += gap + w; n++;
        }
        _visiveis = n;
    }

    /// <summary>Os retângulos das pílulas à mostra e, por último, o do "Mais" (se houver), em coordenadas do cliente. Para o teste.</summary>
    public IReadOnlyList<Rectangle> RetangulosVisiveis()
    {
        Organizar();
        var lista = new List<Rectangle>();
        int x = 0, gap = D(Entre), h = D(Altura), y = Math.Max(0, (ClientSize.Height - h) / 2);
        for (int i = 0; i < _visiveis; i++) { lista.Add(new Rectangle(x, y, _larguras[i], h)); x += _larguras[i] + gap; }
        if (_visiveis < _versoes.Count) lista.Add(new Rectangle(x, y, _larguraMais, h));
        return lista;
    }

    /// <summary>O "Mais" está em destaque porque a versão escolhida está dentro dele. Para o teste.</summary>
    public bool MaisEmDestaque { get { Organizar(); return _visiveis < _versoes.Count && _selecionada >= _visiveis; } }

    // ---------- pintura ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var fundo = new SolidBrush(Theme.Surface)) g.FillRectangle(fundo, e.ClipRectangle);
        var retangulos = RetangulosVisiveis();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < retangulos.Count; i++)
        {
            bool mais = i == _visiveis && _visiveis < _versoes.Count;
            bool destaque = mais ? _selecionada >= _visiveis : i == _selecionada;
            var r = retangulos[i];
            var caixa = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1);
            using var path = Theme.RoundedPath(caixa, caixa.Height);
            Color fundoPilula = destaque ? Theme.Accent : (i == _hover || (mais && _menuAberto)) ? Theme.NeutralHover : Theme.Neutral;
            using (var b = new SolidBrush(fundoPilula)) g.FillPath(b, path);
            if (Focused && ShowFocusCues && ((mais && _selecionada >= _visiveis) || (!mais && i == _selecionada)))
            {
                using var pen = new Pen(Theme.AccentHover, Math.Max(1, D(2)));
                g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, mais ? TextoMais : Rotulo(i), mais ? _fMais! : _fMono!, r, destaque ? Theme.AccentText : Theme.NeutralText, UmaLinha);
        }
    }

    // ---------- eventos ----------

    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); _fonteBase = 0; _larguraMais = 0; Invalidate(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); _fonteBase = 0; _larguraMais = 0; Invalidate(); }
    void OnTema() { if (!IsDisposed) Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    int PilulaEm(Point p)
    {
        var rs = RetangulosVisiveis();
        for (int i = 0; i < rs.Count; i++) if (rs[i].Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = PilulaEm(e.Location);
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
        if (i != _hover) { _hover = i; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover != -2) { _hover = -2; Invalidate(); } }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left) return;
        int i = PilulaEm(e.Location);
        if (i < 0) return;
        if (i < _visiveis) Escolher(i); else AbrirMenu();
    }

    /// <summary>Seleciona a versão e avisa (o mesmo caminho do clique na pílula ou no item do menu).</summary>
    public void Escolher(int i)
    {
        if (i < 0 || i >= _versoes.Count) return;
        Selecionada = i;
        Escolheu?.Invoke(i);
    }

    /// <summary>O menu do "Mais" com as versões que não couberam, no estilo dos menus do app. Quem recebe é dono dele e o descarta.</summary>
    public ContextMenuStrip CriarMenuMais()
    {
        Organizar();
        var menu = new ContextMenuStrip();
        TrayVisuals.Style(menu);
        menu.Font = Font;
        for (int i = _visiveis; i < _versoes.Count; i++)
        {
            int indice = i;
            var item = new ToolStripMenuItem(Rotulo(i)) { Checked = i == _selecionada };
            item.Click += (_, _) => Escolher(indice);
            menu.Items.Add(item);
        }
        return menu;
    }

    void AbrirMenu()
    {
        if (_visiveis >= _versoes.Count) return;
        var menu = CriarMenuMais();
        var mais = RetangulosVisiveis()[^1];
        _menuAberto = true; Invalidate();
        menu.Closed += (_, _) => { _menuAberto = false; if (!IsDisposed) Invalidate(); menu.BeginInvoke(menu.Dispose); };
        menu.Show(this, new Point(mais.Right, mais.Bottom + D(4)), ToolStripDropDownDirection.BelowLeft);
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_versoes.Count == 0) return;
        switch (e.KeyCode)
        {
            case Keys.Left: Escolher(_selecionada - 1); break;
            case Keys.Right: Escolher(_selecionada + 1); break;
            case Keys.Down or Keys.Enter or Keys.Space when _selecionada >= _visiveis && _visiveis < _versoes.Count: AbrirMenu(); break;
            default: return;
        }
        e.Handled = e.SuppressKeyPress = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Theme.Changed -= OnTema; DescartarFontes(); }
        base.Dispose(disposing);
    }
}
