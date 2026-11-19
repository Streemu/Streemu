using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// A barra de rolagem vertical com a cara do Streemu: um trilho da cor do fundo e um traço fino arredondado na cor da marca ,
/// no lugar da barra branca do Windows. Só pinta por cima da barra nativa (que continua mandando: arrastar, roda do mouse, teclado e acessibilidade
/// funcionam como sempre); se algo falhar, o que sobra é a barra de sempre.
/// Uso: <c>ScrollSkin.Attach(painelComAutoScroll);</c> (pode ser chamado antes de a janela existir).
/// </summary>
public sealed class ScrollSkin : NativeWindow
{
    const int WM_NCPAINT = 0x85, WM_PAINT = 0xF, WM_VSCROLL = 0x115, WM_MOUSEWHEEL = 0x20A, WM_PRINT = 0x317, WM_NCLBUTTONDOWN = 0xA1, WM_MOUSEMOVE = 0x200, WM_LBUTTONUP = 0x202, WM_CAPTURECHANGED = 0x215;
    const int SB_VERT = 1, SB_LINEUP = 0, SB_LINEDOWN = 1, SB_PAGEUP = 2, SB_PAGEDOWN = 3, SB_THUMBTRACK = 5, SB_ENDSCROLL = 8, SIF_ALL = 0x1F;
    const int HTVSCROLL = 7, PRF_NONCLIENT = 2;
    const int OBJID_VSCROLL = unchecked((int)0xFFFFFFFB), STATE_SYSTEM_INVISIBLE = 0x8000, STATE_SYSTEM_UNAVAILABLE = 1;

    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct ScrollBarInfo
    {
        public int cbSize; public Rect rcScrollBar; public int dxyLineButton, xyThumbTop, xyThumbBottom, reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public int[] rgstate;
    }
    [StructLayout(LayoutKind.Sequential)] struct ScrollInfo { public int cbSize, fMask, nMin, nMax, nPage, nPos, nTrackPos; }

    [DllImport("user32.dll")] static extern bool GetScrollBarInfo(IntPtr hwnd, int idObject, ref ScrollBarInfo info);
    [DllImport("user32.dll")] static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref ScrollInfo info);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern IntPtr GetWindowDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, ScrollSkin> Attached = new();
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Form, ActivationSkin> Forms = new();
    readonly Control _control;
    readonly List<Control> _parents = new();
    /// <summary>Arrasto do traço em andamento (nulo = parado): posição do mouse e da rolagem ao começar, e o quanto de rolagem cabe no trilho.</summary>
    (int StartY, int StartPos, int FreeTrack, int MaxPos)? _drag;

    public static void Attach(Control control)
    {
        if (Attached.TryGetValue(control, out _)) return;
        Attached.Add(control, new ScrollSkin(control));
    }

    ScrollSkin(Control control)
    {
        _control = control;
        control.HandleCreated += (_, _) => Adopt(control.Handle);
        control.HandleDestroyed += (_, _) => ReleaseHandle();
        control.Disposed += (_, _) => { foreach (var parent in _parents) parent.ParentChanged -= ParentMoved; _parents.Clear(); };
        control.ParentChanged += (_, _) => BindActivation();
        BindActivation();
        if (control.IsHandleCreated) Adopt(control.Handle);
    }

    /// <summary>
    /// Tira o tema do Windows da barra (a clássica não tem animação de realce nem botões que aparecem ao passar o mouse): o Windows só a repinta quando manda
    /// WM_NCPAINT, e aí o traço do Streemu é pintado logo depois, na mesma mensagem. Com o tema ligado ele repintava sozinho, em outro ritmo, e a barra branca piscava por cima.
    /// </summary>
    void Adopt(IntPtr handle)
    {
        AssignHandle(handle);
        _ = SetWindowTheme(handle, "", "");
        BindActivation();
    }

    void BindActivation()
    {
        foreach (var parent in _parents) parent.ParentChanged -= ParentMoved;
        _parents.Clear();
        for (Control? parent = _control.Parent; parent != null; parent = parent.Parent)
        {
            _parents.Add(parent);
            parent.ParentChanged += ParentMoved;
        }
        if (_control.FindForm() is { } form) _ = Forms.GetValue(form, f => new ActivationSkin(f));
    }
    void ParentMoved(object? sender, EventArgs e) => BindActivation();

    /// <summary>
    /// Ativação é uma mensagem da JANELA, não dos painéis. O Windows redesenha as barras dos filhos ao ativar/desativar;
    /// por isso observar só WM_NCPAINT nos painéis deixava a barra branca até o próximo movimento do mouse.
    /// Uma pintura adiada termina depois da ativação nativa; não fica nenhum timer rodando em repouso.
    /// </summary>
    sealed class ActivationSkin : NativeWindow
    {
        readonly Form _form;
        bool _pending;
        public ActivationSkin(Form form)
        {
            _form = form;
            form.HandleCreated += (_, _) => AssignHandle(form.Handle);
            form.HandleDestroyed += (_, _) => { _pending = false; ReleaseHandle(); };
            if (form.IsHandleCreated) AssignHandle(form.Handle);
        }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg is not (0x86 or 0x6 or 0x1C or 0x31A) || _pending || !_form.IsHandleCreated || _form.IsDisposed) return;
            _pending = true;
            _form.BeginInvoke(() =>
            {
                _pending = false;
                if (!_form.IsDisposed) Repaint(_form);
            });
        }
        static void Repaint(Control root)
        {
            if (root.IsDisposed || !root.IsHandleCreated || !root.Visible) return;
            if (Attached.TryGetValue(root, out var skin))
            {
                try { skin.Paint(IntPtr.Zero); }
                catch (Exception e) { Log.Info($"[rolagem] não consegui repintar após ativação: {e.Message}"); }
            }
            foreach (Control child in root.Controls) Repaint(child);
        }
    }

    protected override void WndProc(ref Message m)
    {
        try { if (HandleBarMouse(ref m)) return; }
        catch (Exception e) { Log.Info($"[rolagem] não consegui tratar o mouse na barra: {e.Message}"); _drag = null; }
        base.WndProc(ref m);
        try { AfterNative(ref m); }
        catch (Exception e) { Log.Info($"[rolagem] não consegui pintar a barra: {e.Message}"); }
    }

    void AfterNative(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_NCPAINT: case WM_PAINT: case WM_VSCROLL: case WM_MOUSEWHEEL: Paint(IntPtr.Zero); break;
            case WM_PRINT when ((long)m.LParam & PRF_NONCLIENT) != 0: Paint(m.WParam); break; // DrawToBitmap (as fotos dos testes) pede a janela inteira
        }
    }

    /// <summary>
    /// Clicar e arrastar na barra deixava o Windows num laço próprio que pinta a barra (traço branco) e a página (cópias fantasma dos controles) por conta dele.
    /// Aqui o clique na barra vira as mesmas mensagens de rolagem que ele mandaria (linha, página, arrastar), sem o laço: a página rola como na roda do mouse.
    /// Devolve verdadeiro quando a mensagem foi tratada e não deve ir ao Windows.
    /// </summary>
    bool HandleBarMouse(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_NCLBUTTONDOWN when m.WParam.ToInt32() == HTVSCROLL: return PressBar((int)(short)(((long)m.LParam >> 16) & 0xFFFF));
            case WM_MOUSEMOVE when _drag is { } drag: DragTo(drag, Cursor.Position.Y); return true;
            case WM_LBUTTONUP when _drag != null: EndDrag(); return true;
            case WM_CAPTURECHANGED when _drag != null: EndDrag(); return false;
            default: return false;
        }
    }

    bool PressBar(int screenY)
    {
        var info = new ScrollBarInfo { cbSize = Marshal.SizeOf<ScrollBarInfo>(), rgstate = new int[6] };
        var scroll = new ScrollInfo { cbSize = Marshal.SizeOf<ScrollInfo>(), fMask = SIF_ALL };
        if (!GetScrollBarInfo(Handle, OBJID_VSCROLL, ref info) || !GetScrollInfo(Handle, SB_VERT, ref scroll)) return false;
        if ((info.rgstate[0] & (STATE_SYSTEM_INVISIBLE | STATE_SYSTEM_UNAVAILABLE)) != 0) return false;
        int top = info.rcScrollBar.Top, thumbTop = top + info.xyThumbTop, thumbBottom = top + info.xyThumbBottom, button = info.dxyLineButton;
        if (screenY < top + button) SendScroll(SB_LINEUP);
        else if (screenY >= info.rcScrollBar.Bottom - button) SendScroll(SB_LINEDOWN);
        else if (screenY < thumbTop) SendScroll(SB_PAGEUP);
        else if (screenY >= thumbBottom) SendScroll(SB_PAGEDOWN);
        else BeginDrag(screenY, scroll, info.rcScrollBar.Bottom - top - 2 * button - (thumbBottom - thumbTop));
        if (_drag == null) SendScroll(SB_ENDSCROLL);
        return true;
    }

    void BeginDrag(int screenY, ScrollInfo scroll, int freeTrack)
    {
        _drag = (screenY, scroll.nPos, Math.Max(1, freeTrack), Math.Max(0, scroll.nMax - scroll.nMin + 1 - scroll.nPage));
        _control.Capture = true; // daqui o mouse vem para cá, mesmo fora da barra, até soltar
    }

    void DragTo((int StartY, int StartPos, int FreeTrack, int MaxPos) drag, int screenY)
    {
        int pos = Math.Clamp(drag.StartPos + (int)((long)(screenY - drag.StartY) * drag.MaxPos / drag.FreeTrack), 0, drag.MaxPos);
        // o WinForms lê a posição do arrasto de um valor que só o laço nativo preenche (nTrackPos), por isso o SB_THUMBTRACK mandado daqui não rolava: a posição vai direto na propriedade
        if (_control is ScrollableControl scrollable) scrollable.VerticalScroll.Value = Math.Clamp(pos, scrollable.VerticalScroll.Minimum, scrollable.VerticalScroll.Maximum);
        Paint(IntPtr.Zero);
    }

    void EndDrag()
    {
        _drag = null;
        if (_control.Capture) _control.Capture = false;
        SendScroll(SB_ENDSCROLL);
    }

    void SendScroll(int code, int position = 0) => SendMessage(Handle, WM_VSCROLL, (IntPtr)(code | (position << 16)), IntPtr.Zero);

    /// <summary>Pinta o trilho e o traço por cima da barra nativa. <paramref name="printDc"/> vem do WM_PRINT; nulo, usa o contexto da janela.</summary>
    void Paint(IntPtr printDc)
    {
        var info = new ScrollBarInfo { cbSize = Marshal.SizeOf<ScrollBarInfo>(), rgstate = new int[6] };
        if (!GetScrollBarInfo(Handle, OBJID_VSCROLL, ref info) || (info.rgstate[0] & STATE_SYSTEM_INVISIBLE) != 0) return;
        GetWindowRect(Handle, out var window);
        var bar = Rectangle.FromLTRB(info.rcScrollBar.Left - window.Left, info.rcScrollBar.Top - window.Top, info.rcScrollBar.Right - window.Left, info.rcScrollBar.Bottom - window.Top);
        if (bar.Width <= 0 || bar.Height <= 0) return;
        IntPtr dc = printDc != IntPtr.Zero ? printDc : GetWindowDC(Handle);
        try
        {
            using var g = Graphics.FromHdc(dc);
            g.SetClip(bar);
            using (var track = new SolidBrush(_control.BackColor)) g.FillRectangle(track, bar);
            if ((info.rgstate[0] & STATE_SYSTEM_UNAVAILABLE) != 0) return;
            DrawThumb(g, bar, info.xyThumbTop, info.xyThumbBottom);
        }
        finally { if (printDc == IntPtr.Zero) ReleaseDC(Handle, dc); }
    }

    void DrawThumb(Graphics g, Rectangle bar, int top, int bottom)
    {
        int width = Math.Max(6, bar.Width * 38 / 100);
        var thumb = new Rectangle(bar.X + (bar.Width - width) / 2, bar.Y + top, width, Math.Max(width, bottom - top));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.RoundedPath(thumb, width / 2);
        using var fill = new SolidBrush(Theme.ScrollThumb); // uma cor só: mudar de cor ao passar o mouse piscava e dava mais um ponto de pintura que podia quebrar o layout
        g.FillPath(fill, path);
    }
}
