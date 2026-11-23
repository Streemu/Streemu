using System.Runtime.InteropServices;

namespace Streemu.Util;

/// <summary>
/// Atalho global de teclado que dispara uma ação na thread da tela, para agir sem tirar o jogo do foco. Registra ao construir e libera no <see cref="Dispose"/>:
/// quem usa só o mantém enquanto precisa (assim a combinação não fica presa o tempo todo). Se outro programa já tem a combinação, <see cref="Registrado"/>
/// fica falso e quem chama não promete o atalho ao usuário (mesmo critério de <see cref="Telemetria.AtalhoProblema"/> e <see cref="Ui.PlayerHotkey"/>).
/// Uso: <c>using var atalho = new AtalhoGlobal(0x33 /* '3' */, () => Aceitar());</c> (Ctrl+Alt+3).
/// </summary>
public sealed class AtalhoGlobal : NativeWindow, IDisposable
{
    const int ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000, WmHotkey = 0x312;
    static int _proximoId = 0x5C00;
    readonly int _id = Interlocked.Increment(ref _proximoId);
    readonly Action _acao;

    public bool Registrado { get; }

    /// <param name="tecla">Código virtual da tecla (ex.: 0x33 para "3"); o atalho é sempre Ctrl+Alt+tecla.</param>
    public AtalhoGlobal(int tecla, Action acao)
    {
        _acao = acao;
        CreateHandle(new CreateParams { Parent = (IntPtr)(-3) }); // janela só de mensagens, na thread da tela
        Registrado = RegisterHotKey(Handle, _id, ModControl | ModAlt | ModNoRepeat, tecla);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && (int)m.WParam == _id) _acao();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Registrado) UnregisterHotKey(Handle, _id);
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hwnd, int id, int modifiers, int vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
