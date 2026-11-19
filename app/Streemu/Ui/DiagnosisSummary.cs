using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>Resumo do envio, sem inferir a saúde do PC ou expor os identificadores do diagnóstico.</summary>
public sealed class DiagnosisSummary : Control
{
    DiagState _state;
    string _title = "Preparando diagnóstico", _detail = "Ligando o diagnóstico.";
    readonly Font _heading = UiTypography.HeadingFont();

    public DiagnosisSummary()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 92;
        AccessibleRole = AccessibleRole.StaticText;
    }

    public void SetStatus(DiagState state, string title, string detail)
    {
        if ((_state, _title, _detail) == (state, title, detail)) return;
        (_state, _title, _detail) = (state, title, detail);
        AccessibleName = title + ". " + detail;
        ResizeSummary();
        Invalidate();
    }

    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); ResizeSummary(); }

    void ResizeSummary()
    {
        if (Width <= Dp(64)) return;
        int textWidth = Width - Dp(64);
        int title = TextRenderer.MeasureText(_title, _heading, new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak).Height;
        int detail = TextRenderer.MeasureText(_detail, Font, new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak).Height;
        int height = Math.Max(Dp(UiMetrics.DiagnosisMinHeight), title + detail + Dp(14));
        if (Height != height) Height = height;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var dot = new Rectangle(0, Dp(4), Dp(UiMetrics.DiagnosisIcon), Dp(UiMetrics.DiagnosisIcon));
        Color color = _state == DiagState.Ok ? Theme.SuccessSurface : _state == DiagState.Fail ? Theme.Error : Theme.Neutral;
        using (var fill = new SolidBrush(color)) g.FillEllipse(fill, dot);
        if (_state == DiagState.Ok)
        {
            using var pen = new Pen(Theme.AccentText, Dp(3)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pen, [new Point(Dp(10), Dp(23)), new Point(Dp(17), Dp(30)), new Point(Dp(29), Dp(16))]);
        }
        else TextRenderer.DrawText(g, _state == DiagState.Fail ? "!" : "○", _heading, dot, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        int x = Dp(UiMetrics.DiagnosisText), width = Math.Max(1, Width - x);
        int titleHeight = TextRenderer.MeasureText(_title, _heading, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height;
        TextRenderer.DrawText(g, _title, _heading, new Rectangle(x, 0, width, titleHeight), Theme.Text, TextFormatFlags.WordBreak);
        TextRenderer.DrawText(g, _detail, Font, new Rectangle(x, titleHeight + Dp(5), width, Height - titleHeight - Dp(5)), Theme.TextMuted, TextFormatFlags.WordBreak);
    }

    int Dp(int value) => UiScale.Dp(this, value);
    protected override void Dispose(bool disposing) { if (disposing) _heading.Dispose(); base.Dispose(disposing); }
}
