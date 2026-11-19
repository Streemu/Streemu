using System.Runtime.InteropServices;
using Streemu.Media;

namespace Streemu.Ui;

/// <summary>
/// Roda do mouse (passos de 5% no volume) e tecla M (mudo) sobre a janela do player. O ffplay tem o foco, ou o jogo tem, e a
/// barra do Streemu não recebe teclado nem roda (ela nunca rouba o foco), então isto usa ganchos de baixo nível do Windows.
/// Os ganchos só existem ENQUANTO o cursor está sobre o player: fora dele nenhuma tecla do usuário passa por aqui e o M continua
/// sendo do jogo. A roda só vale se a janela sob o cursor é mesmo o player (não minimizado, sem outra janela na frente) e o M só
/// vale com o player em foco; senão o evento segue para o programa da frente. Só a roda e o M (sem Ctrl, Alt ou Windows) são tratados e engolidos (o ffplay também muda o próprio mudo com o M,
/// o que dobraria o efeito); todo o resto segue adiante sem ser lido.
/// A função do gancho só DECIDE (engolir ou não, em tempo constante, com a área do player guardada no último ciclo) e adia o trabalho
/// para depois de retornar: se um gancho de baixo nível demora mais de ~300 ms o Windows o remove em silêncio (medido em 2026-11-07:
/// só o 1º clique da roda valia, porque ajustar o volume dentro do gancho demorava).
/// Uso: <c>var input = new PlayerInput(audio, () =&gt; Cursor.Position, () =&gt; windowBoundsOnScreen); input.Update(); // a cada 100 ms</c>
/// </summary>
public sealed class PlayerInput : IDisposable
{
    public const int StepPercent = 5;
    const int WhKeyboardLl = 13, WhMouseLl = 14, WmKeyDown = 0x100, WmKeyUp = 0x101, WmSysKeyDown = 0x104, WmMouseWheel = 0x20A;
    const int VkM = 0x4D, VkControl = 0x11, VkMenu = 0x12, VkLWin = 0x5B, VkRWin = 0x5C, WheelNotch = 120;

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    readonly PlayerAudio _audio;
    readonly Func<Point> _cursor;
    readonly Func<Rectangle?> _area;
    readonly Func<Point, bool> _excluded;
    readonly Func<IntPtr[]> _windows;
    readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    readonly HookProc _mouseProc, _keyProc; // guardados em campos: se o coletor de streemu levar o delegate, o Windows chama um endereço morto
    IntPtr _mouseHook, _keyHook;
    int _wheelRemainder;
    bool _mDown, _inHook;
    Rectangle? _lastArea;
    IntPtr[] _lastWindows = [];

    /// <param name="area">Retângulo do player em pixels de tela (nulo se não há janela).</param>
    /// <param name="excluded">Pontos sobre a janela que NÃO são do player (ex.: a lista de pessoas aberta, que rola com a roda).</param>
    /// <param name="windows">Janelas do player: a do ffplay primeiro, depois a barra do Streemu. Nulo = não confere (testes).</param>
    public PlayerInput(PlayerAudio audio, Func<Point> cursor, Func<Rectangle?> area, Func<Point, bool>? excluded = null, Func<IntPtr[]>? windows = null)
    {
        _audio = audio;
        _cursor = cursor;
        _area = area;
        _excluded = excluded ?? (_ => false);
        _windows = windows ?? (() => []);
        _mouseProc = MouseProc;
        _keyProc = KeyProc;
    }

    /// <summary>A roda ou o M mudaram o som: quem mostra os controles os mostra para a pessoa ver o volume novo.</summary>
    public event Action? Used;

    public bool HooksInstalled => _mouseHook != IntPtr.Zero || _keyHook != IntPtr.Zero;

    /// <summary>Liga os ganchos quando o cursor entra no player e desliga quando sai. Chamar a cada ciclo da barra (100 ms).</summary>
    public void Update()
    {
        _lastArea = _area();
        _lastWindows = _windows();
        bool want = IsOverPlayer(_cursor());
        if (want && !HooksInstalled) Install();
        else if (!want && HooksInstalled) Uninstall();
    }

    public bool IsOverPlayer(Point screen) => _area() is { } r && r.Contains(screen) && !_excluded(screen) && PlayerUnder(screen);

    /// <summary>Versão barata para dentro do gancho: usa a área do último ciclo (no máximo 100 ms velha) em vez de procurar a janela de novo.</summary>
    bool IsOverPlayerCached(Point screen) => _lastArea is { } r && r.Contains(screen) && !_excluded(screen) && PlayerUnder(screen);

    // Só o retângulo não basta: com o player minimizado ou atrás do navegador no mesmo lugar, a roda e o M mudavam o volume da
    // stream em vez de rolar a página (relato de 2026-11-08). Por isso confere QUAL janela está de fato sob o cursor.
    bool PlayerUnder(Point screen)
    {
        var windows = _lastWindows;
        if (windows.Length == 0) return true; // sem janelas conhecidas (testes): só o retângulo decide
        if (windows[0] == IntPtr.Zero || IsIconic(windows[0])) return false;
        return Owns(windows, GetAncestor(WindowFromPoint(new PointStruct { X = screen.X, Y = screen.Y }), GaRoot));
    }

    /// <summary>O M só é do player quando o player está em foco: com o navegador ou o jogo em foco a tecla é deles.</summary>
    bool PlayerFocused()
    {
        var windows = _lastWindows;
        if (windows.Length == 0) return true;
        if (windows[0] == IntPtr.Zero || IsIconic(windows[0])) return false;
        return Owns(windows, GetAncestor(GetForegroundWindow(), GaRoot));
    }

    static bool Owns(IntPtr[] windows, IntPtr hwnd) => hwnd != IntPtr.Zero && Array.IndexOf(windows, hwnd) >= 0;

    /// <summary>Dentro do gancho o trabalho vai para a fila da tela (e roda depois de retornar); fora dele (testes) roda na hora.</summary>
    void Perform(Action work)
    {
        void Run() { work(); Used?.Invoke(); }
        if (_inHook && _ui != null) _ui.Post(_ => Run(), null);
        else Run();
    }

    /// <summary>Um giro da roda: +120 por "clique" para cima. Cada clique muda 5% (as rodas de alta resolução mandam frações: acumula). Subir tira do mudo.</summary>
    public void OnWheel(int delta)
    {
        _wheelRemainder += delta;
        int notches = _wheelRemainder / WheelNotch;
        _wheelRemainder -= notches * WheelNotch;
        if (notches == 0) return;
        Perform(() =>
        {
            _audio.SetPercent(Math.Clamp(_audio.Percent + notches * StepPercent, 0, 100));
            if (notches > 0 && _audio.Muted && _audio.Percent > 0) _audio.SetMuted(false); // como arrastar o controle de volume
        });
    }

    /// <summary>Tecla apertada sobre o player. Devolve verdadeiro se foi o M puro (engolido). Segurar o M não fica ligando e desligando o mudo.</summary>
    public bool OnKeyDown(int vk, bool modifiers)
    {
        if (vk != VkM || modifiers) return false;
        if (!_mDown) Perform(_audio.ToggleMuted);
        _mDown = true;
        return true;
    }

    /// <summary>Soltar o M: engole também (o ffplay não pode ver um "soltou" de uma tecla que não viu apertar).</summary>
    public bool OnKeyUp(int vk)
    {
        if (vk != VkM || !_mDown) return false;
        _mDown = false;
        return true;
    }

    IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam == WmMouseWheel)
        {
            var info = Marshal.PtrToStructure<MouseHook>(lParam);
            if (IsOverPlayerCached(new Point(info.X, info.Y)))
            {
                _inHook = true;
                try { OnWheel((short)((info.MouseData >> 16) & 0xFFFF)); } finally { _inHook = false; }
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsOverPlayerCached(_cursor()))
        {
            int vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode é o primeiro campo
            int message = (int)wParam;
            _inHook = true;
            try
            {
                // Soltar um M que foi engolido é sempre engolido (mesmo se o foco mudou no meio); apertar só vale com o player em foco.
                bool handled = message is WmKeyDown or WmSysKeyDown ? vk == VkM && PlayerFocused() && OnKeyDown(vk, ModifierHeld())
                                                                    : message == WmKeyUp && OnKeyUp(vk);
                if (handled) return (IntPtr)1;
            }
            finally { _inHook = false; }
        }
        return CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    static bool ModifierHeld() => new[] { VkControl, VkMenu, VkLWin, VkRWin }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0);

    void Install()
    {
        var module = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, module, 0);
        _keyHook = SetWindowsHookEx(WhKeyboardLl, _keyProc, module, 0);
    }

    void Uninstall()
    {
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) UnhookWindowsHookEx(_keyHook);
        _mouseHook = _keyHook = IntPtr.Zero;
        _mDown = false;
        _wheelRemainder = 0;
    }

    public void Dispose() => Uninstall();

    [StructLayout(LayoutKind.Sequential)]
    struct MouseHook { public int X, Y; public uint MouseData, Flags, Time; public IntPtr Extra; }

    [StructLayout(LayoutKind.Sequential)]
    struct PointStruct { public int X, Y; }

    const uint GaRoot = 2;
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(PointStruct point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}
