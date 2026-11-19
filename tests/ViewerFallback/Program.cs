using System.Diagnostics;
using Streemu.Media;
using Streemu.Util;

static class Program
{
    const string SurfaceError = "Failed to create window: Installed Vulkan doesn't implement the VK_KHR_surface extension";

    static async Task<int> Main(string[] args)
    {
        // Uma cópia do apphost se passa pelo ffplay. Nenhum teste abre janela, rede ou dispositivo de áudio.
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "ffplay") return await FakePlayer(args);
        File.Copy(Environment.ProcessPath!, Path.Combine(AppContext.BaseDirectory, "ffplay.exe"), true);
        var results = new List<string>();
        int failed = 0;
        foreach (var name in new[] { "surface-zero", "surface-late", "vram-loop", "exit-error", "normal-close", "cpu-error", "retry-cpu-error", "network-close", "stop-early", "stop-retry", "resize", "preview" })
        {
            if (Environment.GetEnvironmentVariable("VIEWER_CASE_FILTER") is { } filter && name != filter) continue;
            try { await Check(name); results.Add($"OK    player-{name}"); }
            catch (Exception e) { failed++; results.Add($"FALHA player-{name}: {e.Message}"); }
        }
        foreach (var line in results) Console.WriteLine(line);
        if (args.Length == 1) File.WriteAllLines(args[0], results);
        return failed == 0 ? 0 : 1;
    }

    static async Task<int> FakePlayer(string[] args)
    {
        string name = Environment.GetEnvironmentVariable("VIEWER_FAKE_CASE")!;
        bool hardware = args.Contains("-enable_vulkan");
        File.AppendAllText(Environment.GetEnvironmentVariable("VIEWER_FAKE_TRACE")!, hardware ? "gpu\n" : "cpu\n");
        if (!hardware && name is not ("cpu-error" or "retry-cpu-error")) { await Task.Delay(20000); return 0; }
        if (name == "normal-close") { await Task.Delay(100); return 0; }
        if (name is "stop-early" or "resize") { await Task.Delay(20000); return 0; }
        if (name == "network-close") { Console.Error.WriteLine("Connection to srt://127.0.0.1 failed: I/O error"); return 0; }
        if (name == "exit-error") return 1;
        if (name == "surface-late") await Task.Delay(4200);
        Console.Error.WriteLine(name == "vram-loop" ? "[error] VK_ERROR_OUT_OF_DEVICE_MEMORY" : SurfaceError);
        if (name == "vram-loop") await Task.Delay(20000);
        return 0;
    }

    static async Task Check(string name)
    {
        string trace = Path.GetTempFileName();
        Environment.SetEnvironmentVariable("VIEWER_FAKE_CASE", name);
        Environment.SetEnvironmentVariable("VIEWER_FAKE_TRACE", trace);
        Log.Lines.Clear();
        var viewer = new Viewer { UseHardware = name != "cpu-error" };
        int restarts = 0, exits = 0;
        using var exited = new ManualResetEventSlim();
        viewer.Restarting += () =>
        {
            Interlocked.Increment(ref restarts);
            if (name == "stop-retry") viewer.Stop();
        };
        viewer.Exited += () => { Interlocked.Increment(ref exits); exited.Set(); };
        try
        {
            if (name == "preview") viewer.StartLocal(_ => new Tap(), "teste");
            else viewer.Start(12345, "somente-teste", 80, "teste");
            bool shouldRetry = name is not ("normal-close" or "cpu-error" or "network-close");
            if (name == "stop-early")
            {
                await Until(() => ReadTrace(trace) == "gpu");
                viewer.Stop();
                await Until(() => Volatile.Read(ref exits) == 1);
                Require(restarts == 0 && viewer.FellBackToCpu == null, "Stop tentou recuperar o player.");
                return;
            }
            if (name == "resize")
            {
                await Until(() => ReadTrace(trace) == "gpu");
                viewer.Reopen(new System.Drawing.Rectangle(0, 0, 640, 360));
                await Until(() => ReadTrace(trace) == "gpu,gpu" && !viewer.Reopening);
                Require(exits == 0 && restarts == 1 && viewer.Running && viewer.UsingHardware, "Redimensionar encerrou a live ou trocou a decodificação.");
                return;
            }
            if (name == "stop-retry")
            {
                await Until(() => Volatile.Read(ref restarts) == 1);
                await Task.Delay(1400); // Relaunch dá 1200 ms para o remetente refazer o SRT.
                Require(!viewer.Running && ReadTrace(trace) == "gpu", "Stop durante a recuperação abriu outro player.");
                return;
            }
            if (!shouldRetry)
            {
                await Until(() => Volatile.Read(ref exits) == 1);
                Require(restarts == 0 && viewer.FellBackToCpu == null, "Fechamento normal ou erro na CPU causou recuperação.");
                Require(ReadTrace(trace) == (name == "cpu-error" ? "cpu" : "gpu"), "Abriu player extra.");
                return;
            }
            await Until(() => ReadTrace(trace) == "gpu,cpu");
            if (name == "retry-cpu-error")
            {
                await Until(() => Volatile.Read(ref exits) == 1);
                Require(restarts == 1 && !viewer.Running, "A recuperação entrou em repetição após a CPU falhar.");
                return;
            }
            Require(viewer.Running && !viewer.UsingHardware, "A recuperação não manteve o player na CPU.");
            Require(restarts == 1 && exits == 0, "Encerrou a live ou recuperou mais de uma vez.");
            Require(viewer.FellBackToCpu != null, "Não registrou o motivo da recuperação.");
            if (name is "surface-zero" or "surface-late" or "preview")
                Require(viewer.ErrorCounts()["vulkan_outro"]?.GetValue<int>() == 1, "Perdeu a última linha do stderr.");
        }
        finally
        {
            viewer.Stop();
            await Until(() => !viewer.Running);
            // Aguarda a conclusão do observador antes de descartar o sinal e iniciar o próximo caso.
            if (name != "stop-retry") exited.Wait(3000);
            File.Delete(trace);
        }
    }

    static string ReadTrace(string path)
    {
        try { return string.Join(',', File.ReadAllLines(path)); }
        catch (IOException) { return ""; }
    }

    static async Task Until(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed.TotalSeconds > 10) throw new Exception("Tempo limite esperando o player.");
            await Task.Delay(20);
        }
    }

    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    sealed class Tap : IDisposable { public void Dispose() { } }
}
