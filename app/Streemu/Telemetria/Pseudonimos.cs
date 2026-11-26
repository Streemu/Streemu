using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace Streemu.Telemetria;

/// <summary>
/// O amigo como <c>amigo-N</c> em tudo que sobe do PC, nos três estados (spec 018, D32): nome, apelido e id de quem está na sala viram um número que vale
/// só nesta abertura do app (não liga uma abertura à outra nem a pessoa a quem ela é). Assim a equipe analisa "a live do amigo-3 travou" sem saber quem
/// é o amigo-3, e quem está em Desligado ou Anônimo não aparece no diagnóstico de quem está Ligado.
/// <para>
/// Dois caminhos: na origem (o evento da live e a série de rede pedem <see cref="Para"/> e já gravam <c>amigo-N</c>) e no envio (<see cref="Aplicar"/>
/// troca, no texto do log, dos eventos e dos relatórios, cada nome ou id conhecido, palavra inteira, sem diferenciar maiúsculas). O mesmo amigo tem o mesmo
/// <c>amigo-N</c> no log, nos eventos e no <c>rede.ndjson</c>. No Anônimo isto roda ANTES do <see cref="Anonimizador"/>, que troca o que sobrar (a própria
/// pessoa, o Discord) por <c>pessoa-N</c>.
/// </para>
/// É o melhor esforço, como o <see cref="Anonimizador"/>: um nome que o app nunca viu passa, e nomes de menos de 3 letras só são trocados nos campos que
/// pedem <see cref="Para"/> (trocá-los no texto livre estragaria pedaços de palavras comuns). Fica de fora a própria pessoa: o Completo é identificado.
/// </summary>
public static class Pseudonimos
{
    public const string Prefixo = "amigo-";
    /// <summary>Ligação de privada: o app já grava "convidado" (D5); continua assim, sem número.</summary>
    const string Convidado = "convidado";

    static readonly object _g = new();
    /// <summary>Id ou nome (como o app o conhece) → número do amigo.</summary>
    static readonly Dictionary<string, int> _numeros = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Como cada termo aparece no texto (cru e como o JSON o escreve: acento vira ã, aspas viram \") → número, só os de 3 letras ou mais.</summary>
    static readonly Dictionary<string, int> _texto = new(StringComparer.OrdinalIgnoreCase);
    static int _ultimo;
    static Regex? _regex;

    /// <summary>Quantos amigos o app conhece nesta abertura (testes).</summary>
    public static int Conhecidos { get { lock (_g) return _ultimo; } }

    /// <summary>Só os testes.</summary>
    public static void Reiniciar() { lock (_g) { _numeros.Clear(); _texto.Clear(); _ultimo = 0; _regex = null; } }

    /// <summary>Ensina um amigo (id e nome): daqui em diante os dois viram o mesmo <c>amigo-N</c>. Nome vazio, "convidado" e o de quem usa o app não entram.</summary>
    public static void Registrar(string? id, string? nome) => Numero(id, nome);

    /// <summary>
    /// O <c>amigo-N</c> deste amigo (cria o número se for a primeira vez). Prefere o id, que não repete; sem id, vale o nome. Sem nenhum dos dois: nulo.
    /// "convidado" (a outra ponta de uma live privada) volta como está.
    /// </summary>
    public static string? Para(string? id, string? nome)
    {
        if (string.Equals(nome?.Trim(), Convidado, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(id)) return Convidado;
        int n = Numero(id, nome);
        return n == 0 ? null : Prefixo + n;
    }

    /// <summary>O amigo-N de um nome só (série de rede, relatórios): igual a <see cref="Para"/> sem id.</summary>
    public static string? Para(string? nome) => Para(null, nome);

    static int Numero(string? id, string? nome)
    {
        id = id?.Trim(); nome = nome?.Trim();
        bool temId = !string.IsNullOrEmpty(id), temNome = !string.IsNullOrEmpty(nome) && !string.Equals(nome, Convidado, StringComparison.OrdinalIgnoreCase);
        if (!temId && !temNome) return 0;
        lock (_g)
        {
            int n = 0;
            if (temId && _numeros.TryGetValue(id!, out var porId)) n = porId;
            else if (temNome && _numeros.TryGetValue(nome!, out var porNome)) n = porNome;
            if (n == 0) n = ++_ultimo;
            if (temId) Guardar(id!, n);
            if (temNome) Guardar(nome!, n);
            return n;
        }
    }

    static void Guardar(string termo, int n)
    {
        if (_numeros.TryAdd(termo, n) && termo.Length >= 3)
        {
            // cru e como o JSON do app o escreve (as linhas dos eventos usam o escape padrão do System.Text.Json)
            _texto[termo] = n;
            var json = JavaScriptEncoder.Default.Encode(termo);
            if (json != termo) _texto[json] = n;
            var solto = JavaScriptEncoder.UnsafeRelaxedJsonEscaping.Encode(termo);
            if (solto != termo) _texto[solto] = n;
            _regex = null;
        }
    }

    /// <summary>Palavras que o log e os eventos do app usam: um amigo com esse apelido continua amigo-N nos campos (<see cref="Para"/>), mas o texto livre não as troca.</summary>
    static readonly HashSet<string> Reservadas = new(StringComparer.OrdinalIgnoreCase)
    {
        "live", "lives", "sala", "papel", "tipo", "erro", "aviso", "filme", "amigo", "grupo", "falha", "captura", "telemetria", "discord", "streemu",
        "transmissao", "transmite", "assiste", "envia", "ligacao", "sessao", "encerrada", "propria", "privada", "relay", "direto", "host", "viewer",
    };

    /// <summary>Troca cada amigo conhecido (nome ou id, palavra inteira, sem diferenciar maiúsculas) por <c>amigo-N</c>.</summary>
    public static string Aplicar(string texto)
    {
        if (texto.Length == 0) return texto;
        Regex r; Dictionary<string, int> mapa;
        lock (_g)
        {
            if (_texto.Count == 0) return texto;
            // palavras do próprio app ("Live encerrada", "papel") nunca são trocadas, e uma chave de JSON ("nome": ...) também não: só o valor muda
            _regex ??= new Regex(@"(?<![\p{L}\p{N}])(" + string.Join("|", _texto.Keys.Where(k => !Reservadas.Contains(k)).DefaultIfEmpty("￿").OrderByDescending(k => k.Length).Select(Regex.Escape)) + @")(?![\p{L}\p{N}])(?!""\s*:)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            r = _regex; mapa = new Dictionary<string, int>(_texto, StringComparer.OrdinalIgnoreCase);
        }
        return r.Replace(texto, m => mapa.TryGetValue(m.Value, out var n) ? Prefixo + n : Prefixo + "0");
    }
}
