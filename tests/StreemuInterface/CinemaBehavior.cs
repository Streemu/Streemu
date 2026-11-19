using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Streemu.Filme;
using Streemu.Ui;
using Streemu.Util;

static class CinemaBehavior
{
    [DllImport("user32.dll")] static extern int GetGuiResources(IntPtr process, int flags);
    const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    public static void Run(string? output)
    {
        if (output != null) Directory.CreateDirectory(output);
        JsonObject A(string host, string film = "100-abc") => new() { ["de"] = host, ["filme"] = film, ["nome"] = "Um filme com título longo para conferir a leitura", ["anfitriao"] = host, ["estado"] = "rolando", ["stream"] = true, ["pessoas"] = 2 };
        var a = A("Ana"); var b = A("Bruno");
        var selection = new CinemaSelection();
        selection.Select(b); selection.Refresh([a, b], false);
        Check((string?)selection.Selected?["de"] == "Bruno", "Mesmo arquivo confundiu anfitriões");
        selection.Refresh([a], false);
        Check(selection.Missing && (string?)selection.Selected?["de"] == "Bruno", "Anúncio sumiu e mudou a escolha");
        selection.Refresh([a, A("Bruno", "200-outro")], false);
        Check(selection.Missing, "Reabertura com outro arquivo substituiu a sessão antiga");
        selection.Refresh([a, b], false); Check(!selection.Missing, "Anúncio da sessão escolhida não retornou");
        selection.Refresh([a], true); Check(!selection.Missing, "Operação em curso perdeu o anúncio fixado");
        selection.Create(); Check(selection.Creating && selection.Selected == null, "Criar herdou um filme alheio");

        var announcements = new List<JsonObject> { a, b };
        int commands = 0; bool external = false;
        JsonObject? connected = null;
        TaskCompletionSource<string?>? connection = null;
        var lig = new FilmeLigacao("Rafa", () => null, () => announcements.LastOrDefault(), _ => commands++,
            a => { connected = a; commands++; return connection?.Task ?? Task.FromResult<string?>("ligação fictícia"); }, () => commands++, _ => commands++,
            () => null, () => 0, () => null, () => null, () => announcements, () => external, () => commands++);
        using var surface = new Panel { Size = new Size(1200, 760), Font = new Font(UiTypography.Family, UiTypography.Body) };
        using var tela = new FilmeTela(lig, true);
        surface.Controls.Add(tela); surface.CreateControl(); tela.CreateControl();
        var view = Field<CinemaView>(tela, "_cinema");
        var actual = Field<CinemaSelection>(tela, "_selecao");
        foreach (var size in new[] { new Size(1200, 760), new Size(736, 568), new Size(416, 668), new Size(416, 400) })
        {
            surface.Size = size; Call(tela, "Atualizar");
            var cards = Field<Dictionary<string, object>>(view, "_cards", dictionary: true);
            Check(cards.Count == 2, "Cinema omitiu uma sessão");
            foreach (var card in cards.Values.Cast<Control>())
            {
                var enter = Field<Button>(card, "_enter");
                Check(enter.Left >= 0 && enter.Right <= card.Width && enter.Bottom <= card.Height, "Entrar fora do card");
                Check(card.Width <= surface.Width, "Card extrapolou a janela");
            }
            var picked = cards.Values.Cast<Control>().Single(c => (string?)Field<JsonObject>(c, "_announcement")["de"] == "Bruno");
            Field<Button>(picked, "_enter").PerformClick(); Call(tela, "Atualizar");
            Check(actual.Detail && (string?)actual.Selected?["de"] == "Bruno", "Clique não selecionou Bruno");
            Check(commands == 0 && tela.Sessao == null && tela.Player == null, "Selecionar iniciou participação ou player");
            var detail = Field<Control>(tela, "_card");
            foreach (Control child in detail.Controls)
                Check(child.Right <= detail.ClientSize.Width, $"Conteúdo cortado: {child.Text} em {size}");
            if (output != null)
            {
                using var image = new Bitmap(surface.Width, surface.Height); surface.DrawToBitmap(image, surface.ClientRectangle);
                image.Save(Path.Combine(output, $"cinema-entrada-{size.Width}x{size.Height}.png"));
            }
            Field<Button>(view, "_back").PerformClick();
            Check(!actual.Detail, "Voltar não restaurou a descoberta");
            if (output != null)
            {
                using var image = new Bitmap(surface.Width, surface.Height); surface.DrawToBitmap(image, surface.ClientRectangle);
                image.Save(Path.Combine(output, $"cinema-lista-{size.Width}x{size.Height}.png"));
            }
        }
        CheckEmptyCinema(surface, tela, view, announcements, output);
        Field<Button>(view, "_create").PerformClick();
        Check(actual.Creating && commands == 0, "Criar não preparou sessão própria sem iniciar");
        Field<Button>(view, "_back").PerformClick(); external = true; Call(tela, "Atualizar");
        Field<Button>(view, "_create").PerformClick();
        Check(!actual.Detail && commands == 0, "Criar encerrou ou substituiu participação existente");
        external = false;
        Call(tela, "SelecionarCinema", b); announcements.Remove(b); Call(tela, "Atualizar");
        Check(actual.Missing && !Field<Button>(tela, "_principal").Enabled && !Field<Button>(tela, "_stream").Enabled, "Anúncio ausente ainda permite entrar");
        announcements.Add(b); Call(tela, "Atualizar");
        Check(!actual.Missing && Field<Button>(tela, "_principal").Enabled && Field<Button>(tela, "_tenho").Enabled, "Anúncio recuperado não reativou as ações");
        Call(tela, "SelecionarCinema", b); Field<Button>(tela, "_tenho").Enabled = true;
        JsonObject? delivered = null;
        tela.AssistirStreamPedido += announcement => delivered = announcement;
        Call(tela, "Atualizar");
        Field<Button>(tela, "_stream").PerformClick(); Field<Button>(tela, "_stream").PerformClick();
        Check((string?)delivered?["de"] == "Bruno", "Assistir agora recebeu anúncio de outro anfitrião");
        Check(tela.StreamAbrindo && commands == 0, "Entrada não protegeu clique repetido");
        tela.StreamAbrindo = false;
        connection = new();
        var download = tela.BaixarAsync(Path.GetTempPath());
        var until = Stopwatch.StartNew();
        while (connected == null && until.ElapsedMilliseconds < 3000) Pump(10);
        Check((string?)connected?["de"] == "Bruno", "Download conectou ao anfitrião errado");
        announcements.Remove(b); Field<Button>(view, "_back").PerformClick(); Call(tela, "Atualizar");
        Check(tela.Ocupado && (string?)actual.Selected?["de"] == "Bruno", "Download em curso trocou/descartou anfitrião");
        Check(Field<bool>(tela, "_listaCinema"), "Voltar não abriu a descoberta durante o download");
        Field<Button>(view, "_resume").PerformClick(); Check(!Field<bool>(tela, "_listaCinema"), "Retornar perdeu a entrada em andamento");
        connection.SetResult("Conexão fictícia indisponível. Tente de novo.");
        until.Restart(); while (!download.IsCompleted && until.ElapsedMilliseconds < 3000) Pump(10);
        download.GetAwaiter().GetResult();
        Check(!tela.Ocupado && tela.Player == null && (string?)actual.Selected?["de"] == "Bruno", "Falha perdeu o contexto da entrada");
        Motion();
        Console.WriteLine($"OK: Cinema com anfitriões do mesmo arquivo, seleção/ausência/reabertura, criação independente, 4 tamanhos/rolagem, participação protegida e anúncio correto na entrada. Movimento encerra/libera relógio. DPI {surface.DeviceDpi}. Sem player, captura, rede ou estado pessoal.");
    }
    static void Motion()
    {
        using var owner = new Button(); owner.CreateControl(); int draws = 0;
        using var motion = new UiMotion(owner, () => draws++);
        motion.To(1); Pump(240);
        Check(motion.Value == 1 && Clock() == null, "Movimento não terminou/liberou o relógio");
        motion.To(0); owner.Visible = false;
        Check(motion.Value == 0 && Clock() == null, "Controle invisível reteve animação");
        owner.Visible = true; motion.To(1); motion.Dispose(); Check(Clock() == null, "Descarte reteve relógio");
        var watch = Stopwatch.StartNew(); int before = draws; Pump(250);
        Check(draws == before, "Repouso continuou redesenhando");
        static object? Clock() => typeof(UiMotion).GetField("_clock", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
    }
    public static void Cost(string? output)
    {
        var announcements = new List<JsonObject>();
        var lig = new FilmeLigacao("Rafa", () => null, () => null, _ => { }, _ => Task.FromResult<string?>("ensaio sem rede"),
            () => { }, _ => { }, () => null, () => 0, () => null, () => null, () => announcements);
        using var surface = new Panel { Size = new Size(1200, 760) };
        using var tela = new FilmeTela(lig, true);
        surface.Controls.Add(tela); surface.CreateControl(); tela.CreateControl();
        var view = Field<CinemaView>(tela, "_cinema"); var button = Field<Button>(view, "_create");
        using var image = new Bitmap(1200, 760);
        var samples = new List<object>();
        object Snapshot()
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            using var p = Process.GetCurrentProcess();
            return new { privateMb = p.PrivateMemorySize64 / 1048576.0, managedMb = GC.GetTotalMemory(false) / 1048576.0,
                gdi = GetGuiResources(p.Handle, 0), user = GetGuiResources(p.Handle, 1), handles = p.HandleCount };
        }
        void Cycle(int i)
        {
            announcements.Clear();
            for (int n = 0; n < new[] { 0, 1, 2, 5, 30 }[i % 5]; n++) announcements.Add(new JsonObject
            { ["de"] = $"pessoa{n}", ["filme"] = "100-filme", ["nome"] = "Filme de teste", ["anfitriao"] = $"Pessoa {n}" });
            surface.Size = new Size(i % 2 == 0 ? 1200 : 416, 760);
            Call(tela, "Atualizar");
            typeof(Control).GetMethod(i % 2 == 0 ? "OnMouseEnter" : "OnMouseLeave", Hidden)!.Invoke(button, [EventArgs.Empty]);
            Pump(170);
            surface.DrawToBitmap(image, surface.ClientRectangle);
        }
        for (int i = 0; i < 10; i++) Cycle(i);
        Pump(500); // drena as pinturas nativas do último desenho antes de medir repouso
        Check(!Field<System.Windows.Forms.Timer>(tela, "_relogioTela").Enabled, "Descoberta em repouso manteve sondagem visual");
        Check(typeof(UiMotion).GetField("_clock", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null) == null, "Transição não terminou antes do repouso");
        samples.Add(Snapshot());
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime; Pump(5000);
        double idleCpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        for (int round = 0; round < 3; round++)
        {
            var watch = Stopwatch.StartNew(); cpu = process.TotalProcessorTime;
            for (int i = 0; i < 20; i++) Cycle(i);
            samples.Add(new { round, elapsedMs = watch.Elapsed.TotalMilliseconds, cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds, resources = Snapshot() });
        }
        announcements.Clear(); Call(tela, "Atualizar"); Pump(200); samples.Add(Snapshot());
        var json = JsonSerializer.Serialize(new { dpi = surface.DeviceDpi, motionEnabled = UiMotion.Enabled, idleCpuMs, samples }, new JsonSerializerOptions { WriteIndented = true });
        if (output != null) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!); File.WriteAllText(output, json); }
        Console.WriteLine(json);
    }
    // Cinema sem sessões (2026-11-16): texto e Mumu dentro da página, sem se cobrirem, e com os centros na mesma altura quando lado a lado
    static void CheckEmptyCinema(Panel surface, FilmeTela tela, CinemaView view, List<JsonObject> announcements, string? output)
    {
        var saved = announcements.ToList(); announcements.Clear();
        var empty = Field<CinemaEmptyState>(view, "_ready");
        foreach (var size in new[] { new Size(1200, 760), new Size(736, 568), new Size(416, 668), new Size(416, 400) })
        {
            surface.Size = size; Call(tela, "Atualizar");
            Check(empty.Visible, $"Cinema vazio não mostrou a tela pronta em {size}");
            var scroll = (ScrollableControl)empty.Parent!;
            Check(scroll.DisplayRectangle.Height <= Math.Max(scroll.ClientSize.Height, empty.MinimumContentHeight(empty.Width)) + 1,
                $"Barra de rolagem sem conteúdo no Cinema vazio em {size}: {scroll.DisplayRectangle} numa área de {scroll.ClientSize}");
            var art = Field<ArtView>(empty, "_mumu"); var headline = Field<Rectangle>(empty, "_headline"); var hint = Field<Rectangle>(empty, "_hint");
            var accent = Field<Rectangle>(empty, "_accent"); var text = Rectangle.Union(accent, Rectangle.Union(headline, hint));
            Check(text.Top >= 0 && text.Left >= 0 && text.Right <= empty.Width && text.Bottom <= empty.Height, $"Texto do Cinema vazio cortado em {size}: {text} em {empty.Size}");
            if (!art.Visible) { SaveEmptyShot(surface, output, size); continue; }
            Check(art.Right <= empty.Width && art.Bottom <= empty.Height, $"Mumu cortada em {size}: {art.Bounds} em {empty.Size}");
            Check(!art.Bounds.IntersectsWith(text), $"Mumu cobre o texto em {size}");
            bool side = art.Left >= text.Right;
            Check(!side || Math.Abs((art.Top + art.Bottom) - (text.Top + text.Bottom)) <= 2, $"Mumu e texto desalinhados em {size}: {art.Bounds} x {text}");
            Check(!side || accent.Left == headline.Left, $"Traço fora da coluna do texto em {size}");
            SaveEmptyShot(surface, output, size);
        }
        announcements.AddRange(saved); Call(tela, "Atualizar");
    }
    static void SaveEmptyShot(Panel surface, string? output, Size size)
    {
        if (output == null) return;
        using var image = new Bitmap(surface.Width, surface.Height); surface.DrawToBitmap(image, surface.ClientRectangle);
        image.Save(Path.Combine(output, $"cinema-vazio-{size.Width}x{size.Height}.png"));
    }
    static void Pump(int ms) { var w = Stopwatch.StartNew(); while (w.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(5); } }
    static T Field<T>(object o, string name, bool dictionary = false)
    {
        var value = o.GetType().GetField(name, Hidden)!.GetValue(o)!;
        if (dictionary) return (T)(object)((System.Collections.IDictionary)value).Keys.Cast<string>().ToDictionary(k => k, k => ((System.Collections.IDictionary)value)[k]!);
        return (T)value;
    }
    static void Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, Hidden)!.Invoke(o, args);
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
