using System.ComponentModel;
using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// Lista de opções uma embaixo da outra, cada uma com bolinha de escolha, ícone e texto, e a escolhida com o fundo em destaque (o "Som" do Transmitir).
/// Cada item carrega o seu valor: a ordem na tela é livre, o valor que sai é o do item escolhido.
/// Uso: <c>var som = new OptionList(("PC sem Discord", UiIcon.Monitor, 1), ("Só o app", UiIcon.Gamepad, 0)); som.Selected = 1; som.SelectedChanged += v =&gt; ...;</c>
/// </summary>
public sealed class OptionList : Control
{
    const int RowAt96 = 40, RadioAt96 = 16;
    readonly (string Text, UiIcon Icon, int Value)[] _items;
    int _selected, _hover = -1;
    bool _outline;

    /// <summary>Estilo da página Transmitir (desenho): cada linha é uma caixa de borda, com o ícone à esquerda e a bolinha de escolha à direita.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Outline { get => _outline; set { if (_outline == value) return; _outline = value; FitWidth(Width); Invalidate(); } }
    int RowHeight => Dp(_outline ? 50 : RowAt96);

    public event Action<int>? SelectedChanged;
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool CenterContent { get; set; }

    public OptionList(params (string Text, UiIcon Icon, int Value)[] items)
    {
        _items = items.Length > 0 ? items : throw new ArgumentException("uma lista de opções precisa de pelo menos 1 item (recebi 0)", nameof(items));
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.List;
        Margin = new Padding(0, 4, 0, 4);
        Size = new Size(Dp(240), RowHeight * _items.Length);
    }

    /// <summary>O valor do item escolhido (não a posição). Valor que não existe na lista é ignorado.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Selected
    {
        get => _items[_selected].Value;
        set
        {
            int index = Array.FindIndex(_items, i => i.Value == value);
            if (index < 0 || index == _selected) return;
            _selected = index;
            Invalidate();
            SelectedChanged?.Invoke(Selected);
        }
    }

    int Dp(int at96) => UiScale.Dp(this, at96);

    public void FitWidth(int width) => Size = new Size(Math.Max(1, width), RowHeight * _items.Length);

    int IndexAt(Point p) => ClientRectangle.Contains(p) ? Math.Min(_items.Length - 1, p.Y / RowHeight) : -1;

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int now = IndexAt(e.Location); if (now != _hover) { _hover = now; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int index = IndexAt(e.Location);
        if (e.Button == MouseButtons.Left && index >= 0) Selected = _items[index].Value;
    }

    protected override bool IsInputKey(Keys keys) => (keys & Keys.KeyCode) is Keys.Up or Keys.Down || base.IsInputKey(keys);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        int delta = e.KeyCode switch { Keys.Up => -1, Keys.Down => 1, _ => 0 };
        if (delta != 0) { Selected = _items[Math.Clamp(_selected + delta, 0, _items.Length - 1)].Value; e.Handled = e.SuppressKeyPress = true; }
        base.OnKeyDown(e);
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < _items.Length; i++) DrawRow(g, i, new Rectangle(0, i * RowHeight, Width - 1, RowHeight - 1));
    }

    void DrawOutlineRow(Graphics g, int index, Rectangle row)
    {
        bool on = index == _selected;
        var box = Rectangle.Inflate(row, -1, -Dp(3));
        using (var path = Theme.RoundedPath(box, Dp(10)))
        {
            using (var fill = new SolidBrush(on ? Theme.CardSelected : index == _hover ? Theme.SurfaceRaised : Theme.SurfaceInput)) g.FillPath(fill, path);
            using var pen = new Pen(on ? Theme.CardSelectedBorder : Theme.CardBorder, on ? Math.Max(2f, Dp(2)) : Math.Max(1.5f, Dp(2) * 0.75f));
            g.DrawPath(pen, path);
        }
        if (on && Focused) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(box, -Dp(3), -Dp(3)));
        int icon = Dp(22), radio = Dp(RadioAt96 + 2);
        // no desenho da página, "PC sem Discord" leva o alto-falante e "Só o app escolhido" a tela
        var glyph = _items[index].Icon switch { UiIcon.Monitor => UiIcon.Speaker, UiIcon.Gamepad => UiIcon.Monitor, var other => other };
        UiIcons.Draw(g, glyph, new Rectangle(box.X + Dp(16), box.Y + (box.Height - icon) / 2, icon, icon), Theme.Text);
        TextRenderer.DrawText(g, _items[index].Text, Font, new Rectangle(box.X + Dp(52), box.Y, Math.Max(1, box.Width - Dp(100)), box.Height), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        DrawRadio(g, new Rectangle(box.Right - Dp(16) - radio, box.Y + (box.Height - radio) / 2, radio, radio), on);
    }

    void DrawRow(Graphics g, int index, Rectangle row)
    {
        if (_outline) { DrawOutlineRow(g, index, row); return; }
        bool on = index == _selected;
        if (on || index == _hover)
        {
            using var path = Theme.RoundedPath(Rectangle.Inflate(row, -1, -1), Dp(UiMetrics.SmallRadius));
            using var fill = new SolidBrush(on ? Theme.OptionSelection : Theme.SurfaceInput);
            g.FillPath(fill, path);
        }
        if (on && Focused) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(row, -2, -2));
        if (CenterContent)
        {
            int textWidth = _items.Max(item => TextRenderer.MeasureText(g, item.Text, Font).Width);
            int offset = Math.Max(0, (row.Width - Dp(90) - textWidth) / 2);
            row = new Rectangle(row.X + offset, row.Y, row.Width - offset, row.Height);
        }
        DrawRadio(g, new Rectangle(row.X + Dp(14), row.Y + (row.Height - Dp(RadioAt96)) / 2, Dp(RadioAt96), Dp(RadioAt96)), on);
        var color = on ? Theme.Text : Theme.TextMuted;
        UiIcons.Draw(g, _items[index].Icon, new Rectangle(row.X + Dp(46), row.Y + (row.Height - Dp(20)) / 2, Dp(20), Dp(20)), color);
        TextRenderer.DrawText(g, _items[index].Text, Font, new Rectangle(row.X + Dp(80), row.Y, Math.Max(1, row.Width - Dp(90)), row.Height), color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    void DrawRadio(Graphics g, Rectangle r, bool on)
    {
        using (var ring = new Pen(on ? (_outline ? Theme.CardSelectedBorder : Theme.Accent) : Theme.TextMuted, Math.Max(1.5f, Dp(2) * 0.75f))) g.DrawEllipse(ring, r);
        if (!on) return;
        using var dot = new SolidBrush(_outline ? Theme.CardSelectedBorder : Theme.Accent);
        g.FillEllipse(dot, Rectangle.Inflate(r, -Dp(4), -Dp(4)));
    }
}
