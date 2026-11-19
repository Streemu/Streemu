using System.ComponentModel;
using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>Seletor fechado de categoria, com menu e teclado nativos, desenhado na paleta da interface.</summary>
public sealed class CategoryPicker : Button
{
    readonly ContextMenuStrip _menu = new();
    int _selectedIndex = -1;
    public List<string> Items { get; } = new();
    public event EventHandler? SelectedIndexChanged;

    public CategoryPicker()
    {
        AccessibleRole = AccessibleRole.ComboBox;
        AccessibleName = "Categoria";
        AccessibleDefaultActionDescription = "Abrir categorias";
        Cursor = Cursors.Hand;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        _menu.BackColor = Theme.SurfaceInput;
        _menu.ForeColor = Theme.Text;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value < 0 || value >= Items.Count || _selectedIndex == value) return;
            _selectedIndex = value;
            Text = Items[value];
            AccessibleDescription = "Categoria selecionada: " + Text;
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
    }

    public string? SelectedItem => _selectedIndex >= 0 ? Items[_selectedIndex] : null;

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        OpenMenu();
    }

    void OpenMenu()
    {
        foreach (ToolStripItem item in _menu.Items.Cast<ToolStripItem>().ToArray()) item.Dispose();
        _menu.Font = Font;
        for (int i = 0; i < Items.Count; i++)
        {
            int index = i;
            var item = new ToolStripMenuItem(Items[i]) { Checked = i == _selectedIndex };
            item.Click += (_, _) => { SelectedIndex = index; Focus(); };
            _menu.Items.Add(item);
        }
        _menu.BackColor = Theme.SurfaceInput; _menu.ForeColor = Theme.Text; // pode ter mudado o tema desde que o menu nasceu
        _menu.Show(this, new Point(0, Height));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Down or Keys.Up or Keys.F4) { OpenMenu(); e.Handled = e.SuppressKeyPress = true; }
        else base.OnKeyDown(e);
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = Theme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), UiScale.Dp(this, UiMetrics.SmallRadius));
        using var fill = new SolidBrush(Theme.SurfaceInput);
        using var edge = new Pen(Focused ? Theme.Accent : Theme.Border, Focused ? 2 : 1);
        g.FillPath(fill, shape);
        g.DrawPath(edge, shape);
        int pad = UiScale.Dp(this, UiMetrics.Gap);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(pad, 0, Math.Max(1, Width - pad * 4), Height), Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        UiIcons.Draw(g, UiIcon.ChevronDown, new Rectangle(Width - pad * 3, (Height - pad * 2) / 2, pad * 2, pad * 2), Theme.Text);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void Dispose(bool disposing) { if (disposing) _menu.Dispose(); base.Dispose(disposing); }
}
