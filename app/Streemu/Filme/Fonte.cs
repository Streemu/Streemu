using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Streemu.Filme;

/// <summary>Como o filme sai para quem assiste pela transmissão (spec 011, seção 5): cópia direta (sem perda), Padrão (1080p, ~6 Mbps) ou Leve (720p, ~3 Mbps).</summary>
public enum PerfilStream { Copia, Padrao, Leve }

/// <summary>O que o ffmpeg diz do arquivo (só leitura): vídeo, áudios, legendas, duração e bitrate. Nada daqui vai para a rede nem para o git.</summary>
public sealed record FonteInfo(string Caminho, double DuracaoS, string Video, int Largura, int Altura, double Mbps, bool Hdr, string[] Audios, string[] Legendas, int Capitulos)
{
    public string Resumo => $"{Largura}x{Altura} {Video}{(Hdr ? " HDR" : "")} · {Mbps:0.#} Mbps · {string.Join("/", Audios.Take(2))}";
}

/// <summary>A faixa de áudio do envio: o "-map" do ffmpeg (ex.: <c>0:3</c> ou <c>0:a:1</c>) e o codec (decide se o áudio vai em cópia direta).</summary>
public sealed record FaixaAudio(string Map, string? Codec, string Descricao);

/// <summary>
/// Lê o arquivo do filme e escolhe como mandá-lo (spec 011, T1). O pacote do app não traz o ffprobe (só ffmpeg e ffplay), então a análise lê a saída do
/// <c>ffmpeg -i</c>. Cuidado com as linhas de faixa: o .mkv escreve <c>Stream #0:0: Video:</c> e o .mp4 escreve <c>Stream #0:0[0x1](und): Video:</c>; um padrão só
/// com o primeiro dava "sem vídeo" no .mp4 (spike C6, 2026-11-11).
/// Uso: <c>var info = Fonte.Analisar(Tools.Ffmpeg, arquivo); var perfil = Fonte.Escolher(info, 2, 30);</c>
/// </summary>
public static partial class Fonte
{
    /// <summary>Codecs de áudio que passam para o mpegts sem recompressão e tocam no mpv (medido nos 5 filmes do spike C6).</summary>
    static readonly HashSet<string> AudioDireto = new(StringComparer.OrdinalIgnoreCase) { "aac", "ac3", "eac3", "mp3", "mp2", "dts" };
    static readonly HashSet<string> VideoDireto = new(StringComparer.OrdinalIgnoreCase) { "h264", "hevc" };

    [GeneratedRegex(@"Duration: (\d+):(\d+):(\d+(?:\.\d+)?).*?bitrate: (\d+) kb/s")] private static partial Regex DuracaoRegex();
    [GeneratedRegex(@"Stream #0:\d+(?:\[0x[0-9a-fA-F]+\])?(?:\([a-z]+\))?: (Video|Audio|Subtitle): ([A-Za-z0-9_]+)([^\r\n]*)")] private static partial Regex FaixaRegex();
    [GeneratedRegex(@"[ ,\[](\d{3,5})x(\d{3,5})[ ,\[]")] private static partial Regex ResolucaoRegex();

    /// <summary>Lê o arquivo. Falha com o motivo (arquivo sem vídeo, ilegível) para a tela poder dizer o que fazer.</summary>
    public static FonteInfo Analisar(string ffmpeg, string arquivo)
    {
        if (!File.Exists(arquivo)) throw new FileNotFoundException($"o arquivo do filme não existe mais: {arquivo}");
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-hide_banner", "-i", arquivo }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("o ffmpeg não abriu para ler o arquivo");
        var erro = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } throw new TimeoutException("o ffmpeg demorou mais de 20 s para ler o arquivo (disco lento ou arquivo corrompido)"); }
        return Ler(arquivo, erro.GetAwaiter().GetResult());
    }

    /// <summary>A leitura em si, separada para poder ser conferida com texto de exemplo.</summary>
    internal static FonteInfo Ler(string arquivo, string saidaFfmpeg)
    {
        var d = DuracaoRegex().Match(saidaFfmpeg);
        double dur = d.Success ? int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60 + double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        double mbps = d.Success ? int.Parse(d.Groups[4].Value) / 1000.0 : 0;
        string video = "", resto = ""; var audios = new List<string>(); var legendas = new List<string>();
        foreach (Match m in FaixaRegex().Matches(saidaFfmpeg))
        {
            string tipo = m.Groups[1].Value, codec = m.Groups[2].Value;
            if (tipo == "Video" && video == "" && !m.Groups[3].Value.Contains("attached pic")) { video = codec; resto = m.Groups[3].Value; }
            else if (tipo == "Audio") audios.Add(codec);
            else if (tipo == "Subtitle") legendas.Add(codec);
        }
        if (video == "") throw new InvalidDataException("esse arquivo não tem imagem (só áudio) ou o ffmpeg não conseguiu ler o vídeo dele");
        var r = ResolucaoRegex().Match(resto + " ");
        bool hdr = resto.Contains("smpte2084") || resto.Contains("arib-std-b67");
        return new FonteInfo(arquivo, dur, video, r.Success ? int.Parse(r.Groups[1].Value) : 0, r.Success ? int.Parse(r.Groups[2].Value) : 0, mbps, hdr,
            audios.ToArray(), legendas.ToArray(), Regex.Matches(saidaFfmpeg, @"Chapter #").Count);
    }

    /// <summary>
    /// Cópia direta só quando cabe: bitrate × espectadores até ~70% do upload e codecs que o mpv toca direto. O HDR passa inteiro na cópia (o mpv converte para a tela de quem
    /// assiste). Fora disso, o perfil Padrão; o Leve é para upload curto ou para quem está no relay (spec 011, seção 5).
    /// </summary>
    public static PerfilStream Escolher(FonteInfo f, int espectadores, double uploadMbps, bool alguemNoRelay = false, int nucleos = 0)
    {
        if (alguemNoRelay || uploadMbps <= 0 || uploadMbps * 0.7 < 3 * Math.Max(1, espectadores)) return PerfilStream.Leve;
        bool cabe = f.Mbps > 0 && f.Mbps * Math.Max(1, espectadores) <= uploadMbps * 0.7;
        if (cabe && VideoDireto.Contains(f.Video)) return PerfilStream.Copia;
        // caso de 11/11: um i5-8250U (8 threads, 15 W) recomprimindo um 4K HDR no Padrão andava a ~0,3x do tempo real (estimado). O tone-map em ponto flutuante é o
        // que pesa; acima de 1080p com HDR e até 8 threads, começa no Leve. Se mesmo assim ficar lento, o EnvioStream avisa (Lento) e o MainForm cai de perfil.
        if (f.Hdr && f.Altura > 1200 && nucleos > 0 && nucleos <= 8) return PerfilStream.Leve;
        return PerfilStream.Padrao;
    }

    /// <summary>
    /// Decodificar a fonte na placa de vídeo nos perfis que recomprimem (Padrão e Leve): no 4K HEVC do notebook de 11/11 a CPU decodificava, convertia o HDR e
    /// comprimia ao mesmo tempo. Desliga sozinho se o ffmpeg não abrir com a placa (o EnvioStream tenta de novo sem) ou com STREEMU_FILME_PLACA=0.
    /// </summary>
    public static volatile bool Placa = Environment.GetEnvironmentVariable("STREEMU_FILME_PLACA") != "0";

    /// <summary>
    /// O que o ffmpeg lê do arquivo e escreve em mpegts no stdout, a partir de <paramref name="inicioS"/>. <paramref name="audio"/> é a faixa que o mpv do anfitrião está
    /// tocando (sem ela, a primeira do arquivo).
    /// </summary>
    public static List<string> ArgsEnvio(FonteInfo f, PerfilStream perfil, double inicioS, FaixaAudio? audio = null)
    {
        // -stats a cada 2 s: o EnvioStream lê o "time=" para saber se o envio acompanha o tempo real (a linha começa com "frame=" e não vai para o log)
        // sem -re: o ritmo é do EnvioStream, que lê o stdout adiantado ao filme e segura a leitura quando já mandou o bastante (buffer de quem assiste, teste de 11/11)
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-stats", "-stats_period", "2" };
        bool naPlaca = perfil != PerfilStream.Copia && ConversaoPlaca.Ligada;
        if (naPlaca) a.AddRange(ConversaoPlaca.Entrada());
        // "auto": o ffmpeg tenta d3d11va, dxva2... e decodifica na CPU se nenhum servir; os quadros voltam para a memória, onde os filtros (scale, tone-map) rodam
        else if (perfil != PerfilStream.Copia && Placa) a.AddRange(new[] { "-hwaccel", "auto" });
        a.AddRange(new[] { "-ss", inicioS.ToString("0.###", CultureInfo.InvariantCulture), "-i", f.Caminho, "-map", "0:v:0", "-map", audio?.Map ?? "0:a:0?", "-sn", "-dn" });
        if (perfil == PerfilStream.Copia) a.AddRange(new[] { "-c:v", "copy" });
        else if (naPlaca) a.AddRange(ConversaoPlaca.Video(AltoDe(perfil), KbpsDe(perfil)));
        else a.AddRange(VideoArgs(f, perfil));
        string? codecAudio = audio?.Codec ?? f.Audios.FirstOrDefault();
        bool audioDireto = codecAudio != null && AudioDireto.Contains(codecAudio) && perfil == PerfilStream.Copia;
        a.AddRange(audioDireto ? new[] { "-c:a", "copy" } : new[] { "-c:a", "aac", "-b:a", perfil == PerfilStream.Leve ? "128k" : "192k" });
        // os carimbos saem no tempo do filme (o -ss zera, o offset devolve): o time-pos do mpv de quem assiste (--rebase-start-time=no) é a posição do filme, e o
        // EnvioStream sabe até que ponto já mandou. Na cópia direta o primeiro quadro é o quadro-chave antes do ponto e sai com o tempo dele (não o do ponto).
        // Não use -copyts com ritmo de leitura: o ffmpeg esperava o relógio chegar no carimbo (15 s parado antes do primeiro byte no teste de 11/11).
        a.AddRange(new[] { "-output_ts_offset", inicioS.ToString("0.###", CultureInfo.InvariantCulture), "-muxdelay", "0", "-flush_packets", "1", "-f", "mpegts", "pipe:1" });
        return a;
    }

    /// <summary>Mbps do fluxo que sai para cada espectador (vídeo + áudio), para o ritmo do envio adiantado caber no upload.</summary>
    public static double MbpsDoFluxo(FonteInfo f, PerfilStream perfil) => perfil switch
    {
        PerfilStream.Copia => f.Mbps > 0 ? f.Mbps : 10,
        PerfilStream.Padrao => 6.2,
        _ => 3.15,
    };

    /// <summary>
    /// A faixa de áudio que o mpv está tocando. Caso de 11/11 (Batman "Ita Eng"): o envio mandava sempre a primeira (DTS) e o mpv do anfitrião tocava a AAC; quem assistia
    /// ouvia outra faixa. O <c>ff-index</c> é o índice do ffmpeg (<c>0:N</c>); sem ele, a posição entre as faixas de áudio (o <c>id</c> do mpv conta a partir de 1).
    /// Nulo sem som (aid=no) ou sem resposta: o envio fica com a que já tinha.
    /// </summary>
    public static async Task<FaixaAudio?> FaixaDoMpvAsync(Mpv mpv)
    {
        var ci = CultureInfo.InvariantCulture;
        string? ff = await mpv.TextoAsync("current-tracks/audio/ff-index");
        string? id = await mpv.TextoAsync("current-tracks/audio/id");
        if (ff == null && id == null) return null;
        string? codec = await mpv.TextoAsync("current-tracks/audio/codec");
        string? lang = await mpv.TextoAsync("current-tracks/audio/lang");
        string map = int.TryParse(ff, NumberStyles.Integer, ci, out int n) && n >= 0 ? $"0:{n}"
            : int.TryParse(id, NumberStyles.Integer, ci, out int i) && i >= 1 ? $"0:a:{i - 1}" : "0:a:0?";
        return new FaixaAudio(map, codec, $"{map} {codec}{(lang != null ? " " + lang : "")}");
    }

    /// <summary>HDR → SDR para o filtro do ffmpeg (com a vírgula no fim); vazio sem HDR. Sem isso o quadro de 8 bits sai cinza e lavado.</summary>
    /// <example><code>string vf = $"scale=-2:720,{Fonte.FiltroTom(info)}format=yuv420p";</code></example>
    internal static string FiltroTom(FonteInfo f) =>
        f.Hdr ? "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv," : "";

    static int AltoDe(PerfilStream perfil) => perfil == PerfilStream.Leve ? 720 : 1080;
    static string KbpsDe(PerfilStream perfil) => perfil == PerfilStream.Leve ? "3000k" : "6000k";

    static IEnumerable<string> VideoArgs(FonteInfo f, PerfilStream perfil)
    {
        int alto = AltoDe(perfil);
        string kbps = KbpsDe(perfil);
        // HDR vira SDR antes de recomprimir (o x264 de 8 bits sem isso sai lavado). REDUZ ANTES do tone-map: num 4K HDR10 (3832x1600) de um amigo com o tone-map na resolução cheia o x264
        // rodava a 0,85x do tempo real (Ryzen 5 3600) e engasgava; reduzindo primeiro foram 1,35x (medido em 2026-11-11). Sem HDR só reduz e fica em 4:2:0.
        return new[] { "-vf", $"scale=-2:'min({alto},ih)',{FiltroTom(f)}format=yuv420p", "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-b:v", kbps, "-maxrate", kbps, "-bufsize", "2M", "-g", "48", "-keyint_min", "48" };
    }
}
