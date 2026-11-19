using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using static Streemu.Ui.Avatars.AvatarFormatDetector;

namespace Streemu.Ui.Avatars;

/// <summary>Resultado de ler uma imagem: o avatar pronto, ou o motivo de não dar (em português, para mostrar ao usuário).</summary>
/// <param name="Squares">Só para GIF animado escolhido no PC: os quadros SEM a máscara redonda, para montar a tira. Quem recebe descarta.</param>
public sealed record AvatarLoad(AvatarImage? Image, string? Error, string? Notice, Bitmap[]? Squares = null)
{
    public static AvatarLoad Fail(string reason) => new(null, reason, null);
}

/// <summary>
/// Transforma bytes de uma imagem (JPEG, PNG, GIF ou BMP) em um <see cref="AvatarImage"/>: recorte quadrado do centro, círculo, 64 px.
/// Os bytes podem vir de um amigo, então o formato é reconhecido pelos bytes, tamanho e dimensões são conferidos ANTES de
/// decodificar, e qualquer falha do GDI+ vira uma mensagem, nunca uma exceção para a tela.
/// Uso: <c>var r = AvatarDecoder.LoadFile(path); if (r.Image != null) cache.Put(hash, r.Image);</c>
/// </summary>
public static class AvatarDecoder
{
    const int TagOrientation = 0x0112, TagFrameDelay = 0x5100;

    public static AvatarLoad LoadFile(string path)
    {
        string name = Path.GetFileName(path);
        try
        {
            var load = Load(File.ReadAllBytes(path), AvatarOrigin.LocalFile);
            return load.Error == null ? load : load with { Error = $"{name}: {load.Error}" };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return AvatarLoad.Fail($"{name}: não consegui abrir o arquivo ({e.Message}).");
        }
    }

    public static AvatarLoad Load(byte[] data, AvatarOrigin origin)
    {
        var format = Detect(data);
        string? early = RejectEarly(data.Length, format, origin);
        if (early != null) return AvatarLoad.Fail(early);
        if (!IsComplete(data, format)) return AvatarLoad.Fail(Corrupt(format, data.Length));
        try { return Decode(data, format, origin); }
        catch (Exception e) when (e is ArgumentException or OutOfMemoryException or ExternalException or InvalidOperationException)
        {
            return AvatarLoad.Fail(Corrupt(format, data.Length));
        }
    }

    static string Corrupt(AvatarFormat format, int length) =>
        $"A imagem está corrompida ou incompleta ({format}, {FormatSize(length)}). Formatos aceitos: {AvatarLimits.AcceptedFormats}.";

    /// <summary>
    /// O GDI+ aceita JPEG, PNG e GIF CORTADOS e devolve uma imagem pela metade em vez de dar erro (medido em 2026-11-07), então a
    /// conferência de que o arquivo veio inteiro é nossa: cada formato termina com um marcador (JPEG FF D9, PNG IEND, GIF 3B) e o
    /// BMP declara o próprio tamanho. Tolera uns poucos bytes de enchimento no fim.
    /// </summary>
    static bool IsComplete(byte[] d, AvatarFormat format) => format switch
    {
        AvatarFormat.Jpeg => JpegIsComplete(d),
        AvatarFormat.Png => EndsWith(d, new byte[] { 0x49, 0x45, 0x4E, 0x44 }, 16),
        AvatarFormat.Gif => EndsWith(d, new byte[] { 0x3B }, 8),
        AvatarFormat.Bmp => d.Length < 6 || BitConverter.ToUInt32(d, 2) is var declared && (declared == 0 || d.Length >= declared),
        _ => true,
    };

    /// <summary>
    /// JPEG inteiro: FF D9 no fim, OU a "foto em movimento" de celular (Samsung, Google), que é um JPEG completo com um vídeo MP4
    /// colado depois: nesse caso o último FF D9 é seguido de uma caixa "ftyp". Dentro dos dados da imagem o FF é sempre
    /// seguido de 00 ou de um marcador de reinício, então um FF D9 solto no meio do arquivo é mesmo o fim da imagem.
    /// </summary>
    internal static bool JpegIsComplete(byte[] d)
    {
        if (EndsWith(d, new byte[] { 0xFF, 0xD9 }, 64)) return true;
        int eoi = d.AsSpan().LastIndexOf(new byte[] { 0xFF, 0xD9 });
        if (eoi < 0 || eoi + 14 > d.Length) return false;
        return d.AsSpan(eoi + 2, Math.Min(32, d.Length - eoi - 2)).IndexOf("ftyp"u8) is >= 0 and <= 12;
    }

    static bool EndsWith(byte[] d, byte[] marker, int window)
    {
        for (int i = d.Length - marker.Length; i >= Math.Max(0, d.Length - window); i--)
            if (d.AsSpan(i, marker.Length).SequenceEqual(marker)) return true;
        return false;
    }

    static string? RejectEarly(int length, AvatarFormat format, AvatarOrigin origin)
    {
        long max = origin == AvatarOrigin.LocalFile ? AvatarLimits.LocalFileMaxBytes : AvatarLimits.RemoteMaxBytes;
        if (length == 0) return "O arquivo está vazio.";
        if (format == AvatarFormat.WebP) return $"WebP ainda não é aceito. Formatos aceitos: {AvatarLimits.AcceptedFormats}.";
        if (format == AvatarFormat.Unknown) return $"Formato não reconhecido. Formatos aceitos: {AvatarLimits.AcceptedFormats}.";
        return length > max ? $"A imagem tem {FormatSize(length)}; o máximo é {FormatSize(max)}." : null;
    }

    static AvatarLoad Decode(byte[] data, AvatarFormat format, AvatarOrigin origin)
    {
        using (var header = new MemoryStream(data))
        using (var probe = Image.FromStream(header, false, false)) // só o cabeçalho: dá o tamanho sem decodificar os pixels
        {
            string? tooBig = RejectDimensions(probe.Width, probe.Height, origin);
            if (tooBig != null) return AvatarLoad.Fail(tooBig);
        }
        using var stream = new MemoryStream(data);
        using var source = Image.FromStream(stream, false, true);
        return format == AvatarFormat.Gif ? DecodeGif(source, data.Length, origin) : new AvatarLoad(StillImage(source), null, null);
    }

    static string? RejectDimensions(int w, int h, AvatarOrigin origin)
    {
        if (origin == AvatarOrigin.LocalFile && (long)w * h > AvatarLimits.LocalMaxPixels)
            return $"A imagem tem {w}x{h} pixels; o máximo é {AvatarLimits.LocalMaxPixels / 1_000_000} milhões de pixels.";
        if (origin == AvatarOrigin.Remote && Math.Max(w, h) > AvatarLimits.RemoteMaxSide)
            return $"A imagem recebida tem {w}x{h} pixels; o máximo é {AvatarLimits.RemoteMaxSide} de lado.";
        return null;
    }

    static AvatarImage StillImage(Image source)
    {
        using var oriented = OrientedCopy(source);
        return new AvatarImage(new[] { RenderCircle(oriented ?? source) }, Array.Empty<int>());
    }

    /// <summary>
    /// GIF de qualquer tamanho. O escolhido no PC não tem limite de arquivo nem de lado: só limito o TRABALHO (quadros x pixels) e
    /// pulo quadros para ficar com no máximo <see cref="AvatarLimits.StripMaxFrames"/>, somando os tempos (a animação dura o mesmo).
    /// O que chega de um amigo da versão antiga mantém os limites antigos (200 KB, 128 px).
    /// </summary>
    static AvatarLoad DecodeGif(Image source, int bytes, AvatarOrigin origin)
    {
        int total = source.GetFrameCount(FrameDimension.Time);
        if (total <= 1) return Still(source, null);
        if (origin == AvatarOrigin.Remote && !FitsOldRemoteGif(source, bytes))
            return Still(source, $"O GIF recebido tem {FormatSize(bytes)} e {source.Width}x{source.Height} px; usei o primeiro quadro.");
        int step = FrameStep(total, source.Width, source.Height, origin);
        if (step == 0) return Still(source, $"O GIF tem {source.Width}x{source.Height} px e {total} quadros: é pesado demais para animar. Usei o primeiro quadro.");
        return Animated(source, total, step, keepSquares: origin == AvatarOrigin.LocalFile);
    }

    static AvatarLoad Still(Image source, string? notice) =>
        new(new AvatarImage(new[] { RenderCircle(source) }, Array.Empty<int>()), null, notice);

    static bool FitsOldRemoteGif(Image source, int bytes) =>
        bytes <= AvatarLimits.AnimatedGifMaxBytes && Math.Max(source.Width, source.Height) <= AvatarLimits.AnimatedGifMaxSide;

    /// <summary>De quantos em quantos quadros pegar (1 = todos). Zero se nem 2 quadros cabem no orçamento de trabalho.</summary>
    static int FrameStep(int total, int width, int height, AvatarOrigin origin)
    {
        if (origin == AvatarOrigin.Remote) return 1;
        long affordable = AvatarLimits.GifPixelBudget / Math.Max(1L, (long)width * height);
        int wanted = (int)Math.Min(AvatarLimits.StripMaxFrames, Math.Min(total, affordable));
        return wanted < 2 ? 0 : (total + wanted - 1) / wanted;
    }

    static AvatarLoad Animated(Image source, int total, int step, bool keepSquares)
    {
        var allDelays = FrameDelays(source, total);
        int count = Math.Min(AvatarLimits.MaxFrames, (total + step - 1) / step);
        var frames = new Bitmap[count];
        var squares = keepSquares ? new Bitmap[count] : null;
        var delays = new int[count];
        for (int i = 0; i < count; i++)
        {
            int first = i * step;
            source.SelectActiveFrame(FrameDimension.Time, first);
            var square = RenderSquare(source, AnimatedBackdrop);
            frames[i] = MaskCircle(square);
            if (squares != null) squares[i] = square; else square.Dispose();
            delays[i] = Math.Min(10_000, allDelays.Skip(first).Take(step).Sum());
        }
        return new AvatarLoad(new AvatarImage(frames, delays), null, null, squares);
    }

    /// <summary>Tempo de cada quadro em ms. Como os navegadores, trata 0 a 1 centésimo como 100 ms (senão o GIF roda rápido demais).</summary>
    static int[] FrameDelays(Image source, int count)
    {
        var delays = Enumerable.Repeat(100, count).ToArray();
        if (!source.PropertyIdList.Contains(TagFrameDelay)) return delays;
        byte[] raw = source.GetPropertyItem(TagFrameDelay)!.Value!;
        for (int i = 0; i < count && (i + 1) * 4 <= raw.Length; i++)
        {
            int ms = BitConverter.ToInt32(raw, i * 4) * 10;
            delays[i] = ms < 20 ? 100 : Math.Min(ms, 10_000);
        }
        return delays;
    }

    /// <summary>Foto de celular vem "deitada" com um aviso de rotação (EXIF 0x0112); o GDI+ não gira sozinho. Devolve uma cópia girada ou nulo.</summary>
    static Bitmap? OrientedCopy(Image source)
    {
        if (!source.PropertyIdList.Contains(TagOrientation)) return null;
        var flip = (int)BitConverter.ToUInt16(source.GetPropertyItem(TagOrientation)!.Value!, 0) switch
        {
            2 => RotateFlipType.RotateNoneFlipX, 3 => RotateFlipType.Rotate180FlipNone, 4 => RotateFlipType.RotateNoneFlipY,
            5 => RotateFlipType.Rotate90FlipX, 6 => RotateFlipType.Rotate90FlipNone,
            7 => RotateFlipType.Rotate270FlipX, 8 => RotateFlipType.Rotate270FlipNone, _ => RotateFlipType.RotateNoneFlipNone,
        };
        if (flip == RotateFlipType.RotateNoneFlipNone) return null;
        var copy = new Bitmap(source);
        copy.RotateFlip(flip);
        return copy;
    }

    /// <summary>Recorta o quadrado do centro, reduz para o tamanho do avatar e aplica a máscara redonda com borda suave.</summary>
    static Bitmap RenderCircle(Image source)
    {
        using var square = RenderSquare(source, null);
        return MaskCircle(square);
    }

    /// <summary>O recorte quadrado do centro, reduzido para o tamanho do avatar (ainda sem a máscara redonda).</summary>
    /// <summary>
    /// A tira é JPEG (sem transparência): GIF com fundo transparente ficava PRETO. Os quadros animados são achatados sobre esta cor
    /// (a do card do Grupo). Imagem parada não passa por aqui e mantém a transparência.
    /// </summary>
    static readonly Color AnimatedBackdrop = Color.FromArgb(0x2B, 0x2D, 0x31);

    static Bitmap RenderSquare(Image source, Color? backdrop)
    {
        int size = AvatarLimits.RenderSize, side = Math.Min(source.Width, source.Height);
        var square = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(square);
        using var edge = new ImageAttributes();
        if (backdrop is { } color) g.Clear(color);
        edge.SetWrapMode(WrapMode.TileFlipXY); // sem fio claro na borda do recorte
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(source, new Rectangle(0, 0, size, size), (source.Width - side) / 2, (source.Height - side) / 2, side, side, GraphicsUnit.Pixel, edge);
        return square;
    }

    /// <summary>Aplica a máscara redonda (borda suave) a um quadrado já no tamanho do avatar.</summary>
    internal static Bitmap MaskCircle(Bitmap square)
    {
        int size = square.Width;
        var round = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(round);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new TextureBrush(square);
        g.FillEllipse(brush, 0, 0, size, size);
        return round;
    }
}
