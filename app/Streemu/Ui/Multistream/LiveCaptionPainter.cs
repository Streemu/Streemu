using Streemu.Util;

namespace Streemu.Ui.Multistream;

/// <summary>
/// Desenha o cabeçalho de uma live como no desenho: avatar, nome em negrito, o resto ("jogo · ao vivo há...") esmaecido e a pílula vermelha AO VIVO na
/// ponta direita. Serve à faixa do palco com várias lives (<see cref="StageCanvas"/>) e à janela por cima do vídeo com uma live só (<see cref="LiveHeaderOverlay"/>).
/// Uso: <c>LiveCaptionPainter.Draw(g, area, caption, Font, n =&gt; UiScale.Dp(this, n));</c>
/// </summary>
public static class LiveCaptionPainter
{
    /// <summary>Onde fica a pílula AO VIVO dentro do cabeçalho <paramref name="r"/>. Ex.: <c>PillBounds(area, dp)</c>.</summary>
    public static Rectangle PillBounds(Rectangle r, Func<int, int> dp)
    {
        int width = dp(84), height = dp(22), gap = dp(8);
        return new Rectangle(r.Right - width - gap, r.Y + (r.Height - height) / 2, width, height);
    }

    /// <summary>Cabeçalho inteiro em <paramref name="r"/>.</summary>
    public static void Draw(Graphics g, Rectangle r, StageCaption caption, Font font, Func<int, int> dp)
    {
        string text = caption.Text;
        int gap = dp(8);
        var pill = PillBounds(r, dp);
        DrawPill(g, pill, font, dp);
        int avatar = r.Height - 2 * dp(4);
        int split = text.IndexOf(" · ", StringComparison.Ordinal);
        Streemu.Ui.Avatars.AvatarBadge.Draw(g, new Rectangle(r.X + gap, r.Y + (r.Height - avatar) / 2, avatar, avatar), caption.Avatar, split < 0 ? text : text[..split], 0);
        int textX = r.X + gap + avatar + gap;
        DrawText(g, new Rectangle(textX, r.Y, Math.Max(0, pill.X - textX - gap), r.Height), text, font);
    }

    /// <summary>Só a pílula vermelha "● AO VIVO", ocupando <paramref name="pill"/>.</summary>
    public static void DrawPill(Graphics g, Rectangle pill, Font font, Func<int, int> dp)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = Theme.RoundedPath(pill, pill.Height / 2)) using (var fill = new SolidBrush(Theme.Error)) g.FillPath(fill, path);
        using (var dot = new SolidBrush(Theme.OnMedia)) g.FillEllipse(dot, new Rectangle(pill.X + dp(10), pill.Y + (pill.Height - dp(6)) / 2, dp(6), dp(6)));
        using var small = new Font(font.FontFamily, font.Size * 0.85f, FontStyle.Bold);
        TextRenderer.DrawText(g, "AO VIVO", small, new Rectangle(pill.X + dp(20), pill.Y, pill.Width - dp(22), pill.Height), Theme.OnMedia, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    static void DrawText(Graphics g, Rectangle area, string text, Font font)
    {
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        int split = text.IndexOf(" · ", StringComparison.Ordinal);
        string name = split < 0 ? text : text[..split], rest = split < 0 ? "" : text[split..];
        using var bold = new Font(font, FontStyle.Bold);
        int nameWidth = Math.Min(area.Width, TextRenderer.MeasureText(g, name, bold, Size.Empty, TextFormatFlags.NoPadding).Width);
        TextRenderer.DrawText(g, name, bold, new Rectangle(area.X, area.Y, nameWidth, area.Height), Theme.Text, flags);
        if (rest.Length > 0) TextRenderer.DrawText(g, rest, font, new Rectangle(area.X + nameWidth, area.Y, Math.Max(0, area.Width - nameWidth), area.Height), Theme.TextMuted, flags);
    }
}
