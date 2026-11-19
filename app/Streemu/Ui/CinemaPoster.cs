using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// A capa de uma sessão do Cinema: a imagem do filme que o anfitrião mandou (spec 017) ou, enquanto ela não chega ou quando não dá para tirá-la,
/// um degradê da paleta, diferente para cada filme, com o ícone do Cinema.
/// </summary>
public static class CinemaPoster
{
    /// <summary>Desenha a capa em <paramref name="area"/>, cortada por <paramref name="clip"/> (o cartão arredondado). Sem <paramref name="cover"/>, o degradê.</summary>
    /// <example><code>CinemaPoster.Draw(g, new Rectangle(0, 0, 320, 180), path, "123-abc", capa);</code></example>
    public static void Draw(Graphics g, Rectangle area, GraphicsPath clip, string filmKey, Image? cover = null)
    {
        if (area.Width <= 0 || area.Height <= 0) return;
        var saved = g.Save();
        g.SetClip(clip);
        g.IntersectClip(area); // o brilho é maior que a capa: sem isso, vaza para o resto do cartão
        if (cover != null) { DrawCover(g, area, cover); g.Restore(saved); return; }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int hash = filmKey.Aggregate(23, (h, c) => unchecked(h * 31 + c));
        float angle = 20 + (uint)hash % 50;
        var glow = UiMotion.Blend(Theme.Accent, Theme.CardSelectedBorder, (uint)(hash >> 8) % 100 / 100f);
        using (var fill = new LinearGradientBrush(area, Theme.Backdrop, Theme.CardSelected, angle)) g.FillRectangle(fill, area);
        DrawGlow(g, area, glow, hash);
        int icon = Math.Min(area.Height / 3, area.Width / 6);
        var iconArea = new Rectangle(area.X + (area.Width - icon) / 2, area.Y + (area.Height - icon) / 2, icon, icon);
        UiIcons.Draw(g, UiIcon.Cinema, iconArea, Theme.PosterIcon);
        g.Restore(saved);
    }

    // preenche a área e corta pelo centro (D7): o cartão (~2,4:1) é mais largo que o quadro do filme (16:9), e um pôster em pé perde em cima e embaixo
    static void DrawCover(Graphics g, Rectangle area, Image cover)
    {
        float scale = Math.Max((float)area.Width / cover.Width, (float)area.Height / cover.Height);
        float w = area.Width / scale, h = area.Height / scale;
        var source = new RectangleF((cover.Width - w) / 2, (cover.Height - h) / 2, w, h);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(cover, area, source, GraphicsUnit.Pixel);
    }

    // uma mancha de luz num canto que muda com o filme: duas capas lado a lado não ficam iguais
    static void DrawGlow(Graphics g, Rectangle area, Color glow, int hash)
    {
        float cx = area.X + area.Width * (0.2f + (uint)hash % 60 / 100f), cy = area.Y + area.Height * (0.25f + (uint)(hash >> 4) % 50 / 100f);
        float r = Math.Max(area.Width, area.Height) * 0.55f;
        using var path = new GraphicsPath();
        path.AddEllipse(cx - r, cy - r, r * 2, r * 2);
        using var brush = new PathGradientBrush(path) { CenterColor = Theme.PosterGlow(glow), SurroundColors = [Theme.PosterGlowEdge(glow)] };
        g.FillPath(brush, path);
    }
}
