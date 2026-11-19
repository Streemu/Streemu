using System.Runtime.InteropServices;

namespace Streemu.Filme;

/// <summary>
/// As capas que chegaram dos anfitriões (spec 017, D6): só na memória, pelo id do filme com o hash do anúncio, no máximo 16. Usar só na thread da
/// janela (a tela desenha daqui; trocar uma capa descarta a velha na mesma thread, então nunca no meio de um desenho).
/// Uso: <c>capas.Guardar(filme, hash, bitmap); var img = capas.Get(filme, hash); // nulo = o degradê</c>
/// </summary>
public sealed class CapasRecebidas : IDisposable
{
    const int Maximo = 16;
    readonly Dictionary<string, (string Hash, Bitmap Imagem, long Em)> _porFilme = new();

    public Bitmap? Get(string filme, string? hash) => hash != null && _porFilme.TryGetValue(filme, out var c) && c.Hash == hash ? c.Imagem : null;

    public bool Tem(string filme, string hash) => Get(filme, hash) != null;

    public void Guardar(string filme, string hash, Bitmap imagem)
    {
        if (_porFilme.Remove(filme, out var velha)) velha.Imagem.Dispose();
        _porFilme[filme] = (hash, imagem, Environment.TickCount64);
        while (_porFilme.Count > Maximo)
        {
            var maisVelha = _porFilme.MinBy(kv => kv.Value.Em);
            _porFilme.Remove(maisVelha.Key);
            maisVelha.Value.Imagem.Dispose();
        }
    }

    /// <summary>Confere as dimensões pelo cabeçalho ANTES de decodificar os pixels; nulo se o JPEG não abre ou passa de 480 px. Nunca lança.</summary>
    /// <example><code>Bitmap? img = CapasRecebidas.Decodificar(jpeg);</code></example>
    public static Bitmap? Decodificar(byte[] jpeg)
    {
        try
        {
            using (var cabecalho = Image.FromStream(new MemoryStream(jpeg), false, false))
                if (cabecalho.Width is < 16 or > CapaDoFilme.MaxLado || cabecalho.Height is < 16 or > CapaDoFilme.MaxLado) return null;
            using var origem = Image.FromStream(new MemoryStream(jpeg), false, true);
            return new Bitmap(origem);
        }
        catch (Exception e) when (e is ArgumentException or OutOfMemoryException or ExternalException) { return null; }
    }

    public void Dispose()
    {
        foreach (var c in _porFilme.Values) c.Imagem.Dispose();
        _porFilme.Clear();
    }
}
