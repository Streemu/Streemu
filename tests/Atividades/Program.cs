using System.Text.Json.Nodes;
using Streemu.Net;

// Testes das frases de atividade do Início (plano 019, etapa 9). Sem janela, sem rede, sem estado salvo.
// Cada caso imprime "OK    atividades-nome" ou "FALHA atividades-nome : motivo".

int falhas = 0;
void Caso(string nome, Action f)
{
    try { f(); Console.WriteLine("OK    atividades-" + nome); }
    catch (Exception e) { falhas++; Console.WriteLine($"FALHA atividades-{nome} : {e.Message}"); }
}
void Exige(bool ok, string msg) { if (!ok) throw new Exception(msg); }

var agora = new DateTimeOffset(2026, 11, 18, 15, 0, 0, TimeSpan.Zero);
long Ms(TimeSpan atras) => (agora - atras).ToUnixTimeMilliseconds();
long contador = 0;
Atividade A(string tipo, string conta, string apelido, TimeSpan atras, JsonObject? dados = null, string grupo = "g1") =>
    new(++contador, Ms(atras), conta, apelido, tipo, dados ?? new JsonObject(), grupo);
string Real(string conta, string apelido) => apelido;
string[] Textos(IEnumerable<Atividade> itens, Func<string, string, string>? nome = null) =>
    FrasesDeAtividade.Montar(itens, nome ?? Real, agora).Select(l => l.Texto).ToArray();

Caso("todas-as-frases", () =>
{
    var t = Textos(new[]
    {
        A("filme", "c1", "Ana", TimeSpan.FromMinutes(1)),
        A("entrou", "c2", "Bia", TimeSpan.FromMinutes(2)),
        A("saiu", "c3", "Caio", TimeSpan.FromMinutes(3)),
        A("assistindo", "c4", "Duda", TimeSpan.FromMinutes(4), new JsonObject { ["alvo"] = "c1", ["alvo_apelido"] = "Ana" }),
        A("live", "c1", "Ana", TimeSpan.FromMinutes(5), new JsonObject { ["jogo"] = "Valorant", ["sala"] = "s1" }),
    });
    Exige(t.SequenceEqual(new[] { "Ana abriu um filme", "Bia entrou no grupo", "Caio saiu do grupo", "Duda começou a assistir Ana", "Ana está ao vivo: Valorant" }), string.Join(" | ", t));
});

Caso("live-e-fim-da-mesma-sala-viram-uma-linha", () =>
{
    var itens = new[]
    {
        A("live", "c1", "Ana", TimeSpan.FromMinutes(60), new JsonObject { ["jogo"] = "Valorant", ["sala"] = "s1" }),
        A("live_fim", "c1", "Ana", TimeSpan.FromMinutes(18), new JsonObject { ["sala"] = "s1", ["minutos"] = 42 }),
    };
    var t = Textos(itens);
    Exige(t.Length == 1 && t[0] == "Ana transmitiu Valorant por 42 min", string.Join(" | ", t));
});

Caso("live-sem-fim-esta-ao-vivo", () =>
{
    var t = Textos(new[] { A("live", "c1", "Ana", TimeSpan.FromMinutes(3), new JsonObject { ["jogo"] = "Minecraft", ["sala"] = "s9" }) });
    Exige(t.Length == 1 && t[0] == "Ana está ao vivo: Minecraft", string.Join(" | ", t));
});

Caso("fim-sem-o-inicio-nao-inventa-o-jogo", () =>
{
    var t = Textos(new[] { A("live_fim", "c1", "Ana", TimeSpan.FromMinutes(3), new JsonObject { ["sala"] = "s1", ["minutos"] = 130 }) });
    Exige(t.Length == 1 && t[0] == "Ana transmitiu por 2 h 10 min", string.Join(" | ", t));
});

Caso("duas-lives-de-salas-diferentes-nao-se-misturam", () =>
{
    var itens = new[]
    {
        A("live", "c1", "Ana", TimeSpan.FromHours(5), new JsonObject { ["jogo"] = "Valorant", ["sala"] = "s1" }),
        A("live_fim", "c1", "Ana", TimeSpan.FromHours(4), new JsonObject { ["sala"] = "s1", ["minutos"] = 60 }),
        A("live", "c1", "Ana", TimeSpan.FromMinutes(10), new JsonObject { ["jogo"] = "Minecraft", ["sala"] = "s2" }),
    };
    var t = Textos(itens);
    Exige(t.SequenceEqual(new[] { "Ana está ao vivo: Minecraft", "Ana transmitiu Valorant por 1 h" }), string.Join(" | ", t));
});

Caso("mesma-sala-em-grupos-diferentes-nao-junta", () =>
{
    var itens = new[]
    {
        A("live", "c1", "Ana", TimeSpan.FromMinutes(30), new JsonObject { ["jogo"] = "Valorant", ["sala"] = "s1" }, "g1"),
        A("live_fim", "c1", "Ana", TimeSpan.FromMinutes(10), new JsonObject { ["sala"] = "s1", ["minutos"] = 20 }, "g1"),
        A("live", "c1", "Ana", TimeSpan.FromMinutes(29), new JsonObject { ["jogo"] = "Valorant", ["sala"] = "s1" }, "g2"),
    };
    var t = Textos(itens);
    Exige(t.Length == 2, "cada grupo tem a sua linha: " + string.Join(" | ", t));
});

Caso("apelido-local-no-autor-e-no-alvo", () =>
{
    var t = Textos(new[] { A("assistindo", "c4", "Eduarda", TimeSpan.FromMinutes(1), new JsonObject { ["alvo"] = "c1", ["alvo_apelido"] = "Ana Paula" }) },
        (conta, apelido) => conta == "c4" ? "Duda" : conta == "c1" ? "Aninha" : apelido);
    Exige(t.Length == 1 && t[0] == "Duda começou a assistir Aninha", string.Join(" | ", t));
});

Caso("do-mais-novo-para-o-mais-velho-e-no-maximo-20", () =>
{
    var itens = Enumerable.Range(1, 30).Select(i => A("entrou", "c" + i, "Pessoa" + i, TimeSpan.FromMinutes(i))).ToList();
    var l = FrasesDeAtividade.Montar(itens, Real, agora);
    Exige(l.Count == FrasesDeAtividade.MaxLinhas && l[0].Texto == "Pessoa1 entrou no grupo" && l[19].Texto == "Pessoa20 entrou no grupo", $"{l.Count} linhas; primeira '{l[0].Texto}'");
});

Caso("tipo-desconhecido-e-ignorado", () =>
{
    var t = Textos(new[] { A("coisa_nova", "c1", "Ana", TimeSpan.FromMinutes(1)), A("entrou", "c2", "Bia", TimeSpan.FromMinutes(2)) });
    Exige(t.SequenceEqual(new[] { "Bia entrou no grupo" }), string.Join(" | ", t));
});

Caso("vazio-nao-da-linha", () =>
{
    Exige(Textos(Array.Empty<Atividade>()).Length == 0, "sem itens, sem linhas (a tela mostra o texto de vazio)");
    Exige(FrasesDeAtividade.Vazio == "Nada por aqui ainda. O que o grupo fizer aparece aqui.", "texto do vazio");
});

Caso("minhas-atividades-dizem-voce", () =>
{
    var itens = new[]
    {
        A("assistindo", "c4", "Duda", TimeSpan.FromMinutes(1), new JsonObject { ["alvo"] = "eu", ["alvo_apelido"] = "Lia" }),
        A("filme", "eu", "Lia", TimeSpan.FromMinutes(2)),
        A("live", "eu", "Lia", TimeSpan.FromMinutes(3), new JsonObject { ["jogo"] = "Valorant", ["sala"] = "s9" }),
    };
    var t = FrasesDeAtividade.Montar(itens, Real, agora, meuId: "eu").Select(l => l.Texto).ToArray();
    Exige(t.SequenceEqual(new[] { "Duda começou a assistir você", "Você abriu um filme", "Você está ao vivo: Valorant" }), string.Join(" | ", t));
    var semMim = FrasesDeAtividade.Montar(itens, Real, agora).Select(l => l.Texto).ToArray(); // o relatório não passa meuId: nomes reais
    Exige(semMim[1] == "Lia abriu um filme", string.Join(" | ", semMim));
});

Caso("hora-relativa", () =>
{
    string Q(TimeSpan d) => FrasesDeAtividade.Quando(Ms(d), agora);
    Exige(Q(TimeSpan.FromSeconds(20)) == "agora", Q(TimeSpan.FromSeconds(20)));
    Exige(Q(TimeSpan.FromMinutes(5)) == "há 5 min", Q(TimeSpan.FromMinutes(5)));
    Exige(Q(TimeSpan.FromHours(3)) == "há 3 h", Q(TimeSpan.FromHours(3)));
    Exige(Q(TimeSpan.FromHours(30)) == "ontem", Q(TimeSpan.FromHours(30)));
    Exige(Q(TimeSpan.FromDays(5)) == "há 5 dias", Q(TimeSpan.FromDays(5)));
    Exige(Q(TimeSpan.FromMinutes(-5)) == "agora", "hora no futuro vira agora: " + Q(TimeSpan.FromMinutes(-5)));
});

Caso("duracao", () =>
{
    Exige(FrasesDeAtividade.Duracao(0) == "menos de 1 min" && FrasesDeAtividade.Duracao(42) == "42 min" && FrasesDeAtividade.Duracao(60) == "1 h" && FrasesDeAtividade.Duracao(125) == "2 h 05 min", "formato da duração");
});

Caso("ler-item-do-servidor", () =>
{
    var json = JsonNode.Parse("""{"id":7,"hora":1763478000000,"conta":"c-1","apelido":"Ana","tipo":"live_fim","dados":{"sala":"s1","minutos":42}}""");
    var a = Atividade.Ler(json, "g1")!;
    Exige(a != null && a.Id == 7 && a.Conta == "c-1" && a.Tipo == "live_fim" && a.Minutos == 42 && a.Campo("sala") == "s1" && a.GrupoId == "g1", "campos lidos");
    Exige(Atividade.Ler(JsonNode.Parse("""{"id":1,"hora":1,"tipo":"entrou"}""")) == null, "sem conta é ignorado");
    Exige(Atividade.Ler(JsonNode.Parse("""{"id":1,"hora":0,"conta":"c","tipo":"entrou"}""")) == null, "sem hora é ignorado");
    Exige(Atividade.Ler(JsonNode.Parse("[1,2]")) == null && Atividade.Ler(null) == null, "entrada inválida é ignorada");
    var semDados = Atividade.Ler(JsonNode.Parse("""{"id":2,"hora":5,"conta":"c","tipo":"filme"}"""))!;
    Exige(semDados.Dados.Count == 0 && semDados.Apelido == "amigo", "dados e apelido faltando têm padrão");
});

return falhas == 0 ? 0 : 1;
