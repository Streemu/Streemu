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
    // convite de grupo (spec 018, seção 10): streemu://grupo/<código> e o link https://servidor/g/<código> entram no log como argumento de abertura, na
    // mensagem de erro de "entrar pelo link" etc. O código é a chave do grupo: quem o lê entra nele.
    [GeneratedRegex(@"(streemu://grupo/|/g/)[A-Za-z0-9-]{16,24}(?![A-Za-z0-9-])", RegexOptions.IgnoreCase)] private static partial Regex Convite();
    // Caminho de disco fora de C:\Users (D:\Filmes\x.mkv, E:\Jogos\...): o nome da pasta e do arquivo dizem o que a pessoa assiste e como organiza o PC.
    // Pasta com espaço entra inteira só quando vem outra barra depois ("Serie S01\ep1.mkv"); o último pedaço para no primeiro espaço, para o resto da linha
    // (código de erro, motivo) continuar no log (spec 018, D25). No texto de um JSON as barras vêm dobradas (\\). As pastas do sistema ficam: C:\Windows,
    // C:\Program Files, C:\ProgramData não identificam ninguém e explicam erro de driver e de instalação.
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?![Cc]:\\{1,2}(?:Windows|Program Files|ProgramData)(?![A-Za-z0-9]))[A-Za-z]:\\{1,2}(?:[^\\\s""'<>|*?;,]+(?: [^\\\s""'<>|*?;,]+)*\\{1,2})*[^\\\s""'<>|*?;,]*")] private static partial Regex CaminhoDeDisco();
    // Compartilhamento de rede \\servidor\pasta\arquivo (no texto de um JSON, \\\\servidor\\pasta), com o mesmo fim do caminho de disco.
    [GeneratedRegex(@"(?<![A-Za-z0-9\\.])\\{2,4}[A-Za-z0-9._$-]+\\{1,2}(?:[^\\\s""'<>|*?;,]+(?: [^\\\s""'<>|*?;,]+)*\\{1,2})*[^\\\s""'<>|*?;,]*")] private static partial Regex CaminhoDeRede();

    // Nome de arquivo de vídeo solto no texto (sem pasta): "Filme.Legal.2024.mkv", ou entre aspas com espaço ("Meu Filme.mp4"). O nome do arquivo da pessoa diz o que ela
    // assiste (spec 018): a origem do log não grava (modo filme), e esta é a segunda barreira para o que escapar. Só as extensões de vídeo: ffmpeg.exe, log.txt e afins ficam.
    // Sem aspas e com espaço não dá para saber onde o nome começa; aí vale a regra da origem. Aspas mantidas, para o texto de um JSON continuar JSON.
    [GeneratedRegex(@"([""'])[^""'\r\n]{1,200}?\.(?:mkv|mp4|avi|mov|wmv|webm|m4v|flv|mpg|mpeg|m2ts|ogv)\1", RegexOptions.IgnoreCase)] private static partial Regex VideoEntreAspas();
    [GeneratedRegex(@"(?<![A-Za-z0-9])[^\s\\/""'<>|*?;,:]*[^\s\\/""'<>|*?;,:.]\.(?:mkv|mp4|avi|mov|wmv|webm|m4v|flv|mpg|mpeg|m2ts|ogv)(?![A-Za-z0-9])", RegexOptions.IgnoreCase)] private static partial Regex VideoSolto();

    static readonly object _dispositivos = new();
    static Regex? _regexDispositivos;
    static readonly HashSet<string> _nomesDeDispositivo = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Nomes de dispositivo de áudio do PC (fone, microfone, caixa): eles já saíram do perfil, e aqui entram na limpeza para sumirem também de qualquer linha
    /// de log em que apareçam ("Fone do Bruno" diz de quem é). Quem lê a lista é o perfil, em segundo plano, via NAudio (não é WMI). Nomes de menos de 4
    /// letras não entram: trocariam pedaços de palavras comuns.
    /// </summary>
    public static void RegistrarDispositivos(IEnumerable<string?> nomes)
    {
        lock (_dispositivos)
            foreach (var n in nomes)
                if (n?.Trim() is { Length: >= 4 } t && _nomesDeDispositivo.Add(t)) _regexDispositivos = null;
    }

    /// <summary>Só os testes.</summary>
    public static void EsquecerDispositivos() { lock (_dispositivos) { _nomesDeDispositivo.Clear(); _regexDispositivos = null; } }

    static string TirarDispositivos(string texto)
    {
        Regex? r;
        lock (_dispositivos)
        {
            if (_nomesDeDispositivo.Count == 0) return texto;
            _regexDispositivos ??= new Regex(string.Join("|", _nomesDeDispositivo.OrderByDescending(n => n.Length).Select(Regex.Escape)), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            r = _regexDispositivos;
        }
        return r.Replace(texto, "<dispositivo-de-audio>");
    }

    /// <summary>Tira só os caminhos de disco (fora das pastas do sistema) e de rede do texto: o log local também usa, para não gravar "D:\Filmes\x.mkv".</summary>
    public static string SemCaminhos(string texto) => CaminhoDeDisco().Replace(CaminhoDeRede().Replace(texto, "<caminho>"), "<caminho>");

    /// <summary>Tira só o código de convite (<c>streemu://grupo/&lt;código&gt;</c> e <c>/g/&lt;código&gt;</c>) do texto.</summary>
    public static string SemConvite(string texto) => Convite().Replace(texto, "$1<codigo>");

    public static string Limpar(string texto)
    {
        if (Perfil.Length > 3) texto = texto.Replace(Perfil, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        texto = Usuarios().Replace(texto, "%USERPROFILE%");
        texto = Convite().Replace(texto, "$1<codigo>");
        texto = CaminhoDeRede().Replace(texto, "<caminho>");
        texto = CaminhoDeDisco().Replace(texto, "<caminho>");
        texto = VideoEntreAspas().Replace(texto, "$1<arquivo>$1");
        texto = VideoSolto().Replace(texto, "<arquivo>");
        texto = TirarDispositivos(texto);
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
