using System.Reflection;
using Streemu.Ui;
using Streemu.Util;

/// <summary>
/// Identidade visual (bloco 6) sem janela mostrada: posição fixa de Transmitir, marca na lateral aberta e no trilho, lista de amigos sem vazar sobre o
/// rodapé, ícone da janela/bandeja em vários tamanhos e cores/fontes/glifos num lugar só. Não mede contraste nem leitura no monitor real.
/// </summary>
static class IdentityBehavior
{
    static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(string? output)
    {
        if (output != null) Directory.CreateDirectory(output);
        var friends = Enumerable.Range(0, 30).Select(i => new SideFriend($"f{i}", $"Amigo com nome longo {i}", "Jogo de teste", i % 3 == 0, i < 2, null)).ToList();
        foreach (bool filme in new[] { false, true })
        foreach (var size in new[] { new Size(290, 700), new Size(80, 700), new Size(290, 480), new Size(80, 480) })
        {
            using var side = new SidePanel { Size = size, FilmeVisivel = filme };
            side.SetProfile("Rafa", null);
            side.CreateControl();
            // Transmitir não anda: com e sem live, com e sem vídeos no palco, com e sem amigos (e com a lista rolada).
            int? transmitTop = null;
            foreach (var (list, streams, self) in new (IReadOnlyList<SideFriend>, int, SideSelf?)[]
            {
                ([], 0, null), (friends, 2, null), (friends, 2, new SideSelf("Jogo", 3, false)), ([], 5, new SideSelf("Jogo", 0, true)),
            })
            {
                side.SetData(list, streams, SideView.Home, "Sala de teste", self);
                Call(side, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, 10, 400, -120));
                var rows = Rows(side);
                var transmit = rows.Single(r => r.Kind == "Transmit").Bounds;
                transmitTop ??= transmit.Top;
                Check(transmit.Top == transmitTop, $"Transmitir mudou de lugar em {size} (filme {filme}): {transmit.Top} ≠ {transmitTop}");
                // O espaço do grupo fica antes de Transmitir, qualquer que seja o item de cima (Início desde o #273; antes era Configurações).
                // Fora a lista de amigos: rolada, o título "Amigos" sobe para trás da navegação (recortado) e não conta como vizinho.
                var above = rows.Where(r => r.Kind is not ("Section" or "GroupFilter" or "Friend") && r.Bounds.Bottom <= transmit.Top).MaxBy(r => r.Bounds.Bottom);
                Check(transmit.Top - above.Bounds.Bottom >= side.LogicalToDeviceUnits(10), $"Transmitir colado em {above.Kind} em {size}: não é um grupo à parte");
                var profile = rows.Single(r => r.Kind == "Profile").Bounds;
                Check(profile.Bottom <= size.Height && rows.Where(r => r.Kind is "Problem" or "Update" or "Bell" or "Atividade" or "Diag").All(r => r.Bounds.Bottom <= profile.Top),
                    $"Perfil saiu do rodapé em {size}");
            }

            // A lista rolável não pode desenhar por cima das ferramentas do rodapé: com 30 amigos, a faixa das ferramentas sai igual à de 0 amigos.
            side.SetData([], 2, SideView.Home, "Sala de teste");
            using var empty = Render(side);
            side.SetData(friends, 2, SideView.Home, "Sala de teste");
            for (int i = 0; i < 3; i++) Call(side, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, 10, 400, -120));
            using var full = Render(side);
            var tools = Rows(side).Where(r => r.Kind is "Problem" or "Update" or "Bell" or "Atividade" or "Diag" or "Profile").Select(r => r.Bounds).ToList();
            var viewport = (Rectangle)side.GetType().GetField("_friendsViewport", Hidden)!.GetValue(side)!;
            foreach (var box in tools)
            {
                Check(viewport.Height == 0 || !box.IntersectsWith(viewport), $"Ferramenta {box} dentro da área dos amigos {viewport} em {size}");
                Check(SamePixels(empty, full, box), $"Lista de amigos vazou sobre o rodapé em {size} ({box})");
            }

            // A marca aparece aberta (o logo inteiro, à esquerda) e no trilho (só o gato): o topo não pode ser só o fundo da lateral.
            int markSize = UiScale.Dp(side, 30);
            bool rail = side.Width < UiScale.Dp(side, (UiMetrics.RailWidth + UiMetrics.SidebarWidth) / 2);
            var mark = rail ? new Rectangle((side.Width - markSize) / 2 - UiScale.Dp(side, 6), UiScale.Dp(side, 10), markSize, UiScale.Dp(side, 34))
                : new Rectangle(UiScale.Dp(side, 14), UiScale.Dp(side, 10), UiScale.Dp(side, 150), UiScale.Dp(side, 34));
            Check(Drawn(full, mark, full.GetPixel(1, 1)) > 40, $"Logo ausente no topo em {size}");
            if (output != null && !filme) full.Save(Path.Combine(output, $"lateral-{size.Width}x{size.Height}.png"));
        }

        // Na altura mínima da janela (440 dp com a barra de título; 410 dp de área útil), sem live e sem Cinema, sobra ao menos um amigo visível.
        using (var low = new SidePanel())
        {
            low.Size = new Size(low.LogicalToDeviceUnits(232), low.LogicalToDeviceUnits(410));
            low.SetProfile("Rafa", null);
            low.SetData(friends, 2, SideView.Home, "Sala de teste");
            Check(Rows(low).Count(r => r.Kind == "Friend") >= 1, "Na altura mínima a lateral não mostra nenhum amigo");
        }

        // Ícones: vários tamanhos no .ico em memória (o Windows escolhe), bandeja no tamanho pequeno do sistema, parado ≠ ao vivo.
        using (var idle = UiBrand.CreateIcon(false))
        using (var live = UiBrand.CreateIcon(true))
        {
            foreach (int s in new[] { 16, 20, 24, 32, 40, 48, 64 })
            {
                using var exact = new Icon(idle, s, s);
                Check(exact.Width == s, $"Ícone sem o tamanho {s}");
            }
            using var a = new Icon(idle, 32, 32); using var b = new Icon(live, 32, 32);
            using var ba = a.ToBitmap(); using var bb = b.ToBitmap();
            Check(!SamePixels(ba, bb, new Rectangle(0, 0, 32, 32)), "Ícone ao vivo igual ao parado");
            Check(Drawn(ba, new Rectangle(0, 0, 32, 32), Color.Transparent) > 200, "Ícone parado vazio");
            Check(HasColor(bb, new Rectangle(0, 0, 32, 32), Theme.Error), "Ícone ao vivo sem o ponto vermelho");
            if (output != null)
                using (var sheet = new Bitmap(6 * 72, 2 * 72))
                using (var g = Graphics.FromImage(sheet))
                {
                    g.Clear(Theme.Backdrop);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    int column = 0;
                    foreach (int s in new[] { 16, 20, 24, 32, 48, 64 })
                    {
                        using var i1 = new Icon(idle, s, s); using var i2 = new Icon(live, s, s);
                        using var p1 = i1.ToBitmap(); using var p2 = i2.ToBitmap();
                        g.DrawImage(p1, column * 72 + 4, 4, 64, 64); g.DrawImage(p2, column * 72 + 4, 76, 64, 64);
                        column++;
                    }
                    sheet.Save(Path.Combine(output, "icones-16-20-24-32-48-64.png"));
                }
        }
        using (var tray = TrayVisuals.CreateIcon(false)) Check(tray.Size == SystemInformation.SmallIconSize, $"Bandeja fora do tamanho pequeno do sistema: {tray.Size}");
        Check(PlayerOverlay.FullscreenLook(true, false).Glyph == UiGlyphs.Fullscreen && PlayerOverlay.FullscreenLook(true, true).Glyph == UiGlyphs.ExitFullscreen,
            "Barra do vídeo não usa a tabela única de glifos");

        Sources();
        SameIconAsExe();
        Console.WriteLine($"OK: identidade — Transmitir na mesma posição (com/sem live, palco, amigos, Cinema) e em grupo à parte, perfil no rodapé, lista sem vazar no rodapé, marca aberta/trilho, ícone de 16 a 64 px e bandeja no tamanho do sistema, cores/fontes/glifos centralizados. Ícone pequeno do sistema {SystemInformation.SmallIconSize.Width} px.");
    }

    /// <summary>
    /// Guarda da manutenção (spec 014, 7.4): cor em hexadecimal, nome de fonte e glifo da fonte de símbolos só no Theme/UiResources. As exceções são
    /// listadas com o motivo; uma nova ocorrência fora delas falha o teste para alguém decidir se vira token.
    /// </summary>
    static void Sources()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "app", "Streemu"))) root = root.Parent;
        if (root == null) { Console.WriteLine("Aviso: código-fonte não encontrado; guarda de literais pulada."); return; }
        var app = Path.Combine(root.FullName, "app", "Streemu");
        var allowed = new Dictionary<string, string>
        {
            ["Util/Theme.cs"] = "a paleta",
            ["Util/ThemePalette.cs"] = "os valores das paletas escura e clara",
            ["Util/UiResources.cs"] = "tipografia, glifos e marca",
            ["Media/StreamSender.cs"] = "texto desenhado dentro do vídeo enviado, não é interface",
            ["Ui/Avatars/AvatarDecoder.cs"] = "fundo em que GIFs com transparência são achatados no cache de avatares",
        };
        var pattern = new System.Text.RegularExpressions.Regex("ColorTranslator\\.FromHtml|\"Segoe UI|\"Consolas\"|\"Segoe MDL2|Color\\.FromArgb\\(\\s*\\d|[\\uE700-\\uF8FF]");
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(app, file).Replace('\\', '/');
            if (rel.StartsWith("bin/") || rel.StartsWith("obj/") || allowed.ContainsKey(rel)) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (pattern.IsMatch(lines[i]) && !lines[i].TrimStart().StartsWith("//")) found.Add($"{rel}:{i + 1}");
        }
        Check(found.Count == 0, "Cor, fonte ou glifo fora do Theme/UiResources: " + string.Join(", ", found));
    }

    /// <summary>A janela e a bandeja usam o mesmo .ico do exe e do instalador (embutido no build): trocar a arte num lugar troca em todos.</summary>
    static void SameIconAsExe()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "app", "Streemu"))) root = root.Parent;
        if (root == null) { Console.WriteLine("Aviso: código-fonte não encontrado; comparação do ícone pulada."); return; }
        using var embedded = typeof(UiBrand).Assembly.GetManifestResourceStream("Streemu.Arte.streemu.ico")!;
        using var copy = new MemoryStream(); embedded.CopyTo(copy);
        Check(copy.ToArray().AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(root.FullName, "app", "Streemu", "streemu.ico"))),
            "Ícone embutido diferente do streemu.ico do exe");
        foreach (var art in new[] { UiBrand.Mark, UiBrand.SleepingMumu })
            using (var image = UiBrand.LoadArt(art)) Check(image.Width == image.Height && image.Width >= 256, $"Arte {art} fora do formato (quadrada, 256 px ou mais)");
    }

    /// <summary>Quantos pixels opacos fogem do fundo: a arte é colorida, então qualquer desenho dela dá dezenas.</summary>
    static int Drawn(Bitmap image, Rectangle box, Color background)
    {
        box.Intersect(new Rectangle(0, 0, image.Width, image.Height));
        int count = 0;
        for (int y = box.Top; y < box.Bottom; y++) for (int x = box.Left; x < box.Right; x++)
        {
            var p = image.GetPixel(x, y);
            if (p.A > 200 && Math.Abs(p.R - background.R) + Math.Abs(p.G - background.G) + Math.Abs(p.B - background.B) > 60) count++;
        }
        return count;
    }

    readonly record struct RowInfo(string Kind, Rectangle Bounds);
    static List<RowInfo> Rows(SidePanel side) => ((System.Collections.IEnumerable)side.GetType().GetMethod("BuildRows", Hidden)!.Invoke(side, null)!).Cast<object>()
        .Select(r => new RowInfo(r.GetType().GetProperty("Kind")!.GetValue(r)!.ToString()!, (Rectangle)r.GetType().GetProperty("Bounds")!.GetValue(r)!)).ToList();

    static Bitmap Render(Control c) { var image = new Bitmap(c.Width, c.Height); c.DrawToBitmap(image, c.ClientRectangle); return image; }

    static bool SamePixels(Bitmap a, Bitmap b, Rectangle box)
    {
        box.Intersect(new Rectangle(0, 0, Math.Min(a.Width, b.Width), Math.Min(a.Height, b.Height)));
        for (int y = box.Top; y < box.Bottom; y++) for (int x = box.Left; x < box.Right; x++) if (a.GetPixel(x, y) != b.GetPixel(x, y)) return false;
        return true;
    }

    static bool HasColor(Bitmap image, Rectangle box, Color color)
    {
        box.Intersect(new Rectangle(0, 0, image.Width, image.Height));
        for (int y = box.Top; y < box.Bottom; y++) for (int x = box.Left; x < box.Right; x++)
        {
            var p = image.GetPixel(x, y);
            if (p.A > 200 && Math.Abs(p.R - color.R) < 12 && Math.Abs(p.G - color.G) < 12 && Math.Abs(p.B - color.B) < 12) return true;
        }
        return false;
    }

    static void Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, Hidden)!.Invoke(o, args);
    static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
