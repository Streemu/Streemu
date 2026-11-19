using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>Interruptor que mantém o comportamento nativo de CheckBox, inclusive teclado e acessibilidade.</summary>
public sealed class ToggleSwitch : CheckBox
{
    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = true;
        Cursor = Cursors.Hand;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int gap = UiScale.Dp(this, UiMetrics.ToggleText);
        int width = MaximumSize.Width > gap ? MaximumSize.Width - gap : int.MaxValue;
        var text = TextRenderer.MeasureText(Text, Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        return new Size(text.Width + gap, Math.Max(UiScale.Dp(this, 28), text.Height) + UiScale.Dp(this, 8));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int trackWidth = UiScale.Dp(this, UiMetrics.ToggleWidth), trackHeight = UiScale.Dp(this, UiMetrics.ToggleHeight), inset = UiScale.Dp(this, UiMetrics.ToggleInset);
        var track = new Rectangle(0, (Height - trackHeight) / 2, trackWidth, trackHeight);
        using var path = Theme.RoundedPath(track, trackHeight / 2);
        using var fill = new SolidBrush(Checked && Enabled ? Theme.Accent : Theme.Neutral);
        using var thumb = new SolidBrush(Enabled ? Theme.Text : Theme.TextMuted);
        g.FillPath(fill, path);
        int diameter = trackHeight - inset * 2;
        g.FillEllipse(thumb, Checked ? track.Right - inset - diameter : track.X + inset, track.Y + inset, diameter, diameter);
        var label = new Rectangle(UiScale.Dp(this, UiMetrics.ToggleText), 0, Math.Max(1, Width - UiScale.Dp(this, UiMetrics.ToggleText)), Height);
        // NoPadding: o texto começa exatamente em ToggleText, o mesmo lugar do texto das notas embaixo do interruptor
        TextRenderer.DrawText(g, Text, Font, label, Enabled ? ForeColor : Theme.TextMuted, TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -1, -1));
    }
}
