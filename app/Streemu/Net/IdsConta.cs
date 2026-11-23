using Streemu.Telemetria;
using Streemu.Util;

namespace Streemu.Net;

/// <summary>
/// Os ids do aparelho que o login e a renovação da conta mandam ao servidor (spec 029, seção 8.2), para o bloqueio por PC e por hardware.
/// São para segurança, não telemetria: vão mesmo com o diagnóstico desligado. Por isso o prefixo é próprio da conta (<c>streemu-conta-pc:</c> e
/// <c>streemu-conta-hw:</c>), partindo da mesma fonte que a telemetria usa (<see cref="Ids"/>): o valor é estável, mas não cruza com o <c>pc</c>/<c>hw</c>
/// dos relatórios de diagnóstico. Nada daqui vai a log, a telemetria nem ao estado.json (AGENTS.md, regra 9).
/// </summary>
public static class IdsConta
{
    static string? _pc;
    static volatile string? _hw;
    static int _iniciou;

    /// <summary>16 hex do PC. Lê o registro (ou o pc.txt) uma vez e guarda: rápido o bastante para o login esperar. Nulo se nada deu para ler.</summary>
    public static string? Pc
    {
        get
        {
            if (_pc != null) return _pc;
            try { return _pc = Ids.HashPcConta(Ids.FontePc()); }
            catch { return null; }
        }
    }

    /// <summary>16 hex do hardware, ou nulo enquanto o WMI não terminou (ou se o PC não tem partes boas o bastante). Nunca espera.</summary>
    public static string? Hw => _hw;

    /// <summary>O que o <see cref="Conta.IdsDoAparelho"/> usa: só o que já está em memória.</summary>
    public static (string? Pc, string? Hw) Atuais() => (Pc, _hw);

    /// <summary>
    /// Calcula o <c>hw</c> uma vez, em segundo plano e com prioridade baixa (o WMI leva até alguns segundos). Até acabar, o login e a renovação vão sem ele
    /// e o <c>hw</c> segue numa renovação posterior. A consulta é a mesma da telemetria (<see cref="Ids.PartesHardware"/>), que não a repete.
    /// </summary>
    public static void IniciarHardware()
    {
        if (Interlocked.Exchange(ref _iniciou, 1) == 1) return;
        var t = new Thread(() =>
        {
            try { _hw = Ids.HashHwConta(Ids.PartesHardware()); }
            catch (Exception e) { Log.Info("[conta] não consegui ler o hardware do aparelho: " + e.GetBaseException().Message); }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "conta-hw" };
        t.Start();
    }
}
