using System.Text.RegularExpressions;

namespace Streemu.Util;

/// <summary>
/// Apelidos que só esta pessoa vê (plano 019, etapa 7): id da conta do amigo → como ela quer chamá-lo. Nunca vão ao servidor, ao log nem à telemetria.
/// Sem dependência de tela: guarda no dicionário que o <see cref="AppState"/> serializa. Uso: <c>apelidos.Definir(id, "Bru"); apelidos.Nome(id, "Bruno")</c>.
/// </summary>
public sealed class ApelidosLocais
{
    public const int Limite = 24;
    readonly Dictionary<string, string> _mapa;
    /// <summary>Disparado quando algo mudou (quem guarda grava o estado).</summary>
    public event Action? Mudou;

    public ApelidosLocais(Dictionary<string, string>? guardados = null) => _mapa = guardados ?? new();

    public int Quantidade => _mapa.Count;
    public IReadOnlyDictionary<string, string> Todos => _mapa;

    /// <summary>O nome para mostrar na tela: o apelido local se houver, senão o nome real que o amigo usa no grupo.</summary>
    public string Nome(string id, string apelidoReal) => _mapa.TryGetValue(id, out var a) ? a : apelidoReal;
    public bool Tem(string id) => _mapa.ContainsKey(id);

    /// <summary>Grava o apelido (aparado, até 24 caracteres). Vazio tira. Devolve o que ficou guardado, ou nulo se ficou sem apelido.</summary>
    public string? Definir(string id, string? apelido)
    {
        if (string.IsNullOrEmpty(id)) return null;
        string limpo = (apelido ?? "").Trim();
        if (limpo.Length > Limite) limpo = limpo[..Limite].TrimEnd();
        if (limpo.Length == 0) { Tirar(id); return null; }
        if (_mapa.TryGetValue(id, out var atual) && atual == limpo) return limpo;
        _mapa[id] = limpo;
        Mudou?.Invoke();
        return limpo;
    }

    public void Tirar(string id) { if (_mapa.Remove(id)) Mudou?.Invoke(); }

    /// <summary>Palavras que aparecem nas frases do log: como apelido, o filtro trocaria essas palavras pelo nome do amigo em linhas que não falam dele.</summary>
    static readonly string[] PalavrasDoLog = { "amigo", "live", "sala", "grupo", "filme", "você", "voce", "streemu", "arquivo", "conta" };

    /// <summary>
    /// Por que este apelido não pode ser usado (a frase para a pessoa), ou nulo se pode. Vazio pode (tira o apelido). O filtro do log troca o apelido pelo
    /// nome real em TODA linha: um apelido igual ao nome real de outra pessoa do grupo, ao apelido de outro amigo, ao seu nome ou a uma palavra do log faria
    /// o log atribuir a um amigo o que outro fez (ex.: chamar o Bruno de "Davi" e as linhas do Davi saírem como do Bruno).
    /// </summary>
    public string? Problema(string id, string? apelido, IEnumerable<string> nomesReaisDosOutros, string meuNome)
    {
        string limpo = (apelido ?? "").Trim();
        if (limpo.Length == 0) return null;
        if (limpo.Length > Limite) limpo = limpo[..Limite].TrimEnd();
        if (limpo.Length < 2) return "O apelido precisa de pelo menos 2 letras.";
        bool Igual(string? x) => string.Equals(x?.Trim(), limpo, StringComparison.CurrentCultureIgnoreCase);
        if (Igual(meuNome)) return "Esse é o seu nome no grupo. Escolha outro apelido.";
        if (nomesReaisDosOutros.Any(Igual)) return "Já tem alguém no grupo com esse nome. Escolha outro apelido para não confundir.";
        if (_mapa.Any(p => p.Key != id && Igual(p.Value))) return "Você já usa esse apelido para outro amigo. Escolha outro.";
        if (PalavrasDoLog.Any(Igual)) return "Essa palavra o Streemu usa nos avisos. Escolha outro apelido.";
        return null;
    }

    /// <summary>
    /// O filtro do <see cref="Log"/>: troca cada apelido local pelo nome real. O nome real vem de <paramref name="nomeReal"/> (id → nome; nulo se o amigo
    /// não é mais conhecido, e então o apelido vira "amigo"). Só troca a palavra inteira ("Bru" em "Brunel" fica); sem apelidos devolve nulo, e o
    /// <c>Log</c> não filtra nada.
    /// </summary>
    public Func<string, string>? CriarFiltro(Func<string, string?> nomeReal)
    {
        if (_mapa.Count == 0) return null;
        var pares = _mapa.Select(p => (Apelido: p.Value, Id: p.Key)).OrderByDescending(p => p.Apelido.Length).ToList(); // o maior primeiro: "Ana Maria" antes de "Ana"
        return linha =>
        {
            foreach (var (apelido, id) in pares)
            {
                if (!linha.Contains(apelido, StringComparison.OrdinalIgnoreCase)) continue;
                string real = nomeReal(id) ?? "amigo";
                linha = Regex.Replace(linha, $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(apelido)}(?![\p{{L}}\p{{N}}_])", real.Replace("$", "$$"), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            return linha;
        };
    }
}
