using System.Runtime.InteropServices;

namespace Streemu.Telemetria;

/// <summary>
/// Atalho global <c>Ctrl+Alt+B</c> ("bug") para marcar problema sem sair do jogo (spec 009, seção 5.6). O <c>Ctrl+Alt+M</c> já é da janela pequena
/// do player (<see cref="Ui.PlayerHotkey"/>). Se outro programa já tem a combinação, só fica a linha no log: a bandeira da barra lateral e a bandeja
/// continuam funcionando.
/// </summary>
public sealed class AtalhoProblema : NativeWindow, IDisposable
{
    public const string Nome = "Ctrl+Alt+B";
    const int Id = 0x4C59, ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000, VkB = 0x42, WmHotkey = 0x312;
    readonly Action _marcar;

    public bool Registrado { get; }

    public AtalhoProblema(Action marcar)
    {
        _marcar = marcar;
        CreateHandle(new CreateParams { Parent = (IntPtr)(-3) }); // janela só de mensagens, na thread da tela
        Registrado = RegisterHotKey(Handle, Id, ModControl | ModAlt | ModNoRepeat, VkB);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && (int)m.WParam == Id) _marcar();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Registrado) UnregisterHotKey(Handle, Id);
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hwnd, int id, int modifiers, int vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
