using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// O estado de uma sessão como o cartão e a entrada do Cinema mostram: bolinha verde "Em reprodução", relógio amarelo "Aguardando",
/// bolinha apagada "Pausado". Vem do campo "estado" do anúncio do anfitrião ("rolando", "pausado", "aguardo").
/// </summary>
public readonly record struct CinemaStatus(string Text, Color Color, bool Clock)
{
    /// <example><code>CinemaStatus.Of((string?)anuncio["estado"]).Draw(g, this, area, Font, alignRight: true);</code></example>
    public static CinemaStatus Of(string? estado) => estado switch
    {
        "rolando" => new("Em reprodução", Theme.Ok, false),
        "pausado" => new("Pausado", Theme.TextMuted, false),
        _ => new("Aguardando", Theme.Warn, true),
    };

    const TextFormatFlags Line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;

    /// <summary>Largura do marcador mais o texto, para alinhar à direita.</summary>
    public int Width(Graphics g, Control owner, Font font) => Mark(owner) + UiScale.Dp(owner, 10) + TextRenderer.MeasureText(g, Text, font, Size.Empty, Line & ~TextFormatFlags.EndEllipsis).Width;

    public void Draw(Graphics g, Control owner, Rectangle area, Font font, bool alignRight)
    {
        int mark = Mark(owner), x = alignRight ? Math.Max(area.X, area.Right - Width(g, owner, font)) : area.X;
        int cy = area.Y + area.Height / 2;
        if (Clock) UiIcons.Draw(g, UiIcon.Clock, new Rectangle(x, cy - mark / 2, mark, mark), Color);
        else
        {
            int dot = UiScale.Dp(owner, 10);
            using var brush = new SolidBrush(Color);
            g.FillEllipse(brush, x + (mark - dot) / 2, cy - dot / 2, dot, dot);
        }
        int textX = x + mark + UiScale.Dp(owner, 10);
        TextRenderer.DrawText(g, Text, font, new Rectangle(textX, area.Y, Math.Max(0, area.Right - textX), area.Height), Theme.Text, Line);
    }

    static int Mark(Control owner) => UiScale.Dp(owner, 20);
}
