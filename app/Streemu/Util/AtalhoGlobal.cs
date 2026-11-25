using System.Runtime.InteropServices;

namespace Streemu.Util;

/// <summary>
/// Atalho global de teclado que dispara uma ação na thread da tela, para agir sem tirar o jogo do foco. Registra ao construir e libera no <see cref="Dispose"/>:
/// quem usa só o mantém enquanto precisa (assim a combinação não fica presa o tempo todo). Se outro programa já tem a combinação, <see cref="Registrado"/>
/// fica falso e quem chama não promete o atalho ao usuário (mesmo critério de <see cref="Telemetria.AtalhoProblema"/> e <see cref="Ui.PlayerHotkey"/>).
/// Uso: <c>using var atalho = new AtalhoGlobal(0x72 /* F3 */, () => Aceitar());</c> (Ctrl+Alt+F3). Evite letras e números: Ctrl+Alt é o AltGr do ABNT2 e
/// Ctrl+Alt+3 digitaria "³" no chat do jogo. A ação roda adiada (fora do WndProc), então pode chamar o próprio <see cref="Dispose"/>.
/// </summary>
public sealed class AtalhoGlobal : NativeWindow, IDisposable
{
    const int ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000, WmHotkey = 0x312;
    static int _proximoId = 0x5C00;
    readonly int _id = Interlocked.Increment(ref _proximoId);
    readonly Action _acao;
    readonly SynchronizationContext? _tela = SynchronizationContext.Current;
    bool _liberado;

    public bool Registrado { get; }

    /// <param name="tecla">Código virtual da tecla (ex.: 0x72 para F3); o atalho é sempre Ctrl+Alt+tecla.</param>
    public AtalhoGlobal(int tecla, Action acao)
    {
        _acao = acao;
        CreateHandle(new CreateParams { Parent = (IntPtr)(-3) }); // janela só de mensagens, na thread da tela
        Registrado = RegisterHotKey(Handle, _id, ModControl | ModAlt | ModNoRepeat, tecla);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && (int)m.WParam == _id)
        {
            // adiada: a ação costuma chamar o Dispose daqui, e destruir o handle no meio do WndProc quebraria a mensagem em andamento
            void Chamar() { if (!_liberado) _acao(); }
            if (_tela != null) _tela.Post(_ => Chamar(), null); else Chamar();
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_liberado) return;
        _liberado = true;
        if (Registrado) UnregisterHotKey(Handle, _id);
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hwnd, int id, int modifiers, int vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
