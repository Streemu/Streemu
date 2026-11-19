using Streemu.Util;

namespace Streemu.Ui;

/// <summary>Expansor cuja altura acompanha o conteúdo, inclusive dentro de tabelas com largura percentual.</summary>
public sealed class SettingsDisclosure : Panel
{
    readonly Button _toggle;
    readonly Control[] _content;
    bool _open, _arranging;

    public SettingsDisclosure(string title, params Control[] content)
    {
        _content = content;
        _toggle = new Button { Text = title + "   ›", AccessibleName = title, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty }; // a margem padrão de 3 px deixava o botão 6 px mais estreito que os cartões
        Controls.Add(_toggle);
        foreach (var control in content) { control.Visible = false; control.Dock = DockStyle.None; Controls.Add(control); }
        _toggle.Click += (_, _) =>
        {
            _open = !_open;
            foreach (var control in content) control.Visible = _open;
            _toggle.Text = title + (_open ? "   ⌄" : "   ›");
            PerformLayout();
        };
        Height = UiScale.Dp(this, UiMetrics.DisclosureHeight);
    }

    public override Size GetPreferredSize(Size proposedSize) => new(Width, Height);

    /// <summary>A página muda a largura DEPOIS de criar o expansor: sem refazer o layout, o botão ficava na largura antiga (6 px mais estreito que os cartões).</summary>
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); PerformLayout(); }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_arranging) return;
        _arranging = true;
        try
        {
            int y = UiScale.Dp(this, UiMetrics.DisclosureHeight), gap = UiScale.Dp(this, UiMetrics.Gap);
            _toggle.Bounds = new Rectangle(0, 0, ClientSize.Width, y);
            if (_open)
                foreach (var control in _content)
                {
                    int height = control.AutoSize ? control.GetPreferredSize(new Size(ClientSize.Width, 0)).Height : control.Height;
                    control.Bounds = new Rectangle(0, y + gap, ClientSize.Width, height);
                    y += height + gap;
                }
            if (Height != y) Height = y;
        }
        finally { _arranging = false; }
    }
}
