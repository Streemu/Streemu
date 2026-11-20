using System.Diagnostics;
using Streemu.Media;
using Streemu.Util;

static class Program
{
    const string SurfaceError = "Failed to create window: Installed Vulkan doesn't implement the VK_KHR_surface extension";

    static async Task<int> Main(string[] args)
    {
        // Uma cópia do apphost se passa pelo ffplay. Nenhum teste abre janela, rede ou dispositivo de áudio.
        string executable = Path.GetFileNameWithoutExtension(Environment.ProcessPath)!;
        if (executable == "mpv") return await FakeDirect3D(args);
        if (executable == "ffplay") return await FakePlayer(args);
        File.Copy(Environment.ProcessPath!, Path.Combine(AppContext.BaseDirectory, "ffplay.exe"), true);
        File.Copy(Environment.ProcessPath!, Path.Combine(AppContext.BaseDirectory, "mpv.exe"), true);
        var results = new List<string>();
        int failed = 0;
        foreach (var name in new[] { "surface-zero", "surface-late", "vram-loop", "d3d11-after-vram", "d3d11-fails", "d3d11-cooldown", "d3d11-not-memory", "d3d11-resize", "exit-error", "normal-close", "cpu-error", "retry-cpu-error", "network-close", "stop-early", "stop-retry", "resize", "preview" })
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
        if (name == "d3d11-not-memory")
        {
            Console.Error.WriteLine("Failed creating an unrelated resource");
            return 0;
        }
        Console.Error.WriteLine(name == "vram-loop" || name.StartsWith("d3d11-") ? "[error] VK_ERROR_OUT_OF_DEVICE_MEMORY" : SurfaceError);
        if (name == "vram-loop" || name.StartsWith("d3d11-")) await Task.Delay(20000);
        return 0;
    }

    static async Task<int> FakeDirect3D(string[] args)
    {
        if (!args.Contains("--hwdec=d3d11va") || !args.Contains("--gpu-context=d3d11"))
            return 2;
        File.AppendAllText(Environment.GetEnvironmentVariable("VIEWER_FAKE_TRACE")!, "d3d11\n");
        string name = Environment.GetEnvironmentVariable("VIEWER_FAKE_CASE")!;
        Console.WriteLine(name == "d3d11-fails" ? "STREEMU_D3D11 no 1280 720 1.7778" : "STREEMU_D3D11 d3d11va 1280 720 1.7778");
        Console.WriteLine("STREEMU_MPV_STATUS -0.012 9 2");
        await Task.Delay(20000);
        return 0;
    }

    static async Task Check(string name)
    {
        string trace = Path.GetTempFileName();
        Environment.SetEnvironmentVariable("VIEWER_FAKE_CASE", name);
        Environment.SetEnvironmentVariable("VIEWER_FAKE_TRACE", trace);
        Log.Lines.Clear();
        bool direct3D = name.StartsWith("d3d11-");
        var viewer = new Viewer
        {
            UseHardware = name != "cpu-error",
            AvoidVulkan = name == "d3d11-cooldown",
            Direct3DExecutable = direct3D ? Path.Combine(AppContext.BaseDirectory, "mpv.exe") : null,
        };
        int vramFailures = 0;
        viewer.VulkanMemoryFailed += () => Interlocked.Increment(ref vramFailures);
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
            if (direct3D)
            {
                if (name == "d3d11-not-memory")
                {
                    await Until(() => Volatile.Read(ref exits) == 1);
                    Require(vramFailures == 0 && restarts == 0 && ReadTrace(trace) == "gpu", "Erro genérico disparou recuperação de VRAM.");
                    return;
                }
                string expected = name == "d3d11-cooldown" ? "d3d11" : name == "d3d11-fails" ? "gpu,d3d11,cpu" : "gpu,d3d11";
                await Until(() => ReadTrace(trace) == expected && viewer.Running
                    && viewer.Decoder == (name == "d3d11-fails" ? "cpu" : "d3d11va"));
                Require(vramFailures == (name == "d3d11-cooldown" ? 0 : 1), "Registro de falha de memória incorreto.");
                Require(restarts == expected.Count(c => c == ',') && exits == 0, "Avisou o transmissor em uma hora errada.");
                if (name != "d3d11-fails")
                {
                    Require(viewer.VideoSize == new Size(1280, 720) && viewer.UsingHardware, "Não confirmou o decoder ou a resolução.");
                    await Until(() => viewer.PlaybackSnapshot()["quadros_descartados_exibicao"]?.GetValue<int>() == 9);
                    var stats = viewer.PlaybackSnapshot();
                    Require(stats["quadros_descartados_decodificador"]?.GetValue<int>() == 2
                        && stats["audio_menos_video_ms"]?.GetValue<int>() == -12, "Misturou os contadores ou perdeu o valor de sincronia.");
                }
                if (name == "d3d11-cooldown")
                {
                    Require(viewer.SampleCpuPerCore() == null, "A primeira leitura precisa de uma linha de base.");
                    await Task.Delay(40);
                    Require(viewer.SampleCpuPerCore() is >= 0, "O player aberto não produziu medida de CPU.");
                }
                if (name == "d3d11-resize")
                {
                    viewer.Reopen(new Rectangle(100, 100, 480, 270));
                    await Until(() => ReadTrace(trace) == "gpu,d3d11,d3d11" && viewer.Decoder == "d3d11va" && !viewer.Reopening);
                    Require(exits == 0 && restarts == 2, "Redimensionar desligou o player ou trocou de decoder.");
                }
                return;
            }
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
