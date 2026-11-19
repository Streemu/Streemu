using System.Text.Json.Nodes;
using Streemu.Ui.Avatars;

namespace Streemu.Ui;

/// <summary>
/// O JPEG que um amigo manda pela sala (miniatura da live, capa do filme): campos <c>dados</c> (base64) e <c>hash</c>. Tudo é tratado como não
/// confiável e conferido ANTES de decodificar qualquer pixel: tamanho do texto, base64, tamanho dos bytes, assinatura, fim do arquivo e hash.
/// </summary>
public static class JpegPayload
{
    /// <summary>Os bytes do JPEG se tudo confere; nulo se algo estiver errado. Nunca lança.</summary>
    /// <example><code>var jpeg = JpegPayload.Read(msg, 32 * 1024); if (jpeg == null) return;</code></example>
    public static byte[]? Read(JsonObject msg, int maxBytes)
    {
        if (msg["dados"] is not JsonValue dados || !dados.TryGetValue(out string? base64)) return null;
        if (msg["hash"] is not JsonValue h || !h.TryGetValue(out string? hash)) return null;
        if (base64.Length > maxBytes * 4 / 3 + 8) return null;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { return null; }
        bool plausible = bytes.Length > 100 && bytes.Length <= maxBytes && AvatarFormatDetector.Detect(bytes) == AvatarFormat.Jpeg;
        return plausible && AvatarDecoder.JpegIsComplete(bytes) && AvatarMessage.HashOf(bytes) == hash ? bytes : null;
    }
}
