using System.Runtime.InteropServices;

namespace Streemu.Util;

/// <summary>
/// Segura a pintura de uma janela enquanto ela troca de tela e pinta tudo de uma vez no fim. Sem isso, cada controle que aparece, some ou muda de
/// lugar se pinta na hora, e quem olha via ~0,3 s de pedaços da tela anterior misturados com a nova (vídeo de 2026-11-16).
/// Uso: <c>using (new PinturaPausada(this)) { painelA.Visible = false; painelB.Visible = true; PerformLayout(); }</c>
/// </summary>
internal sealed class PinturaPausada : IDisposable
{
    const int WmSetRedraw = 0x000B;
    const uint RdwInvalidate = 0x1, RdwErase = 0x4, RdwAllChildren = 0x80, RdwUpdateNow = 0x100, RdwFrame = 0x400;
    readonly Control? _control;

    public PinturaPausada(Control control)
    {
        if (!control.IsHandleCreated || !control.Visible) return; // sem janela à vista não há o que segurar
        _control = control;
        SendMessage(control.Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_control is not { IsDisposed: false, IsHandleCreated: true } control) return;
        SendMessage(control.Handle, WmSetRedraw, (IntPtr)1, IntPtr.Zero);
        RedrawWindow(control.Handle, IntPtr.Zero, IntPtr.Zero, RdwInvalidate | RdwErase | RdwFrame | RdwAllChildren | RdwUpdateNow);
    }

    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hWnd, IntPtr update, IntPtr region, uint flags);
}
