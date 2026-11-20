using System.Runtime.InteropServices;
using System.Text;

namespace Streemu.Ui;

/// <summary>
/// A barra de tarefas do Windows está por cima deste ponto agora? Com o palco em tela cheia e o foco em outra janela (outro monitor, o próprio vídeo,
/// que é de outro processo), o Windows mostra a barra de tarefas por cima, e a barra de controles da live de baixo ficava embaixo dela, sumida (teste
/// --alt-tab-check, 2026-11-20). Quem desenha sobre o vídeo sobe acima dela enquanto ela estiver à vista.
/// Uso: <c>if (TaskbarCover.At(ponto) is { } barra) alto = barra.Top;</c>
/// </summary>
internal static class TaskbarCover
{
    /// <summary>O retângulo da barra de tarefas (principal ou de outro monitor) que está à vista em <paramref name="screen"/>; nulo se não é ela.</summary>
    public static Rectangle? At(Point screen)
    {
        var root = GetAncestor(WindowFromPoint(screen), GaRoot);
        if (root == IntPtr.Zero || !IsTaskbar(root) || !GetWindowRect(root, out var r)) return null;
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    static bool IsTaskbar(IntPtr hwnd)
    {
        var name = new StringBuilder(32);
        GetClassName(hwnd, name, name.Capacity);
        return name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    const uint GaRoot = 2;
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
}
