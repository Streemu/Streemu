using System.ComponentModel;
using System.Runtime.InteropServices;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// Atalho global <c>Ctrl+Alt+M</c> para alternar a janela pequena do player SEM alt-tab, com o jogo em foco. Usa RegisterHotKey, que
/// consome a combinação (o jogo não recebe um M perdido) e não lê nenhuma outra tecla. O atalho só fica registrado enquanto existe
/// algum player aberto. Vários players abertos dividem o mesmo atalho: ele age no mais recente.
/// Combinações reservas: em 2026-11-10 a telemetria mostrou o Ctrl+Alt+M tomado por outro programa nos dois PCs com NVIDIA do grupo
/// (RegisterHotKey com erro 1409 a cada player aberto; no PC com AMD funcionou). Então o app tenta M, depois J, depois K, e diz UMA vez
/// qual ficou valendo. Depois que uma funcionou, ela é a primeira a ser tentada nas próximas aberturas do player, para o atalho não
/// trocar de tecla no meio do dia. Se nenhuma funcionar, avisa uma vez o motivo e a alternativa (o botão na barra).
/// Uso: <c>using var handle = PlayerHotkey.Attach(() =&gt; ToggleSmall(), aviso =&gt; Notify(aviso));</c>
/// </summary>
public sealed class PlayerHotkey : NativeWindow, IDisposable
{
    public const string Name = "Ctrl+Alt+M";
    const int HotkeyId = 0x4C58, ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000, WmHotkey = 0x312;
    static readonly IntPtr HwndMessage = (IntPtr)(-3);
    /// <summary>Em ordem de preferência. O B não entra: é o Ctrl+Alt+B de marcar problema (Telemetria/AtalhoProblema.cs).</summary>
    static readonly (int Vk, string Name)[] Combos = { (0x4D, Name), (0x4A, "Ctrl+Alt+J"), (0x4B, "Ctrl+Alt+K") };

    static PlayerHotkey? _shared;
    static readonly List<Action> Targets = new();
    static bool _warned;
    static int _preferred;

    /// <summary>A combinação registrada agora (nula se nenhuma deu).</summary>
    public static string? Current => _shared?.Active;

    /// <summary>A combinação que este registro conseguiu (nula se nenhuma).</summary>
    public string? Active { get; }

    /// <summary>As combinações que falharam e o código do Windows de cada uma (1409 = outro programa já registrou).</summary>
    public string Failures { get; } = "";

    PlayerHotkey()
    {
        CreateHandle(new CreateParams { Parent = HwndMessage }); // janela só para receber mensagens: nunca aparece
        var failures = new List<string>();
        foreach (int i in Enumerable.Range(0, Combos.Length).OrderBy(i => i == _preferred ? -1 : i))
        {
            if (RegisterHotKey(Handle, HotkeyId, ModControl | ModAlt | ModNoRepeat, Combos[i].Vk))
            {
                Active = Combos[i].Name;
                _preferred = i;
                break;
            }
            int code = Marshal.GetLastWin32Error();
            failures.Add($"{Combos[i].Name}: {new Win32Exception(code).Message} (código {code})");
        }
        Failures = string.Join("; ", failures);
    }

    /// <summary>Liga o atalho a este player. Quem chama guarda o retorno e o descarta ao fechar o player.</summary>
    public static IDisposable Attach(Action toggle, Action<string> warn)
    {
        _shared ??= new PlayerHotkey();
        Targets.Add(toggle);
        if (_shared.Active != Name && !_warned)
        {
            _warned = true;
            Log.Info($"[atalho] janela pequena: {_shared.Active ?? "nenhum atalho"}. Falharam: {_shared.Failures}");
            warn(_shared.Active != null
                ? $"Neste PC o {Name} já é de outro programa: para alternar a janela pequena com o jogo em foco, use {_shared.Active}."
                : $"Os atalhos {string.Join(", ", Combos.Select(c => c.Name))} já estão sendo usados por outros programas. Para alternar a janela pequena, use o botão na barra do player.");
        }
        return new Attachment(toggle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && (int)m.WParam == HotkeyId && Targets.Count > 0) Targets[^1]();
        base.WndProc(ref m);
    }

    static void Detach(Action toggle)
    {
        Targets.Remove(toggle);
        if (Targets.Count > 0 || _shared == null) return;
        _shared.Dispose();
        _shared = null;
    }

    public void Dispose()
    {
        if (Active != null) UnregisterHotKey(Handle, HotkeyId);
        DestroyHandle();
    }

    sealed class Attachment : IDisposable
    {
        readonly Action _toggle;
        bool _disposed;
        public Attachment(Action toggle) => _toggle = toggle;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Detach(_toggle);
        }
    }

    /// <summary>Devolve o primeiro plano a outra janela (usado logo depois do atalho, que o Windows conta como entrada do usuário).</summary>
    internal static void RestoreForeground(IntPtr hwnd) => SetForegroundWindow(hwnd);

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hwnd, int id, int modifiers, int vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
