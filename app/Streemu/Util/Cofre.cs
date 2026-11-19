using System.Security.Cryptography;
using System.Text;

namespace Streemu.Util;

/// <summary>
/// Guarda um segredo no disco só para este usuário do Windows (DPAPI, escopo <c>CurrentUser</c>). Usado para o segredo do dispositivo da
/// conta no <c>estado.json</c> (spec 019): quem copiar o arquivo para outro PC ou outro usuário não consegue abrir.
/// Não é proteção contra um programa rodando como o próprio usuário; é o que o Windows dá de graça.
/// </summary>
public static class Cofre
{
    // um "sal" fixo do app: separa o que o Streemu guarda do que outros programas guardam com a mesma API
    static readonly byte[] Entropia = Encoding.UTF8.GetBytes("Streemu.Cofre.v1");

    /// <summary>Texto → base64 do que o Windows cifra. Vazio ou nulo devolve nulo.</summary>
    public static string? Proteger(string? texto)
    {
        if (string.IsNullOrEmpty(texto)) return null;
        try { return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(texto), Entropia, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }

    /// <summary>O inverso de <see cref="Proteger"/>. Devolve nulo se não abrir (outro usuário, outro PC, arquivo estragado): nunca lança.</summary>
    public static string? Abrir(string? protegido)
    {
        if (string.IsNullOrEmpty(protegido)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protegido), Entropia, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }
}
