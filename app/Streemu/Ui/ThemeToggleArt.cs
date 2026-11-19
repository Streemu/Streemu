using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// O botão de tema (pacote "Botão": tema-escuro.svg e tema-claro.svg) desenhado em GDI+ na geometria original de 104 × 52, escalada sem deformar.
/// <c>progress</c> vai de 0 (escuro: puxador à esquerda com a lua) a 1 (claro: puxador à direita com o sol); os valores no meio são a animação,
/// e cores e ícones se misturam por ele, então a arte nunca depende de qual tema está ativo.
/// Uso: <c>ThemeToggleArt.Draw(g, new Rectangle(x, y, 48, 24), progress: 1, hover: false, focused: false);</c>
/// </summary>
public static class ThemeToggleArt
{
    public const int BaseWidth = 104, BaseHeight = 52;
    const float ThumbRadius = 20, ThumbTravel = 52, ThumbStart = 26, Middle = 26;
    static ThemePalette Dark => ThemePalette.Dark;
    static ThemePalette Light => ThemePalette.Light;

    public static void Draw(Graphics g, Rectangle bounds, float progress, bool hover, bool focused)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(bounds.X, bounds.Y);
        g.ScaleTransform(bounds.Width / (float)BaseWidth, bounds.Height / (float)BaseHeight);
        if (hover) DrawHalo(g, Mix(Dark.ToggleFocus, Light.ToggleFocus, progress), 3, 36);
        DrawTrack(g, progress);
        DrawIdleIcons(g, progress);
        DrawThumb(g, ThumbStart + ThumbTravel * progress, progress);
        if (focused) DrawHalo(g, Mix(Dark.ToggleFocus, Light.ToggleFocus, progress), 4, 255, ring: true);
        g.Restore(state);
    }

    /// <summary>Só o puxador (o trilho recolhido da lateral não comporta o botão inteiro): o mesmo círculo com a lua ou o sol.</summary>
    public static void DrawThumbOnly(Graphics g, Rectangle box, float progress)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = Math.Min(box.Width, box.Height) / (ThumbRadius * 2);
        g.TranslateTransform(box.X + box.Width / 2f, box.Y + box.Height / 2f);
        g.ScaleTransform(scale, scale);
        g.TranslateTransform(-ThumbStart, -Middle);
        DrawThumb(g, ThumbStart, progress);
        g.Restore(state);
    }

    static Color Mix(Color a, Color b, float t) => UiMotion.Blend(a, b, Math.Clamp(t, 0, 1));
    static Color Fade(Color c, float opacity) => Color.FromArgb((int)(Math.Clamp(opacity, 0, 1) * c.A), c);

    static void DrawHalo(Graphics g, Color color, float grow, int alpha, bool ring = false)
    {
        using var path = Theme.RoundedPath(Rectangle.Round(new RectangleF(1 - grow, 1 - grow, 102 + 2 * grow, 50 + 2 * grow)), 25 + (int)grow);
        if (ring) { using var pen = new Pen(Fade(color, alpha / 255f), 3); g.DrawPath(pen, path); return; }
        using var brush = new SolidBrush(Color.FromArgb(alpha, color));
        g.FillPath(brush, path);
    }

    static void DrawTrack(Graphics g, float p)
    {
        using var path = Theme.RoundedPath(new Rectangle(1, 1, 102, 50), 25);
        using var fill = new LinearGradientBrush(new PointF(8, 4), new PointF(95, 48), Mix(Dark.ToggleTrackA, Light.ToggleTrackA, p), Mix(Dark.ToggleTrackB, Light.ToggleTrackB, p));
        using var edge = new Pen(Mix(Dark.ToggleTrackBorder, Light.ToggleTrackBorder, p), 2);
        g.FillPath(fill, path);
        g.DrawPath(edge, path);
    }

    /// <summary>Os ícones do lado oposto ao puxador (sol à direita no escuro, lua à esquerda no claro) e as estrelinhas de cada tema.</summary>
    static void DrawIdleIcons(Graphics g, float p)
    {
        float sunOpacity = (1 - p) * .72f, moonOpacity = p * .8f;
        DrawSun(g, 78, Fade(Dark.ToggleIdle, sunOpacity), 1.7f, 3.2f);
        using (var moon = MoonPath()) using (var brush = new SolidBrush(Fade(Light.ToggleIdle, moonOpacity))) g.FillPath(brush, moon);
        FillStar(g, 32.1f, 14.6f, 1.5f, .45f, Fade(Light.ToggleIdle, moonOpacity));
        using (var brush = new SolidBrush(Fade(Dark.ToggleSparkle, (1 - p) * .8f))) { FillStar(g, 93, 13.4f, 2.4f, .65f, brush.Color); g.FillEllipse(brush, 86.4f, 37.9f, 2.2f, 2.2f); }
        using (var brush = new SolidBrush(Fade(Light.ToggleSparkle, p * .78f))) { FillStar(g, 12, 39.4f, 2.4f, .65f, brush.Color); g.FillEllipse(brush, 15.9f, 12.9f, 2.2f, 2.2f); }
    }

    static void DrawThumb(Graphics g, float cx, float p)
    {
        var area = new RectangleF(cx - ThumbRadius, Middle - ThumbRadius, ThumbRadius * 2, ThumbRadius * 2);
        DrawThumbShadow(g, area, Mix(Dark.ToggleShadow, Light.ToggleShadow, p));
        using (var fill = new LinearGradientBrush(new PointF(area.X + 4, area.Y), new PointF(area.Right - 4, area.Bottom), Mix(Dark.ToggleThumbA, Light.ToggleThumbA, p), Mix(Dark.ToggleThumbB, Light.ToggleThumbB, p)))
            g.FillEllipse(fill, area);
        using (var rim = new Pen(Dark.ToggleRim, 1)) g.DrawEllipse(rim, Rectangle.Round(RectangleF.Inflate(area, -.5f, -.5f)));
        var state = g.Save();
        g.TranslateTransform(cx - ThumbStart, 0);
        using (var moon = MoonPath()) using (var brush = new SolidBrush(Fade(Dark.ToggleMoon, 1 - p))) g.FillPath(brush, moon);
        FillStar(g, 32.1f, 14.6f, 1.5f, .45f, Fade(Dark.ToggleMoon, 1 - p));
        g.Restore(state);
        DrawSun(g, cx, Fade(Dark.ToggleSun, p), 1.8f, 3.25f);
    }

    /// <summary>Sombra suave sob o puxador (o desfoque do SVG, aproximado por três camadas).</summary>
    static void DrawThumbShadow(Graphics g, RectangleF area, Color color)
    {
        for (int i = 3; i >= 1; i--)
        {
            using var brush = new SolidBrush(Fade(color, .4f));
            g.FillEllipse(brush, RectangleF.Inflate(new RectangleF(area.X, area.Y + 2, area.Width, area.Height), i, i));
        }
    }

    static void DrawSun(Graphics g, float cx, Color color, float width, float core)
    {
        if (color.A == 0) return;
        using var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawEllipse(pen, cx - core, Middle - core, core * 2, core * 2);
        for (int ray = 0; ray < 8; ray++)
        {
            double angle = ray * Math.PI / 4;
            var (cos, sin) = ((float)Math.Cos(angle), (float)Math.Sin(angle));
            g.DrawLine(pen, cx + cos * 7.2f, Middle + sin * 7.2f, cx + cos * 9.2f, Middle + sin * 9.2f);
        }
    }

    /// <summary>A lua crescente do SVG (dois arcos de raio 8,1 entre as pontas (23,2; 21,5) e (33,5; 31,8)), com as pontas viradas para cima e para a direita.</summary>
    static GraphicsPath MoonPath()
    {
        var path = new GraphicsPath();
        path.AddArc(25.84f - 8.1f, 29.16f - 8.1f, 16.2f, 16.2f, 251, -232);
        path.AddArc(30.86f - 8.1f, 24.14f - 8.1f, 16.2f, 16.2f, 71, 128);
        path.CloseFigure();
        return path;
    }

    static void FillStar(Graphics g, float cx, float cy, float tip, float waist, Color color)
    {
        if (color.A == 0) return;
        PointF[] points =
        [
            new(cx, cy - tip), new(cx + waist, cy - waist), new(cx + tip, cy), new(cx + waist, cy + waist),
            new(cx, cy + tip), new(cx - waist, cy + waist), new(cx - tip, cy), new(cx - waist, cy - waist),
        ];
        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, points);
    }
}
