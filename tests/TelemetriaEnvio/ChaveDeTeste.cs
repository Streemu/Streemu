using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// O par de chaves DE TESTE do servidor falso: nasce em memória a cada execução, nunca é gravado nem impresso (só a pública, para o app cifrar).
/// A privada de verdade (a da equipe) não existe aqui nem em lugar nenhum do repositório. Este decifrador segue o contrato de <c>relay/cifra.js</c>
/// e é escrito sem usar o <c>CifraEnvio</c> do app, para o teste não concordar consigo mesmo se os dois errarem igual.
/// </summary>
sealed class ChaveDeTeste
{
    readonly ECDiffieHellman _par = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>A chave pública (SPKI DER em base64), para <c>STREEMU_TELEMETRIA_CHAVE_TESTE</c>.</summary>
    public string Spki => Convert.ToBase64String(_par.ExportSubjectPublicKeyInfo());

    /// <summary>Abre os quadros colados e devolve o texto claro colado. Lança se a forma estiver torta, a versão for outra ou a etiqueta não bater.</summary>
    public byte[] Decifrar(ReadOnlySpan<byte> quadros, int versaoEsperada = 1)
    {
        var saida = new MemoryStream();
        int pos = 0;
        while (pos < quadros.Length)
        {
            var q = quadros[pos..];
            if (q.Length < 87 + 16 || !q[..4].SequenceEqual("SEC1"u8)) throw new InvalidDataException("quadro sem a marca SEC1 ou curto demais");
            int versao = BinaryPrimitives.ReadUInt16BigEndian(q[4..]);
            if (versao != versaoEsperada) throw new InvalidDataException($"chave_v {versao} no quadro, esperada {versaoEsperada}");
            if (q[6] != 0x04) throw new InvalidDataException("chave efêmera fora do formato não comprimido");
            int n = (int)BinaryPrimitives.ReadUInt32BigEndian(q[83..]);
            if (n < 16 || n > q.Length - 87) throw new InvalidDataException("tamanho do texto cifrado não bate");
            var ef = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = q.Slice(7, 32).ToArray(), Y = q.Slice(39, 32).ToArray() } });
            byte[] segredo = _par.DeriveRawSecretAgreement(ef.PublicKey);
            byte[] aes = HKDF.DeriveKey(HashAlgorithmName.SHA256, segredo, 32, Array.Empty<byte>(), Encoding.ASCII.GetBytes("streemu-telemetria-v1"));
            var claro = new byte[n - 16];
            using (var g = new AesGcm(aes, 16))
                g.Decrypt(q.Slice(71, 12), q.Slice(87, n - 16), q.Slice(87 + n - 16, 16), claro, q[..83]);
            saida.Write(claro);
            pos += 87 + n;
        }
        return saida.ToArray();
    }
}
