using System.Text.RegularExpressions;
using Streemu.Telemetria;

namespace Streemu.Media;

/// <summary>
/// O que o app lembra, entre aberturas, de que o Vulkan do ffplay não funciona nesta placa. Fica em <c>estado.json</c> (<c>AppState.VulkanBloqueio</c>).
/// <see cref="Placas"/> é a chave da placa com o driver (<see cref="VulkanMemoria.Chave"/>); <see cref="Falhas"/> conta as aberturas que falharam;
/// <see cref="Evitar"/> vale desde <see cref="DesdeUnixMs"/> (ms Unix, UTC) e acaba no reteste (<see cref="VulkanMemoria.Reteste"/>).
/// </summary>
public sealed record VulkanBloqueio(string Placas, int Falhas, long DesdeUnixMs, bool Evitar);

/// <summary>
/// O que o MainForm faz com uma falha do Vulkan (<see cref="VulkanMemoria.DecidirFalha"/>): a nova janela de 20 min (em <c>Environment.TickCount64</c>),
/// o registro a gravar, se a falha foi contada, se esta falha fez o app passar a evitar o Vulkan e se o registro mudou (e então vai para o disco).
/// </summary>
public sealed record DecisaoDeFalha(long JanelaAteTicks, VulkanBloqueio? Registro, bool Registrou, bool PassouAEvitar, bool Salvar);

/// <summary>
/// Regra pura (sem Win32, WMI nem janela) de quando o app deixa de tentar o Vulkan e abre a live direto pelo Direct3D 11.
/// Duas placas AMD RDNA3 (RX 7600 e RX 7800 XT, driver 32.0.32029.2010) falharam em 100% das aberturas do ffplay com
/// <c>VK_ERROR_OUT_OF_DEVICE_MEMORY</c> logo depois do primeiro quadro, com a placa livre (2026-11-19 a 24): é a alocação dos quadros da
/// decodificação, não VRAM cheia. Lembrar só 20 min na memória fazia cada live seguinte pagar 3-4 s e uma janela que fecha e reabre.
/// </summary>
public static class VulkanMemoria
{
    /// <summary>
    /// Falhas seguidas, na mesma placa e driver, para parar de tentar o Vulkan. Duas e não uma porque uma VRAM cheia de verdade (jogo pesado aberto,
    /// RTX 5070 nas DESCOBERTAS) também dá OUT_OF_DEVICE_MEMORY e não deve tirar o Vulkan de vez; e evitar por engano custa pouco: o Direct3D 11
    /// também decodifica na placa. A exceção é a assinatura da decodificação (<see cref="DecidirFalha"/>), que vale por duas.
    /// </summary>
    public const int FalhasParaEvitar = 2;

    /// <summary>Depois deste prazo o app testa o Vulkan de novo (o driver pode ter sido corrigido sem mudar a versão que o Windows mostra).</summary>
    public static readonly TimeSpan Reteste = TimeSpan.FromDays(30);

    /// <summary>Janela, em ms, em que uma falha explícita faz as lives novas abrirem sem o Vulkan (só na memória do app, nesta sessão).</summary>
    public const long JanelaMs = 20 * 60_000;

    /// <summary>
    /// Adaptadores que aparecem e somem sem a placa mudar (display virtual do Parsec, do spacedesk, do Citrix, o do Windows sem driver...). Se entrassem
    /// na chave, abrir ou fechar o Parsec trocaria a chave e o app esqueceria a falha da placa de verdade.
    /// </summary>
    static readonly string[] AdaptadoresVirtuais = { "Parsec", "Basic Display", "Basic Render", "Remote Display", "Virtual", "spacedesk", "Citrix", "IddSample" };

    /// <summary>
    /// A chave da placa: a lista de <c>SystemInfo.Gpus()</c> (cada item já é "Nome (driver X)"), sem os adaptadores virtuais, ordenada e juntada.
    /// Trocar de placa ou atualizar o driver muda a chave e o app volta a testar o Vulkan. Vazia quando não dá para saber (lista vazia, só adaptadores
    /// virtuais ou o WMI falhou): aí nada é lembrado.
    /// </summary>
    public static string Chave(IEnumerable<string> placas)
    {
        var lista = placas.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (lista.Count == 0 || lista.Any(p => p.StartsWith("desconhecida:", StringComparison.Ordinal))) return "";
        lista = lista.Where(p => !AdaptadoresVirtuais.Any(v => p.Contains(v, StringComparison.OrdinalIgnoreCase))).ToList();
        if (lista.Count == 0) return "";
        return string.Join(" + ", lista.OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>Verdadeiro só se o registro é destas mesmas placas e drivers, manda evitar e o reteste ainda não chegou.</summary>
    public static bool Evitar(VulkanBloqueio? registro, string placasAtuais, DateTimeOffset agora)
        => registro is { Evitar: true } && placasAtuais.Length > 0 && registro.Placas == placasAtuais && !RetesteVencido(registro, agora);

    /// <summary>
    /// Conta uma abertura que falhou e devolve o registro novo. Placa ou driver diferente do registro zera e começa em 1. Com
    /// <see cref="FalhasParaEvitar"/> falhas passa a evitar (ou já na primeira, se for a <paramref name="assinaturaDaDecodificacao"/>). Falha depois
    /// do reteste volta a evitar por mais 30 dias, sem precisar de duas de novo.
    /// </summary>
    public static VulkanBloqueio? RegistrarFalha(VulkanBloqueio? registro, string placasAtuais, DateTimeOffset agora, bool assinaturaDaDecodificacao = false)
    {
        if (placasAtuais.Length == 0) return registro;
        long agoraMs = agora.ToUnixTimeMilliseconds();
        if (registro == null || registro.Placas != placasAtuais)
            return Contar(placasAtuais, 1, agoraMs, assinaturaDaDecodificacao);
        if (registro.Evitar)
        {
            // já evitando e dentro do prazo: uma abertura que já estava em andamento falhou depois; não prolonga o prazo
            if (!RetesteVencido(registro, agora)) return registro;
            return registro with { Falhas = registro.Falhas + 1, DesdeUnixMs = agoraMs };
        }
        return Contar(placasAtuais, registro.Falhas + 1, agoraMs, assinaturaDaDecodificacao);
    }

    /// <summary>
    /// O Vulkan funcionou (vídeo confirmado e seguiu sem falha): apaga o registro. Um registro de outra placa já estava velho, então também some.
    /// </summary>
    public static VulkanBloqueio? RegistrarSucesso(VulkanBloqueio? registro, string placasAtuais) => null;

    /// <summary>
    /// A decisão inteira de uma falha do Vulkan (o MainForm só aplica o que volta daqui). A janela de 20 min vale para qualquer falha explícita; só
    /// memória e Vulkan (não "janela não abriu") entram na conta que vai para o disco, e só quem falha com a janela livre: a prévia da própria live
    /// e uma live de amigo falhando juntas, na mesma abertura, contam uma vez. A segunda falha, depois dos 20 min, conta de novo.
    /// <paramref name="decodificacao"/> é a assinatura das RDNA3 (<c>get_buffer() failed</c> logo depois da falta de memória, com a VRAM livre): é
    /// determinística, então a primeira falha já passa a evitar. VRAM cheia de verdade dá falta de memória no renderizador (<c>No slab to serve</c>),
    /// sem essa linha, e continua pedindo duas falhas.
    /// </summary>
    public static DecisaoDeFalha DecidirFalha(long janelaAteTicks, long agoraTicks, MotivoMidia motivo, bool decodificacao,
        VulkanBloqueio? registro, string placas, DateTimeOffset agora)
    {
        long novaJanela = agoraTicks + JanelaMs;
        if (agoraTicks < janelaAteTicks || motivo is not (MotivoMidia.SemMemoria or MotivoMidia.VulkanFalhou))
            return new DecisaoDeFalha(novaJanela, registro, Registrou: false, PassouAEvitar: false, Salvar: false);
        var depois = RegistrarFalha(registro, placas, agora, assinaturaDaDecodificacao: decodificacao && motivo == MotivoMidia.SemMemoria);
        return new DecisaoDeFalha(novaJanela, depois, Registrou: true,
            PassouAEvitar: depois is { Evitar: true } && registro is not { Evitar: true }, Salvar: depois != registro);
    }

    /// <summary>
    /// Este PC pode precisar do mpv (Direct3D 11) logo na primeira live? Sim se já falhou o Vulkan, na RTX série 50 (abre direto pelo Direct3D 11) ou numa
    /// AMD RX série 7000 (RDNA3, as duas placas que falharam). Aí o app baixa o mpv ao abrir, antes de qualquer live: na primeira falha de um PC o mpv
    /// de 47 MB terminou 1 s depois do prazo e a live ficou na CPU.
    /// </summary>
    public static bool PodePrecisarDoMpv(VulkanBloqueio? registro, bool rtx50, IEnumerable<string> placas)
        => registro != null || rtx50 || placas.Any(p => Regex.IsMatch(p, @"Radeon.*RX\s*7\d{3}", RegexOptions.IgnoreCase));

    static VulkanBloqueio Contar(string placas, int falhas, long agoraMs, bool assinatura) => new(placas, falhas, agoraMs, falhas >= FalhasParaEvitar || assinatura);

    static bool RetesteVencido(VulkanBloqueio registro, DateTimeOffset agora)
        => agora.ToUnixTimeMilliseconds() >= registro.DesdeUnixMs + (long)Reteste.TotalMilliseconds;
}
