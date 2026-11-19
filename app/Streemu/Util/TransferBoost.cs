using System.Runtime.InteropServices;

namespace Streemu.Util;

/// <summary>
/// Alta prioridade enquanto um arquivo do modo filme está sendo enviado ou baixado, com a janela do app em foco ou não.
/// O Windows 11 trata um app minimizado ou coberto como "em segundo plano": ignora o <c>timeBeginPeriod(1)</c> dele (o relógio volta a acordar a cada ~15,6 ms) e
/// pode pô-lo no modo de eficiência (EcoQoS). O laço de envio do arquivo dorme <c>Thread.Sleep(1)</c> e manda no máximo um "balde" por passada; com 64 passadas por
/// segundo a vazão ficava em ~1 MB/s com a taxa em 2 MB/s e em ~8 MB/s com a taxa em 31 MB/s, qualquer que fosse a rede (medido em 2026-11-11: 316 MB a 55 MB/s com
/// a janela à vista; mais de 150 s, menos de 2 MB/s, com todas as janelas minimizadas).
/// Enquanto houver pelo menos um pedido aberto, o app avisa o Windows que NÃO quer nenhuma das duas coisas; ao fechar o último, volta ao automático do sistema.
/// Uso: <c>using var boost = TransferBoost.Request(); /* envia ou baixa */</c>
/// </summary>
public static class TransferBoost
{
    const int ProcessPowerThrottling = 4;
    const uint Version1 = 1, ExecutionSpeed = 0x1, IgnoreTimerResolution = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState state, int size);

    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

    static readonly object Gate = new();
    static int _open;

    /// <summary>Liga a prioridade (se ainda não está) e devolve o que a desliga quando o último pedido fecha. Chamar duas vezes é seguro.</summary>
    public static IDisposable Request()
    {
        lock (Gate) { if (_open++ == 0) Apply(optOut: true); }
        return new Release();
    }

    sealed class Release : IDisposable
    {
        int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            lock (Gate) { if (--_open == 0) Apply(optOut: false); }
        }
    }

    /// <summary>
    /// optOut = true: pede ao Windows para não limitar a velocidade do processo nem ignorar o relógio de 1 ms (controle ligado, estado desligado).
    /// optOut = false: devolve as duas escolhas ao sistema (controle desligado). Sem esse recurso (Windows antigo) não há o que fazer e nada quebra.
    /// </summary>
    static void Apply(bool optOut)
    {
        var state = new PowerThrottlingState { Version = Version1, ControlMask = optOut ? ExecutionSpeed | IgnoreTimerResolution : 0, StateMask = 0 };
        try
        {
            if (!SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>()))
                Log.Info($"[filme] o Windows não aceitou a alta prioridade da transferência (erro {Marshal.GetLastWin32Error()}); a velocidade pode cair com a janela minimizada.");
        }
        catch (EntryPointNotFoundException) { }
    }
}
