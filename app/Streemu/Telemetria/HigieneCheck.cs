namespace Streemu.Telemetria;

/// <summary>
/// Confere que MAC e GUID não saem do PC (regra 9), sem rede nem janela: --higiene-check.
/// Caso de 11/11: o netsh do Windows 11 26300 respondeu em UTF-8, o filtro pelo rótulo não reconheceu "Endereço físico" e o MAC subiu.
/// Grava higiene-check.txt ao lado do .exe; código de saída 0 = tudo certo. Roda no scripts/testes-locais.ps1.
/// </summary>
public static class HigieneCheck
{
    const string Netsh = "\r\nHá 1 interface no sistema:\r\n\r\n" +
        "    Nome                   : Wi-Fi\r\n" +
        "    Descrição              : Intel(R) Dual Band Wireless-AC 8265\r\n" +
        "    GUID                   : 3f2a6c1e-9b7d-4e21-8a55-0c1d2e3f4a5b\r\n" +
        "    Endereço físico        : 3c:6a:a7:12:34:56\r\n" +
        "    Estado                 : conectado\r\n" +
        "    SSID                   : CasaDoAmigo\r\n" +
        "    BSSID                  : a4-2b-b0-9c-de-f1\r\n" +
        "    Tipo de rádio          : 802.11ac\r\n" +
        "    Canal                  : 36\r\n" +
        "    Taxa de recepção (Mbps)  : 866.7\r\n" +
        "    Sinal                  : 92%\r\n" +
        "    Rótulo desconhecido    : 3C:6A:A7:12:34:57\r\n";

    static readonly string[] Proibidos = ["3c:6a:a7", "3C:6A:A7", "a4-2b-b0", "3f2a6c1e", "CasaDoAmigo"];

    public static int Run()
    {
        var linhas = new List<string>();
        int falhas = 0;
        void Conferir(string nome, bool ok, string detalhe)
        {
            linhas.Add((ok ? "OK    " : "FALHA ") + nome + (ok ? "" : " : " + detalhe));
            if (!ok) falhas++;
        }

        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        foreach (var (nome, cod) in new[] { ("utf8", (System.Text.Encoding)new System.Text.UTF8Encoding(false)), ("cp850", System.Text.Encoding.GetEncoding(850)) })
        {
            string json = Perfil.WifiDaSaida(cod.GetBytes(Netsh))?.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "";
            string? vazou = Proibidos.FirstOrDefault(p => json.Contains(p, StringComparison.OrdinalIgnoreCase));
            Conferir($"wifi-{nome}-sem-mac-guid-ssid", vazou == null, $"vazou {vazou}: {json}");
            Conferir($"wifi-{nome}-acentos", json.Contains("Tipo de rádio") && json.Contains("802.11ac"), json);
        }

        string log = "[12:34:56.789] wifi bssid=a4:2b:b0:9c:de:f1 placa 3C-6A-A7-12-34-56 driver 32.0.16.1742 ipv6 fe80::1";
        string limpo = Higiene.Limpar(log);
        Conferir("higiene-mascara-mac", !limpo.Contains("a4:2b", StringComparison.OrdinalIgnoreCase) && !limpo.Contains("3C-6A", StringComparison.OrdinalIgnoreCase)
            && limpo.Contains("<MAC>"), limpo);
        Conferir("higiene-nao-estraga-hora-versao", limpo.Contains("12:34:56.789") && limpo.Contains("32.0.16.1742") && limpo.Contains("fe80::1"), limpo);

        string contas = "Authorization: Bearer sa1.eyJjIjoiYy0xMjM0NTY3ODkwYWIifQ.abcDEF123_-x bilhete st1.20456.AbCdEf_-12 jwt eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ1aWQifQ.c2ln";
        string contasLimpo = Higiene.Limpar(contas);
        Conferir("higiene-mascara-token-bilhete-jwt", !contasLimpo.Contains("sa1.") && !contasLimpo.Contains("st1.") && !contasLimpo.Contains("eyJ") && !contasLimpo.Contains("AbCdEf"), contasLimpo);

        try { File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "higiene-check.txt"), linhas, new System.Text.UTF8Encoding(true)); } catch { }
        return falhas == 0 ? 0 : 1;
    }
}
