namespace Streemu.Util;

/// <summary>
/// Os valores de cor de um tema (escuro: identidade da spec 014; claro: estudo de 2026-11 em "Streemu Temas/lightMode"). <see cref="Theme"/> lê da paleta
/// ativa, então todo <c>Theme.Text</c> do app já segue o tema. Dentro de uma paleta os valores são todos distintos: é por eles que
/// <see cref="ThemeSwitch"/> acha, nos controles já criados, qual cor trocar por qual (ver <see cref="Pairs"/>).
/// Uso: <c>var fundo = ThemePalette.Light.Surface;</c>
/// </summary>
public sealed record ThemePalette
{
    public required Color Surface { get; init; }
    public required Color SurfaceRaised { get; init; }
    public required Color SurfaceInput { get; init; }
    public required Color TitleBar { get; init; }
    public required Color Backdrop { get; init; }
    public required Color Border { get; init; }
    public required Color Text { get; init; }
    public required Color TextMuted { get; init; }
    public required Color Accent { get; init; }
    public required Color AccentHover { get; init; }
    public required Color AccentMuted { get; init; }
    public required Color AccentMutedHover { get; init; }
    public required Color AccentSoft { get; init; }
    public required Color Neutral { get; init; }
    public required Color NeutralHover { get; init; }
    /// <summary>Texto sobre <see cref="Neutral"/>: branco no escuro; no claro o fundo é quase branco e o texto escurece.</summary>
    public required Color NeutralText { get; init; }
    public required Color Ok { get; init; }
    public required Color Warn { get; init; }
    public required Color Error { get; init; }
    public required Color CardBorder { get; init; }
    public required Color CardSelected { get; init; }
    public required Color CardSelectedBorder { get; init; }
    public required Color WarnSurface { get; init; }
    public required Color WarnBorder { get; init; }

    // Desenhadas à mão (nunca copiadas para um controle), então podem ter transparência.
    public required Color Selection { get; init; }
    public required Color SelectionSoft { get; init; }
    public required Color OptionSelection { get; init; }
    public required Color ScrollThumb { get; init; }
    public required Color PosterIcon { get; init; }
    public required Color Divider { get; init; }
    public required Color WatchedVeil { get; init; }
    public required Color Separator { get; init; }
    public required Color MarkOutline { get; init; }
    public required Color LiveHalo { get; init; }
    public required Color SwitchThumbOn { get; init; }
    public required Color SwitchThumbOff { get; init; }

    // O botão de tema (pacote "Botão": tema-escuro.svg e tema-claro.svg).
    public required Color ToggleTrackA { get; init; }
    public required Color ToggleTrackB { get; init; }
    public required Color ToggleTrackBorder { get; init; }
    public required Color ToggleIdle { get; init; }
    public required Color ToggleThumbA { get; init; }
    public required Color ToggleThumbB { get; init; }
    public required Color ToggleSparkle { get; init; }
    public required Color ToggleFocus { get; init; }
    public required Color ToggleShadow { get; init; }
    public required Color ToggleMoon { get; init; }
    public required Color ToggleSun { get; init; }
    public required Color ToggleRim { get; init; }

    // Tela de entrada da conta: o desenho dela é escuro nos dois temas (o botão de tema nem aparece ali), então a paleta escura é fixa.
    public required Color LoginCard { get; init; }
    public required Color LoginField { get; init; }
    public required Color LoginGlow { get; init; }
    public required Color LoginEyebrow { get; init; }
    public required Color LoginOnline { get; init; }
    public required Color LoginDisabled { get; init; }
    public Color LoginShadow(int alpha) => Color.FromArgb(alpha, 8, 6, 18);

    static Color Hex(string html) => ColorTranslator.FromHtml(html);

    public static readonly ThemePalette Dark = new()
    {
        Surface = Hex("#2A2640"), SurfaceRaised = Hex("#1E1B2E"), SurfaceInput = Hex("#25213A"), TitleBar = Hex("#171523"), Backdrop = Hex("#14121F"),
        Border = Hex("#675A88"), Text = Hex("#F4F0FA"), TextMuted = Hex("#C5BADB"),
        Accent = Hex("#7C62DC"), AccentHover = Hex("#8F76ED"), AccentMuted = Hex("#5B4A9E"), AccentMutedHover = Hex("#6A58B3"), AccentSoft = Hex("#B9A8F4"),
        Neutral = Hex("#49415F"), NeutralHover = Hex("#5A4E76"), NeutralText = Color.White,
        Ok = Hex("#3DDC6A"), Warn = Hex("#F0B232"), Error = Hex("#E5484D"),
        CardBorder = Hex("#4B4463"), CardSelected = Hex("#3A3160"), CardSelectedBorder = Hex("#9B7BFF"), WarnSurface = Hex("#3A2F2B"), WarnBorder = Hex("#7A5A3A"),
        Selection = Color.FromArgb(120, 0x7C, 0x62, 0xDC), SelectionSoft = Color.FromArgb(60, 0x7C, 0x62, 0xDC), OptionSelection = Color.FromArgb(90, 0x7C, 0x62, 0xDC),
        ScrollThumb = Color.FromArgb(150, 0x7C, 0x62, 0xDC), PosterIcon = Color.FromArgb(150, 0xB9, 0xA8, 0xF4), Divider = Color.FromArgb(70, 0, 0, 0),
        WatchedVeil = Color.FromArgb(110, 0x1E, 0x1B, 0x2E), Separator = Color.FromArgb(40, 0xF4, 0xF0, 0xFA),
        MarkOutline = Color.FromArgb(80, 0xB9, 0xA8, 0xF4), LiveHalo = Color.FromArgb(90, 0xE5, 0x48, 0x4D),
        SwitchThumbOn = Hex("#F4F0FA"), SwitchThumbOff = Hex("#F4F0FA"),
        ToggleTrackA = Hex("#302A46"), ToggleTrackB = Hex("#211E30"), ToggleTrackBorder = Hex("#51476F"), ToggleIdle = Hex("#A79ABD"),
        ToggleThumbA = Hex("#9278ED"), ToggleThumbB = Hex("#6E55C4"), ToggleSparkle = Hex("#B9A8F4"), ToggleFocus = Hex("#B9A8F4"),
        ToggleShadow = Color.FromArgb(70, 8, 6, 18), ToggleMoon = Hex("#F6F1FF"), ToggleSun = Hex("#FFF9ED"), ToggleRim = Color.FromArgb(107, 0xC9, 0xBC, 0xFF),
        LoginCard = Hex("#29243D"), LoginField = Hex("#0E0C17"), LoginGlow = Hex("#2E274F"), LoginEyebrow = Hex("#CDBBFF"), LoginOnline = Hex("#48BA74"),
        LoginDisabled = Color.FromArgb(120, 0x7C, 0x62, 0xDC),
    };

    /// <summary>
    /// Violeta frio e acinzentado, superfícies quase neutras (estudo lightMode): fundo #F4F3F8, lateral #E5E4ED, seleção #D5D3E5, texto #343144, ação #7469AD.
    /// Os verdes, o âmbar e o vermelho escurecem para ler sobre fundo claro. Valores com diferença de 1 (Backdrop, AccentSoft) existem só para não repetir uma cor.
    /// </summary>
    public static readonly ThemePalette Light = new()
    {
        Surface = Hex("#F4F3F8"), SurfaceRaised = Hex("#E5E4ED"), SurfaceInput = Hex("#FFFFFF"), TitleBar = Hex("#E8E7EF"), Backdrop = Hex("#F5F4F9"),
        Border = Hex("#C9C6D6"), Text = Hex("#343144"), TextMuted = Hex("#6B687A"),
        Accent = Hex("#7469AD"), AccentHover = Hex("#62589A"), AccentMuted = Hex("#8A80BB"), AccentMutedHover = Hex("#7B71AE"), AccentSoft = Hex("#7568AE"),
        Neutral = Hex("#DEDDE9"), NeutralHover = Hex("#D2D1E1"), NeutralText = Hex("#4A465B"),
        Ok = Hex("#16814B"), Warn = Hex("#8B5B08"), Error = Hex("#BD3544"),
        CardBorder = Hex("#D3CFE0"), CardSelected = Hex("#E4E3EF"), CardSelectedBorder = Hex("#AAA5C7"), WarnSurface = Hex("#FBF0D9"), WarnBorder = Hex("#E0C99A"),
        Selection = Hex("#D5D3E5"), SelectionSoft = Hex("#DFDDEB"), OptionSelection = Hex("#DAD8E8"),
        ScrollThumb = Color.FromArgb(150, 0x74, 0x69, 0xAD), PosterIcon = Color.FromArgb(150, 0x75, 0x68, 0xAE), Divider = Color.FromArgb(43, 69, 65, 93),
        WatchedVeil = Color.FromArgb(110, 0xE5, 0xE4, 0xED), Separator = Color.FromArgb(43, 69, 65, 93),
        MarkOutline = Color.FromArgb(0, 0x75, 0x68, 0xAE), LiveHalo = Color.FromArgb(70, 0xBD, 0x35, 0x44),
        SwitchThumbOn = Color.White, SwitchThumbOff = Hex("#8C879C"),
        ToggleTrackA = Hex("#EEEAF5"), ToggleTrackB = Hex("#E3DFED"), ToggleTrackBorder = Hex("#D2CBE0"), ToggleIdle = Hex("#A69EB8"),
        ToggleThumbA = Hex("#9278ED"), ToggleThumbB = Hex("#6E55C4"), ToggleSparkle = Hex("#796CA8"), ToggleFocus = Hex("#8874C9"),
        ToggleShadow = Color.FromArgb(41, 52, 43, 74), ToggleMoon = Hex("#F6F1FF"), ToggleSun = Hex("#FFF9ED"), ToggleRim = Color.FromArgb(107, 0xC9, 0xBC, 0xFF),
        LoginCard = Dark.LoginCard, LoginField = Dark.LoginField, LoginGlow = Dark.LoginGlow, LoginEyebrow = Dark.LoginEyebrow, LoginOnline = Dark.LoginOnline,
        LoginDisabled = Dark.LoginDisabled,
    };

    /// <summary>Cada cor opaca de <paramref name="from"/> com a sua equivalente em <paramref name="to"/>, pela cor (ARGB), para trocar nos controles já criados.</summary>
    public static Dictionary<int, Color> Pairs(ThemePalette from, ThemePalette to, bool includeWhite)
    {
        var map = new Dictionary<int, Color>();
        void Add(Color a, Color b) { if (a.A == 255 && (includeWhite || a.ToArgb() != Color.White.ToArgb()) && a.ToArgb() != b.ToArgb()) map[a.ToArgb()] = b; }
        Add(from.Surface, to.Surface); Add(from.SurfaceRaised, to.SurfaceRaised); Add(from.SurfaceInput, to.SurfaceInput); Add(from.TitleBar, to.TitleBar);
        Add(from.Backdrop, to.Backdrop); Add(from.Border, to.Border); Add(from.Text, to.Text); Add(from.TextMuted, to.TextMuted);
        Add(from.Accent, to.Accent); Add(from.AccentHover, to.AccentHover); Add(from.AccentMuted, to.AccentMuted); Add(from.AccentMutedHover, to.AccentMutedHover);
        Add(from.AccentSoft, to.AccentSoft); Add(from.Neutral, to.Neutral); Add(from.NeutralHover, to.NeutralHover); Add(from.NeutralText, to.NeutralText);
        Add(from.Ok, to.Ok); Add(from.Warn, to.Warn); Add(from.Error, to.Error); Add(from.CardBorder, to.CardBorder); Add(from.CardSelected, to.CardSelected);
        Add(from.CardSelectedBorder, to.CardSelectedBorder); Add(from.WarnSurface, to.WarnSurface); Add(from.WarnBorder, to.WarnBorder);
        return map;
    }
}
