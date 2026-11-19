using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// O Cinema sem sessões (pedido de 2026-11-16): traço roxo, título, frase e a Mumu com pipoca. Texto e Mumu formam um bloco só,
/// centrado na página e com os dois centros na mesma altura; no mockup cada um flutuava solto e pareciam desalinhados.
/// Estreito, a Mumu sobe para cima do texto, como no Início vazio. Sem altura para a Mumu, fica só o texto.
/// O texto é pintado aqui, sem Label: o Label recua cada fonte por um tanto diferente, e o traço, o título e a frase não começavam na mesma coluna.
/// </summary>
public sealed class CinemaEmptyState : Control
{
    const int ArtMaxAt96 = 300, ArtMinAt96 = 120, SideGapAt96 = 56, AccentWidthAt96 = 48, AccentHeightAt96 = 4;
    const string Headline = "A sala está pronta\npara o próximo filme", Hint = "Crie uma sessão para assistir juntos,\nou aguarde alguém abrir a próxima.";
    const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl;
    readonly ArtView _mumu = new(UiBrand.PopcornMumu);
    Font _headlineFont = UiTypography.CreateSemibold(UiTypography.EmptyState);
    Rectangle _accent, _headline, _hint;
    bool _centered;

    public CinemaEmptyState()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
        AccessibleName = Headline.Replace('\n', ' ');
        Controls.Add(_mumu);
        UiScale.WatchFont(this);
    }

    // o título acompanha a fonte do controle, que o Ctrl + / Ctrl - escala (UiScale.WatchFont)
    protected override void OnFontChanged(EventArgs e)
    {
        var previous = _headlineFont;
        _headlineFont = UiTypography.CreateSemibold(Font.Size * UiTypography.EmptyState / UiTypography.Body);
        previous.Dispose();
        base.OnFontChanged(e);
    }

    /// <summary>A altura mínima para o texto caber nesta largura (a página rola abaixo disso). Ex.: <c>empty.Height = Math.Max(room, empty.MinimumContentHeight(width))</c>.</summary>
    public int MinimumContentHeight(int width) => TextSize(Math.Max(1, width)).Height;

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var text = TextSize(Width);
        int sideArt = Math.Min(Math.Min(Dp(ArtMaxAt96), Height), Width - text.Width - Dp(SideGapAt96));
        if (sideArt >= Dp(ArtMinAt96)) ArrangeSideBySide(sideArt, text);
        else ArrangeStacked(text);
        Invalidate();
    }

    // texto à esquerda e Mumu à direita; o bloco inteiro no meio da página, os dois centrados na mesma altura
    void ArrangeSideBySide(int art, Size text)
    {
        int left = (Width - (text.Width + Dp(SideGapAt96) + art)) / 2;
        PlaceText(new Point(left, (Height - text.Height) / 2), text.Width, false);
        ShowArt(new Rectangle(left + text.Width + Dp(SideGapAt96), (Height - art) / 2, art, art));
    }

    // Mumu em cima e o texto centrado embaixo; a Mumu some quando não cabe
    void ArrangeStacked(Size text)
    {
        int gap = Dp(UiMetrics.Gap) * 2;
        int art = Math.Min(Math.Min(Dp(ArtMaxAt96), Width), Height - text.Height - gap);
        bool showArt = art >= Dp(ArtMinAt96);
        int block = (showArt ? art + gap : 0) + text.Height, top = Math.Max(0, (Height - block) / 2);
        _mumu.Visible = showArt;
        if (showArt) ShowArt(new Rectangle((Width - art) / 2, top, art, art));
        PlaceText(new Point((Width - text.Width) / 2, top + (showArt ? art + gap : 0)), text.Width, true);
    }

    // a coluna tem a largura do texto (não um máximo fixo), senão sobra um vão entre o texto e a Mumu
    Size TextSize(int maxWidth)
    {
        var headline = Measure(Headline, _headlineFont, maxWidth);
        var hint = Measure(Hint, Font, maxWidth);
        int height = Dp(AccentHeightAt96) + Dp(UiMetrics.Gap) * 2 + headline.Height + hint.Height;
        return new Size(Math.Min(maxWidth, Math.Max(headline.Width, hint.Width)), height);
    }

    void PlaceText(Point origin, int width, bool centered)
    {
        int gap = Dp(UiMetrics.Gap), accent = Dp(AccentWidthAt96);
        _centered = centered;
        _accent = new Rectangle(centered ? origin.X + (width - accent) / 2 : origin.X, origin.Y, accent, Dp(AccentHeightAt96));
        _headline = new Rectangle(origin.X, _accent.Bottom + gap, width, Measure(Headline, _headlineFont, width).Height);
        _hint = new Rectangle(origin.X, _headline.Bottom + gap, width, Measure(Hint, Font, width).Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (var accent = new SolidBrush(Theme.Accent)) e.Graphics.FillRectangle(accent, _accent);
        var align = _centered ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left;
        TextRenderer.DrawText(e.Graphics, Headline, _headlineFont, _headline, Theme.Text, Wrap | align);
        TextRenderer.DrawText(e.Graphics, Hint, Font, _hint, Theme.TextMuted, Wrap | align);
    }

    void ShowArt(Rectangle bounds) { _mumu.Bounds = bounds; _mumu.Visible = true; }

    static Size Measure(string text, Font font, int width) => TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), Wrap);

    int Dp(int value) => UiScale.Dp(this, value);

    protected override void Dispose(bool disposing) { if (disposing) _headlineFont.Dispose(); base.Dispose(disposing); }
}
