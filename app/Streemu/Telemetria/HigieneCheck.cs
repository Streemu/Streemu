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
            // spec 018, A3: só a banda e o sinal em %; nem o tipo de rádio, nem o canal, nem a placa, nem a taxa sobem
            Conferir($"wifi-{nome}-so-banda-e-sinal", json.Contains("\"banda\"") && json.Contains("\"sinal_pct\":92") && !json.Contains("802.11") && !json.Contains("Intel")
                && !json.Contains("866") && !json.Contains("rádio"), json);
        }

        string log = "[12:34:56.789] wifi bssid=a4:2b:b0:9c:de:f1 placa 3C-6A-A7-12-34-56 driver 32.0.16.1742 ipv6 fe80::1";
        string limpo = Higiene.Limpar(log);
        Conferir("higiene-mascara-mac", !limpo.Contains("a4:2b", StringComparison.OrdinalIgnoreCase) && !limpo.Contains("3C-6A", StringComparison.OrdinalIgnoreCase)
            && limpo.Contains("<MAC>"), limpo);
        Conferir("higiene-nao-estraga-hora-versao", limpo.Contains("12:34:56.789") && limpo.Contains("32.0.16.1742") && limpo.Contains("fe80::1"), limpo);

        string contas = "Authorization: Bearer sa1.eyJjIjoiYy0xMjM0NTY3ODkwYWIifQ.abcDEF123_-x bilhete st1.20456.AbCdEf_-12 jwt eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ1aWQifQ.c2ln";
        string contasLimpo = Higiene.Limpar(contas);
        Conferir("higiene-mascara-token-bilhete-jwt", !contasLimpo.Contains("sa1.") && !contasLimpo.Contains("st1.") && !contasLimpo.Contains("eyJ") && !contasLimpo.Contains("AbCdEf"), contasLimpo);

        // spec 018, A4 (N16): o que identifica o que a pessoa assiste, onde guarda e quem convidou não sobe, e o que explica o erro continua
        string convite = "Args: streemu://grupo/AbCdEf0123456789XyZw e https://streemu.exemplo/g/AbCdEf0123456789XyZw";
        string conviteLimpo = Higiene.Limpar(convite);
        Conferir("higiene-tira-convite", !conviteLimpo.Contains("AbCdEf0123", StringComparison.Ordinal) && conviteLimpo.Contains("<codigo>"), conviteLimpo);
        string discos = "abriu D:\\Filmes\\Serie S01\\ep1.mkv e \\\\NAS\\videos\\x.mkv; erro 887a0026 em C:\\Windows\\System32\\x.dll";
        string discosLimpo = Higiene.Limpar(discos);
        Conferir("higiene-tira-caminho-de-disco-e-de-rede", !discosLimpo.Contains("Filmes") && !discosLimpo.Contains("NAS") && discosLimpo.Contains("<caminho>")
            && discosLimpo.Contains("887a0026") && discosLimpo.Contains("C:\\Windows\\System32\\x.dll"), discosLimpo); // o resto da linha de erro fica
        string meio = Higiene.Limpar("[aviso] abriu D:\\Jogos\\x.exe e falhou 887a0026 ao capturar");
        Conferir("higiene-caminho-no-meio-mantem-o-erro", meio.Contains("<caminho> e falhou 887a0026 ao capturar") && !meio.Contains("Jogos"), meio);
        Conferir("higiene-mantem-pasta-do-sistema", Higiene.Limpar("falhou em C:\\Windows\\System32\\x.dll").Contains("C:\\Windows\\System32\\x.dll"), "");
        // 2026-11-26: o nome do arquivo de filme (com a extensão) apareceu no log do modo filme; o log não grava mais, e a higiene é a segunda barreira
        string video = Higiene.Limpar("[filme] mpv aberto em 300 ms: Meu.Filme.Secreto.2024.mkv e \"Outro Filme Secreto.mp4\" e {\"nome\":\"Serie.S01E02.avi\"} com ffmpeg.exe e log.txt");
        Conferir("higiene-tira-nome-de-video-solto", !video.Contains("Secreto") && !video.Contains("Serie.") && video.Contains("<arquivo>") && video.Contains("{\"nome\":\"<arquivo>\"}")
            && video.Contains("ffmpeg.exe") && video.Contains("log.txt") && video.Contains("mpv aberto em 300 ms"), video);
        Higiene.RegistrarDispositivos(["Fone do Bruno", "Alto-falantes (Realtek Audio)"]);
        string audio = Higiene.Limpar("saida: Fone do Bruno; entrada Alto-falantes (Realtek Audio) falhou");
        Higiene.EsquecerDispositivos();
        Conferir("higiene-tira-dispositivo-de-audio", !audio.Contains("Bruno") && !audio.Contains("Realtek") && audio.Contains("<dispositivo-de-audio>"), audio);

        string ffmpeg = Media.Tools.ForLog("[error] Error opening input file 'D:\\Filmes\\x.mkv'.");
        Conferir("ffmpeg-tira-arquivo", !ffmpeg.Contains("Filmes") && ffmpeg.Contains("input file"), ffmpeg);
        string meta = Media.Tools.ForLog("    title           : Nome do Filme Secreto");
        Conferir("ffmpeg-tira-titulo-do-arquivo", !meta.Contains("Secreto") && meta.Contains("title"), meta);
        string codigo = Media.Tools.ForLog("[error] decoder falhou 887a0026 em D:\\Filmes\\x.mkv");
        Conferir("ffmpeg-mantem-codigo-de-erro", codigo.Contains("887a0026"), codigo);
        string srt = Media.Tools.ForLog("[warning] SRT RCV-DROPPED 12 pacotes");
        Conferir("ffmpeg-mantem-srt-rcv-dropped", srt.Contains("SRT RCV-DROPPED"), srt);
        string args = Media.Tools.ArgsParaLog(["-f", "gdigrab", "-i", "title=Jogo Secreto", "-i", "D:\\Filmes\\x.mkv", "-metadata", "title=Meu Filme", "-f", "mpegts", "srt://127.0.0.1:9000?mode=caller"]);
        Conferir("ffmpeg-args-sem-janela-arquivo-titulo", !args.Contains("Secreto") && !args.Contains("Filmes") && !args.Contains("Meu Filme") && args.Contains("srt://127.0.0.1:9000") && args.Contains("gdigrab"), args);

        try { File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "higiene-check.txt"), linhas, new System.Text.UTF8Encoding(true)); } catch { }
        return falhas == 0 ? 0 : 1;
    }
}
