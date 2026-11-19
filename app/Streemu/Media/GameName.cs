using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Streemu.Media;

/// <summary>
/// Nome bonito do que está sendo transmitido, para o Discord e a lista do grupo ("Cyberpunk 2077", e não "Cyberpunk2077").
/// Ordem: descrição do executável, título da janela, nome do processo. Monitor inteiro vira "a tela".
/// </summary>
public static class GameName
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);

    static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase) { "", "game", "launcher", "client", "application", "unreal engine", "unity" };

    public static string Of(CaptureSource s)
    {
        if (!s.IsWindow) return "a tela";
        try
        {
            string? exe = ExePath(s.ProcessId);
            if (exe != null)
            {
                var desc = FileVersionInfo.GetVersionInfo(exe).FileDescription?.Trim();
                if (!string.IsNullOrEmpty(desc) && !Generic.Contains(desc) && desc.Length <= 48) return desc;
                var product = FileVersionInfo.GetVersionInfo(exe).ProductName?.Trim();
                if (!string.IsNullOrEmpty(product) && !Generic.Contains(product) && product.Length <= 48) return product;
            }
        }
        catch { }
        var sb = new StringBuilder(256);
        GetWindowText(s.Handle, sb, sb.Capacity);
        string title = sb.ToString().Trim();
        if (title.Length is > 0 and <= 48) return title;
        return string.IsNullOrEmpty(s.ProcessName) ? "um jogo" : s.ProcessName;
    }

    /// <summary>Caminho do .exe com o acesso mínimo (funciona com jogo rodando como administrador ou com anti-cheat, onde Process.MainModule falha).</summary>
    public static string? ExePath(int pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }
}
