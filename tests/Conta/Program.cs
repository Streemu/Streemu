using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Streemu.Net;
using Streemu.Util;

// Testes do cliente de conta (spec 019, etapa 5): Streemu.Net.Conta contra um servidor FALSO dentro do HttpClient. Sem rede, sem janela, sem
// Worker e sem tocar no estado salvo (o "salvar" daqui não grava nada). Cada caso imprime "OK    nome" ou "FALHA nome : motivo".

int falhas = 0, total = 0;
void Caso(string nome, Func<Task> f)
{
    total++;
    try { f().GetAwaiter().GetResult(); Console.WriteLine("OK    " + nome); }
    catch (Exception e) { falhas++; Console.WriteLine($"FALHA {nome} : {e.Message}"); }
}
void Exige(bool ok, string msg) { if (!ok) throw new Exception(msg); }

const string Servidor = "https://servidor.teste";
const string SenhaAntiga = "senha-que-nao-existe-mais"; // só para provar que nada do que sai do app a leva

(Conta C, AppState S, Falso F) Novo(Func<DateTimeOffset>? agora = null)
{
    var s = new AppState();
    var f = new Falso();
    return (new Conta(Servidor, s, () => { }, f, agora), s, f);
}

static JsonObject Sessao(string acesso, long expira, string metodo = "google.com") => new()
{
    ["conta"] = "c-abcdefghij12", ["dispositivo"] = "d-abcdefghij12", ["acesso"] = acesso, ["expira"] = expira, ["bilhete"] = "st1.1.xx",
    ["metodo"] = metodo, ["grupos"] = new JsonArray(new JsonObject { ["id"] = "grupo", ["nome"] = "Grupo do Streemu" }),
};
static long Agora() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

/// <summary>Faz o login inteiro e devolve o que o servidor viu.</summary>
async Task<(Conta C, AppState S, Falso F, string Verificador, string Segredo, string Codigo)> Entrar(Func<DateTimeOffset>? agora = null, long? expira = null)
{
    var (c, s, f) = Novo(agora);
    string verificador = "", segredo = "";
    f.Rota = (m, p, corpo) =>
    {
        if (p == "/conta/login/iniciar") return (200, new JsonObject { ["pedido"] = new string('a', 32), ["expira"] = 1 });
        if (p == "/conta/login/retirar") { verificador = corpo!["verificador"]!.GetValue<string>(); segredo = corpo["segredo"]!.GetValue<string>(); return (200, Sessao("sa1.carga.assinatura", expira ?? Agora() + 3600)); }
        return (404, new JsonObject { ["erro"] = "nao_encontrado" });
    };
    var ini = await c.IniciarEntradaAsync();
    Exige(ini.Ok && ini.Url == $"{Servidor}/entrar?pedido={new string('a', 32)}", "iniciar: " + ini.Texto);
    var fim = await c.ConcluirEntradaAsync("123 456", "Fulano");
    Exige(fim.Ok, "concluir: " + fim.Texto);
    return (c, s, f, verificador, segredo, "123456");
}

Caso("entrada-completa-guarda-a-sessao-e-o-segredo-so-cifrado", async () =>
{
    var (c, s, f, verificador, segredo, _) = await Entrar();
    Exige(c.TemConta && s.ContaId == "c-abcdefghij12" && s.DispositivoId == "d-abcdefghij12" && s.ContaMetodo == "google.com", "estado da conta");
    Exige(s.Grupos.Count == 1 && s.Grupos[0].Id == "grupo", "grupos da resposta");
    Exige(s.ContaSegredoProtegido != null && !s.ContaSegredoProtegido.Contains(segredo) && Cofre.Abrir(s.ContaSegredoProtegido) == segredo, "o segredo tem que estar cifrado e abrir de volta");
    Exige(c.Bilhete == "st1.1.xx", "bilhete do dia guardado");
    Exige(await c.AcessoAsync() == "sa1.carga.assinatura", "token de acesso");
    // o verificador nunca vai para o estado
    Exige(!System.Text.Json.JsonSerializer.Serialize(s).Contains(verificador), "o verificador não pode estar no estado");
    // o desafio enviado é o SHA-256 do verificador
    var ini = f.Pedidos.First(p => p.Path == "/conta/login/iniciar").Corpo!;
    Exige(ini["desafio"]!.GetValue<string>() == Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(verificador))).TrimEnd('=').Replace('+', '-').Replace('/', '_'), "desafio = SHA-256 do verificador");
    // o código vai só com os 6 dígitos
    Exige(f.Pedidos.First(p => p.Path == "/conta/login/retirar").Corpo!["codigo"]!.GetValue<string>() == "123456", "código só com dígitos");
});

Caso("senha-do-grupo-nao-vai-em-nenhum-pedido", async () =>
{
    var (c, _, f, _, _, _) = await Entrar();
    f.Rota = (m, p, corpo) => p == "/conta/renovar" ? (200, Sessao("sa1.novo.x", Agora() + 3600)) : (404, new JsonObject());
    Exige(await c.GarantirAsync() == EstadoSessao.Ok, "renovar");
    await c.ConfigAsync();
    foreach (var p in f.Pedidos)
        Exige(!p.Cabecalhos.ContainsKey("x-streemu-senha"), $"{p.Path}: a senha do grupo acabou (spec 022) e não pode sair em pedido nenhum");
});

Caso("resposta-do-retirar-perdida-e-repeticao-usam-o-mesmo-segredo", async () =>
{
    var (c, s, f) = Novo();
    var segredos = new List<string>();
    int tentativa = 0;
    f.Rota = (m, p, corpo) =>
    {
        if (p == "/conta/login/iniciar") return (200, new JsonObject { ["pedido"] = new string('b', 32) });
        if (p == "/conta/login/retirar") { segredos.Add(corpo!["segredo"]!.GetValue<string>()); return ++tentativa == 1 ? (-1, null!) : (200, Sessao("sa1.x.y", Agora() + 3600)); }
        return (404, new JsonObject());
    };
    Exige((await c.IniciarEntradaAsync()).Ok, "iniciar");
    var r1 = await c.ConcluirEntradaAsync("111111", "");
    Exige(!r1.Ok && r1.Erro == "sem_rede" && !c.TemConta, "primeira tentativa sem resposta: não entrou ainda");
    var r2 = await c.ConcluirEntradaAsync("111111", "");
    Exige(r2.Ok && c.TemConta, "repetir com o mesmo pedido entra");
    Exige(segredos.Count == 2 && segredos[0] == segredos[1], "o servidor tem que ver o MESMO segredo nas duas tentativas (o retirar é idempotente por ele)");
});

Caso("codigo-errado-e-pedido-vencido", async () =>
{
    var (c, s, f) = Novo();
    f.Rota = (m, p, corpo) => p == "/conta/login/iniciar" ? (200, new JsonObject { ["pedido"] = new string('c', 32) })
        : (403, new JsonObject { ["erro"] = "codigo_errado", ["texto"] = "Código errado. Confira os 6 dígitos que apareceram no navegador." });
    Exige((await c.IniciarEntradaAsync()).Ok, "iniciar");
    var r = await c.ConcluirEntradaAsync("000000", "");
    Exige(!r.Ok && r.Erro == "codigo_errado" && r.Texto!.Contains("Código errado") && !c.TemConta, "código errado");
    Exige((await c.ConcluirEntradaAsync("12", "")).Erro == "codigo_errado" && f.Pedidos.Count(p => p.Path == "/conta/login/retirar") == 1, "menos de 6 dígitos nem vai ao servidor");
    f.Rota = (m, p, corpo) => (410, new JsonObject { ["erro"] = "pedido_vencido", ["texto"] = "Este pedido de login venceu ou não existe. Volte ao Streemu e clique em Entrar de novo." });
    var v = await c.ConcluirEntradaAsync("222222", "");
    Exige(v.Erro == "pedido_vencido", "vencido");
    var depois = await c.ConcluirEntradaAsync("222222", "");
    Exige(depois.Erro == "pedido_vencido" && f.Pedidos.Count(p => p.Path == "/conta/login/retirar") == 2, "pedido vencido é esquecido: não chama o servidor de novo");
});

Caso("renovacao-sozinha-quando-faltam-menos-de-10-minutos", async () =>
{
    var relogio = DateTimeOffset.UtcNow;
    var (c, s, f, _, _, _) = await Entrar(agora: () => relogio, expira: DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600);
    int renovou = 0;
    f.Rota = (m, p, corpo) => { renovou++; return (200, Sessao("sa1.renovado.z", relogio.ToUnixTimeSeconds() + 3600)); };
    Exige(await c.AcessoAsync() == "sa1.carga.assinatura" && renovou == 0, "com folga, usa o que tem");
    relogio += TimeSpan.FromMinutes(52); // faltam 8 minutos
    Exige(await c.AcessoAsync() == "sa1.renovado.z" && renovou == 1, "faltando menos de 10 minutos, renova");
    Exige(await c.AcessoAsync(forcar: true) == "sa1.renovado.z" && renovou == 2, "depois de um 401, renova na hora");
});

Caso("servidor-sem-contas-nunca-vira-modo-sem-conta", async () =>
{
    foreach (var (status, corpo, quer) in new (int, JsonObject?, EstadoServidor)[]
    {
        (404, null, EstadoServidor.Indisponivel),                                           // Worker antigo
        (503, new JsonObject { ["erro"] = "servidor_sem_contas" }, EstadoServidor.Indisponivel), // sem CONTA_SEGREDO
        (401, null, EstadoServidor.Indisponivel),
        (200, new JsonObject { ["contas"] = false }, EstadoServidor.Indisponivel),
        (200, new JsonObject { ["contas"] = true, ["obrigatoria"] = false, ["codigo"] = true }, EstadoServidor.Obrigatoria), // "opcional" não existe mais
        (200, new JsonObject { ["contas"] = true, ["obrigatoria"] = true, ["codigo"] = true }, EstadoServidor.Obrigatoria),
    })
    {
        var (c, _, f) = Novo();
        f.Rota = (m, p, b) => (status, corpo);
        var (estado, _) = await c.ConfigAsync();
        Exige(estado == quer, $"{status}: esperava {quer}, veio {estado}");
    }
    var (c2, s2, f2) = Novo();
    f2.Rota = (m, p, b) => (-1, null!);
    Exige((await c2.ConfigAsync()).Estado == EstadoServidor.Indisponivel, "sem rede é Indisponível");
});

Caso("conta-invalida-apaga-e-volta-ao-estado-sem-conta", async () =>
{
    var (c, s, f, _, _, _) = await Entrar();
    f.Rota = (m, p, b) => (401, new JsonObject { ["erro"] = "conta_invalida", ["texto"] = "Esta conta ou sessão não vale mais. Entre de novo." });
    Exige(await c.GarantirAsync() == EstadoSessao.Encerrada, "encerrada");
    Exige(!c.TemConta && s.ContaId == null && s.DispositivoId == null && s.ContaSegredoProtegido == null && s.Grupos.Count == 0 && await c.AcessoAsync() == null, "credenciais locais apagadas");
    Exige(Conta.FraseSessaoEncerrada == "Sua sessão foi encerrada. Entre de novo.", "frase combinada");
});

Caso("segredo-que-nao-abre-volta-ao-estado-sem-conta", async () =>
{
    var (c, s, f) = Novo();
    s.DispositivoId = "d-abcdefghij12"; s.ContaId = "c-abcdefghij12"; s.ContaSegredoProtegido = "isto-nao-e-um-segredo-cifrado";
    f.Rota = (m, p, b) => (200, Sessao("sa1.x.y", Agora() + 3600));
    Exige(await c.GarantirAsync() == EstadoSessao.Encerrada && !c.TemConta && f.Pedidos.Count == 0, "sem segredo que abra não há o que mandar ao servidor");
});

Caso("sem-rede-nunca-apaga-a-credencial", async () =>
{
    var relogio = DateTimeOffset.UtcNow;
    var (c, s, f, _, _, _) = await Entrar(agora: () => relogio, expira: DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 700);
    f.Rota = (m, p, b) => (-1, null!);
    relogio += TimeSpan.FromMinutes(5); // faltam menos de 10 minutos, ainda não venceu
    Exige(await c.AcessoAsync() == "sa1.carga.assinatura", "sem rede usa o token que tem até vencer");
    Exige(await c.GarantirAsync() == EstadoSessao.SemRede && c.TemConta && s.ContaSegredoProtegido != null, "GarantirAsync sem rede mantém a credencial");
    relogio += TimeSpan.FromMinutes(30); // venceu
    Exige(await c.AcessoAsync(forcar: true) == null && c.TemConta, "token vencido e sem rede: sem token, mas a credencial fica");
    f.Rota = (m, p, b) => (500, new JsonObject { ["erro"] = "interno" });
    Exige(await c.GarantirAsync() == EstadoSessao.SemRede && c.TemConta, "erro 500 também não apaga");
    f.Rota = (m, p, b) => (200, Sessao("sa1.volta.ok", relogio.ToUnixTimeSeconds() + 3600));
    relogio += TimeSpan.FromMinutes(10);
    Exige(await c.GarantirAsync() == EstadoSessao.Ok && await c.AcessoAsync() == "sa1.volta.ok", "quando a rede volta, renova");
});

Caso("sair-com-o-servidor-fora-apaga-o-local-e-avisa", async () =>
{
    var (c, s, f, _, _, _) = await Entrar();
    f.Rota = (m, p, b) => (-1, null!);
    var r = await c.SairAsync();
    Exige(!r.Ok && r.Erro == "servidor_fora" && r.Texto!.Contains("pode continuar valendo"), "avisa que a sessão pode continuar no servidor");
    Exige(!c.TemConta && s.DispositivoId == null && s.ContaSegredoProtegido == null, "o local foi apagado mesmo assim");
    var (c2, s2, f2, _, _, _) = await Entrar();
    f2.Rota = (m, p, b) => (200, new JsonObject { ["ok"] = true });
    Exige((await c2.SairAsync()).Ok && !c2.TemConta && f2.Pedidos.Any(p => p.Path == "/conta/sair"), "sair com o servidor no ar");
});

Caso("segredo-verificador-e-codigo-nunca-aparecem-nos-textos-de-erro", async () =>
{
    var (c, s, f, verificador, segredo, _) = await Entrar();
    var textos = new List<string>();
    void Junta(ResultadoConta r) { if (r.Texto != null) textos.Add(r.Texto); if (r.Erro != null) textos.Add(r.Erro); }
    foreach (var (status, corpo) in new (int, JsonObject?)[] { (-1, null), (500, null), (403, new JsonObject { ["erro"] = "codigo_errado" }), (410, new JsonObject { ["erro"] = "pedido_vencido" }), (429, new JsonObject { ["erro"] = "muitas_tentativas" }), (200, new JsonObject()) })
    {
        var (c2, s2, f2) = Novo();
        f2.Rota = (m, p, b) => p == "/conta/login/iniciar" ? (200, new JsonObject { ["pedido"] = new string('d', 32) }) : (status, corpo!);
        var ini = await c2.IniciarEntradaAsync(); if (ini.Texto != null) textos.Add(ini.Texto);
        Junta(await c2.ConcluirEntradaAsync("987654", ""));
        Junta(await c2.SairAsync());
    }
    foreach (var t in textos)
    {
        Exige(!t.Contains(segredo) && !t.Contains(verificador) && !t.Contains("987654") && !t.Contains("123456"), "texto vazou algo: " + t);
        Exige(!t.Contains("sa1.") && !t.Contains("Bearer") && !t.Contains(SenhaAntiga), "texto com token ou senha: " + t);
    }
    Exige(textos.Count >= 6, "o caso tem que ter exercitado os erros");
});

// ---- etapa 6: vincular o Google, sair de todos os dispositivos e excluir a conta. O app pede o pedido com o token da conta e espera a página terminar.

/// <summary>Servidor falso das ações: o pedido sai com o token no cabeçalho; "estado" responde esperando N vezes e depois concluído.</summary>
Func<string, string, JsonObject?, (int, JsonObject?)> RotaAcao(int esperaVezes, string pedido = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee")
{
    int vezes = 0;
    return (m, p, corpo) =>
    {
        if (p == "/conta/login/iniciar") return (200, new JsonObject { ["pedido"] = pedido, ["expira"] = 1 });
        if (p == "/conta/login/estado") return (200, new JsonObject { ["acao"] = "x", ["estado"] = ++vezes > esperaVezes ? "concluido" : "esperando" });
        return (404, new JsonObject { ["erro"] = "nao_encontrado" });
    };
}

Caso("acao-vincular-pede-com-token-espera-a-pagina-e-guarda-que-o-google-esta-ligado", async () =>
{
    var (c, s, f, _, _, _) = await Entrar();
    f.Rota = RotaAcao(2);
    f.Pedidos.Clear();
    Exige(!c.GoogleVinculado, "antes: sem Google vinculado");
    var ini = await c.IniciarAcaoAsync(AcaoConta.Vincular);
    Exige(ini.Ok && ini.Url == $"{Servidor}/entrar?pedido={new string('e', 32)}", "iniciar: " + ini.Texto);
    var pedido = f.Pedidos.Single(p => p.Path == "/conta/login/iniciar");
    Exige(pedido.Corpo!["acao"]!.GetValue<string>() == "vincular", "a ação vai com o nome do servidor");
    Exige(pedido.Cabecalhos["authorization"] == "Bearer sa1.carga.assinatura", "o pedido tem que ir com o token da conta (é ele que prende o pedido à conta)");
    Exige(!pedido.Cabecalhos.ContainsKey("x-streemu-senha"), "a senha do grupo não vai nas ações da conta");
    var fim = await c.AguardarAcaoAsync(CancellationToken.None, TimeSpan.FromMilliseconds(5));
    Exige(fim.Ok && c.GoogleVinculado && s.ContaGoogleVinculado, "vinculado");
    Exige(c.TemConta, "vincular não mexe na sessão");
    var estados = f.Pedidos.Where(p => p.Path == "/conta/login/estado").ToList();
    Exige(estados.Count == 3, "perguntou até o servidor dizer concluído (3 vezes, foram " + estados.Count + ")");
    Exige(estados.All(p => !p.Cabecalhos.ContainsKey("authorization") && p.Corpo!["verificador"] != null), "quem pergunta se prova pelo verificador, não pelo token");
});

Caso("acao-sair-de-todos-nao-mexe-na-sessao-deste-pc", async () =>
{
    var (c, s, f, _, _, _) = await Entrar();
    f.Rota = RotaAcao(0);
    var ini = await c.IniciarAcaoAsync(AcaoConta.SairDeTodos);
    Exige(ini.Ok, "iniciar");
    Exige(f.Pedidos.Last(p => p.Path == "/conta/login/iniciar").Corpo!["acao"]!.GetValue<string>() == "sessoes", "nome 'sessoes'");
    var fim = await c.AguardarAcaoAsync(CancellationToken.None, TimeSpan.FromMilliseconds(5));
    Exige(fim.Ok && c.TemConta && s.ContaSegredoProtegido != null && !c.GoogleVinculado, "este PC continua entrado e nada de Google foi marcado");
});

Caso("acao-excluir-apaga-as-credenciais-locais-so-quando-o-servidor-diz-concluido", async () =>
{
    var (c, s, f, _, _, _) = await Entrar();
    f.Rota = RotaAcao(2);
    Exige((await c.IniciarAcaoAsync(AcaoConta.Excluir)).Ok, "iniciar");
    Exige(f.Pedidos.Last(p => p.Path == "/conta/login/iniciar").Corpo!["acao"]!.GetValue<string>() == "excluir", "nome 'excluir'");
    Exige(c.TemConta, "esperando a página: as credenciais ainda estão aqui (a pessoa pode desistir)");
    var fim = await c.AguardarAcaoAsync(CancellationToken.None, TimeSpan.FromMilliseconds(5));
    Exige(fim.Ok && !c.TemConta && s.ContaId == null && s.DispositivoId == null && s.ContaSegredoProtegido == null && s.Grupos.Count == 0 && !s.ContaGoogleVinculado, "conta excluída: tudo local apagado");
    Exige(await c.AcessoAsync() == null, "sem token depois de excluir");
});

Caso("acao-pedido-vencido-cancelamento-e-sem-rede", async () =>
{
    // pedido vencido no servidor: erro claro e a conta fica
    var (c, s, f, _, _, _) = await Entrar();
    f.Rota = (m, p, b) => p == "/conta/login/iniciar" ? (200, new JsonObject { ["pedido"] = new string('f', 32) })
        : (410, new JsonObject { ["erro"] = "pedido_vencido", ["texto"] = "Este pedido venceu." });
    Exige((await c.IniciarAcaoAsync(AcaoConta.Excluir)).Ok, "iniciar");
    var v = await c.AguardarAcaoAsync(CancellationToken.None, TimeSpan.FromMilliseconds(5));
    Exige(!v.Ok && v.Erro == "pedido_vencido" && c.TemConta, "vencido: não apaga nada");
    var denovo = await c.AguardarAcaoAsync(CancellationToken.None, TimeSpan.FromMilliseconds(5));
    Exige(denovo.Erro == "pedido_vencido" && f.Pedidos.Count(p => p.Path == "/conta/login/estado") == 1, "pedido esquecido: não pergunta de novo");

    // cancelamento pela pessoa
    f.Rota = RotaAcao(1000);
    Exige((await c.IniciarAcaoAsync(AcaoConta.SairDeTodos)).Ok, "iniciar de novo");
    using var cts = new CancellationTokenSource();
    var espera = c.AguardarAcaoAsync(cts.Token, TimeSpan.FromMilliseconds(5));
    await Task.Delay(40); cts.Cancel();
    Exige((await espera).Erro == "cancelado" && c.TemConta, "cancelar para de esperar e não mexe na conta");

    // sem rede no meio da espera: continua esperando e conclui quando a rede volta
    int chamadas = 0;
    f.Rota = (m, p, b) => p == "/conta/login/iniciar" ? (200, new JsonObject { ["pedido"] = new string('g', 32) })
        : ++chamadas < 3 ? (-1, null!) : (200, new JsonObject { ["estado"] = "concluido" });
    Exige((await c.IniciarAcaoAsync(AcaoConta.Vincular)).Ok, "iniciar com rede");
    Exige((await c.AguardarAcaoAsync(CancellationToken.None, TimeSpan.FromMilliseconds(5))).Ok && chamadas == 3, "sem rede não desiste: espera e conclui");

    // sem rede ao iniciar, ou sem conta: texto claro, nada guardado
    f.Rota = (m, p, b) => (-1, null!);
    var sr = await c.IniciarAcaoAsync(AcaoConta.Vincular);
    Exige(!sr.Ok && sr.Texto!.Contains("internet"), "sem rede ao iniciar");
    var (semConta, _, f2) = Novo();
    var sc = await semConta.IniciarAcaoAsync(AcaoConta.Excluir);
    Exige(!sc.Ok && sc.Texto!.Contains("Entre") && f2.Pedidos.Count == 0, "sem conta nem chama o servidor");

    // conta_invalida ao iniciar (já excluída): apaga o local e diz que a sessão acabou
    var (c3, s3, f3, _, _, _) = await Entrar();
    f3.Rota = (m, p, b) => (401, new JsonObject { ["erro"] = "conta_invalida" });
    var inv = await c3.IniciarAcaoAsync(AcaoConta.SairDeTodos);
    Exige(!inv.Ok && inv.Texto == Conta.FraseSessaoEncerrada && !c3.TemConta, "conta inválida: volta ao estado sem conta");
});

Caso("acao-token-que-venceu-no-meio-renova-e-repete-o-iniciar", async () =>
{
    var (c, s, f, _, _, _) = await Entrar();
    int iniciar = 0;
    f.Rota = (m, p, b) =>
    {
        if (p == "/conta/renovar") return (200, Sessao("sa1.renovado.ok", Agora() + 3600));
        if (p == "/conta/login/iniciar") return ++iniciar == 1 ? (401, new JsonObject { ["erro"] = "token_recusado" }) : (200, new JsonObject { ["pedido"] = new string('h', 32) });
        return (404, new JsonObject());
    };
    Exige((await c.IniciarAcaoAsync(AcaoConta.Excluir)).Ok && iniciar == 2, "o 401 faz renovar e tentar uma vez mais");
    Exige(f.Pedidos.Last(p => p.Path == "/conta/login/iniciar").Cabecalhos["authorization"] == "Bearer sa1.renovado.ok", "a segunda tentativa usa o token novo");
});

Caso("acao-verificador-token-e-pedido-nunca-aparecem-nos-textos", async () =>
{
    var (c, s, f, _, _, _) = await Entrar();
    var textos = new List<string>();
    foreach (var (status, corpo) in new (int, JsonObject?)[] { (-1, null), (500, null), (429, new JsonObject { ["erro"] = "servidor_ocupado" }), (401, new JsonObject { ["erro"] = "conta_invalida" }) })
    {
        var (c2, s2, f2, _, _, _) = await Entrar();
        f2.Rota = (m, p, b) => (status, corpo!);
        var r = await c2.IniciarAcaoAsync(AcaoConta.Excluir);
        if (r.Texto != null) textos.Add(r.Texto);
    }
    foreach (var t in textos) Exige(!t.Contains("sa1.") && !t.Contains("Bearer") && !t.Contains(SenhaAntiga) && !t.Contains("pedido="), "texto com token, senha ou pedido: " + t);
    Exige(textos.Count == 4, "os quatro erros precisam ter texto");
});

// ---- grupos (plano 019, etapa 8)

/// <summary>Servidor falso de grupos: guarda a lista de grupos da conta e devolve o que o Worker devolveria.</summary>
Func<string, string, JsonObject?, (int, JsonObject?)> RotaGrupos(List<(string Id, string Nome)> grupos, int status401 = 0)
{
    int contador401 = 0;
    return (m, p, corpo) =>
    {
        if (p == "/conta/renovar")
        {
            var r = Sessao("sa1.carga.assinatura", Agora() + 3600);
            r["grupos"] = new JsonArray(grupos.Select(g => (JsonNode)new JsonObject { ["id"] = g.Id, ["nome"] = g.Nome }).ToArray());
            return (200, r);
        }
        if (status401 > 0 && contador401++ < status401 && p.StartsWith("/grupos")) return (401, new JsonObject { ["erro"] = "token_recusado" });
        if (p == "/grupos" && m == "POST")
        {
            grupos.Add(("ABCD2345", corpo!["nome"]!.GetValue<string>()));
            return (200, new JsonObject { ["id"] = "ABCD2345", ["nome"] = corpo["nome"]!.GetValue<string>(), ["convite"] = "ABCD-2345-EFGH-JKMN" });
        }
        if (p == "/grupos/entrar")
        {
            if (corpo!["convite"]!.GetValue<string>() != "ABCD2345EFGHJKMN") return (404, new JsonObject { ["erro"] = "convite_invalido", ["texto"] = "texto do servidor" });
            grupos.Add(("ABCD2345", "Os Amigos"));
            return (200, new JsonObject { ["id"] = "ABCD2345", ["nome"] = "Os Amigos" });
        }
        if (p == "/grupos/ABCD2345/sair") { grupos.RemoveAll(g => g.Id == "ABCD2345"); return (200, new JsonObject { ["ok"] = true, ["acabou"] = true }); }
        if (p == "/grupos/ABCD2345" && m == "GET")
            return (200, new JsonObject
            {
                ["id"] = "ABCD2345", ["nome"] = "Os Amigos", ["papel"] = "dono", ["convite"] = "ABCD-2345-EFGH-JKMN",
                ["membros"] = new JsonArray(new JsonObject { ["id"] = "c-abcdefghij12", ["apelido"] = "Eu", ["papel"] = "dono" }, new JsonObject { ["id"] = "c-zzzzzzzzzzzz", ["apelido"] = "Ana", ["papel"] = "membro" }),
            });
        if (p == "/grupos/ABCD2345/nome") return (200, new JsonObject { ["ok"] = true, ["nome"] = corpo!["nome"]!.GetValue<string>() });
        if (p == "/grupos/ABCD2345/convite") return (200, new JsonObject { ["convite"] = "WXYZ-2345-EFGH-JKMN" });
        if (p == "/grupos/ABCD2345/remover") return (200, new JsonObject { ["ok"] = true });
        return (404, new JsonObject { ["erro"] = "nao_encontrado" });
    };
}

Caso("grupo-criar-vai-com-token-e-renova-a-lista-de-grupos", async () =>
{
    var (c, s, f, _, _, _) = await Entrar();
    var grupos = new List<(string, string)> { ("grupo", "Grupo do Streemu") };
    f.Rota = RotaGrupos(grupos);
    f.Pedidos.Clear();
    var r = await c.CriarGrupoAsync("  Os Amigos ", "Fulano");
    Exige(r.Ok && r.Id == "ABCD2345" && r.Convite == "ABCD-2345-EFGH-JKMN", "criar: " + r.Texto);
    var pedido = f.Pedidos.Single(p => p.Path == "/grupos");
    Exige(pedido.Cabecalhos["authorization"] == "Bearer sa1.carga.assinatura", "criar grupo vai com o token");
    Exige(pedido.Corpo!["nome"]!.GetValue<string>() == "Os Amigos" && pedido.Corpo["apelido"]!.GetValue<string>() == "Fulano", "nome sem espaços nas pontas e o apelido");
    Exige(f.Pedidos.Any(p => p.Path == "/conta/renovar"), "depois de criar o token é renovado (o campo g)");
    Exige(s.Grupos.Select(g => g.Id).SequenceEqual(new[] { "grupo", "ABCD2345" }), "a lista local tem o grupo novo");
    Exige(c.LinkDoConvite(r.Convite!) == $"{Servidor}/g/ABCD2345EFGHJKMN", "link do convite: " + c.LinkDoConvite(r.Convite!));
});

Caso("link-do-convite-usa-o-site-quando-o-servidor-diz-qual-e", async () =>
{
    var (c, _, f) = Novo();
    f.Rota = (m, p, b) => (200, new JsonObject { ["contas"] = true, ["obrigatoria"] = true, ["codigo"] = true, ["site"] = "https://www.streemu.com.br" });
    await c.ConfigAsync();
    Exige(c.LinkDoConvite("ABCD-2345-EFGH-JKMN") == "https://www.streemu.com.br/g/ABCD2345EFGHJKMN", "com site: " + c.LinkDoConvite("ABCD-2345-EFGH-JKMN"));
    f.Rota = (m, p, b) => (200, new JsonObject { ["contas"] = true, ["site"] = "http://exemplo.com" }); // só https
    await c.ConfigAsync();
    Exige(c.LinkDoConvite("ABCD2345EFGHJKMN") == $"{Servidor}/g/ABCD2345EFGHJKMN", "site sem https é ignorado: " + c.LinkDoConvite("ABCD2345EFGHJKMN"));
});

Caso("grupo-entrar-por-convite-e-convite-errado-com-frase-do-plano", async () =>
{
    var (c, s, f, _, _, _) = await Entrar();
    var grupos = new List<(string, string)> { ("grupo", "Grupo do Streemu") };
    f.Rota = RotaGrupos(grupos);
    var ruim = await c.EntrarAsync("ZZZZZZZZZZZZZZZZ", "Fulano");
    Exige(!ruim.Ok && ruim.Erro == "convite_invalido" && ruim.Texto == "Esse convite não vale mais. Peça um novo a quem te convidou.", "frase do convite: " + ruim.Texto);
    Exige(s.Grupos.Count == 1, "convite errado não muda a lista");
    var ok = await c.EntrarAsync("ABCD2345EFGHJKMN", "Fulano");
    Exige(ok.Ok && ok.Nome == "Os Amigos" && s.Grupos.Any(g => g.Id == "ABCD2345"), "entrou: " + ok.Texto);
});

Caso("grupo-sair-ultimo-membro-avisa-que-acabou", async () =>
{
    var (c, s, f, _, _, _) = await Entrar();
    var grupos = new List<(string, string)> { ("grupo", "Grupo do Streemu"), ("ABCD2345", "Os Amigos") };
    f.Rota = RotaGrupos(grupos);
    var r = await c.SairDoGrupoAsync("ABCD2345");
    Exige(r.Ok && r.Acabou && s.Grupos.Select(g => g.Id).SequenceEqual(new[] { "grupo" }), "saiu e a lista local ficou só com o grupo antigo");
});

Caso("grupo-info-renomear-trocar-convite-e-remover", async () =>
{
    var (c, s, f, _, _, _) = await Entrar();
    var grupos = new List<(string, string)> { ("grupo", "Grupo do Streemu"), ("ABCD2345", "Os Amigos") };
    f.Rota = RotaGrupos(grupos);
    await c.AcessoAsync(forcar: true);
    var (info, texto) = await c.InfoAsync("ABCD2345");
    Exige(info != null && info.Papel == "dono" && info.Membros.Count == 2 && info.Convite == "ABCD-2345-EFGH-JKMN", "info: " + texto);
    var nome = await c.RenomearAsync("ABCD2345", " Novo nome ");
    Exige(nome.Ok && nome.Nome == "Novo nome" && s.Grupos.First(g => g.Id == "ABCD2345").Nome == "Novo nome", "o nome novo vale já na lista local");
    var convite = await c.TrocarConviteAsync("ABCD2345");
    Exige(convite.Ok && convite.Convite == "WXYZ-2345-EFGH-JKMN", "convite novo");
    var tirar = await c.RemoverAsync("ABCD2345", "c-zzzzzzzzzzzz");
    Exige(tirar.Ok, "remover");
    Exige(f.Pedidos.Single(p => p.Path.EndsWith("/remover")).Corpo!["conta"]!.GetValue<string>() == "c-zzzzzzzzzzzz", "o remover leva a conta do membro");
});

Caso("grupo-token-que-venceu-renova-e-repete-uma-vez", async () =>
{
    var (c, _, f, _, _, _) = await Entrar();
    var grupos = new List<(string, string)> { ("grupo", "Grupo do Streemu") };
    f.Rota = RotaGrupos(grupos, status401: 1);
    f.Pedidos.Clear();
    var r = await c.CriarGrupoAsync("Os Amigos", "Fulano");
    Exige(r.Ok, "criar depois de um 401: " + r.Texto);
    Exige(f.Pedidos.Count(p => p.Path == "/grupos") == 2, "o pedido foi repetido uma vez só");
});

Caso("grupo-sem-conta-sem-rede-e-sessao-encerrada-dizem-o-que-fazer", async () =>
{
    var (c0, _, _) = Novo();
    var semConta = await c0.CriarGrupoAsync("X", "Y");
    Exige(!semConta.Ok && semConta.Texto == "Entre na sua conta primeiro.", "sem conta: " + semConta.Texto);
    var (c, s, f, _, _, _) = await Entrar();
    f.Rota = (m, p, b) => p == "/conta/renovar" ? (200, Sessao("sa1.carga.assinatura", Agora() + 3600)) : (-1, null);
    var offline = await c.EntrarAsync("ABCD2345EFGHJKMN", "Fulano");
    Exige(!offline.Ok && offline.Texto!.Contains("internet"), "sem rede: " + offline.Texto);
    Exige(c.TemConta, "sem rede nunca apaga a credencial");
    f.Rota = (m, p, b) => p.StartsWith("/grupos") ? (403, new JsonObject { ["erro"] = "conta_invalida" }) : (200, Sessao("sa1.carga.assinatura", Agora() + 3600));
    var morta = await c.CriarGrupoAsync("X", "Y");
    Exige(!morta.Ok && morta.Texto == Conta.FraseSessaoEncerrada && !c.TemConta, "conta_invalida apaga as credenciais locais e manda entrar de novo");
});

Caso("grupo-frases-de-erro-tem-motivo-e-proxima-acao-e-nada-secreto", () =>
{
    foreach (var erro in new[] { "convite_invalido", "grupo_cheio", "muitos_grupos", "muitas_tentativas", "nao_membro", "so_dono", "token_recusado", "servidor_ocupado", "nao_encontrado", "qualquer_outro" })
    {
        var t = Conta.FraseDeGrupo(erro, null);
        Exige(t.Length > 20 && t.EndsWith('.'), $"{erro}: frase curta demais ou sem ponto final: '{t}'");
        Exige(!t.Contains("sa1.") && !t.Contains("Bearer"), $"{erro}: texto com token");
    }
    Exige(Conta.FraseDeGrupo("requisicao_invalida", "Dê um nome ao grupo (até 40 letras).") == "Dê um nome ao grupo (até 40 letras).", "requisição inválida usa o texto do servidor");
    Exige(Conta.FraseDeGrupo("erro_novo", "Texto do servidor.") == "Texto do servidor.", "erro desconhecido usa o texto do servidor");
    return Task.CompletedTask;
});

Caso("link-de-convite-aceita-hifens-minusculas-e-link-https-e-recusa-o-resto", () =>
{
    Exige(AppLink.InviteFrom("streemu://grupo/ABCD2345EFGHJKMN") == "ABCD2345EFGHJKMN", "link simples");
    Exige(AppLink.InviteFrom("streemu://grupo/abcd-2345-efgh-jkmn/") == "ABCD2345EFGHJKMN", "link com hífens e minúsculas");
    Exige(AppLink.InviteFrom("streemu://assistir/ABCD2345EFGHJKMN") == null, "o link de assistir não é convite");
    Exige(AppLink.InviteFrom("streemu://grupo/curto") == null && AppLink.InviteFrom("streemu://grupo/ABCD2345EFGHJKM1") == null, "tamanho errado ou letra fora do alfabeto");
    Exige(AppLink.NormalizeInvite("https://servidor.exemplo/g/ABCD2345EFGHJKMN") == "ABCD2345EFGHJKMN", "link https do convite");
    Exige(AppLink.NormalizeInvite(" ABCD-2345 EFGH-JKMN ") == "ABCD2345EFGHJKMN", "código colado com espaços e hífens");
    Exige(AppLink.NormalizeInvite("") == null && AppLink.NormalizeInvite(null) == null, "vazio");
    return Task.CompletedTask;
});

// A cota do relay (spec 024): o /turn responde 429/503 com `texto` em português; o app mostra esse texto e, sem ele, os textos de antes.
Caso("relay-texto-do-servidor", () =>
{
    const string cota = "O relay de reserva do grupo chegou ao limite do mês. A conexão direta continua funcionando; o relay volta no dia 1.";
    Exige(RelayConfig.TextoDoErro(HttpStatusCode.TooManyRequests, new JsonObject { ["erro"] = "cota_grupo", ["texto"] = cota }.ToJsonString()) == cota, "429 com texto");
    Exige(RelayConfig.TextoDoErro(HttpStatusCode.ServiceUnavailable, new JsonObject { ["erro"] = "relay_pausado", ["texto"] = "pausado" }.ToJsonString()) == "pausado", "503 com texto");
    Exige(RelayConfig.TextoDoErro(HttpStatusCode.Forbidden, new JsonObject { ["erro"] = "sem_grupo", ["texto"] = "Entre num grupo (ou crie um) para usar o relay." }.ToJsonString()) == "Entre num grupo (ou crie um) para usar o relay.", "403 com texto");
    Exige(RelayConfig.TextoDoErro(HttpStatusCode.Unauthorized, "entre na sua conta do Streemu").Contains("não aceitou sua conta"), "401 sem JSON mantém o texto de antes");
    Exige(RelayConfig.TextoDoErro(HttpStatusCode.BadGateway, "a Cloudflare recusou") == "o servidor do grupo respondeu 502: a Cloudflare recusou", "502 sem JSON mantém o texto de antes");
    Exige(RelayConfig.TextoDoErro(HttpStatusCode.TooManyRequests, "{\"erro\":\"ritmo\"}").StartsWith("o servidor do grupo respondeu 429"), "JSON sem texto mantém o de antes");
    Exige(RelayConfig.TextoDoErro(HttpStatusCode.TooManyRequests, "{\"texto\":\"  \"}").StartsWith("o servidor do grupo respondeu 429"), "texto vazio não vale");
    return Task.CompletedTask;
});

// ---- nome da conta (spec 027)

Caso("nome-vem-do-retirar-e-fica-no-estado", async () =>
{
    var (c, s, f) = Novo();
    f.Rota = (m, p, b) => p switch
    {
        "/conta/login/iniciar" => (200, new JsonObject { ["pedido"] = new string('a', 32) }),
        "/conta/login/retirar" => (200, Sessao("sa1.a.b", Agora() + 3600).Also(j => j["nome"] = "Ana")),
        _ => (404, new JsonObject()),
    };
    await c.IniciarEntradaAsync();
    Exige((await c.ConcluirEntradaAsync("123456", "")).Ok, "concluir");
    Exige(c.Nome == "Ana" && c.NomeConhecido && s.ContaNome == "Ana", "o nome do retirar tem que ir para o estado");
});

Caso("nome-do-renovar-servidor-antigo-nao-muda-nada", async () =>
{
    var (c, s, f, _, _, _) = await Entrar();
    s.ContaNome = "Ana"; // o que já estava guardado
    f.Rota = (m, p, b) => p == "/conta/renovar" ? (200, Sessao("sa1.r.x", Agora() + 3600)) : (404, new JsonObject()); // sem o campo "nome"
    int avisos = 0; c.NomeAdotadoNaMigracao += _ => avisos++;
    Exige(await c.GarantirAsync() == EstadoSessao.Ok, "renovar");
    Exige(c.Nome == "Ana" && !c.NomeConhecido && avisos == 0, "servidor antigo não manda o campo: o nome guardado fica e nada é 'conhecido'");
});

Caso("nome-do-renovar-adotado-na-migracao-avisa-uma-vez", async () =>
{
    var (c, s, f, _, _, _) = await Entrar();
    Exige(c.Nome == null && !c.NomeConhecido, "o login sem campo deixa a conta sem nome");
    string? nome = null; int avisos = 0; c.NomeAdotadoNaMigracao += n => { nome = n; avisos++; };
    string? mandado = null;
    c.ApelidoAtual = () => "Fulano";
    f.Rota = (m, p, b) => { if (p == "/conta/renovar") { mandado = b!["apelido"]!.GetValue<string>(); return (200, Sessao("sa1.r.x", Agora() + 3600).Also(j => j["nome"] = "Fulano")); } return (404, new JsonObject()); };
    Exige(await c.GarantirAsync() == EstadoSessao.Ok, "renovar 1");
    Exige(mandado == "Fulano", "conta sem nome manda o apelido salvo (migração)");
    Exige(c.Nome == "Fulano" && c.NomeConhecido && s.ContaNome == "Fulano" && avisos == 1 && nome == "Fulano", "o servidor adotou o apelido: guarda e avisa");
    Exige(await c.GarantirAsync() == EstadoSessao.Ok, "renovar 2");
    Exige(mandado == "", "com nome, o apelido não vai mais (vai vazio)");
    Exige(avisos == 1, "o aviso é uma vez só");
});

Caso("nome-do-renovar-nulo-conta-sem-nome-e-conhecida", async () =>
{
    var (c, s, f, _, _, _) = await Entrar();
    f.Rota = (m, p, b) => p == "/conta/renovar" ? (200, Sessao("sa1.r.x", Agora() + 3600).Also(j => j["nome"] = null)) : (404, new JsonObject());
    int avisos = 0; c.NomeAdotadoNaMigracao += _ => avisos++;
    Exige(await c.GarantirAsync() == EstadoSessao.Ok, "renovar");
    Exige(c.Nome == null && c.NomeConhecido && avisos == 0, "nome nulo com o campo presente = conta sem nome (a tela pede 'Escolha seu nome')");
});

Caso("nome-trocado-em-outro-aparelho-chega-no-renovar", async () =>
{
    var (c, s, f, _, _, _) = await Entrar();
    s.ContaNome = "Ana";
    f.Rota = (m, p, b) => p == "/conta/renovar" ? (200, Sessao("sa1.r.x", Agora() + 3600).Also(j => j["nome"] = "Ana Maria")) : (404, new JsonObject());
    int avisos = 0; c.NomeAdotadoNaMigracao += _ => avisos++;
    await c.GarantirAsync();
    Exige(c.Nome == "Ana Maria" && avisos == 0, "troca de nome feita no celular: guarda, sem o aviso da migração");
});

Caso("mudar-nome-200-guarda-e-vai-com-token", async () =>
{
    var (c, s, f, _, _, _) = await Entrar();
    int mudou = 0; c.Changed += () => mudou++;
    f.Rota = (m, p, b) => p == "/conta/nome" ? (200, new JsonObject { ["nome"] = "Bia" }) : (404, new JsonObject());
    var r = await c.MudarNomeAsync("  Bia  ");
    Exige(r.Ok && c.Nome == "Bia" && s.ContaNome == "Bia" && c.NomeConhecido && mudou == 1, "200 guarda o nome do servidor e avisa a tela");
    var p = f.Pedidos.Last(x => x.Path == "/conta/nome");
    Exige(p.Cabecalhos["authorization"] == "Bearer sa1.carga.assinatura" && p.Corpo!["nome"]!.GetValue<string>() == "Bia", "vai com o token e o nome aparado");
});

Caso("mudar-nome-400-429-e-vazio-nao-mudam-nada-e-tem-texto", async () =>
{
    var (c, s, f, _, _, _) = await Entrar();
    s.ContaNome = "Ana";
    f.Rota = (m, p, b) => p == "/conta/nome" ? (400, new JsonObject { ["erro"] = "nome_invalido", ["texto"] = "Esse nome não vale. Use até 24 letras." }) : (404, new JsonObject());
    var r = await c.MudarNomeAsync("x*y");
    Exige(!r.Ok && r.Erro == "nome_invalido" && r.Texto == "Esse nome não vale. Use até 24 letras." && c.Nome == "Ana", "400: motivo do servidor e nome intacto");
    f.Rota = (m, p, b) => p == "/conta/nome" ? (429, new JsonObject { ["erro"] = "muitas_trocas", ["texto"] = "Você já trocou o nome muitas vezes hoje. Tente amanhã." }) : (404, new JsonObject());
    r = await c.MudarNomeAsync("Bia");
    Exige(!r.Ok && r.Erro == "muitas_trocas" && !string.IsNullOrEmpty(r.Texto) && c.Nome == "Ana", "429: motivo do servidor e nome intacto");
    int antes = f.Pedidos.Count;
    r = await c.MudarNomeAsync("   ");
    Exige(!r.Ok && r.Erro == "nome_invalido" && !string.IsNullOrEmpty(r.Texto) && f.Pedidos.Count == antes, "vazio nem vai ao servidor");
});

Caso("mudar-nome-401-renova-e-repete-uma-vez", async () =>
{
    var (c, s, f, _, _, _) = await Entrar();
    int tentativas = 0;
    f.Rota = (m, p, b) =>
    {
        if (p == "/conta/renovar") return (200, Sessao("sa1.renovado.ok", Agora() + 3600));
        if (p == "/conta/nome") return ++tentativas == 1 ? (401, new JsonObject { ["erro"] = "token_recusado" }) : (200, new JsonObject { ["nome"] = "Bia" });
        return (404, new JsonObject());
    };
    Exige((await c.MudarNomeAsync("Bia")).Ok && tentativas == 2 && c.Nome == "Bia", "o 401 renova o token e tenta uma vez mais");
    Exige(f.Pedidos.Last(x => x.Path == "/conta/nome").Cabecalhos["authorization"] == "Bearer sa1.renovado.ok", "a segunda tentativa usa o token novo");
    // 401 que continua: para na segunda tentativa, com o texto do servidor, e não apaga a conta
    f.Rota = (m, p, b) => p == "/conta/renovar" ? (200, Sessao("sa1.r2.x", Agora() + 3600)) : p == "/conta/nome" ? (401, new JsonObject { ["erro"] = "token_recusado", ["texto"] = "Sua sessão não vale mais. Entre de novo." }) : (404, new JsonObject());
    var r = await c.MudarNomeAsync("Caio");
    Exige(!r.Ok && r.Erro == "token_recusado" && r.Texto == "Sua sessão não vale mais. Entre de novo." && c.Nome == "Bia" && c.TemConta, "401 duas vezes: erro com texto, nome e conta intactos");
});

Caso("mudar-nome-conta-invalida-apaga-e-sem-rede-nao-muda-nada", async () =>
{
    var (c, s, f, _, _, _) = await Entrar();
    s.ContaNome = "Ana";
    f.Rota = (m, p, b) => (-1, null!);
    var r = await c.MudarNomeAsync("Bia");
    Exige(!r.Ok && r.Erro == "sem_rede" && r.Texto!.Contains("internet") && c.Nome == "Ana" && c.TemConta, "sem rede: texto de sem rede, nome e conta intactos");
    f.Rota = (m, p, b) => p == "/conta/nome" ? (403, new JsonObject { ["erro"] = "conta_invalida" }) : (404, new JsonObject());
    r = await c.MudarNomeAsync("Bia");
    Exige(!r.Ok && r.Erro == "conta_invalida" && r.Texto == Conta.FraseSessaoEncerrada && !c.TemConta && c.Nome == null && s.ContaNome == null, "conta_invalida apaga a conta local e o nome");
});

Caso("sair-apaga-o-nome-e-sem-conta-nao-troca", async () =>
{
    var (c, s, f, _, _, _) = await Entrar();
    s.ContaNome = "Ana"; s.EscolhaDeNomeMostrada = true;
    f.Rota = (m, p, b) => (200, new JsonObject { ["ok"] = true });
    await c.SairAsync();
    Exige(c.Nome == null && !c.NomeConhecido && !s.EscolhaDeNomeMostrada, "sair zera o nome");
    int antes = f.Pedidos.Count;
    var r = await c.MudarNomeAsync("Bia");
    Exige(!r.Ok && f.Pedidos.Count == antes, "sem conta não chama o servidor");
});

Caso("nome-da-sala-so-vale-com-conta-ja-com-nome-e-depois-da-troca-recente", async () =>
{
    var hora = DateTimeOffset.UtcNow;
    var (c, s, f) = Novo(() => hora);
    f.Rota = (m, p, b) => p == "/conta/login/iniciar" ? (200, new JsonObject { ["pedido"] = new string('a', 32) }) : p == "/conta/login/retirar" ? (200, Sessao("sa1.a.b", Agora() + 3600)) : (404, new JsonObject());
    await c.IniciarEntradaAsync(); await c.ConcluirEntradaAsync("123456", "");
    Exige(!c.AdotarNomeDaSala("Qualquer") && c.Nome == null, "conta sem nome: a sala não dá nome (é o apelido do ola)");
    s.ContaNome = "Ana"; hora += TimeSpan.FromMinutes(1);
    int mudou = 0; c.Changed += () => mudou++;
    Exige(c.AdotarNomeDaSala("Ana Maria") && c.Nome == "Ana Maria" && mudou == 1, "conta com nome: a sala difundiu outro nome (troca em outro aparelho)");
    hora += TimeSpan.FromSeconds(20);
    Exige(!c.AdotarNomeDaSala("Ana Maria") && !c.AdotarNomeDaSala("  ") && mudou == 1, "igual ou vazio não muda");
    f.Rota = (m, p, b) => p == "/conta/nome" ? (200, new JsonObject { ["nome"] = "Bia" }) : (404, new JsonObject());
    await c.MudarNomeAsync("Bia");
    Exige(!c.AdotarNomeDaSala("Ana Maria") && c.Nome == "Bia", "logo depois de uma troca minha, a sala ainda pode difundir o nome antigo: não vale");
    hora += TimeSpan.FromSeconds(20);
    Exige(c.AdotarNomeDaSala("Caio") && c.Nome == "Caio", "passado o tempo, vale");
});

Console.WriteLine($"casos: {falhas} falha(s) em {total}");
return falhas == 0 ? 0 : 1;

/// <summary>Servidor falso dentro do HttpClient. <see cref="Rota"/> devolve (status, corpo); status -1 = a conexão cai sem resposta.</summary>
sealed class Falso : HttpMessageHandler
{
    public sealed record Pedido(string Metodo, string Path, JsonObject? Corpo, Dictionary<string, string> Cabecalhos);
    public readonly List<Pedido> Pedidos = new();
    public Func<string, string, JsonObject?, (int Status, JsonObject? Corpo)> Rota = (m, p, c) => (404, null);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        string texto = req.Content == null ? "" : await req.Content.ReadAsStringAsync(ct);
        JsonObject? corpo = null; try { corpo = JsonNode.Parse(texto) as JsonObject; } catch { }
        var cab = req.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        Pedidos.Add(new(req.Method.Method, req.RequestUri!.AbsolutePath, corpo, cab));
        var (status, resp) = Rota(req.Method.Method, req.RequestUri.AbsolutePath, corpo);
        if (status < 0) throw new HttpRequestException("conexão caiu");
        var r = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(resp?.ToJsonString() ?? "não é json", Encoding.UTF8, "application/json") };
        return r;
    }
}

static class Ext { public static JsonObject Also(this JsonObject j, Action<JsonObject> a) { a(j); return j; } }
