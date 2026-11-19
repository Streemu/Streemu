using Streemu.Util;

namespace Streemu.Media;

/// <summary>
/// A janela do ffplay vista de fora: onde está, e como movê-la ou deixá-la no topo.
/// O ffplay desenha com a placa e não aceita ser filho de outra janela (spike de 2026-11-06: a imagem sai
/// fora da janela hospedeira), então os controles do Streemu ficam em outra janela que acompanha esta.
///
/// Redimensionar por fora só escala a imagem se o ffplay foi aberto SEM -noborder (spike de 2026-11-07; com -noborder a
/// janela muda de tamanho mas a imagem fica cortada, medido em 2026-11-06). A moldura sai depois, por estilo (<see cref="SetFrame"/>).
/// Uso: <c>viewer.Window.MoveTo(new Point(1600, 800)); viewer.Window.SetTopMost(true);</c>
/// </summary>
public sealed class PlayerWindow
{
    readonly Func<int?> _processId;
    IntPtr _hwnd;

    public PlayerWindow(Func<int?> processId) => _processId = processId;

    /// <summary>Retângulo da janela em pixels de tela, ou falso se o ffplay ainda não abriu a janela.</summary>
    public bool TryGetBounds(out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        var hwnd = Resolve();
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return false;
        bounds = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        return true;
    }

    /// <summary>Área do vídeo (sem barra de título nem borda) em pixels de tela.</summary>
    public bool TryGetClientBounds(out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        var hwnd = Resolve();
        var origin = new Native.POINT();
        if (hwnd == IntPtr.Zero || !Native.GetClientRect(hwnd, out var r) || !Native.ClientToScreen(hwnd, ref origin)) return false;
        bounds = new Rectangle(origin.X, origin.Y, r.Width, r.Height);
        return true;
    }

    /// <summary>Janela atual do ffplay (zero se ainda não abriu), para outra janela do app se tornar "dona" dela.</summary>
    public IntPtr Handle => Resolve();

    public bool IsMinimized => Resolve() is var h && h != IntPtr.Zero && Native.IsIconic(h);

    /// <summary>
    /// O ffplay está escondido? Minimizar o app esconde (não minimiza) o ffplay, que é filho da janela principal; as posições que o palco guarda continuam
    /// válidas, e sem esta checagem a barra e o cabeçalho eram reabertos sozinhos, flutuando sobre a área de trabalho sem vídeo.
    /// </summary>
    public bool IsHidden => Resolve() is var h && h != IntPtr.Zero && !Native.IsWindowVisible(h);

    /// <summary>
    /// Aperta uma tecla no ffplay (ex.: F alterna a tela cheia). Posta a mensagem direto na janela dele: não precisa de foco,
    /// e `keybd_event` com foco NÃO funcionou (medido em 2026-11-06: 1302 → 1920 com PostMessage; sem efeito com foco + keybd_event).
    /// Uso: <c>viewer.Window.PressKey(Keys.F);</c>
    /// </summary>
    public void PressKey(Keys key)
    {
        var hwnd = Resolve();
        if (hwnd == IntPtr.Zero) return;
        uint scan = Native.MapVirtualKey((uint)key, Native.MAPVK_VK_TO_VSC);
        Native.PostMessage(hwnd, Native.WM_KEYDOWN, (IntPtr)(int)key, (IntPtr)(1 | (scan << 16)));
        Native.PostMessage(hwnd, Native.WM_KEYUP, (IntPtr)(int)key, (IntPtr)(1 | (scan << 16) | (1 << 30) | (1u << 31)));
    }

    /// <summary>A janela do ffplay ainda tem título e borda do Windows? (Sai da tela cheia do ffplay com moldura de volta.)</summary>
    public bool HasFrame => Resolve() is var h && h != IntPtr.Zero && (Native.GetWindowLongPtr(h, Native.GWL_STYLE).ToInt64() & 0xC00000L) != 0;

    /// <summary>
    /// Tira (ou devolve) a moldura da janela já aberta. O ffplay aberto SEM -noborder escala a imagem quando o tamanho muda por fora
    /// mesmo depois disso (spike de 2026-11-07, spec 006); com -noborder a janela não aceita mudar de tamanho.
    /// Uso: <c>viewer.Window.SetFrame(false); viewer.Window.SetBounds(new Rectangle(1400, 780, 480, 270));</c>
    /// </summary>
    public void SetFrame(bool on)
    {
        var hwnd = Resolve();
        if (hwnd == IntPtr.Zero) return;
        long style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
        style = on ? style | Native.WS_FRAME_BITS : style & ~Native.WS_FRAME_BITS;
        Native.SetWindowLongPtr(hwnd, Native.GWL_STYLE, (IntPtr)style);
        Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
    }

    /// <summary>Move e redimensiona sem esperar a janela responder (síncrono custa ~38 ms por janela; assíncrono ~0,3 ms).</summary>
    public void SetBounds(Rectangle bounds)
    {
        var hwnd = Resolve();
        if (hwnd == IntPtr.Zero) return;
        Native.SetWindowPos(hwnd, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_ASYNCWINDOWPOS);
    }

    /// <summary>
    /// Faz a janela do ffplay DEPENDENTE de outra (some, minimiza e fica sempre por cima dela). Zero solta o vínculo. É o mesmo truque que a barra de
    /// controles já usa com o ffplay (o ffplay não aceita ser filho, spec 002).
    /// </summary>
    public void SetOwner(IntPtr owner)
    {
        var hwnd = Resolve();
        if (hwnd == IntPtr.Zero) return;
        Native.SetWindowLongPtr(hwnd, Native.GWLP_HWNDPARENT, owner);
    }

    public void MoveTo(Point topLeft)
    {
        var hwnd = Resolve();
        if (hwnd == IntPtr.Zero) return;
        Native.SetWindowPos(hwnd, IntPtr.Zero, topLeft.X, topLeft.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    public void SetTopMost(bool on)
    {
        var hwnd = Resolve();
        if (hwnd == IntPtr.Zero) return;
        var after = on ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST;
        Native.SetWindowPos(hwnd, after, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    // Se o player cair para a CPU o Viewer abre outro ffplay (outro PID e outra janela): por isso a janela é
    // reencontrada sempre que a antiga some, e não guardada para sempre.
    IntPtr Resolve()
    {
        if (_hwnd != IntPtr.Zero && Native.IsWindow(_hwnd) && OwnerIs(_hwnd, _processId())) return _hwnd;
        _hwnd = _processId() is int pid ? FindVisibleWindowOf(pid) : IntPtr.Zero;
        return _hwnd;
    }

    static bool OwnerIs(IntPtr hwnd, int? pid)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint owner);
        return pid != null && owner == pid;
    }

    static IntPtr FindVisibleWindowOf(int pid)
    {
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || !OwnerIs(h, pid)) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
