using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// Aviso numa caixa de borda arredondada, com o triângulo de alerta e o texto em âmbar (o "Não foi possível capturar o jogo" do desenho).
/// Uso: <c>box.Message = "Não foi possível capturar o jogo. Abra o jogo e tente de novo."; box.FitWidth(480);</c>
/// </summary>
public sealed class NoticeBox : Control
{
    string _message = "";

    public NoticeBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        AccessibleRole = AccessibleRole.Alert;
        TabStop = false;
        Visible = false;
    }

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Message
    {
        get => _message;
        set { _message = value; AccessibleName = value; Invalidate(); }
    }

    int Dp(int at96) => UiScale.Dp(this, at96);
    int TextWidth(int width) => Math.Max(1, width - Dp(56) - Dp(16));

    /// <summary>A altura acompanha o texto, quebrado na largura dada.</summary>
    public void FitWidth(int width)
    {
        var text = TextRenderer.MeasureText(_message, Font, new Size(TextWidth(width), 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        Size = new Size(Math.Max(1, width), Math.Max(Dp(52), text.Height + 2 * Dp(12)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var box = new Rectangle(1, 1, Width - 3, Height - 3);
        using (var path = Theme.RoundedPath(box, Dp(10)))
        {
            using (var fill = new SolidBrush(Theme.WarnSurface)) g.FillPath(fill, path);
            using var pen = new Pen(Theme.WarnBorder, Math.Max(1f, Dp(2) * 0.75f));
            g.DrawPath(pen, path);
        }
        UiIcons.Draw(g, UiIcon.Warning, new Rectangle(Dp(16), (Height - Dp(26)) / 2, Dp(26), Dp(26)), Theme.Warn);
        TextRenderer.DrawText(g, _message, Font, new Rectangle(Dp(56), 0, TextWidth(Width), Height), Theme.Warn, TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}
