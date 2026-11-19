using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Streemu.Ui.Avatars;

/// <summary>
/// A mensagem `avatar` que vai pela sala do grupo (mensagens `para`/`de`, o Worker repassa sem olhar). Três formas:
/// imagem (miniatura PNG ou GIF pequeno em base64, com o hash), Discord (id e código; quem recebe baixa da CDN) e nenhum.
/// Nunca leva caminho, nome de arquivo nem endereço de rede. Quem não conhece o tipo "avatar" (versão antiga) ignora a mensagem.
/// Uso: <c>await room.SendTo(friendId, AvatarMessage.Image(store.Payload!, store.PayloadFormat, store.Hash!));</c>
/// </summary>
public static class AvatarMessage
{
    public const string Type = "avatar";
    /// <summary>"Me mande seu avatar de novo": quem acabou de abrir o app (memória vazia) pede, e o amigo esquece que já mandou. Versão antiga ignora.</summary>
    public const string AskType = "avatar_pedir";

    public static JsonObject Ask() => new() { ["tipo"] = AskType };

    public static JsonObject Image(byte[] payload, AvatarFormat format, string hash) => new()
    {
        ["tipo"] = Type, ["fonte"] = "imagem", ["formato"] = format switch { AvatarFormat.Gif => "gif", AvatarFormat.Strip => "tira", _ => "png" },
        ["hash"] = hash, ["dados"] = Convert.ToBase64String(payload)
    };

    public static JsonObject Discord(string id, string code) => new()
    {
        ["tipo"] = Type, ["fonte"] = "discord", ["id"] = id, ["codigo"] = code
    };

    public static JsonObject None() => new() { ["tipo"] = Type, ["fonte"] = "nenhum" };

    /// <summary>Os 16 hex do SHA-256 que identificam a imagem (o mesmo cálculo do <see cref="AvatarStore"/>).</summary>
    public static string HashOf(byte[] payload) => Convert.ToHexString(SHA256.HashData(payload))[..16].ToLowerInvariant();
}
