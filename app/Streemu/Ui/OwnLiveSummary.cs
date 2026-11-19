using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// Resumo da própria live no painel Transmitir (desenho "Sua transmissão"): título com a pílula "AO VIVO", o jogo com a qualidade e quantos assistem.
/// Uso: <c>resumo.Update("Dragon's Dogma 2", "1080p 60 FPS", 2);</c>
/// </summary>
public sealed class OwnLiveSummary : Control
{
    string _game = "", _quality = "";
    int _watching;

    public OwnLiveSummary()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(Dp(320), Dp(112));
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
    }

    int Dp(int at96) => UiScale.Dp(this, at96);

    public void Update(string game, string quality, int watching)
    {
        if (game == _game && quality == _quality && watching == _watching) return;
        (_game, _quality, _watching) = (game, quality, watching);
        AccessibleName = $"Sua transmissão ao vivo: {game}, {quality}, {watching} assistindo";
        Invalidate();
    }

    public void FitWidth(int width) => Size = new Size(Math.Max(1, width), Dp(112));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
        int line = Dp(30);
        using var title = new Font(Font.FontFamily, Font.Size * 1.25f, FontStyle.Bold);
        var titleSize = TextRenderer.MeasureText(g, "Sua transmissão", title);
        TextRenderer.DrawText(g, "Sua transmissão", title, new Rectangle(0, 0, titleSize.Width + Dp(4), line), Theme.Text, flags);
        DrawLivePill(g, new Rectangle(titleSize.Width + Dp(14), (line - Dp(22)) / 2, Dp(84), Dp(22)));
        string detail = string.IsNullOrEmpty(_quality) ? _game : $"{_game} · {_quality}";
        TextRenderer.DrawText(g, detail, Font, new Rectangle(0, line + Dp(4), Width, line), Theme.TextMuted, flags);
        UiIcons.Draw(g, UiIcon.Eye, new Rectangle(0, line * 2 + Dp(8), Dp(18), Dp(18)), Theme.Accent);
        TextRenderer.DrawText(g, _watching == 1 ? "1 assistindo" : $"{_watching} assistindo", Font, new Rectangle(Dp(26), line * 2 + Dp(2), Width - Dp(26), line), Theme.Text, flags);
    }

    void DrawLivePill(Graphics g, Rectangle r)
    {
        using (var path = Theme.RoundedPath(r, r.Height / 2)) using (var fill = new SolidBrush(Theme.Error)) g.FillPath(fill, path);
        using (var dot = new SolidBrush(Theme.AccentText)) g.FillEllipse(dot, new Rectangle(r.X + Dp(10), r.Y + (r.Height - Dp(6)) / 2, Dp(6), Dp(6)));
        using var small = new Font(Font.FontFamily, Font.Size * 0.85f, FontStyle.Bold);
        TextRenderer.DrawText(g, "AO VIVO", small, new Rectangle(r.X + Dp(20), r.Y, r.Width - Dp(22), r.Height), Theme.AccentText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}
