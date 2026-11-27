using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Streemu;
using Streemu.Ui;
using Streemu.Ui.Multistream;

static class Program
{
    [DllImport("user32.dll")] static extern int GetGuiResources(IntPtr process, int flags);
    static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    [STAThread] static int Main(string[] args)
    {
        try { Run(args); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static void Run(string[] args)
    {
        Environment.SetEnvironmentVariable("STREEMU_NOSTATE", "1");
        Environment.SetEnvironmentVariable("STREEMU_TEST_UI_ONLY", "1");
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        if (args.FirstOrDefault() == "--sidebar-rows") { SidebarOverlapBehavior.RowsCheck(args.Skip(1).FirstOrDefault()); return; }
        if (args.FirstOrDefault() == "--player-bar") { SidebarOverlapBehavior.PlayerBar(args.Skip(1).FirstOrDefault() ?? "."); return; }
        if (args.FirstOrDefault() == "--sidebar-audit") { SidebarOverlapBehavior.Run(args.Skip(1).FirstOrDefault()); return; }
        if (args.FirstOrDefault() == "--palco-estresse-check") { PalcoEstresseBehavior.Run(args.Skip(1).FirstOrDefault()); return; }
        if (args.FirstOrDefault() == "--alt-tab-check") { AltTabBehavior.Run(args.Skip(1).FirstOrDefault()); return; }
        if (args.FirstOrDefault() == "--palco-tema-check") { PalcoTemaBehavior.Run(args.Skip(1).FirstOrDefault()); return; }
        if (args.FirstOrDefault() == "--denuncia-check") { DenunciaBehavior.Run(); return; }
        if (args.FirstOrDefault() == "--layout-check") { LayoutBehavior.Run(); return; }
        if (args.FirstOrDefault() == "--fontes-check") { FontesBehavior.Run(args.Skip(1).FirstOrDefault()); return; }
        if (args.FirstOrDefault() == "--cinema-player-check") { CinemaPlayerBehavior.Run(args.Skip(1).FirstOrDefault()); return; }
        if (args.FirstOrDefault() == "--cinema-player-cost") { CinemaPlayerBehavior.Cost(args.Skip(1).FirstOrDefault(), args.Length > 2 ? int.Parse(args[2]) : 3); return; }
        if (args.FirstOrDefault() == "--entrada-check") { EntradaBehavior.Run(args.Skip(1).FirstOrDefault()); return; }
        if (args.FirstOrDefault() == "--grupos-check") { GruposBehavior.Run(args.Skip(1).FirstOrDefault()); return; }
        if (args.FirstOrDefault() == "--novidades-check") { NovidadesBehavior.Run(args.Skip(1).FirstOrDefault()); return; }
        if (args.FirstOrDefault() == "--apoiar-check") { ApoiarBehavior.Run(args.Skip(1).FirstOrDefault()); return; }
        if (args.FirstOrDefault() == "--novidades-leitura-check") { NovidadesLeituraBehavior.Run(args.Skip(1).FirstOrDefault()); return; }
        if (args.FirstOrDefault() == "--cinema-check") { CinemaBehavior.Run(args.Skip(1).FirstOrDefault()); return; }
        if (args.FirstOrDefault() == "--cinema-cost") { CinemaBehavior.Cost(args.Skip(1).FirstOrDefault()); return; }
        if (args.FirstOrDefault() == "--native-check") { Behavior.Native(); return; }
        if (args.FirstOrDefault() == "--identity-check") { IdentityBehavior.Run(args.Skip(1).FirstOrDefault()); return; }
        if (args.FirstOrDefault() == "--check") { Behavior.Run(args.Skip(1).FirstOrDefault()); return; }
        using var form = new MainForm(true);
        // Sem Load/Show: controles reais, sem sala, rede, captura, player ou estado pessoal.
        using var surface = new Panel { Size = new Size(1302, 700) };
        var home = Field<Panel>(form, "_homeHost");
        var settings = Field<Panel>(form, "_settingsHost");
        form.Controls.Remove(home); form.Controls.Remove(settings);
        surface.Controls.Add(home); surface.Controls.Add(settings);
        surface.CreateControl();
        using var image = new Bitmap(surface.Width, surface.Height);
        var cards = Field<LiveCardsView>(form, "_liveCards");
        var side = Field<SidePanel>(form, "_side");
        var samples = new List<object>();
        void Draw() { surface.DrawToBitmap(image, surface.ClientRectangle); side.DrawToBitmap(image, new Rectangle(0, 0, side.Width, Math.Min(side.Height, image.Height))); }
        for (int i = 0; i < 12; i++) Cycle(i);
        Collect();
        samples.Add(Snapshot("base"));
        var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(10); }
        double idleCpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        for (int round = 0; round < 3; round++)
        {
            cpu = process.TotalProcessorTime; watch.Restart();
            var times = new List<double>();
            for (int i = 0; i < 60; i++) { var step = Stopwatch.StartNew(); Cycle(i); times.Add(step.Elapsed.TotalMilliseconds); }
            double elapsed = watch.Elapsed.TotalMilliseconds, cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            Collect();
            times.Sort();
            samples.Add(new { round, elapsed, cpuMs, p50 = times[30], p95 = times[57], resources = Snapshot("ciclos") });
        }
        cards.SetCards([]); Draw(); Collect();
        samples.Add(Snapshot("final"));
        var report = new { dpi = surface.DeviceDpi, idleCpuMs, idleWallMs = 5000, samples };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (args.FirstOrDefault() is { } path) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); File.WriteAllText(path, json); }
        Console.WriteLine(json);
        void Cycle(int i)
        {
            Call(form, "ShowView", false);
            int count = new[] { 0, 1, 2, 5, 30 }[i % 5];
            cards.SetCards(Enumerable.Range(0, count).Select(n => new LiveCard($"amigo{n}", $"Amigo com nome longo {n}", "Jogo de teste", "1080p · 60 FPS", null, false)).ToList());
            surface.Size = new Size(i % 2 == 0 ? 1302 : 736, 700);
            home.PerformLayout(); Draw();
            Call(form, "ShowSettings");
            Field<SettingsView>(form, "_settingsPage").SelectCategory(i % 2 == 0 ? "Diagnóstico" : "Aparência");
            Draw(); Application.DoEvents();
        }
    }
    static object Snapshot(string name)
    {
        using var p = Process.GetCurrentProcess();
        return new { name, workingMb = p.WorkingSet64 / 1048576.0, privateMb = p.PrivateMemorySize64 / 1048576.0, managedMb = GC.GetTotalMemory(false) / 1048576.0,
            gdi = GetGuiResources(p.Handle, 0), user = GetGuiResources(p.Handle, 1), handles = p.HandleCount };
    }
    static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Hidden)!.GetValue(o)!;
    static object? Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, Hidden)!.Invoke(o, args);
}
