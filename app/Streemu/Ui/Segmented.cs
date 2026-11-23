using System.ComponentModel;
using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// Seletor segmentado (uma escolha entre poucas): os itens lado a lado, todos da mesma largura e altura, com o escolhido em destaque. Substitui as fileiras
/// de botões soltos da janela Transmitir, que ficavam com larguras e posições diferentes.
/// Uso: <c>var res = new Segmented("720p", "1080p", "1440p", "Fonte") { Selected = 1 }; res.SelectedChanged += i =&gt; ...;</c>
/// </summary>
public sealed class Segmented : Control
{
    const int Radius = UiMetrics.SmallRadius, PadAt96 = UiMetrics.SegmentTextPadding, MinItemAt96 = UiMetrics.SegmentMinWidth, HeightAt96 = UiMetrics.SegmentHeight, InsetAt96 = UiMetrics.SegmentInset;
    readonly string[] _items;
    int _selected, _hover = -1;
    int _columns;
    readonly UiMotion _selectionMotion;
    int _previous;
    readonly string?[] _badges;

    public event Action<int>? SelectedChanged;

    /// <summary>Colunas fixas que ocupam a largura toda, cada opção como um botão separado (Qualidade 2x2 e FPS no Transmitir); 0 = o comportamento de sempre.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int StretchColumns { get; set; }

    /// <summary>Estilo da página Transmitir (desenho): cada opção é uma caixa de borda com a bolinha de escolha e o texto à esquerda.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Outline { get => _outline; set { if (_outline == value) return; _outline = value; Invalidate(); } }
    bool _outline;
    int RowHeight => Dp(_outline ? 46 : HeightAt96);

    public Segmented(params string[] items)
    {
        _items = items.Length > 0 ? items : throw new ArgumentException("um seletor precisa de pelo menos 1 item (recebi 0)", nameof(items));
        _columns = _items.Length;
        _badges = new string?[_items.Length];
        _selectionMotion = new(this, Invalidate);
        _selectionMotion.Snap(1);
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.List;
        Margin = new Padding(0, 4, 0, 4);
        AutoSize = false;
        Size = GetPreferredSize(Size.Empty);
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Selected
    {
        get => _selected;
        set
        {
            int next = Math.Clamp(value, 0, _items.Length - 1);
            if (next == _selected) return;
            _previous = _selected;
            _selected = next;
            _selectionMotion.Snap(0);
            _selectionMotion.To(1);
            Invalidate();
            SelectedChanged?.Invoke(_selected);
        }
    }

    /// <summary>Marca um item com um selo curto (ex.: "BETA"), uma pílula à direita do texto. Entra na largura preferida e no nome acessível; nulo remove.</summary>
    public void SetBadge(int index, string? badge)
    {
        if ((uint)index >= (uint)_items.Length) return;
        _badges[index] = string.IsNullOrEmpty(badge) ? null : badge;
        // leitor de tela: o nome (a legenda da seção) é de quem usa o controle; o selo vai na descrição
        var beta = _items.Where((_, k) => _badges[k] != null).ToArray();
        AccessibleDescription = beta.Length == 0 ? null : $"{string.Join(" e ", beta)}: {_badges.First(x => x != null)!.ToLowerInvariant()}";
        Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }

    // O selo é menor e em negrito para não competir com o rótulo.
    Font BadgeFont() => new(Font.FontFamily, Math.Max(6f, Font.Size * 0.72f), FontStyle.Bold);

    /// <summary>Largura da pílula (texto + folga) mais o espaço que a separa do rótulo; 0 sem selo.</summary>
    int BadgeExtra(int i)
    {
        if (_badges[i] is not { } b) return 0;
        using var f = BadgeFont();
        return TextRenderer.MeasureText(b, f, Size.Empty, TextFormatFlags.NoPadding).Width + Dp(10) + Dp(6);
    }

    /// <summary>Desenha a pílula à direita do rótulo centralizado; o conjunto rótulo+selo fica centralizado na célula.</summary>
    Rectangle TextAndBadge(int i, Rectangle cell, out Rectangle pill, bool centered, int left)
    {
        int extra = BadgeExtra(i);
        int textW = TextRenderer.MeasureText(_items[i], Font, Size.Empty, TextFormatFlags.NoPadding).Width;
        int total = Math.Min(cell.Width, textW + extra);
        int x = centered ? cell.X + (cell.Width - total) / 2 : left;
        var text = new Rectangle(x, cell.Y, Math.Max(1, total - extra), cell.Height);
        pill = Rectangle.Empty;
        if (extra > 0)
        {
            using var f = BadgeFont();
            int pw = extra - Dp(6), ph = f.Height + Dp(2);
            pill = new Rectangle(text.Right + Dp(6), cell.Y + (cell.Height - ph) / 2, pw, ph);
        }
        return text;
    }

    void DrawBadge(Graphics g, int i, Rectangle pill, bool on)
    {
        if (pill.IsEmpty || _badges[i] is not { } b) return;
        using var f = BadgeFont();
        using var path = RoundedRect(pill, pill.Height / 2);
        // sobre o item escolhido (fundo Accent) a pílula inverte as cores para continuar visível
        using (var fill = new SolidBrush(on ? Theme.AccentText : Theme.AccentSoft)) g.FillPath(fill, path);
        TextRenderer.DrawText(g, b, f, pill, Theme.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    int Dp(int at96) => UiScale.Dp(this, at96);

    /// <summary>Todos os itens têm a largura do maior texto (mais folga), nunca menos que o mínimo: o conjunto fica simétrico.</summary>
    int ItemWidth => Math.Max(Dp(MinItemAt96), _items.Select((t, i) => TextRenderer.MeasureText(t, Font).Width + BadgeExtra(i)).Max() + 2 * Dp(PadAt96));

    public override Size GetPreferredSize(Size proposedSize) => new(ItemWidth * _items.Length, Dp(HeightAt96));

    /// <summary>Configurações estreitas empilham opções inteiras, em vez de cortar os rótulos.</summary>
    public void FitWidth(int width)
    {
        if (StretchColumns > 0)
        {
            _columns = Math.Min(StretchColumns, _items.Length);
            Size = new Size(Math.Max(1, width), RowHeight * ((_items.Length + _columns - 1) / _columns));
            return;
        }
        _columns = Math.Clamp(width / ItemWidth, 1, _items.Length);
        Size = new Size(Math.Min(width, ItemWidth * _columns), Dp(HeightAt96) * ((_items.Length + _columns - 1) / _columns));
    }

    int IndexAt(Point p)
    {
        if (!ClientRectangle.Contains(p)) return -1;
        int index = p.Y / RowHeight * _columns + Math.Min(_columns - 1, p.X * _columns / Math.Max(1, Width));
        return index < _items.Length ? index : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int now = IndexAt(e.Location); if (now != _hover) { _hover = now; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }
    protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); int i = IndexAt(e.Location); if (e.Button == MouseButtons.Left && i >= 0) Selected = i; }

    protected override bool IsInputKey(Keys keys) => (keys & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keys);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        int delta = e.KeyCode switch { Keys.Left => -1, Keys.Right => 1, Keys.Up => -_columns, Keys.Down => _columns, _ => 0 };
        if (delta != 0) { Selected += delta; e.Handled = e.SuppressKeyPress = true; }
        base.OnKeyDown(e);
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var all = new Rectangle(0, 0, Width - 1, Height - 1);
        bool buttons = StretchColumns > 0;
        if (_outline && buttons) { PaintOutline(g); return; }
        if (!buttons) using (var trough = RoundedRect(all, Dp(Radius))) using (var fill = new SolidBrush(Theme.SurfaceInput)) g.FillPath(fill, trough);
        int inset = Dp(InsetAt96), w = Width / _columns;
        for (int i = 0; i < _items.Length; i++)
        {
            var cell = new Rectangle(i % _columns * w + inset, i / _columns * Dp(HeightAt96) + inset, w - 2 * inset, Dp(HeightAt96) - 1 - 2 * inset);
            bool on = i == _selected;
            if (buttons || on || i == _hover || i == _previous && _selectionMotion.Value < 1)
            {
                using var path = RoundedRect(cell, Dp(Radius) - 2);
                float weight = on ? _selectionMotion.Value : i == _previous ? 1 - _selectionMotion.Value : 0;
                using var fill = new SolidBrush(UiMotion.Blend(i == _hover ? Theme.Neutral : Theme.SurfaceInput, Theme.Accent, weight));
                g.FillPath(fill, path);
            }
            if (on && Focused) ControlPaint.DrawFocusRectangle(g, cell);
            var textRect = TextAndBadge(i, cell, out var pill, true, 0);
            TextRenderer.DrawText(g, _items[i], Font, textRect, on ? Theme.AccentText : i == _hover ? Theme.Text : Theme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            DrawBadge(g, i, pill, on);
        }
    }

    void PaintOutline(Graphics g)
    {
        int w = Width / _columns, inset = Dp(3);
        for (int i = 0; i < _items.Length; i++)
        {
            var cell = new Rectangle(i % _columns * w + inset, i / _columns * RowHeight + inset, w - 2 * inset - 1, RowHeight - 2 * inset - 1);
            bool on = i == _selected;
            using (var path = RoundedRect(cell, Dp(10)))
            {
                using (var fill = new SolidBrush(on ? Theme.CardSelected : i == _hover ? Theme.SurfaceRaised : Theme.SurfaceInput)) g.FillPath(fill, path);
                using var pen = new Pen(on ? Theme.CardSelectedBorder : Theme.CardBorder, on ? Math.Max(2f, Dp(2)) : Math.Max(1.5f, Dp(2) * 0.75f));
                g.DrawPath(pen, path);
            }
            if (on && Focused) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(cell, -Dp(3), -Dp(3)));
            int ring = Dp(18), ringX = cell.X + Dp(16), ringY = cell.Y + (cell.Height - ring) / 2;
            using (var pen = new Pen(on ? Theme.CardSelectedBorder : Theme.TextMuted, Math.Max(1.5f, Dp(2) * 0.75f))) g.DrawEllipse(pen, ringX, ringY, ring, ring);
            if (on) using (var dot = new SolidBrush(Theme.CardSelectedBorder)) g.FillEllipse(dot, ringX + Dp(5), ringY + Dp(5), ring - Dp(10), ring - Dp(10));
            var avail = new Rectangle(ringX + ring + Dp(12), cell.Y, Math.Max(1, cell.Right - ringX - ring - Dp(20)), cell.Height);
            var textRect = TextAndBadge(i, avail, out var pill, false, avail.X);
            TextRenderer.DrawText(g, _items[i], Font, textRect, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            DrawBadge(g, i, pill, false);
        }
    }

    protected override void Dispose(bool disposing) { if (disposing) _selectionMotion.Dispose(); base.Dispose(disposing); }

    static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = Math.Max(2, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
