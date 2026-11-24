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

// ---- encerrar live (spec 029, E5). GroupRoom.Handle sem rede: o token nulo deixa a sala esperando; só se alimenta a mensagem.

GroupRoom SalaDeTeste() => new(new Uri("wss://sala.invalid/grupos/g/sala"), "c-aaaaaaaaaaaa", "Eu", _ => Task.FromResult<string?>(null));
JsonObject J(string json) => (JsonObject)JsonNode.Parse(json)!;

Caso("encerrar_live vira o evento, sem conta", () =>
{
    using var r = SalaDeTeste();
    int n = 0; r.EncerrarLive += () => n++;
    r.Handle(J("""{"t":"encerrar_live"}"""));
    Exige(n == 1, $"EncerrarLive disparou {n}x");
    r.Handle(J("""{"t":"encerrar_live"}""")); // o servidor pode repetir
    Exige(n == 2, "devia disparar de novo a cada mensagem");
});

Caso("live_encerrada vira o evento com a conta", () =>
{
    using var r = SalaDeTeste();
    var contas = new List<string>(); r.LiveEncerrada += c => contas.Add(c);
    int encerrar = 0; r.EncerrarLive += () => encerrar++;
    r.Handle(J("""{"t":"live_encerrada","conta":"c-abc123def456"}"""));
    Exige(contas.SequenceEqual(new[] { "c-abc123def456" }), "contas: " + string.Join(",", contas));
    Exige(encerrar == 0, "live_encerrada não pode parar a minha live");
});

Caso("live_encerrada com conta ausente ou invalida nao dispara", () =>
{
    using var r = SalaDeTeste();
    int n = 0; r.LiveEncerrada += _ => n++;
    foreach (var json in new[]
    {
        """{"t":"live_encerrada"}""", """{"t":"live_encerrada","conta":null}""", """{"t":"live_encerrada","conta":""}""", """{"t":"live_encerrada","conta":42}""",
        """{"t":"live_encerrada","conta":"c-ana"}""", """{"t":"live_encerrada","conta":"C-ABC123DEF456"}""", """{"t":"live_encerrada","conta":"x-abc123def456"}""",
        """{"t":"live_encerrada","conta":"c-abc123def4567"}""", """{"t":"live_encerrada","conta":"c-abc123def45"}""", """{"t":"live_encerrada","conta":"c-abc123def456\n"}""",
        """{"t":"live_encerrada","conta":{"id":"c-abc123def456"}}""", """{"t":"live_encerrada","conta":["c-abc123def456"]}""",
    })
        r.Handle(J(json));
    Exige(n == 0, $"disparou {n}x com conta inválida");
});

Caso("t desconhecido e mensagem nula continuam ignorados", () =>
{
    using var r = SalaDeTeste();
    int n = 0; r.EncerrarLive += () => n++; r.LiveEncerrada += _ => n++; r.TelemetryNow += () => n++;
    r.Handle(J("""{"t":"coisa_nova","conta":"c-abc123def456"}"""));
    r.Handle(J("""{"conta":"c-abc123def456"}"""));
    r.Handle(null);
    Exige(n == 0, $"algo disparou ({n})");
});

Caso("SalaUnida repassa as duas mensagens de qualquer grupo e para depois de tirar a sala", () =>
{
    using var u = new SalaUnida("eu", "Eu");
    var a = new Falsa(); var b = new Falsa();
    u.Adicionar("g1", "Um", a); u.Adicionar("g2", "Dois", b);
    int parar = 0; var contas = new List<string>();
    u.EncerrarLive += () => parar++; u.LiveEncerrada += c => contas.Add(c);
    a.PedirParar(); b.PedirParar();
    a.AvisarLiveEncerrada("c-abc123def456"); b.AvisarLiveEncerrada("c-abc123def456");
    Exige(parar == 2 && contas.Count == 2 && contas.All(c => c == "c-abc123def456"), $"não repassou ({parar}/{contas.Count})");
    u.Remover("g1");
    a.PedirParar(); a.AvisarLiveEncerrada("c-zzzzzzzzzzzz");
    Exige(parar == 2 && contas.Count == 2, "a sala removida ainda repassa");
    var b2 = new Falsa(); u.Adicionar("g2", "Dois", b2); // troca a sala do mesmo grupo
    b.PedirParar(); b2.PedirParar();
    Exige(parar == 3, $"só a sala nova devia repassar ({parar})");
});

Caso("SessoesParaFechar: so as que assisto dessa conta", () =>
{
    var ligacoes = new (string Sessao, string PeerId, bool EuTransmito)[]
    {
        ("s1", "c-abc123def456", false), // assisto a conta: fecha
        ("s2", "c-abc123def456", true),  // essa conta me assiste (eu transmito para ela): não fecha
        ("s3", "c-outraconta1", false),  // assisto outra conta: não fecha
        ("s4", "c-abc123def456", false), // a mesma conta numa segunda ligação: fecha também
    };
    var fecha = GroupRoom.SessoesParaFechar(ligacoes, "c-abc123def456");
    Exige(fecha.SequenceEqual(new[] { "s1", "s4" }), "fechou: " + string.Join(",", fecha));
    Exige(GroupRoom.SessoesParaFechar(ligacoes, "c-zzzzzzzzzzzz").Count == 0, "conta que ninguém assiste");
    Exige(GroupRoom.SessoesParaFechar(ligacoes, null).Count == 0 && GroupRoom.SessoesParaFechar(ligacoes, "").Count == 0, "sem conta não fecha nada");
    Exige(GroupRoom.SessoesParaFechar(new (string, string, bool)[] { ("s5", "", false) }, "").Count == 0, "id vazio não casa com conta vazia");
    Exige(GroupRoom.SessoesParaFechar(Array.Empty<(string, string, bool)>(), "c-abc123def456").Count == 0, "sem ligações");
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
    public event Action? EncerrarLive;
    public event Action<string>? LiveEncerrada;
    public void PedirParar() => EncerrarLive?.Invoke();
    public void AvisarLiveEncerrada(string conta) => LiveEncerrada?.Invoke(conta);
    public void Mudar() => Changed?.Invoke();
    public void Falar(string de) => Message?.Invoke(de, de, new JsonObject());
    public void Encerrar(string motivo) => Encerrada?.Invoke(motivo);
    public void Dispose() => Descartada = true;
}
