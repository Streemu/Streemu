using Streemu.Util;

namespace Streemu.Ui;

/// <summary>Uma categoria visível, navegação fixa e rolagem independente do conteúdo.</summary>
public sealed class SettingsView : UserControl
{
    readonly Panel _navigation = new() { AutoScroll = true };
    readonly Panel _header = new();
    readonly Panel _body = new() { AutoScroll = true };
    readonly Label _title = new() { Text = "Configurações", AutoSize = true };
    readonly Label _categoryLabel = new() { Text = "Categoria", AutoSize = true };
    readonly CategoryPicker _category = new();
    readonly List<(string Name, Control Content, Button Button)> _pages = new();
    readonly Dictionary<string, int> _scroll = new();
    string _selected = "";
    bool _layingOut;
    bool _layingOutPage;

    public SettingsView()
    {
        Dock = DockStyle.Fill;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _title.Font = UiTypography.TitleFont();
        _header.Controls.AddRange([_title, _categoryLabel, _category]);
        Controls.AddRange([_body, _navigation, _header]);
        ScrollSkin.Attach(_body); ScrollSkin.Attach(_navigation);
        _category.SelectedIndexChanged += (_, _) => { if (_category.SelectedItem is string name) SelectCategory(name); };
        _body.ClientSizeChanged += (_, _) => LayoutPage();
    }

    /// <summary>O item da navegação lateral: sem o retângulo pontilhado de foco, que aparecia dentro do item selecionado depois do clique.</summary>
    sealed class CategoryButton : Button
    {
        protected override bool ShowFocusCues => false;
    }

    /// <summary>
    /// WS_EX_COMPOSITED: a árvore inteira de controles (cartões, interruptores, campos) é pintada fora da tela e aparece de uma vez. Sem isto, ao trocar
    /// de categoria o Windows mostrava por ~100 ms só o fundo de cada controle (blocos vazios) antes do texto, nos dois temas (vídeo de 2026-11-18).
    /// </summary>
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x02000000; return cp; }
    }

    public string SelectedCategory => _selected;
    public bool UsesCategorySelector => _category.Visible;

    internal void ApplyAppearance()
    {
        _navigation.BackColor = Theme.SurfaceRaised;
        foreach (var page in _pages) StyleCategoryButton(page.Button, page.Name == _selected);
        PerformLayout();
    }

    public void AddCategory(string name, Control content)
    {
        var button = new CategoryButton { Text = name, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = name };
        button.Click += (_, _) => SelectCategory(name);
        _navigation.Controls.Add(button);
        content.Visible = false;
        content.Margin = Padding.Empty;
        _body.Controls.Add(content);
        _pages.Add((name, content, button));
        _category.Items.Add(name);
        if (_pages.Count == 1) SelectCategory(name);
    }

    /// <summary>Uma página ganhou uma linha depois de montada (ex.: a conta no Perfil): mede e encaixa de novo na largura da categoria.</summary>
    public void RelayoutPage() => LayoutPage();

    public void SelectCategory(string name)
    {
        int index = _pages.FindIndex(p => p.Name == name);
        if (index < 0 || _selected == name) return;
        if (_selected.Length > 0) _scroll[_selected] = -_body.AutoScrollPosition.Y;
        _body.SuspendLayout();
        foreach (var page in _pages)
        {
            page.Content.Visible = page.Name == name;
            StyleCategoryButton(page.Button, page.Name == name);
        }
        _selected = name;
        _body.AutoScrollPosition = Point.Empty;
        _category.SelectedIndex = index;
        _body.ResumeLayout();
        LayoutPage();
        _body.AutoScrollPosition = new Point(0, _scroll.GetValueOrDefault(name));
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_layingOut) return;
        _layingOut = true;
        try
        {
            int pad = Dp(UiMetrics.PagePadding), nav = Dp(UiMetrics.CategoryWidth);
            // Uma coluna de campos de 640 dp e as folgas precisam caber ao lado das categorias.
            bool compact = ClientSize.Width < nav + Dp(UiMetrics.SettingsCompactWidth);
            _header.BackColor = _title.BackColor = compact ? Theme.Surface : Theme.SurfaceRaised;
            _navigation.Visible = !compact;
            _category.Visible = _categoryLabel.Visible = compact;
            _navigation.Bounds = new Rectangle(0, 0, compact ? 0 : nav, ClientSize.Height);
            _header.Bounds = new Rectangle(compact ? 0 : Dp(12), 0, compact ? ClientSize.Width : nav - Dp(24), compact ? Dp(UiMetrics.SettingsHeader) : Dp(76));
            _title.Location = new Point(compact ? pad : Dp(4), Dp(22));
            _categoryLabel.Location = new Point(pad, Dp(81));
            int labelWidth = _categoryLabel.PreferredWidth;
            _category.Bounds = new Rectangle(pad + labelWidth + Dp(16), Dp(74), Math.Max(Dp(120), ClientSize.Width - pad * 2 - labelWidth - Dp(16)), Dp(34));
            int y = Dp(90);
            foreach (var page in _pages)
            {
                page.Button.Bounds = new Rectangle(Dp(12), y, nav - Dp(24), Dp(UiMetrics.CategoryHeight));
                y += Dp(UiMetrics.CategoryStep);
            }
            _navigation.AutoScrollMinSize = new Size(0, y + Dp(16));
            _body.Bounds = new Rectangle(compact ? 0 : nav, compact ? _header.Height : 0,
                Math.Max(0, ClientSize.Width - (compact ? 0 : nav)), Math.Max(0, ClientSize.Height - (compact ? _header.Height : 0)));
        }
        finally { _layingOut = false; }
        LayoutPage();
    }

    void LayoutPage()
    {
        int index = _pages.FindIndex(p => p.Name == _selected);
        if (_layingOut || _layingOutPage || index < 0) return;
        _layingOutPage = true;
        try
        {
            var content = _pages[index].Content;
            int pad = Dp(UiMetrics.PagePadding), width = Math.Max(Dp(180), Math.Min(Dp(UiMetrics.SettingsContentMax), _body.ClientSize.Width - pad * 2 - SystemInformation.VerticalScrollBarWidth));
            _body.SuspendLayout();
            SettingsLayout.Constrain(content, width);
            content.Location = new Point(pad, pad + _body.AutoScrollPosition.Y);
            _body.ResumeLayout();
        }
        finally { _layingOutPage = false; }
    }

    int Dp(int value) => UiScale.Dp(this, value);

    static void StyleCategoryButton(Button button, bool selected)
    {
        Theme.StyleButton(button, selected);
        if (!selected) button.BackColor = Theme.SurfaceRaised;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _title.Font.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Limita os controles reais à largura disponível, sem reduzir a fonte.</summary>
internal static class SettingsLayout
{
    sealed class InitialWidth { public int Value; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, InitialWidth> InitialWidths = new();

    public static void Constrain(Control control, int width)
    {
        width = Math.Max(1, width);
        if (control is Label or CheckBox)
        {
            control.MaximumSize = new Size(width, 0);
            if (control is Label { AutoSize: false }) control.Width = Math.Min(control.Width, width);
            return;
        }
        if (control is CampoArredondado campo) { campo.Width = Math.Min(campo.LarguraPadrao, width); return; }
        if (control is Segmented segmented) { segmented.FitWidth(width); return; }
        if (control is OptionList list) { list.FitWidth(width); return; }
        if (control is FlowLayoutPanel flow)
        {
            if (flow.FlowDirection == FlowDirection.LeftToRight) flow.WrapContents = true;
            flow.MaximumSize = new Size(width, 0);
            flow.Width = width;
        }
        else if (control is TableLayoutPanel table)
        {
            table.MaximumSize = new Size(width, 0);
            table.Width = width;
        }
        else if (control is TextBox or ComboBox or TrackBar)
        {
            int initial = InitialWidths.GetValue(control, c => new InitialWidth { Value = (int)Math.Round(c.Width * 96.0 / c.DeviceDpi) }).Value;
            control.Width = Math.Min(UiScale.Dp(control, initial), width);
            return;
        }
        else if (control is Button)
        {
            control.MaximumSize = new Size(width, 0);
            return;
        }
        else if (control is DiagnosisSummary || control.AccessibleRole == AccessibleRole.Separator) control.Width = width;
        else if (control.Controls.Count == 0) return;
        else control.Width = width;
        int inner = Math.Max(1, width - control.Padding.Horizontal);
        foreach (Control child in control.Controls) Constrain(child, Math.Max(1, inner - child.Margin.Horizontal));
        if (control.AutoSize) control.PerformLayout();
    }
}
