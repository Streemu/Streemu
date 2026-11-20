using Streemu;
using Streemu.Media;
using Streemu.Ui;
using Streemu.Ui.Multistream;
using Streemu.Util;

/// <summary>
/// Troca de tema com lives no palco (relato de 2026-11-19: com 3 lives, passar do escuro para o claro deixou artefatos brancos nas barras). A janela real
/// fica fora da tela, sem rede nem player; os vídeos não abrem, só o lugar deles, as faixas de nome e as barras de controle. Confere, nos dois sentidos:
/// a barra recolorida pela troca sai igual a uma criada já no tema novo (mostrada e escondida na hora da troca), o nome na faixa do palco lê sobre o
/// preto, o "+N" da fileira de pessoas lê sobre o próprio fundo e a pílula AO VIVO de uma live sozinha não tem borda da cor do fundo da janela.
/// </summary>
static class PalcoTemaBehavior
{
    const string Quality = "1080p 60 FPS";
    static readonly IReadOnlyList<RosterEntry> People =
        Enumerable.Range(0, 14).Select(n => new RosterEntry($"Amigo {n}", n == 0, n == 3)).ToList();

    public static void Run(string? output)
    {
        if (output != null) Directory.CreateDirectory(output);
        bool wasLight = Theme.IsLight;
        using var form = new MainForm(true) { StartPosition = FormStartPosition.Manual, Location = new Point(-8000, 0), Size = new Size(1500, 900), ShowInTaskbar = false };
        var problems = new List<string>();
        Exception? failure = null;
        form.Shown += async (_, _) =>
        {
            try { await CheckBothWaysAsync(form, output, problems); }
            catch (Exception ex) { failure = ex; }
            finally { ThemeSwitch.Set(form, wasLight); form.Close(); }
        };
        Application.Run(form);
        if (failure != null) throw failure;
        if (problems.Count > 0) throw new Exception($"{problems.Count} problema(s) na troca de tema com lives:\n" + string.Join("\n", problems));
        Console.WriteLine("OK: troca de tema com 1 e 3 lives nos dois sentidos: barra recolorida igual à criada no tema, nome da faixa legível sobre o preto, \"+N\" legível e pílula AO VIVO sem borda do fundo.");
    }

    static async Task CheckBothWaysAsync(MainForm form, string? output, List<string> problems)
    {
        foreach (bool light in new[] { true, false, true, false })
        {
            ThemeSwitch.Set(form, !light);
            await UiProbe.SettleAsync();
            string label = light ? "escuro→claro" : "claro→escuro";
            await CheckStageAsync(form, 3, light, label, output, problems);
            await CheckStageAsync(form, 1, light, label, output, problems);
        }
    }

    static async Task CheckStageAsync(MainForm form, int lives, bool light, string label, string? output, List<string> problems)
    {
        using var stage = new PlayerStage(form);
        UiProbe.Set(form, "_playerStage", stage);
        var viewers = Enumerable.Range(0, lives).Select(_ => new Viewer()).ToList();
        for (int i = 0; i < lives; i++) { int n = i; stage.Add(viewers[i], () => $"Amigo {n} · Jogo de teste · ao vivo há 12 min"); }
        form.StreamsChanged(lives);
        UiProbe.Call(form, "ShowView", true);
        var shown = Overlay(viewers[0], stage);
        var hidden = viewers.Count > 1 ? Overlay(viewers[1], stage) : null;
        PrepareShown(shown);
        await UiProbe.SettleAsync();
        ThemeSwitch.Set(form, light);
        await UiProbe.SettleAsync(10); // a transição (ThemeFade) dura 320 ms
        string name = $"{(light ? "claro" : "escuro")}-{lives}";
        CompareWithFresh(shown, viewers[0], stage, $"{label}, {lives} live(s), barra à vista na troca", output == null ? null : Path.Combine(output, $"barra-{name}"), problems);
        if (hidden != null) { PrepareShown(hidden); CompareWithFresh(hidden, viewers[1], stage, $"{label}, {lives} lives, barra escondida na troca", null, problems); }
        problems.AddRange(RosterChip(shown).Select(p => $"{label}, {lives} live(s): {p}"));
        problems.AddRange(PeopleList(shown).Select(p => $"{label}, {lives} live(s): {p}"));
        if (lives >= 2) problems.AddRange(CaptionsReadable(form, output == null ? null : Path.Combine(output, $"faixas-{name}.png")).Select(p => $"{label}: {p}"));
        else problems.AddRange(HeaderPill(shown, stage, viewers[0]).Select(p => $"{label}: {p}"));
        foreach (var o in new[] { shown, hidden }) o?.Close();
        UiProbe.Set(form, "_playerStage", null);
        form.StreamsChanged(0);
    }

    static PlayerOverlay Overlay(Viewer viewer, PlayerStage stage)
    {
        var overlay = PlayerOverlay.ShowFor(viewer, () => Quality, () => People, stage: stage);
        UiProbe.Field<System.Windows.Forms.Timer>(overlay, "_tick").Stop(); // sem o mouse sobre o vídeo o relógio a esconderia antes da foto
        return overlay;
    }

    /// <summary>A barra à vista, fora da tela, com as pessoas e o estado do palco já aplicados (o que o relógio dela faria).</summary>
    static void PrepareShown(PlayerOverlay overlay)
    {
        overlay.Bounds = new Rectangle(-8000, 1000, 1400, overlay.LogicalToDeviceUnits(44));
        if (!overlay.Visible) overlay.Show();
        Application.DoEvents();
        overlay.Bounds = new Rectangle(-8000, 1000, 1400, overlay.LogicalToDeviceUnits(44));
        UiProbe.Field<RosterStrip>(overlay, "_roster").SetEntries(People);
        UiProbe.Call(overlay, "KeepLiveNow");
        UiProbe.Call(overlay, "LayoutZones");
    }

    static void Pose(PlayerOverlay overlay)
    {
        overlay.Bounds = new Rectangle(-8000, 1000, 1400, overlay.LogicalToDeviceUnits(44));
        UiProbe.Call(overlay, "LayoutZones");
    }

    /// <summary>A barra que atravessou a troca tem que sair pixel a pixel igual a uma criada já no tema atual.</summary>
    static void CompareWithFresh(PlayerOverlay switched, Viewer viewer, PlayerStage stage, string label, string? photo, List<string> problems)
    {
        using var fresh = Overlay(viewer, stage);
        PrepareShown(fresh);
        Pose(switched); Pose(fresh); // o palco realinha a barra ao vídeo a cada ciclo: as duas no mesmo tamanho logo antes da foto
        using var a = UiProbe.Photo(switched);
        using var b = UiProbe.Photo(fresh);
        if (photo != null) { a.Save(photo + "-trocada.png"); b.Save(photo + "-nova.png"); }
        var diff = Differences(a, b);
        if (diff.Count > 0) problems.Add($"{label}: {diff.Count} pixel(s) diferentes da barra criada no tema (ex.: {string.Join("; ", diff.Take(3))})");
        fresh.Close();
    }

    static List<string> Differences(Bitmap a, Bitmap b)
    {
        var diff = new List<string>();
        if (a.Size != b.Size) { diff.Add($"tamanho {a.Size} x {b.Size}"); return diff; }
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) is var p && b.GetPixel(x, y) is var q && p.ToArgb() != q.ToArgb()) diff.Add($"({x},{y}) {Hex(p)} em vez de {Hex(q)}");
        return diff;
    }

    static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>O "+N" (pessoas que não cabem) precisa ler sobre o próprio fundo: texto grande em negrito pede contraste 3.</summary>
    static IEnumerable<string> RosterChip(PlayerOverlay overlay)
    {
        var roster = UiProbe.Field<RosterStrip>(overlay, "_roster");
        roster.Width = roster.LogicalToDeviceUnits(140); // estreita: sobra gente para o "+N"
        roster.SetEntries(People);
        if (roster.Hidden == 0) { yield return "a fileira estreita não mostrou o \"+N\""; yield break; }
        using var image = UiProbe.Photo(roster);
        var chip = Rectangle.Intersect(roster.ChipBounds, new Rectangle(Point.Empty, image.Size));
        var fill = image.GetPixel(chip.X + 1, chip.Y + 1);
        double best = Pixels(image, chip).Max(p => UiProbe.Contrast(p, fill));
        if (best < 3) yield return $"\"+{roster.Hidden}\" ilegível: contraste {best:0.0} sobre {Hex(fill)} (mínimo 3)";
    }

    /// <summary>
    /// A lista do "+N" é criada junto com a barra e só abre depois, às vezes já no outro tema: aberta pela primeira vez depois da troca, tem que sair igual
    /// a uma lista criada no tema atual.
    /// </summary>
    static IEnumerable<string> PeopleList(PlayerOverlay overlay)
    {
        var roster = UiProbe.Field<RosterStrip>(overlay, "_roster");
        var chip = roster.RectangleToScreen(roster.ChipBounds);
        var switched = UiProbe.Field<RosterPopup>(overlay, "_popup");
        switched.ShowAbove(overlay, chip, People);
        using var fresh = new RosterPopup();
        fresh.ShowAbove(overlay, chip, People);
        Application.DoEvents();
        using var a = UiProbe.Photo(switched);
        using var b = UiProbe.Photo(fresh);
        var diff = Differences(a, b);
        switched.Hide();
        if (diff.Count > 0) yield return $"lista do \"+N\" aberta depois da troca: {diff.Count} pixel(s) diferentes da criada no tema (ex.: {string.Join("; ", diff.Take(3))})";
    }

    static IEnumerable<Color> Pixels(Bitmap image, Rectangle r)
    {
        for (int y = r.Top; y < r.Bottom; y++)
            for (int x = r.Left; x < r.Right; x++) yield return image.GetPixel(x, y);
    }

    /// <summary>Com 2+ lives o nome fica numa faixa do palco, que é preta nos dois temas: o nome precisa de contraste 4,5 sobre ela.</summary>
    static IEnumerable<string> CaptionsReadable(MainForm form, string? photo)
    {
        var canvas = UiProbe.Field<StageCanvas>(form, "_stageCanvas");
        var captions = UiProbe.Field<IReadOnlyList<StageCaption>>(canvas, "_captions");
        if (captions.Count == 0) { yield return "o palco com várias lives ficou sem faixas de nome"; yield break; }
        using var image = UiProbe.Photo(canvas);
        if (photo != null) image.Save(photo);
        var back = Theme.VideoMargin;
        foreach (var caption in captions)
        {
            var r = canvas.RectangleToClient(caption.Area);
            int avatar = r.Height; // o avatar e a pílula ficam fora da conta: só o texto
            var text = Rectangle.Intersect(new Rectangle(r.X + avatar, r.Y, r.Width / 3, r.Height), new Rectangle(Point.Empty, image.Size));
            double best = Pixels(image, text).Select(p => UiProbe.Contrast(p, back)).DefaultIfEmpty(0).Max();
            if (best < 4.5) yield return $"nome \"{caption.Text.Split(" · ")[0]}\" ilegível na faixa: contraste {best:0.0} sobre o preto (mínimo 4,5)";
        }
    }

    /// <summary>
    /// Só a pílula: a janela é recortada por região (sem serrilhado suave), então o que ela pinta nas bordas da região aparece sobre o vídeo. Precisa ser a
    /// própria cor da pílula: com o fundo da janela, o claro deixava um contorno branco em volta do AO VIVO.
    /// </summary>
    static IEnumerable<string> HeaderPill(PlayerOverlay overlay, PlayerStage stage, Viewer viewer)
    {
        var header = UiProbe.Field<Form>(overlay, "_header"); // LiveHeaderOverlay é interna do app
        if (stage.HeaderOver(viewer) is not { } caption) { yield return "a live sozinha ficou sem cabeçalho por cima"; yield break; }
        UiProbe.Call(header, "Place", caption with { Area = new Rectangle(-8000, 1200, caption.Area.Width, caption.Area.Height) }, false, overlay.Handle, IntPtr.Zero);
        Application.DoEvents();
        using var image = UiProbe.Photo(header);
        var region = header.Region; // é da janela: não descartar aqui
        if (region == null) { yield return "a pílula sozinha ficou sem recorte"; yield break; }
        using var g = header.CreateGraphics();
        int mid = image.Height / 2;
        int left = Enumerable.Range(0, image.Width).First(x => region.IsVisible(x, mid, g));
        int right = Enumerable.Range(0, image.Width).Reverse().First(x => region.IsVisible(x, mid, g));
        foreach (int x in new[] { left, right })
            if (Distance(image.GetPixel(x, mid), Theme.Error) > 60) yield return $"borda da pílula AO VIVO em ({x},{mid}) é {Hex(image.GetPixel(x, mid))}, não o vermelho {Hex(Theme.Error)}: contorno do fundo sobre o vídeo";
        header.Hide();
    }

    static int Distance(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
}
