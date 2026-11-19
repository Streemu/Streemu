using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Streemu.Ui.Avatars;

/// <summary>
/// O avatar animado como uma "tira": os quadros de 64x64 empilhados numa imagem JPEG, com o tempo de cada quadro num cabeçalho de
/// poucos bytes. É o que vai para os amigos e o que fica guardado no disco. O Windows não grava GIF animado, e o GIF original
/// (de qualquer tamanho) seria pesado demais para mandar a cada amigo; a tira fica em ~30 a 130 KB, qualquer que seja o GIF.
/// O círculo NÃO vai na tira (JPEG não tem transparência): quem recebe aplica a máscara de novo.
/// Formato: "LXT1" + nº de quadros (1 byte, 2 a 120) + tempos (uint16 em ms, um por quadro) + JPEG de 64 x (64 * quadros).
/// Uso: <c>var bytes = AvatarFilmstrip.Encode(squares, delays); var load = AvatarFilmstrip.Decode(bytes);</c>
/// </summary>
public static class AvatarFilmstrip
{
    const int HeaderBytes = 5, MaxFrames = AvatarLimits.MaxFrames;
    static readonly byte[] Magic = "LXT1"u8.ToArray();
    static readonly int[] Qualities = { 80, 65, 50, 35 };

    /// <summary>
    /// Monta a tira. Se passar do limite de tamanho, baixa a qualidade e, em último caso, tira quadros alternados somando os tempos
    /// (a animação fica mais curta de quadros, mas dura o mesmo). Não descarta os bitmaps recebidos.
    /// </summary>
    public static byte[] Encode(IReadOnlyList<Bitmap> squares, IReadOnlyList<int> delaysMs)
    {
        if (squares.Count < 2 || squares.Count != delaysMs.Count)
            throw new ArgumentException($"a tira pede 2 ou mais quadros com um tempo cada; recebi {squares.Count} quadros e {delaysMs.Count} tempos");
        var frames = squares.ToList();
        var delays = delaysMs.ToList();
        while (true)
        {
            foreach (int quality in Qualities)
            {
                var bytes = Pack(frames, delays, quality);
                if (bytes.Length <= AvatarLimits.StripMaxBytes) return bytes;
            }
            if (frames.Count <= 2) return Pack(frames, delays, Qualities[^1]);
            (frames, delays) = EveryOther(frames, delays);
        }
    }

    /// <summary>Lê uma tira (de um amigo ou do disco). Confere cabeçalho, tamanho da imagem e integridade antes de decodificar; nunca lança.</summary>
    public static AvatarLoad Decode(byte[] data)
    {
        string? bad = CheckHeader(data, out int count);
        if (bad != null) return AvatarLoad.Fail(bad);
        var delays = ReadDelays(data, count);
        var jpeg = data.AsSpan(HeaderBytes + count * 2).ToArray();
        if (!AvatarDecoder.JpegIsComplete(jpeg)) return AvatarLoad.Fail($"A tira do avatar está incompleta ({AvatarFormatDetector.FormatSize(data.Length)}).");
        try { return Cut(jpeg, count, delays); }
        catch (Exception e) when (e is ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        {
            return AvatarLoad.Fail($"A tira do avatar não abriu ({AvatarFormatDetector.FormatSize(data.Length)}, {count} quadros).");
        }
    }

    static string? CheckHeader(byte[] data, out int count)
    {
        count = data.Length > 4 ? data[4] : 0;
        if (data.Length < HeaderBytes || !data.AsSpan(0, 4).SequenceEqual(Magic))
            return $"A tira do avatar tem um cabeçalho inválido ({data.Length} bytes; esperado começar com LXT1).";
        if (count < 2 || count > MaxFrames) return $"A tira do avatar diz ter {count} quadros; o esperado é de 2 a {MaxFrames}.";
        return data.Length > HeaderBytes + count * 2 + 100 ? null : $"A tira do avatar está curta demais ({data.Length} bytes para {count} quadros).";
    }

    static int[] ReadDelays(byte[] data, int count)
    {
        var delays = new int[count];
        for (int i = 0; i < count; i++)
        {
            int ms = BitConverter.ToUInt16(data, HeaderBytes + i * 2);
            delays[i] = ms < 20 ? 100 : Math.Min(ms, 10_000); // como nos GIFs: 0 a 1 centésimo = 100 ms
        }
        return delays;
    }

    static AvatarLoad Cut(byte[] jpeg, int count, int[] delays)
    {
        int size = AvatarLimits.RenderSize;
        using (var probe = Image.FromStream(new MemoryStream(jpeg), false, false))
            if (probe.Width != size || probe.Height != size * count)
                return AvatarLoad.Fail($"A tira do avatar tem {probe.Width}x{probe.Height} px; o esperado para {count} quadros é {size}x{size * count}.");
        using var strip = Image.FromStream(new MemoryStream(jpeg), false, true);
        var frames = new Bitmap[count];
        for (int i = 0; i < count; i++)
        {
            using var square = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(square))
                g.DrawImage(strip, new Rectangle(0, 0, size, size), new Rectangle(0, i * size, size, size), GraphicsUnit.Pixel);
            frames[i] = AvatarDecoder.MaskCircle(square);
        }
        return new AvatarLoad(new AvatarImage(frames, delays), null, null);
    }

    static byte[] Pack(IReadOnlyList<Bitmap> frames, IReadOnlyList<int> delays, int quality)
    {
        int size = AvatarLimits.RenderSize;
        using var strip = new Bitmap(size, size * frames.Count, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(strip))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            for (int i = 0; i < frames.Count; i++) g.DrawImage(frames[i], new Rectangle(0, i * size, size, size));
        }
        using var jpeg = new MemoryStream();
        var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        strip.Save(jpeg, encoder, parameters);
        using var result = new MemoryStream();
        result.Write(Magic);
        result.WriteByte((byte)frames.Count);
        foreach (int ms in delays) result.Write(BitConverter.GetBytes((ushort)Math.Clamp(ms, 1, ushort.MaxValue)));
        result.Write(jpeg.GetBuffer(), 0, (int)jpeg.Length);
        return result.ToArray();
    }

    static (List<Bitmap>, List<int>) EveryOther(List<Bitmap> frames, List<int> delays)
    {
        var keptFrames = new List<Bitmap>();
        var keptDelays = new List<int>();
        for (int i = 0; i < frames.Count; i += 2)
        {
            keptFrames.Add(frames[i]);
            keptDelays.Add(delays[i] + (i + 1 < frames.Count ? delays[i + 1] : 0));
        }
        return (keptFrames, keptDelays);
    }
}
