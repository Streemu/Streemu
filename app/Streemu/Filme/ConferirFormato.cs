using System.Collections.Concurrent;
using System.Diagnostics;

namespace Streemu.Filme;

/// <summary>
/// Confere pelo CONTEÚDO se um arquivo que veio de outro PC é mesmo um vídeo antes de entregá-lo ao mpv. O mpv decide o que um arquivo é
/// olhando o conteúdo e, se for uma lista (.m3u/.m3u8/.pls/.cue, um EDL "# mpv EDL" ou um HLS), segue as entradas dela: busca URLs (mostra o
/// IP de quem assiste e puxa conteúdo de fora) ou abre arquivos do próprio PC. O nome e a extensão que o amigo mandou não provam nada
/// (medido em 2026-11-19: o mpv ignora a extensão, então a trava é o conteúdo). Só vale para o que veio de outro PC; o arquivo que a
/// própria pessoa escolheu não passa por aqui.
/// Uso: <c>string? motivo = await Task.Run(() => ConferirFormato.Motivo(Tools.FfmpegOuNulo, caminho));</c> (nulo = pode abrir).
/// </summary>
public static class ConferirFormato
{
    /// <summary>
    /// Demuxers de vídeo aceitos (nomes do <c>ffmpeg -demuxers</c>; "mov,mp4,m4a,3gp,3g2,mj2" e "matroska,webm" são um demuxer só e o ffmpeg
    /// casa cada pedaço). Só contêineres de vídeo de verdade, que não leem outros arquivos nem URLs: Matroska/WebM, MP4/MOV, AVI, MPEG-TS,
    /// FLV, ASF/WMV, MPEG-PS, Ogg (.ogv; conferido em 2026-11-19 com Theora e VP8), RealMedia (rm/rmvb), NUT e WTV. Ficam de fora de propósito:
    /// hls, applehttp, concat, ffconcat, sdp, rtsp, image2 e os de lista em geral, e os fluxos crus sem contêiner (h264, hevc, rawvideo...), que
    /// não são o que se espera de um filme baixado.
    /// </summary>
    public const string Formatos = "matroska,webm,mov,mp4,m4a,3gp,3g2,mj2,avi,mpegts,flv,asf,mpeg,ogg,rm,nut,wtv";

    // arquivo+tamanho+data → já conferido bom; filme de 30 GB não espera de novo ao reabrir (o ffmpeg só lê o cabeçalho: 0,2 s num mp4 de 200 MB)
    static readonly ConcurrentDictionary<string, bool> Bons = new();

    /// <summary>Nulo se o arquivo é um vídeo de um formato aceito; senão o motivo (para o log, sem o nome do arquivo).</summary>
    public static string? Motivo(string? ffmpeg, string arquivo)
    {
        if (ffmpeg == null) return "sem o ffmpeg para conferir o arquivo";
        FileInfo fi;
        try { fi = new FileInfo(arquivo); if (!fi.Exists) return "o arquivo não existe mais"; }
        catch (Exception e) { return "não consegui ler o arquivo: " + e.GetType().Name; }
        string chave = $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        if (Bons.ContainsKey(chave)) return null;
        try
        {
            var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            // -protocol_whitelist file: nada de rede nem de "concat:"/"subfile:" por dentro do arquivo. -c copy -frames:v 1: só lê o cabeçalho e um pacote, sem decodificar.
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-protocol_whitelist", "file", "-format_whitelist", Formatos,
                "-i", arquivo, "-map", "0:v:0", "-c", "copy", "-frames:v", "1", "-f", "null", "-" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("o ffmpeg não abriu");
            var erro = p.StandardError.ReadToEndAsync();
            _ = p.StandardOutput.ReadToEndAsync(); // assíncrono: um ReadToEnd aqui esperaria o ffmpeg sair e o limite de 20 s nunca valeria
            if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } return "o ffmpeg demorou mais de 20 s para conferir o arquivo"; }
            if (p.ExitCode != 0) return "o ffmpeg não reconheceu como vídeo (" + Resumo(erro.GetAwaiter().GetResult()) + ")";
        }
        catch (Exception e) { return "não consegui conferir o arquivo: " + e.GetType().Name; }
        Bons[chave] = true;
        return null;
    }

    /// <summary>Uma linha da saída do ffmpeg para o log, sem o caminho do arquivo (ele leva o nome do usuário do PC).</summary>
    static string Resumo(string saida)
    {
        foreach (var l in saida.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string t = l.Trim();
            if (t.StartsWith("[in#", StringComparison.Ordinal)) { int i = t.IndexOf("] ", StringComparison.Ordinal); if (i > 0) t = t[(i + 2)..]; }
            if (t.StartsWith("Error opening input file", StringComparison.Ordinal)) continue; // traz o caminho
            if (t.Length > 0) return t.Length > 120 ? t[..120] : t;
        }
        return "sem detalhe";
    }
}
