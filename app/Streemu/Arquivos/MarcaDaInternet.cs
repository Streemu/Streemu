namespace Streemu.Arquivos;

/// <summary>
/// Marca o arquivo recebido como vindo da internet (a "Mark of the Web", o fluxo NTFS <c>Zone.Identifier</c> com <c>ZoneId=3</c>), a
/// mesma marca que o navegador põe. Com ela, o SmartScreen e o antivírus avisam ao abrir um .exe ou .bat recebido de um amigo (spec
/// 020, seção 3). Pasta em FAT32/exFAT (pendrive) não tem esse fluxo: a marca fica de fora e quem chama registra isso no log.
/// </summary>
public static class MarcaDaInternet
{
    const string Fluxo = ":Zone.Identifier";
    const string Conteudo = "[ZoneTransfer]\r\nZoneId=3\r\n";

    /// <summary>true se marcou; false se o disco não aceita a marca. Ex.: <c>MarcaDaInternet.Marcar(@"C:\Down\x.exe")</c>.</summary>
    public static bool Marcar(string caminho)
    {
        try
        {
            File.WriteAllText(caminho + Fluxo, Conteudo);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    /// <summary>true se o arquivo tem a marca da zona da internet (usado pelos testes e para conferir depois de marcar).</summary>
    public static bool Tem(string caminho)
    {
        try { return File.ReadAllText(caminho + Fluxo).Contains("ZoneId=3"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }
}
