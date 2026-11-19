using System.Text.Json.Nodes;
using Streemu.Net;

// Testes da SalaUnida (spec 019, etapa 8) contra ISala FALSAS: sem rede, sem janela, sem estado salvo.
// Cada caso imprime "OK    nome" ou "FALHA nome : motivo".

int falhas = 0;
void Caso(string nome, Action f)
{
    try { f(); Console.WriteLine("OK    " + nome); }
    catch (Exception e) { falhas++; Console.WriteLine($"FALHA {nome} : {e.Message}"); }
}
void Exige(bool ok, string msg) { if (!ok) throw new Exception(msg); }
bool Espera(Func<bool> cond, int ms = 2000) { var ate = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < ate) { if (cond()) return true; Thread.Sleep(10); } return cond(); }

Member M(string id, string nick = "", LiveInfo? live = null) => new(id, nick == "" ? id : nick, "0", live);
LiveInfo Live(string sid) => new(sid, "Jogo", "720p", 0, 0);

Caso("uniao sem repetir a mesma pessoa", () =>
{
    using var u = new SalaUnida("eu", "Eu");
    var a = new Falsa { Online = { M("eu"), M("c-ana"), M("c-bia") } };
    var b = new Falsa { Online = { M("eu"), M("c-bia"), M("c-caio") } };
    u.Adicionar("g1", "Um", a); u.Adicionar("g2", "Dois", b);
    var ids = u.Members.Select(m => m.Id).OrderBy(x => x).ToArray();
    Exige(ids.SequenceEqual(new[] { "c-ana", "c-bia", "c-caio", "eu" }), "união errada: " + string.Join(",", ids));
    Exige(u.Friends.Count() == 3, "Friends deveria ter 3 sem eu");
});

Caso("entre duas linhas da mesma conta vale a que esta ao vivo", () =>
{
    using var u = new SalaUnida("eu", "Eu");
    var a = new Falsa { Online = { M("c-ana") } };
    var b = new Falsa { Online = { M("c-ana", "Ana", Live("s1")) } };
    u.Adicionar("g1", "Um", a); u.Adicionar("g2", "Dois", b);
    Exige(u.Find("c-ana")?.Live?.StreamId == "s1", "perdeu a live");
    Exige(u.FindByStream("s1")?.Id == "c-ana", "FindByStream falhou");
});

Caso("offline num grupo e online no outro nao e ausente", () =>
{
    using var u = new SalaUnida("eu", "Eu");
    var a = new Falsa { Online = { M("c-ana") } };
    var b = new Falsa { Ausentes = { new("c-ana", "Ana"), new("c-davi", "Davi") } };
    var c = new Falsa { Ausentes = { new("c-davi", "Davi") } };
    u.Adicionar("g1", "Um", a); u.Adicionar("g2", "Dois", b); u.Adicionar("g3", "Tres", c);
    var aus = u.Ausentes.Select(x => x.Id).ToArray();
    Exige(aus.SequenceEqual(new[] { "c-davi" }), "ausentes errados: " + string.Join(",", aus));
    Exige(u.GruposDe("c-ana").OrderBy(x => x).SequenceEqual(new[] { "g1", "g2" }), "GruposDe errado");
});

Caso("envio pela sala onde a pessoa esta", () =>
{
    using var u = new SalaUnida("eu", "Eu");
    var a = new Falsa { Online = { M("c-ana") } };
    var b = new Falsa { Online = { M("c-bia") } };
    u.Adicionar("g1", "Um", a); u.Adicionar("g2", "Dois", b);
    u.SendTo("c-bia", new JsonObject { ["t"] = "x" }).Wait();
    Exige(a.Enviados.Count == 0 && b.Enviados.Count == 1, $"foi para a sala errada ({a.Enviados.Count}/{b.Enviados.Count})");
});

Caso("envio pula sala caida e usa a conectada", () =>
{
    using var u = new SalaUnida("eu", "Eu");
    var a = new Falsa { Online = { M("c-ana") }, Conectada = false };
    var b = new Falsa { Online = { M("c-ana") } };
    u.Adicionar("g1", "Um", a); u.Adicionar("g2", "Dois", b);
    u.SendTo("c-ana", new JsonObject()).Wait();
    Exige(a.Enviados.Count == 0 && b.Enviados.Count == 1, "devia usar a sala conectada");
});

Caso("envio com todas caidas avisa uma vez (Undeliverable)", () =>
{
    using var u = new SalaUnida("eu", "Eu");
    var a = new Falsa { Online = { M("c-ana") }, Conectada = false };
    var b = new Falsa { Online = { M("c-ana") }, Conectada = false };
    u.Adicionar("g1", "Um", a); u.Adicionar("g2", "Dois", b);
    int avisos = 0; u.Undeliverable += (_, _) => avisos++;
    u.SendTo("c-ana", new JsonObject()).Wait();
    Exige(avisos == 1, $"avisos = {avisos}");
    Exige(!u.Connected, "Connected deveria ser falso");
});

Caso("SetLive e SetNick em todas, e a sala nova ja nasce com eles", () =>
{
    using var u = new SalaUnida("eu", "Eu");
    var a = new Falsa(); var b = new Falsa();
    u.Adicionar("g1", "Um", a); u.Adicionar("g2", "Dois", b);
    u.SetLive(Live("s9")); u.SetNick("Novo");
    Exige(a.Live?.StreamId == "s9" && b.Live?.StreamId == "s9" && a.Nick == "Novo" && b.Nick == "Novo", "não chegou em todas");
    var c = new Falsa(); u.Adicionar("g3", "Tres", c);
    Exige(c.Live?.StreamId == "s9" && c.Nick == "Novo", "a sala nova não recebeu o estado de agora");
});

Caso("telemetria agrupada por sala", () =>
{
    using var u = new SalaUnida("eu", "Eu");
    var a = new Falsa { Online = { M("c-ana"), M("c-bia") } };
    var b = new Falsa { Online = { M("c-bia"), M("c-caio") } };
    u.Adicionar("g1", "Um", a); u.Adicionar("g2", "Dois", b);
    u.SendTelemetryNow(new[] { "c-ana", "c-caio" }).Wait();
    Exige(a.Telemetria.SequenceEqual(new[] { "c-ana" }) && b.Telemetria.SequenceEqual(new[] { "c-caio" }), "agrupamento errado");
});

Caso("tirar uma sala com ligacao aberta nao derruba as outras", () =>
{
    using var u = new SalaUnida("eu", "Eu");
    var a = new Falsa { Online = { M("c-ana") } };
    var b = new Falsa { Online = { M("c-bia") } };
    u.Adicionar("g1", "Um", a); u.Adicionar("g2", "Dois", b);
    Exige(u.Remover("g1"), "Remover devia achar a sala");
    Exige(a.Descartada && !b.Descartada, "só a sala tirada devia ser desligada");
    Exige(u.Members.Select(m => m.Id).SequenceEqual(new[] { "c-bia" }), "sobrou gente do grupo tirado");
    Exige(u.Connected && u.Quantidade == 1, "a outra devia seguir de pé");
    Thread.Sleep(250); // o aviso da propria remocao
    int ouviu = 0; u.Changed += () => ouviu++;
    a.Mudar(); // a sala tirada não deve mais avisar
    Thread.Sleep(250);
    Exige(ouviu == 0, "a sala tirada ainda avisa mudanças");
    Exige(!u.Remover("g1"), "tirar duas vezes devia dizer que não achou");
});

Caso("removido de um grupo tira so aquele e avisa qual", () =>
{
    using var u = new SalaUnida("eu", "Eu");
    var a = new Falsa(); var b = new Falsa();
    u.Adicionar("g1", "Um", a); u.Adicionar("g2", "Dois", b);
    string? gid = null, motivo = null; u.GrupoEncerrado += (g, m) => { gid = g; motivo = m; };
    a.Encerrar("removido");
    Exige(gid == "g1" && motivo == "removido", "não avisou o grupo certo");
    Exige(u.Quantidade == 1 && a.Descartada, "devia tirar só o grupo removido");
});

Caso("sessao encerrada sobe como Encerrada e mantem as salas", () =>
{
    using var u = new SalaUnida("eu", "Eu");
    var a = new Falsa(); u.Adicionar("g1", "Um", a);
    string? m = null; u.Encerrada += x => m = x;
    a.Encerrar("sessao");
    Exige(m == "sessao", "não subiu");
});

Caso("Changed junta rajadas (no maximo um disparo por 100 ms)", () =>
{
    using var u = new SalaUnida("eu", "Eu");
    var a = new Falsa(); var b = new Falsa(); var c = new Falsa();
    u.Adicionar("g1", "Um", a); u.Adicionar("g2", "Dois", b); u.Adicionar("g3", "Tres", c);
    Thread.Sleep(300);
    int n = 0; u.Changed += () => Interlocked.Increment(ref n);
    for (int i = 0; i < 20; i++) { a.Mudar(); b.Mudar(); c.Mudar(); }
    Exige(Espera(() => n >= 1), "nunca avisou");
    Thread.Sleep(300);
    Exige(n <= 2, $"não juntou as mudanças: {n} avisos para 60");
});

Caso("Status: da que falha, ou Em N grupos", () =>
{
    using var u = new SalaUnida("eu", "Eu");
    Exige(u.Status.Contains("nenhum grupo"), "sem sala devia dizer que não está em grupo");
    var a = new Falsa { StatusTexto = "Na sala do grupo." }; var b = new Falsa { StatusTexto = "Na sala do grupo." };
    u.Adicionar("g1", "Um", a);
    Exige(u.Status == "Na sala do grupo.", "com uma sala, o texto dela");
    u.Adicionar("g2", "Dois", b);
    Exige(u.Status == "Em 2 grupos.", "com duas: " + u.Status);
    b.Conectada = false; b.StatusTexto = "Sala fora do ar.";
    Exige(u.Status == "Sala fora do ar.", "devia mostrar a que falha");
});

Caso("mensagem de qualquer sala chega, trocar a sala do mesmo grupo desliga a antiga", () =>
{
    using var u = new SalaUnida("eu", "Eu");
    var a = new Falsa(); u.Adicionar("g1", "Um", a);
    string? de = null; u.Message += (d, _, _) => de = d;
    a.Falar("c-ana");
    Exige(de == "c-ana", "mensagem perdida");
    var a2 = new Falsa(); u.Adicionar("g1", "Um", a2);
    Exige(a.Descartada && u.Quantidade == 1, "a sala antiga devia ser desligada");
    de = null; a.Falar("c-velha");
    Exige(de == null, "a sala antiga ainda fala");
});

Console.WriteLine($"casos: {falhas} falha(s)");
return falhas == 0 ? 0 : 1;

sealed class Falsa : ISala
{
    public List<Member> Online = new();
    public List<Ausente> Ausentes = new();
    public List<(string Id, JsonObject Msg)> Enviados = new();
    public List<string> Telemetria = new();
    public bool Conectada = true, Descartada;
    public string StatusTexto = "Na sala do grupo.";
    public LiveInfo? Live; public string Nick = "";

    public string MyId => "eu";
    public bool Connected => Conectada;
    public string Status => StatusTexto;
    public IReadOnlyList<Member> Members => Online;
    public IEnumerable<Member> Friends => Online.Where(m => m.Id != MyId);
    IReadOnlyList<Ausente> ISala.Ausentes => Ausentes;
    public bool IsPresent(string id) => Online.Any(m => m.Id == id);
    public Member? Find(string id) => Online.FirstOrDefault(m => m.Id == id);
    public Member? FindByStream(string s) => Online.FirstOrDefault(m => m.Live?.StreamId == s);
    public void SetNick(string nick) => Nick = nick;
    public void SetLive(LiveInfo? live) => Live = live;
    public Task SendTo(string id, JsonObject msg) { Enviados.Add((id, msg)); return Task.CompletedTask; }
    public Task SendTelemetryNow(IEnumerable<string> ids) { Telemetria.AddRange(ids); return Task.CompletedTask; }
    public event Action? Changed;
    public event Action<string, string, JsonObject>? Message;
    public event Action<string, JsonObject?>? Undeliverable;
    public event Action? TelemetryNow;
    public event Action<string>? Encerrada;
    public void Mudar() => Changed?.Invoke();
    public void Falar(string de) => Message?.Invoke(de, de, new JsonObject());
    public void Encerrar(string motivo) => Encerrada?.Invoke(motivo);
    public void Dispose() => Descartada = true;
}
