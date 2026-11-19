using System.Net;
using System.Text;

namespace Streemu.Net;

/// <summary>
/// Código de pareamento, em base32 sem caracteres ambíguos (sem 0/O, 1/I).
/// v1 (14 bytes, 23 caracteres): versão + IPv4 público + porta + segredo(6) + verificação.
/// v2 (20 bytes, 32 caracteres): v1 + endereço alternativo (IPv4 + porta), usado quando a operadora
///     entrega os pacotes entre clientes dela com o endereço INTERNO do CGNAT (ex.: 100.90.x.x).
/// v3 (26 bytes, 42 caracteres): v2 + endereço de relay (TURN), para quem não consegue receber direto.
/// </summary>
public sealed class PeerCode
{
    const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public IPAddress Address { get; }
    public int Port { get; }
    public byte[] Secret { get; }
    public IPEndPoint? Alt { get; }
    public IPEndPoint? Relay { get; }

    public PeerCode(IPAddress address, int port, byte[] secret, IPEndPoint? alt = null, IPEndPoint? relay = null)
    {
        Address = address; Port = port; Secret = secret; Alt = alt; Relay = relay;
    }

    public IPEndPoint Endpoint => new(Address, Port);

    public string Encode()
    {
        int version = Relay != null ? 3 : Alt != null ? 2 : 1;
        var b = new byte[version switch { 3 => 26, 2 => 20, _ => 14 }];
        b[0] = (byte)version;
        Address.GetAddressBytes().CopyTo(b, 1);
        b[5] = (byte)(Port >> 8); b[6] = (byte)Port;
        Secret.CopyTo(b, 7);
        if (version >= 2 && Alt != null) WriteEp(b, 13, Alt);
        if (version == 3) WriteEp(b, 19, Relay!);
        b[^1] = Checksum(b.AsSpan(0, b.Length - 1));
        string raw = ToBase32(b);
        var sb = new StringBuilder("LX");
        for (int i = 0; i < raw.Length; i += 5) sb.Append('-').Append(raw.AsSpan(i, Math.Min(5, raw.Length - i)));
        return sb.ToString();
    }

    static void WriteEp(byte[] b, int at, IPEndPoint ep)
    {
        ep.Address.GetAddressBytes().CopyTo(b, at);
        b[at + 4] = (byte)(ep.Port >> 8); b[at + 5] = (byte)ep.Port;
    }

    static IPEndPoint? ReadEp(byte[] b, int at)
    {
        int port = (b[at + 4] << 8) | b[at + 5];
        return port == 0 ? null : new IPEndPoint(new IPAddress(b.AsSpan(at, 4)), port);
    }

    /// <summary>Lê um código colado (aceita espaços, minúsculas e sem traços). Lança FormatException com o motivo.</summary>
    public static PeerCode Parse(string text)
    {
        string clean = new string(text.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (clean.StartsWith("LX")) clean = clean[2..];
        clean = clean.Replace('0', 'O').Replace('1', 'I');
        int len = clean.Length switch { 23 => 14, 32 => 20, 42 => 26, _ => 0 };
        if (len == 0)
            throw new FormatException($"o código precisa ter 23, 32 ou 42 letras/números depois de \"LX\"; o colado tem {clean.Length} (copiou pela metade?)");
        foreach (char c in clean)
            if (!Alphabet.Contains(c)) throw new FormatException($"o caractere '{c}' não existe nos códigos (não usamos 0, 1, O nem I)");
        byte[] b = FromBase32(clean, len);
        int expectedVersion = len switch { 14 => 1, 20 => 2, _ => 3 };
        if (b[0] != expectedVersion) throw new FormatException($"versão de código {b[0]} desconhecida; os dois precisam usar a mesma versão do app");
        if (Checksum(b.AsSpan(0, len - 1)) != b[len - 1]) throw new FormatException("o código tem algum caractere errado (falhou a verificação)");
        var ip = new IPAddress(b.AsSpan(1, 4));
        int port = (b[5] << 8) | b[6];
        if (port == 0) throw new FormatException("porta 0 no código");
        var alt = len >= 20 ? ReadEp(b, 13) : null;
        var relay = len == 26 ? ReadEp(b, 19) : null;
        return new PeerCode(ip, port, b.AsSpan(7, 6).ToArray(), alt, relay);
    }

    /// <summary>
    /// Confere para onde o código manda este PC mandar pacotes (fora do modo local). Um app adulterado de um amigo poderia
    /// pôr no código o roteador da vítima (192.168.x), o loopback ou o IP de um terceiro, e o túnel varreria ~576 portas
    /// por até 10 min nesse endereço. Por isso o endereço público e o do relay TURN precisam ser IPv4 público de verdade
    /// (recusa o código inteiro); o alternativo pode ser da operadora (CGNAT) ou privado, que é para o que ele existe, mas
    /// não loopback, 0/8, link-local, multicast nem reservado (esse é só descartado). Devolve o código a usar.
    /// </summary>
    public PeerCode ValidateRemote()
    {
        if (!IsPublicUnicast(Address))
            throw new FormatException($"o código aponta para um endereço que não é público na internet ({Tunnel.Mask(Endpoint)}); peça ao amigo para gerar o código de novo (ou o app dele está adulterado)");
        if (Relay != null && !IsPublicUnicast(Relay.Address))
            throw new FormatException($"o relay do código aponta para um endereço que não é público na internet ({Tunnel.Mask(Relay)}); peça ao amigo para gerar o código de novo");
        var alt = Alt;
        if (alt != null && !IsAcceptableAlt(alt.Address))
        {
            Util.Log.Info($"Código do amigo: endereço alternativo {Tunnel.Mask(alt)} descartado (não é um endereço válido de operadora ou rede local).");
            alt = null;
        }
        return alt == Alt ? this : new PeerCode(Address, Port, Secret, alt, Relay);
    }

    /// <summary>IPv4 que a internet roteia de verdade: fora de loopback, 0/8, privados, CGNAT, link-local, multicast, reservado, broadcast e faixas de documentação.</summary>
    public static bool IsPublicUnicast(IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        if (!IsAcceptableAlt(ip)) return false;
        if (Upnp.IsCarrierOrPrivate(ip)) return false;               // 10/8, 172.16/12, 192.168/16, 100.64/10
        if (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) return false; // 192.0.0/24 e 192.0.2/24
        if (b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100)) return false; // 198.18/15 e 198.51.100/24
        if (b[0] == 203 && b[1] == 0 && b[2] == 113) return false;   // 203.0.113/24
        return true;
    }

    /// <summary>Alt: privado e CGNAT valem; loopback, 0/8, link-local, multicast e reservado/broadcast (240/4) não.</summary>
    static bool IsAcceptableAlt(IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        if (b[0] == 0 || b[0] == 127) return false;
        if (b[0] == 169 && b[1] == 254) return false;
        if (b[0] >= 224) return false; // multicast 224/4 e reservado 240/4 (inclui o broadcast)
        return true;
    }

    static byte Checksum(ReadOnlySpan<byte> data)
    {
        int sum = 0x5A;
        foreach (byte x in data) sum = (sum * 31 + x) & 0xFF;
        return (byte)sum;
    }

    static string ToBase32(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (byte x in data)
        {
            buffer = ((buffer << 8) | x) & 0xFFFFF; bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    static byte[] FromBase32(string text, int length)
    {
        var result = new byte[length];
        int buffer = 0, bits = 0, index = 0;
        foreach (char c in text)
        {
            buffer = ((buffer << 5) | Alphabet.IndexOf(c)) & 0xFFFFF; bits += 5;
            if (bits >= 8 && index < length) { result[index++] = (byte)(buffer >> (bits - 8)); bits -= 8; }
        }
        return result;
    }
}
