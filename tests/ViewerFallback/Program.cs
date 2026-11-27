using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Streemu.Media;
using Streemu.Telemetria;
using Streemu.Util;

static class Program
{
    const string SurfaceError = "Failed to create window: Installed Vulkan doesn't implement the VK_KHR_surface extension";
    // o que o ffplay de verdade escreve quando acha o vídeo e quando o laço de exibição roda (o player na CPU daqui confirma o vídeo com isso)
    const string VideoLine = "[info]   Stream #0:0[0x100]: Video: h264 (High) ([27][0][0][0] / 0x001B), yuv420p(progressive), 1280x720 [SAR 1:1 DAR 16:9], 60 fps";
    const string StatusLine = "[info]    1.23 A-V:  0.012 fd=   0 aq=   10KB vq=  100KB sq=    0B";
    static readonly List<(string Tipo, JsonObject Dados)> Midia = new();

    static async Task<int> Main(string[] args)
    {
        // Uma cópia do apphost se passa pelo ffplay. Nenhum teste abre janela, rede ou dispositivo de áudio.
        string executable = Path.GetFileNameWithoutExtension(Environment.ProcessPath)!;
        if (executable == "mpv") return await FakeDirect3D(args);
        if (executable == "ffplay") return await FakePlayer(args);
        // Os prazos de verdade (5 s na abertura direta, 12 s na recuperação, 30 s para o Vulkan valer como funcionou, 300 ms para o aviso "Ajustando o
        // player..." e 300 ms de espera da linha get_buffer() depois da falta de memória) ficam conferidos aqui e trocados por valores curtos: o teste não
        // espera 12 s. A recuperação tem que ser maior que a abertura, senão o caso d3d11-install-late não prova nada. A segunda tentativa do Direct3D 11
        // depois do reset do driver (1,5 s de espera, até 12 s depois do reset) também é conferida e trocada: o teste espera 200 ms.
        if (Viewer.Direct3DPreparationTimeout != TimeSpan.FromSeconds(5) || Viewer.Direct3DRecoveryPreparationTimeout != TimeSpan.FromSeconds(12)
            || Viewer.VulkanWorkedAfter != TimeSpan.FromSeconds(30) || Viewer.Direct3DPreparingNoticeAfter != TimeSpan.FromMilliseconds(300)
            || Viewer.MemoryFailureKillDelay != TimeSpan.FromMilliseconds(300)
            || Viewer.Direct3DRetryDelay != TimeSpan.FromMilliseconds(1500) || Viewer.Direct3DRetryWindow != TimeSpan.FromSeconds(12)
            || Viewer.DropGrace != TimeSpan.FromSeconds(3) || Viewer.StatusLogEvery != TimeSpan.FromSeconds(10))
        { Console.WriteLine("FALHA player-prazos: os prazos do Viewer mudaram (5 s, 12 s, 30 s, 300 ms, 300 ms, 1,5 s, 12 s, 3 s e 10 s)."); return 1; }
        Viewer.Direct3DPreparationTimeout = TimeSpan.FromMilliseconds(300);
        Viewer.Direct3DRecoveryPreparationTimeout = TimeSpan.FromMilliseconds(1500);
        Viewer.VulkanWorkedAfter = TimeSpan.FromMilliseconds(400);
        Viewer.Direct3DPreparingNoticeAfter = TimeSpan.FromMilliseconds(100);
        Viewer.Direct3DRetryDelay = TimeSpan.FromMilliseconds(200);
        Viewer.DropGrace = TimeSpan.FromMilliseconds(400);
        File.Copy(Environment.ProcessPath!, Path.Combine(AppContext.BaseDirectory, "ffplay.exe"), true);
        File.Copy(Environment.ProcessPath!, Path.Combine(AppContext.BaseDirectory, "mpv.exe"), true);
        var results = new List<string>();
        int failed = 0;
        CaminhoMidia.Ligar((tipo, dados) => { lock (Midia) Midia.Add((tipo, dados)); });
        foreach (var name in new[] { "surface-zero", "surface-late", "vram-loop", "d3d11-after-vram", "d3d11-fails", "d3d11-cooldown", "d3d11-not-memory", "d3d11-resize", "d3d11-surface", "d3d11-device-lost", "d3d11-device-lost-retry", "d3d11-device-lost-twice", "d3d11-install", "d3d11-install-late", "d3d11-install-timeout", "d3d11-install-stop", "d3d11-queda-srt", "d3d11-queda-conexao", "d3d11-fim-sem-srt", "d3d11-fecha-janela", "d3d11-live-acabou", "mpv-linhas", "exit-error", "normal-close", "cpu-error", "retry-cpu-error", "network-close", "stop-early", "stop-retry", "resize", "preview", "preview-vram", "vram-get-buffer", "vram-slab", "vulkan-worked", "vulkan-no-hwaccel", "vulkan-video-then-vram" })
        {
            if (Environment.GetEnvironmentVariable("VIEWER_CASE_FILTER") is { } filter && name != filter) continue;
            try { CaminhoMidia.Descarregar(TimeSpan.FromSeconds(15)); lock (Midia) Midia.Clear(); await Check(name); ConferirMidia(name); results.Add($"OK    player-{name}"); }
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
        if (!hardware && name is not ("cpu-error" or "retry-cpu-error"))
        {
            Console.Error.WriteLine(VideoLine);
            Console.Error.WriteLine(StatusLine);
            await Task.Delay(20000);
            return 0;
        }
        if (name == "vulkan-worked")
        {
            // o Vulkan que funciona: confirma o vídeo e segue rodando
            Console.Error.WriteLine(VideoLine);
            Console.Error.WriteLine(StatusLine);
            await Task.Delay(20000);
            return 0;
        }
        if (name == "vulkan-no-hwaccel")
        {
            // o ffplay avisa que o hwaccel não iniciou e segue sozinho decodificando na CPU: o vídeo roda, mas não é o Vulkan que funcionou
            Console.Error.WriteLine("[warning] Failed setup for format vulkan: hwaccel initialisation returned error.");
            Console.Error.WriteLine(VideoLine);
            Console.Error.WriteLine(StatusLine);
            await Task.Delay(20000);
            return 0;
        }
        if (name == "vram-get-buffer")
        {
            // a assinatura das RDNA3: a falta de memória e, logo depois, o decodificador sem conseguir quadro (a VRAM está livre)
            Console.Error.WriteLine("[error] VK_ERROR_OUT_OF_DEVICE_MEMORY");
            Console.Error.WriteLine("[h264 @ 0000020a1b2c3d40] get_buffer() failed");
            await Task.Delay(20000);
            return 0;
        }
        if (name == "vram-slab")
        {
            // VRAM cheia de verdade: o renderizador fica sem memória, sem o get_buffer do decodificador
            Console.Error.WriteLine("[error] Failed creating LUT texture");
            Console.Error.WriteLine("[error] No slab to serve request for 1048576 bytes in pool 1");
            await Task.Delay(20000);
            return 0;
        }
        if (name == "vulkan-video-then-vram")
        {
            // o que as placas AMD RDNA3 faziam: o primeiro quadro aparece e logo depois falta memória
            Console.Error.WriteLine(VideoLine);
            Console.Error.WriteLine(StatusLine);
            Console.Error.WriteLine("[error] VK_ERROR_OUT_OF_DEVICE_MEMORY");
            await Task.Delay(20000);
            return 0;
        }
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
        if (name == "d3d11-surface") { Console.Error.WriteLine(SurfaceError); return 0; }
        if (name.StartsWith("d3d11-device-lost"))
        {
            // reset do driver (LIV-140, 2026-11-23), as linhas como o ffplay escreveu: a primeira não tem "Vulkan" nem "failed"
            Console.Error.WriteLine("[h264 @ 0000015695873e40] [vk @ 0000015695822080] Unable to submit command buffer: VK_ERROR_DEVICE_LOST");
            Console.Error.WriteLine("[h264 @ 0000015695873e40] hardware accelerator failed to decode picture");
            Console.Error.WriteLine("[Vulkan Renderer @ 000001569bccd000] Failed acquiring swapchain image: VK_ERROR_DEVICE_LOST");
            await Task.Delay(20000);
            return 0;
        }
        Console.Error.WriteLine(name is "vram-loop" or "preview-vram" || name.StartsWith("d3d11-") ? "[error] VK_ERROR_OUT_OF_DEVICE_MEMORY" : SurfaceError);
        if (name is "vram-loop" or "preview-vram" || name.StartsWith("d3d11-")) await Task.Delay(20000);
        return 0;
    }

    static async Task<int> FakeDirect3D(string[] args)
    {
        // o descarte na exibição (LIV-178) fica explícito, depois do perfil de baixa latência, e nunca no decodificador ("decoder" estraga o H.264 até o próximo quadro-chave)
        if (!args.Contains("--hwdec=d3d11va") || !args.Contains("--gpu-context=d3d11") || !args.Contains("--framedrop=vo")
            || args.Any(a => a.StartsWith("--framedrop=", StringComparison.Ordinal) && a != "--framedrop=vo")
            || Array.IndexOf(args, "--framedrop=vo") < Array.IndexOf(args, "--profile=low-latency"))
            return 2;
        File.AppendAllText(Environment.GetEnvironmentVariable("VIEWER_FAKE_TRACE")!, "d3d11\n");
        string name = Environment.GetEnvironmentVariable("VIEWER_FAKE_CASE")!;
        if (QuedaD3D(name))
        {
            // o que o mpv de verdade escreve (conferido com o mpv do app, 2026-11-27): a linha de estado de 5 campos, "nan" no avsync enquanto não há áudio e
            // "Exiting... (Quit)" ao fechar a janela ou "Exiting... (End of file)" quando a conexão acaba; o SRT do ffmpeg reclama no stderr.
            // Só o d3d11-queda-srt chega com o player atrasado (áudio menos vídeo de 2,6 s); nos outros o atraso é pequeno
            Console.WriteLine("STREEMU_D3D11 d3d11va 3440 1370 2.5109");
            Console.WriteLine("STREEMU_MPV_STATUS nan 0 0 0");
            Console.WriteLine($"STREEMU_MPV_STATUS {(name == "d3d11-queda-srt" ? "2.600" : "-0.250")} 3 1 120.000000");
            if (name is "d3d11-queda-srt" or "d3d11-queda-conexao")
            {
                Console.Error.WriteLine("[ffmpeg] srt: No room to store incoming packet: offset=1234 avail=0");
                Console.Error.WriteLine("[ffmpeg] srt: SEQUENCE DISCREPANCY. BREAKING CONNECTION");
            }
            await Task.Delay(300);
            Console.WriteLine(name == "d3d11-fecha-janela" ? "Exiting... (Quit)" : "Exiting... (End of file)");
            return 0;
        }
        // d3d11-device-lost-retry: o driver ainda voltando na 1ª abertura do mpv, já de volta na 2ª; d3d11-device-lost-twice: não volta
        int abertura = File.ReadAllLines(Environment.GetEnvironmentVariable("VIEWER_FAKE_TRACE")!).Count(l => l == "d3d11");
        bool semHardware = name is "d3d11-fails" or "d3d11-device-lost-twice" || name == "d3d11-device-lost-retry" && abertura == 1;
        Console.WriteLine(semHardware ? "STREEMU_D3D11 no 1280 720 1.7778" : "STREEMU_D3D11 d3d11va 1280 720 1.7778");
        Console.WriteLine("STREEMU_MPV_STATUS -0.012 9 2");
        await Task.Delay(20000);
        return 0;
    }

    /// <summary>Os casos em que o mpv simulado mostra vídeo de 3440x1370 a 120 FPS e depois sai, de jeitos diferentes.</summary>
    static bool QuedaD3D(string name) => name is "d3d11-queda-srt" or "d3d11-queda-conexao" or "d3d11-fim-sem-srt" or "d3d11-fecha-janela" or "d3d11-live-acabou";

    static async Task Check(string name)
    {
        if (name == "mpv-linhas") { ConferirLinhasMpv(); return; }
        string trace = Path.GetTempFileName();
        Environment.SetEnvironmentVariable("VIEWER_FAKE_CASE", name);
        Environment.SetEnvironmentVariable("VIEWER_FAKE_TRACE", trace);
        Log.Lines.Clear();
        bool direct3D = name.StartsWith("d3d11-");
        string mpv = Path.Combine(AppContext.BaseDirectory, "mpv.exe");
        var installation = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int preparations = 0;
        var viewer = new Viewer
        {
            UseHardware = name != "cpu-error",
            AvoidVulkan = name == "d3d11-cooldown" || QuedaD3D(name),
            PrepareDirect3D = direct3D ? () =>
            {
                Interlocked.Increment(ref preparations);
                return name.StartsWith("d3d11-install") ? installation.Task : Task.FromResult<string?>(mpv);
            } : null,
        };
        int vulkanFailures = 0, rejected = 0, worked = 0;
        var motivos = new List<MotivoMidia>();
        var decodificacoes = new List<bool>();
        var preparando = new List<bool>();
        viewer.VulkanFailed += (motivo, decodificacao) => { lock (motivos) { motivos.Add(motivo); decodificacoes.Add(decodificacao); } Interlocked.Increment(ref vulkanFailures); };
        viewer.PreparingDirect3D += ligado => { lock (preparando) preparando.Add(ligado); };
        viewer.VulkanWorked += () => Interlocked.Increment(ref worked);
        viewer.Direct3DRejected += () => Interlocked.Increment(ref rejected);
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
            if (name.StartsWith("preview")) viewer.StartLocal(_ => new Tap(), "teste");
            else viewer.Start(12345, "somente-teste", 80, "teste");
            if (direct3D)
            {
                if (name == "d3d11-not-memory")
                {
                    await Until(() => Volatile.Read(ref exits) == 1);
                    Require(vulkanFailures == 0 && preparations == 0 && restarts == 0 && ReadTrace(trace) == "gpu",
                        "Erro genérico disparou a recuperação do Vulkan.");
                    return;
                }
                if (QuedaD3D(name))
                {
                    await Until(() => viewer.VideoSize != null);
                    if (name == "d3d11-live-acabou")
                    {
                        // o transmissor encerra a live: o SRT fecha e o mpv sai antes da mensagem "parou de transmitir"; o Stop() dela, dentro do prazo, não é queda
                        await Until(() => !viewer.Running);
                        viewer.Stop();
                    }
                    await Until(() => Volatile.Read(ref exits) == 1);
                    Require(restarts == 0 && vulkanFailures == 0 && ReadTrace(trace) == "d3d11", "A saída do mpv disparou recuperação ou reabertura.");
                    if (name is "d3d11-fecha-janela" or "d3d11-live-acabou")
                    {
                        Require(viewer.Drop == null, $"{name}: contou como queda uma saída que ninguém precisa avisar.");
                        return;
                    }
                    // só o SRT estourado COM o player atrasado acusa o player; o resto (SRT sem atraso, ou fim sem sinal do SRT) é a conexão que caiu
                    bool atrasado = name == "d3d11-queda-srt";
                    var queda = viewer.Drop;
                    Require(queda is { Fps: 120 } && queda.PlayerBehind == atrasado && queda.WatchedSeconds >= 0, $"{name}: a queda não chegou classificada (player atrasado: {atrasado}).");
                    var aviso = queda!.Notice();
                    Require(aviso.Contains("Entre de novo"), "O aviso não diz a próxima ação.");
                    Require(atrasado ? aviso.Contains("60 FPS") && aviso.Contains("player")
                        : aviso.Contains("A conexão caiu") && aviso.Contains("internet de quem transmite") && !aviso.Contains("player") && !aviso.Contains("FPS"),
                        $"{name}: o aviso culpa quem não devia ou não dá a dica certa: {aviso}");
                    // o log passa a ter a sincronia do mpv (antes a linha só ia para a amostra da telemetria), uma vez, com o primeiro avsync medido e os FPS
                    var statusNoLog = Log.Lines.Where(l => l.StartsWith("Player (mpv): áudio menos vídeo")).ToList();
                    string avEsperado = atrasado ? "2600 ms" : "-250 ms";
                    Require(statusNoLog.Count == 1 && statusNoLog[0].Contains(avEsperado) && statusNoLog[0].Contains("120 FPS"), $"Status do mpv no log: {string.Join(" | ", statusNoLog)}");
                    return;
                }
                if (name.StartsWith("d3d11-install"))
                {
                    await Until(() => Volatile.Read(ref preparations) == 1);
                    Require(ReadTrace(trace) == "gpu" && restarts == 0, "Reabriu antes de o mpv estar pronto.");
                    if (name == "d3d11-install-stop") viewer.Stop();
                    // o mpv ainda baixava e termina depois do prazo da abertura direta e antes do da recuperação: a live NÃO pode cair na CPU
                    if (name == "d3d11-install-late")
                    {
                        await Task.Delay(700);
                        Require(viewer.IsPreparingDirect3D, "A espera longa pelo mpv não avisou o palco (\"Ajustando o player...\").");
                    }
                    if (name == "d3d11-install-timeout")
                        await Until(() => ReadTrace(trace) == "gpu,cpu");
                    installation.SetResult(mpv);
                    if (name is "d3d11-install-stop" or "d3d11-install-timeout")
                    {
                        await Task.Delay(1400);
                        bool stopped = name == "d3d11-install-stop";
                        Require(ReadTrace(trace) == (stopped ? "gpu" : "gpu,cpu") && restarts == (stopped ? 0 : 1),
                            "O mpv instalado atrasado reabriu uma live encerrada ou já recuperada.");
                        return;
                    }
                }
                bool naCpu = name is "d3d11-fails" or "d3d11-device-lost-twice";
                string expected = name == "d3d11-cooldown" ? "d3d11" : name == "d3d11-fails" ? "gpu,d3d11,cpu"
                    : name == "d3d11-device-lost-retry" ? "gpu,d3d11,d3d11" : name == "d3d11-device-lost-twice" ? "gpu,d3d11,d3d11,cpu" : "gpu,d3d11";
                await Until(() => ReadTrace(trace) == expected && viewer.Running && viewer.Decoder == (naCpu ? "cpu" : "d3d11va"));
                Require(vulkanFailures == (name == "d3d11-cooldown" ? 0 : 1) && worked == 0, "Registro da falha do Vulkan incorreto.");
                if (name == "d3d11-install-late")
                {
                    // o aviso liga com a espera longa e desliga quando a espera acaba; mpv já pronto (os outros casos) não pisca texto nenhum
                    await Until(() => { lock (preparando) return preparando.Count == 2; });
                    Require(preparando[0] && !preparando[1] && !viewer.IsPreparingDirect3D, "O aviso de preparo do Direct3D 11 não ligou e desligou uma vez.");
                }
                else if (!name.StartsWith("d3d11-install"))
                    Require(preparando.Count == 0 && !viewer.IsPreparingDirect3D, "O aviso de preparo apareceu com o mpv já pronto.");
                if (name.StartsWith("d3d11-device-lost"))
                    Require(motivos[0] == MotivoMidia.DispositivoRemovido && viewer.ErrorCounts()["placa_reiniciou"]?.GetValue<int>() >= 1,
                        $"O reset do driver não chegou como DispositivoRemovido ({string.Join(",", motivos)}).");
                // a 1ª falha do Direct3D 11 logo depois do reset não bloqueia o Direct3D 11 (nem cai na CPU); só a 2ª, que segue o caminho de sempre
                Require(rejected == (name is "d3d11-fails" or "d3d11-device-lost-twice" ? 1 : 0), "Registro da rejeição do Direct3D 11 incorreto.");
                Require(restarts == expected.Count(c => c == ',') && exits == 0, "Avisou o transmissor em uma hora errada.");
                if (!naCpu)
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
            if (name == "vulkan-worked")
            {
                await Until(() => Volatile.Read(ref worked) == 1);
                Require(viewer.Running && viewer.UsingHardware && vulkanFailures == 0 && restarts == 0 && ReadTrace(trace) == "gpu",
                    "O Vulkan que funcionou foi tratado como falha ou reaberto.");
                await Task.Delay(900);
                Require(Volatile.Read(ref worked) == 1, "VulkanWorked disparou mais de uma vez na mesma abertura.");
                return;
            }
            if (name == "vulkan-no-hwaccel")
            {
                // o vídeo roda além do VulkanWorkedAfter, mas o hwaccel não iniciou: nunca vale como "funcionou"
                await Until(() => viewer.VideoSize != null);
                await Task.Delay(900);
                Require(Volatile.Read(ref worked) == 0, "VulkanWorked disparou com o hwaccel que não iniciou.");
                Require(viewer.Running && vulkanFailures == 0 && restarts == 0 && ReadTrace(trace) == "gpu", "O ffplay que caiu sozinho para a CPU foi derrubado ou reaberto.");
                return;
            }
            if (name == "vulkan-video-then-vram")
            {
                // vídeo confirmado e falha logo depois: nunca vale como "funcionou", nem passado o tempo do VulkanWorkedAfter
                await Until(() => ReadTrace(trace) == "gpu,cpu");
                await Task.Delay(900);
                Require(Volatile.Read(ref worked) == 0, "VulkanWorked disparou numa abertura que falhou depois do primeiro quadro.");
                Require(vulkanFailures == 1 && motivos[0] == MotivoMidia.SemMemoria && viewer.Running && !viewer.UsingHardware, "A falha depois do primeiro quadro não foi tratada.");
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
            // a falha do Vulkan chega com o motivo (também na prévia da própria live, que cai na CPU sem o Direct3D 11)
            if (name is "vram-loop" or "preview-vram" or "vram-get-buffer" or "vram-slab" or "preview" or "surface-zero")
            {
                var esperado = name is "vram-loop" or "preview-vram" or "vram-get-buffer" or "vram-slab" ? MotivoMidia.SemMemoria : MotivoMidia.JanelaNaoAbriu;
                Require(vulkanFailures == 1 && motivos[0] == esperado, $"VulkanFailed com o motivo errado: {string.Join(",", motivos)} (esperava {esperado}).");
                // só a falta de memória seguida de get_buffer() failed é a assinatura da decodificação (RDNA3); o OOM do renderizador (VRAM cheia) não é
                Require(decodificacoes[0] == (name == "vram-get-buffer"), $"VulkanFailed com a assinatura da decodificação errada ({decodificacoes[0]}) no caso {name}.");
            }
            Require(worked == 0, "VulkanWorked disparou numa abertura que falhou.");
        }
        finally
        {
            await EsperarMidia(name);
            viewer.Stop();
            await Until(() => !viewer.Running);
            // Aguarda a conclusão do observador antes de descartar o sinal e iniciar o próximo caso.
            if (name is not ("stop-retry" or "d3d11-install-stop")) exited.Wait(3000);
            File.Delete(trace);
        }
    }

    /// <summary>
    /// Os eventos do caminho de mídia (Telemetria/CaminhoMidia.cs) de cada caso: a tentativa que falhou, a falha com etapa e motivo da lista fechada,
    /// a tentativa da recuperação e o tempo até voltar, ligados pelo id. Sem texto livre: todo texto é código, versão ou id.
    /// </summary>
    static void ConferirMidia(string name)
    {
        Require(CaminhoMidia.Descarregar(TimeSpan.FromSeconds(15)), $"A fila dos eventos de mídia não esvaziou ({CaminhoMidia.Pendentes} pendentes).");
        List<(string Tipo, JsonObject Dados)> ev;
        lock (Midia) ev = Midia.ToList();
        foreach (var (tipo, d) in ev)
        {
            Require(CaminhoMidia.Tipos.Contains(tipo), $"Tipo de evento inesperado: {tipo}.");
            Require(d["lado"]?.GetValue<string>() == CaminhoMidia.Viewer, $"{tipo} sem lado viewer.");
            Require(Regex.IsMatch(d["tentativa"]?.GetValue<string>() ?? "", "^[0-9a-f]{12}$"), $"{tipo} sem id de tentativa.");
            foreach (var (k, v) in d)
                if (v is JsonValue jv && jv.TryGetValue<string>(out var texto))
                    Require(Regex.IsMatch(texto, @"^[a-z0-9_]{1,32}$") || k == "resolucao" && Regex.IsMatch(texto, @"^\d+x\d+$"),
                        $"{tipo}.{k} não é código fechado: {texto}.");
        }
        string Resumo((string Tipo, JsonObject Dados) e) => e.Tipo switch
        {
            CaminhoMidia.TipoTentativa => $"tentativa:{e.Dados["decodificador"]}/{e.Dados["renderizador"]}/{e.Dados["gatilho"]}",
            CaminhoMidia.TipoFalha => $"falha:{e.Dados["etapa"]}/{e.Dados["motivo"]}",
            CaminhoMidia.TipoVideoOk => "video",
            _ => "recuperada",
        };
        string got = string.Join(" ", ev.Select(Resumo));
        string? want = Esperado(name);
        if (want != null) Require(got == want, $"Eventos de mídia: esperava \"{want}\", veio \"{got}\".");
        // a mesma tentativa liga a falha à tentativa que falhou, e a volta à que falhou
        var falha = ev.FirstOrDefault(e => e.Tipo == CaminhoMidia.TipoFalha);
        var volta = ev.FirstOrDefault(e => e.Tipo == CaminhoMidia.TipoRecuperada);
        if (falha.Dados != null)
            Require(falha.Dados["tentativa"]?.GetValue<string>() == ev[0].Dados["tentativa"]?.GetValue<string>(), "A falha não aponta para a tentativa que falhou.");
        if (name is "d3d11-queda-srt" or "d3d11-queda-conexao" or "d3d11-fim-sem-srt")
        {
            // o que o painel lê da queda: só números e códigos (a regra do laço acima proíbe texto livre), e a ligação com a sincronia do mpv e a live
            var q = falha.Dados!;
            bool comSrt = name != "d3d11-fim-sem-srt";
            Require(q["fatal"]?.GetValue<bool>() == true && q["teste"]?.GetValue<bool>() == false, "A queda do player é fatal para esta live e não é teste.");
            Require(q["srt_sem_espaco"]?.GetValue<bool>() == comSrt && q["srt_sequencia"]?.GetValue<bool>() == comSrt, "O SRT estourado não chegou como visto (ou chegou sem ter sido).");
            Require(q["assistiu_s"]?.GetValue<int>() is >= 0 and < 10 && q["av_ms"]?.GetValue<int>() == (name == "d3d11-queda-srt" ? 2600 : -250) && q["fps"]?.GetValue<int>() == 120,
                "Faltou o tempo assistido, o avsync ou os FPS.");
            Require(q["descartes_exibicao"]?.GetValue<int>() == 3 && q["descartes_decodificador"]?.GetValue<int>() == 1 && q["descartes_recentes"]?.GetValue<int>() == 4, "Faltaram os descartes (a primeira amostra com 0 serve de base, a seguinte soma 3 + 1).");
            Require(q["resolucao"]?.GetValue<string>() == "3440x1370", "Faltou a resolução da live.");
        }
        if (volta.Dados != null)
        {
            Require(volta.Dados["tentativa_falha"]?.GetValue<string>() == falha.Dados?["tentativa"]?.GetValue<string>(), "A recuperação não aponta para a falha.");
            Require(volta.Dados["ms"]?.GetValue<long>() is >= 0 and < 20000, "Tempo até voltar fora do esperado.");
            // quem volta é a última tentativa aberta antes da volta (depois dela pode vir a reabertura da janela, como no d3d11-resize)
            var antes = ev.Take(ev.FindIndex(e => e.Tipo == CaminhoMidia.TipoRecuperada)).Where(e => e.Tipo == CaminhoMidia.TipoTentativa).ToList();
            Require(volta.Dados["tentativa"]?.GetValue<string>() == antes[^1].Dados["tentativa"]?.GetValue<string>()
                && antes[^1].Dados["numero"]?.GetValue<int>() == antes.Count, "Numeração ou id da tentativa que voltou errados.");
        }
    }

    static string? Esperado(string name) => name switch
    {
        "surface-zero" => "tentativa:vulkan/vulkan/inicio falha:renderizacao/janela_nao_abriu tentativa:cpu/sdl/recuperacao video recuperada",
        "vram-loop" => "tentativa:vulkan/vulkan/inicio falha:renderizacao/sem_memoria tentativa:cpu/sdl/recuperacao video recuperada",
        "exit-error" => "tentativa:vulkan/vulkan/inicio falha:renderizacao/saiu_cedo tentativa:cpu/sdl/recuperacao video recuperada",
        "d3d11-after-vram" => "tentativa:vulkan/vulkan/inicio falha:renderizacao/sem_memoria tentativa:d3d11va/d3d11/recuperacao video recuperada",
        "d3d11-device-lost" => "tentativa:vulkan/vulkan/inicio falha:renderizacao/dispositivo_removido tentativa:d3d11va/d3d11/recuperacao video recuperada",
        "d3d11-device-lost-retry" => "tentativa:vulkan/vulkan/inicio falha:renderizacao/dispositivo_removido tentativa:d3d11va/d3d11/recuperacao falha:decodificacao/d3d11_sem_hardware tentativa:d3d11va/d3d11/recuperacao video recuperada",
        "d3d11-device-lost-twice" => "tentativa:vulkan/vulkan/inicio falha:renderizacao/dispositivo_removido tentativa:d3d11va/d3d11/recuperacao falha:decodificacao/d3d11_sem_hardware tentativa:d3d11va/d3d11/recuperacao falha:decodificacao/d3d11_sem_hardware tentativa:cpu/sdl/recuperacao video recuperada",
        "d3d11-fails" => "tentativa:vulkan/vulkan/inicio falha:renderizacao/sem_memoria tentativa:d3d11va/d3d11/recuperacao falha:decodificacao/d3d11_sem_hardware tentativa:cpu/sdl/recuperacao video recuperada",
        "d3d11-cooldown" => "tentativa:d3d11va/d3d11/inicio video",
        "d3d11-queda-srt" => "tentativa:d3d11va/d3d11/inicio video falha:recepcao/srt_estourou",
        "d3d11-queda-conexao" or "d3d11-fim-sem-srt" => "tentativa:d3d11va/d3d11/inicio video falha:recepcao/conexao_caiu",
        "d3d11-fecha-janela" or "d3d11-live-acabou" => "tentativa:d3d11va/d3d11/inicio video",
        "normal-close" => "tentativa:vulkan/vulkan/inicio",
        "network-close" => "tentativa:vulkan/vulkan/inicio",
        _ => null,
    };

    /// <summary>O player na CPU confirma o vídeo um instante depois de abrir: o caso espera os eventos esperados antes do Stop (que encerra a live).</summary>
    static async Task EsperarMidia(string name)
    {
        if (Esperado(name) is not { } want) return;
        int n = want.Split(' ').Length;
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed.TotalSeconds < 5)
        {
            lock (Midia) if (Midia.Count >= n && CaminhoMidia.Pendentes == 0) return;
            await Task.Delay(20);
        }
    }

    /// <summary>As linhas do mpv (stdout e stderr) e o aviso, sem abrir processo.</summary>
    static void ConferirLinhasMpv()
    {
        Require(Viewer.ParseMpvExitReason("Exiting... (End of file)") == "End of file" && Viewer.ParseMpvExitReason("Exiting... (Quit)\r") == "Quit"
            && Viewer.ParseMpvExitReason("Exiting... ()") == null && Viewer.ParseMpvExitReason("Playing: x") == null, "Motivo da saída do mpv mal lido.");
        // a linha de 5 campos de agora, a de 4 de antes (sem os FPS) e a que ainda não tem áudio
        var cheia = Viewer.ParseMpvStatus("STREEMU_MPV_STATUS -0.250 3 1 119.880000");
        Require(cheia is { AudioVideoMs: -250, RenderDrops: 3, DecoderDrops: 1, Fps: 120 }, "Linha de estado de 5 campos mal lida.");
        Require(Viewer.ParseMpvStatus("STREEMU_MPV_STATUS -0.012 9 2") is { AudioVideoMs: -12, RenderDrops: 9, DecoderDrops: 2, Fps: null }, "Linha de estado de 4 campos mal lida.");
        Require(Viewer.ParseMpvStatus("STREEMU_MPV_STATUS nan 0 0 0") is { AudioVideoMs: null, Fps: null }, "O avsync ainda sem medida não pode virar zero.");
        Require(Viewer.ParseMpvStatus("STREEMU_MPV_STATUS  0 0") == null && Viewer.ParseMpvStatus("VO: [gpu-next] 1280x720") == null
            && Viewer.ParseMpvStatus("STREEMU_MPV_STATUS 0.1 a b 60") == null, "Aceitou uma linha que não é de estado.");
        Require(Viewer.StatusLogText(cheia!).Contains("-250 ms") && Viewer.StatusLogText(cheia!).Contains("120 FPS"), "Texto do log sem o avsync ou os FPS.");
        // a sincronia vai para o log uma vez a cada 10 s, não a cada linha
        long ultimo = 0;
        var cada = TimeSpan.FromSeconds(10);
        Require(Viewer.StatusLogDue(ref ultimo, 5_000, cada) && !Viewer.StatusLogDue(ref ultimo, 6_000, cada) && !Viewer.StatusLogDue(ref ultimo, 14_999, cada)
            && Viewer.StatusLogDue(ref ultimo, 15_000, cada) && !Viewer.StatusLogDue(ref ultimo, 15_001, cada), "A sincronia do mpv não sai a cada 10 s.");
        // o aviso: o motivo provável e a próxima ação; a dica dos 60 FPS só quando a live passa disso
        var srt120 = new Viewer.PlayerDrop(true, 120, 112).Notice();
        Require(srt120.Contains("Entre de novo") && srt120.Contains("60 FPS"), "Aviso do player que não acompanhou sem a próxima ação.");
        Require(!new Viewer.PlayerDrop(true, 60, 5).Notice().Contains("60 FPS") && new Viewer.PlayerDrop(true, null, 5).Notice().Contains("baixar a qualidade"), "Dica de 60 FPS numa live que já é de 60.");
        var conexao = new Viewer.PlayerDrop(false, 120, 5).Notice();
        Require(conexao.Contains("A conexão caiu") && conexao.Contains("Entre de novo") && conexao.Contains("internet de quem transmite") && !conexao.Contains("player") && !conexao.Contains("FPS"),
            "Aviso da conexão que caiu culpa o player ou dá dica de FPS.");
        // quem é "player que não acompanhou": o SRT estourado E (atraso de áudio menos vídeo > 2 s, para qualquer lado, ou descartes subindo); o SRT sozinho é a conexão
        Viewer.Direct3DSample Amostra(int? av, int render = 0, int decoder = 0) => new(av, render, decoder);
        Require(Viewer.PlayerFellBehind(true, Amostra(2600), 0) && Viewer.PlayerFellBehind(true, Amostra(-2600), 0), "SRT estourado com atraso grande não acusou o player.");
        Require(Viewer.PlayerFellBehind(true, Amostra(50, 40), 10) && Viewer.PlayerFellBehind(true, null, 25), "SRT estourado com descartes subindo não acusou o player.");
        Require(!Viewer.PlayerFellBehind(true, Amostra(2000), 9) && !Viewer.PlayerFellBehind(true, Amostra(-250), 0) && !Viewer.PlayerFellBehind(true, null, 0)
            && !Viewer.PlayerFellBehind(true, Amostra(null), 0), "SRT estourado sem atraso nem descartes subindo acusou o player.");
        Require(!Viewer.PlayerFellBehind(false, Amostra(9000), 500), "Atraso sem sinal do SRT acusou o player (é a conexão).");
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
