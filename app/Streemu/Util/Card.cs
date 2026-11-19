using System.Drawing.Drawing2D;

namespace Streemu.Util;

/// <summary>
/// Cartão sem borda, com cantos arredondados e título em negrito (no lugar do GroupBox do sistema).
/// Herda de GroupBox para o MainForm continuar tratando cada bloco como um grupo.
/// Uso: <c>var card = new Card("2. Transmitir");</c>
/// </summary>
public sealed class Card : GroupBox
{
    const int Radius = 8;
    const int TitleHeight = 30;

    public Card(string title)
    {
        Text = title;
        AutoSize = true;
        Dock = DockStyle.Fill;
        Padding = new Padding(12, TitleHeight, 12, 10);
        Margin = new Padding(0, 0, 0, 10);
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Surface);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1));
        using var fill = new SolidBrush(Theme.SurfaceRaised);
        e.Graphics.FillPath(fill, path);
        using var bold = new Font(Font, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, Text, bold, new Point(12, 8), Theme.Text);
    }

    static GraphicsPath RoundedRect(Rectangle r)
    {
        int d = Radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
