using Streemu.Util;

// Testes dos apelidos locais e do filtro do log (plano 019, etapa 7). Sem janela, sem rede, sem estado salvo.
// Cada caso imprime "OK    apelidos-nome" ou "FALHA apelidos-nome : motivo".

int falhas = 0;
void Caso(string nome, Action f)
{
    Log.Filtro = null;
    try { f(); Console.WriteLine("OK    apelidos-" + nome); }
    catch (Exception e) { falhas++; Console.WriteLine($"FALHA apelidos-{nome} : {e.Message}"); }
    finally { Log.Filtro = null; }
}
void Exige(bool ok, string msg) { if (!ok) throw new Exception(msg); }

Caso("definir-e-mostrar", () =>
{
    var a = new ApelidosLocais();
    Exige(a.Nome("c-1", "Bruno") == "Bruno", "sem apelido devia mostrar o nome real");
    Exige(a.Definir("c-1", "Bru") == "Bru", "Definir devia devolver o apelido");
    Exige(a.Nome("c-1", "Bruno") == "Bru", "devia mostrar o apelido");
    Exige(a.Nome("c-2", "Eduardo") == "Eduardo", "outro amigo não muda");
    Exige(a.Quantidade == 1, "devia ter 1 apelido");
});

Caso("tirar", () =>
{
    var a = new ApelidosLocais();
    a.Definir("c-1", "Bru"); a.Tirar("c-1");
    Exige(a.Nome("c-1", "Bruno") == "Bruno" && !a.Tem("c-1"), "depois de tirar volta ao nome real");
    a.Tirar("c-que-nao-existe"); // não pode lançar
});

Caso("vazio-ou-espacos-tira", () =>
{
    var a = new ApelidosLocais();
    a.Definir("c-1", "Bru");
    Exige(a.Definir("c-1", "   ") == null, "só espaços devia tirar");
    Exige(!a.Tem("c-1"), "o apelido devia ter saído");
    a.Definir("c-1", "Bru"); a.Definir("c-1", null);
    Exige(!a.Tem("c-1"), "nulo devia tirar");
});

Caso("limite-de-24-caracteres", () =>
{
    var a = new ApelidosLocais();
    string guardado = a.Definir("c-1", new string('x', 40))!;
    Exige(guardado.Length == ApelidosLocais.Limite && ApelidosLocais.Limite == 24, $"devia cortar em 24, ficou {guardado.Length}");
    Exige(a.Definir("c-1", "  Bru  ") == "Bru", "devia aparar os espaços");
});

Caso("avisa-quando-muda", () =>
{
    var a = new ApelidosLocais(); int n = 0; a.Mudou += () => n++;
    a.Definir("c-1", "Bru"); a.Definir("c-1", "Bru"); a.Definir("c-1", "Bruninho"); a.Tirar("c-1"); a.Tirar("c-1");
    Exige(n == 3, $"devia avisar 3 vezes (definir, mudar, tirar), avisou {n}");
});

Caso("guardados-voltam-ao-reabrir", () =>
{
    var estado = new Dictionary<string, string> { ["c-1"] = "Bru" };
    var a = new ApelidosLocais(estado);
    Exige(a.Nome("c-1", "Bruno") == "Bru", "devia ler o que o estado guardou");
    a.Definir("c-2", "Edu");
    Exige(estado["c-2"] == "Edu", "o dicionário do estado devia ser o mesmo, para o AppState gravar");
});

Caso("filtro-nulo-sem-apelidos", () =>
{
    Exige(new ApelidosLocais().CriarFiltro(_ => "x") == null, "sem apelidos o filtro devia ser nulo");
});

Caso("filtro-troca-apelido-pelo-nome-real", () =>
{
    var a = new ApelidosLocais(); a.Definir("c-1", "Bru");
    var filtro = a.CriarFiltro(id => id == "c-1" ? "Bruno" : null)!;
    Exige(filtro("Bru começou uma live: Jogo.") == "Bruno começou uma live: Jogo.", "devia trocar: " + filtro("Bru começou uma live: Jogo."));
    Exige(filtro("Avisos de bru silenciados.") == "Avisos de Bruno silenciados.", "sem diferença de maiúscula");
    Exige(filtro("nada a ver") == "nada a ver", "frase sem apelido fica igual");
});

Caso("filtro-nao-troca-pedaco-de-palavra", () =>
{
    var a = new ApelidosLocais(); a.Definir("c-1", "Bru");
    var filtro = a.CriarFiltro(_ => "Bruno")!;
    Exige(filtro("Brunel entrou.") == "Brunel entrou.", "Bru dentro de Brunel não pode trocar: " + filtro("Brunel entrou."));
    Exige(filtro("Brunela saiu.") == "Brunela saiu.", "Bru dentro de Brunela não pode trocar");
    Exige(filtro("(Bru) e Bru, Bru.") == "(Bruno) e Bruno, Bruno.", "pontuação em volta troca: " + filtro("(Bru) e Bru, Bru."));
});

Caso("filtro-apelido-maior-antes", () =>
{
    var a = new ApelidosLocais(); a.Definir("c-1", "Ana"); a.Definir("c-2", "Ana Maria");
    var filtro = a.CriarFiltro(id => id == "c-1" ? "Ana Paula" : "Maria Silva")!;
    Exige(filtro("Ana Maria chegou") == "Maria Silva chegou", "Ana Maria devia vencer Ana: " + filtro("Ana Maria chegou"));
});

Caso("filtro-amigo-desconhecido-vira-amigo", () =>
{
    var a = new ApelidosLocais(); a.Definir("c-1", "Bru");
    var filtro = a.CriarFiltro(_ => null)!;
    Exige(filtro("Bru saiu.") == "amigo saiu.", "sem nome real conhecido vira 'amigo': " + filtro("Bru saiu."));
});

Caso("filtro-nome-real-com-cifrao", () =>
{
    var a = new ApelidosLocais(); a.Definir("c-1", "Bru");
    var filtro = a.CriarFiltro(_ => "B$ru$1")!;
    Exige(filtro("Bru") == "B$ru$1", "o $ do nome real não pode virar grupo da regex: " + filtro("Bru"));
});

Caso("problema-recusa-o-que-confunde-o-log", () =>
{
    var a = new ApelidosLocais();
    a.Definir("c-2", "Dudu");
    var outros = new[] { "Eduardo", "Caio" };
    Exige(a.Problema("c-1", "Bru", outros, "Ana") == null, "um apelido comum devia passar");
    Exige(a.Problema("c-1", "  ", outros, "Ana") == null, "vazio tira o apelido e devia passar");
    Exige(a.Problema("c-1", "R", outros, "Ana") != null, "1 letra devia ser recusada");
    Exige(a.Problema("c-1", "eduardo", outros, "Ana") != null, "nome real de outro membro devia ser recusado (sem diferença de maiúscula)");
    Exige(a.Problema("c-1", "Ana", outros, "Ana") != null, "o meu próprio nome devia ser recusado");
    Exige(a.Problema("c-1", "dudu", outros, "Ana") != null, "apelido de outro amigo devia ser recusado");
    Exige(a.Problema("c-2", "Dudu", outros, "Ana") == null, "o mesmo amigo pode repetir o próprio apelido");
    Exige(a.Problema("c-1", "Live", outros, "Ana") != null, "palavra do log devia ser recusada");
});

Caso("log-nunca-grava-o-apelido", () =>
{
    var a = new ApelidosLocais(); a.Definir("c-1", "Bru");
    Log.Filtro = a.CriarFiltro(_ => "Bruno");
    string? disparada = null; Action<string> h = l => disparada = l; Log.Line += h;
    Log.Info("Você já está assistindo Bru.");
    Log.Line -= h;
    var ultima = Log.Tail(1)[0];
    Exige(ultima.EndsWith("Você já está assistindo Bruno."), "o log em memória devia ter o nome real: " + ultima);
    Exige(disparada != null && !disparada.Contains("Bru.") && disparada.Contains("Bruno"), "o evento Line também devia ter o nome real: " + disparada);
    Exige(!string.Join("\n", Log.Tail(50)).Contains("assistindo Bru."), "o apelido não pode estar em nenhuma linha");
});

Caso("log-sem-filtro-nao-muda-nada", () =>
{
    Log.Info("Bru entrou.");
    Exige(Log.Tail(1)[0].EndsWith("Bru entrou."), "sem filtro a linha fica como veio");
});

Caso("log-filtro-que-falha-nao-derruba-nem-vaza", () =>
{
    Log.Filtro = _ => throw new InvalidOperationException("boom");
    Log.Info("Bru saiu.");
    var ultima = Log.Tail(1)[0];
    Exige(!ultima.Contains("Bru"), "se o filtro falha a linha original não pode ser gravada: " + ultima);
});

return falhas == 0 ? 0 : 1;
