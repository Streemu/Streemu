using System.Diagnostics;
using System.Text;
using Streemu.Util;

namespace Streemu.Media;

/// <summary>Uma janela ou um monitor que pode ser capturado.</summary>
public sealed class CaptureSource
{
    public required string Label { get; init; }
    public required bool IsWindow { get; init; }
    public required IntPtr Handle { get; init; }
    public int ProcessId { get; init; }
    /// <summary>Janela do tamanho exato de um monitor: quase sempre um jogo em tela cheia/sem bordas.</summary>
    public bool IsFullScreen { get; init; }
    public bool IsPrimary { get; init; }
    public string ProcessName { get; init; } = "";
    public override string ToString() => Label;

    /// <summary>A mesma fonte com outro identificador: o Windows pode recriar o monitor com outro HMONITOR depois de uma troca de modo (ver VoltaDaCaptura).</summary>
    public CaptureSource WithHandle(IntPtr handle) => new()
    {
        Label = Label, IsWindow = IsWindow, Handle = handle, ProcessId = ProcessId, IsFullScreen = IsFullScreen, IsPrimary = IsPrimary, ProcessName = ProcessName,
    };

    /// <summary>Tamanho atual em pixels físicos (área da janela ou do monitor).</summary>
    public (int Width, int Height) CurrentSize()
    {
        if (IsWindow)
        {
            Native.GetClientRect(Handle, out var r);
            return (r.Width, r.Height);
        }
        var info = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
        Native.GetMonitorInfo(Handle, ref info);
        return (info.rcMonitor.Width, info.rcMonitor.Height);
    }

    public bool StillExists() => !IsWindow || Native.IsWindow(Handle);

    /// <summary>A janela está minimizada (jogo em tela cheia exclusiva que o Windows minimizou ao você clicar em outro programa, por exemplo).</summary>
    public bool IsMinimized => IsWindow && Native.IsIconic(Handle);

    /// <summary>
    /// Restaura a janela minimizada e espera ela ter tamanho (até 3 s). A captura não vê janela minimizada (área 0x0), então isto vem antes de capturar.
    /// Falso se ela não voltou. Chamar fora da thread da tela.
    /// Uso: <c>if (!source.RestoreIfMinimized()) throw ...;</c>
    /// </summary>
    public bool RestoreIfMinimized()
    {
        if (!IsMinimized) return true;
        Native.ShowWindow(Handle, Native.SW_RESTORE);
        for (int i = 0; i < 60; i++)
        {
            Thread.Sleep(50);
            if (!Native.IsIconic(Handle) && CurrentSize() is (>= 200, >= 150)) { Thread.Sleep(300); return true; }
        }
        return false;
    }

    /// <summary>
    /// A janela está AGORA com o tamanho exato de um monitor (jogo em tela cheia ou sem bordas)? <see cref="IsFullScreen"/> só vale no momento em que a janela
    /// foi escolhida: o jogo pode trocar de modo depois (janela 720p → tela cheia 1080p, Alt+Enter ou Alt+Tab) e a captura precisa acompanhar.
    /// Falso com a janela minimizada (o tamanho dela não diz nada).
    /// </summary>
    public bool IsFullScreenNow()
    {
        if (!IsWindow || Native.IsIconic(Handle)) return false;
        var (w, h) = CurrentSize();
        return Sources.MonitorSizes().Contains((w, h));
    }

    /// <summary>
    /// A área da janela cobre AGORA (quase) todo o monitor onde ela está? Só assim capturar o monitor mostra o mesmo que a janela. O vigia usa isto antes de
    /// trocar a captura da janela pela do monitor (campo 2026-11-10, Spotify em janela num PC do grupo: a troca mostrou a área de trabalho para quem assistia).
    /// Mais estrito que <see cref="IsFullScreenNow"/> (confere a posição e o monitor certo) e com folga de 3% para jogo "sem bordas" com 1 pixel a menos.
    /// Falso com a janela minimizada.
    /// </summary>
    public bool CoversMonitorNow()
    {
        if (!IsWindow || !Native.IsWindow(Handle) || Native.IsIconic(Handle)) return false;
        if (!Native.GetClientRect(Handle, out var r)) return false;
        var origin = new Native.POINT();
        if (!Native.ClientToScreen(Handle, ref origin)) return false;
        var client = new Native.RECT { Left = origin.X, Top = origin.Y, Right = origin.X + r.Width, Bottom = origin.Y + r.Height };
        var info = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
        if (!Native.GetMonitorInfo(Display.MonitorOf(Handle), ref info)) return false;
        return CoveredFraction(client, info.rcMonitor) >= MinCoverage;
    }

    /// <summary>Quanto do monitor a janela precisa cobrir para a captura do monitor não mostrar nada além dela (o resto é borda de 1 pixel, não área de trabalho).</summary>
    internal const double MinCoverage = 0.97;

    /// <summary>Fração da área do monitor coberta pela área da janela (0 a 1).</summary>
    internal static double CoveredFraction(Native.RECT client, Native.RECT monitor)
    {
        long w = Math.Min(client.Right, monitor.Right) - Math.Max(client.Left, monitor.Left);
        long h = Math.Min(client.Bottom, monitor.Bottom) - Math.Max(client.Top, monitor.Top);
        long area = (long)monitor.Width * monitor.Height;
        return w <= 0 || h <= 0 || area <= 0 ? 0 : (double)(w * h) / area;
    }
}

public static class Sources
{
    /// <summary>Tamanho em pixels de cada monitor ligado.</summary>
    internal static HashSet<(int, int)> MonitorSizes()
    {
        var sizes = new HashSet<(int, int)>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
            Native.GetMonitorInfo(h, ref info);
            sizes.Add((info.rcMonitor.Width, info.rcMonitor.Height));
            return true;
        }, IntPtr.Zero);
        return sizes;
    }

    /// <summary>
    /// Tamanho que a área da janela terá quando ela voltar de minimizada: o retângulo "normal" guardado pelo Windows menos a moldura. Janela minimizada
    /// tem área 0x0 (e fica em -32000,-32000), por isso não dá para ler o tamanho dela direto.
    /// </summary>
    static (int Width, int Height) RestoredClientSize(IntPtr h)
    {
        var placement = new Native.WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        if (!Native.GetWindowPlacement(h, ref placement)) return (0, 0);
        var frame = new Native.RECT();
        Native.AdjustWindowRectEx(ref frame, (uint)Native.GetWindowLong32(h, -16), false, (uint)Native.GetWindowLong32(h, -20));
        var n = placement.rcNormalPosition;
        return (n.Width - frame.Width, n.Height - frame.Height);
    }

    static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "TextInputHost", "ShellExperienceHost", "SearchHost", "StartMenuExperienceHost",
        "SystemSettings", "ApplicationFrameHost", "Streemu", "ffplay"
    };

    /// <summary>
    /// Ordem da lista: janelas em tela cheia (provável jogo) primeiro, depois as outras janelas, e os monitores por último.
    /// Motivo (teste de campo com dois PCs do grupo): capturando o MONITOR com jogo em tela cheia, a captura recebia
    /// só ~0-11 quadros novos por segundo, porque o Windows mostra o jogo direto na tela sem compor a área de trabalho;
    /// capturando a JANELA do jogo, vinham ~54/s. Mas no teste da 0.1.8 a janela também deu 5–10/s (Cyberpunk, Risk of Rain 2),
    /// então "Jogo/tela cheia" agora vai pelo ddagrab do monitor onde o jogo está (ver <see cref="Capture.For"/>).
    /// </summary>
    public static List<CaptureSource> List()
    {
        var monitors = new List<CaptureSource>();
        var monitorSizes = new HashSet<(int, int)>();
        int index = 1;
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
            Native.GetMonitorInfo(h, ref info);
            bool primary = (info.dwFlags & 1) != 0;
            monitorSizes.Add((info.rcMonitor.Width, info.rcMonitor.Height));
            monitors.Add(new CaptureSource
            {
                Label = $"Monitor {index++}{(primary ? " (principal)" : "")} — {info.rcMonitor.Width}x{info.rcMonitor.Height} (área de trabalho; para jogo escolha o jogo, não o monitor)",
                IsWindow = false,
                IsPrimary = primary,
                Handle = h
            });
            return true;
        }, IntPtr.Zero);

        var windows = new List<CaptureSource>();
        int self = Environment.ProcessId;
        Native.EnumWindows((h, _) =>
        {
            try
            {
                if (!Native.IsWindowVisible(h)) return true;
                if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
                if ((Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64() & Native.WS_EX_TOOLWINDOW) != 0) return true;
                if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
                int len = Native.GetWindowTextLength(h);
                if (len == 0) return true;
                var sb = new StringBuilder(len + 1);
                Native.GetWindowText(h, sb, sb.Capacity);
                bool minimized = Native.IsIconic(h); // jogo minimizado continua na lista: ao transmitir o app o restaura (RestoreIfMinimized)
                int width, height;
                if (minimized) (width, height) = RestoredClientSize(h);
                else { Native.GetClientRect(h, out var rc); (width, height) = (rc.Width, rc.Height); }
                if (width < 200 || height < 150) return true;
                Native.GetWindowThreadProcessId(h, out uint pid);
                if (pid == self) return true;
                string pname = "";
                try { pname = Process.GetProcessById((int)pid).ProcessName; } catch { }
                if (IgnoredProcesses.Contains(pname)) return true;
                string title = sb.ToString();
                if (title.Length > 60) title = title[..57] + "...";
                bool full = monitorSizes.Contains((width, height));
                windows.Add(new CaptureSource
                {
                    Label = (full ? "Jogo/tela cheia: " : "Janela: ") + $"{title} ({pname}) — {width}x{height}{(minimized ? " (minimizada)" : "")}",
                    IsWindow = true,
                    IsFullScreen = full,
                    Handle = h,
                    ProcessId = (int)pid,
                    ProcessName = pname
                });
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        var list = new List<CaptureSource>();
        list.AddRange(windows.Where(w => w.IsFullScreen).OrderBy(w => w.Label));
        list.AddRange(windows.Where(w => !w.IsFullScreen).OrderBy(w => w.Label));
        list.AddRange(monitors);
        return list;
    }

    /// <summary>Processo raiz do Discord (o que não tem pai Discord), para excluir toda a árvore dele do áudio.</summary>
    public static Process? FindDiscordRoot()
    {
        var all = new[] { "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment" }
            .SelectMany(Process.GetProcessesByName).ToList();
        if (all.Count == 0) return null;
        var ids = all.Select(p => p.Id).ToHashSet();
        var roots = all.Where(p => Native.ParentProcessId(p) is not int parent || !ids.Contains(parent)).ToList();
        if (roots.Count > 1) Log.Info($"Aviso: {roots.Count} Discords abertos (ex.: estável + PTB). Só um pode ser excluído do áudio.");
        return roots.FirstOrDefault() ?? all[0];
    }
}
