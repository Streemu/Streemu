namespace Streemu;

/// <summary>
/// Ganchos de teste: fazem um cenário da bateria local acabar assim que o resultado que o teste confere está pronto, em vez de esperar o tempo todo.
/// STREEMU_TEST_SECONDS passa a ser o TETO (para não esconder um caso lento). Só valem com STREEMU_TEST_PRONTO, que o scripts/testes/*.ps1 define só nos
/// cenários que conferem a condição; sem ela (o app de verdade, os outros cenários) nada muda. Ver docs/TESTES.md.
/// Formatos de STREEMU_TEST_PRONTO:
///   arquivos:X,Y       pronto quando todos os arquivos existem na pasta do teste (os outros papéis e o próprio A gravam o que o teste lê)
///   tolerancia:N       som na live: pronto N s depois de a segunda ligação cair (o "saiu" só sai depois da tolerância de 10 s do anfitrião)
/// </summary>
public sealed partial class MainForm
{
    static readonly string? TestPronto = Environment.GetEnvironmentVariable("STREEMU_TEST_PRONTO");

    /// <summary>Espera até <paramref name="segundos"/> (o teto), parando antes quando <paramref name="pronto"/> ficar verdadeiro; depois dá uma folga para o resto assentar.</summary>
    static async Task TesteAteProntoAsync(int segundos, Func<bool> pronto, int folgaMs = 800)
    {
        var teto = DateTime.Now.AddSeconds(segundos);
        while (DateTime.Now < teto && !pronto()) await Task.Delay(100);
        if (DateTime.Now < teto) await Task.Delay(folgaMs);
    }

    /// <summary>A condição de "pronto" pedida por STREEMU_TEST_PRONTO, ou nulo (sem pedido, ou formato que este ponto não usa): segue o tempo todo.</summary>
    Func<bool>? TesteCondicaoPronta()
    {
        if (TestPronto is not { Length: > 0 } p) return null;
        if (p.StartsWith("arquivos:"))
        {
            var nomes = p["arquivos:".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return () => nomes.All(n => File.Exists(Path.Combine(TestDir!, n)));
        }
        if (p.StartsWith("tolerancia:") && int.TryParse(p["tolerancia:".Length..], out int segundos))
        {
            bool viuDuas = false;
            DateTime? voltouA1 = null;
            return () =>
            {
                int n = _links.Values.Count(l => l.IAmSender && l.Connected);
                if (n >= 2) { viuDuas = true; voltouA1 = null; }
                else if (viuDuas && voltouA1 == null) voltouA1 = DateTime.Now;
                return voltouA1 is { } t && (DateTime.Now - t).TotalSeconds >= segundos;
            };
        }
        return null;
    }
}
