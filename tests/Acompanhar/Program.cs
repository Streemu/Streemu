using Streemu.Acompanhar;

// Testes da regra do "continuar assistindo" (spec 038, T2): espera, entrada sozinha, aviso, desistência e o "Sair".
// Relógio de mentira, sem janela nem rede. Cada caso imprime "OK    acompanhar-nome" ou "FALHA acompanhar-nome : motivo".

int falhas = 0;
void Caso(string nome, Action f)
{
    try { f(); Console.WriteLine("OK    acompanhar-" + nome); }
    catch (Exception e) { falhas++; Console.WriteLine($"FALHA acompanhar-{nome} : {e.Message}"); }
}
void Exige(bool ok, string msg) { if (!ok) throw new Exception(msg); }

var t0 = new DateTime(2026, 11, 28, 20, 0, 0);
var agora = t0;
var acompanhados = new Dictionary<string, int>();
int padrao = 10;
AcompanharAmigos Novo(params (string id, int min)[] lista)
{
    agora = t0; acompanhados.Clear(); padrao = 10;
    foreach (var (id, min) in lista) acompanhados[id] = min;
    return new AcompanharAmigos(() => agora, acompanhados, () => padrao);
}
IReadOnlyList<AcaoAcompanhar> Olha(AcompanharAmigos a, bool possoEntrar, params AmigoVisto[] vistos) => a.Atualizar(vistos, possoEntrar);
AmigoVisto Assistindo(string id) => new(id, true, true);
AmigoVisto LiveSemEu(string id) => new(id, true, false);
AmigoVisto Fechou(string id) => new(id, false, false);
void Passa(int minutos, int segundos = 0) => agora = agora.AddMinutes(minutos).AddSeconds(segundos);
// deixa o amigo "h" no estado: eu assistia e ele fechou a live (começou a espera)
AcompanharAmigos EsperandoH(int minutos)
{
    var a = Novo(("h", minutos));
    Olha(a, true, Assistindo("h"));
    Olha(a, true, Fechou("h"));
    return a;
}

// ---------- critérios 1 a 4 ----------

Caso("1-fechar-live-comeca-espera", () =>
{
    var a = Novo(("h", 10));
    Olha(a, true, Assistindo("h"));
    Exige(a.EsperaDe("h") is null, "esperando com a live ainda aberta");
    Olha(a, true, Fechou("h"));
    var e = a.EsperaDe("h");
    Exige(e is { Ate: not null } && e.Value.Ate == t0.AddMinutes(10), $"esperaria até {t0.AddMinutes(10)}, veio {e?.Ate}");
});

Caso("2-live-no-prazo-entra-sozinho", () =>
{
    var a = EsperandoH(10);
    Passa(4);
    var acoes = Olha(a, true, LiveSemEu("h"));
    Exige(acoes.Count == 1 && acoes[0] == new AcaoAcompanhar(TipoAcaoAcompanhar.Entrar, "h"), "não mandou entrar: " + string.Join(",", acoes));
    Exige(a.EsperaDe("h") is null, "a espera continuou depois de entrar");
});

Caso("3-prazo-acabou-desiste-uma-vez", () =>
{
    var a = EsperandoH(5);
    Passa(5, 1);
    var acoes = Olha(a, true, Fechou("h"));
    Exige(acoes.Count == 1 && acoes[0].Tipo == TipoAcaoAcompanhar.DesistiuDeEsperar, "não desistiu: " + string.Join(",", acoes));
    Exige(Olha(a, true, Fechou("h")).Count == 0, "avisou a desistência duas vezes");
    Exige(Olha(a, true, LiveSemEu("h")).Count == 0, "entrou sozinho depois do prazo");
});

Caso("4-ate-voltar-nao-tem-prazo", () =>
{
    var a = EsperandoH(0);
    Exige(a.EsperaDe("h") is { Ate: null }, "o prazo 'até voltar' devia vir sem hora final");
    Passa(60 * 24);
    Exige(Olha(a, true, Fechou("h")).Count == 0 && a.EsperaDe("h") != null, "desistiu depois de um dia");
    Exige(Olha(a, true, LiveSemEu("h")) is [{ Tipo: TipoAcaoAcompanhar.Entrar }], "não entrou quando ele voltou");
});

Caso("prazo-padrao-vale-quando-negativo", () =>
{
    var a = Novo(("h", -1));
    padrao = 20;
    Olha(a, true, Assistindo("h"));
    Olha(a, true, Fechou("h"));
    Exige(a.EsperaDe("h") is { Ate: { } fim } && fim == t0.AddMinutes(20), "não usou o prazo padrão de 20 min");
});

// ---------- critério 5 e D9 ----------

Caso("5-assistindo-outra-live-so-avisa", () =>
{
    var a = EsperandoH(10);
    var acoes = Olha(a, false, LiveSemEu("h"));
    Exige(acoes is [{ Tipo: TipoAcaoAcompanhar.AvisarLiveAberta }], "devia só avisar: " + string.Join(",", acoes));
    Exige(a.EsperaDe("h") != null, "a espera acabou ao avisar");
    Exige(Olha(a, false, LiveSemEu("h")).Count == 0, "avisou de novo a cada olhada");
});

Caso("5b-livre-depois-do-aviso-entra", () =>
{
    var a = EsperandoH(10);
    Olha(a, false, LiveSemEu("h"));
    Exige(Olha(a, true, LiveSemEu("h")) is [{ Tipo: TipoAcaoAcompanhar.Entrar }], "não entrou quando a tela ficou livre");
});

Caso("9-duas-lives-entra-na-primeira-e-avisa-a-outra", () =>
{
    var a = Novo(("h", 10), ("g", 10));
    Olha(a, true, Assistindo("h"), Assistindo("g"));
    Olha(a, true, Fechou("h"), Fechou("g"));
    var acoes = Olha(a, true, LiveSemEu("h"), LiveSemEu("g"));
    Exige(acoes.Count(x => x.Tipo == TipoAcaoAcompanhar.Entrar) == 1, "entrou em mais de uma live: " + string.Join(",", acoes));
    Exige(acoes.Count(x => x.Tipo == TipoAcaoAcompanhar.AvisarLiveAberta) == 1, "a outra live não virou aviso");
});

// ---------- critério 8 e a entrada que não se desfaz sozinha ----------

Caso("8-parar-nao-insiste", () =>
{
    var a = EsperandoH(10);
    a.Parar("h");
    Exige(a.EsperaDe("h") is null, "continuou esperando depois de Parar");
    Exige(Olha(a, true, LiveSemEu("h")).Count == 0, "entrou sozinho depois de Parar");
});

Caso("8b-parar-nao-tira-a-marca", () =>
{
    var a = EsperandoH(10);
    a.Parar("h");
    Olha(a, true, Assistindo("h"));
    Olha(a, true, Fechou("h"));
    Exige(a.EsperaDe("h") != null, "a próxima live que ele fechou não recomeçou a espera");
});

Caso("entrada-demora-a-ligar-nao-desfaz", () =>
{
    var a = EsperandoH(10);
    Olha(a, true, LiveSemEu("h")); // mandou entrar
    Passa(0, 20);
    Olha(a, true, LiveSemEu("h")); // ainda ligando
    Olha(a, true, Assistindo("h")); // a imagem chegou
    Olha(a, true, Fechou("h"));
    Exige(a.EsperaDe("h") != null, "a espera não recomeçou depois de assistir");
});

Caso("fechei-eu-mesmo-a-live-nao-espera", () =>
{
    var a = Novo(("h", 10));
    Olha(a, true, Assistindo("h"));
    Passa(0, 31);
    Olha(a, true, LiveSemEu("h")); // fechei a janela, ele segue ao vivo
    Passa(0, 31);
    Olha(a, true, LiveSemEu("h"));
    Olha(a, true, Fechou("h"));
    Exige(a.EsperaDe("h") is null, "esperou o amigo que eu mesmo larguei");
});

// ---------- critério 10 e o resto ----------

Caso("10-abrir-o-app-nao-continua-espera", () =>
{
    var a = Novo(("h", 10)); // app aberto de novo: lista salva, nenhuma espera
    Exige(Olha(a, true, Fechou("h")).Count == 0 && a.EsperaDe("h") is null, "começou a esperar sem ter visto a live");
    Exige(Olha(a, true, LiveSemEu("h")).Count == 0, "entrou sozinho numa live que eu nunca assisti");
});

Caso("desmarcar-esquece-a-espera", () =>
{
    var a = EsperandoH(10);
    acompanhados.Remove("h");
    Exige(Olha(a, true, LiveSemEu("h")).Count == 0 && a.EsperaDe("h") is null, "agiu num amigo desmarcado");
});

Caso("amigo-fora-da-sala-conta-como-sem-live", () =>
{
    var a = Novo(("h", 10));
    Olha(a, true, Assistindo("h"));
    Olha(a, true); // sumiu da sala
    Exige(a.EsperaDe("h") != null, "amigo que saiu da sala não começou a espera");
});

Console.WriteLine(falhas == 0 ? "TUDO OK" : $"{falhas} FALHA(S)");
return falhas == 0 ? 0 : 1;
