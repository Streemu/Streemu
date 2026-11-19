using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Streemu.Telemetria;

/// <summary>
/// O diagnóstico anônimo (quando a pessoa desliga "Enviar diagnóstico"): o app continua mandando log, séries, relatórios e perfil do PC, o
/// suficiente para achar e analisar bugs, mas sem dizer de quem veio. Duas partes: aqui, a troca de nomes por <c>pessoa-N</c> em tudo que sobe
/// (o N é o mesmo para o mesmo nome dentro de uma abertura do app, então ainda dá para seguir "quem fez o quê" num log), e no <see cref="Envio"/> a
/// troca do id do PC e da instalação por um id anônimo próprio e a retirada de Discord, apelido e impressão do hardware.
/// Os nomes vêm do próprio app: nome e apelido da pessoa, Discord, amigos da sala. É o melhor esforço: um nome que o app nunca viu (digitado num
/// título de janela, por exemplo) passa; por isso o envio só começa quando a sala já trouxe os amigos (<see cref="Pronto"/>) e a promessa feita
/// à pessoa é "sem seu nome nem o dos amigos que o app conhece", não "impossível de reconhecer".
/// </summary>
public static class Anonimizador
{
    static readonly object _g = new();
    static readonly Dictionary<string, string> _termos = new(StringComparer.OrdinalIgnoreCase);
    static Regex? _regex;
    static volatile bool _pronto;

    /// <summary>Campos de identidade que nunca sobem no modo anônimo (o resto do evento passa pela troca de nomes).</summary>
    static readonly string[] CamposDeIdentidade = { "discord_id", "discord_nome", "apelido", "install", "outro_install", "outro_apelido", "hw" };

    /// <summary>Já dá para subir: a sala trouxe os amigos (ou passou tempo suficiente sem sala). Antes disso um nome ainda desconhecido vazaria.</summary>
    public static bool Pronto => _pronto;
    public static void MarcarPronto() => _pronto = true;

    /// <summary>Só os testes.</summary>
    public static void Reiniciar() { lock (_g) { _termos.Clear(); _regex = null; } _pronto = false; }

    /// <summary>Quantos nomes o app conhece (testes).</summary>
    public static int Conhecidos { get { lock (_g) return _termos.Count; } }

    /// <summary>Ensina um nome (apelido, Discord, amigo). Nomes de menos de 3 letras não entram: trocariam pedaços de palavras comuns.</summary>
    public static void Registrar(string? nome)
    {
        nome = nome?.Trim();
        if (nome == null || nome.Length < 3 || nome == "convidado") return;
        lock (_g)
        {
            if (_termos.ContainsKey(nome)) return;
            _termos[nome] = "pessoa-" + (_termos.Count + 1);
            _regex = null;
        }
    }

    /// <summary>Troca cada nome conhecido (palavra inteira, sem diferenciar maiúsculas) pelo apelido anônimo dele.</summary>
    public static string Aplicar(string texto)
    {
        if (texto.Length == 0) return texto;
        Regex? r; Dictionary<string, string> mapa;
        lock (_g)
        {
            if (_termos.Count == 0) return texto;
            if (_regex == null)
            {
                var alternativas = _termos.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape);
                _regex = new Regex(@"(?<![\p{L}\p{N}])(" + string.Join("|", alternativas) + @")(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            r = _regex; mapa = new Dictionary<string, string>(_termos, StringComparer.OrdinalIgnoreCase);
        }
        return r.Replace(texto, m => mapa.TryGetValue(m.Value, out var p) ? p : "pessoa-0");
    }

    /// <summary>Evento (uma linha JSON) para subir no modo anônimo: sem campos de identidade e com os nomes trocados.</summary>
    public static JsonObject? Evento(JsonObject ev)
    {
        foreach (var k in CamposDeIdentidade) ev.Remove(k);
        return JsonNode.Parse(Aplicar(ev.ToJsonString())) as JsonObject;
    }

    /// <summary>Eventos que nem são gravados no modo anônimo: texto livre que a pessoa escreveu.</summary>
    public static bool NaoGrava(string tipo) => tipo == "marcacao_nota";
}
