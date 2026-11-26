using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Streemu.Net;

namespace Streemu.Telemetria;

/// <summary>
/// Cifra o que o app envia à telemetria com a chave PÚBLICA da equipe (spec 018, 7.2, decisão D22; plano 018, A9). Formato idêntico ao que o Worker
/// confere em <c>relay/cifra.js</c> e o <c>scripts/telemetria.ps1</c> abre: um "quadro" por pedaço enviado.
/// <code>
///   0  4 bytes   "SEC1"
///   4  2 bytes   versão da chave pública (chave_v), big-endian
///   6 65 bytes   chave pública efêmera do app (P-256, ponto não comprimido, 0x04 + X + Y)
///  71 12 bytes   IV do AES-GCM
///  83  4 bytes   tamanho do texto cifrado (com a etiqueta de 16 bytes no fim), big-endian
///  87  N bytes   texto cifrado
/// </code>
/// Chave do AES-256: HKDF-SHA256 (sal vazio, info "streemu-telemetria-v1") sobre o segredo do ECDH; o AAD do AES-GCM são os 83 primeiros bytes
/// do quadro. Cada quadro tem a sua chave efêmera e o seu IV. O que se cifra num pedaço de arquivo é o gzip dele (o mesmo que ia em claro), então
/// quem decifra e cola os quadros de um arquivo na ordem obtém o "gzip de vários membros" de sempre.
///
/// A chave pública vai EMBUTIDA aqui (não é segredo) e NUNCA é baixada do Worker: quem invadisse o Worker não conseguiria trocar a chave para ler o
/// que chegasse depois. Trocar a chave = nova versão do app com uma linha nova em <see cref="Chaves"/> e <see cref="VersaoAtual"/> subindo; o Worker
/// e o painel aceitam as versões antigas (cada quadro diz a sua). A chave privada não existe neste repositório.
/// </summary>
public static class CifraEnvio
{
    /// <summary>Versão da chave pública que este app usa (vai em <c>cifra.chave_v</c> e em cada quadro).</summary>
    public const int VersaoAtual = 1;

    /// <summary>Tabela versão → chave pública (SPKI DER em base64, P-256). Só se acrescenta; versão antiga não sai, para o código continuar abrindo o que já foi enviado com ela.</summary>
    static readonly Dictionary<int, string> Chaves = new()
    {
        [1] = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAESEcqlyDQK1cdRXBAOri4KP1K5v5zPhE2ddbvkXtZqrmY8v4pf61ItcEacaZSEtlKVcITlHUA54up7yBltUOogQ==",
    };

    /// <summary>
    /// Só em teste automático (STREEMU_TEST_DIR): troca a chave pública por esta (SPKI DER em base64), para o servidor falso, que gera um par em
    /// memória, poder decifrar. Fora de teste a variável é ignorada: um programa que mude o ambiente do app não consegue desviar a cifra.
    /// STREEMU_TELEMETRIA_CHAVE_V_TESTE (opcional, 1 a 65535) diz a versão que acompanha essa chave; sem ela vale <see cref="VersaoAtual"/>.
    /// </summary>
    public const string VarChaveTeste = "STREEMU_TELEMETRIA_CHAVE_TESTE", VarVersaoTeste = "STREEMU_TELEMETRIA_CHAVE_V_TESTE";

    const string Magia = "SEC1", InfoHkdf = "streemu-telemetria-v1";
    public const int CabecalhoQuadro = 83, PerfilCifradoMax = 90000; // o Worker recusa o pacote inteiro se o perfil_cifrado passar disto

    /// <summary>A chave (versão e SPKI) que vale agora: a embutida, ou a de teste quando o app está em teste automático.</summary>
    public static (int Versao, byte[] Spki) ChaveAtual()
    {
        if (RelayConfig.ModoTeste && Environment.GetEnvironmentVariable(VarChaveTeste) is { Length: > 0 } teste)
        {
            int v = int.TryParse(Environment.GetEnvironmentVariable(VarVersaoTeste), out var n) && n is >= 1 and <= 65535 ? n : VersaoAtual;
            return (v, Convert.FromBase64String(teste));
        }
        return (VersaoAtual, Convert.FromBase64String(Chaves[VersaoAtual]));
    }

    /// <summary>Cifra <paramref name="claro"/> num quadro. Lança se a chave não abre ou o sistema não cifra; quem chama trata como falha do envio (nunca manda em claro no lugar).</summary>
    public static byte[] Quadro(ReadOnlySpan<byte> claro)
    {
        try { return Cifrar(claro); }
        catch (Exception e) when (e is not CryptographicException { Message: var m } || !m.StartsWith("não consegui cifrar"))
        {
            throw new CryptographicException($"não consegui cifrar o diagnóstico antes de enviar ({e.GetType().Name}); nada sai em claro no lugar. Avise a equipe do Streemu."); // sem `e` dentro: o envio mostra a causa mais interna, e esta frase é a que diz o que fazer
        }
    }

    static byte[] Cifrar(ReadOnlySpan<byte> claro)
    {
        var (versao, spki) = ChaveAtual();
        using var destino = ECDiffieHellman.Create();
        destino.ImportSubjectPublicKeyInfo(spki, out _);
        using var efemera = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = efemera.ExportParameters(false).Q;
        var quadro = new byte[CabecalhoQuadro + 4 + claro.Length + 16];
        var s = quadro.AsSpan();
        Encoding.ASCII.GetBytes(Magia, s);
        BinaryPrimitives.WriteUInt16BigEndian(s[4..], (ushort)versao);
        s[6] = 0x04;
        q.X!.AsSpan().CopyTo(s[(7 + 32 - q.X!.Length)..39]);
        q.Y!.AsSpan().CopyTo(s[(39 + 32 - q.Y!.Length)..71]);
        RandomNumberGenerator.Fill(s.Slice(71, 12));
        BinaryPrimitives.WriteUInt32BigEndian(s[CabecalhoQuadro..], (uint)(claro.Length + 16));
        byte[] segredo = efemera.DeriveRawSecretAgreement(destino.PublicKey);
        byte[] chaveAes = HKDF.DeriveKey(HashAlgorithmName.SHA256, segredo, 32, Array.Empty<byte>(), Encoding.ASCII.GetBytes(InfoHkdf));
        try
        {
            using var aes = new AesGcm(chaveAes, 16);
            int ini = CabecalhoQuadro + 4;
            aes.Encrypt(s.Slice(71, 12), claro, s.Slice(ini, claro.Length), s.Slice(ini + claro.Length, 16), s[..CabecalhoQuadro]);
        }
        finally { CryptographicOperations.ZeroMemory(segredo); CryptographicOperations.ZeroMemory(chaveAes); }
        return quadro;
    }

    /// <summary>O cabeçalho <c>cifra</c> do pacote: avisa o Worker que os pedaços e o perfil completo são quadros desta versão de chave.</summary>
    public static JsonObject Cabecalho() => new() { ["v"] = 1, ["chave_v"] = ChaveAtual().Versao };

    /// <summary>
    /// O perfil completo (áudio, rede, telas, programas) como UM quadro em base64, para <c>perfil_cifrado</c>. Nulo se passar do limite que o Worker
    /// aceita (<see cref="PerfilCifradoMax"/>): o pacote sobe sem ele (o perfil também vai, cifrado, como arquivo perfil-*.json) em vez de ser recusado.
    /// </summary>
    public static string? PerfilCifrado(JsonObject perfilCompleto)
    {
        string b64 = Convert.ToBase64String(Quadro(Encoding.UTF8.GetBytes(perfilCompleto.ToJsonString())));
        return b64.Length <= PerfilCifradoMax ? b64 : null;
    }

    // ------------------------------------------------------------------ o que fica legível (spec 018, 7.2): espelho de perfilLegivel e eventoLegivel (relay/cifra.js)

    static string? Texto(JsonNode? n, int max = 120) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? (s.Trim().Length > max ? s.Trim()[..max] : s.Trim()) : null;
    static double? Numero(JsonNode? n, double max) => n is JsonValue v && v.TryGetValue<double>(out var d) && d >= 0 && d <= max ? d : null;
    static void Por(JsonObject o, string k, JsonNode? v) { if (v != null) o[k] = v; }
    static JsonNode? Num(JsonNode? n, double max) => Numero(n, max) is double d ? JsonValue.Create(d) : null;
    static JsonNode? Txt(JsonNode? n, int max = 120) => Texto(n, max) is string s ? JsonValue.Create(s) : null;

    /// <summary>
    /// O resumo do perfil que o painel lê (<c>estavel</c>: CPU, RAM, Windows, GPUs) e a hora do <c>momento</c>. É o que vai em <c>perfil</c> quando
    /// o resto vai cifrado: áudio, rede, telas e programas não saem do PC em claro. Campo novo que o painel passar a ler entra aqui e na tabela da spec 018, 7.2.
    /// </summary>
    public static JsonObject PerfilLegivel(JsonObject perfil)
    {
        var e = perfil["estavel"] as JsonObject ?? perfil;
        var estavel = new JsonObject();
        Por(estavel, "plataforma", Txt(e["plataforma"], 16));
        if (e["cpu"] is JsonObject cpu)
        {
            var o = new JsonObject(); Por(o, "nome", Txt(cpu["nome"])); Por(o, "nucleos", Num(cpu["nucleos"], 1024)); Por(o, "threads", Num(cpu["threads"], 4096));
            estavel["cpu"] = o;
        }
        if (e["ram"] is JsonObject ram)
        {
            var o = new JsonObject(); Por(o, "total_gb", Num(ram["total_gb"], 65536)); Por(o, "total_mb", Num(ram["total_mb"], 65536.0 * 1024));
            estavel["ram"] = o;
        }
        if (e["windows"] is JsonObject win)
        {
            var o = new JsonObject();
            Por(o, "produto", Txt(win["produto"], 60)); Por(o, "versao", Txt(win["versao"], 16)); Por(o, "build", Num(win["build"], 999999)); Por(o, "ubr", Num(win["ubr"], 99999999));
            estavel["windows"] = o;
        }
        if (e["gpus"] is JsonArray gpus)
        {
            var lista = new JsonArray();
            foreach (var g in gpus.OfType<JsonObject>().Take(4))
            {
                var o = new JsonObject();
                Por(o, "nome", Txt(g["nome"])); Por(o, "fabricante", Txt(g["fabricante"], 60)); Por(o, "driver", Txt(g["driver"], 40));
                if (Texto(g["driver_data"]) is string dd && dd.Length == 10 && dd[4] == '-' && dd[7] == '-') o["driver_data"] = dd;
                Por(o, "vram_mb", Num(g["vram_mb"], 1 << 20));
                if (g["integrada"] is JsonValue iv && iv.TryGetValue<bool>(out var b)) o["integrada"] = b;
                lista.Add(o);
            }
            estavel["gpus"] = lista;
        }
        var r = new JsonObject { ["estavel"] = estavel };
        if (Texto((perfil["momento"] as JsonObject)?["hora_utc"], 40) is string hora) r["momento"] = new JsonObject { ["hora_utc"] = hora };
        return r;
    }

    /// <summary>
    /// O evento que sobe em claro ao índice: a nota do "Marcar problema" é texto livre da pessoa (o app a grava no evento <c>marcacao_nota</c>, campo
    /// <c>texto</c>) e vai só dentro do relatório e do arquivo de eventos (cifrados). O <c>marcacao_nota</c> sobe só com o <c>marca</c>, para o índice
    /// saber que houve nota. O Worker também descarta o <c>texto</c> da <c>marcacao</c>, mas o dado nem deve chegar a ele.
    /// </summary>
    public static JsonNode EventoLegivel(JsonObject ev)
    {
        if (!(ev["tipo"] is JsonValue tv && tv.TryGetValue<string>(out var tipo) && tipo is "marcacao" or "marcacao_nota")) return ev.DeepClone();
        var c = (JsonObject)ev.DeepClone();
        c.Remove("texto"); c.Remove("nota"); c.Remove("notas");
        return c;
    }
}
