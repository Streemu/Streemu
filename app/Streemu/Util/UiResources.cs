using System.Drawing.Drawing2D;

namespace Streemu.Util;

// Recursos por função. As medidas são lógicas (96 DPI); cada controle converte uma única vez.
public static class UiMetrics
{
    public const int Space = 4, Gap = 12, PagePadding = 24, Radius = 8, SmallRadius = 6;
    public const int ActionHeight = 38, CategoryWidth = 220, CategoryHeight = 44, CategoryStep = 49;
    public const int SettingsHeader = 126, SettingsCompactWidth = 688, SettingsContentMax = 800;
    public const int RailWidth = 64, SidebarWidth = 232, Avatar = 32;
    public const int CardMinWidth = 240, CardMaxWidth = 480, FeaturedCardMaxWidth = 640, CardDetailsHeight = 112;
    public const int CinemaCardHeight = 224;
    public const int PreparationMaxWidth = 760, PreparationColumnWidth = 344, SourceWidth = 184, SourceHeight = 104;
    public const int SegmentHeight = 34, SegmentMinWidth = 72, SegmentTextPadding = 18, SegmentInset = 3;
    public const int ToggleWidth = 44, ToggleHeight = 24, ToggleInset = 3, ToggleText = 60;
    public const int DisclosureHeight = 42, DiagnosisIcon = 38, DiagnosisText = 56, DiagnosisMinHeight = 60;
}

/// <summary>
/// Família e tamanhos (pontos) por função. Trocar a família ou um tamanho aqui muda todas as telas; quem cria a fonte é dono dela e a descarta.
/// </summary>
public static class UiTypography
{
    public const string Family = "Segoe UI";
    /// <summary>Peso intermediário para títulos de cartão (o Segoe UI só tem esse peso como família separada no WinForms).</summary>
    public const string FamilySemibold = "Segoe UI Semibold";
    /// <summary>Códigos de pareamento, estatísticas e log: precisam de largura fixa.</summary>
    public const string Mono = "Consolas";
    /// <summary>Glifos dos botões de texto da barra do vídeo (ver <see cref="UiGlyphs"/>); o resto da interface usa <see cref="UiIcons"/>.</summary>
    public const string Symbols = "Segoe MDL2 Assets";
    public const float Body = 10.5f, Title = 18, Heading = 13, CaptionScale = .85f, BadgeScale = .75f, InitialScale = 2.2f, BrandScale = 1.55f;
    /// <summary>Título de página dos Ajustes antigos, título de seção, texto de ação, legenda/estado e nota pequena.</summary>
    public const float PageTitle = 23, Section = 12, Action = 10, Caption = 9.5f, Note = 9, CardTitle = 16, EmptyState = 20, MonoBody = 11, MonoSmall = 9, MonoLog = 8.5f, Symbol = 13;
    /// <summary>Tela de entrada da conta, medida do desenho de 2026-11-17 (marca, frase do topo, destaque, apoio, títulos do cartão, rótulo, código, botão, notas e rodapé).</summary>
    public const float LoginBrand = 21.25f, LoginTagline = 11.5f, LoginEyebrow = 8.75f, LoginHero = 24, LoginLead = 12, LoginTitle = 24.25f, LoginLabel = 11.25f, LoginCode = 19.75f, LoginAction = 13, LoginFine = 10, LoginFooter = 9.25f, LoginFooterLink = 9.25f;
    public static Font TitleFont() => new(Family, Title, FontStyle.Bold);
    public static Font HeadingFont() => new(Family, Heading, FontStyle.Bold);
    public static Font Create(float size, FontStyle style = FontStyle.Regular) => new(Family, size, style);
    public static Font Create(float size, FontStyle style, GraphicsUnit unit) => new(Family, size, style, unit);
    public static Font CreateSemibold(float size) => new(FamilySemibold, size);
    public static Font CreateMono(float size) => new(Mono, size);
    public static Font CreateSymbols(float size) => new(Symbols, size);
}

/// <summary>
/// Glifos da fonte de símbolos do Windows usados como texto de botão (a barra do vídeo é feita de botões do WinForms e não foi redesenhada).
/// Ficam juntos para trocar um ícone num lugar só; desenhos à mão usam <see cref="UiIcons"/>.
/// </summary>
public static class UiGlyphs
{
    public const string Volume = "", Muted = "", Fullscreen = "", ExitFullscreen = "", Close = "", SmallWindow = "", NormalWindow = "";
    /// <summary>Fila do Cinema (Ui/FilaFilmeView.cs): tocar o episódio (Play) e fechar o painel (ChromeClose, o X fino da barra de título); tirar da fila usa <see cref="Close"/>.</summary>
    public const string Play = "", ChromeClose = "";
    /// <summary>Mostrar ou esconder a barra lateral (o antigo botão "Barra" da barra do vídeo; OpenPane do Segoe MDL2).</summary>
    public const string Sidebar = "";
}

/// <summary>Fontes do componente, recriadas só quando a fonte muda; nenhuma fonte global descartável.</summary>
public sealed class UiFonts : IDisposable
{
    readonly Control _owner;
    public Font Bold { get; private set; } = null!;
    public Font Small { get; private set; } = null!;
    public Font Badge { get; private set; } = null!;
    public Font Initial { get; private set; } = null!;
    public Font Brand { get; private set; } = null!;
    public UiFonts(Control owner) { _owner = owner; Reset(); owner.FontChanged += Changed; }
    void Changed(object? sender, EventArgs e) => Reset();
    void Reset()
    {
        Release();
        Bold = new Font(_owner.Font, FontStyle.Bold);
        Small = new Font(_owner.Font.FontFamily, _owner.Font.Size * UiTypography.CaptionScale, FontStyle.Bold);
        Badge = new Font(_owner.Font.FontFamily, _owner.Font.Size * UiTypography.BadgeScale, FontStyle.Bold);
        Initial = new Font(_owner.Font.FontFamily, _owner.Font.Size * UiTypography.InitialScale, FontStyle.Bold);
        Brand = new Font(_owner.Font.FontFamily, _owner.Font.Size * UiTypography.BrandScale, FontStyle.Bold);
    }
    void Release() { Bold?.Dispose(); Small?.Dispose(); Badge?.Dispose(); Initial?.Dispose(); Brand?.Dispose(); }
    public void Dispose() { _owner.FontChanged -= Changed; Release(); }
}

public enum UiIcon { Home, Cinema, Settings, Broadcast, Stage, Back, Cat, Window, Monitor, ChevronDown, ChevronRight, ChevronLeft, Problem, Update, Bell, MutedBell, Check, Failure, Pending, Lock, Gamepad, SpeakerOff, Eye, Speaker, Warning, Shield, Plus, Clock, Upload, Activity, Folder, People }

/// <summary>Ícones vetoriais substituíveis por função, sem emojis ou dependência de uma fonte de símbolos.</summary>
public static class UiIcons
{
    public static void Draw(Graphics g, UiIcon icon, Rectangle bounds, Color color)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var saved = g.Save();
        int size = Math.Min(bounds.Width, bounds.Height);
        g.TranslateTransform(bounds.X + (bounds.Width - size) / 2f, bounds.Y + (bounds.Height - size) / 2f);
        g.ScaleTransform(size / 24f, size / 24f);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(color);
        switch (icon)
        {
            case UiIcon.Home:
                g.DrawLines(pen, [new(3, 11), new(12, 3), new(21, 11)]); g.DrawLines(pen, [new(5, 10), new(5, 21), new(10, 21), new(10, 15), new(14, 15), new(14, 21), new(19, 21), new(19, 10)]); break;
            case UiIcon.Cinema:
                g.DrawRectangle(pen, 3, 4, 18, 16); g.DrawLine(pen, 7, 4, 7, 20); g.DrawLine(pen, 17, 4, 17, 20);
                for (int y = 8; y <= 16; y += 4) { g.DrawLine(pen, 3, y, 7, y); g.DrawLine(pen, 17, y, 21, y); } break;
            case UiIcon.Broadcast:
                g.FillEllipse(brush, 10, 10, 4, 4); g.DrawArc(pen, 6, 6, 12, 12, -55, 110); g.DrawArc(pen, 6, 6, 12, 12, 125, 110);
                g.DrawArc(pen, 2, 2, 20, 20, -55, 110); g.DrawArc(pen, 2, 2, 20, 20, 125, 110); break;
            case UiIcon.Settings: // controles deslizantes, como em todos os mockups (a engrenagem anterior não aparecia em nenhum)
                foreach (var (y, knob) in new[] { (6f, 15f), (12f, 8f), (18f, 13f) })
                {
                    g.DrawLine(pen, 3, y, knob - 2.5f, y); g.DrawLine(pen, knob + 2.5f, y, 21, y); g.DrawEllipse(pen, knob - 2.5f, y - 2.5f, 5, 5);
                }
                break;
            case UiIcon.Lock:
                g.DrawRectangle(pen, 5, 11, 14, 10); g.DrawArc(pen, 8, 4, 8, 10, 180, 180); g.DrawLine(pen, 8, 9, 8, 11); g.DrawLine(pen, 16, 9, 16, 11);
                g.FillEllipse(brush, 10.5f, 14.5f, 3, 3); break;
            case UiIcon.Problem:
                g.DrawLine(pen, 5, 3, 5, 22); g.DrawLines(pen, [new(5, 4), new(20, 4), new(17, 9), new(20, 14), new(5, 14)]); break;
            case UiIcon.Update:
                g.DrawArc(pen, 4, 4, 16, 16, 30, 300); g.DrawLines(pen, [new(16, 2), new(20, 5), new(16, 8)]); break;
            case UiIcon.Check: g.DrawLines(pen, [new(4, 12), new(10, 18), new(20, 5)]); break;
            case UiIcon.Upload: // enviar arquivo (spec 020): seta para cima saindo de uma bandeja, como no mockup
                g.DrawLine(pen, 12, 16, 12, 4); g.DrawLines(pen, [new(7, 9), new(12, 4), new(17, 9)]);
                g.DrawLines(pen, [new(5, 14), new(5, 20), new(19, 20), new(19, 14)]); break;
            case UiIcon.Folder: // pasta de recebidos (Enviar arquivo): pasta com seta para baixo
                g.DrawLines(pen, [new PointF(3, 6), new PointF(9, 6), new PointF(11, 8.5f), new PointF(21, 8.5f), new PointF(21, 19), new PointF(3, 19), new PointF(3, 6)]);
                g.DrawLine(pen, 12, 11.5f, 12, 16); g.DrawLines(pen, [new PointF(9.5f, 14), new PointF(12, 16.5f), new PointF(14.5f, 14)]); break;
            case UiIcon.People: // Grupos: duas pessoas (cabeça e ombros), no mesmo traço dos outros ícones
                g.DrawEllipse(pen, 4, 4.5f, 7, 7); g.DrawArc(pen, 2, 14, 11, 12, 180, 180);
                g.DrawEllipse(pen, 14, 5, 6, 6); g.DrawArc(pen, 14.5f, 14.5f, 8, 9, 180, 180); break;
            case UiIcon.Failure: g.DrawLine(pen, 6, 6, 18, 18); g.DrawLine(pen, 18, 6, 6, 18); break;
            case UiIcon.Pending: g.DrawEllipse(pen, 5, 5, 14, 14); break;
            case UiIcon.Bell:
            case UiIcon.MutedBell:
                using (var body = new GraphicsPath())
                {
                    body.AddBezier(4, 18, 5, 14, 4, 5, 12, 4); body.AddBezier(12, 4, 20, 5, 19, 14, 20, 18); body.AddLine(20, 18, 4, 18); g.DrawPath(pen, body);
                }
                g.DrawArc(pen, 9, 18, 6, 4, 0, 180);
                if (icon == UiIcon.MutedBell) g.DrawLine(pen, 3, 3, 21, 21);
                break;
            case UiIcon.Gamepad:
                using (var body = Theme.RoundedPath(new Rectangle(3, 7, 18, 11), 5)) g.DrawPath(pen, body);
                g.DrawLine(pen, 7, 12.5f, 11, 12.5f); g.DrawLine(pen, 9, 10.5f, 9, 14.5f);
                g.FillEllipse(brush, 14, 10.5f, 2.4f, 2.4f); g.FillEllipse(brush, 16.8f, 13, 2.4f, 2.4f); break;
            case UiIcon.Eye:
                g.DrawEllipse(pen, 2.5f, 7, 19, 10); g.FillEllipse(brush, 9.5f, 9.5f, 5, 5); break;
            case UiIcon.Shield:
                g.DrawLines(pen, [new(12, 3), new(20, 6), new(20, 12), new(12, 21), new(4, 12), new(4, 6), new(12, 3)]);
                g.DrawLines(pen, [new PointF(8.5f, 12), new PointF(11, 14.5f), new PointF(15.5f, 9.5f)]); break;
            case UiIcon.Speaker:
                g.DrawLines(pen, [new(4, 9), new(8, 9), new(13, 5), new(13, 19), new(8, 15), new(4, 15), new(4, 9)]);
                g.DrawArc(pen, 12, 7, 8, 10, -60, 120); g.DrawArc(pen, 12, 3, 14, 18, -50, 100); break;
            case UiIcon.Warning:
                g.DrawLines(pen, [new(12, 3), new(22, 20), new(2, 20), new(12, 3)]); g.DrawLine(pen, 12, 9, 12, 14); g.DrawLine(pen, 12, 17, 12, 17.5f); break;
            case UiIcon.SpeakerOff:
                g.DrawLines(pen, [new(4, 9), new(8, 9), new(13, 5), new(13, 19), new(8, 15), new(4, 15), new(4, 9)]);
                g.DrawLine(pen, 16, 9, 21, 15); g.DrawLine(pen, 21, 9, 16, 15); break;
            case UiIcon.Plus: g.DrawLine(pen, 12, 5, 12, 19); g.DrawLine(pen, 5, 12, 19, 12); break;
            case UiIcon.Activity: // atividade recente do grupo: um pulso, como um sinal de vida
                g.DrawLines(pen, [new(2, 12), new(7, 12), new(10, 5), new(14, 19), new(17, 12), new(22, 12)]); break;
            case UiIcon.Clock: g.DrawEllipse(pen, 3.5f, 3.5f, 17, 17); g.DrawLines(pen, [new PointF(12, 7.5f), new PointF(12, 12), new PointF(15.5f, 14)]); break;
            case UiIcon.Back: g.DrawLines(pen, [new(11, 5), new(4, 12), new(11, 19)]); g.DrawLine(pen, 4, 12, 21, 12); break;
            case UiIcon.ChevronDown: g.DrawLines(pen, [new(5, 9), new(12, 16), new(19, 9)]); break;
            case UiIcon.ChevronRight: g.DrawLines(pen, [new(9, 5), new(16, 12), new(9, 19)]); break;
            case UiIcon.ChevronLeft: g.DrawLines(pen, [new(15, 5), new(8, 12), new(15, 19)]); break;
            case UiIcon.Cat: // a marca é arte, não segue a cor do item
                g.Restore(saved);
                UiBrand.DrawMark(g, bounds);
                return;
            default:
                g.DrawRectangle(pen, 3, 4, 18, 14);
                if (icon == UiIcon.Window) g.DrawLine(pen, 3, 8, 21, 8);
                else { g.DrawLine(pen, 12, 18, 12, 21); g.DrawLine(pen, 8, 21, 16, 21); }
                break;
        }
        g.Restore(saved);
    }
}

/// <summary>
/// A marca do Streemu (spec 014: o gato é a marca para ícone, bandeja e atalhos) e a arte das telas, num lugar só. O gato e a Mumu são a arte
/// recebida em 13/11/2026 (embutida em <c>Arte/</c>); o ícone é o mesmo <c>streemu.ico</c> do exe e do instalador. Trocar a arte é trocar esses
/// arquivos: a lateral, a janela, a bandeja e o Início mudam juntos.
/// </summary>
public static class UiBrand
{
    public const string Name = "Streemu";
    public const string Mark = "gato.png", SleepingMumu = "mumu-dormindo.png", PopcornMumu = "mumu-pipoca.png";
    /// <summary>O gato dormindo na gaveta da área de soltar arquivos (a versão clara é <c>gato-arquivo-claro.png</c>).</summary>
    public const string FileCabinetCat = "gato-arquivo.png";
    const string LightSuffix = "-claro";
    static readonly Dictionary<bool, Bitmap> MarkSources = new();
    // Cópias do gato já reduzidas, para a lateral não reamostrar 256 px a cada pintura; poucas medidas são usadas (aberta, trilho, DPI), com teto.
    static readonly Dictionary<(int Size, bool Light), Bitmap> MarkSizes = new();
    const int MaxMarkSizes = 8;

    /// <summary>
    /// A arte para o tema de agora: <c>mumu-pipoca.png</c> vira <c>mumu-pipoca-claro.png</c> no tema claro, se essa versão existir embutida
    /// (a Mumu do controle não tem versão clara). Uso: <c>UiBrand.ThemedName("gato.png")</c>.
    /// </summary>
    public static string ThemedName(string name, bool? light = null)
    {
        if (!(light ?? Theme.IsLight)) return name;
        string themed = Path.GetFileNameWithoutExtension(name) + LightSuffix + Path.GetExtension(name);
        return typeof(UiBrand).Assembly.GetManifestResourceNames().Contains("Streemu.Arte." + themed) ? themed : name;
    }

    /// <summary>Esvazia as cópias do gato guardadas (o tema mudou e a arte é outra).</summary>
    public static void ForgetMarks()
    {
        foreach (var scaled in MarkSizes.Values) scaled.Dispose();
        MarkSizes.Clear();
    }

    static Stream Resource(string name) =>
        typeof(UiBrand).Assembly.GetManifestResourceStream("Streemu.Arte." + name) ?? throw new FileNotFoundException("arte embutida ausente", name);

    /// <summary>Uma arte embutida, decodificada agora; quem chama é dono do bitmap e o descarta.</summary>
    public static Bitmap LoadArt(string name)
    {
        using var stream = Resource(name);
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    /// <summary>O gato da marca dentro de <paramref name="bounds"/> (quadrado centrado), no tema de agora ou em <paramref name="light"/> (a tela de entrada é sempre escura). Só na thread da tela.</summary>
    public static void DrawMark(Graphics g, Rectangle bounds, bool? light = null)
    {
        int size = Math.Min(bounds.Width, bounds.Height);
        if (size <= 0) return;
        bool useLight = light ?? Theme.IsLight;
        if (!MarkSizes.TryGetValue((size, useLight), out var scaled))
        {
            if (!MarkSources.TryGetValue(useLight, out var source)) MarkSources[useLight] = source = LoadArt(ThemedName(Mark, useLight));
            if (MarkSizes.Count >= MaxMarkSizes) ForgetMarks();
            var outline = useLight ? ThemePalette.Light.MarkOutline : ThemePalette.Dark.MarkOutline;
            scaled = WithOutline(Scale(source, size), outline, Math.Max(1, (int)Math.Round(size / 32f)));
            MarkSizes[(size, useLight)] = scaled;
        }
        g.DrawImage(scaled, bounds.X + (bounds.Width - size) / 2, bounds.Y + (bounds.Height - size) / 2, size, size);
    }

    /// <summary>
    /// Acrescenta um contorno de <paramref name="radius"/> px em volta da silhueta (a transparência da arte dilatada), por baixo da arte. Feito uma vez
    /// por tamanho, no cache; descarta <paramref name="art"/> e devolve um bitmap novo.
    /// </summary>
    static Bitmap WithOutline(Bitmap art, Color outline, int radius)
    {
        int w = art.Width, h = art.Height;
        var alpha = new byte[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) alpha[y * w + x] = art.GetPixel(x, y).A;
        var result = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int max = 0;
            for (int dy = -radius; dy <= radius; dy++) for (int dx = -radius; dx <= radius; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (dx * dx + dy * dy > radius * radius || nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                max = Math.Max(max, alpha[ny * w + nx]);
            }
            if (max > 0) result.SetPixel(x, y, Color.FromArgb(max * outline.A / 255, outline.R, outline.G, outline.B));
        }
        using (var g = Graphics.FromImage(result)) g.DrawImage(art, 0, 0, w, h);
        art.Dispose();
        return result;
    }

    /// <summary>Reduz uma arte com boa qualidade uma vez (o desenho de cada pintura só copia).</summary>
    public static Bitmap Scale(Image source, int size)
    {
        var result = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(result);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.DrawImage(source, new Rectangle(0, 0, size, size));
        return result;
    }

    /// <summary>
    /// O ícone com todos os tamanhos (o Windows escolhe o certo na barra de título, na barra de tarefas e na bandeja). <paramref name="live"/> acrescenta
    /// o ponto vermelho de "ao vivo" no canto, em cada tamanho; o gato continua o mesmo.
    /// </summary>
    public static Icon CreateIcon(bool live)
    {
        using var stream = Resource("streemu.ico");
        var icon = new Icon(stream);
        if (!live) return icon;
        using (icon) return WithLiveDot(icon);
    }

    static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64, 256];

    static Icon WithLiveDot(Icon source)
    {
        var images = new List<(int Size, byte[] Png)>();
        foreach (int s in IconSizes)
        {
            using var frame = new Icon(source, s, s);
            using var bmp = new Bitmap(s, s, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            using (var art = frame.ToBitmap())
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(art, 0, 0, s, s);
                float d = Math.Max(6, s * .42f);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var ring = new SolidBrush(Theme.OnMedia);
                g.FillEllipse(ring, s - d, 0, d, d);
                using var dot = new SolidBrush(Theme.Error);
                float inset = Math.Max(1, d * .16f);
                g.FillEllipse(dot, s - d + inset, inset, d - 2 * inset, d - 2 * inset);
            }
            using var png = new MemoryStream();
            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            images.Add((s, png.ToArray()));
        }
        using var ico = new MemoryStream();
        using (var w = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)images.Count);
            int offset = 6 + 16 * images.Count;
            foreach (var (s, data) in images)
            {
                w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32); w.Write(data.Length); w.Write(offset);
                offset += data.Length;
            }
            foreach (var (_, data) in images) w.Write(data);
        }
        ico.Position = 0;
        return new Icon(ico);
    }
}
