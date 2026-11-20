using Streemu.Util;

namespace Streemu.Ui.Multistream;

/// <summary>
/// Desenha o cabeçalho de uma live como no desenho: avatar, nome em negrito, o resto ("jogo · ao vivo há...") esmaecido e a pílula vermelha AO VIVO na
/// ponta direita. Serve à faixa do palco com várias lives (<see cref="StageCanvas"/>) e à janela por cima do vídeo com uma live só (<see cref="LiveHeaderOverlay"/>).
/// Uso: <c>LiveCaptionPainter.Draw(g, area, caption, Font, n =&gt; UiScale.Dp(this, n));</c>
/// </summary>
public static class LiveCaptionPainter
{
    const int FullPillAt96 = 84, PillHeightAt96 = 22, GapAt96 = 8;
    /// <summary>O mínimo do nome que precisa aparecer (umas 3 letras e as reticências; a fonte cresce junto com o Ctrl +): menos que isso, o texto da pílula e a foto saem antes.</summary>
    public const int MinNameAt96 = 32;

    /// <summary>Onde cada parte do cabeçalho fica: a foto (vazia quando não cabe), o texto e a pílula AO VIVO.</summary>
    public readonly record struct CaptionParts(Rectangle Avatar, Rectangle Text, Rectangle Pill);

    /// <summary>
    /// Divide o cabeçalho <paramref name="r"/>. Em faixa estreita sai primeiro o texto da pílula (fica só o ponto vermelho), depois a foto e por último o
    /// ponto (todas as peças do palco estão ao vivo; o nome é o que diz quem é quem). Inteiros, cobriam o nome nas peças pequenas do destaque com a
    /// interface ampliada (bateria de estresse, 2026-11-20). Pílula vazia = não desenhar. Ex.: <c>Parts(area, dp).Pill</c>.
    /// </summary>
    public static CaptionParts Parts(Rectangle r, Func<int, int> dp)
    {
        int gap = dp(GapAt96), avatar = r.Height - 2 * dp(4), dot = dp(PillHeightAt96), name = dp(MinNameAt96);
        int pillWidth = r.Width >= gap + avatar + gap + name + gap + dp(FullPillAt96) + gap ? dp(FullPillAt96) : dot;
        bool withAvatar = r.Width >= gap + avatar + gap + name + gap + pillWidth + gap;
        bool withPill = withAvatar || r.Width >= gap + name + gap + pillWidth + gap;
        var pill = withPill ? new Rectangle(r.Right - pillWidth - gap, r.Y + (r.Height - dot) / 2, pillWidth, dot) : Rectangle.Empty;
        var face = withAvatar ? new Rectangle(r.X + gap, r.Y + (r.Height - avatar) / 2, avatar, avatar) : Rectangle.Empty;
        int textX = withAvatar ? face.Right + gap : r.X + gap, textRight = withPill ? pill.X - gap : r.Right - gap;
        return new CaptionParts(face, new Rectangle(textX, r.Y, Math.Max(0, textRight - textX), r.Height), pill);
    }

    /// <summary>Onde fica a pílula AO VIVO dentro do cabeçalho <paramref name="r"/>. Ex.: <c>PillBounds(area, dp)</c>.</summary>
    public static Rectangle PillBounds(Rectangle r, Func<int, int> dp) => Parts(r, dp).Pill;

    /// <summary>
    /// Cabeçalho inteiro em <paramref name="r"/>. <paramref name="onStage"/>: desenhado sobre o preto do palco (faixa com várias lives), que não muda com o
    /// tema; senão sobre o fundo da janela do cabeçalho, que muda.
    /// </summary>
    public static void Draw(Graphics g, Rectangle r, StageCaption caption, Font font, Func<int, int> dp, bool onStage)
    {
        string text = caption.Text;
        var parts = Parts(r, dp);
        if (!parts.Pill.IsEmpty) DrawPill(g, parts.Pill, font, dp);
        int split = text.IndexOf(" · ", StringComparison.Ordinal);
        if (!parts.Avatar.IsEmpty) Streemu.Ui.Avatars.AvatarBadge.Draw(g, parts.Avatar, caption.Avatar, split < 0 ? text : text[..split], 0);
        Color nameInk = onStage ? Theme.StageText : Theme.Text, restInk = onStage ? Theme.StageTextMuted : Theme.TextMuted;
        DrawText(g, parts.Text, text, font, nameInk, restInk);
    }

    /// <summary>Só a pílula vermelha "● AO VIVO", ocupando <paramref name="pill"/>.</summary>
    public static void DrawPill(Graphics g, Rectangle pill, Font font, Func<int, int> dp)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = Theme.RoundedPath(pill, pill.Height / 2)) using (var fill = new SolidBrush(Theme.Error)) g.FillPath(fill, path);
        bool compact = pill.Width < dp(FullPillAt96) / 2; // só o ponto (PillBounds): sem lugar para o texto
        int dotX = compact ? pill.X + (pill.Width - dp(6)) / 2 : pill.X + dp(10);
        using (var dot = new SolidBrush(Theme.OnMedia)) g.FillEllipse(dot, new Rectangle(dotX, pill.Y + (pill.Height - dp(6)) / 2, dp(6), dp(6)));
        if (compact) return;
        using var small = new Font(font.FontFamily, font.Size * 0.85f, FontStyle.Bold);
        TextRenderer.DrawText(g, "AO VIVO", small, new Rectangle(pill.X + dp(20), pill.Y, pill.Width - dp(22), pill.Height), Theme.OnMedia, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    static void DrawText(Graphics g, Rectangle area, string text, Font font, Color nameInk, Color restInk)
    {
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        int split = text.IndexOf(" · ", StringComparison.Ordinal);
        string name = split < 0 ? text : text[..split], rest = split < 0 ? "" : text[split..];
        using var bold = new Font(font, FontStyle.Bold);
        int nameWidth = Math.Min(area.Width, TextRenderer.MeasureText(g, name, bold, Size.Empty, TextFormatFlags.NoPadding).Width);
        TextRenderer.DrawText(g, name, bold, new Rectangle(area.X, area.Y, nameWidth, area.Height), nameInk, flags);
        if (rest.Length > 0) TextRenderer.DrawText(g, rest, font, new Rectangle(area.X + nameWidth, area.Y, Math.Max(0, area.Width - nameWidth), area.Height), restInk, flags);
    }
}
