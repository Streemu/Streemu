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

    int Dp(int at96) => UiScale.Dp(this, at96);

    /// <summary>Todos os itens têm a largura do maior texto (mais folga), nunca menos que o mínimo: o conjunto fica simétrico.</summary>
    int ItemWidth => Math.Max(Dp(MinItemAt96), _items.Max(i => TextRenderer.MeasureText(i, Font).Width) + 2 * Dp(PadAt96));

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
            TextRenderer.DrawText(g, _items[i], Font, cell, on ? Theme.AccentText : i == _hover ? Theme.Text : Theme.TextMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
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
            TextRenderer.DrawText(g, _items[i], Font, new Rectangle(ringX + ring + Dp(12), cell.Y, Math.Max(1, cell.Right - ringX - ring - Dp(20)), cell.Height), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
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
