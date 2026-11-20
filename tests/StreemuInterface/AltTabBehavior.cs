using System.Diagnostics;
using System.Runtime.InteropServices;
using Streemu;
using Streemu.Media;
using Streemu.Ui;
using Streemu.Ui.Multistream;

/// <summary>
/// A barra de controles das lives depois de Alt+Tab (relato de 2026-11-19: quem assistia deu Alt+Tab e, ao voltar, a barra tinha sumido). Diferente dos
/// outros modos, ABRE JANELAS NA TELA e mexe no mouse e no teclado (~1 min): a janela real com 1 e 3 players de verdade (ffplay lendo uma imagem de teste
/// do ffmpeg, sem som e sem rede), Alt+Tab de verdade (SendInput), minimizar e tela cheia. Em cada passo o mouse passa sobre cada vídeo e a barra precisa
/// aparecer e ser a janela que está sob o mouse (não o vídeo por cima dela). Só rode com quem usa o PC avisado.
/// </summary>
static class AltTabBehavior
{
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    const byte VkMenu = 0x12, VkTab = 0x09, VkWin = 0x5B, VkD = 0x44;
    const uint KeyUp = 2, LeftDown = 2, LeftUp = 4;

    sealed record Live(Viewer Viewer, PlayerOverlay Bar, Process Source);

    /// <summary>Player na placa de vídeo (Vulkan), como o app abre por padrão; sem "placa", na CPU.</summary>
    static bool _hardware;

    public static void Run(string? mode)
    {
        _hardware = mode == "placa";
        using var form = new MainForm(true) { StartPosition = FormStartPosition.Manual, Location = new Point(80, 60), Size = new Size(1400, 860) };
        var problems = new List<string>();
        Exception? failure = null;
        form.Shown += async (_, _) =>
        {
            try { await RunAllAsync(form, problems); }
            catch (Exception ex) { failure = ex; }
            finally { form.Close(); }
        };
        Application.Run(form);
        if (failure != null) throw failure;
        // o app inteiro atrás de outra janela quer dizer que o teste não conseguiu trazê-lo à frente (Win+D simulado): não prova nada sobre a barra
        foreach (var note in problems.Where(p => p.Contains(Behind))) Console.WriteLine($"AVISO (o teste não concluiu este passo): {note}");
        problems.RemoveAll(p => p.Contains(Behind));
        if (problems.Count > 0) throw new Exception($"{problems.Count} problema(s) com a barra das lives:\n" + string.Join("\n", problems));
        Console.WriteLine($"OK ({(_hardware ? "player na placa" : "player na CPU")}): com 1 e 3 players de verdade, a barra de controles aparece por cima de cada vídeo depois de Alt+Tab (com e sem clique no vídeo), minimizar e restaurar, em janela e em tela cheia.");
    }

    static async Task RunAllAsync(MainForm form, List<string> problems)
    {
        foreach (int count in new[] { 3, 1 })
        {
            using var stage = new PlayerStage(form);
            UiProbe.Set(form, "_playerStage", stage);
            var lives = Enumerable.Range(0, count).Select(n => Open(stage, n)).ToList();
            form.StreamsChanged(count);
            UiProbe.Call(form, "ShowView", true);
            try
            {
                await WaitForVideosAsync(lives);
                foreach (bool full in new[] { false, true })
                {
                    if (full != stage.Fullscreen) stage.ToggleFullscreen();
                    await UiProbe.SettleAsync(12);
                    await StepsAsync(form, stage, lives, $"{count} live(s){(full ? ", tela cheia" : "")}", problems);
                }
                if (stage.Fullscreen) stage.ToggleFullscreen();
            }
            finally
            {
                foreach (var live in lives) Close(live);
                await UiProbe.SettleAsync();
                UiProbe.Set(form, "_playerStage", null);
                form.StreamsChanged(0);
            }
        }
    }

    static async Task StepsAsync(MainForm form, PlayerStage stage, List<Live> lives, string label, List<string> problems)
    {
        await FocusAsync(form);
        await CheckBarsAsync(stage, lives, $"{label}, antes", problems);
        await AltTabAsync(form, lives, $"{label}, Alt+Tab", problems);
        await CheckBarsAsync(stage, lives, $"{label}, depois do Alt+Tab", problems);
        await ClickAsync(Center(stage.TargetOf(lives[0].Viewer)!.Value)); // o foco vai para o vídeo (outro processo) antes de sair
        await AltTabAsync(form, lives, $"{label}, clique no vídeo e Alt+Tab", problems);
        await CheckBarsAsync(stage, lives, $"{label}, depois de clicar no vídeo e Alt+Tab", problems);
        form.WindowState = FormWindowState.Minimized;
        await UiProbe.SettleAsync(10);
        if (lives.Any(l => l.Bar.Visible && IsWindowVisible(l.Bar.Handle))) problems.Add($"{label}: barra à vista com a janela minimizada");
        form.WindowState = stage.Fullscreen ? FormWindowState.Maximized : FormWindowState.Normal;
        await FocusAsync(form);
        await CheckBarsAsync(stage, lives, $"{label}, depois de minimizar e restaurar", problems);
        if (stage.Fullscreen) await FocusElsewhereAsync(form, stage, lives, label, problems);
        await ShowDesktopTwiceAsync(form);
        await CheckBarsAsync(stage, lives, $"{label}, depois de Win+D duas vezes", problems);
        // a placa de vídeo falhando no Alt+Tab faz o player reabrir na CPU: outro processo e outra janela, com a barra da antiga
        lives[0].Viewer.Reopen(stage.TargetOf(lives[0].Viewer));
        await WaitForVideosAsync(lives);
        await AltTabAsync(form, lives, $"{label}, player reaberto e Alt+Tab", problems);
        await CheckBarsAsync(stage, lives, $"{label}, depois de o player reabrir e Alt+Tab", problems);
    }

    /// <summary>
    /// Tela cheia com o foco em outra janela pequena (outro programa, outro monitor): o Windows põe a barra de tarefas por cima do palco, e a barra de
    /// controles da live de baixo ficava embaixo dela (visto numa rodada de 2026-11-20). A janelinha fica no canto de cima, longe dos vídeos de baixo.
    /// </summary>
    static async Task FocusElsewhereAsync(MainForm form, PlayerStage stage, List<Live> lives, string label, List<string> problems)
    {
        var screen = Screen.FromHandle(form.Handle).Bounds;
        using var other = new Form { StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(screen.X + 8, screen.Y + 8, 160, 60), Text = "outro programa", ShowInTaskbar = false };
        other.Show();
        keybd_event(VkMenu, 0, 0, UIntPtr.Zero);
        keybd_event(VkMenu, 0, KeyUp, UIntPtr.Zero);
        SetForegroundWindow(other.Handle);
        await UiProbe.SettleAsync(10);
        await CheckBarsAsync(stage, lives, $"{label}, foco em outra janela", problems);
        other.Close();
        await FocusAsync(form);
    }

    static async Task ShowDesktopTwiceAsync(MainForm form)
    {
        for (int i = 0; i < 2; i++)
        {
            keybd_event(VkWin, 0, 0, UIntPtr.Zero);
            keybd_event(VkD, 0, 0, UIntPtr.Zero);
            keybd_event(VkD, 0, KeyUp, UIntPtr.Zero);
            keybd_event(VkWin, 0, KeyUp, UIntPtr.Zero);
            await UiProbe.SettleAsync(16);
        }
        await FocusAsync(form);
    }

    static Live Open(PlayerStage stage, int n)
    {
        var viewer = new Viewer { UseHardware = _hardware };
        Process? source = null;
        stage.Add(viewer, () => $"Amigo {n} · Imagem de teste · ao vivo há 1 min");
        viewer.StartLocal(sink => source = Feed(sink, n), $"Streemu teste {n}");
        var bar = PlayerOverlay.ShowFor(viewer, () => "720p 30 FPS", () => [new RosterEntry($"Amigo {n}", true, false)], stage: stage);
        return new Live(viewer, bar, source!);
    }

    /// <summary>Imagem de teste do ffmpeg (sem câmera, tela ou rede) em H.264/MPEG-TS, o que o player espera de uma live.</summary>
    static Process Feed(Stream sink, int n)
    {
        string ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (!File.Exists(ffmpeg)) throw new FileNotFoundException($"Falta {ffmpeg} (esperado: o ffmpeg copiado ao lado do teste pelo build)");
        var info = new ProcessStartInfo(ffmpeg, $"-hide_banner -loglevel error -re -f lavfi -i testsrc2=size=1280x720:rate=30 -vf hue=h={n * 120} -c:v libx264 -preset ultrafast -tune zerolatency -g 30 -f mpegts pipe:1")
        { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        var process = Process.Start(info)!;
        _ = Task.Run(async () => { try { await process.StandardOutput.BaseStream.CopyToAsync(sink); } catch (IOException) { } catch (ObjectDisposedException) { } });
        return process;
    }

    static void Close(Live live)
    {
        live.Viewer.Stop();
        try { if (!live.Source.HasExited) live.Source.Kill(); } catch (InvalidOperationException) { }
        live.Bar.Close();
    }

    static async Task WaitForVideosAsync(List<Live> lives)
    {
        for (int i = 0; i < 200 && lives.Any(l => l.Viewer.Window.Handle == IntPtr.Zero); i++) await UiProbe.SettleAsync(1);
        if (lives.Any(l => l.Viewer.Window.Handle == IntPtr.Zero)) throw new TimeoutException("O player não abriu a janela em 10 s (esperado: ffplay mostrando a imagem de teste)");
        await UiProbe.SettleAsync(20);
    }

    /// <summary>Passa o mouse sobre cada vídeo: a barra dele tem que aparecer e ser a janela sob o mouse no meio dela.</summary>
    static async Task CheckBarsAsync(PlayerStage stage, List<Live> lives, string label, List<string> problems)
    {
        foreach (var (live, index) in lives.Select((l, i) => (l, i)))
        {
            if (stage.TargetOf(live.Viewer) is not { } video) { problems.Add($"{label}: live {index} sem lugar no palco"); continue; }
            var spot = new Point(video.X + video.Width / 2, video.Bottom - video.Height / 4);
            for (int i = 0; i < 4; i++) { SetCursorPos(spot.X + i * 3, spot.Y); await UiProbe.SettleAsync(2); }
            if (Problem(live, video) is not { } p) continue;
            for (int i = 0; i < 10; i++) { SetCursorPos(spot.X + i * 3, spot.Y + 2); await UiProbe.SettleAsync(2); } // mais 1 s mexendo: passageiro ou preso?
            string later = Problem(live, video) is { } q ? $"1 s depois continua: {q}" : "1 s depois apareceu";
            problems.Add($"{label}: live {index}: {p} ({later}; {Describe(live, stage)})");
        }
    }

    static string? Problem(Live live, Rectangle video)
    {
        var bar = live.Bar;
        if (!bar.Visible) return "a barra não apareceu com o mouse sobre o vídeo";
        if (!IsWindowVisible(bar.Handle)) return "a barra se diz à vista, mas o Windows a escondeu";
        var center = new Point(bar.Bounds.X + bar.Width / 2, bar.Bounds.Y + bar.Height / 2);
        var root = RootAt(center);
        if (root == bar.Handle) return null;
        // o meio do vídeo também coberto por outro programa: o app inteiro ficou atrás (o teste não o trouxe de volta), não é a barra
        if (RootAt(new Point(video.X + video.Width / 2, video.Y + video.Height / 3)) != live.Viewer.Window.Handle) return $"{Behind}: {Name(root)}";
        string who = root == live.Viewer.Window.Handle ? "o próprio vídeo" : Name(root);
        return $"a barra está atrás: sob o mouse fica {who}; barra {bar.Bounds}, vídeo {video}";
    }

    const string Behind = "o app inteiro ficou atrás de outra janela";

    static IntPtr RootAt(Point p) => GetAncestor(WindowFromPoint(p), 2); // GA_ROOT

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    /// <summary>Quem é a janela (classe e processo), para dizer o que cobriu a barra.</summary>
    static string Name(IntPtr hwnd)
    {
        var cls = new System.Text.StringBuilder(128);
        GetClassName(hwnd, cls, cls.Capacity);
        GetWindowThreadProcessId(hwnd, out uint pid);
        string process = pid == Environment.ProcessId ? "este app" : TryProcessName(pid);
        string control = Control.FromHandle(hwnd) is { } c ? $" ({c.GetType().Name})" : "";
        return $"0x{hwnd:X} {cls}{control} de {process}";
    }

    static string TryProcessName(uint pid) { try { return Process.GetProcessById((int)pid).ProcessName; } catch (ArgumentException) { return $"processo {pid}"; } }

    static string Describe(Live live, PlayerStage stage)
    {
        var w = live.Viewer.Window;
        return $"cursor {Cursor.Position}, alvo {stage.TargetOf(live.Viewer)}, barra {live.Bar.Bounds} visível={live.Bar.Visible}, "
            + $"vídeo minimizado={w.IsMinimized} escondido={w.IsHidden}, janela principal escondida={stage.HostHidden}, à frente: {Name(GetForegroundWindow())}, "
            + $"último movimento há {(DateTime.Now - UiProbe.Field<DateTime>(live.Bar, "_lastMove")).TotalMilliseconds:0} ms, relógio={UiProbe.Field<System.Windows.Forms.Timer>(live.Bar, "_tick").Enabled}";
    }

    /// <summary>
    /// Sai para outro programa com Alt+Tab (mais Tabs se o anterior na lista ainda for nosso: o vídeo clicado é outro processo, mas é do app) e volta com
    /// um Alt+Tab, como quem dá uma olhada em outra coisa.
    /// </summary>
    static async Task AltTabAsync(MainForm form, List<Live> lives, string label, List<string> problems)
    {
        for (int tabs = 1; tabs <= 3; tabs++)
        {
            PressAltTab(tabs);
            await UiProbe.SettleAsync(16);
            if (!Ours(form, lives, GetForegroundWindow())) break;
            if (tabs == 3) problems.Add($"{label}: o Alt+Tab não saiu do app (o teste não provou nada neste passo)");
        }
        PressAltTab(1);
        await UiProbe.SettleAsync(16);
    }

    static bool Ours(MainForm form, List<Live>? lives, IntPtr hwnd) =>
        hwnd == form.Handle || Application.OpenForms.Cast<Form>().Any(f => f.Handle == hwnd) || lives?.Any(l => l.Viewer.Window.Handle == hwnd) == true;

    static void PressAltTab(int tabs)
    {
        keybd_event(VkMenu, 0, 0, UIntPtr.Zero);
        for (int i = 0; i < tabs; i++) { keybd_event(VkTab, 0, 0, UIntPtr.Zero); keybd_event(VkTab, 0, KeyUp, UIntPtr.Zero); Thread.Sleep(60); }
        Thread.Sleep(80);
        keybd_event(VkMenu, 0, KeyUp, UIntPtr.Zero);
    }

    /// <summary>Traz a janela à frente como o usuário faria (o Windows só deixa trocar o primeiro plano depois de uma tecla).</summary>
    static async Task FocusAsync(MainForm form)
    {
        for (int attempt = 0; attempt < 5 && !Ours(form, null, GetForegroundWindow()); attempt++)
        {
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            keybd_event(VkMenu, 0, 0, UIntPtr.Zero);
            keybd_event(VkMenu, 0, KeyUp, UIntPtr.Zero);
            // a fila de entrada da janela da frente libera a troca (o que o clique no ícone da barra de tarefas faz por nós)
            uint front = GetWindowThreadProcessId(GetForegroundWindow(), out _), mine = GetCurrentThreadId();
            AttachThreadInput(mine, front, true);
            BringWindowToTop(form.Handle);
            SetForegroundWindow(form.Handle);
            AttachThreadInput(mine, front, false);
            await UiProbe.SettleAsync(4);
        }
        await UiProbe.SettleAsync(6);
    }

    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    static async Task ClickAsync(Point at)
    {
        SetCursorPos(at.X, at.Y);
        mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
        mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
        await UiProbe.SettleAsync(6);
    }

    static Point Center(Rectangle r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);
}
