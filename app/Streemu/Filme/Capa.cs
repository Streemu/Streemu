using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text.Json.Nodes;
using Streemu.Ui.Avatars;

namespace Streemu.Filme;

/// <summary>
/// A capa do filme no Cinema (spec 017): a imagem que o anfitrião tira do arquivo e as mensagens que a levam pela sala. A capa embutida no arquivo
/// (o pôster que muitos .mkv trazem, faixa "attached pic") vale primeiro (D1); sem ela, um quadro a 10 % da duração, escolhido pelo filtro
/// <c>thumbnail</c> entre ~2 s de quadros para não cair num quadro preto de transição (D2). HDR vira SDR antes (D4). JPEG de 480x270 e até 32 KB.
/// Uso: <c>byte[]? jpeg = await Task.Run(() => CapaDoFilme.Tirar(Tools.Ffmpeg, arquivo));</c>
/// </summary>
public static class CapaDoFilme
{
    public const string TipoPedido = "filme_capa_pedir", TipoImagem = "filme_capa";
    public const int MaxBytes = 32 * 1024, MaxLado = 480;
    // corta no anfitrião numa faixa 16:9 de 480x270: o cartão e a entrada são faixas largas e cortariam de qualquer jeito. Um pôster em pé reduzido
    // para caber em 480 ficava com 320 px de largura e borrado no cartão largo (foto do teste de 2026-11-15)
    const string Escala = "scale=480:270:force_original_aspect_ratio=increase,crop=480:270";
    static readonly TimeSpan Limite = TimeSpan.FromSeconds(20);
    static readonly long[] Qualidades = [80, 65, 50, 35];

    /// <summary>Quem vê pede a capa ao anfitrião (o hash veio no anúncio).</summary>
    /// <example><code>room.SendTo(anfitriao, CapaDoFilme.Pedido(filme, hash));</code></example>
    public static JsonObject Pedido(string filme, string hash) => new() { ["tipo"] = TipoPedido, ["filme"] = filme, ["hash"] = hash };

    /// <summary>O anfitrião responde com a imagem, só a quem pediu.</summary>
    /// <example><code>room.SendTo(amigo, CapaDoFilme.Imagem(filme, jpeg));</code></example>
    public static JsonObject Imagem(string filme, byte[] jpeg) => new()
    {
        ["tipo"] = TipoImagem, ["filme"] = filme, ["hash"] = AvatarMessage.HashOf(jpeg), ["dados"] = Convert.ToBase64String(jpeg)
    };

    /// <summary>A capa em JPEG, ou nulo se o ffmpeg não conseguiu (fica o degradê). Bloqueia por até ~40 s: chame em segundo plano.</summary>
    /// <example><code>byte[]? jpeg = CapaDoFilme.Tirar(Tools.Ffmpeg, @"C:\filmes\filme.mkv");</code></example>
    public static byte[]? Tirar(string ffmpeg, string arquivo)
    {
        byte[]? imagem = Rodar(ffmpeg, ArgsEmbutida(arquivo)) ?? Rodar(ffmpeg, ArgsQuadro(arquivo, Fonte.Analisar(ffmpeg, arquivo)));
        return imagem == null ? null : Comprimir(imagem);
    }

    // "-map 0:v -map -0:V" fica só com as faixas "attached pic" (V maiúsculo = vídeo que não é capa); sem nenhuma, o ffmpeg sai com erro e vale o quadro
    static string[] ArgsEmbutida(string arquivo) =>
        ["-hide_banner", "-loglevel", "error", "-i", arquivo, "-map", "0:v", "-map", "-0:V", "-frames:v", "1", "-vf", Escala, "-c:v", "png", "-f", "image2pipe", "pipe:1"];

    // reduz ANTES do tone-map, como no envio pela transmissão (Fonte.VideoArgs): no 4K HDR o tone-map na resolução cheia é o que pesa
    static string[] ArgsQuadro(string arquivo, FonteInfo info)
    {
        string ponto = (info.DuracaoS * 0.10).ToString("0.###", CultureInfo.InvariantCulture);
        string filtro = $"{Escala},{Fonte.FiltroTom(info)}format=rgb24,thumbnail=48";
        return ["-hide_banner", "-loglevel", "error", "-ss", ponto, "-i", arquivo, "-map", "0:V:0", "-an", "-sn", "-frames:v", "1", "-vf", filtro,
            "-c:v", "png", "-f", "image2pipe", "pipe:1"];
    }

    /// <summary>A saída do ffmpeg (a imagem em PNG) ou nulo se ele falhou ou passou do tempo limite.</summary>
    static byte[]? Rodar(string ffmpeg, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"o ffmpeg não abriu para tirar a capa ({ffmpeg})");
        _ = p.StandardError.ReadToEndAsync();
        using var saida = new MemoryStream();
        var copia = p.StandardOutput.BaseStream.CopyToAsync(saida);
        if (!p.WaitForExit(Limite)) { try { p.Kill(); } catch (InvalidOperationException) { } return null; }
        copia.Wait(Limite);
        return p.ExitCode == 0 && saida.Length > 0 ? saida.ToArray() : null;
    }

    /// <summary>Recomprime em JPEG baixando a qualidade até caber em <see cref="MaxBytes"/>; nulo se nem a mais baixa coube.</summary>
    static byte[]? Comprimir(byte[] imagem)
    {
        using var origem = Image.FromStream(new MemoryStream(imagem));
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        foreach (long qualidade in Qualidades)
        {
            using var saida = new MemoryStream();
            using var parametros = new EncoderParameters(1);
            parametros.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, qualidade);
            origem.Save(saida, codec, parametros);
            if (saida.Length <= MaxBytes) return saida.ToArray();
        }
        return null;
    }
}
