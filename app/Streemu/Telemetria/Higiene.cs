using System.Text.RegularExpressions;

namespace Streemu.Telemetria;

/// <summary>
/// Limpa o texto antes de sair do PC (spec 009, seção 8): o nome de usuário do Windows nos caminhos, o nome do PC, IP inteiro e MAC.
/// O log já mascara os IPs do túnel (<c>Tunnel.Mask</c>) e a senha do SRT (***); isto é a segunda barreira, para o que escapar.
/// </summary>
public static partial class Higiene
{
    static readonly string Perfil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
    static readonly string Maquina = Environment.MachineName;

    [GeneratedRegex(@"[A-Za-z]:\\Users\\[^\\/\s""':;|<>]+", RegexOptions.IgnoreCase)] private static partial Regex Usuarios();
    // IPv4 inteiro (cada parte até 255, sem dígito ou ponto colado dos lados): versões de driver como 32.0.16.1742 não batem (1742 > 255)
    [GeneratedRegex(@"(?<![\d.])((?:25[0-5]|2[0-4]\d|1?\d?\d)\.(?:25[0-5]|2[0-4]\d|1?\d?\d))\.(?:25[0-5]|2[0-4]\d|1?\d?\d)\.(?:25[0-5]|2[0-4]\d|1?\d?\d)(?![\d.]|\.x)")]
    private static partial Regex Ipv4();
    [GeneratedRegex(@"(passphrase|senha|password|credential|token)=([^&\s""]+)", RegexOptions.IgnoreCase)] private static partial Regex Segredos();
    // contas (spec 019): token de acesso (sa1.), bilhete do dia (st1.), cabeçalho Bearer e JWT do Firebase nunca saem do PC, nem num log que escape
    [GeneratedRegex(@"\b(?:sa1|st1)\.[A-Za-z0-9_.-]+")] private static partial Regex TokensDaConta();
    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase)] private static partial Regex Bearer();
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*")] private static partial Regex Jwt();
    // MAC/BSSID (aa:bb:cc:dd:ee:ff ou aa-bb-cc-dd-ee-ff): em 11/11 o perfil de um notebook mandou o MAC do Wi-Fi porque o netsh
    // respondeu em UTF-8 e o filtro pelo rótulo ("Endereço físico") não reconheceu o texto quebrado. Aqui cai qualquer um que escapar.
    [GeneratedRegex(@"(?<![0-9A-Fa-f:-])[0-9A-Fa-f]{2}([:-])[0-9A-Fa-f]{2}(?:\1[0-9A-Fa-f]{2}){4}(?![0-9A-Fa-f:-])")]
    public static partial Regex Mac();

    public static string Limpar(string texto)
    {
        if (Perfil.Length > 3) texto = texto.Replace(Perfil, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        texto = Usuarios().Replace(texto, "%USERPROFILE%");
        if (Maquina.Length >= 3) texto = Regex.Replace(texto, @"\b" + Regex.Escape(Maquina) + @"\b", "<PC>", RegexOptions.IgnoreCase);
        texto = Ipv4().Replace(texto, m => m.Groups[1].Value + ".x.x");
        texto = TokensDaConta().Replace(texto, "<token>");
        texto = Bearer().Replace(texto, "Bearer ***");
        texto = Jwt().Replace(texto, "<jwt>");
        texto = Segredos().Replace(texto, m => m.Groups[1].Value + "=***");
        texto = Mac().Replace(texto, "<MAC>");
        return texto;
    }
}
