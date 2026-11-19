using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Streemu.Telemetria;

/// <summary>Casos do envio contra o servidor falso. Cada caso usa pasta, sessão e servidor próprios; nada toca em %LOCALAPPDATA%\Streemu.</summary>
static class Casos
{
    public static int Falhas;
    public static string Tmp = "";
    static int _n;

    public static void Rodar(bool sabotar)
    {
        if (sabotar)
        {
            // Prova de que o teste falha de verdade: o servidor falso confirma sem gravar e o caso "simples" tem que reprovar.
            Caso("SABOTADO-envio-simples-com-servidor-que-mente", () => Simples(sabotar: true));
            return;
        }
        Caso("envio-simples-confirma-e-cursor-bate-com-o-tamanho", () => Simples(false));
        Caso("resposta-perdida-nao-duplica-nem-falta", RespostaPerdida);
        Caso("confirmado-menor-reenvia-do-ponto-certo", ConfirmadoMenor);
        Caso("erro-500-vira-falha-e-recupera", () => FalhaERecupera(Modo.Erro500, 2, 20_000, "500"));
        Caso("demora-acima-de-20s-vira-falha-e-recupera", () => FalhaERecupera(Modo.Demorar, 1, 45_000, "demora"));
        Caso("desabilitado-nao-faz-nenhum-pedido", Desabilitado);
        Caso("sessao-anterior-pendente-sobe-na-abertura-seguinte", SessaoAnterior);
        Caso("controle-verificador-de-cursor-pega-servidor-que-mente", ControleVerificador);
        Caso("recusados-isola-o-arquivo-e-o-resto-sobe", RecusadosIsolaOArquivo);
        Caso("confirmado-maior-que-o-arquivo-nao-vira-cursor-nem-loop", ConfirmadoMaior);
        Caso("arquivo-local-menor-que-o-confirmado-nao-reenvia-do-zero", ArquivoLocalMenor);
        Caso("confirmado-negativo-e-ignorado-e-recupera", () => RespostaInvalida(JsonValue.Create(-5L), "negativo"));
        Caso("confirmado-texto-e-ignorado-e-recupera", () => RespostaInvalida(JsonValue.Create("abc"), "texto"));
        Caso("peca-sem-resposta-nao-repete-20-vezes-e-recupera", () => RespostaInvalida(null, "omitido"));
        Caso("perda-local-vira-evento-perda_local-no-servidor", PerdaLocal);
        Caso("teto-3gb-e-descarte-concluidas-antes-das-pendentes", TetoDescarte);
        Caso("teto-nao-apaga-nada-quando-cabe", TetoNaoApagaQuandoCabe);
        Caso("teto-nunca-apaga-a-sessao-atual", TetoNuncaApagaAAtual);
        Caso("gravacao-nao-espera-o-disco-e-chega-inteira-e-em-ordem", GravacaoNaoEspera);
        Caso("gravacao-concorrente-guarda-a-ordem-de-cada-thread-e-nao-perde", GravacaoConcorrente);
        Caso("gravacao-fila-cheia-descarta-sem-bloquear-e-conta-perda", GravacaoFilaCheia);
        Caso("custo-telemetria_custo-sobe-ao-servidor-so-com-numeros", CustoSobe);
        Caso("fechar-descarrega-a-fila-de-gravacao", FecharDescarrega);
        Caso("evento-urgente-pula-o-agrupamento-e-o-normal-espera", EventoUrgente);
        Caso("vigia-ui-travada-avisa-ao-voltar-e-em-curso", VigiaTravada);
        Caso("vigia-interface-que-responde-nao-gera-aviso", VigiaSemTrava);
        Caso("vigia-suspensao-nao-vira-ui-travada", VigiaSuspensao);
        Caso("rede-por-ligacao-calcula-taxa-e-perda-do-intervalo", RedePorLigacao);
        Caso("rede-por-ligacao-so-amostra-conectada-e-esquece-quem-saiu", RedeSoConectada);
        Caso("anonimo-troca-nomes-por-pessoa-n-e-tira-identidade-do-evento", AnonimizadorTroca);
        Caso("anonimo-sessao-grava-log-e-series-mas-nao-o-texto-livre", ReduzidaGrava);
        Caso("anonimo-envio-sem-nome-sem-identidade-e-so-depois-da-sala", ReduzidaEnvio);
        Caso("conta-token-no-envio-normal-e-conta-no-pacote", ContaTokenNormal);
        Caso("conta-bilhete-no-anonimo-e-token-nunca-sai", ContaBilheteAnonimo);
        Caso("conta-sem-conta-so-sobem-os-eventos-do-login", ContaSemConta);
        Caso("conta-arquivos-de-antes-do-login-sobem-completos-depois-do-login", ArquivosDeAntesDoLoginSobemDepois);
        Caso("login-sem-conta-fila-limita-em-20-e-so-leva-os-campos-permitidos", LoginFila);
        Caso("login-sem-conta-corpo-cabe-em-2kb-e-sinal-nao-fica-verde-nem-vermelho", LoginCorpoESinal);
        Caso("conta-401-renova-o-token-e-tenta-de-novo", Conta401Renova);
        Caso("aviso-instalacao-nova-nao-envia-antes-de-ler-o-aviso", AguardaOAviso);
        Caso("aviso-texto-bate-com-o-worker-e-diz-o-que-importa", TextoBateComOWorker);
        Caso("pesquisa-convite-so-com-link-valido-apos-dias-de-uso-e-respeita-adiar", ConvitePesquisaRegras);
        Caso("falha-aviso-de-amigo-nao-conta-como-falha", AvisoDeAmigoNaoEFalha);
        Caso("falha-grave-classifica-so-o-que-importa", FalhaGraveClassifica);
        Caso("falha-grave-freio-limita-rajada-e-nao-segura-o-fatal", FalhaGraveFreio);
        Caso("cobertura-e-a-hora-do-conteudo-nao-a-do-envio", CoberturaHoraDoConteudo);
        Caso("cobertura-hora-vira-o-dia-e-log-que-comecou-antes-da-meia-noite", CoberturaViradaDoDia);
        Caso("cobertura-arquivo-sem-hora-vai-nulo-e-ancora-sobrevive-ao-reinicio", CoberturaSemHoraEAncora);
    }

    // ---------- contra o Worker de verdade (wrangler dev iniciado pelo scripts/testes-locais.ps1) ----------

    /// <summary>
    /// Os mesmos Envio e SessaoLocal contra o Worker real (relay/telemetria.js) rodando local. O servidor falso copia o protocolo; este é o que
    /// diz se a cópia está certa. Não injeta falha de rede (isso é do servidor falso). Tudo vai para o estado temporário do wrangler, nunca à produção.
    /// </summary>
    public static void RodarWorker(string url, string tokenConta, string token)
    {
        url = url.TrimEnd('/');
        Caso("worker-envio-simples-e-leitura-devolve-os-mesmos-bytes", () =>
        {
            var s = Sessao(300, 40);
            using var envio = Criar(url, tokenConta, s);
            envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>()); envio.Agora();
            Exigir(Esperar(() => Converge(s), 30_000), "não confirmou tudo: " + Estado(s));
            envio.Fechar(TimeSpan.FromSeconds(3));
            Exigir(Esperar(() => Converge(s), 10_000), "depois de fechar ficou pendente: " + Estado(s));
            ExigirIgualNoWorker(url, token, envio.Pc, s);
            using var http = new HttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{url}/telemetria/sessoes?pc={envio.Pc}&desde=0");
            req.Headers.Add("x-streemu-token", token);
            string json = http.Send(req).Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Exigir(json.Contains(s.Id), "a sessão não apareceu em /telemetria/sessoes");
            var sessoes = JsonNode.Parse(json)!.AsArray().SelectMany(x => x!["sessoes"]!.AsArray()).FirstOrDefault(x => x!["sessao"]!.GetValue<string>() == s.Id);
            Exigir(sessoes?["fechou"]?.GetValue<string>() == "normal", "o servidor não marcou a sessão como \"fechou normalmente\": " + sessoes);
        });
        Caso("worker-sessao-anterior-pendente-sobe-na-abertura-seguinte", () =>
        {
            var velha = Sessao(150, 10);
            velha.E.Fechou = "normal"; velha.E.Versao = "0.1.0"; velha.E.Commit = "1234567"; velha.E.Canal = "main"; velha.Salvar(); // "criada por um app antigo"
            var nova = Sessao(50, 5);
            var anteriores = Manutencao.Anteriores(nova);
            Exigir(anteriores.Count == 1 && anteriores[0].Id == velha.Id, "Manutencao.Anteriores não devolveu a pendente");
            using var envio = Criar(url, tokenConta, nova);
            envio.Ativo = true; envio.Iniciar(anteriores); envio.Agora();
            Exigir(Esperar(() => Converge(nova) && Converge(anteriores[0]), 30_000), "não subiu tudo: " + Estado(anteriores[0]) + " | " + Estado(nova));
            envio.Fechar(TimeSpan.FromSeconds(3));
            ExigirIgualNoWorker(url, token, envio.Pc, anteriores[0]);
            ExigirIgualNoWorker(url, token, envio.Pc, nova);
            // o build de cada sessão é o do app que a CRIOU (o cabeçalho do envio é o do app de agora)
            ExigirBuildNoWorker(url, token, envio.Pc, anteriores[0], "0.1.0", "1234567");
            ExigirBuildNoWorker(url, token, envio.Pc, nova, nova.E.Versao!, nova.E.Commit!);
        });
        Caso("worker-token-errado-vira-falha-e-nada-sobe", () =>
        {
            var s = Sessao(50, 5);
            using var envio = Criar(url, "sa1.falso.falso", s);
            envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>()); envio.Agora();
            Exigir(Esperar(() => envio.UltimoErro != null, 15_000), "o app não percebeu o token recusado");
            Exigir(envio.UltimoErro!.Contains("sessão da conta"), "mensagem inesperada: " + envio.UltimoErro);
            Exigir(envio.Situacao().Estado == EstadoEnvio.Falha, "o indicador devia ficar vermelho");
            Exigir(s.Cursor(SessaoLocal.Log) == 0, "cursor andou sem o servidor aceitar");
            envio.Fechar(TimeSpan.FromSeconds(1));
        });
        Caso("worker-leitura-exige-o-token-e-a-senha-do-grupo-nao-le", () =>
        {
            using var http = new HttpClient();
            HttpStatusCode Get(string rota, string? cab, string? valor)
            {
                using var r = new HttpRequestMessage(HttpMethod.Get, url + rota);
                if (cab != null) r.Headers.Add(cab, valor);
                return http.Send(r).StatusCode;
            }
            Exigir(Get("/telemetria/pcs", "x-streemu-senha", "teste") == HttpStatusCode.Unauthorized, "a senha do grupo leu /telemetria/pcs");
            Exigir(Get("/telemetria/pcs", null, null) == HttpStatusCode.Unauthorized, "sem credencial leu /telemetria/pcs");
            Exigir(Get("/telemetria/pcs", "x-streemu-token", token) == HttpStatusCode.OK, "o token de leitura não leu /telemetria/pcs");
        });
    }

    static void ExigirIgualNoWorker(string url, string token, string pc, SessaoLocal s)
    {
        using var http = new HttpClient();
        foreach (var (nome, caminho) in s.Arquivos())
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{url}/telemetria/arquivo?pc={pc}&sessao={s.Id}&arquivo={Uri.EscapeDataString(nome)}");
            req.Headers.Add("x-streemu-token", token);
            using var resp = http.Send(req);
            Exigir(resp.IsSuccessStatusCode, $"{nome}: leitura respondeu {(int)resp.StatusCode}");
            using var gz = new System.IO.Compression.GZipStream(resp.Content.ReadAsStream(), System.IO.Compression.CompressionMode.Decompress);
            var ms = new MemoryStream();
            gz.CopyTo(ms);
            byte[] disco = Ler(caminho);
            Exigir(disco.AsSpan().SequenceEqual(ms.ToArray()), $"{nome}: o Worker devolveu {ms.Length} bytes diferentes dos {disco.Length} do disco");
            Exigir(s.Cursor(nome) == disco.Length, $"{nome}: cursor {s.Cursor(nome)} ≠ tamanho {disco.Length}");
        }
    }

    // ---------- infraestrutura ----------

    static void Caso(string nome, Action a)
    {
        Reiniciar();
        try { a(); Console.WriteLine("OK    " + nome); }
        catch (Exception e) { Falhas++; Console.WriteLine("FALHA " + nome + ": " + e.Message); }
    }

    /// <summary>Comportamento ATUAL registrado como conhecido: nunca reprova (cada etapa seguinte troca a linha por um caso que exige o correto).</summary>
    static void Conhecido(string nome, Func<string> a)
    {
        Reiniciar();
        try { Console.WriteLine("CONHECIDO " + nome + ": " + a()); }
        catch (Exception e) { Console.WriteLine("CONHECIDO " + nome + " (erro ao observar: " + e.GetType().Name + ": " + e.Message + ")"); }
    }

    static void Reiniciar()
    {
        Gravador.PausarParaTeste = false; Gravador.MaxItens = 20_000; Gravador.MaxBytes = 32L * 1024 * 1024;
        Gravador.Descarregar(TimeSpan.FromSeconds(10)); // nada do caso anterior chega ao disco depois de a pasta dele ser apagada
        string raiz = Pasta.Raiz;
        if (!Directory.Exists(raiz)) return;
        foreach (var f in Directory.EnumerateFiles(raiz, "*", SearchOption.AllDirectories))
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        foreach (var d in Directory.GetDirectories(raiz))
            try { Directory.Delete(d, true); } catch { }
    }

    static void Exigir(bool ok, string porque) { if (!ok) throw new Exception(porque); }

    static bool Esperar(Func<bool> cond, int ms, Action? tick = null)
    {
        var fim = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < fim)
        {
            tick?.Invoke();
            if (cond()) return true;
            Thread.Sleep(20);
        }
        tick?.Invoke();
        return cond();
    }

    public static SessaoLocal Sessao(int linhas, int eventos)
    {
        string id = $"20261116-{DateTime.Now:HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        string pasta = Path.Combine(Tmp, "logs");
        Directory.CreateDirectory(pasta);
        string log = Path.Combine(pasta, $"sessao-{id}.log");
        var sb = new StringBuilder();
        for (int i = 0; i < linhas; i++) sb.Append($"12:00:{i % 60:00}.000 linha {i} texto neutro para o teste de envio\n");
        File.WriteAllText(log, sb.ToString(), new UTF8Encoding(false));
        var s = SessaoLocal.Criar(id, log, naoEnviar: false);
        for (int e = 0; e < eventos; e++) s.Evento("teste", new JsonObject { ["n"] = e });
        Gravador.Descarregar(TimeSpan.FromSeconds(10)); // a gravação é assíncrona (Gravador): os testes que olham o disco esperam ela
        return s;
    }

    public static Envio Criar(ServidorFalso srv, SessaoLocal s) => Criar($"http://127.0.0.1:{srv.Porta}", ServidorFalso.TokenPadrao, s);

    /// <summary>Envio com a conta entrada: o token é o que o servidor aceita. Sem token (sem conta) nenhum arquivo sobe (spec 022).</summary>
    public static Envio Criar(string url, string tokenConta, SessaoLocal s) =>
        new(url, s, () => new Identidade(null, null, "teste", "instalacao-de-teste")) { Token = _ => tokenConta };

    static long Tam(string caminho)
    {
        using var f = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return f.Length;
    }

    static byte[] Ler(string caminho)
    {
        using var f = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var ms = new MemoryStream();
        f.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Todo arquivo da sessão confirmado até o fim e o índice de eventos em dia.</summary>
    static bool Converge(SessaoLocal s) =>
        s.Arquivos().All(a => s.Cursor(a.Nome) == Tam(a.Caminho)) && s.E.Indexados >= Tam(Path.Combine(s.Dir, SessaoLocal.Eventos));

    static string Estado(SessaoLocal s) =>
        string.Join(", ", s.Arquivos().Select(a => $"{a.Nome}: cursor {s.Cursor(a.Nome)} de {Tam(a.Caminho)}")) + $", indexados {s.E.Indexados}";

    /// <summary>O que o servidor remontou tem que ser byte a byte o arquivo do disco (não duplica nem falta).</summary>
    static void ExigirIgual(ServidorFalso srv, SessaoLocal s)
    {
        foreach (var (nome, caminho) in s.Arquivos())
        {
            byte[] disco = Ler(caminho), servidor = srv.Conteudo($"{s.Id}|{nome}");
            Exigir(disco.AsSpan().SequenceEqual(servidor), $"{nome}: o servidor remontou {servidor.Length} bytes diferentes dos {disco.Length} do disco");
            Exigir(s.Cursor(nome) == disco.Length, $"{nome}: cursor {s.Cursor(nome)} ≠ tamanho {disco.Length}");
        }
        Exigir(srv.Anomalias().Length == 0, "anomalias no servidor: " + string.Join("; ", srv.Anomalias()));
    }

    /// <summary>Verificador: a cada 25 ms confere que nenhum cursor passou do que o servidor tem gravado. Lê o cursor ANTES do servidor, então
    /// cursor &gt; servidor só acontece se o app andou sem confirmação.</summary>
    sealed class Invariante : IDisposable
    {
        readonly ServidorFalso _srv;
        readonly SessaoLocal[] _ss;
        readonly Thread _t;
        readonly List<string> _violacoes = new();
        volatile bool _parar;

        public Invariante(ServidorFalso srv, params SessaoLocal[] ss)
        {
            _srv = srv; _ss = ss;
            _t = new Thread(Laco) { IsBackground = true };
            _t.Start();
        }

        void Verificar()
        {
            foreach (var s in _ss)
                foreach (var (nome, _) in s.Arquivos())
                {
                    long c = s.Cursor(nome), t = _srv.Tamanho($"{s.Id}|{nome}");
                    if (c > t) lock (_violacoes) _violacoes.Add($"{nome}: cursor {c} > {t} gravados no servidor");
                }
        }

        void Laco() { while (!_parar) { try { Verificar(); } catch { } Thread.Sleep(25); } }
        public int Parar() { _parar = true; _t.Join(); Verificar(); lock (_violacoes) return _violacoes.Count; }
        public string Primeira() { lock (_violacoes) return _violacoes.FirstOrDefault() ?? ""; }
        public void Dispose() { _parar = true; }
    }

    // ---------- casos que devem passar ----------

    static void Simples(bool sabotar)
    {
        using var srv = new ServidorFalso { Mentir = sabotar };
        var s = Sessao(300, 40);
        using var envio = Criar(srv, s);
        using var inv = new Invariante(srv, s);
        envio.Ativo = true;
        envio.Iniciar(Array.Empty<SessaoLocal>());
        envio.Agora();
        if (sabotar)
        {
            Thread.Sleep(3000);
            Exigir(inv.Parar() == 0, "cursor andou sem confirmação: " + inv.Primeira());
        }
        Exigir(Esperar(() => Converge(s), 20_000), "não confirmou tudo: " + Estado(s));
        Exigir(Esperar(() => envio.Situacao().Estado == EstadoEnvio.Ok, 5000), "o indicador não ficou verde: " + envio.Situacao().Titulo);
        envio.Fechar(TimeSpan.FromSeconds(2));
        Exigir(Esperar(() => Converge(s), 5000), "depois de fechar ficou pendente: " + Estado(s));
        ExigirIgual(srv, s);
        Exigir(s.E.Fechou == "normal", "a sessão não ficou marcada como fechada");
        Exigir(srv.VeioEvento("sessao_fim"), "o servidor não recebeu o evento sessao_fim");
        Exigir(inv.Parar() == 0, "cursor andou sem confirmação: " + inv.Primeira());
    }

    static void RespostaPerdida()
    {
        using var srv = new ServidorFalso();
        srv.Fila.Enqueue(Modo.PerderResposta);
        var s = Sessao(400, 30);
        using var envio = Criar(srv, s);
        using var inv = new Invariante(srv, s);
        envio.Ativo = true;
        envio.Iniciar(Array.Empty<SessaoLocal>());
        envio.Agora();
        Exigir(Esperar(() => Converge(s), 30_000), "não recuperou depois da resposta perdida: " + Estado(s));
        envio.Fechar(TimeSpan.FromSeconds(2));
        Exigir(srv.GravouEPerdeu >= 1, "o servidor não chegou a gravar-e-calar (o caso não exercitou o que devia)");
        Exigir(srv.Pedidos >= 2, "esperava ao menos 2 pedidos, vieram " + srv.Pedidos);
        ExigirIgual(srv, s);
        Exigir(inv.Parar() == 0, "cursor andou sem confirmação: " + inv.Primeira());
    }

    static void ConfirmadoMenor()
    {
        using var srv = new ServidorFalso();
        srv.Fila.Enqueue(Modo.ConfirmadoMenor);
        var s = Sessao(500, 30);
        using var envio = Criar(srv, s);
        using var inv = new Invariante(srv, s);
        envio.Ativo = true;
        envio.Iniciar(Array.Empty<SessaoLocal>());
        envio.Agora();
        Exigir(Esperar(() => Converge(s), 30_000), "não convergiu: " + Estado(s));
        envio.Fechar(TimeSpan.FromSeconds(2));
        ExigirIgual(srv, s);
        Exigir(inv.Parar() == 0, "cursor andou sem confirmação: " + inv.Primeira());
    }

    /// <summary>O servidor falha <paramref name="vezes"/> vezes: o app tem que mostrar falha (vermelho) e depois voltar sozinho, sem perder nem duplicar.</summary>
    static void FalhaERecupera(Modo modo, int vezes, int limiteMs, string rotulo)
    {
        using var srv = new ServidorFalso();
        for (int i = 0; i < vezes; i++) srv.Fila.Enqueue(modo);
        var s = Sessao(300, 30);
        using var envio = Criar(srv, s);
        using var inv = new Invariante(srv, s);
        bool viuFalha = false;
        string? erro = null;
        envio.Ativo = true;
        envio.Iniciar(Array.Empty<SessaoLocal>());
        envio.Agora();
        Exigir(Esperar(() => Converge(s), limiteMs, () =>
        {
            if (envio.UltimoErro is string e && envio.Situacao().Estado == EstadoEnvio.Falha) { viuFalha = true; erro = e; }
        }), $"não recuperou ({rotulo}): " + Estado(s));
        Exigir(viuFalha, $"o indicador nunca ficou vermelho durante a falha ({rotulo})");
        Exigir(Esperar(() => envio.UltimoErro == null, 5000), "UltimoErro não limpou depois de recuperar: " + envio.UltimoErro);
        envio.Fechar(TimeSpan.FromSeconds(2));
        ExigirIgual(srv, s);
        Exigir(inv.Parar() == 0, "cursor andou sem confirmação: " + inv.Primeira());
        Console.WriteLine($"      ({rotulo}: falha vista = \"{erro}\")");
    }

    static void Desabilitado()
    {
        using var srv = new ServidorFalso();
        var s = Sessao(100, 10);
        using var envio = Criar(srv, s);
        envio.Habilitado = false;
        envio.Ativo = true;
        envio.Iniciar(Array.Empty<SessaoLocal>());
        envio.Agora();
        Thread.Sleep(3500);
        Exigir(envio.Situacao().Estado == EstadoEnvio.Neutro, "com o envio desligado o indicador devia ficar cinza");
        envio.Fechar(TimeSpan.FromSeconds(2));
        Exigir(srv.Tamanho($"{s.Id}|log.txt") == 0 && srv.ContarEventos("teste") == 0, "uma sessão completa subiu com o diagnóstico desligado");
        Exigir(s.Cursor(SessaoLocal.Log) == 0 && s.Cursor(SessaoLocal.Eventos) == 0, "cursor andou sem envio");
    }

    static void SessaoAnterior()
    {
        using var srv = new ServidorFalso();
        var velha = Sessao(150, 10);
        velha.E.Fechou = "normal"; // a abertura anterior fechou, mas o servidor não confirmou tudo
        velha.Salvar();
        var nova = Sessao(50, 5);
        var anteriores = Manutencao.Anteriores(nova);
        Exigir(anteriores.Count == 1 && anteriores[0].Id == velha.Id, $"Manutencao.Anteriores devolveu {anteriores.Count} sessão(ões) em vez da pendente");
        using var envio = Criar(srv, nova);
        using var inv = new Invariante(srv, nova, anteriores[0]);
        envio.Ativo = true;
        envio.Iniciar(anteriores);
        envio.Agora();
        Exigir(Esperar(() => Converge(nova) && Converge(anteriores[0]), 30_000), "não subiu tudo: " + Estado(anteriores[0]) + " | " + Estado(nova));
        envio.Fechar(TimeSpan.FromSeconds(2));
        ExigirIgual(srv, anteriores[0]);
        ExigirIgual(srv, nova);
        Exigir(anteriores[0].E.Concluida, "a sessão anterior não foi marcada como concluída depois de subir");
        Exigir(inv.Parar() == 0, "cursor andou sem confirmação: " + inv.Primeira());
    }

    /// <summary>Controle: o verificador de cursor tem que reprovar um servidor que confirma sem gravar (senão o teste "passaria" qualquer coisa).</summary>
    static void ControleVerificador()
    {
        using var srv = new ServidorFalso { Mentir = true };
        var s = Sessao(100, 10);
        using var envio = Criar(srv, s);
        using var inv = new Invariante(srv, s);
        envio.Ativo = true;
        envio.Iniciar(Array.Empty<SessaoLocal>());
        envio.Agora();
        Exigir(Esperar(() => srv.Pedidos >= 2, 10_000), "o app nem falou com o servidor");
        Thread.Sleep(500);
        envio.Fechar(TimeSpan.FromSeconds(1));
        Exigir(inv.Parar() > 0, "o verificador NÃO percebeu um servidor que confirma sem gravar: o teste não prova nada");
    }

    // ---------- recusados e cursor inconsistente (etapa 3) ----------

    static void RecusadosIsolaOArquivo()
    {
        var antes = Envio.EsperaRecusa;
        Envio.EsperaRecusa = TimeSpan.FromSeconds(3); // o padrão é 5 min
        try
        {
            using var srv = new ServidorFalso();
            srv.RecusarArquivo(SessaoLocal.Log);
            var s = Sessao(200, 20);
            using var envio = Criar(srv, s);
            using var inv = new Invariante(srv, s);
            envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>()); envio.Agora();
            string eventos = Path.Combine(s.Dir, SessaoLocal.Eventos);
            Exigir(Esperar(() => envio.Recusados.Count == 1 && s.Cursor(SessaoLocal.Eventos) == Tam(eventos) && s.E.Indexados >= Tam(eventos), 15_000),
                "o resto não subiu ou o log não foi isolado: " + Estado(s));
            Exigir(s.Cursor(SessaoLocal.Log) == 0, "o cursor do arquivo recusado andou");
            var sit = envio.Situacao();
            Exigir(sit.Estado == EstadoEnvio.Falha && sit.Detalhe.Contains(SessaoLocal.Log), "o indicador devia ficar vermelho dizendo qual arquivo: " + sit.Detalhe);
            int p0 = srv.Pedidos;
            Thread.Sleep(2000);
            Exigir(srv.Pedidos - p0 <= 2, $"o app repetiu {srv.Pedidos - p0} pedidos em 2 s com o arquivo recusado");
            srv.LiberarRecusas();
            Exigir(Esperar(() => Converge(s) && envio.Recusados.Count == 0, 30_000), "não voltou a enviar o arquivo depois que o servidor passou a aceitar: " + Estado(s));
            Exigir(Esperar(() => envio.Situacao().Estado == EstadoEnvio.Ok, 10_000), "o indicador não voltou ao verde: " + envio.Situacao().Detalhe);
            envio.Fechar(TimeSpan.FromSeconds(2));
            ExigirIgual(srv, s);
            Exigir(inv.Parar() == 0, "cursor andou sem confirmação: " + inv.Primeira());
        }
        finally { Envio.EsperaRecusa = antes; }
    }

    static void ConfirmadoMaior()
    {
        using var srv = new ServidorFalso();
        srv.Forcar[SessaoLocal.Log] = JsonValue.Create(999_999_999L); // o servidor jura ter muito mais bytes do que o arquivo tem
        var s = Sessao(200, 20);
        using var envio = Criar(srv, s);
        envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>()); envio.Agora();
        string eventos = Path.Combine(s.Dir, SessaoLocal.Eventos);
        Exigir(Esperar(() => envio.Recusados.Count == 1 && s.Cursor(SessaoLocal.Eventos) == Tam(eventos), 15_000), "não isolou o arquivo inconsistente: " + Estado(s));
        Exigir(s.Cursor(SessaoLocal.Log) == 0, $"o cursor virou {s.Cursor(SessaoLocal.Log)} (número do servidor maior que o arquivo)");
        Exigir(envio.Recusados[0].Motivo.Contains("inconsistente"), "motivo: " + envio.Recusados[0].Motivo);
        int p0 = srv.Pedidos;
        Thread.Sleep(2000);
        Exigir(srv.Pedidos - p0 <= 2, $"vai e volta: {srv.Pedidos - p0} pedidos em 2 s");
        Exigir(srv.Pedidos <= 8, $"{srv.Pedidos} pedidos no total (antes eram 20 por rodada)");
        Exigir(envio.Situacao().Estado == EstadoEnvio.Falha, "o indicador devia ficar vermelho");
        envio.Fechar(TimeSpan.FromSeconds(1));
    }

    static void ArquivoLocalMenor()
    {
        using var srv = new ServidorFalso();
        var s = Sessao(300, 10);
        using var envio = Criar(srv, s);
        envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>()); envio.Agora();
        Exigir(Esperar(() => Converge(s), 20_000), "não confirmou tudo: " + Estado(s));
        long n = s.Cursor(SessaoLocal.Log);
        string chave = $"{s.Id}|{SessaoLocal.Log}";
        using (var f = new FileStream(s.E.LogPath!, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) f.SetLength(n / 2); // o log foi trocado por um menor
        s.Evento("depois-do-truncamento"); Gravador.Descarregar(TimeSpan.FromSeconds(10));
        envio.Agora();
        string eventos = Path.Combine(s.Dir, SessaoLocal.Eventos);
        Exigir(Esperar(() => envio.Recusados.Any(x => x.Arquivo == SessaoLocal.Log) && s.E.Indexados >= Tam(eventos), 15_000), "não percebeu o arquivo menor: " + Estado(s));
        Exigir(s.Cursor(SessaoLocal.Log) == n, $"o cursor mudou de {n} para {s.Cursor(SessaoLocal.Log)}");
        Exigir(srv.Tamanho(chave) == n, "o servidor recebeu bytes do arquivo menor");
        int p0 = srv.Pedidos;
        Thread.Sleep(2500);
        Exigir(srv.Pedidos - p0 <= 2, $"vai e volta: {srv.Pedidos - p0} pedidos em 2,5 s");
        envio.Fechar(TimeSpan.FromSeconds(1));
    }

    /// <summary>O servidor responde algo que não pode virar cursor (negativo, texto) ou não responde sobre o arquivo: falha com espera, sem mexer no cursor.</summary>
    static void RespostaInvalida(JsonNode? valor, string rotulo)
    {
        using var srv = new ServidorFalso();
        srv.Forcar[SessaoLocal.Log] = valor;
        var s = Sessao(200, 20);
        using var envio = Criar(srv, s);
        using var inv = new Invariante(srv, s);
        envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>()); envio.Agora();
        Exigir(Esperar(() => envio.UltimoErro != null, 15_000), $"não virou falha ({rotulo})");
        Exigir(envio.UltimoErro!.Contains("sem sentido"), "mensagem: " + envio.UltimoErro);
        Exigir(s.Cursor(SessaoLocal.Log) == 0, $"o cursor virou {s.Cursor(SessaoLocal.Log)} ({rotulo})");
        Exigir(srv.Pedidos <= 3, $"{srv.Pedidos} pedidos até a falha (antes eram 20 por rodada)");
        srv.Forcar.Clear();
        Exigir(Esperar(() => Converge(s), 30_000), $"não recuperou ({rotulo}): " + Estado(s));
        envio.Fechar(TimeSpan.FromSeconds(2));
        ExigirIgual(srv, s);
        Exigir(inv.Parar() == 0, "cursor andou sem confirmação: " + inv.Primeira());
    }

    // ---------- perda local e teto (etapa 4) ----------

    static void PerdaLocal()
    {
        using var srv = new ServidorFalso();
        var s = Sessao(20, 3);
        using var envio = Criar(srv, s);
        string eventos = Path.Combine(s.Dir, SessaoLocal.Eventos), quadros = Path.Combine(s.Dir, "quadros.csv");
        s.Acrescentar("quadros.csv", "1,2"); Gravador.Descarregar(TimeSpan.FromSeconds(10)); // o arquivo precisa existir para virar somente leitura
        File.SetAttributes(eventos, FileAttributes.ReadOnly);
        File.SetAttributes(quadros, FileAttributes.ReadOnly);
        s.Evento("perdido-1"); s.Evento("perdido-2"); s.Evento("perdido-3");
        s.Acrescentar("quadros.csv", "3,4"); s.Acrescentar("quadros.csv", "5,6");
        Gravador.Descarregar(TimeSpan.FromSeconds(10)); // só então a gravação falha (e conta a perda); liberar o arquivo antes deixaria ela dar certo
        Exigir(s.PerdaEventos == 3 && s.PerdaLinhas == 2, $"contou {s.PerdaEventos} eventos e {s.PerdaLinhas} linhas perdidos (esperava 3 e 2)");
        Exigir(envio.Situacao().Estado == EstadoEnvio.Falha, "o indicador devia ficar vermelho com o disco falhando");
        // o disco AINDA falha e o aviso tem que subir assim mesmo (a rede funciona)
        envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>()); envio.Agora();
        Exigir(Esperar(() => srv.VeioEvento("perda_local"), 15_000), "o servidor não recebeu o evento perda_local");
        var ev = srv.UltimoEvento("perda_local")!;
        Exigir(ev["eventos_perdidos"]!.GetValue<long>() == 3 && ev["linhas_perdidas"]!.GetValue<long>() == 2, "totais errados: " + ev.ToJsonString());
        Exigir(ev["primeira_utc"] != null && ev["motivo"] != null, "faltou período ou motivo: " + ev.ToJsonString());
        Exigir(!ev.ToJsonString().Contains("Users"), "o aviso vazou caminho: " + ev.ToJsonString());
        File.SetAttributes(eventos, FileAttributes.Normal); File.SetAttributes(quadros, FileAttributes.Normal);
        s.Evento("gravado-depois");
        // passada a falha, o aviso não se repete a cada rodada
        // (o próprio envio também grava eventos enquanto o arquivo está travado, então pode ter subido mais de um aviso até aqui)
        envio.Agora(); Thread.Sleep(3500); envio.Agora(); Thread.Sleep(3500);
        int antes = srv.ContarEventos("perda_local");
        envio.Agora(); Thread.Sleep(3500); envio.Agora(); Thread.Sleep(3500);
        Exigir(srv.ContarEventos("perda_local") == antes, $"o aviso se repetiu sem perda nova: {antes} → {srv.ContarEventos("perda_local")}");
        // uma perda nova sobe de novo, com os totais acumulados
        long totalAntes = s.PerdaEventos;
        File.SetAttributes(eventos, FileAttributes.ReadOnly);
        s.Evento("perdido-4"); Gravador.Descarregar(TimeSpan.FromSeconds(10));
        File.SetAttributes(eventos, FileAttributes.Normal);
        envio.Agora();
        Exigir(Esperar(() => srv.ContarEventos("perda_local") > antes, 15_000), "a perda nova não subiu");
        Exigir(srv.UltimoEvento("perda_local")!["eventos_perdidos"]!.GetValue<long>() >= totalAntes + 1, "o total acumulado não cresceu");
        envio.Fechar(TimeSpan.FromSeconds(1));
    }

    static void ExigirBuildNoWorker(string url, string token, string pc, SessaoLocal s, string versao, string commit)
    {
        using var http = new HttpClient();
        foreach (var rota in new[] { $"/telemetria/arquivos?pc={pc}&sessao={s.Id}", $"/telemetria/sessoes?pc={pc}&desde=0" })
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url + rota);
            req.Headers.Add("x-streemu-token", token);
            var json = JsonNode.Parse(http.Send(req).Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
            JsonNode? linha = rota.Contains("arquivos?") ? json.AsArray().FirstOrDefault()
                : json.AsArray().SelectMany(x => x!["sessoes"]!.AsArray()).FirstOrDefault(x => x!["sessao"]!.GetValue<string>() == s.Id);
            string? c = linha?[rota.Contains("arquivos?") ? "commit" : "commit_git"]?.GetValue<string>(), v = linha?["versao"]?.GetValue<string>();
            Exigir(c == commit && v == versao, $"{rota.Split('?')[0]}: versão/commit = {v}/{c}, esperava {versao}/{commit}");
        }
    }

    static SessaoLocal Fabricar(string rotulo, double horasAtras, int bytes, string estado)
    {
        string id = $"20261116-{rotulo}-{Guid.NewGuid().ToString("N")[..8]}";
        var s = SessaoLocal.Criar(id, null, naoEnviar: false);
        File.WriteAllBytes(Path.Combine(s.Dir, "dados.csv"), new byte[bytes]);
        s.E.Inicio = DateTimeOffset.UtcNow.AddHours(-horasAtras).ToUnixTimeMilliseconds();
        if (estado == "concluida") { s.E.Concluida = true; s.E.Fechou = "normal"; }
        else if (estado == "pendente") s.E.Fechou = "normal"; // fechou, mas o servidor não confirmou tudo
        // "aberta": nada (o dono é este processo, vivo)
        s.Salvar();
        return s;
    }

    static bool Existe(SessaoLocal s) => Directory.Exists(s.Dir);

    static void TetoDescarte()
    {
        Exigir(Manutencao.TetoBytes == 3L * 1024 * 1024 * 1024, "o teto devia ser 3 GB");
        var s1 = Fabricar("a1", 10, 60_000, "concluida");
        var p1 = Fabricar("a2", 5, 60_000, "pendente");
        var p2 = Fabricar("a3", 2, 60_000, "pendente");
        var p3 = Fabricar("a4", 20, 60_000, "aberta");
        var atual = Fabricar("a5", 0, 10_000, "aberta");
        var descartadas = new List<string>();
        Manutencao.Limpar(null, atual, tetoBytes: 150_000, aoDescartar: descartadas.Add);
        Gravador.Descarregar(TimeSpan.FromSeconds(10));
        Exigir(!Existe(s1), "a sessão já enviada devia sair primeiro");
        Exigir(!Existe(p1), "a pendente mais antiga devia sair quando ainda passava do teto");
        Exigir(Existe(p2), "a pendente mais nova devia ficar (já coube no teto)");
        Exigir(Existe(p3), "a sessão aberta por outra instância viva nunca sai");
        Exigir(Existe(atual), "a sessão atual nunca sai");
        Exigir(descartadas.SequenceEqual(new[] { p1.Id }), "aoDescartar devia avisar só a " + p1.Id + " e avisou: " + string.Join(",", descartadas));
        var linhas = File.ReadAllLines(Path.Combine(atual.Dir, SessaoLocal.Eventos)).Where(l => l.Contains("sessao_descartada")).ToArray();
        Exigir(linhas.Length == 1 && linhas[0].Contains(p1.Id) && linhas[0].Contains("teto_local"), "devia haver 1 evento sessao_descartada da " + p1.Id + ": " + string.Join(" | ", linhas));
        long semEnviar = JsonNode.Parse(linhas[0])!["bytes_sem_enviar"]!.GetValue<long>();
        Exigir(semEnviar >= 60_000, $"bytes_sem_enviar = {semEnviar} (esperava ao menos os 60000 do dados.csv)");
    }

    static void TetoNaoApagaQuandoCabe()
    {
        var s1 = Fabricar("b1", 10, 60_000, "concluida"); // enviada há 10 h: dentro dos 3 dias
        var p1 = Fabricar("b2", 5, 60_000, "pendente");
        var atual = Fabricar("b3", 0, 10_000, "aberta");
        var descartadas = new List<string>();
        Manutencao.Limpar(null, atual, aoDescartar: descartadas.Add);
        Gravador.Descarregar(TimeSpan.FromSeconds(10));
        Exigir(Existe(s1) && Existe(p1) && Existe(atual) && descartadas.Count == 0, "apagou algo com a pasta bem abaixo do teto");
        Exigir(!File.Exists(Path.Combine(atual.Dir, SessaoLocal.Eventos)) || !File.ReadAllText(Path.Combine(atual.Dir, SessaoLocal.Eventos)).Contains("sessao_descartada"), "registrou descarte sem descartar");
    }

    static void TetoNuncaApagaAAtual()
    {
        var atual = Fabricar("c1", 0, 200_000, "aberta");
        var descartadas = new List<string>();
        Manutencao.Limpar(null, atual, tetoBytes: 100_000, aoDescartar: descartadas.Add);
        Exigir(Existe(atual) && descartadas.Count == 0, "a sessão atual foi apagada ou descartada");
    }

    // ---------- cobertura pela hora do conteúdo (etapa 5) ----------

    static long LocalParaUtcMs(DateTime local) => new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(local)).ToUnixTimeMilliseconds();
    static string IsoMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    static void CoberturaHoraDoConteudo()
    {
        // o backlog de um PC que ficou 3 h sem internet: o log é de 3 h atrás e sobe agora. A cobertura tem que dizer "até 3 h atrás", não "até agora".
        using var srv = new ServidorFalso();
        var s = Sessao(0, 1);
        var inicio = DateTime.Now.AddHours(-3).AddSeconds(-1);
        s.E.Inicio = LocalParaUtcMs(inicio); s.Salvar();
        var sb = new StringBuilder();
        for (int i = 0; i < 20; i++) sb.Append($"{inicio.AddSeconds(1 + i):HH:mm:ss.fff} linha {i}\n");
        File.WriteAllText(s.E.LogPath!, sb.ToString(), new UTF8Encoding(false));
        using var envio = Criar(srv, s);
        envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>()); envio.Agora();
        Exigir(Esperar(() => Converge(s), 20_000), "não convergiu: " + Estado(s));
        var h = srv.Horas(SessaoLocal.Log);
        Exigir(h.Count >= 1 && h[0].H1 != null && h[0].H0 != null, "o servidor não recebeu h0/h1 do log");
        string esperadoFim = IsoMs(LocalParaUtcMs(inicio.AddSeconds(20))), esperadoIni = IsoMs(LocalParaUtcMs(inicio.AddSeconds(1)));
        Exigir(h[^1].H1 == esperadoFim, $"h1 do log = {h[^1].H1} (esperava {esperadoFim}, a hora da última linha, não a do envio)");
        Exigir(h[0].H0 == esperadoIni, $"h0 do log = {h[0].H0} (esperava {esperadoIni})");
        var agora = DateTimeOffset.UtcNow;
        Exigir(agora - DateTimeOffset.Parse(h[^1].H1!) > TimeSpan.FromHours(2.5), "h1 está perto de agora: virou a hora do envio");
        // eventos.ndjson: a hora do último evento
        var he = srv.Horas(SessaoLocal.Eventos);
        Gravador.Descarregar(TimeSpan.FromSeconds(10));
        // o h1 do pedaço de eventos é a hora de um evento que está no arquivo (o do fim do pedaço; o envio ainda grava eventos depois, como o relógio)
        var horasEv = File.ReadAllLines(Path.Combine(s.Dir, SessaoLocal.Eventos)).Where(l => l.Length > 2).Select(l => JsonNode.Parse(l)!["hora"]!.GetValue<long>()).ToHashSet();
        Exigir(he.Count == 0 || horasEv.Contains(DateTimeOffset.Parse(he[^1].H1!).ToUnixTimeMilliseconds()), $"h1 dos eventos = {(he.Count == 0 ? "-" : he[^1].H1)} não é a hora de nenhum evento do arquivo");
        // a âncora guardada é a hora do conteúdo confirmado
        Exigir(s.HoraConteudo(SessaoLocal.Log) == LocalParaUtcMs(inicio.AddSeconds(20)), "a âncora do log não é a hora da última linha confirmada");
        envio.Fechar(TimeSpan.FromSeconds(1));
    }

    static void CoberturaViradaDoDia()
    {
        var hoje = DateTime.Today;
        // virou o dia no meio do pedaço: 23:59:58 e 00:00:02 do dia seguinte
        var (ini, fim) = HoraDoConteudo.De(SessaoLocal.Log, "23:59:58.000 a\n00:00:02.500 b\n", LocalParaUtcMs(hoje.AddHours(23).AddMinutes(59)), doZero: false);
        Exigir(ini == LocalParaUtcMs(hoje.AddHours(23).AddMinutes(59).AddSeconds(58)), "ini errado na virada");
        Exigir(fim == LocalParaUtcMs(hoje.AddDays(1).AddSeconds(2.5)), $"fim errado na virada: {(fim is long f ? IsoMs(f) : "nulo")}");
        // o log começou às 23:59:59 e a sessão foi criada às 00:00:01 do dia seguinte: a primeira linha é do dia anterior
        (ini, fim) = HoraDoConteudo.De(SessaoLocal.Log, "\uFEFF23:59:59.100 a\n00:00:01.000 b\n", LocalParaUtcMs(hoje.AddSeconds(1)), doZero: true);
        Exigir(ini == LocalParaUtcMs(hoje.AddSeconds(-0.9)), $"ini devia ser do dia anterior: {(ini is long a ? IsoMs(a) : "nulo")}");
        Exigir(fim == LocalParaUtcMs(hoje.AddSeconds(1)), "fim devia ser 00:00:01 do dia da sessão");
        // sem linha com hora (ou linha quebrada) = nulo, nunca a hora de agora
        (ini, fim) = HoraDoConteudo.De(SessaoLocal.Log, "texto sem hora\n25:61:61.000 x\n", LocalParaUtcMs(hoje), doZero: false);
        Exigir(ini == null && fim == null, "linha sem hora virou hora");
    }

    static void CoberturaSemHoraEAncora()
    {
        using var srv = new ServidorFalso();
        var s = Sessao(5, 2);
        s.Acrescentar("quadros.csv", "1,2\n3,4");
        using (var envio = Criar(srv, s)) { envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>()); envio.Agora(); Exigir(Esperar(() => Converge(s), 20_000), "não convergiu: " + Estado(s)); envio.Fechar(TimeSpan.FromSeconds(1)); }
        var q = srv.Horas("quadros.csv");
        Exigir(q.Count >= 1 && q.All(x => x.H0 == null && x.H1 == null), "arquivo sem hora no conteúdo devia ir com h0/h1 nulos");
        // a âncora vai para o sessao.json e volta na próxima abertura
        long ancora = s.HoraConteudo(SessaoLocal.Log);
        var reaberta = SessaoLocal.Abrir(s.Dir)!;
        Exigir(reaberta.HoraConteudo(SessaoLocal.Log) == ancora && ancora != s.E.Inicio, "a âncora não sobreviveu ao sessao.json");
    }

    // ---------- gravação fora das threads de quem chama e custo (etapa 8) ----------

    static string[] LinhasEventos(SessaoLocal s) => File.ReadAllLines(Path.Combine(s.Dir, SessaoLocal.Eventos)).Where(l => l.Length > 2).ToArray();

    static void GravacaoNaoEspera()
    {
        var s = Sessao(0, 0);
        string arq = Path.Combine(s.Dir, SessaoLocal.Eventos);
        long antes = File.Exists(arq) ? Tam(arq) : 0;
        Gravador.PausarParaTeste = true; // a thread de disco fica parada: só a chamada, sem disco
        var cron = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 2000; i++) s.Evento("fila", new JsonObject { ["n"] = i });
        cron.Stop();
        long durante = File.Exists(arq) ? Tam(arq) : 0;
        Exigir(durante == antes, $"o arquivo cresceu ({antes} → {durante}) com a gravação parada: a chamada gravou no disco");
        Exigir(Gravador.Pendentes == 2000, $"pendentes = {Gravador.Pendentes}");
        Exigir(cron.ElapsedMilliseconds < 1500, $"2000 chamadas levaram {cron.ElapsedMilliseconds} ms sem disco");
        Gravador.PausarParaTeste = false;
        Exigir(Gravador.Descarregar(TimeSpan.FromSeconds(20)), "a fila não esvaziou");
        var linhas = LinhasEventos(s).Where(l => l.Contains("\"fila\"")).Select(l => JsonNode.Parse(l)!["n"]!.GetValue<int>()).ToArray();
        Exigir(linhas.Length == 2000 && linhas.SequenceEqual(Enumerable.Range(0, 2000)), $"chegaram {linhas.Length} linhas, fora de ordem ou faltando");
        Exigir(s.PerdaEventos == 0 && s.FalhaLocal == null, "contou perda sem ter falhado");
    }

    static void GravacaoConcorrente()
    {
        var s = Sessao(0, 0);
        var ths = Enumerable.Range(0, 4).Select(k => new Thread(() =>
        {
            for (int i = 0; i < 500; i++) { s.Evento("conc", new JsonObject { ["k"] = k, ["n"] = i }); if (i % 100 == 0) Thread.Yield(); }
            s.Acrescentar("conc.csv", $"{k},fim");
        })).ToList();
        ths.ForEach(x => x.Start()); ths.ForEach(x => x.Join());
        Exigir(Gravador.Descarregar(TimeSpan.FromSeconds(20)), "a fila não esvaziou");
        var ev = LinhasEventos(s).Select(l => JsonNode.Parse(l)!).Where(o => o["tipo"]!.GetValue<string>() == "conc").ToList();
        Exigir(ev.Count == 2000, $"chegaram {ev.Count} de 2000 eventos");
        for (int k = 0; k < 4; k++)
        {
            var ns = ev.Where(o => o["k"]!.GetValue<int>() == k).Select(o => o["n"]!.GetValue<int>()).ToArray();
            Exigir(ns.SequenceEqual(Enumerable.Range(0, 500)), $"a thread {k} perdeu ordem ou evento");
        }
        Exigir(File.ReadAllLines(Path.Combine(s.Dir, "conc.csv")).Length == 4, "faltou linha do conc.csv");
        Exigir(s.PerdaEventos == 0 && s.PerdaLinhas == 0, "contou perda sem ter falhado");
    }

    static void GravacaoFilaCheia()
    {
        var s = Sessao(0, 0);
        Gravador.PausarParaTeste = true; Gravador.MaxItens = 50;
        var cron = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 80; i++) s.Evento("cheia", new JsonObject { ["n"] = i });
        cron.Stop();
        Exigir(cron.ElapsedMilliseconds < 1000, $"fila cheia bloqueou a chamada ({cron.ElapsedMilliseconds} ms)");
        Exigir(Gravador.Pendentes == 50, $"pendentes = {Gravador.Pendentes} (esperava 50)");
        Exigir(s.PerdaEventos == 30, $"perda = {s.PerdaEventos} (esperava 30)");
        var info = s.PerdaParaInformar(out _);
        Exigir(info != null && info["motivo"]!.GetValue<string>() == "FilaCheiaException" && info["eventos_perdidos"]!.GetValue<long>() == 30, "o aviso perda_local não diz fila cheia: " + info?.ToJsonString());
        Gravador.PausarParaTeste = false;
        Exigir(Gravador.Descarregar(TimeSpan.FromSeconds(20)), "a fila não esvaziou");
        var ns = LinhasEventos(s).Where(l => l.Contains("\"cheia\"")).Select(l => JsonNode.Parse(l)!["n"]!.GetValue<int>()).ToArray();
        Exigir(ns.SequenceEqual(Enumerable.Range(0, 50)), $"os 50 primeiros deviam chegar em ordem: {ns.Length} linhas");
    }

    static void CustoSobe()
    {
        using var srv = new ServidorFalso();
        var s = Sessao(30, 5);
        using var envio = Criar(srv, s);
        envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>()); envio.Agora();
        Exigir(Esperar(() => Converge(s), 20_000), "não convergiu: " + Estado(s));
        envio.RegistrarCusto(); envio.Agora();
        Exigir(Esperar(() => srv.VeioEvento("telemetria_custo"), 15_000), "o servidor não recebeu telemetria_custo");
        var ev = srv.UltimoEvento("telemetria_custo")!;
        Exigir(ev["gravacao"]!["itens"]!.GetValue<long>() >= 5, "gravacao.itens devia contar os eventos gravados: " + ev.ToJsonString());
        Exigir(ev["envio"]!["rodadas"]!.GetValue<long>() >= 1 && ev["envio"]!["bytes_enviados"]!.GetValue<long>() > 0, "envio sem rodada ou bytes: " + ev.ToJsonString());
        double ocup = ev["ocupada_pct"]!.GetValue<double>();
        Exigir(ocup >= 0 && ocup < 50, $"ocupada_pct = {ocup}");
        Exigir(ev["app"]!["cpu_ms"]!.GetValue<double>() >= 0 && ev["app"]!["alocado_mb"]!.GetValue<double>() >= 0, "app sem cpu/alocado");
        // só números: nada de texto que possa carregar caminho, nome ou conteúdo
        void SoNumeros(JsonNode? n, string onde)
        {
            if (n is JsonObject o) { foreach (var (k, v) in o) { if (k is "tipo" or "eid" or "hora_utc" or "sessao") continue; SoNumeros(v, onde + "." + k); } }
            else if (n is JsonValue v && v.TryGetValue<string>(out var st)) throw new Exception($"texto em {onde}: {st}");
        }
        SoNumeros(ev, "telemetria_custo");
        envio.Fechar(TimeSpan.FromSeconds(1));
    }

    static void FecharDescarrega()
    {
        using var srv = new ServidorFalso();
        var s = Sessao(5, 1);
        using var envio = Criar(srv, s);
        envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>());
        for (int i = 0; i < 25; i++) s.Evento("ultimos", new JsonObject { ["n"] = i });
        envio.Fechar(TimeSpan.FromSeconds(2));
        var linhas = LinhasEventos(s);
        Exigir(linhas.Count(l => l.Contains("\"ultimos\"")) == 25, "os eventos da fila não chegaram ao disco antes de fechar");
        Exigir(linhas.Any(l => l.Contains("sessao_fim")) && linhas.Any(l => l.Contains("telemetria_custo")), "faltou sessao_fim ou telemetria_custo ao fechar");
        Exigir(Gravador.Pendentes == 0, "a fila não esvaziou ao fechar");
    }

    static void EventoUrgente()
    {
        var s = Sessao(0, 0);
        string arq = Path.Combine(s.Dir, SessaoLocal.Eventos);
        long Lat(string tipo)
        {
            long antes = File.Exists(arq) ? Tam(arq) : 0;
            var c = System.Diagnostics.Stopwatch.StartNew();
            s.Evento(tipo, new JsonObject { ["x"] = 1 });
            while (c.ElapsedMilliseconds < 2000 && (File.Exists(arq) ? Tam(arq) : 0) == antes) Thread.Sleep(1);
            long ms = c.ElapsedMilliseconds;
            Gravador.Descarregar(TimeSpan.FromSeconds(5));
            return ms;
        }
        long urg = long.MaxValue, norm = long.MaxValue;
        for (int i = 0; i < 5; i++) { urg = Math.Min(urg, Lat("marcacao")); norm = Math.Min(norm, Lat("comum")); }
        Exigir(urg < 100, $"evento urgente levou {urg} ms para chegar ao disco");
        Exigir(norm >= 100, $"evento comum levou {norm} ms: o agrupamento sumiu");
        Exigir(SessaoLocal.EventoUrgente("falha") && SessaoLocal.EventoUrgente("sessao_fim") && !SessaoLocal.EventoUrgente("relogio"), "lista de urgentes errada");
    }

    // ---------- eventos do sistema (etapa 9) ----------

    static Vigia NovaVigia(SessaoLocal s) => new(s, () => new JsonArray(), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(700), TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(50), sistema: false);

    static void VigiaTravada()
    {
        var s = Sessao(0, 0);
        using var v = NovaVigia(s);
        v.Batimento();
        Thread.Sleep(1200); // a interface "trava": a thread do vigia vê sozinha
        Gravador.Descarregar(TimeSpan.FromSeconds(5));
        Exigir(LinhasEventos(s).Count(l => l.Contains("\"ui_travada\"") && l.Contains("\"em_curso\":true")) == 1, "faltou um (e só um) aviso em curso: " + string.Join(" | ", LinhasEventos(s)));
        v.Batimento(); // voltou
        Gravador.Descarregar(TimeSpan.FromSeconds(5));
        var fim = LinhasEventos(s).Select(l => JsonNode.Parse(l)!).Where(o => o["tipo"]!.GetValue<string>() == "ui_travada" && !o["em_curso"]!.GetValue<bool>()).ToList();
        Exigir(fim.Count == 1 && fim[0]["duracao_s"]!.GetValue<double>() >= 1.0, "faltou o aviso de volta com a duração: " + string.Join(" | ", LinhasEventos(s)));
        Exigir(fim[0]["lives"] is JsonArray, "faltou o contexto das lives");
    }

    static void VigiaSemTrava()
    {
        var s = Sessao(0, 0);
        using var v = NovaVigia(s);
        for (int i = 0; i < 30; i++) { v.Batimento(); Thread.Sleep(50); }
        Gravador.Descarregar(TimeSpan.FromSeconds(5));
        Exigir(v.Avisos == 0 && !(File.Exists(Path.Combine(s.Dir, SessaoLocal.Eventos)) && LinhasEventos(s).Any(l => l.Contains("ui_travada"))), "avisou trava com a interface respondendo");
    }

    static void VigiaSuspensao()
    {
        var s = Sessao(0, 0);
        using var v = NovaVigia(s);
        v.Batimento();
        v.Suspendeu();
        Thread.Sleep(900); // dormiu: nem em curso nem trava
        v.Voltou();
        Thread.Sleep(250); // dentro da graça (400 ms) o relógio ainda não cobra
        Gravador.Descarregar(TimeSpan.FromSeconds(5));
        var ev = LinhasEventos(s).Select(l => JsonNode.Parse(l)!).ToList();
        Exigir(v.Avisos == 0 && !ev.Any(o => o["tipo"]!.GetValue<string>() == "ui_travada"), "suspensão virou ui_travada");
        var sus = ev.Where(o => o["tipo"]!.GetValue<string>() == "suspensao").ToList();
        Exigir(sus.Count == 2 && sus[0]["fase"]!.GetValue<string>() == "suspende" && sus[1]["fase"]!.GetValue<string>() == "volta" && sus[1]["duracao_s"]!.GetValue<double>() >= 0.8, "eventos de suspensão errados: " + string.Join(" | ", sus.Select(o => o.ToJsonString())));
        Exigir(Sessao_Urgentes(), "suspensao e ui_travada deviam ser urgentes");
    }

    static bool Sessao_Urgentes() => SessaoLocal.EventoUrgente("suspensao") && SessaoLocal.EventoUrgente("ui_travada");

    // ---------- rede por ligação (etapa 10) ----------

    static LeituraRede Leit(string estado, long tx, long rx, long rec, double perda, bool assisto = true, string caminho = "direto", double rtt = 25) => new(estado, caminho, rtt, tx, rx, rec, perda, assisto);
    static IEnumerable<(string, string, string, string, LeituraRede)> Uma(string id, LeituraRede l, string papel = "assiste") { yield return (id, "live1", "Ana", papel, l); }

    static JsonNode[] LinhasRede(SessaoLocal s)
    {
        Gravador.Descarregar(TimeSpan.FromSeconds(5));
        string arq = Path.Combine(s.Dir, SerieRede.Arquivo);
        return File.Exists(arq) ? File.ReadAllLines(arq).Where(l => l.Length > 2).Select(l => JsonNode.Parse(l)!).ToArray() : Array.Empty<JsonNode>();
    }

    static void RedePorLigacao()
    {
        var s = Sessao(0, 0);
        var r = new SerieRede(s);
        // linha de base (conectada): 1000 recebidos, 0% perda
        r.Amostrar(0, () => Uma("L1", Leit("Connected", 0, 0, 1000, 0)));
        // 5 s depois: +500 recebidos e perda total 1000 -> ~9%: de 1500 recebidos, perdidos = 1500*p/(100-p); escolho p = 100*100/1600 = 6.25 (100 perdidos)
        r.Amostrar(5000, () => Uma("L1", Leit("Connected", 625_000, 1_250_000, 1500, 100.0 * 100 / 1600)));
        var l = LinhasRede(s);
        Exigir(l.Length >= 1, "nenhuma linha de rede");
        var u = l[^1];
        Exigir(u["enviado_kbps"]!.GetValue<double>() == 1000 && u["recebido_kbps"]!.GetValue<double>() == 2000, "taxas: " + u.ToJsonString());
        Exigir(u["pacotes_recebidos"]!.GetValue<long>() == 500 && u["pacotes_perdidos"]!.GetValue<long>() == 100, "pacotes: " + u.ToJsonString());
        Exigir(Math.Abs(u["perda_pct"]!.GetValue<double>() - 16.67) < 0.05, "perda do intervalo: " + u.ToJsonString());
        Exigir(u["caminho"]!.GetValue<string>() == "direto" && u["amigo"]!.GetValue<string>() == "Ana" && u["transmissao"]!.GetValue<string>() == "live1", "identificação: " + u.ToJsonString());
        // quem transmite não tem perda para informar
        r.Amostrar(10_000, () => Uma("L2", Leit("Connected", 0, 0, 0, 0, assisto: false), "transmite"));
        r.Amostrar(15_000, () => Uma("L2", Leit("Connected", 1_250_000, 0, 0, 0, assisto: false, caminho: "meu_relay"), "transmite"));
        var t2 = LinhasRede(s).Last(x => x["ligacao"]!.GetValue<string>() == "L2");
        Exigir(t2["perda_pct"] == null && t2["caminho"]!.GetValue<string>() == "meu_relay" && t2["enviado_kbps"]!.GetValue<double>() == 2000, "transmissor: " + t2.ToJsonString());
        // só números e nomes de caminho: nada que pareça endereço
        Exigir(!l.Any(x => System.Text.RegularExpressions.Regex.IsMatch(x.ToJsonString(), @"\d+\.\d+\.\d+\.\d+")), "IP na série de rede");
    }

    static void RedeSoConectada()
    {
        var s = Sessao(0, 0);
        var r = new SerieRede(s);
        Exigir(r.Amostrar(0, () => Uma("L1", Leit("Punching", 0, 0, 0, 0))) == 0, "amostrou ligação que não conectou");
        Exigir(r.Amostrar(1000, () => Uma("L1", Leit("Connected", 0, 0, 0, 0))) == 0, "amostrou antes do intervalo");
        Exigir(r.Amostrar(5000, () => Uma("L1", Leit("Connected", 100, 100, 10, 0))) == 1, "devia amostrar");
        Exigir(r.Amostrar(10_000, () => Array.Empty<(string, string, string, string, LeituraRede)>()) == 0 && r.Ligacoes == 0, "ligação que acabou ficou na memória");
    }

    // ---------- relatório em falha grave (etapa 11) ----------

    static void FalhaGraveClassifica()
    {
        Exigir(Gravidade.Classificar("12:00:00.000 ERRO (fatal): System.Exception: x") == "fatal", "fatal");
        Exigir(Gravidade.Classificar("12:00:00.000 ERRO (UI): System.Exception: x") == "ui", "ui");
        Exigir(Gravidade.Classificar("12:00:00.000 a captura TRAVOU: o jogo manda 60/s") == "captura_travou", "captura travou");
        Exigir(Gravidade.Classificar("12:00:00.000 Reabrindo a captura (x).") == "captura_reaberta", "reabrindo");
        Exigir(Gravidade.Classificar("12:00:00.000 o player caiu para a CPU") == "caiu_para_cpu", "cpu");
        Exigir(Gravidade.Classificar("12:00:00.000 [aviso] o amigo caiu") == null, "aviso comum não é grave");
        Exigir(Gravidade.Classificar("12:00:00.000 Não consegui abrir") == null, "falha comum não é grave");
    }

    static void FalhaGraveFreio()
    {
        var f = new Gravidade.Freio { EntreMesmoTipo = TimeSpan.FromMinutes(2), EntreQuaisquer = TimeSpan.FromSeconds(20), MaximoPorSessao = 4 };
        Exigir(f.Permitir("captura_travou", 0), "o primeiro devia sair");
        Exigir(!f.Permitir("captura_travou", 5_000), "rajada do mesmo tipo passou");
        Exigir(!f.Permitir("caiu_para_cpu", 10_000), "outro tipo logo depois passou (intervalo geral)");
        Exigir(f.Permitir("caiu_para_cpu", 25_000), "outro tipo depois do intervalo geral devia sair");
        Exigir(!f.Permitir("captura_travou", 100_000), "mesmo tipo antes de 2 min passou");
        Exigir(f.Permitir("captura_travou", 130_000), "mesmo tipo depois de 2 min devia sair");
        Exigir(f.Permitir("ui", 200_000), "o quarto devia sair");
        Exigir(!f.Permitir("captura_reaberta", 400_000), "passou do teto por sessão");
        Exigir(f.Permitir("fatal", 400_001) && f.Permitir("fatal", 400_002), "o fatal nunca é freado");
    }

    // ---------- coleta reduzida no opt-out (etapa 12) ----------

    static SessaoLocal SessaoReduzida(string logTexto = "12:00:00.000 linha neutra\n")
    {
        string id = $"20261116-{DateTime.Now:HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        string log = Path.Combine(Tmp, "logs", $"sessao-{id}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, logTexto, new UTF8Encoding(false));
        return SessaoLocal.Criar(id, log, naoEnviar: false, reduzida: true);
    }

    static void AnonimizadorTroca()
    {
        Anonimizador.Reiniciar();
        Anonimizador.Registrar("Fulano"); Anonimizador.Registrar("Beltrano"); Anonimizador.Registrar("ab"); Anonimizador.Registrar("convidado");
        string r = Anonimizador.Aplicar("Ligação com Fulano conectada; fulano e BELTRANO; Fulanoide e abc ficam; ab idem");
        Exigir(r == "Ligação com pessoa-1 conectada; pessoa-1 e pessoa-2; Fulanoide e abc ficam; ab idem", "troca de nomes: " + r);
        var ev = new JsonObject { ["tipo"] = "x", ["discord_id"] = "1", ["apelido"] = "Fulano", ["outro_install"] = "z", ["de"] = "Beltrano", ["n"] = 3 };
        var o = Anonimizador.Evento(ev)!;
        Exigir(o["discord_id"] == null && o["apelido"] == null && o["outro_install"] == null && o["de"]!.GetValue<string>() == "pessoa-2" && o["n"]!.GetValue<int>() == 3, "evento anônimo: " + o.ToJsonString());
        Exigir(Anonimizador.NaoGrava("marcacao_nota") && !Anonimizador.NaoGrava("marcacao"), "texto livre");
        Anonimizador.Reiniciar();
    }

    static void ReduzidaGrava()
    {
        var s = SessaoReduzida();
        s.Evento("sessao_inicio", new JsonObject { ["versao"] = "9.9.9" });
        s.Evento("marcacao", new JsonObject { ["lives"] = new JsonArray() });
        s.Evento("marcacao_nota", new JsonObject { ["nota"] = "texto livre da pessoa" });
        s.Acrescentar("rede.ndjson", "{\"n\":1}");
        Gravador.Descarregar(TimeSpan.FromSeconds(5));
        var tipos = LinhasEventos(s).Select(l => JsonNode.Parse(l)!["tipo"]!.GetValue<string>()).ToArray();
        Exigir(tipos.SequenceEqual(new[] { "sessao_inicio", "marcacao" }), "tipos gravados: " + string.Join(",", tipos));
        var arquivos = s.Arquivos().Select(a => a.Nome).ToArray();
        Exigir(arquivos.Contains(SessaoLocal.Log) && arquivos.Contains("rede.ndjson"), "log e séries continuam no modo anônimo: " + string.Join(",", arquivos));
    }

    static Envio EnvioComConta(ServidorFalso srv, SessaoLocal s, Func<bool, string?>? token, Func<string?>? bilhete, bool anonimo = false) =>
        new($"http://127.0.0.1:{srv.Porta}", s, () => new Identidade(null, null, "teste", "instalacao-de-teste", "c-abcdefghijkl"), "id-anonimo-de-teste")
        { Token = token, Bilhete = bilhete, Habilitado = !anonimo };

    static void ContaTokenNormal()
    {
        using var srv = new ServidorFalso();
        var s = Sessao(5, 1);
        using var envio = EnvioComConta(srv, s, _ => "tok-ok", () => "st1.1.bilhete-ok");
        envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>()); envio.Agora();
        Exigir(Esperar(() => srv.Credenciais().Length > 0 && srv.Cabecalhos().Length > 0, 20_000), "nada chegou ao servidor");
        envio.Fechar(TimeSpan.FromSeconds(2));
        Exigir(srv.Credenciais().All(c => c == "token:tok-ok"), "o envio normal devia usar o token: " + string.Join(",", srv.Credenciais()));
        Exigir(srv.Cabecalhos().All(c => c["conta"]?.GetValue<string>() == "c-abcdefghijkl"), "o pacote devia levar a conta");
    }

    static void ContaBilheteAnonimo()
    {
        using var srv = new ServidorFalso();
        var s = SessaoReduzida("12:00:00.000 linha qualquer\n");
        Gravador.Descarregar(TimeSpan.FromSeconds(5));
        Anonimizador.MarcarPronto();
        using var envio = EnvioComConta(srv, s, _ => "tok-ok", () => ServidorFalso.BilheteAceito, anonimo: true);
        envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>()); envio.Agora();
        Exigir(Esperar(() => srv.Credenciais().Length > 0, 20_000), "nada chegou ao servidor");
        envio.Fechar(TimeSpan.FromSeconds(2));
        Exigir(srv.Credenciais().All(c => c.StartsWith("bilhete:")), "o anônimo devia usar só o bilhete: " + string.Join(",", srv.Credenciais()));
        Exigir(!srv.Credenciais().Any(c => c.Contains("tok-ok")), "o token da conta saiu no envio anônimo");
        Anonimizador.Reiniciar();
    }

    /// <summary>Spec 022 (Q2-B): sem conta (sem token nem bilhete) nenhum arquivo sobe; só os eventos do login vão à rota aberta, sem credencial.</summary>
    static void ContaSemConta()
    {
        LoginSemConta.Limpar();
        using var srv = new ServidorFalso();
        var s = Sessao(5, 1);
        LoginSemConta.Enfileirar("inicio", true, null);
        LoginSemConta.Enfileirar("codigo", false, "codigo_errado");
        using var envio = EnvioComConta(srv, s, _ => null, () => null);
        envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>()); envio.Agora();
        Exigir(Esperar(() => srv.Logins().Length > 0, 20_000), "os eventos do login não chegaram ao servidor");
        envio.Fechar(TimeSpan.FromSeconds(2));
        Exigir(srv.Credenciais().Length == 0 && srv.Cabecalhos().Length == 0, "sem conta nenhum arquivo podia subir: " + string.Join(",", srv.Credenciais()));
        var (pc, credencial, corpo) = srv.Logins()[0];
        Exigir(pc == envio.Pc && !credencial, "a rota do login leva só o id do PC, sem credencial");
        var j = JsonNode.Parse(corpo)!.AsObject();
        Exigir(j["eventos"]!.AsArray().Count == 2 && j["eventos"]![1]!["erro"]!.GetValue<string>() == "codigo_errado" && j["eventos"]![0]!["erro"] == null, "eventos do login: " + corpo);
        Exigir(LoginSemConta.Pendentes == 0, "a fila devia esvaziar depois do envio confirmado");
    }

    /// <summary>
    /// O caso do beta: a pessoa abre o app, usa (o log e os eventos já são gravados), e só então entra na conta. Nada sobe sem credencial; quando ela aparece,
    /// o que ficou guardado desde a abertura tem que subir inteiro e confirmado, em ordem, e o sinal sair do neutro. Nenhum byte de antes do login pode se perder.
    /// </summary>
    static void ArquivosDeAntesDoLoginSobemDepois()
    {
        LoginSemConta.Limpar();
        using var srv = new ServidorFalso();
        var s = Sessao(40, 6);                       // log e eventos gravados antes de existir conta
        string? tok = null;                          // o "Token" do Envio devolve nulo até a pessoa entrar
        LoginSemConta.Enfileirar("inicio", true, null);
        using var envio = EnvioComConta(srv, s, _ => tok, () => null);
        envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>()); envio.Agora();
        Exigir(Esperar(() => srv.Logins().Length > 0, 20_000), "o evento do login devia subir mesmo sem conta");
        // o app continua gravando enquanto espera a conta: isso também precisa subir depois
        for (int i = 0; i < 4; i++) s.Evento("teste", new JsonObject { ["n"] = 100 + i });
        Gravador.Descarregar(TimeSpan.FromSeconds(10));
        for (int i = 0; i < 5; i++) { envio.Agora(); Thread.Sleep(400); }
        Exigir(srv.Credenciais().Length == 0 && srv.Cabecalhos().Length == 0, "sem credencial nenhum arquivo podia subir: " + string.Join(",", srv.Credenciais()));
        Exigir(s.Arquivos().All(a => s.Cursor(a.Nome) == 0), "o cursor andou sem credencial: " + Estado(s));
        Exigir(envio.Situacao().Estado == EstadoEnvio.Neutro, $"sem conta o sinal devia ser neutro: {envio.Situacao().Estado}");
        // a pessoa entra: o mesmo envio, os mesmos arquivos
        tok = ServidorFalso.TokenPadrao;
        Exigir(Esperar(() => Converge(s), 30_000, () => envio.Agora()), "depois do login os arquivos de antes não subiram inteiros: " + Estado(s));
        envio.Fechar(TimeSpan.FromSeconds(2));
        ExigirIgual(srv, s);
        Exigir(srv.Credenciais().Length > 0 && srv.Credenciais().All(c => c == "token:" + ServidorFalso.TokenPadrao), "depois do login devia usar o token: " + string.Join(",", srv.Credenciais().Distinct()));
        Exigir(envio.Situacao().Estado != EstadoEnvio.Neutro, $"o sinal devia sair do neutro depois do login: {envio.Situacao().Estado} {envio.Situacao().Titulo}");
    }

    static void LoginFila()
    {
        LoginSemConta.Limpar();
        for (int i = 0; i < 35; i++) LoginSemConta.Enfileirar("passo" + i, i % 2 == 0, null);
        Exigir(LoginSemConta.Pendentes == LoginSemConta.Max, "a fila devia parar em 20: " + LoginSemConta.Pendentes);
        // só passo, ok e tipo do erro: texto livre, código digitado e e-mail viram um tipo inofensivo (minúsculas, dígitos e _)
        LoginSemConta.Limpar();
        LoginSemConta.Enfileirar("codigo", false, "123 456");
        LoginSemConta.Enfileirar("Entrar com a@b.com", false, "Código errado: 654321!");
        var corpo = LoginSemConta.Montar(new[] { new LoginSemConta.Evento(DateTime.UtcNow, "x", true, null) }, "0.0.0", "abcdef0", out int n)!;
        Exigir(n == 1, "um evento");
        var campos = JsonNode.Parse(corpo)!["eventos"]![0]!.AsObject().Select(p => p.Key).OrderBy(k => k).ToArray();
        Exigir(campos.SequenceEqual(new[] { "erro", "hora", "ok", "passo" }), "campos do evento: " + string.Join(",", campos));
        Exigir(LoginSemConta.Limpo("a@b.com") == "ab" + "com" && LoginSemConta.Limpo("  ") == null && LoginSemConta.Limpo("###") == "outro", "limpeza do texto");
        Exigir(!corpo.Contains("@"), "corpo com e-mail");
        LoginSemConta.Limpar();
    }

    static void LoginCorpoESinal()
    {
        var muitos = Enumerable.Range(0, 20).Select(i => new LoginSemConta.Evento(DateTime.UtcNow, "passo_com_nome_comprido_" + i, false, "tipo_de_erro_bem_comprido_" + i)).ToList();
        var corpo = LoginSemConta.Montar(muitos, "0.4.0", "abcdef0", out int usados)!;
        Exigir(Encoding.UTF8.GetByteCount(corpo) <= LoginSemConta.TamanhoMax && usados is > 0 and <= LoginSemConta.Max, $"corpo de {Encoding.UTF8.GetByteCount(corpo)} bytes com {usados} eventos");
        // o sinal sem conta: nem verde (o servidor não confirmou arquivo) nem vermelho (não é defeito)
        LoginSemConta.Limpar();
        using var srv = new ServidorFalso();
        var s = Sessao(5, 1);
        using var envio = EnvioComConta(srv, s, _ => null, () => null);
        envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>()); envio.Agora();
        Thread.Sleep(2500);
        var sit = envio.Situacao();
        Exigir(sit.Estado == EstadoEnvio.Neutro && sit.Titulo.Contains("conta"), $"sem conta o sinal devia ser neutro: {sit.Estado} {sit.Titulo}");
        envio.Fechar(TimeSpan.FromSeconds(1));
    }

    static void Conta401Renova()
    {
        using var srv = new ServidorFalso();
        srv.TokenAceito = "tok-novo";
        var s = Sessao(5, 1);
        var pediuRenovar = new List<bool>();
        using var envio = EnvioComConta(srv, s, renovar => { lock (pediuRenovar) pediuRenovar.Add(renovar); return renovar ? "tok-novo" : "tok-velho"; }, () => null);
        envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>()); envio.Agora();
        Exigir(Esperar(() => srv.Credenciais().Contains("token:tok-velho"), 20_000), "o primeiro envio devia usar o token velho");
        for (int i = 0; i < 20 && !srv.Credenciais().Contains("token:tok-novo"); i++) { envio.Agora(); Thread.Sleep(1000); }
        envio.Fechar(TimeSpan.FromSeconds(2));
        Exigir(srv.Credenciais().Contains("token:tok-novo"), "depois do 401 devia renovar e tentar com o token novo: " + string.Join(",", srv.Credenciais()));
        lock (pediuRenovar) Exigir(pediuRenovar.Contains(true), "não pediu token novo depois do 401");
    }

    static void ReduzidaEnvio()
    {
        Anonimizador.Reiniciar();
        Anonimizador.Registrar("Fulano"); Anonimizador.Registrar("ApelidoFulano"); Anonimizador.Registrar("Amigona");
        using var srv = new ServidorFalso();
        var completa = Sessao(20, 3);               // sessão aberta ANTES de desligar: não pode subir depois de desligar
        var s = SessaoReduzida("12:00:00.000 Ligação com Amigona conectada; Fulano abriu o jogo; ApelidoFulano saiu\n");
        s.Evento("sessao_inicio", new JsonObject { ["versao"] = "9.9.9" });
        s.Evento("live", new JsonObject { ["jogo"] = "jogo do Fulano", ["outro_install"] = "id-da-amiga", ["outro_apelido"] = "Amigona" });
        Gravador.Descarregar(TimeSpan.FromSeconds(5));
        using var envio = new Envio($"http://127.0.0.1:{srv.Porta}", s, () => new Identidade("123456", "Fulano", "ApelidoFulano", "instalacao-de-teste"), "id-anonimo-de-teste") { Habilitado = false, Bilhete = () => ServidorFalso.BilheteAceito };
        envio.Ativo = true;
        envio.Iniciar(new[] { completa });
        envio.DefinirPerfil(new JsonObject { ["gpu"] = "placa de Fulano" }, "hardware-x");
        envio.Agora();
        Thread.Sleep(2500);
        Exigir(srv.Tamanho($"{s.Id}|log.txt") == 0 && !srv.VeioEvento("sessao_inicio"), "subiu antes de a sala trazer os amigos (Pronto)");
        Anonimizador.MarcarPronto();
        envio.Agora();
        Exigir(Esperar(() => srv.VeioEvento("live") && srv.Tamanho($"{s.Id}|log.txt") > 0, 20_000), "o servidor não recebeu o log e o evento anônimos");
        envio.Fechar(TimeSpan.FromSeconds(2));
        string log = Encoding.UTF8.GetString(srv.Conteudo($"{s.Id}|log.txt"));
        Exigir(log.Contains("pessoa-") && !log.Contains("Fulano") && !log.Contains("Amigona") && !log.Contains("ApelidoFulano"), "log com nome: " + log);
        var live = srv.UltimoEvento("live")!;
        Exigir(live["outro_install"] == null && live["outro_apelido"] == null && !live.ToJsonString().Contains("Fulano"), "evento com identidade: " + live.ToJsonString());
        Exigir(srv.ContarEventos("teste") == 0, "a sessão completa pendente subiu no modo anônimo");
        foreach (var c in srv.Cabecalhos())
        {
            Exigir(c["discord_id"] == null && c["discord_nome"] == null && c["apelido"] == null && c["hw"] == null, "cabeçalho com identidade: " + c.ToJsonString());
            Exigir(c["install"]!.GetValue<string>() == "id-anonimo-de-teste" && c["pc"]!.GetValue<string>() == envio.PcAnonimo && c["pc"]!.GetValue<string>() != envio.Pc, "ids do modo anônimo: " + c["pc"] + "/" + c["install"]);
            if (c["perfil"] != null) Exigir(!c["perfil"]!.ToJsonString().Contains("Fulano"), "perfil com nome: " + c["perfil"]);
        }
        Exigir(envio.Situacao().Estado != EstadoEnvio.Ok, "o indicador não pode ficar verde \"em dia\" no modo anônimo");
        Anonimizador.Reiniciar();
    }

    // ---------- "Sobre o diagnóstico" e o aviso (etapa 14) ----------

    static void AguardaOAviso()
    {
        using var srv = new ServidorFalso();
        var s = Sessao(30, 3);
        using var envio = Criar(srv, s);
        envio.Aguardando = true; envio.Ativo = true;
        envio.Iniciar(Array.Empty<SessaoLocal>());
        envio.Agora();
        Thread.Sleep(3000);
        Exigir(srv.Pedidos == 0, $"enviou {srv.Pedidos} pedido(s) antes de a pessoa ler o aviso");
        Exigir(envio.Situacao().Estado == EstadoEnvio.Neutro && envio.Situacao().Titulo == "Diagnóstico aguardando", "indicador: " + envio.Situacao().Titulo);
        Exigir(File.Exists(Path.Combine(s.Dir, SessaoLocal.Eventos)), "o diagnóstico devia continuar gravando no disco");
        envio.Aguardando = false; envio.Agora();
        Exigir(Esperar(() => Converge(s), 20_000), "depois de ler o aviso devia subir: " + Estado(s));
    }

    static void ConvitePesquisaRegras()
    {
        const string link = "https://exemplo.invalid/pesquisa";
        var agora = new DateTime(2026, 11, 20, 12, 0, 0);
        var s = new Streemu.Util.AppState { PrimeiroUsoEm = agora.AddDays(-4), TelemetriaAvisoVersao = TextoDiagnostico.VersaoDoAviso };
        Exigir(Streemu.Util.ConvitePesquisa.Link == "" || Streemu.Util.ConvitePesquisa.LinkValido(Streemu.Util.ConvitePesquisa.Link), "o Link do código não é https");
        Exigir(!Streemu.Util.ConvitePesquisa.Deve(s, agora, ""), "convidou sem link");
        Exigir(!Streemu.Util.ConvitePesquisa.Deve(s, agora, "http://exemplo.invalid/p"), "convidou com link http");
        Exigir(!Streemu.Util.ConvitePesquisa.Deve(s, agora, "https://usuario:senha@exemplo.invalid/p"), "convidou com senha no link");
        Exigir(Streemu.Util.ConvitePesquisa.Deve(s, agora, link), "devia convidar depois de 4 dias");
        s.PrimeiroUsoEm = agora.AddDays(-2);
        Exigir(!Streemu.Util.ConvitePesquisa.Deve(s, agora, link), "convidou antes dos dias de uso");
        s.PrimeiroUsoEm = default;
        Exigir(!Streemu.Util.ConvitePesquisa.Deve(s, agora, link), "convidou sem data de primeiro uso");
        s.PrimeiroUsoEm = agora.AddDays(-4); s.TelemetriaAvisoVersao = 0;
        Exigir(!Streemu.Util.ConvitePesquisa.Deve(s, agora, link), "convidou antes de ler o aviso de diagnóstico");
        s.TelemetriaAvisoVersao = TextoDiagnostico.VersaoDoAviso;
        s.PesquisaAdiadaAte = agora.AddDays(7);
        Exigir(!Streemu.Util.ConvitePesquisa.Deve(s, agora, link), "convidou durante o adiamento");
        Exigir(Streemu.Util.ConvitePesquisa.Deve(s, agora.AddDays(7), link), "não voltou a convidar depois do adiamento");
        s.PesquisaEstado = Streemu.Util.ConvitePesquisa.NaoPerguntar;
        Exigir(!Streemu.Util.ConvitePesquisa.Deve(s, agora.AddDays(30), link), "convidou de novo depois de não perguntar mais");
        s.PesquisaEstado = Streemu.Util.ConvitePesquisa.Respondeu;
        Exigir(!Streemu.Util.ConvitePesquisa.Deve(s, agora.AddDays(30), link), "convidou de novo depois de responder");
    }

    static void AvisoDeAmigoNaoEFalha()
    {
        Exigir(!Gravidade.EhFalha("21:28:19.123 [amigo] Ana começou uma live: a tela."), "o anúncio de amigo contou como falha");
        Exigir(!Gravidade.EhFalha("21:28:19.123 [amigo] Fulano abriu um filme: Matrix. Clique em Filme para assistir junto."), "o anúncio de filme contou como falha");
        Exigir(!Gravidade.EhFalha("21:28:19.123 [ffmpeg] frame dropped, falhou"), "linha do ffmpeg contou como falha");
        Exigir(Gravidade.EhFalha("21:28:32.100 [aviso] A captura está entregando só ~44 quadros novos por segundo"), "o aviso de captura lenta devia continuar sendo falha");
        Exigir(Gravidade.EhFalha("21:29:46.100 [captura] a captura TRAVOU: ..."), "TRAVOU devia ser falha");
        Exigir(Gravidade.EhFalha("21:30:00.000 ERRO (UI) boom"), "ERRO devia ser falha");
        Exigir(Gravidade.EhFalha("21:30:00.000 Não consegui abrir o player"), "Não consegui devia ser falha");
        Exigir(!Gravidade.EhFalha("21:30:00.000 Conectado com Davi"), "linha comum contou como falha");
    }

    static void TextoBateComOWorker()
    {
        // o texto diz quantos dias o servidor guarda: tem que ser o número que o Worker usa
        string? dir = AppContext.BaseDirectory;
        string? js = null;
        while (dir != null && js == null) { var c = Path.Combine(dir, "relay", "telemetria.js"); if (File.Exists(c)) js = c; dir = Path.GetDirectoryName(dir); }
        Exigir(js != null, "não achei relay/telemetria.js subindo a partir de " + AppContext.BaseDirectory);
        var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(js!), @"SESSAO_GUARDA_MS\s*=\s*(\d+)\s*\*\s*86400e3");
        Exigir(m.Success && int.Parse(m.Groups[1].Value) == TextoDiagnostico.DiasDeGuarda, $"o Worker guarda {m.Groups[1].Value} dias e o texto diz {TextoDiagnostico.DiasDeGuarda}");
        string todo = TextoDiagnostico.Completo;
        foreach (var trecho in new[] { "120 dias", "5 dias úteis", "ANÔNIMO", "equipe do Streemu", "Ver o que é enviado", "sem prazo", "pessoa-1", "o MAC" })
            Exigir(todo.Contains(trecho), "o texto completo não cita: " + trecho);
        Exigir(TextoDiagnostico.Aviso(false).Contains("120 dias") && TextoDiagnostico.Aviso(false).Contains("desligar"), "aviso curto (ligado)");
        Exigir(TextoDiagnostico.Aviso(true).Contains("ANÔNIMO") && !TextoDiagnostico.Aviso(true).Contains("Desligar diagnóstico"), "aviso curto (quem tinha desligado)");
        Exigir(TextoDiagnostico.Aviso(false).Length < 600 && TextoDiagnostico.Aviso(true).Length < 600, "o aviso curto passou de 600 caracteres");
    }
}
