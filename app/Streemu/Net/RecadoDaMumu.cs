using System.Globalization;
using System.Text.Json.Nodes;

namespace Streemu.Net;

/// <summary>
/// Um recado dos responsáveis (spec 031): <paramref name="Id"/> muda a cada recado; <paramref name="Texto"/> é o que a Mumu diz; <paramref name="Titulo"/> é o assunto curto
/// do histórico e <paramref name="Data"/> o dia em que saiu (os dois podem faltar nos recados do formato antigo). <paramref name="Sprite"/> é a pose fixa da Mumu
/// (nome curto, ex.: "pulando" = <c>Arte/mumu-pulando.png</c>); sem ela, a aba sorteia (<see cref="Streemu.Ui.MumuPoses"/>).
/// </summary>
public sealed record RecadoDaMumu(string Id, string Texto, string? Titulo = null, DateOnly? Data = null, string? Sprite = null)
{
    /// <summary>
    /// Pergunta ao servidor do grupo os recados vigentes, do mais novo ao mais antigo. Lista vazia quando não há recado; lança <see cref="InvalidOperationException"/>
    /// quando o servidor não responde como esperado (a aba guarda os últimos conhecidos).
    /// Exemplo: <c>var recados = await RecadoDaMumu.BuscarAsync(RelayConfig.Load(state)!);</c>
    /// </summary>
    public static async Task<IReadOnlyList<RecadoDaMumu>> BuscarAsync(RelayConfig.Settings servidor, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var resposta = await RelayConfig.EnviarAsync(http, () => new HttpRequestMessage(HttpMethod.Get, servidor.Url.TrimEnd('/') + "/recado"), ct);
        string corpo = await resposta.Content.ReadAsStringAsync(ct);
        if (!resposta.IsSuccessStatusCode) throw new InvalidOperationException($"o servidor respondeu {(int)resposta.StatusCode} em /recado (esperado 200)");
        return LerLista(corpo);
    }

    /// <summary>
    /// Lê <c>{"recados":[...], "recado":{...}|null}</c>. Sem <c>recados</c> (o Worker de antes da lista), usa só <c>recado</c>. Corpo fora do formato lança com o valor recebido.
    /// </summary>
    public static IReadOnlyList<RecadoDaMumu> LerLista(string corpo)
    {
        JsonNode? raiz;
        try { raiz = JsonNode.Parse(corpo); }
        catch (System.Text.Json.JsonException) { throw new InvalidOperationException($"/recado não devolveu JSON (recebido: {Resumo(corpo)})"); }
        if (raiz is not JsonObject objeto || !objeto.ContainsKey("recado") && !objeto.ContainsKey("recados"))
            throw new InvalidOperationException($"/recado sem o campo \"recado\" (recebido: {Resumo(corpo)})");
        var lista = new List<RecadoDaMumu>();
        if (objeto["recados"] is JsonArray todos) { foreach (var item in todos) if (Um(item) is { } r) lista.Add(r); }
        else if (Um(objeto["recado"]) is { } so) lista.Add(so);
        return lista;
    }

    /// <summary>
    /// A regra do selo "NOVO" da barra e do destaque da aba: o recado mais novo (o primeiro da lista) ainda não é o último que a pessoa viu.
    /// Lista vazia (sem recado, ou o recado foi retirado) nunca é novidade; quem nunca viu nenhum vê o primeiro como novo.
    /// Exemplo: <c>RecadoDaMumu.TemNovo(recados, estado.MumuRecadoVisto)</c>.
    /// </summary>
    public static bool TemNovo(IReadOnlyList<RecadoDaMumu> recados, string? idVisto) => recados.Count > 0 && recados[0].Id != idVisto;

    /// <summary>Variável só de teste: um JSON no formato de <c>/recado</c> que substitui a busca ao servidor (docs/VARIAVEIS-DE-TESTE.md).</summary>
    public const string VariavelDeTeste = "STREEMU_MUMU_RECADO_TESTE";

    /// <summary>O mais novo, ou nulo (mantido para quem só quer o recado vigente).</summary>
    public static RecadoDaMumu? Ler(string corpo) => LerLista(corpo).FirstOrDefault();

    /// <summary>A lista no mesmo formato da resposta de <c>/recado</c> (para guardar no estado e ler de novo com <see cref="LerLista"/>).</summary>
    public static string Guardar(IEnumerable<RecadoDaMumu> recados)
    {
        var lista = new JsonArray();
        foreach (var r in recados)
        {
            var o = new JsonObject { ["id"] = r.Id, ["texto"] = r.Texto };
            if (r.Titulo != null) o["titulo"] = r.Titulo;
            if (r.Data is { } d) o["data"] = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (r.Sprite != null) o["sprite"] = r.Sprite;
            lista.Add(o);
        }
        return new JsonObject { ["recados"] = lista }.ToJsonString();
    }

    static RecadoDaMumu? Um(JsonNode? no)
    {
        if (no is not JsonObject r) return null;
        string? id = Valor(r["id"]), texto = Valor(r["texto"]), titulo = Valor(r["titulo"]), data = Valor(r["data"]), sprite = Valor(r["sprite"]);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(texto)) return null;
        DateOnly? dia = DateOnly.TryParseExact(data, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d
            // formato antigo: o id é "yyyyMMdd-HHmm" (o mesmo que a aba Recados do painel gera); serve de data quando não vem outra
            : id.Length >= 8 && DateOnly.TryParseExact(id[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2) ? d2 : null;
        return new RecadoDaMumu(id.Trim(), texto.Trim(), string.IsNullOrWhiteSpace(titulo) ? null : titulo.Trim(), dia, string.IsNullOrWhiteSpace(sprite) ? null : sprite.Trim());
    }

    static string? Valor(JsonNode? no) => no is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static string Resumo(string corpo) => corpo.Length <= 80 ? corpo : corpo[..80] + "...";
}
