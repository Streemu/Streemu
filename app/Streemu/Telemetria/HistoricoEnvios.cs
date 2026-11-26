using System.Text.Json;
using Streemu.Util;

namespace Streemu.Telemetria;

/// <summary>Um pacote que o gravador local mandou (spec 018, seção 6.1): o que a pessoa vê em "Ver o que foi" e pode apagar.</summary>
public sealed class EntradaEnvio
{
    /// <summary>O id que o Worker guarda nos arquivos do pacote: é o que o Apagar manda.</summary>
    public string Pacote { get; set; } = "";
    /// <summary>Primeira vez que algo deste pacote subiu (UTC, ISO).</summary>
    public string Quando { get; set; } = "";
    /// <summary>O disparo que abriu o pacote (<see cref="Disparo"/>).</summary>
    public string Motivo { get; set; } = "";
    /// <summary>O estado do diagnóstico quando subiu ("Completo" ou "Anonimo").</summary>
    public string Modo { get; set; } = "";
    public string Sessao { get; set; } = "";
    public List<string> Arquivos { get; set; } = new();
    /// <summary>Bytes já confirmados pelo servidor neste pacote (a parte do complemento soma aqui).</summary>
    public long Bytes { get; set; }
    /// <summary>A pessoa já abriu o diagnóstico depois que este subiu: some o ponto do botão.</summary>
    public bool Visto { get; set; }
}

/// <summary>
/// Histórico dos últimos 30 dias do que o gravador local mandou, em <c>historico-envios.json</c> ao lado das sessões. Só metadados (pacote, hora, disparo, nomes
/// dos arquivos e tamanho): o conteúdo continua nas séries locais, que duram 7 dias. Cifrado (<see cref="CofreLocal"/>, A8 do plano 018); o arquivo antigo, em texto simples, ainda abre.
/// Seguro entre threads; erro de disco nunca derruba quem chama.
/// </summary>
public static class HistoricoEnvios
{
    public static readonly TimeSpan Guarda = TimeSpan.FromDays(30);
    static readonly object _g = new();
    static string Arquivo => Path.Combine(Pasta.Raiz, "historico-envios.json");

    static List<EntradaEnvio> Carregar()
    {
        try
        {
            if (!File.Exists(Arquivo)) return new();
            return JsonSerializer.Deserialize<List<EntradaEnvio>>(CofreLocal.LerArquivo(Arquivo)) ?? new(); // cifrado desde a A8; o de antes, em claro, também abre e é regravado cifrado
        }
        catch { return new(); }
    }

    static void Gravar(List<EntradaEnvio> lista)
    {
        try
        {
            Directory.CreateDirectory(Pasta.Raiz);
            string tmp = Arquivo + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, CofreLocal.Arquivo(JsonSerializer.Serialize(lista)));
            File.Move(tmp, Arquivo, overwrite: true);
        }
        catch { }
    }

    /// <summary>Do mais novo para o mais velho, já sem o que passou de 30 dias.</summary>
    public static List<EntradaEnvio> Listar()
    {
        lock (_g)
        {
            var lista = Carregar();
            int antes = lista.Count;
            lista.RemoveAll(e => !DateTime.TryParse(e.Quando, null, System.Globalization.DateTimeStyles.RoundtripKind, out var q) || DateTime.UtcNow - q.ToUniversalTime() > Guarda);
            if (lista.Count != antes) Gravar(lista);
            return lista.OrderByDescending(e => e.Quando, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>O servidor confirmou <paramref name="bytes"/> de <paramref name="arquivo"/> neste pacote. O mesmo pacote reaparece no complemento: soma e junta os arquivos.</summary>
    public static void Registrar(string pacote, string? motivo, string modo, string sessao, string arquivo, long bytes)
    {
        if (bytes <= 0) return;
        lock (_g)
        {
            var lista = Carregar();
            var e = lista.FirstOrDefault(x => x.Pacote == pacote);
            if (e == null)
            {
                e = new EntradaEnvio { Pacote = pacote, Quando = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), Motivo = motivo ?? "", Modo = modo, Sessao = sessao };
                lista.Add(e);
            }
            if (!e.Arquivos.Contains(arquivo)) e.Arquivos.Add(arquivo);
            e.Bytes += bytes;
            e.Visto = false; // o complemento também é novidade
            Gravar(lista);
        }
    }

    /// <summary>Há pacote que a pessoa ainda não viu: o ponto do botão Diagnóstico.</summary>
    public static bool TemNovo() { lock (_g) return Carregar().Any(e => !e.Visto); }

    /// <summary>A pessoa abriu o histórico: tudo o que havia vira visto.</summary>
    public static void MarcarVistos()
    {
        lock (_g)
        {
            var lista = Carregar();
            if (!lista.Any(e => !e.Visto)) return;
            foreach (var e in lista) e.Visto = true;
            Gravar(lista);
        }
    }

    /// <summary>Tira o pacote do histórico (o Worker já o apagou).</summary>
    public static void Remover(string pacote)
    {
        lock (_g)
        {
            var lista = Carregar();
            if (lista.RemoveAll(e => e.Pacote == pacote) > 0) Gravar(lista);
        }
    }

    /// <summary>Os testes limpam o histórico entre os casos.</summary>
    public static void Apagar() { lock (_g) { try { File.Delete(Arquivo); } catch { } } }
}
