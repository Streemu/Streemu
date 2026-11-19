using System.Globalization;

namespace Streemu.Ui.Avatars;

/// <summary>De onde vêm os bytes da imagem: um arquivo que o próprio usuário escolheu ou algo que chegou de um amigo pela sala.</summary>
public enum AvatarOrigin { LocalFile, Remote }

/// <summary>Formatos de imagem que o app reconhece pelos primeiros bytes (a extensão do arquivo não é confiável).</summary>
/// <summary>Strip = a "tira" de quadros do avatar animado (<see cref="AvatarFilmstrip"/>); só existe dentro do app, nunca é um arquivo do usuário.</summary>
public enum AvatarFormat { Unknown, Jpeg, Png, Gif, Bmp, WebP, Strip }

/// <summary>Limites dos avatares. Valores da spec 005 (seção 6 aceita ajuste depois da primeira medição).</summary>
public static class AvatarLimits
{
    /// <summary>Lado, em pixels, em que todo avatar é guardado (a faixa mostra ~28 a 56 px; 64 cobre até 200% de escala).</summary>
    public const int RenderSize = 64;
    public const long LocalFileMaxBytes = 25L * 1024 * 1024;
    public const long RemoteMaxBytes = 256L * 1024;
    /// <summary>
    /// Limites do GIF que CHEGA de um amigo da versão antiga (que mandava o GIF inteiro). O GIF que o próprio usuário escolhe não tem
    /// limite de tamanho: vira uma tira (<see cref="StripMaxFrames"/> quadros, <see cref="StripMaxBytes"/>).
    /// </summary>
    public const long AnimatedGifMaxBytes = 200L * 1024;
    public const int AnimatedGifMaxSide = 128;
    /// <summary>Teto de pixels de uma foto escolhida no PC (24 MP, uma foto de celular) e de lado de uma miniatura recebida.</summary>
    public const long LocalMaxPixels = 24_000_000;
    public const int RemoteMaxSide = 512;
    public const int MaxFrames = 120;
    /// <summary>Quadros e bytes da tira que vai para os amigos e fica guardada (40 quadros de 64 px = 640 KB decodificados por avatar).</summary>
    public const int StripMaxFrames = 40;
    public const long StripMaxBytes = 128L * 1024;
    /// <summary>Quadros x pixels que aceito decodificar de um GIF escolhido (limita o tempo que a tela fica parada ao escolher).</summary>
    public const long GifPixelBudget = 120_000_000;
    public const string AcceptedFormats = "JPEG, PNG, GIF e BMP";
}

public static class AvatarFormatDetector
{
    /// <summary>Reconhece o formato pelos bytes iniciais (assinatura do arquivo).</summary>
    public static AvatarFormat Detect(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return AvatarFormat.Jpeg;
        if (d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return AvatarFormat.Png;
        if (d.Length >= 6 && d[0] == (byte)'G' && d[1] == (byte)'I' && d[2] == (byte)'F' && d[3] == (byte)'8') return AvatarFormat.Gif;
        if (d.Length >= 2 && d[0] == (byte)'B' && d[1] == (byte)'M') return AvatarFormat.Bmp;
        if (d.Length >= 12 && d[0] == (byte)'R' && d[1] == (byte)'I' && d[2] == (byte)'F' && d[3] == (byte)'F' && d[8] == (byte)'W' && d[9] == (byte)'E') return AvatarFormat.WebP;
        return AvatarFormat.Unknown;
    }

    /// <summary>"180 KB" ou "3,2 MB", como o usuário lê.</summary>
    public static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? (bytes / 1048576.0).ToString("0.#", CultureInfo.GetCultureInfo("pt-BR")) + " MB"
        : Math.Max(1, (bytes + 512) / 1024) + " KB";
}
