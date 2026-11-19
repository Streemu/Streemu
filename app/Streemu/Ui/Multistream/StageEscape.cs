using System.Runtime.InteropServices;

namespace Streemu.Ui.Multistream;

/// <summary>
/// Esc com o foco num vídeo do palco em tela cheia: sai da tela cheia em vez de fechar a live (decisão de 13/11/2026 na interface Streemu). O ffplay fecha com
/// Esc e não tem opção para trocar a tecla, e o foco fica na janela dele, então isto usa um gancho de teclado de baixo nível do Windows, como o
/// <see cref="PlayerInput"/>. O gancho só existe ENQUANTO o palco está em tela cheia e à vista: fora disso nenhuma tecla passa por aqui e o Esc segue fechando a
/// live, como antes. Só o Esc puro (sem Ctrl, Alt, Shift ou Windows) com um vídeo do palco em foco é engolido; o Q continua fechando a live.
/// A função do gancho só DECIDE (em tempo constante, com as janelas guardadas no último ciclo do palco) e adia a saída da tela cheia para depois de retornar:
/// gancho de baixo nível que demora mais de ~300 ms é removido pelo Windows em silêncio (ver PlayerInput).
/// Uso: <c>var esc = new StageEscape(saiDaTelaCheia); esc.Update(ativo, janelasDosVideos); // a cada ciclo do palco</c>
/// </summary>
public sealed class StageEscape : IDisposable
{
    const int WhKeyboardLl = 13, WmKeyDown = 0x100, WmKeyUp = 0x101, WmSysKeyDown = 0x104, WmSysKeyUp = 0x105;
    const int VkEscape = 0x1B, VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkLWin = 0x5B, VkRWin = 0x5C;
    const uint GaRoot = 2;

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    readonly Action _exitFullscreen;
    readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    readonly HookProc _proc; // guardado em campo: se o coletor de streemu levar o delegate, o Windows chama um endereço morto
    IntPtr _hook;
    IntPtr[] _videos = [];
    bool _escDown, _inHook;

    public StageEscape(Action exitFullscreen)
    {
        _exitFullscreen = exitFullscreen;
        _proc = KeyProc;
    }

    public bool Installed => _hook != IntPtr.Zero;

    /// <summary>Liga o gancho quando o palco entra em tela cheia com vídeo e desliga quando sai. Chamar a cada ciclo do palco (250 ms) e ao mudar a tela cheia.</summary>
    /// <param name="videos">Janelas do ffplay que estão no palco (as da janela pequena ficam de fora: lá o Esc continua fechando).</param>
    public void Update(bool active, IntPtr[] videos)
    {
        _videos = videos;
        bool want = active && videos.Length > 0;
        if (want && !Installed) Install();
        else if (!want && Installed) Uninstall();
    }

    /// <summary>
    /// Tecla vista com o gancho ligado. Devolve verdadeiro se foi engolida. Segurar o Esc não fica entrando e saindo da tela cheia, e o "soltou" de um Esc
    /// engolido é engolido também (o ffplay não pode ver um "soltou" de uma tecla que não viu apertar).
    /// </summary>
    public bool OnKey(int vk, bool down, bool modifiers, IntPtr foreground)
    {
        if (vk != VkEscape) return false;
        if (!down) { if (!_escDown) return false; _escDown = false; return true; }
        if (_escDown) return true; // repetição automática do Esc já engolido
        if (modifiers || foreground == IntPtr.Zero || Array.IndexOf(_videos, foreground) < 0) return false;
        _escDown = true;
        if (_inHook && _ui != null) _ui.Post(_ => _exitFullscreen(), null);
        else _exitFullscreen();
        return true;
    }

    IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode é o primeiro campo
            int message = (int)wParam;
            if (vk == VkEscape && message is WmKeyDown or WmSysKeyDown or WmKeyUp or WmSysKeyUp)
            {
                bool down = message is WmKeyDown or WmSysKeyDown;
                _inHook = true;
                try
                {
                    var foreground = down ? GetAncestor(GetForegroundWindow(), GaRoot) : IntPtr.Zero;
                    if (OnKey(vk, down, down && ModifierHeld(), foreground)) return (IntPtr)1;
                }
                finally { _inHook = false; }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    static bool ModifierHeld() => new[] { VkShift, VkControl, VkMenu, VkLWin, VkRWin }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0);

    void Install() => _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);

    void Uninstall()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _escDown = false;
    }

    public void Dispose() => Uninstall();

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}
