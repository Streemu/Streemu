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
        Caso("rede-trocas-de-caminho-tempo-no-relay-e-perda-da-volta-do-transmissor", RedeCaminhoEVolta);
        Caso("latencia-srt-calculo-por-ida-e-volta-piso-do-relay-e-teto", LatenciaCalculo);
        Caso("latencia-srt-escalada-so-com-perda-sustentada-e-no-maximo-4-vezes", LatenciaEscalada);
        Caso("vigia-para-de-reabrir-captura-quando-reabrir-nao-ganha-quadros", ReaberturaInutilReplay);
        Caso("vigia-limita-reaberturas-por-janela-mesmo-voltando-saudavel", ReaberturaInutilLimitePorJanela);
        Caso("vigia-reabertura-inutil-duas-threads-sem-excecao", ReaberturaInutilDuasThreads);
        Caso("oferta-de-30-fps-so-quando-a-live-em-60-fica-abaixo-e-a-pessoa-decide", SugestaoDe30FpsRegra);
        Caso("anonimo-troca-nomes-por-pessoa-n-e-tira-identidade-do-evento", AnonimizadorTroca);
        Caso("anonimo-sessao-grava-log-e-series-mas-nao-o-texto-livre", ReduzidaGrava);
        Caso("anonimo-envio-sem-nome-sem-identidade-e-so-depois-da-sala", ReduzidaEnvio);
        Caso("perfil-enxuto-sem-o-que-a-spec-tirou-e-hags-nao-gera-perfil-novo", PerfilEnxuto);
        Caso("perfil-o-mesmo-conjunto-no-completo-e-no-anonimo-e-o-anonimo-sem-identidade-nem-id-cru-nem-pc-real", PerfilIgualEAnonimoSemIdentidade);
        Caso("discord-nao-sobe-em-nenhum-envio-nem-no-cabecalho-nem-no-log", DiscordNaoSobe);
        Caso("amigo-vira-amigo-n-no-log-e-nos-eventos-e-o-desligado-nao-aparece-no-envio-do-ligado", AmigoViraAmigoN);
        Caso("amigo-eventos-das-duas-pontas-continuam-ligados-por-transmissao-e-ligacao", AmigoDuasPontasLigadas);
        Caso("conta-token-no-envio-normal-e-conta-no-pacote", ContaTokenNormal);
        Caso("conta-bilhete-no-anonimo-e-token-nunca-sai", ContaBilheteAnonimo);
        Caso("conta-sem-conta-so-sobem-os-eventos-do-login", ContaSemConta);
        Caso("conta-arquivos-de-antes-do-login-sobem-completos-depois-do-login", ArquivosDeAntesDoLoginSobemDepois);
        Caso("login-sem-conta-fila-limita-em-20-e-so-leva-os-campos-permitidos", LoginFila);
        Caso("login-sem-conta-corpo-cabe-em-2kb-e-sinal-nao-fica-verde-nem-vermelho", LoginCorpoESinal);
        Caso("conta-401-renova-o-token-e-tenta-de-novo", Conta401Renova);
        Caso("aviso-instalacao-nova-nao-envia-antes-de-ler-o-aviso", AguardaOAviso);
        Caso("aviso-texto-bate-com-o-worker-e-diz-o-que-importa", TextoBateComOWorker);
        Caso("aviso-texto-do-desligado-e-do-anonimo-diz-o-que-mudou-e-nao-promete-demais", TextoDosTresEstados);
        Caso("aviso-exportar-privacidade-grava-utf8-com-bom-e-crlf-e-sem-caminho-sai-2", ExportarPrivacidade);
        Caso("estado-antigo-desligado-cai-em-desligado-nunca-liga-o-anonimo-e-a-faixa-vem-uma-vez", MigracaoDesligado);
        Caso("estado-os-tres-modos-persistem-no-json-e-o-antigo-segue-legivel", ModosPersistem);
        Caso("desligado-sessao-inteira-so-entrega-a-camada-essencial", DesligadoSoEssencial);
        Caso("essencial-corpo-so-leva-o-que-a-lista-branca-permite-e-descarta-o-resto", EssencialCorpoMinimo);
        Caso("essencial-no-anonimo-nao-leva-id-aleatorio-sessao-nem-hora-e-sai-pelo-token", EssencialNoAnonimo);
        Caso("essencial-no-anonimo-ao-fechar-guarda-e-so-sobe-na-abertura-seguinte", EssencialAnonimoGuarda);
        Caso("essencial-espera-a-faixa-e-a-conta-e-nao-perde-o-que-ja-contou", EssencialEsperaFaixaEConta);
        Caso("essencial-falha-nao-mexe-no-indicador-e-recupera-sem-duplicar", EssencialFalhaERecupera);
        Caso("essencial-401-renova-o-token-e-400-descarta-o-lote", EssencialRenovaEDescarta);
        Caso("essencial-agrupa-eventos-iguais-em-n-e-respeita-os-limites-do-worker", EssencialAgrupa);
        Caso("pesquisa-convite-so-com-link-valido-apos-dias-de-uso-e-respeita-adiar", ConvitePesquisaRegras);
        Caso("falha-aviso-de-amigo-nao-conta-como-falha", AvisoDeAmigoNaoEFalha);
        Caso("falha-grave-classifica-so-o-que-importa", FalhaGraveClassifica);
        Caso("falha-grave-freio-limita-rajada-e-nao-segura-o-fatal", FalhaGraveFreio);
        Caso("cobertura-e-a-hora-do-conteudo-nao-a-do-envio", CoberturaHoraDoConteudo);
        Caso("cobertura-hora-vira-o-dia-e-log-que-comecou-antes-da-meia-noite", CoberturaViradaDoDia);
        Caso("cobertura-arquivo-sem-hora-vai-nulo-e-ancora-sobrevive-ao-reinicio", CoberturaSemHoraEAncora);
        // gravador local e "Ver o que é enviado" (spec 018, A6 e A7)
        Caso("gravador-serie-sem-disparo-fica-no-pc-e-a-previa-diz", GravadorSerieSemDisparoFicaNoPc);
        Caso("gravador-marcacao-libera-a-serie-com-pacote-e-registra-no-historico", GravadorMarcacaoLiberaASerieComPacote);
        Caso("gravador-fim-da-live-completa-a-serie-no-mesmo-pacote", GravadorFimDaLiveCompletaNoMesmoPacote);
        Caso("gravador-abertura-seguinte-e-fechar-completam-a-live-marcada", GravadorAberturaSeguinteECompletarAoFechar);
        Caso("gravador-permite-conforme-o-estado-anonimo-sem-pedido-desligado-nunca", GravadorPermiteConformeOEstado);
        Caso("gravador-so-quando-marcar-so-a-marcacao-vale", GravadorSoQuandoMarcar);
        Caso("gravador-disparos-pelos-eventos-e-pacote-unico", GravadorDisparosPelosEventos);
        Caso("gravador-resumo-com-problema-limiares-e-minutos-seguidos", GravadorResumoComProblemaLimiares);
        Caso("gravador-serie-guarda-sete-dias-no-pc", GravadorSerieGuardaSeteDiasNoPc);
        Caso("gravador-uma-serie-por-live-com-pacote-proprio-e-nome-da-serie-por-prefixo", GravadorSerieUmaPorLiveENomes);
        Caso("gravador-desligado-so-sobe-com-o-clique-anonimo-e-com-pacote", GravadorDesligadoSoSobeComOCliqueEAnonimo);
        Caso("historico-de-envios-soma-complemento-marca-visto-remove-e-vence-em-30-dias", GravadorHistoricoDeEnvios);
        Caso("apagar-pacote-no-servidor-em-etapas-identificado-anonimo-sem-conta", ApagarPacoteNoServidor);
        // spec 018, A8: cifra no PC (D21) e prazos locais (D24). Escritos sem rodar.
        Caso("cifra-local-linha-cifrada-abre-so-com-a-chave-e-texto-antigo-passa", CifraLocalLinhas);
        Caso("cifra-local-chave-ilegivel-nao-e-trocada-nas-2-primeiras-aberturas-e-na-3a-vira-antigo-e-chave-nova", CifraLocalChaveIlegivelSoEhTrocadaNaTerceiraAbertura);
        Caso("cifra-local-abrir-zera-a-contagem-e-chave-que-abre-depois-liga-a-cifra-sem-reiniciar", CifraLocalAbrirZeraAContagemELateAbreSemReiniciar);
        Caso("cifra-local-sessao-nao-deixa-texto-legivel-e-reabre-depois-de-reiniciar", CifraLocalSessao);
        Caso("cifra-local-estado-cifra-so-a-identidade-tira-a-relaysenha-e-le-o-estado-antigo", CifraLocalEstado);
        Caso("cifra-local-historico-de-arquivos-e-de-envios-cifrados-e-o-antigo-em-claro-abre", CifraLocalHistoricos);
        Caso("prazos-locais-log-30-dias-e-avatar-90-dias-batem-com-o-texto", PrazosLocais);
        // spec 018, A9: cifra do envio
        Caso("cifra-log-relatorio-perfil-e-nota-vao-cifrados-e-o-resumo-e-os-eventos-ficam-legiveis", CifraNoEnvio);
        Caso("cifra-quadro-segue-o-contrato-cada-quadro-tem-chave-propria-e-adulterar-reprova", CifraQuadroEForma);
        Caso("cifra-chave-embutida-v1-e-a-variavel-de-teste-so-vale-em-teste", CifraChaveEmbutidaEIgnoraTesteForaDeTeste);
        Caso("cifra-falha-vira-sinal-vermelho-nunca-cai-para-o-claro-e-recupera", CifraFalhaNaoCaiParaOClaro);
        Caso("cifra-sessao-de-app-antigo-que-ja-comecou-em-claro-termina-em-claro", CifraSessaoAntigaTerminaEmClaro);
    }

    // ---------- cifra no PC e prazos locais (spec 018, A8, D21 e D24) ----------

    sealed class Desliga : IDisposable { readonly Action _a; public Desliga(Action a) => _a = a; public void Dispose() => _a(); }

    /// <summary>Liga a cifra local com a chave numa pasta do teste (nunca a do usuário) e devolve quem volta ao padrão. Em teste automático a cifra vem desligada.</summary>
    static IDisposable CifraLigada()
    {
        CofreLocal.UsarPasta(Path.Combine(Tmp, "chave-local"));
        CofreLocal.Forcar(true);
        return new Desliga(() => { CofreLocal.Forcar(null); CofreLocal.UsarPasta(null); });
    }

    /// <summary>Como <see cref="Sessao"/>, mas com o log cifrado linha a linha do jeito que o Log.cs grava.</summary>
    static SessaoLocal SessaoCifrada(int linhas, int eventos)
    {
        string id = $"20261126-{DateTime.Now:HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        string pasta = Path.Combine(Tmp, "logs");
        Directory.CreateDirectory(pasta);
        string log = Path.Combine(pasta, $"sessao-{id}.log");
        var sb = new StringBuilder();
        for (int i = 0; i < linhas; i++) sb.Append(CofreLocal.Linha($"12:00:{i % 60:00}.000 linha {i} texto neutro para o teste de envio")).Append('\n');
        File.WriteAllText(log, sb.ToString(), new UTF8Encoding(false));
        var s = SessaoLocal.Criar(id, log, naoEnviar: false);
        for (int e = 0; e < eventos; e++) s.Evento("teste", new JsonObject { ["n"] = e });
        Gravador.Descarregar(TimeSpan.FromSeconds(10));
        return s;
    }

    static void CifraLocalLinhas()
    {
        using var _ = CifraLigada();
        string claro = "12:00:01.000 Liguei com NOME-DO-AMIGO no jogo JOGO-SECRETO (ação)";
        string c = CofreLocal.Linha(claro);
        Exigir(c.StartsWith(CofreLocal.Prefixo) && !c.Contains('\n') && !c.Contains("AMIGO") && !c.Contains("JOGO"), "a linha cifrada devia ter o prefixo, nenhuma quebra e nada do texto: " + c);
        Exigir(CofreLocal.Abrir(c) == claro, "a linha cifrada não voltou igual");
        Exigir(CofreLocal.Linha(claro) != c, "o mesmo texto cifrou igual duas vezes (o nonce tem que mudar)");
        // adulterada: não abre, e o texto lido vira o marcador em vez de bytesRuins
        var b = Convert.FromBase64String(c[CofreLocal.Prefixo.Length..]); b[^1] ^= 1;
        string ruim = CofreLocal.Prefixo + Convert.ToBase64String(b);
        Exigir(CofreLocal.Abrir(ruim) == null && CofreLocal.TextoDaLinha(ruim) == CofreLocal.LinhaIlegivel, "linha adulterada devia virar o marcador");
        Exigir(CofreLocal.TextoDaLinha("12:00 linha antiga em claro") == "12:00 linha antiga em claro", "texto de antes da A8 devia passar como está");
        // várias linhas: o texto que o arquivo guarda, e o que o envio recupera dos bytes lidos (linhas antigas em claro misturadas passam)
        string texto = "primeira SEGREDO-1\nsegunda SEGREDO-2\n";
        string cifrado = CofreLocal.CifrarLinhas(texto);
        Exigir(!cifrado.Contains("SEGREDO") && cifrado.Count(ch => ch == '\n') == 2 && cifrado.EndsWith("\n"), "CifrarLinhas devia cifrar cada linha e manter as quebras");
        Exigir(CofreLocal.DecifrarLinhas(Encoding.UTF8.GetBytes(cifrado)) == texto, "DecifrarLinhas não devolveu o texto");
        Exigir(CofreLocal.DecifrarLinhas(Encoding.UTF8.GetBytes("antiga em claro\n" + cifrado)) == "antiga em claro\n" + texto, "linha antiga misturada devia passar");
        // o Log abre o arquivo com Encoding.UTF8, que grava o BOM antes da 1ª linha: ela tem que abrir mesmo assim (em 26/11 o "c1:<base64>" subiu como conteúdo)
        var comBom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(cifrado)).ToArray();
        Exigir(comBom[0] == 0xEF, "o teste devia começar com o BOM");
        string aberto = CofreLocal.DecifrarLinhas(comBom);
        Exigir(aberto == texto && !aberto.Contains(CofreLocal.Prefixo), "a 1ª linha cifrada com BOM devia abrir, sem o BOM nem o c1: no que sobe: " + aberto);
        Exigir(CofreLocal.TextoDaLinha(Encoding.UTF8.GetString(comBom).Split('\n')[0]) == "primeira SEGREDO-1", "TextoDaLinha com BOM não abriu a linha");
        Exigir(CofreLocal.DecifrarLinhas(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("só em claro\n")).ToArray()) == "só em claro\n", "log em claro com BOM devia subir sem o BOM");
        // reiniciar: a chave vem do arquivo (DPAPI), não da memória; outra pasta de chave = outra chave, que não abre a linha
        CofreLocal.Forcar(true);
        Exigir(CofreLocal.Abrir(c) == claro, "depois de esquecer a chave em memória a linha não abriu (a chave devia vir do arquivo)");
        Exigir(File.Exists(Path.Combine(Tmp, "chave-local", "chave-local.dat")), "faltou o chave-local.dat");
        CofreLocal.UsarPasta(Path.Combine(Tmp, "outra-chave"));
        Exigir(CofreLocal.Abrir(c) == null, "uma chave de outra pasta abriu a linha");
        CofreLocal.UsarPasta(Path.Combine(Tmp, "chave-local"));
        CofreLocal.Forcar(false);
        Exigir(CofreLocal.Linha(claro) == claro && CofreLocal.CifrarLinhas(texto) == texto, "com a cifra desligada o texto devia ir como está");
    }

    /// <summary>Pasta de chave nova para um caso, com um <c>chave-local.dat</c> que o DPAPI não abre (bytesRuins) quando <paramref name="ilegivel"/>.</summary>
    static string PastaDeChave(string nome, bool ilegivel)
    {
        string pasta = Path.Combine(Tmp, nome);
        if (Directory.Exists(pasta)) Directory.Delete(pasta, true);
        Directory.CreateDirectory(pasta);
        if (ilegivel) File.WriteAllBytes(Path.Combine(pasta, "chave-local.dat"), Encoding.UTF8.GetBytes("isto não é uma chave protegida pelo DPAPI"));
        return pasta;
    }

    /// <summary>Uma "abertura do app": esquece a chave em memória, tenta a chave da pasta e espera a thread das tentativas em segundo plano. Devolve o que <see cref="CofreLocal.Linha"/> deu na hora.</summary>
    static string AberturaDoApp(string pasta, string texto = "linha de teste")
    {
        CofreLocal.UsarPasta(pasta);
        CofreLocal.Forcar(true);
        string l = CofreLocal.Linha(texto);
        Exigir(CofreLocal.AguardarTentativas(15_000), "a thread das tentativas em segundo plano não terminou");
        return l;
    }

    /// <summary>Põe em <paramref name="pasta"/> um chave-local.dat que abre (uma chave de verdade, criada numa pasta à parte).</summary>
    static void ColocarChaveValida(string pasta)
    {
        string origem = PastaDeChave("chave-valida-origem", ilegivel: false);
        CofreLocal.UsarPasta(origem); CofreLocal.Forcar(true);
        CofreLocal.Linha("cria a chave");
        Exigir(File.Exists(Path.Combine(origem, "chave-local.dat")), "a chave de origem não foi criada");
        File.Copy(Path.Combine(origem, "chave-local.dat"), Path.Combine(pasta, "chave-local.dat"), overwrite: true);
    }

    static void CifraLocalChaveIlegivelSoEhTrocadaNaTerceiraAbertura()
    {
        string pasta = PastaDeChave("chave-ilegivel", ilegivel: true);
        string dat = Path.Combine(pasta, "chave-local.dat"), antigo = dat + ".antigo", falhas = Path.Combine(pasta, "chave-local.falhas");
        byte[] bytesRuins = File.ReadAllBytes(dat);
        CofreLocal.EsperaDasTentativas(TimeSpan.Zero);
        try
        {
            for (int n = 1; n <= 2; n++)
            {
                Exigir(AberturaDoApp(pasta) == "linha de teste", $"abertura {n}: sem chave a linha devia sair em claro");
                Exigir(CofreLocal.Falha != null, $"abertura {n}: a falha devia estar preenchida (alimenta o diagnóstico)");
                Exigir(File.ReadAllBytes(dat).SequenceEqual(bytesRuins) && !File.Exists(antigo), $"abertura {n}: a chave ilegível foi trocada cedo demais");
                Exigir(File.ReadAllText(falhas).Trim() == n.ToString(), $"abertura {n}: o contador devia ser {n}, é '{File.ReadAllText(falhas).Trim()}'");
            }
            // 3ª abertura seguida: o arquivo vira .antigo (com os mesmos bytes), nasce chave nova e a cifra volta sozinha
            AberturaDoApp(pasta);
            Exigir(File.Exists(antigo) && File.ReadAllBytes(antigo).SequenceEqual(bytesRuins), "na 3ª abertura o chave-local.dat devia ter virado .antigo, com os mesmos bytes");
            Exigir(File.Exists(dat) && !File.ReadAllBytes(dat).SequenceEqual(bytesRuins), "na 3ª abertura devia existir uma chave nova");
            Exigir(CofreLocal.Falha == null && CofreLocal.ChaveAberta, "depois de recriar a chave a falha devia estar limpa: " + CofreLocal.Falha);
            Exigir(!File.Exists(falhas), "depois de recriar o contador devia zerar");
            string c = CofreLocal.Linha("depois da troca");
            Exigir(c.StartsWith(CofreLocal.Prefixo) && CofreLocal.Abrir(c) == "depois da troca", "com a chave nova a linha devia cifrar e abrir");
            // a próxima abertura já abre a chave nova de cara, sem thread
            string c2 = AberturaDoApp(pasta, "de novo");
            Exigir(c2.StartsWith(CofreLocal.Prefixo) && CofreLocal.Falha == null && CofreLocal.Abrir(c) == "depois da troca", "a abertura seguinte devia abrir a chave nova e a linha anterior");
            // um .antigo anterior é sobrescrito
            File.WriteAllBytes(dat, bytesRuins); File.WriteAllBytes(antigo, new byte[] { 1, 2, 3 });
            for (int n = 1; n <= 3; n++) AberturaDoApp(pasta);
            Exigir(File.ReadAllBytes(antigo).SequenceEqual(bytesRuins), "o .antigo anterior devia ser sobrescrito pelo novo");
        }
        finally { CofreLocal.EsperaDasTentativas(null); CofreLocal.Forcar(null); CofreLocal.UsarPasta(null); }
    }

    static void CifraLocalAbrirZeraAContagemELateAbreSemReiniciar()
    {
        string pasta = PastaDeChave("chave-zera", ilegivel: true);
        string dat = Path.Combine(pasta, "chave-local.dat"), antigo = dat + ".antigo", falhas = Path.Combine(pasta, "chave-local.falhas");
        byte[] bytesRuins = File.ReadAllBytes(dat);
        CofreLocal.EsperaDasTentativas(TimeSpan.Zero);
        try
        {
            AberturaDoApp(pasta); AberturaDoApp(pasta);
            Exigir(File.ReadAllText(falhas).Trim() == "2", "duas aberturas sem abrir: o contador devia ser 2");
            // a chave passa a abrir: zera
            ColocarChaveValida(pasta);
            string c = AberturaDoApp(pasta);
            Exigir(c.StartsWith(CofreLocal.Prefixo) && CofreLocal.Falha == null, "com a chave abrindo a cifra devia valer já na abertura");
            Exigir(!File.Exists(falhas) && !File.Exists(antigo), "abrir devia zerar o contador e não criar .antigo");
            // volta a não abrir: recomeça do 1, não da 3ª (sem o zero, esta já recriaria a chave)
            File.WriteAllBytes(dat, bytesRuins);
            AberturaDoApp(pasta);
            Exigir(File.ReadAllText(falhas).Trim() == "1" && !File.Exists(antigo) && File.ReadAllBytes(dat).SequenceEqual(bytesRuins), "depois de abrir, a contagem devia recomeçar do 1 sem trocar a chave");

            // abre DEPOIS, na mesma abertura (perfil carregando): a cifra passa a valer sem reiniciar e a falha some
            File.Delete(falhas);
            File.WriteAllBytes(dat, bytesRuins);
            CofreLocal.EsperaDasTentativas(TimeSpan.FromMilliseconds(400));
            CofreLocal.UsarPasta(pasta); CofreLocal.Forcar(true);
            Exigir(CofreLocal.Linha("antes") == "antes" && CofreLocal.Falha != null, "enquanto a chave não abre a linha sai em claro e a falha aparece");
            ColocarChaveValidaSemMexerNaAbertura(pasta);
            Exigir(CofreLocal.AguardarTentativas(15_000), "a thread das tentativas não terminou");
            Exigir(CofreLocal.Falha == null && CofreLocal.ChaveAberta, "a chave abriu em segundo plano: a falha devia ter sumido: " + CofreLocal.Falha);
            string depois = CofreLocal.Linha("depois");
            Exigir(depois.StartsWith(CofreLocal.Prefixo) && CofreLocal.Abrir(depois) == "depois", "a cifra devia valer depois que a chave abriu");
            Exigir(!File.Exists(falhas) && !File.Exists(antigo), "abrir em segundo plano não conta falha nem cria .antigo");
        }
        finally { CofreLocal.EsperaDasTentativas(null); CofreLocal.Forcar(null); CofreLocal.UsarPasta(null); }
    }

    /// <summary>Como <see cref="ColocarChaveValida"/>, mas sem trocar a pasta nem o modo do cofre que o caso está testando (a chave de origem vem de um arquivo gerado antes, sem mexer no estado em memória).</summary>
    static void ColocarChaveValidaSemMexerNaAbertura(string pasta)
    {
        string origem = Path.Combine(Tmp, "chave-valida-origem", "chave-local.dat"); // criada por ColocarChaveValida no passo anterior
        File.Copy(origem, Path.Combine(pasta, "chave-local.dat"), overwrite: true);
    }

    static void CifraLocalSessao()
    {
        using var _ = CifraLigada();
        string[] segredos = { "NOME-SECRETO-DO-AMIGO", "REDE-SECRETA-1", "REDE-SECRETA-2", "CPU-SECRETA" };
        var s = SessaoCifrada(5, 0);
        s.Evento("teste", new JsonObject { ["nome"] = segredos[0] });
        s.Acrescentar("rede.ndjson", $"linha {segredos[1]}\nlinha {segredos[2]}");
        s.Retrato("perfil-abc.json", "{\n  \"cpu\": \"" + segredos[3] + "\"\n}");
        Gravador.Descarregar(TimeSpan.FromSeconds(10));
        var arquivos = Directory.EnumerateFiles(s.Dir, "*", SearchOption.AllDirectories).Append(s.E.LogPath!).ToList();
        Exigir(arquivos.Count >= 5, "faltou arquivo da sessão: " + string.Join(", ", arquivos.Select(Path.GetFileName)));
        foreach (var f in arquivos)
        {
            string t = File.ReadAllText(f);
            foreach (var seg in segredos.Append("texto neutro").Append(s.E.LogPath!)) Exigir(!t.Contains(seg), $"{Path.GetFileName(f)} tem texto legível: {seg}");
        }
        Exigir(File.ReadAllLines(Path.Combine(s.Dir, SessaoLocal.Eventos)).All(l => l.StartsWith(CofreLocal.Prefixo)), "o eventos.ndjson tem linha sem cifra");
        Exigir(File.ReadAllLines(Path.Combine(s.Dir, "perfil-abc.json")).All(l => l.StartsWith(CofreLocal.Prefixo)), "o perfil tem linha sem cifra");
        // o app lê o que gravou
        string Decifrado(string nome) => CofreLocal.DecifrarLinhas(File.ReadAllBytes(Path.Combine(s.Dir, nome)));
        Exigir(Decifrado("rede.ndjson") == $"linha {segredos[1]}\nlinha {segredos[2]}\n", "rede.ndjson não voltou igual: " + Decifrado("rede.ndjson"));
        Exigir(Decifrado("perfil-abc.json") == "{\n  \"cpu\": \"" + segredos[3] + "\"\n}\n", "o perfil não voltou igual");
        Exigir(Decifrado(SessaoLocal.Eventos).Contains(segredos[0]) && JsonNode.Parse(Decifrado(SessaoLocal.Eventos).Split('\n')[0]) is JsonObject, "o evento não voltou como JSON");
        Exigir(CofreLocal.DecifrarLinhas(File.ReadAllBytes(s.E.LogPath!)).Contains("linha 3 texto neutro"), "o log não voltou");
        // reiniciar: a chave sai da memória e a sessão reabre pelo sessao.json cifrado, com o mesmo log
        CofreLocal.Forcar(true);
        var r = SessaoLocal.Abrir(s.Dir);
        Exigir(r != null && r.Id == s.Id && r.E.LogPath == s.E.LogPath, "a sessão não reabriu pelo sessao.json cifrado");
        // sessao.json de antes da A8 (em claro) continua abrindo
        CofreLocal.Forcar(false);
        s.Salvar();
        Exigir(File.ReadAllText(Path.Combine(s.Dir, "sessao.json")).StartsWith("{"), "com a cifra desligada o sessao.json devia ir em claro");
        CofreLocal.Forcar(true);
        Exigir(SessaoLocal.Abrir(s.Dir)?.Id == s.Id, "o sessao.json em claro devia abrir com a cifra ligada");
    }

    static void CifraLocalEstado()
    {
        string arq = Path.Combine(Tmp, "estado-cifra", "estado.json");
        Streemu.Util.AppState.ArquivoParaTeste = arq;
        try
        {
            using var _ = CifraLigada();
            string[] segredos = { "NICK-SECRETO", "CONTA-NOME-SECRETO", "DISCORD-SECRETO", "123456789012345678", "conta-secreta-1", "dispositivo-secreto-1", "INSTALL-SECRETO", "ANONIMO-SECRETO", "100.90.1.2", "GRUPO-SECRETO", "APELIDO-SECRETO", "amigo-silenciado-x" };
            var e = new Streemu.Util.AppState
            {
                Nick = segredos[0], ContaNome = segredos[1], DiscordNome = segredos[2], DiscordId = segredos[3], ContaId = segredos[4], DispositivoId = segredos[5], InstallId = segredos[6],
                TelemetriaIdAnonimo = segredos[7], AltEndpoint = segredos[8], Grupos = { new Streemu.Util.GrupoSalvo { Id = "g-secreto", Nome = segredos[9] } },
                ApelidosLocais = { ["amigo1"] = segredos[10] }, AmigosSilenciados = { segredos[11] },
                TemaClaro = true, InterfaceScale = 1.25, PlayerVolumePercent = 42,
            };
            e.Save();
            string cru = File.ReadAllText(arq);
            foreach (var seg in segredos.Append("g-secreto")) Exigir(!cru.Contains(seg), "o estado.json tem texto legível: " + seg);
            Exigir(cru.Contains("\"TemaClaro\": true") && cru.Contains("1.25") && cru.Contains("\"PlayerVolumePercent\": 42"), "as preferências sem dado pessoal devem seguir legíveis: " + cru[..Math.Min(300, cru.Length)]);
            Exigir(cru.Contains("IdentidadeCifrada") && !cru.Contains("RelaySenha"), "faltou a identidade cifrada ou a RelaySenha ainda existe");
            var v = Streemu.Util.AppState.Load();
            Exigir(v.Nick == segredos[0] && v.ContaNome == segredos[1] && v.DiscordNome == segredos[2] && v.DiscordId == segredos[3] && v.ContaId == segredos[4]
                   && v.DispositivoId == segredos[5] && v.InstallId == segredos[6] && v.TelemetriaIdAnonimo == segredos[7] && v.AltEndpoint == segredos[8], "a identidade não voltou depois de reiniciar");
            Exigir(v.Grupos.Count == 1 && v.Grupos[0].Nome == segredos[9] && v.ApelidosLocais["amigo1"] == segredos[10] && v.AmigosSilenciados.SequenceEqual(new[] { segredos[11] }), "grupos, apelidos ou silenciados não voltaram");
            Exigir(v.TemaClaro && v.InterfaceScale == 1.25 && v.PlayerVolumePercent == 42, "as preferências não voltaram");

            // estado de antes da A8: lido em claro e regravado cifrado, e a RelaySenha antiga vai embora
            File.WriteAllText(arq, "{\"ContaNome\":\"LEGADO-SECRETO\",\"RelaySenha\":\"SENHA-ANTIGA\",\"TemaClaro\":true}");
            var l = Streemu.Util.AppState.Load();
            Exigir(l.ContaNome == "LEGADO-SECRETO" && l.TemaClaro, "o estado antigo em claro devia abrir sem perder nada");
            string depois = File.ReadAllText(arq);
            Exigir(!depois.Contains("LEGADO-SECRETO") && !depois.Contains("SENHA-ANTIGA") && depois.Contains("IdentidadeCifrada"), "o estado antigo devia ser regravado cifrado e sem a RelaySenha: " + depois);
            Exigir(Streemu.Util.AppState.Load().ContaNome == "LEGADO-SECRETO", "o estado regravado não abriu");

            // outro usuário do Windows (ou arquivo estragado): não abre a identidade, mas o resto do estado segue
            File.WriteAllText(arq, "{\"IdentidadeCifrada\":\"c1:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"TemaClaro\":true}");
            var o = Streemu.Util.AppState.Load();
            Exigir(o.ContaNome == null && o.ContaId == null && o.TemaClaro, "sem a chave a identidade devia vir vazia e as preferências ficar");
        }
        finally { Streemu.Util.AppState.ArquivoParaTeste = null; }
    }

    static void CifraLocalHistoricos()
    {
        // arquivos.json: nome de amigo, nome e caminho do arquivo
        string arq = Path.Combine(Tmp, "arquivos-cifra.json");
        Streemu.Arquivos.Historico Abrir() => new(arq, () => DateTime.Now, _ => { });
        Streemu.Arquivos.ItemHistorico Item(string pedido) => new(Streemu.Arquivos.Sentido.Recebido, pedido, "amigo-id", "APELIDO-DO-AMIGO", "arquivo-secreto.zip", 1234, "ident-" + pedido,
            DateTime.Now, Streemu.Arquivos.Resultado.Conferido, @"C:\Users\ana\Downloads\Streemu\arquivo-secreto.zip"); // "ana": o único perfil que a trava g do espelho público aceita
        // de antes da A8: em claro
        Abrir().Registrar(Item("pedido-antigo"));
        Exigir(File.ReadAllText(arq).Contains("APELIDO-DO-AMIGO"), "(preparo) o histórico antigo devia estar em claro com a cifra desligada");
        using (var _ = CifraLigada())
        {
            var h = Abrir();
            Exigir(h.Itens.Count == 1 && h.Itens[0].Pedido == "pedido-antigo", "o arquivos.json em claro devia abrir com a cifra ligada");
            h.Registrar(Item("pedido-novo"));
            string cru = File.ReadAllText(arq);
            foreach (var seg in new[] { "APELIDO-DO-AMIGO", "arquivo-secreto", "Downloads", "pedido-" }) Exigir(!cru.Contains(seg), "o arquivos.json tem texto legível: " + seg);
            var h2 = Abrir();
            Exigir(h2.Itens.Count == 2 && h2.Itens[0].Apelido == "APELIDO-DO-AMIGO" && h2.Itens[0].Caminho!.Contains("fulano"), "o histórico cifrado não voltou depois de reiniciar");

            // historico-envios.json
            HistoricoEnvios.Apagar();
            HistoricoEnvios.Registrar("pacote-secreto-1", "Marcacao", "Completo", "sessao-secreta", "quadros.csv", 100);
            string envios = File.ReadAllText(Path.Combine(Pasta.Raiz, "historico-envios.json"));
            Exigir(envios.StartsWith(CofreLocal.Prefixo) && !envios.Contains("pacote-secreto-1") && !envios.Contains("sessao-secreta") && !envios.Contains("quadros"), "o historico-envios.json tem texto legível");
            Exigir(HistoricoEnvios.Listar().Count == 1 && HistoricoEnvios.Listar()[0].Pacote == "pacote-secreto-1", "o histórico de envios não voltou");
            HistoricoEnvios.Apagar();
        }
        // o histórico de envios de antes da A8 (JSON em claro) abre e passa a ser gravado cifrado
        Directory.CreateDirectory(Pasta.Raiz);
        string antigo = Path.Combine(Pasta.Raiz, "historico-envios.json");
        File.WriteAllText(antigo, "[{\"Pacote\":\"pacote-antigo\",\"Quando\":\"" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + "\",\"Motivo\":\"Marcacao\",\"Modo\":\"Completo\",\"Sessao\":\"s\",\"Arquivos\":[\"quadros.csv\"],\"Bytes\":5,\"Visto\":false}]");
        using (var _ = CifraLigada())
        {
            Exigir(HistoricoEnvios.Listar().Count == 1, "o histórico de envios em claro devia abrir com a cifra ligada");
            HistoricoEnvios.MarcarVistos();
            Exigir(File.ReadAllText(antigo).StartsWith(CofreLocal.Prefixo), "depois de gravar, o histórico de envios devia estar cifrado");
        }
        HistoricoEnvios.Apagar();
    }

    static void PrazosLocais()
    {
        // N15: o código e o texto do app dizem os mesmos números (spec 018, seção 9)
        Exigir(Manutencao.DiasDeLog == 30 && Manutencao.DiasDeAvatar == 90, $"prazos do código: log {Manutencao.DiasDeLog}, avatar {Manutencao.DiasDeAvatar} (a spec diz 30 e 90)");
        string texto = TextoDiagnostico.Completo;
        Exigir(texto.Contains("o log fica 30 dias") && texto.Contains("o avatar guardado, 90 dias sem aparecer"), "o texto do app não cita os prazos locais (30 e 90)");

        // avatar: 90 dias sem aparecer (o AvatarStore renova a data ao abrir com ele)
        string avatares = Path.Combine(Tmp, "avatar-prazo");
        Directory.CreateDirectory(avatares);
        var agora = DateTime.Now;
        File.WriteAllText(Path.Combine(avatares, "meu.png"), "x"); File.SetLastWriteTime(Path.Combine(avatares, "meu.png"), agora.AddDays(-91));
        File.WriteAllText(Path.Combine(avatares, "meu.tira"), "x"); File.SetLastWriteTime(Path.Combine(avatares, "meu.tira"), agora.AddDays(-89));
        File.WriteAllText(Path.Combine(avatares, "novo.png"), "x");
        Exigir(Manutencao.LimparAvatares(avatares, agora) == 1, "devia apagar só o avatar de 91 dias");
        Exigir(!File.Exists(Path.Combine(avatares, "meu.png")) && File.Exists(Path.Combine(avatares, "meu.tira")) && File.Exists(Path.Combine(avatares, "novo.png")), "o de 89 dias e o novo ficam");
        Exigir(Manutencao.LimparAvatares(Path.Combine(Tmp, "nao-existe"), agora) == 0 && Manutencao.LimparAvatares(null, agora) == 0, "pasta que não existe ou nula não pode dar erro");

        // log: 30 dias; nunca o desta abertura nem o que ainda falta enviar
        string logs = Path.Combine(Tmp, "logs-prazo");
        Directory.CreateDirectory(logs);
        string F(string nome, int dias) { string c = Path.Combine(logs, nome); File.WriteAllText(c, "x"); File.SetLastWriteTime(c, agora.AddDays(-dias)); return c; }
        string velho = F("sessao-velho.log", 31), novo = F("sessao-novo.log", 29), atual = F("sessao-atual.log", 40), pendente = F("sessao-pendente.log", 45), outro = F("outro.txt", 45);
        int apagados = Manutencao.ApagarLogsVelhos(logs, atual, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pendente }, agora);
        Exigir(apagados == 1 && !File.Exists(velho), "devia apagar só o log de 31 dias");
        Exigir(File.Exists(novo) && File.Exists(atual) && File.Exists(pendente) && File.Exists(outro), "o log de 29 dias, o desta abertura, o pendente e o que não é sessao-*.log ficam");
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
        Caso("worker-cifra-no-pc-o-que-sobe-e-o-texto-em-claro-e-o-cursor-segue-o-arquivo-cifrado", () =>
        {
            // o servidor falso confirma pelo tamanho do que recebeu; o Worker de verdade confirma pelo `fim` que o app mandou. Só o Worker prova o cursor sobre o arquivo cifrado.
            using var _ = CifraLigada();
            var s = SessaoCifrada(300, 40);
            Exigir(File.ReadAllText(s.E.LogPath!).StartsWith(CofreLocal.Prefixo), "o log do teste devia estar cifrado");
            using var envio = Criar(url, tokenConta, s);
            envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>()); envio.Agora();
            Exigir(Esperar(() => Converge(s), 30_000), "não confirmou tudo: " + Estado(s));
            envio.Fechar(TimeSpan.FromSeconds(3));
            Exigir(Esperar(() => Converge(s), 10_000), "depois de fechar ficou pendente: " + Estado(s));
            ExigirIgualNoWorker(url, token, envio.Pc, s, cifradoNoPc: true);
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

    static void ExigirIgualNoWorker(string url, string token, string pc, SessaoLocal s, bool cifradoNoPc = false)
    {
        using var http = new HttpClient();
        foreach (var (nome, caminho) in s.Arquivos())
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{url}/telemetria/arquivo?pc={pc}&sessao={s.Id}&arquivo={Uri.EscapeDataString(nome)}");
            req.Headers.Add("x-streemu-token", token);
            using var resp = http.Send(req);
            Exigir(resp.IsSuccessStatusCode, $"{nome}: leitura respondeu {(int)resp.StatusCode}");
            // o Worker guarda o que o app mandou: quadros cifrados (spec 018, A9). Só a chave de teste abre; o resto é o gzip de vários membros de sempre.
            byte[] guardado = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            Exigir(guardado.Length > 4 && Encoding.ASCII.GetString(guardado, 0, 4) == "SEC1", $"{nome}: o Worker não guardou um envelope cifrado");
            using var gz = new System.IO.Compression.GZipStream(new MemoryStream(ServidorFalso.Chave.Decifrar(guardado)), System.IO.Compression.CompressionMode.Decompress);
            var ms = new MemoryStream();
            gz.CopyTo(ms);
            byte[] disco = Ler(caminho);
            // arquivo cifrado no PC (A8): o servidor recebe o texto em claro (a cifra do envio é a A9) e o cursor é a posição no arquivo cifrado
            byte[] esperado = cifradoNoPc ? Encoding.UTF8.GetBytes(CofreLocal.DecifrarLinhas(disco)) : disco;
            Exigir(esperado.AsSpan().SequenceEqual(ms.ToArray()), $"{nome}: o Worker devolveu {ms.Length} bytes diferentes dos {esperado.Length} esperados");
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
        Envio.EsperaEssencial = TimeSpan.FromSeconds(20);
        Envio.EsperaEssencialAnonimo = (TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10));
        Envio.ReavaliarEssencial = TimeSpan.FromSeconds(10);
        string raiz = Pasta.Raiz;
        if (!Directory.Exists(raiz)) return;
        try { File.Delete(Path.Combine(raiz, "essencial-pendente.json")); } catch { }
        try { File.Delete(Path.Combine(raiz, "historico-envios.json")); } catch { } // o histórico de 30 dias (A7) não passa de um caso para o outro
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

    /// <summary>Todo arquivo da sessão confirmado até o fim (as séries por segundo, até o teto que um disparo liberou) e o índice de eventos em dia.</summary>
    static bool Converge(SessaoLocal s) =>
        s.Arquivos().All(a => s.Cursor(a.Nome) == s.Limite(a.Nome, Tam(a.Caminho))) && s.E.Indexados >= Tam(Path.Combine(s.Dir, SessaoLocal.Eventos));

    static string Estado(SessaoLocal s) =>
        string.Join(", ", s.Arquivos().Select(a => $"{a.Nome}: cursor {s.Cursor(a.Nome)} de {Tam(a.Caminho)} (limite {s.Limite(a.Nome, Tam(a.Caminho))})")) + $", indexados {s.E.Indexados}";

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
        envio.Modo = ModoEnvio.Desligado;
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
        s.E.Tetos["quadros.csv"] = long.MaxValue; // a série só sobe liberada por um disparo (A6): aqui o que se testa é a hora do conteúdo, não o disparo
        using (var envio = Criar(srv, s)) { envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>()); envio.Agora(); Exigir(Esperar(() => Converge(s), 20_000), "não convergiu: " + Estado(s)); envio.Fechar(TimeSpan.FromSeconds(1)); }
        var q = srv.Horas("quadros.csv");
        Exigir(q.Count >= 1 && q.All(x => x.H0 == null && x.H1 == null), "arquivo sem hora no conteúdo devia ir com h0/h1 nulos");
        // a âncora vai para o sessao.json e volta na próxima abertura
        long ancora = s.HoraConteudo(SessaoLocal.Log);
        var reaberta = SessaoLocal.Abrir(s.Dir)!;
        Exigir(reaberta.HoraConteudo(SessaoLocal.Log) == ancora && ancora != s.E.Inicio, "a âncora não sobreviveu ao sessao.json");
    }

    // ---------- gravador local e "Ver o que é enviado" (spec 018, A6 e A7; plano 018, A-III) ----------

    /// <summary>A série da primeira live da sessão (uma série por live: quadros-N.csv; o nome sem número é o formato antigo).</summary>
    const string Q1 = "quadros-1.csv";
    static string Quadros(SessaoLocal s) => Path.Combine(s.Dir, Q1);
    static string ChaveQuadros(SessaoLocal s) => $"{s.Id}|{Q1}";
    static string LinhasDeQuadros(int de, int ate) => string.Join("\n", Enumerable.Range(de, ate - de).Select(i => $"{i},{i * 2}"));

    /// <summary>Sessão com uma série por segundo de 30 linhas, um envio ligado nela e o gravador local armado; o que não é série já subiu.</summary>
    static (SessaoLocal S, Envio Envio, GravadorLocal G) SessaoComSerie(ServidorFalso srv, Func<SessaoLocal, Envio>? criar = null)
    {
        var s = Sessao(5, 2);
        s.Acrescentar(Q1, LinhasDeQuadros(0, 30));
        Gravador.Descarregar(TimeSpan.FromSeconds(10));
        var envio = criar?.Invoke(s) ?? Criar(srv, s);
        var g = new GravadorLocal(envio);
        g.LiveComecou("propria"); // a série é da live: sem live aberta um disparo não tem o que liberar
        envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>()); envio.Agora();
        Exigir(Esperar(() => Converge(s), 20_000), "o resto da sessão não subiu: " + Estado(s));
        return (s, envio, g);
    }

    static bool SerieSubiu(SessaoLocal s) => Tam(Quadros(s)) > 0 && s.Cursor(Q1) == Tam(Quadros(s));

    static void GravadorSerieSemDisparoFicaNoPc()
    {
        using var srv = new ServidorFalso();
        var (s, envio, _) = SessaoComSerie(srv);
        using var _e = envio;
        Thread.Sleep(1500); envio.Agora(); Thread.Sleep(1500); // mais uma rodada: sem disparo a série continua parada
        Exigir(srv.Tamanho(ChaveQuadros(s)) == 0 && s.Cursor(Q1) == 0, "a série subiu sem nenhum disparo");
        Exigir(srv.PecasAceitas().All(p => p.Arq != Q1), "chegou peça da série sem disparo");
        var previa = envio.Previa();
        Exigir(previa.Any(i => i.Arquivo == Q1 && !i.Sobe && i.Bytes == Tam(Quadros(s))), "a prévia devia mostrar a série como 'fica só no PC': " + string.Join(" | ", previa.Select(i => $"{i.Arquivo} {i.Bytes} {i.Sobe}")));
        Exigir(previa.All(i => i.Arquivo != Q1 || !i.Sobe), "a prévia diz que a série sobe sem disparo");
        envio.Fechar(TimeSpan.FromSeconds(1));
        Exigir(s.Cursor(Q1) == 0 && srv.Tamanho(ChaveQuadros(s)) == 0, "o último envio ao fechar levou a série sem disparo");
    }

    static void GravadorMarcacaoLiberaASerieComPacote()
    {
        using var srv = new ServidorFalso();
        var (s, envio, _) = SessaoComSerie(srv);
        using var _e = envio;
        s.Evento("marcacao", new JsonObject { ["origem"] = "teste" });
        Exigir(Esperar(() => SerieSubiu(s), 20_000), "a marcação não liberou a série: " + Estado(s));
        string? pacote = s.Pacote(Q1);
        Exigir(pacote != null && System.Text.RegularExpressions.Regex.IsMatch(pacote, "^[A-Za-z0-9_-]{6,64}$"), "pacote ausente ou fora da regra do Worker: " + pacote);
        Exigir(s.Pacote(SessaoLocal.NomeSincronia(1)) == pacote, "os dois arquivos da live dividem o pacote");
        var q = srv.PecasAceitas().Where(p => p.Arq == Q1).ToList();
        Exigir(q.Count >= 1 && q.All(p => p.Pacote == pacote), "as peças da série precisam levar o pacote: " + string.Join(",", q.Select(p => p.Pacote)));
        Exigir(srv.PecasAceitas().Where(p => p.Arq != Q1).All(p => p.Pacote == null), "peça que não é série levou pacote (o Apagar levaria eventos de outro assunto)");
        Exigir(Esperar(() => HistoricoEnvios.Listar().Count == 1 && HistoricoEnvios.Listar()[0].Bytes == Tam(Quadros(s)), 5_000), "o histórico não registrou o envio: " + HistoricoEnvios.Listar().Count);
        var h = HistoricoEnvios.Listar()[0];
        Exigir(h.Pacote == pacote && h.Motivo == nameof(Disparo.Marcacao) && h.Modo == nameof(ModoEnvio.Completo) && !h.Visto && h.Arquivos.SequenceEqual(new[] { Q1 }), $"entrada do histórico errada: {h.Pacote}/{h.Motivo}/{h.Modo}/{h.Visto}/{string.Join(",", h.Arquivos)}");
        Exigir(HistoricoEnvios.TemNovo(), "o ponto do botão Diagnóstico devia acender");
        Exigir(Esperar(() => Converge(s), 10_000), "não convergiu: " + Estado(s));
        ExigirIgual(srv, s);
        envio.Fechar(TimeSpan.FromSeconds(1));
    }

    static void GravadorFimDaLiveCompletaNoMesmoPacote()
    {
        using var srv = new ServidorFalso();
        var (s, envio, g) = SessaoComSerie(srv);
        using var _e = envio;
        s.Evento("marcacao");
        Exigir(Esperar(() => SerieSubiu(s), 20_000), "a marcação não liberou a série: " + Estado(s));
        string pacote = s.Pacote(Q1)!; long primeiro = Tam(Quadros(s));
        Exigir(s.E.LiveMarcada, "a live aberta devia ficar marcada para o complemento");
        s.Acrescentar(Q1, LinhasDeQuadros(30, 60)); Gravador.Descarregar(TimeSpan.FromSeconds(10));
        envio.Agora(); Thread.Sleep(2500);
        Exigir(s.Cursor(Q1) == primeiro, $"a série passou do teto com a live ainda aberta ({s.Cursor(Q1)} de {primeiro})");
        g.LiveAcabou("propria");
        Exigir(Esperar(() => SerieSubiu(s) && Tam(Quadros(s)) > primeiro, 20_000), "o fim da live não completou a série: " + Estado(s));
        var q = srv.PecasAceitas().Where(p => p.Arq == Q1).ToList();
        Exigir(q.Count >= 2 && q.All(p => p.Pacote == pacote), "o complemento precisa ser do mesmo pacote: " + string.Join(",", q.Select(p => p.Pacote)));
        Exigir(Esperar(() => HistoricoEnvios.Listar().Count == 1 && HistoricoEnvios.Listar()[0].Bytes == Tam(Quadros(s)), 5_000), "o histórico devia ter UM pacote com a soma dos dois envios: " + string.Join(",", HistoricoEnvios.Listar().Select(e => e.Bytes)));
        Exigir(!s.E.LiveMarcada, "depois do complemento a live deixa de estar marcada");
        Exigir(Esperar(() => Converge(s), 10_000), "não convergiu: " + Estado(s));
        ExigirIgual(srv, s);
        envio.Fechar(TimeSpan.FromSeconds(1));
    }

    static void GravadorAberturaSeguinteECompletarAoFechar()
    {
        // app que morreu (ou fechou) no meio de uma live marcada: na abertura seguinte o resto da série sobe como complemento
        var anterior = Sessao(3, 1); anterior.E.LiveMarcada = true; anterior.Salvar();
        GravadorLocal.CompletarSessaoAnterior(anterior);
        Exigir(!anterior.E.LiveMarcada && anterior.E.TetoPendente, "a sessão anterior com live marcada devia liberar o resto da série");
        Exigir(SessaoLocal.Abrir(anterior.Dir)!.E.TetoPendente, "a liberação não foi salva no sessao.json");
        var outra = Sessao(3, 1); GravadorLocal.CompletarSessaoAnterior(outra);
        Exigir(!outra.E.TetoPendente, "a sessão sem live marcada não devia liberar nada");
        // fechar o app com a live aberta e marcada faz o mesmo antes do último envio
        using var srv = new ServidorFalso();
        var s = Sessao(3, 1);
        using var envio = Criar(srv, s);
        var g = new GravadorLocal(envio);
        g.LiveComecou("propria"); Exigir(g.Disparar(Disparo.Marcacao), "a marcação devia valer no Completo");
        s.E.TetoPendente = false; // como se a rodada já tivesse aplicado o primeiro teto
        g.EncerrarTudo();
        Exigir(!s.E.LiveMarcada && s.E.TetoPendente, "ao fechar, a live marcada devia completar a série");
    }

    static void GravadorPermiteConformeOEstado()
    {
        foreach (Disparo d in Enum.GetValues<Disparo>())
        {
            Exigir(GravadorLocal.Permite(ModoEnvio.Completo, d), $"Completo devia permitir {d}");
            Exigir(GravadorLocal.Permite(ModoEnvio.Anonimo, d) == (d != Disparo.Pedido), $"Anônimo: {d} devia {(d == Disparo.Pedido ? "não valer (sem o pedido da sala)" : "valer")}");
            Exigir(!GravadorLocal.Permite(ModoEnvio.Desligado, d), $"Desligado nunca sobe sozinho: {d}");
            Exigir(GravadorLocal.Permite(ModoEnvio.Completo, d, soQuandoMarcar: true) == (d == Disparo.Marcacao), $"só-quando-marcar no Completo: {d}");
            Exigir(GravadorLocal.Permite(ModoEnvio.Anonimo, d, soQuandoMarcar: true) == (d == Disparo.Marcacao), $"só-quando-marcar no Anônimo: {d}");
        }
        using var srv = new ServidorFalso();
        // Anônimo: o pedido de um amigo não vale, a marcação vale
        var a = Sessao(3, 1); a.E.Reduzida = true; a.Salvar();
        using var ea = EnvioEssencial(srv, a, ModoEnvio.Anonimo);
        var ga = new GravadorLocal(ea);
        Exigir(!ga.Disparar(Disparo.Pedido) && !a.E.TetoPendente, "o pedido 'agora' não vale no Anônimo");
        Exigir(ga.Disparar(Disparo.Marcacao) && a.E.TetoPendente, "a marcação devia valer no Anônimo");
        // Desligado: nem a marcação sobe sozinha
        var d0 = Sessao(3, 1); d0.E.NaoEnviar = true; d0.Salvar();
        using var ed = EnvioEssencial(srv, d0, ModoEnvio.Desligado);
        var gd = new GravadorLocal(ed);
        gd.LiveComecou("propria");
        Exigir(!gd.Disparar(Disparo.Marcacao) && !d0.E.TetoPendente && d0.E.Pacotes.Count == 0, "no Desligado nada sobe sozinho");
        d0.Evento("marcacao");
        Exigir(!d0.E.TetoPendente, "o evento de marcação no Desligado não pode liberar a série");
    }

    static void GravadorSoQuandoMarcar()
    {
        using var srv = new ServidorFalso();
        var s = Sessao(3, 1);
        using var envio = Criar(srv, s);
        var g = new GravadorLocal(envio) { SoQuandoMarcar = () => true };
        g.LiveComecou("propria");
        Exigir(!g.Disparar(Disparo.FalhaGrave) && !g.Disparar(Disparo.Pedido) && !g.Disparar(Disparo.ResumoComProblema), "com 'só quando eu marcar' só a marcação vale");
        s.Evento("ui_travada");
        Exigir(!s.E.TetoPendente && s.E.Pacotes.Count == 0, "a interface travada não podia liberar a série");
        Exigir(g.Disparar(Disparo.Marcacao) && s.E.TetoPendente, "a marcação devia valer");
    }

    static void GravadorDisparosPelosEventos()
    {
        using var srv = new ServidorFalso();
        var s = Sessao(3, 1);
        using var envio = Criar(srv, s);
        var g = new GravadorLocal(envio);
        g.LiveComecou("propria");
        s.Evento("falha", new JsonObject { ["tipo_falha"] = "outra" });
        Exigir(!s.E.TetoPendente, "a falha 'outra' não é falha grave");
        s.Evento("falha", new JsonObject { ["tipo_falha"] = "captura_travada" });
        Exigir(s.E.TetoPendente && s.Motivo(Q1) == nameof(Disparo.FalhaGrave) && s.Pacote(Q1) != null, "a falha grave devia disparar: " + s.Motivo(Q1));
        string primeiro = s.Pacote(Q1)!;
        s.Evento("ui_travada"); // um segundo disparo antes de a rodada aplicar o primeiro entra no MESMO pacote
        Exigir(s.Pacote(Q1) == primeiro && s.E.Pacotes.Values.Distinct().Count() == 1, "o segundo disparo abriu outro pacote");
        s.FixarTetos();
        // live_resumo: só com problema que durou (o disparo 3 é desligado por padrão até a P8; aqui ligado)
        GravadorLocal.Disparo3Ligado = true;
        s.Evento("live_resumo", ResumoDeTeste(CaminhoMidia.Host, Enumerable.Range(0, 3).Select(i => (i, 60, 0, 25, 0)).ToArray()));
        Exigir(!s.E.TetoPendente, "um resumo sem problema não pode disparar");
        s.Evento("live_resumo", ResumoDeTeste(CaminhoMidia.Host, Enumerable.Range(0, 3).Select(i => (i, 60, 0, 6, 0)).ToArray()));
        Exigir(s.E.TetoPendente && s.Motivo(Q1) == nameof(Disparo.FalhaGrave), "o pacote já aberto guarda o motivo do primeiro disparo: " + s.Motivo(Q1));
        Exigir(s.E.TetoPendente, "um resumo com 3 minutos ruins devia disparar");
        GravadorLocal.Disparo3Ligado = false;
    }

    /// <summary>Um <c>live_resumo</c> mínimo: por minuto (m, segundos medidos, segundos de pausa, mediana de quadros novos, pior desvio de áudio e vídeo em ms).</summary>
    static JsonObject ResumoDeTeste(string lado, (int M, int S, int Pausa, int Quadros, int Av)[] minutos)
    {
        var arr = new JsonArray();
        foreach (var x in minutos)
            arr.Add(new JsonObject { ["m"] = x.M, ["s"] = x.S, ["pausa_s"] = x.Pausa, ["quadros_novos_mediana"] = x.Quadros, ["av_pior_ms"] = x.Av });
        return new JsonObject { ["lado"] = lado, ["minutos"] = arr };
    }

    static void GravadorResumoComProblemaLimiares()
    {
        double fps = GravadorLocal.FpsMinimo; int av = GravadorLocal.AvMaximoMs, seg = GravadorLocal.MinutosSeguidos; bool ligado = GravadorLocal.Disparo3Ligado;
        try
        {
            GravadorLocal.Disparo3Ligado = false;
            Exigir(!GravadorLocal.ResumoComProblema(ResumoDeTeste(CaminhoMidia.Host, [(0, 60, 0, 6, 0), (1, 60, 0, 6, 0), (2, 60, 0, 6, 0)])), "o disparo 3 fica desligado por padrão até a calibragem (P8)");
            GravadorLocal.Disparo3Ligado = true;
            GravadorLocal.FpsMinimo = 10; GravadorLocal.AvMaximoMs = 1500; GravadorLocal.MinutosSeguidos = 3; // os padrões, fixos para o caso não depender do ambiente
            const string H = CaminhoMidia.Host, V = CaminhoMidia.Viewer;
            Exigir(GravadorLocal.ResumoComProblema(ResumoDeTeste(H, [(0, 60, 0, 6, 0), (1, 60, 0, 6, 0), (2, 60, 0, 6, 0)])), "3 minutos seguidos com 6 quadros por segundo devia disparar");
            Exigir(!GravadorLocal.ResumoComProblema(ResumoDeTeste(H, [(0, 60, 0, 6, 0), (1, 60, 0, 6, 0)])), "2 minutos ruins ainda é pouco");
            Exigir(!GravadorLocal.ResumoComProblema(ResumoDeTeste(H, [(0, 60, 0, 6, 0), (1, 60, 0, 6, 0), (2, 60, 0, 25, 0), (3, 60, 0, 6, 0)])), "minutos ruins separados por um bom não somam");
            Exigir(!GravadorLocal.ResumoComProblema(ResumoDeTeste(H, [(0, 60, 0, 6, 0), (1, 60, 0, 6, 0), (3, 60, 0, 6, 0)])), "um minuto sem amostra no meio recomeça a conta");
            Exigir(!GravadorLocal.ResumoComProblema(ResumoDeTeste(H, [(0, 20, 0, 6, 0), (1, 20, 0, 6, 0), (2, 20, 0, 6, 0)])), "minuto com menos de 30 s medidos não prova nada");
            Exigir(!GravadorLocal.ResumoComProblema(ResumoDeTeste(H, [(0, 60, 40, 0, 0), (1, 60, 40, 0, 0), (2, 60, 40, 0, 0)])), "minuto quase todo em pausa de privacidade não conta");
            Exigir(!GravadorLocal.ResumoComProblema(ResumoDeTeste(H, [(0, 60, 0, 25, 9999), (1, 60, 0, 25, 9999), (2, 60, 0, 25, 9999)])), "quem transmite: o desvio de áudio e vídeo não conta, só os quadros");
            Exigir(GravadorLocal.ResumoComProblema(ResumoDeTeste(V, [(0, 60, 0, 25, 2000), (1, 60, 0, 25, 2000), (2, 60, 0, 25, 2000)])), "quem assiste: 3 minutos com desvio acima de 1500 ms devia disparar");
            Exigir(!GravadorLocal.ResumoComProblema(ResumoDeTeste(V, [(0, 60, 0, 1, 900), (1, 60, 0, 1, 900), (2, 60, 0, 1, 900)])), "quem assiste: poucos quadros não é problema dele, só o desvio conta");
            Exigir(!GravadorLocal.ResumoComProblema(new JsonObject { ["lado"] = H }), "resumo sem minutos");
            GravadorLocal.MinutosSeguidos = 2;
            Exigir(GravadorLocal.ResumoComProblema(ResumoDeTeste(H, [(0, 60, 0, 6, 0), (1, 60, 0, 6, 0)])), "a quantidade de minutos é configurável (STREEMU_GRAVADOR_MINUTOS)");
        }
        finally { GravadorLocal.FpsMinimo = fps; GravadorLocal.AvMaximoMs = av; GravadorLocal.MinutosSeguidos = seg; GravadorLocal.Disparo3Ligado = ligado; }
    }

    static void GravadorSerieGuardaSeteDiasNoPc()
    {
        // série passou de 7 dias: sai do PC tenha subido ou não, e a sessão pendente fica; a de 5 dias com série fica mais que as sem série (3 dias)
        var velhaPendente = Fabricar("r1", 24 * 8, 1000, "pendente");
        var recenteComSerie = Fabricar("r2", 24 * 5, 1000, "concluida");
        var recenteSemSerie = Fabricar("r3", 24 * 5, 1000, "concluida");
        var velhaConcluida = Fabricar("r4", 24 * 8, 1000, "concluida");
        var atual = Fabricar("r5", 0, 1000, "aberta");
        foreach (var x in new[] { velhaPendente, recenteComSerie, velhaConcluida, atual }) File.WriteAllText(Quadros(x), LinhasDeQuadros(0, 10));
        // uma série por live: as de outras lives e o formato antigo (sem número) saem junto; arquivo que só parece série não
        foreach (var extra in new[] { "quadros-2.csv", "sincronia-1.ndjson", "sincronia-12.ndjson", "quadros.csv", "sincronia.ndjson" }) File.WriteAllText(Path.Combine(velhaPendente.Dir, extra), "1,2\n");
        File.WriteAllText(Path.Combine(velhaPendente.Dir, "quadros-x.csv"), "1,2\n");
        Manutencao.Limpar(null, atual);
        Gravador.Descarregar(TimeSpan.FromSeconds(10));
        Exigir(new[] { "quadros-2.csv", "sincronia-1.ndjson", "sincronia-12.ndjson", "quadros.csv", "sincronia.ndjson" }.All(n => !File.Exists(Path.Combine(velhaPendente.Dir, n))), "a série de cada live (e a do formato antigo) devia sair com a de mais de 7 dias");
        Exigir(File.Exists(Path.Combine(velhaPendente.Dir, "quadros-x.csv")), "arquivo que não é série (quadros-x.csv) não podia sair");
        Exigir(Existe(velhaPendente) && !File.Exists(Quadros(velhaPendente)) && File.Exists(Path.Combine(velhaPendente.Dir, "dados.csv")), "a série de mais de 7 dias devia sair, e só ela");
        Exigir(Existe(recenteComSerie) && File.Exists(Quadros(recenteComSerie)), "a sessão de 5 dias com série devia ficar (a guarda dela é de 7)");
        Exigir(!Existe(recenteSemSerie), "a sessão enviada de 5 dias sem série devia sair (a guarda é de 3)");
        Exigir(!Existe(velhaConcluida), "a sessão enviada de 8 dias devia sair inteira");
        Exigir(Existe(atual) && File.Exists(Quadros(atual)), "a sessão atual nunca perde a série");
    }

    static void GravadorSerieUmaPorLiveENomes()
    {
        // o nome da série: por live (com número) ou o formato antigo; nada que só pareça
        foreach (var n in new[] { "quadros.csv", "quadros-1.csv", "quadros-12.csv", "sincronia.ndjson", "sincronia-3.ndjson", SessaoLocal.NomeQuadros(2), SessaoLocal.NomeSincronia(9) })
            Exigir(SessaoLocal.Serie(n), $"{n} devia ser série");
        foreach (var n in new[] { "quadros-.csv", "quadros-x.csv", "quadros-1a.csv", "quadros-1.csv.tmp", "quadros-1234567.csv", "sincronia-1.csv", "relatorios/quadros-1.csv", "log.txt", "eventos.ndjson", "quadros.ndjson" })
            Exigir(!SessaoLocal.Serie(n), $"{n} não é série");

        using var srv = new ServidorFalso();
        var (s, envio, g) = SessaoComSerie(srv); // a live 1 já está aberta
        using var _e = envio;
        Exigir(s.ArquivoQuadros == Q1 && s.ArquivoSincronia == SessaoLocal.NomeSincronia(1), "a primeira live usa os arquivos 1");
        s.Evento("marcacao");
        Exigir(Esperar(() => SerieSubiu(s), 20_000), "a marcação da live 1 não liberou a série: " + Estado(s));
        string p1 = s.Pacote(Q1)!;
        g.LiveAcabou("propria");
        Exigir(Esperar(() => Converge(s), 20_000), "não convergiu depois da live 1: " + Estado(s));
        // live 2 da mesma sessão: arquivos novos; sem disparo a série dela fica no PC
        g.LiveComecou("propria");
        string q2 = SessaoLocal.NomeQuadros(2);
        Exigir(s.ArquivoQuadros == q2, "a segunda live devia abrir " + q2 + ", abriu " + s.ArquivoQuadros);
        s.Acrescentar(q2, LinhasDeQuadros(0, 20)); Gravador.Descarregar(TimeSpan.FromSeconds(10));
        envio.Agora(); Thread.Sleep(2000);
        string chave2 = $"{s.Id}|{q2}";
        Exigir(srv.Tamanho(chave2) == 0 && s.Pacote(q2) == null && s.Cursor(q2) == 0, "a série da live 2 subiu sem disparo");
        Exigir(s.Pacote(Q1) == p1, "o pacote da live 1 não pode mudar");
        // marcou na live 2: pacote próprio (o Apagar da segunda não depende da primeira)
        s.Evento("marcacao");
        Exigir(Esperar(() => Tam(Path.Combine(s.Dir, q2)) > 0 && s.Cursor(q2) == Tam(Path.Combine(s.Dir, q2)), 20_000), "a marcação da live 2 não liberou a série dela: " + Estado(s));
        string p2 = s.Pacote(q2)!;
        Exigir(p2 != null && p2 != p1, $"cada live marcada precisa do seu pacote ({p1} / {p2})");
        var pecas = srv.PecasAceitas();
        Exigir(pecas.Where(p => p.Arq == Q1).All(p => p.Pacote == p1) && pecas.Where(p => p.Arq == q2).All(p => p.Pacote == p2) && pecas.Any(p => p.Arq == q2), "as peças de cada live precisam levar o pacote dela: " + string.Join(",", pecas.Where(p => SessaoLocal.Serie(p.Arq)).Select(p => p.Arq + "=" + p.Pacote)));
        Exigir(Esperar(() => HistoricoEnvios.Listar().Any(e => e.Pacote == p1) && HistoricoEnvios.Listar().Any(e => e.Pacote == p2 && e.Arquivos.SequenceEqual(new[] { q2 })), 5_000), "o histórico devia ter um pacote por live");
        envio.Fechar(TimeSpan.FromSeconds(1));
    }

    static void GravadorDesligadoSoSobeComOCliqueEAnonimo()
    {
        using var srv = new ServidorFalso();
        Anonimizador.Reiniciar();
        var s = Sessao(5, 2); s.E.NaoEnviar = true; s.Salvar();
        s.Acrescentar(Q1, LinhasDeQuadros(0, 30));
        Gravador.Descarregar(TimeSpan.FromSeconds(10));
        using var envio = EnvioEssencial(srv, s, ModoEnvio.Desligado);
        var g = new GravadorLocal(envio);
        g.LiveComecou("propria");
        envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>()); envio.Agora();
        Thread.Sleep(1500);
        Exigir(!g.Disparar(Disparo.Marcacao) && srv.PecasAceitas().Length == 0, "no Desligado nada sobe sem o clique");
        Exigir(g.ArquivosDaMarcacaoDesligada().SequenceEqual(new[] { SessaoLocal.Log, SessaoLocal.Eventos, Q1 }), "a lista da janela: " + string.Join(",", g.ArquivosDaMarcacaoDesligada()));
        // nomes dos amigos ainda não carregados: recusa com o motivo, sem enviar
        var (ok0, msg0) = g.EnviarMarcacaoDesligada();
        Exigir(!ok0 && msg0.Length > 0 && srv.PecasAceitas().Length == 0, "sem os nomes carregados não devia enviar: " + msg0);
        Anonimizador.MarcarPronto();
        var (ok, msg) = g.EnviarMarcacaoDesligada();
        Exigir(ok, "o envio único devia dar certo: " + msg);
        var pecas = srv.PecasAceitas();
        var nomes = pecas.Select(p => p.Arq).Distinct().ToList();
        Exigir(nomes.Count > 0 && nomes.All(n => n is SessaoLocal.Log or SessaoLocal.Eventos || n == Q1), "só o log, os eventos e a série da live: " + string.Join(",", nomes));
        Exigir(nomes.Contains(Q1) && pecas.All(p => p.Pacote != null), "todo arquivo leva pacote (para o Apagar): " + string.Join(",", pecas.Select(p => p.Arq + "=" + p.Pacote)));
        Exigir(pecas.First(p => p.Arq == Q1).Pacote != pecas.First(p => p.Arq != Q1).Pacote, "a série da live tem pacote próprio, separado do log");
        Exigir(srv.Credenciais().All(c => c == "bilhete:" + ServidorFalso.BilheteAceito), "o envio único é anônimo: só o bilhete, nunca o token: " + string.Join(",", srv.Credenciais().Distinct()));
        Exigir(envio.Modo == ModoEnvio.Desligado && !s.E.TetoPendente, "o envio único não muda o modo nem liga nada para depois");
        string pacoteSerie = s.Pacote(Q1)!;
        Exigir(HistoricoEnvios.Listar().Any(e => e.Pacote == pacoteSerie && e.Modo == nameof(ModoEnvio.Anonimo)), "o histórico devia ter o pacote como anônimo, para o Apagar");
        // depois do clique o laço continua parado: nada novo sobe sozinho
        int antes = srv.PecasAceitas().Length;
        s.Acrescentar(Q1, LinhasDeQuadros(30, 40)); Gravador.Descarregar(TimeSpan.FromSeconds(10));
        envio.Agora(); Thread.Sleep(1500);
        Exigir(srv.PecasAceitas().Length == antes, "o laço do Desligado voltou a enviar sozinho");
        Anonimizador.Reiniciar();
    }

    static void GravadorHistoricoDeEnvios()
    {
        HistoricoEnvios.Registrar("pacote-aaaaaa", "Marcacao", "Completo", "s1", Q1, 100);
        HistoricoEnvios.Registrar("pacote-aaaaaa", "Marcacao", "Completo", "s1", "sincronia.ndjson", 50); // complemento do mesmo pacote
        HistoricoEnvios.Registrar("pacote-bbbbbb", "FalhaGrave", "Anonimo", "s2", Q1, 10);
        HistoricoEnvios.Registrar("pacote-cccccc", "Marcacao", "Completo", "s3", Q1, 0); // nada subiu: nada registrado
        var l = HistoricoEnvios.Listar();
        Exigir(l.Count == 2, $"esperava 2 pacotes, vieram {l.Count}");
        var a = l.First(e => e.Pacote == "pacote-aaaaaa");
        Exigir(a.Bytes == 150 && a.Arquivos.Count == 2, $"o complemento devia somar no mesmo pacote: {a.Bytes} bytes, {a.Arquivos.Count} arquivos");
        Exigir(HistoricoEnvios.TemNovo(), "devia haver pacote não visto");
        HistoricoEnvios.MarcarVistos();
        Exigir(!HistoricoEnvios.TemNovo() && HistoricoEnvios.Listar().All(e => e.Visto), "abrir o histórico devia marcar tudo como visto");
        HistoricoEnvios.Registrar("pacote-aaaaaa", "Marcacao", "Completo", "s1", Q1, 5);
        Exigir(HistoricoEnvios.TemNovo(), "um complemento é novidade de novo");
        HistoricoEnvios.Remover("pacote-aaaaaa");
        Exigir(HistoricoEnvios.Listar().Select(e => e.Pacote).SequenceEqual(new[] { "pacote-bbbbbb" }), "Remover devia tirar só o pacote apagado");
        // passou de 30 dias: sai sozinho
        string arq = Path.Combine(Pasta.Raiz, "historico-envios.json");
        var json = JsonNode.Parse(File.ReadAllText(arq))!.AsArray();
        json[0]!["Quando"] = DateTime.UtcNow.AddDays(-31).ToString("yyyy-MM-ddTHH:mm:ssZ");
        File.WriteAllText(arq, json.ToJsonString());
        Exigir(HistoricoEnvios.Listar().Count == 0, "o pacote de 31 dias devia sair do histórico");
        HistoricoEnvios.Apagar();
    }

    static void ApagarPacoteNoServidor()
    {
        using var srv = new ServidorFalso();
        var s = Sessao(3, 1);
        // identificado: token da conta + o PC
        using (var envio = EnvioEssencial(srv, s, ModoEnvio.Completo))
        {
            srv.ApagarIncompletos = 2; // o Worker apaga em etapas: o app repete até completo
            var (ok, msg) = envio.ApagarPacote("pacote-aaaaaa", nameof(ModoEnvio.Completo));
            Exigir(ok, "devia apagar: " + msg);
            var ap = srv.Apagados();
            Exigir(ap.Length == 3, $"esperava 3 pedidos (2 incompletos + 1 completo), vieram {ap.Length}");
            Exigir(ap.All(x => x.Cred == "token:" + ServidorFalso.TokenPadrao && x.Corpo["pacote"]!.GetValue<string>() == "pacote-aaaaaa" && x.Corpo["pc"]!.GetValue<string>() == envio.Pc && x.Corpo["chave"] == null), "o pedido identificado tem token, pacote e pc, e nada de chave: " + string.Join(" | ", ap.Select(x => x.Corpo.ToJsonString())));
        }
        // anônimo: a chave local + o bilhete (ou o token), nunca o pc identificado
        using (var envio = EnvioEssencial(srv, s, ModoEnvio.Anonimo))
        {
            var (ok, msg) = envio.ApagarPacote("pacote-bbbbbb", nameof(ModoEnvio.Anonimo));
            Exigir(ok, "devia apagar o anônimo: " + msg);
            var ult = srv.Apagados().Last();
            Exigir(ult.Cred == "bilhete:" + ServidorFalso.BilheteAceito && ult.Corpo["chave"] != null && ult.Corpo["pc"] == null, "o pedido anônimo usa chave e bilhete, sem pc: " + ult.Corpo.ToJsonString());
        }
        // sem conta: não faz o pedido e diz o que fazer
        int antes = srv.Apagados().Length;
        using (var envio = EnvioEssencial(srv, s, ModoEnvio.Completo, token: _ => null))
        {
            var (ok, msg) = envio.ApagarPacote("pacote-cccccc", nameof(ModoEnvio.Completo));
            Exigir(!ok && msg.Contains("conta") && srv.Apagados().Length == antes, "sem conta não devia haver pedido: " + msg);
        }
        // servidor recusa: a mensagem diz o motivo e o pacote continua
        srv.TokenAceito = "outro-token";
        using (var envio = EnvioEssencial(srv, s, ModoEnvio.Completo))
        {
            var (ok, msg) = envio.ApagarPacote("pacote-dddddd", nameof(ModoEnvio.Completo));
            Exigir(!ok && msg.Contains("401"), "o 401 devia aparecer na frase: " + msg);
        }
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
        Pseudonimos.Reiniciar(); // o "Ana" de Uma() é o primeiro amigo desta abertura: amigo-1
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
        Exigir(u["caminho"]!.GetValue<string>() == "direto" && u["amigo"]!.GetValue<string>() == "amigo-1" && u["transmissao"]!.GetValue<string>() == "live1", "identificação: " + u.ToJsonString());
        // quem transmite não tem perda para informar
        r.Amostrar(10_000, () => Uma("L2", Leit("Connected", 0, 0, 0, 0, assisto: false), "transmite"));
        r.Amostrar(15_000, () => Uma("L2", Leit("Connected", 1_250_000, 0, 0, 0, assisto: false, caminho: "meu_relay"), "transmite"));
        var t2 = LinhasRede(s).Last(x => x["ligacao"]!.GetValue<string>() == "L2");
        Exigir(t2["perda_pct"] == null && t2["caminho"]!.GetValue<string>() == "meu_relay" && t2["enviado_kbps"]!.GetValue<double>() == 2000, "transmissor: " + t2.ToJsonString());
        // só números e nomes de caminho: nada que pareça endereço
        Exigir(!l.Any(x => System.Text.RegularExpressions.Regex.IsMatch(x.ToJsonString(), @"\d+\.\d+\.\d+\.\d+")), "IP na série de rede");
    }

    static void RedeCaminhoEVolta()
    {
        var s = Sessao(0, 0);
        var r = new SerieRede(s);
        LeituraRede T(long tx, long volta, double perdaVolta, long trocas, long relayMs, bool conf = true) =>
            new("Connected", "meu_relay", 100, tx, 0, 0, 0, false, volta, perdaVolta, trocas, relayMs, conf, 80);
        r.Amostrar(0, () => Uma("T1", T(0, 1000, 0, 0, 0), "transmite"));
        // 5 s: 3 trocas, 4 s no relay (80%), 1000 -> 1500 pacotes de volta com 100 perdidos no total (6,25% do total de 1600)
        r.Amostrar(5000, () => Uma("T1", T(1000, 1500, 100.0 * 100 / 1600, 3, 4000, conf: false), "transmite"));
        var u = LinhasRede(s).Last(x => x["ligacao"]!.GetValue<string>() == "T1");
        Exigir(u["trocas_caminho"]!.GetValue<long>() == 3 && u["tempo_relay_pct"]!.GetValue<double>() == 80, "caminho no intervalo: " + u.ToJsonString());
        Exigir(u["caminho"]!.GetValue<string>() == "meu_relay" && !u["caminho_confirmado"]!.GetValue<bool>() && u["latencia_srt_ms"]!.GetValue<int>() == 80, "campos novos: " + u.ToJsonString());
        Exigir(u["perda_pct"] == null && Math.Abs(u["perda_recebendo_pct"]!.GetValue<double>() - 16.67) < 0.05 && u["pacotes_recebidos_volta"]!.GetValue<long>() == 500, "perda da volta: " + u.ToJsonString());
        // quem assiste não ganha o campo da volta e não inventa latência quando o app não informou
        r.Amostrar(10_000, () => Uma("V1", Leit("Connected", 0, 0, 0, 0)));
        r.Amostrar(15_000, () => Uma("V1", Leit("Connected", 1, 1, 10, 0)));
        var v = LinhasRede(s).Last(x => x["ligacao"]!.GetValue<string>() == "V1");
        Exigir(v["perda_recebendo_pct"] == null && v["latencia_srt_ms"] == null && v["perda_pct"] != null, "espectador: " + v.ToJsonString());
    }

    static void LatenciaCalculo()
    {
        Exigir(Streemu.Media.LatenciaSrt.Calcular(5, false) == 80, "rede local deve dar 80: " + Streemu.Media.LatenciaSrt.Calcular(5, false));
        Exigir(Streemu.Media.LatenciaSrt.Calcular(106, false) == 338, "106 ms direto: " + Streemu.Media.LatenciaSrt.Calcular(106, false));
        Exigir(Streemu.Media.LatenciaSrt.Calcular(130, true) == 410, "130 ms relay: " + Streemu.Media.LatenciaSrt.Calcular(130, true));
        Exigir(Streemu.Media.LatenciaSrt.Calcular(20, true) == 200, "piso do relay: " + Streemu.Media.LatenciaSrt.Calcular(20, true));
        Exigir(Streemu.Media.LatenciaSrt.Calcular(900, false) == 600, "teto de 600");
        Exigir(Streemu.Media.LatenciaSrt.Calcular(-1, false) == 380, "sem medida presume 120 ms: " + Streemu.Media.LatenciaSrt.Calcular(-1, false));
        Exigir(Streemu.Media.LatenciaSrt.Subir(400) == 600 && Streemu.Media.LatenciaSrt.Subir(800) == 1000 && Streemu.Media.LatenciaSrt.Subir(1000) == 1000, "subir x1,5 até 1000");
        // campo 2026-11-22 (notebook): conexão em 10 ms e ida e volta ainda não medida -> 80 ms, não os 380 que atrasavam a live
        Exigir(Streemu.Media.LatenciaSrt.Calcular(-1, false, 10) == 80, "conexão rápida sem medida: " + Streemu.Media.LatenciaSrt.Calcular(-1, false, 10));
        Exigir(Streemu.Media.LatenciaSrt.Calcular(-1, false, 169) == 380, "conexão lenta não baixa a presunção: " + Streemu.Media.LatenciaSrt.Calcular(-1, false, 169));
        Exigir(Streemu.Media.LatenciaSrt.Calcular(-1, true, 10) == 200, "pelo relay mantém o piso de 200 ms");
        Exigir(Streemu.Media.LatenciaSrt.Calcular(-1, false, -1) == 380, "sem tempo de conexão presume 120 ms");
    }

    /// <summary>Oferta de 30 FPS: o notebook em 60 (~35/s) recebe; o mesmo notebook com um jogo leve (~58/s) e a live em 30 FPS nunca recebem.</summary>
    static void SugestaoDe30FpsRegra()
    {
        var r = new Streemu.Media.SugestaoDe30Fps();
        int Segundos(int n, int fps, int quadros, string medida = "sensor") { int mudou = 0; for (int i = 0; i < n; i++) if (r.Observar(fps, quadros, medida)) mudou++; return mudou; }
        Segundos(24, 60, 35);
        Exigir(!r.Visivel, "10 s de abertura + 14 s abaixo ainda não bastam");
        Exigir(Segundos(1, 60, 35) == 1 && r.Visivel && r.Tipico == 35, "o 15º segundo abaixo mostra o card: " + r.Tipico);
        Segundos(9, 60, 58);
        Exigir(r.Visivel, "9 s bons não escondem");
        Exigir(Segundos(1, 60, 58) == 1 && !r.Visivel, "10 s bons seguidos escondem sozinho");
        r.Reiniciar();
        Segundos(200, 60, 58);
        Exigir(!r.Visivel, "jogo leve a 58/s nunca mostra");
        Segundos(200, 30, 12);
        Exigir(!r.Visivel, "em 30 FPS nunca mostra, nem com poucos quadros");
        r.Reiniciar();
        Segundos(10, 60, 35, "pausa"); Segundos(10, 60, 0, "sem_medida"); Segundos(10, 60, 175);
        Exigir(!r.Visivel, "pausa, sem medida e despejo do contador são neutros");
        Segundos(26, 60, 35);
        Exigir(r.Visivel, "depois dos neutros o card ainda aparece");
        r.Recusar();
        Segundos(100, 60, 20);
        Exigir(!r.Visivel, "recusou: não volta nesta live");
        r.Reiniciar();
        Segundos(40, 60, 20);
        Exigir(r.Visivel, "nova live recomeça e a oferta volta");
        // engasgos isolados (3 s a cada ~12 s, como no notebook) não acumulam
        r.Reiniciar(); Segundos(10, 60, 50);
        for (int i = 0; i < 10; i++) { Segundos(3, 60, 20); Segundos(9, 60, 50); }
        Exigir(!r.Visivel, "engasgos curtos com patamar bom não mostram");
        r.Reiniciar();
        Segundos(200, 90, 40); Segundos(200, 120, 40);
        Exigir(!r.Visivel, "90 e 120 FPS (beta) não recebem a oferta de 30");
        Exigir(Streemu.Media.Preset.Pick(0, 60).WithFps(30).Name == "Fonte30" && Streemu.Media.Preset.Pick(1080, 60).WithFps(30).Name == "1080p30", "Fonte e 1080p mantêm a altura");
        Exigir(Streemu.Media.Preset.All.First(x => x.Name == "2160p60").WithFps(30).Name == "2160p30", "2160p60 vira 2160p30");
        Exigir(Streemu.Media.Preset.All.First(x => x.Name == "1440p60 ultrawide").WithFps(30).Name == "1440p30 ultrawide", "ultrawide é mantido");
    }

    /// <summary>Replay do notebook de 2026-11-22: engasgos de 2–3 s que voltam ao mesmo ~35/s depois de reabrir.</summary>
    static void ReaberturaInutilReplay()
    {
        var r = new Streemu.Media.ReaberturaInutil();
        double t = 0;
        bool Passar(int segundos, int quadros) { bool inutil = false; for (int i = 0; i < segundos; i++) { t++; inutil |= r.Observar(t, quadros); } return inutil; }
        r.Reabriu(t, 35, 60);
        Exigir(!Passar(8, 5) && !r.Desistiu, "ainda dentro da janela de abertura");
        Exigir(Passar(8, 36) && !r.Desistiu, "1ª reabertura voltou ao mesmo patamar: inútil, mas ainda não desiste");
        r.Reabriu(t, 35, 60);
        Passar(20, 36);
        Exigir(r.Desistiu, "2 inúteis seguidas: o vigia para de reabrir nesta live");
        r.Reiniciar();
        Exigir(!r.Desistiu, "nova live recomeça");
        r.Reabriu(t, 3, 60); // captura realmente travada (~3/s) e a nova pega 58/s: ganho de verdade
        Exigir(!Passar(20, 58) && !r.Desistiu, "reabertura que resolve não conta");
        r.Reabriu(t, 17, 60);
        Passar(20, 40); // 17 -> 40 (>= 1,25x) mas ainda abaixo de 75%: ganhou
        Exigir(!r.Desistiu, "ganho de 1,25x ou mais conta como útil");
    }

    /// <summary>
    /// Replay da LIV-161 (2026-11-25): trava, reabre, volta a ~60/s em 12 s, trava de novo ~37 s depois. Cada reabertura "funciona", mas quem assiste para 2,4–5,5 s.
    /// O limite é por janela (4 em 10 min) e NÃO permanente: se a trava for de verdade, a live não pode ficar congelada até o fim.
    /// </summary>
    static void ReaberturaInutilLimitePorJanela()
    {
        // (a) o ciclo do campo: reabre a cada 37 s e depois de cada uma a captura volta saudável; a 5ª dentro de 10 min é bloqueada
        var r = new Streemu.Media.ReaberturaInutil();
        double t = 0;
        bool Passar(int segundos, int quadros) { bool inutil = false; for (int i = 0; i < segundos; i++) { t++; inutil |= r.Observar(t, quadros); } return inutil; }
        for (int i = 1; i <= 3; i++)
        {
            Exigir(!r.NoLimite(t), $"{i}ª reabertura ainda cabe");
            Exigir(!r.Reabriu(t, 5, 60), $"{i}ª reabertura não enche a janela");
            Exigir(!Passar(14, 59), "volta saudável: reabertura útil");
            Exigir(!r.Desistiu, $"{i}ª reabertura útil não faz desistir");
            t += 37 - 14;
        }
        Exigir(!r.NoLimite(t), "a 4ª ainda é permitida");
        Exigir(r.Reabriu(t, 5, 60), "a 4ª reabertura (~2 min) enche a janela e devolve true uma vez");
        Exigir(r.NoLimite(t) && !r.Desistiu, "janela cheia, mas não é desistência permanente");
        Exigir(r.ReaberturasRecentes(t) == 4 && r.SegundosDaJanela(t) is > 100 and < 120, $"conta e janela para o log: {r.ReaberturasRecentes(t)} em {r.SegundosDaJanela(t):0} s");
        Passar(14, 59);
        t += 23;
        Exigir(r.NoLimite(t), "a 5ª, ~37 s depois da 4ª e dentro de 10 min, é barrada na reabertura rápida (a sonda não é limitada)");
        Exigir(!r.Desistiu, "o bloqueio por janela não vira Desistiu");

        // (b) quando a reabertura mais antiga sai da janela, volta a poder reabrir
        // horários: 0, 37, 74, 111 -> a de 0 sai em t > 600; a de 37, em t > 637
        t = 600;
        Exigir(r.NoLimite(t), "exatamente 600 s depois da mais antiga ela ainda está na janela");
        t = 601;
        Exigir(!r.NoLimite(t), "601 s depois: a mais antiga saiu, volta a poder reabrir");
        Exigir(r.Reabriu(t, 5, 60) && r.NoLimite(t), "a reabertura que ocupa a vaga (3 antigas + a nova) enche a janela de novo e avisa de novo");

        // o aviso do log sai uma vez por vez que a janela enche
        r = new Streemu.Media.ReaberturaInutil();
        int avisos = 0;
        for (int i = 0; i < 4; i++) { if (r.ContarReabertura(i * 37)) avisos++; }
        Exigir(avisos == 1, "enche uma vez: um aviso (" + avisos + ")");
        Exigir(!r.ContarReabertura(150), "chamadas a mais, já cheia, não repetem o aviso");
        Exigir(r.ContarReabertura(1000) is false && r.ContarReabertura(1010) is false && r.ContarReabertura(1020) is false && r.ContarReabertura(1030), "encheu de novo depois de esvaziar: avisa de novo");

        // (b2) 4 reaberturas espalhadas em mais de 10 min nunca enchem a janela
        r = new Streemu.Media.ReaberturaInutil(); t = 0;
        for (int i = 0; i < 6; i++)
        {
            Exigir(!r.NoLimite(t), $"reabertura {i + 1} a cada 4 min cabe");
            Exigir(!r.Reabriu(t, 5, 60), $"reabertura {i + 1} a cada 4 min não enche a janela");
            Passar(14, 59);
            t += 240 - 14;
        }
        Exigir(!r.Desistiu && !r.NoLimite(t), "janela de 10 min só guarda as recentes: " + r.ReaberturasRecentes(t));

        // (c) o caso antigo: 2 inúteis seguidas continuam desistindo, para sempre (a janela esvaziar não desfaz)
        r = new Streemu.Media.ReaberturaInutil(); t = 0;
        r.Reabriu(t, 35, 60); Passar(20, 36);
        r.Reabriu(t, 35, 60); Passar(20, 36);
        Exigir(r.Desistiu, "2 inúteis seguidas: desistiu");
        t += 5000;
        Exigir(r.Desistiu && !r.NoLimite(t), "SemGanho é permanente: a janela vazia não faz o vigia voltar");

        // (d) Reiniciar (nova live) zera o limite e a desistência
        r = new Streemu.Media.ReaberturaInutil(); t = 0;
        for (int i = 0; i < 4; i++) { r.Reabriu(t, 5, 60); t += 30; }
        Exigir(r.NoLimite(t), "chegou ao limite");
        r.Reiniciar();
        Exigir(!r.Desistiu && !r.NoLimite(t) && r.ReaberturasRecentes(t) == 0, "Reiniciar zera o limite");
        for (int i = 0; i < 3; i++) { Exigir(!r.Reabriu(t, 5, 60), "recomeça a contar do zero"); t += 30; }
        Exigir(!r.NoLimite(t), "3 reaberturas depois de reiniciar ainda cabem");
        r.Reabriu(t, 35, 60); Passar(20, 36); r.Reabriu(t, 35, 60); Passar(20, 36);
        Exigir(r.Desistiu, "preparo: desistiu por falta de ganho");
        r.Reiniciar();
        Exigir(!r.Desistiu, "Reiniciar zera também a desistência por falta de ganho");

        // reabertura só contada (encoder aos trancos) entra na mesma janela
        r = new Streemu.Media.ReaberturaInutil(); t = 0;
        Exigir(!r.ContarReabertura(t) && !r.ContarReabertura(t + 60) && !r.ContarReabertura(t + 120), "3 contadas não enchem");
        Exigir(r.ContarReabertura(t + 180) && r.NoLimite(t + 180), "a 4ª, de qualquer origem do vigia, enche");
    }

    /// <summary>
    /// O vigia (thread do segundo) e a sonda (Task.Run, fora do _observeGate) mexem no mesmo ReaberturaInutil: sem o lock interno a lista de horários
    /// lançava "Collection was modified" / índice fora do intervalo. Duas threads por ~1 s, sem exceção.
    /// </summary>
    static void ReaberturaInutilDuasThreads()
    {
        var r = new Streemu.Media.ReaberturaInutil();
        var erros = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var fim = DateTime.UtcNow.AddSeconds(1);
        var escritora = new Thread(() =>
        {
            try { double t = 0; while (DateTime.UtcNow < fim) { t += 0.5; if (((int)t) % 3 == 0) r.Reabriu(t, 5, 60); else r.ContarReabertura(t); r.Observar(t, 40); } }
            catch (Exception e) { erros.Enqueue(e); }
        });
        var leitora = new Thread(() =>
        {
            try { double t = 0; while (DateTime.UtcNow < fim) { t += 0.5; _ = r.NoLimite(t); _ = r.SegundosDaJanela(t); _ = r.ReaberturasRecentes(t); _ = r.Desistiu; if (((int)t) % 2000 == 0) r.Reiniciar(); } }
            catch (Exception e) { erros.Enqueue(e); }
        });
        escritora.Start(); leitora.Start();
        escritora.Join(); leitora.Join();
        Exigir(erros.IsEmpty, "duas threads sem exceção: " + (erros.TryPeek(out var ex) ? ex.GetType().Name + " " + ex.Message : ""));
    }

    static void LatenciaEscalada()
    {
        var e = new Streemu.Media.EscaladaLatencia();
        e.Iniciar(200);
        long rec = 0, perd = 0, t = 0;
        bool Passo(int segundos, long dRec, long dPerd) { bool subiu = false; for (int i = 0; i < segundos; i++) { t += 1000; rec += dRec; perd += dPerd; subiu |= e.Observar(t, rec, perd); } return subiu; }
        Exigir(!Passo(10, 1000, 10), "1% de perda não sobe");
        Exigir(!Passo(5, 1000, 50), "5 s de perda não bastam");
        Exigir(Passo(2, 1000, 50) && e.ProximaMs == 300 && e.Subidas == 1, "6 s de perda sobe 200 -> 300: " + e.ProximaMs);
        Exigir(!Passo(3, 1000, 0) , "perda some");
        Exigir(!Passo(4, 1000, 50) && Passo(3, 1000, 50) && e.ProximaMs == 450, "interrupção zera a espera: " + e.ProximaMs);
        Exigir(!Passo(10, 3, 1), "pouco tráfego não conta nem dispara");
        for (int i = 0; i < 20; i++) Passo(7, 1000, 100);
        Exigir(e.Subidas == 4 && e.ProximaMs <= 1000, "no máximo 4 subidas: " + e.Subidas + " " + e.ProximaMs);
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
        { Token = token, Bilhete = bilhete, Modo = anonimo ? ModoEnvio.Anonimo : ModoEnvio.Completo };

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
        using var envio = new Envio($"http://127.0.0.1:{srv.Porta}", s, () => new Identidade("123456", "Fulano", "ApelidoFulano", "instalacao-de-teste"), "id-anonimo-de-teste") { Modo = ModoEnvio.Anonimo, Bilhete = () => ServidorFalso.BilheteAceito };
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
                // A5/N5: nem o ID aleatório cru sobe mais (era o `install` de todo pacote anônimo): só o id de PC derivado dele
            Exigir(c["install"] == null && c["pc"]!.GetValue<string>() == envio.PcAnonimo && c["pc"]!.GetValue<string>() != envio.Pc && !c.ToJsonString().Contains("id-anonimo-de-teste"), "ids do modo anônimo: " + c["pc"] + "/" + c["install"]);
            if (c["perfil"] != null) Exigir(!c["perfil"]!.ToJsonString().Contains("Fulano"), "perfil com nome: " + c["perfil"]);
        }
        Exigir(envio.Situacao().Estado != EstadoEnvio.Ok, "o indicador não pode ficar verde \"em dia\" no modo anônimo");
        Anonimizador.Reiniciar();
    }

    // ---------- spec 018, A-II: log limpo, perfil enxuto, anônimo sem identidade e o amigo como amigo-N (N4, N5, N8, N17, N18, N22) ----------

    static SessaoLocal SessaoComLog(string logTexto, bool reduzida)
    {
        string id = $"20261126-{DateTime.Now:HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        string log = Path.Combine(Tmp, "logs", $"sessao-{id}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, logTexto, new UTF8Encoding(false));
        return SessaoLocal.Criar(id, log, naoEnviar: false, reduzida: reduzida);
    }

    /// <summary>Todas as chaves de um JSON, em qualquer profundidade.</summary>
    static IEnumerable<string> Chaves(JsonNode? n)
    {
        if (n is JsonObject o) foreach (var (k, v) in o) { yield return k; foreach (var f in Chaves(v)) yield return f; }
        else if (n is JsonArray a) foreach (var item in a) foreach (var f in Chaves(item)) yield return f;
    }

    /// <summary>N8 e N17. Lê o perfil do PC que roda o teste (só leitura, nada sobe) e confere o que NÃO pode estar nele.</summary>
    static void PerfilEnxuto()
    {
        var p = Perfil.Coletar();
        var chaves = Chaves(p).ToHashSet();
        foreach (var proibida in new[] { "fuso", "ligado_ha_h", "ram_livre_mb", "ultima_atualizacao", "instalado_em", "pasta", "bateria_pct", "programas_que_interferem", "antivirus",
                                         "pentes", "fabricante_pc", "modelo", "ssid", "bssid", "mac", "gateway", "ip", "descricao", "taxa_mbps", "radio" }) // "canal" fica de fora: é o canal de lançamento do app em estavel.app (spec 018, 5.1); o do Wi-Fi é conferido abaixo
            Exigir(!chaves.Contains(proibida), $"o perfil ainda leva \"{proibida}\" (spec 018, 5.4)");
        var estavel = (JsonObject)p["estavel"]!; var momento = (JsonObject)p["momento"]!;
        Exigir(momento.Select(kv => kv.Key).All(k => k is "hora_utc" or "energia" or "wifi" or "windows_jogo"), "o momento tem chave fora da lista: " + string.Join(",", momento.Select(kv => kv.Key)));
        // o nome do fone e do microfone não sobem: o áudio só tem formato e quantidade
        Exigir(!Chaves(estavel["audio"]).Contains("nome") && !Chaves(estavel["audio"]).Contains("id"), "o áudio leva nome de dispositivo: " + estavel["audio"]);
        // a rede só tem tipo, velocidade e VPN
        if (estavel["rede"] is JsonArray rede)
            Exigir(rede.All(r => r is JsonObject ro && ro.Select(kv => kv.Key).All(k => k is "tipo" or "mbps" or "vpn")), "a rede leva mais que tipo, mbps e vpn: " + rede.ToJsonString());
        if (estavel["maquina"] is JsonObject maq)
            Exigir(maq.Select(kv => kv.Key).All(k => k is "tipo" or "tem_bateria" or "erro"), "a máquina leva mais que o tipo: " + maq.ToJsonString());
        if (estavel["ram"] is JsonObject ram)
            Exigir(ram.Select(kv => kv.Key).All(k => k is "total_gb" or "erro"), "a RAM leva mais que o total: " + ram.ToJsonString());
        if (momento["energia"] is JsonObject en)
            Exigir(en["plano"] == null || en["plano"]!.GetValue<string>() is "economia" or "equilibrado" or "desempenho" or "outro", "o plano de energia não é uma categoria: " + en["plano"]);
        if (momento["wifi"] is JsonObject wf)
            Exigir(wf.Select(kv => kv.Key).All(k => k is "banda" or "sinal_pct" or "sem_detalhes" or "erro"), "o Wi-Fi leva mais que banda e sinal: " + wf.ToJsonString());

        // N17: HAGS, Modo de Jogo, Game DVR e captura Xbox vivem no "momento": trocar um deles não muda o hash (perfil novo no índice)
        string antes = Perfil.Hash(p);
        var igual = (JsonObject)JsonNode.Parse(p.ToJsonString())!;
        var jogo = igual["momento"]!["windows_jogo"] as JsonObject ?? new JsonObject();
        jogo["agendamento_gpu_hardware"] = jogo["agendamento_gpu_hardware"]?.GetValue<string>() == "ligado" ? "desligado" : "ligado";
        jogo["modo_de_jogo"] = jogo["modo_de_jogo"]?.GetValue<bool>() != true;
        igual["momento"]!["windows_jogo"] = jogo;
        igual["momento"]!["hora_utc"] = "2000-01-01T00:00:00Z";
        Exigir(Perfil.Hash(igual) == antes, "trocar HAGS/Modo de Jogo ou a hora gerou outro hash do perfil");
        Exigir(!estavel.ToJsonString().Contains("agendamento_gpu_hardware") && !estavel.ToJsonString().Contains("modo_de_jogo"), "o HAGS voltou para o \"estavel\"");
        // e uma mudança de verdade no estável (driver novo) muda o hash
        var novo = (JsonObject)JsonNode.Parse(p.ToJsonString())!;
        novo["estavel"]!["windows"] = new JsonObject { ["ubr"] = 999999 };
        Exigir(Perfil.Hash(novo) != antes, "mudar o Windows não mudou o hash");
        Higiene.EsquecerDispositivos();
    }

    /// <summary>N4 e N5: o mesmo perfil no Completo e no Anônimo; o anônimo não leva identidade, nem o ID aleatório cru, nem o id real do PC (nem no cabeçalho HTTP).</summary>
    static void PerfilIgualEAnonimoSemIdentidade()
    {
        Anonimizador.Reiniciar(); Pseudonimos.Reiniciar();
        var perfil = new JsonObject
        {
            ["estavel"] = new JsonObject { ["cpu"] = new JsonObject { ["nome"] = "CPU de teste" }, ["ram"] = new JsonObject { ["total_gb"] = 16 }, ["rede"] = new JsonArray(new JsonObject { ["tipo"] = "cabo", ["mbps"] = 1000, ["vpn"] = false }) },
            ["momento"] = new JsonObject { ["hora_utc"] = "2026-11-26T12:00:00Z", ["wifi"] = new JsonObject { ["banda"] = "5 GHz", ["sinal_pct"] = 80 } },
        };
        JsonObject Enviar(bool anonimo)
        {
            using var srv = new ServidorFalso();
            var s = SessaoComLog("12:00:00.000 linha neutra\n", reduzida: anonimo);
            s.Evento("sessao_inicio", new JsonObject { ["versao"] = "9.9.9", ["args"] = "normal" });
            Gravador.Descarregar(TimeSpan.FromSeconds(5));
            if (anonimo) Anonimizador.MarcarPronto();
            var ident = new Identidade("123456789012345678", "DiscordDoFulano", "ApelidoDoFulano", "instalacao-de-teste", "c-abcdefghijkl");
            using var envio = new Envio($"http://127.0.0.1:{srv.Porta}", s, () => ident, "id-anonimo-de-teste")
            { Modo = anonimo ? ModoEnvio.Anonimo : ModoEnvio.Completo, Token = _ => ServidorFalso.TokenPadrao, Bilhete = () => ServidorFalso.BilheteAceito };
            envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>());
            envio.DefinirPerfil((JsonObject)JsonNode.Parse(perfil.ToJsonString())!, "hardware-x");
            envio.Agora();
            Exigir(Esperar(() => srv.Cabecalhos().Any(c => c["perfil"] != null), 20_000), "o perfil não chegou ao servidor");
            envio.Fechar(TimeSpan.FromSeconds(2));
            string pcDoEnvio = anonimo ? envio.PcAnonimo : envio.Pc;
            Exigir(srv.PcsDoCabecalho().All(x => x == pcDoEnvio), "o cabeçalho x-streemu-pc devia ser " + pcDoEnvio + ": " + string.Join(",", srv.PcsDoCabecalho()));
            if (anonimo) Exigir(!srv.PcsDoCabecalho().Contains(envio.Pc), "o id real do PC foi no cabeçalho HTTP do anônimo");
            return srv.Cabecalhos().First(c => c["perfil"] != null);
        }
        var completo = Enviar(false);
        var anon = Enviar(true);
        // N4: o perfil é o mesmo conjunto de campos nos dois estados
        Exigir(Chaves(completo["perfil"]).SequenceEqual(Chaves(anon["perfil"])), "perfil do Completo e do Anônimo com campos diferentes: " + completo["perfil"] + " | " + anon["perfil"]);
        // N5: o anônimo sem identidade, sem o ID aleatório cru, sem hardware ligável
        Exigir(anon["install"] == null && anon["conta"] == null && anon["apelido"] == null && anon["hw"] == null && anon["discord_id"] == null && anon["discord_nome"] == null,
            "cabeçalho anônimo com identidade: " + anon.ToJsonString());
        Exigir(!anon.ToJsonString().Contains("id-anonimo-de-teste") && !anon.ToJsonString().Contains("instalacao-de-teste") && !anon.ToJsonString().Contains("DiscordDoFulano"), "cabeçalho anônimo com ID cru: " + anon.ToJsonString());
        Exigir(completo["hw"]?.GetValue<string>() == "hardware-x" && completo["conta"]?.GetValue<string>() == "c-abcdefghijkl", "o Completo devia seguir identificado: " + completo.ToJsonString());
        Anonimizador.Reiniciar(); Pseudonimos.Reiniciar();
    }

    /// <summary>N18: o Discord (id e nome) não sobe em estado nenhum, mesmo quando o app o conhece.</summary>
    static void DiscordNaoSobe()
    {
        Anonimizador.Reiniciar(); Pseudonimos.Reiniciar();
        foreach (var anonimo in new[] { false, true })
        {
            using var srv = new ServidorFalso();
            var s = SessaoComLog("12:00:00.000 linha neutra\n", reduzida: anonimo);
            s.Evento("sessao_inicio", new JsonObject { ["versao"] = "9.9.9" });
            Gravador.Descarregar(TimeSpan.FromSeconds(5));
            if (anonimo) Anonimizador.MarcarPronto();
            using var envio = new Envio($"http://127.0.0.1:{srv.Porta}", s, () => new Identidade("987654321098765432", "DiscordDoFulano", "ApelidoDoFulano", "instalacao-de-teste", "c-abcdefghijkl"), "id-anonimo-de-teste")
            { Modo = anonimo ? ModoEnvio.Anonimo : ModoEnvio.Completo, Token = _ => ServidorFalso.TokenPadrao, Bilhete = () => ServidorFalso.BilheteAceito };
            envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>());
            envio.DefinirPerfil(new JsonObject { ["estavel"] = new JsonObject(), ["momento"] = new JsonObject() }, "hardware-x");
            envio.Agora();
            Exigir(Esperar(() => srv.Cabecalhos().Length > 0 && srv.VeioEvento("sessao_inicio"), 20_000), "nada chegou ao servidor");
            envio.Fechar(TimeSpan.FromSeconds(2));
            foreach (var c in srv.Cabecalhos())
            {
                string cab = c.ToJsonString();
                Exigir(!c.ContainsKey("discord_id") && !c.ContainsKey("discord_nome") && !cab.Contains("987654321098765432") && !cab.Contains("DiscordDoFulano"), $"o Discord subiu no cabeçalho ({(anonimo ? "anônimo" : "completo")}): {cab}");
            }
            string log = Encoding.UTF8.GetString(srv.Conteudo($"{s.Id}|log.txt"));
            Exigir(!log.Contains("987654321098765432"), "o id do Discord subiu no log");
        }
        Anonimizador.Reiniciar(); Pseudonimos.Reiniciar();
    }

    /// <summary>
    /// D32 e N22. O app registra o amigo (id e nome) e, no envio, ele vira amigo-N no log e nos eventos do Completo e do Anônimo, sempre o mesmo número. O
    /// outro lado (B, em Desligado) não manda nada do diagnóstico; o que A manda sobre B é só amigo-N: nem o nome nem o id de B chegam ao servidor.
    /// </summary>
    static void AmigoViraAmigoN()
    {
        Anonimizador.Reiniciar(); Pseudonimos.Reiniciar();
        const string idB = "id-do-amigo-b-0123456789", nickB = "BrunoDoFundo";
        Pseudonimos.Registrar(idB, nickB);
        string numero = Pseudonimos.Para(idB, nickB)!;
        Exigir(numero == "amigo-1" && Pseudonimos.Para(null, nickB) == numero && Pseudonimos.Para(idB, null) == numero, "o mesmo amigo devia ter um número só: " + numero);
        Exigir(Pseudonimos.Para("convidado") == "convidado", "o convidado da live privada devia ficar como está");
        foreach (var anonimo in new[] { false, true })
        {
            using var srv = new ServidorFalso();
            var s = SessaoComLog($"12:00:00.000 Ligação com {nickB} conectada\n12:00:01.000 {idB} pediu para assistir; bruno dofundo entrou\n", reduzida: anonimo);
            s.Evento("live", new JsonObject { ["transmissao"] = "LIV-9", ["ligacao"] = "L-1", ["papel"] = "envia", ["outro_install"] = numero, ["outro_apelido"] = numero, ["nota"] = $"{nickB} abriu" });
            Gravador.Descarregar(TimeSpan.FromSeconds(5));
            if (anonimo) Anonimizador.MarcarPronto();
            using var envio = new Envio($"http://127.0.0.1:{srv.Porta}", s, () => new Identidade(null, null, "Eu", "instalacao-de-teste", "c-abcdefghijkl"), "id-anonimo-de-teste")
            { Modo = anonimo ? ModoEnvio.Anonimo : ModoEnvio.Completo, Token = _ => ServidorFalso.TokenPadrao, Bilhete = () => ServidorFalso.BilheteAceito };
            envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>()); envio.Agora();
            Exigir(Esperar(() => srv.VeioEvento("live") && srv.Tamanho($"{s.Id}|log.txt") > 0, 20_000), "o servidor não recebeu o log e o evento");
            envio.Fechar(TimeSpan.FromSeconds(2));
            string log = Encoding.UTF8.GetString(srv.Conteudo($"{s.Id}|log.txt"));
            string modo = anonimo ? "anônimo" : "completo";
            Exigir(!log.Contains(nickB, StringComparison.OrdinalIgnoreCase) && !log.Contains(idB) && log.Contains(numero), $"o log ({modo}) tem o amigo pelo nome ou pelo id: " + log);
            var live = srv.UltimoEvento("live")!;
            string ev = live.ToJsonString();
            Exigir(!ev.Contains(nickB, StringComparison.OrdinalIgnoreCase) && !ev.Contains(idB) && ev.Contains(numero), $"o evento da live ({modo}) tem o amigo pelo nome ou pelo id: " + ev);
            Exigir(live["transmissao"]?.GetValue<string>() == "LIV-9" && live["ligacao"]?.GetValue<string>() == "L-1", "a transmissão e a ligação (a junção das duas pontas) não podem mudar: " + ev);
            foreach (var c in srv.Cabecalhos()) Exigir(!c.ToJsonString().Contains(nickB, StringComparison.OrdinalIgnoreCase) && !c.ToJsonString().Contains(idB), "o amigo apareceu no cabeçalho: " + c.ToJsonString());
        }
        // B em Desligado: nenhum pedido do diagnóstico saiu do PC dele, então nada dele (nem o id) chega ao servidor por ele
        using (var srvB = new ServidorFalso())
        {
            var sb = SessaoComLog("12:00:00.000 Ligação com o amigo A conectada\n", reduzida: false); sb.E.NaoEnviar = true; sb.Salvar();
            using var envioB = EnvioEssencial(srvB, sb, ModoEnvio.Desligado);
            envioB.Ativo = true; envioB.Iniciar(Array.Empty<SessaoLocal>()); envioB.Agora();
            Thread.Sleep(2500);
            envioB.Fechar(TimeSpan.FromSeconds(2));
            Exigir(srvB.Pedidos == 0 && srvB.BytesRecebidos == 0 && srvB.Cabecalhos().Length == 0, "o Desligado mandou diagnóstico");
        }
        Anonimizador.Reiniciar(); Pseudonimos.Reiniciar();
    }

    /// <summary>
    /// N22, o outro lado: com as duas pontas Ligadas, cada uma só diz amigo-N sobre a outra, mas as duas gravam a mesma transmissão e a mesma ligação, que é
    /// por onde o Worker (B5) e o painel as juntam. O amigo-1 de uma não precisa ser o amigo-1 da outra.
    /// </summary>
    static void AmigoDuasPontasLigadas()
    {
        Anonimizador.Reiniciar();
        var vistos = new List<JsonObject>();
        foreach (var (eu, outro, idOutro, papel) in new[] { ("Alice", "BrunoDoFundo", "id-b-0123456789", "envia"), ("BrunoDoFundo", "Alice", "id-a-0123456789", "assiste") })
        {
            Pseudonimos.Reiniciar();
            Pseudonimos.Registrar(idOutro, outro);
            using var srv = new ServidorFalso();
            var s = SessaoComLog("12:00:00.000 linha neutra\n", reduzida: false);
            s.Evento("live", new JsonObject { ["transmissao"] = "LIV-42", ["ligacao"] = "L-7", ["papel"] = papel, ["outro_install"] = Pseudonimos.Para(idOutro, outro), ["outro_conta"] = Pseudonimos.Para(idOutro, outro), ["outro_apelido"] = Pseudonimos.Para(idOutro, outro) });
            Gravador.Descarregar(TimeSpan.FromSeconds(5));
            using var envio = new Envio($"http://127.0.0.1:{srv.Porta}", s, () => new Identidade(null, null, eu, "instalacao-de-" + eu, "c-abcdefghijkl"), "id-anonimo-de-teste") { Token = _ => ServidorFalso.TokenPadrao };
            envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>()); envio.Agora();
            Exigir(Esperar(() => srv.VeioEvento("live"), 20_000), "o evento da live não chegou");
            envio.Fechar(TimeSpan.FromSeconds(2));
            var live = srv.UltimoEvento("live")!;
            Exigir(!live.ToJsonString().Contains(outro, StringComparison.OrdinalIgnoreCase) && !live.ToJsonString().Contains(idOutro), "a ponta " + eu + " mandou o nome ou o id da outra: " + live.ToJsonString());
            vistos.Add(live);
        }
        Pseudonimos.Reiniciar();
        Exigir(vistos[0]["transmissao"]?.ToString() == vistos[1]["transmissao"]?.ToString() && vistos[0]["ligacao"]?.ToString() == vistos[1]["ligacao"]?.ToString(), "as duas pontas não têm a mesma transmissão e ligação: " + vistos[0] + " | " + vistos[1]);
        Exigir(vistos[0]["papel"]?.ToString() == "envia" && vistos[1]["papel"]?.ToString() == "assiste", "os papéis das pontas não bateram");
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
        // spec 018, N15: os prazos do texto são os do Worker. O de 180 dias (perfil do PC) é a limpeza B4: se o Worker já tem a constante, tem que bater
        var perfil = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(js!), @"PERFIL_(?:GUARDA|SEM_CONTATO)\w*\s*=\s*(\d+)\s*\*\s*86400e3");
        if (perfil.Success) Exigir(int.Parse(perfil.Groups[1].Value) == TextoDiagnostico.DiasDePerfil, $"o Worker apaga o perfil em {perfil.Groups[1].Value} dias e o texto diz {TextoDiagnostico.DiasDePerfil}");
        else Console.WriteLine("      (o Worker ainda não tem a constante dos 180 dias do perfil, B4: o texto cita o número, mas não há com o que comparar)");
        // A2: o texto completo cita base legal, IP visto pelo amigo, Discord, anônimo, apagar, IA e os prazos (120 e 180)
        foreach (var trecho in new[] { "120 dias", "180 dias", "5 dias úteis", "LGPD", "legítimo interesse", "execução do contrato", "endereço de rede (IP)", "Discord", "anônimo", "Anônimo não é anonimato completo",
                     "APAGAR", "equipe do Streemu", "Ver o que é enviado", "pessoa-1", "o MAC", "USA IA", "não treinar", "CAMADA ESSENCIAL" })
            Exigir(todo.Contains(trecho, StringComparison.OrdinalIgnoreCase), "o texto completo não cita: " + trecho);
        Exigir(!todo.Contains("Cid" + "ão") && !todo.Contains("Ra" + "pha"), "o texto cita nome de pessoa (D35): a equipe é sempre \"a equipe do Streemu\"");
        Exigir(TextoDiagnostico.Aviso(ModoEnvio.Completo).Contains("120 dias") && TextoDiagnostico.Aviso(ModoEnvio.Completo).Contains("desligar"), "aviso curto (ligado)");
        foreach (var modo in new[] { ModoEnvio.Completo, ModoEnvio.Anonimo, ModoEnvio.Desligado })
            Exigir(TextoDiagnostico.Aviso(modo).Length < 800 && TextoDiagnostico.Faixa(modo).Length < 420, $"o aviso ou a faixa ({modo}) passou do tamanho");
    }

    static void TextoDosTresEstados()
    {
        string desligado = TextoDiagnostico.Aviso(ModoEnvio.Desligado), faixaDesligado = TextoDiagnostico.Faixa(ModoEnvio.Desligado);
        Exigir(!desligado.Contains("ANÔNIMO") && !faixaDesligado.Contains("ainda manda um diagnóstico anônimo"), "o texto do Desligado ainda promete o anônimo que não existe mais");
        foreach (var t in new[] { desligado, faixaDesligado }) Exigir(t.Contains("não desliga") || t.Contains("Isso não desliga"), "o texto do Desligado não diz que a camada essencial não desliga: " + t);
        Exigir(desligado.Contains("não guarda quem foi"), "o Desligado devia dizer que a contagem não guarda a pessoa (D38)");
        Exigir(!desligado.Contains("Cid" + "ão") && !desligado.Contains("Ra" + "pha"), "nome de pessoa no aviso");
        Exigir(TextoDiagnostico.Aviso(ModoEnvio.Anonimo).Contains("não é anonimato completo"), "o aviso do anônimo devia dizer que não é anonimato completo (D16)");
        Exigir(TextoDiagnostico.Faixa(ModoEnvio.Anonimo).Contains("não é anonimato completo"), "a faixa do anônimo devia dizer que não é anonimato completo");
        string ligado = TextoDiagnostico.Faixa(ModoEnvio.Completo);
        Exigir(ligado.Contains("Cada PC é de um jeito") && ligado.Contains("Pode desligar"), "a faixa do Ligado não é a frase da spec (D13): " + ligado);
        Exigir(TextoDiagnostico.TextoDoLink == "Como cuidamos dos seus dados" && TextoDiagnostico.UrlPolitica.StartsWith("https://"), "o link discreto da faixa");
        Exigir(TextoDiagnostico.LinhaDaIa.Contains("IA") && TextoDiagnostico.LinhaDaIa.Contains("sem o seu nome"), "a linha da IA em Ajustes");
        Exigir(TextoDiagnostico.VersaoDoAviso >= 3, "a versão do aviso precisa subir para quem já viu a de antes ver a faixa nova uma vez");
    }

    static void ExportarPrivacidade()
    {
        string arq = Path.Combine(Tmp, "privacidade.txt");
        Exigir(TextoDiagnostico.Exportar(arq) == 0, "Exportar não devolveu 0");
        byte[] bytes = File.ReadAllBytes(arq);
        Exigir(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "sem BOM UTF-8 (o Inno Setup lê como ANSI sem ele e estraga os acentos)");
        string texto = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Exigir(texto.StartsWith(TextoDiagnostico.TituloDoAviso) && texto.Contains("POR QUE PODEMOS") && texto.Contains("não"), "o arquivo não tem o texto do app (ou os acentos quebraram)");
        Exigir(!texto.Replace("\r\n", "").Contains('\n'), "o fim de linha não é CRLF");
        Exigir(TextoDiagnostico.Exportar(null) == 2 && TextoDiagnostico.Exportar("  ") == 2, "sem caminho devia sair com 2");
        Exigir(TextoDiagnostico.Exportar(Path.Combine(Tmp, "pasta-que-nao-existe-" + Guid.NewGuid().ToString("N"), "y.txt")) == 1, "caminho impossível devia sair com 1");
    }

    // ---------- três estados e migração (spec 018, A1; N3 e N28) ----------

    static void MigracaoDesligado()
    {
        // quem tinha desligado antes dos três estados: só TelemetriaDesligada existe no json
        var antigo = System.Text.Json.JsonSerializer.Deserialize<Streemu.Util.AppState>("{\"TelemetriaDesligada\":true,\"TelemetriaAvisoVisto\":true,\"TelemetriaAvisoVersao\":2}")!;
        Exigir(Envio.ModoDosAjustes(antigo) == ModoEnvio.Desligado, "o estado antigo desligado devia cair em Desligado, não em anônimo");
        Exigir(TextoDiagnostico.AvisoPendente(antigo), "quem já tinha visto a versão 2 do texto devia ver a faixa nova (versão " + TextoDiagnostico.VersaoDoAviso + ")");
        Exigir(Envio.ModoDosAjustes(System.Text.Json.JsonSerializer.Deserialize<Streemu.Util.AppState>("{}")!) == ModoEnvio.Completo, "estado vazio devia ser Completo");
        // a faixa vem uma vez só e a escolha continua
        TextoDiagnostico.MarcarVisto(antigo);
        Exigir(!TextoDiagnostico.AvisoPendente(antigo), "a faixa continua pendente depois de vista");
        string json = System.Text.Json.JsonSerializer.Serialize(antigo);
        var reaberto = System.Text.Json.JsonSerializer.Deserialize<Streemu.Util.AppState>(json)!;
        Exigir(Envio.ModoDosAjustes(reaberto) == ModoEnvio.Desligado, "a segunda abertura não é mais Desligado");
        Exigir(!TextoDiagnostico.AvisoPendente(reaberto), "a segunda abertura mostraria a faixa de novo (N28)");
        Exigir(TextoDiagnostico.Faixa(Envio.ModoDosAjustes(reaberto)).Contains("não desliga"), "a faixa do migrado não explica a camada essencial");
        // o app nunca liga o anônimo sozinho, nem com o Completo gravado como "completo"
        Exigir(Envio.ModoDosAjustes(new Streemu.Util.AppState { TelemetriaDesligada = true }) != ModoEnvio.Anonimo, "o app ligou o anônimo sozinho");
    }

    static void ModosPersistem()
    {
        foreach (var modo in new[] { ModoEnvio.Completo, ModoEnvio.Anonimo, ModoEnvio.Desligado })
        {
            var estado = new Streemu.Util.AppState();
            Envio.GravarModo(estado, modo);
            var volta = System.Text.Json.JsonSerializer.Deserialize<Streemu.Util.AppState>(System.Text.Json.JsonSerializer.Serialize(estado))!;
            Exigir(Envio.ModoDosAjustes(volta) == modo, $"o modo {modo} não persistiu: {Envio.ModoDosAjustes(volta)}");
            // a forma antiga continua coerente para uma versão anterior que abrir o mesmo estado: só o Completo é "ligado"
            Exigir(volta.TelemetriaDesligada == (modo != ModoEnvio.Completo), $"TelemetriaDesligada incoerente com {modo}");
        }
    }

    // ---------- camada essencial (spec 018, A1; B1 no Worker) ----------

    /// <summary>Esperas curtas para o teste não levar minutos (o app usa 20 s, e no anônimo de 2 a 10 min).</summary>
    static void EsperasCurtas()
    {
        Envio.EsperaEssencial = TimeSpan.FromMilliseconds(150);
        Envio.EsperaEssencialAnonimo = (TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(300));
        Envio.ReavaliarEssencial = TimeSpan.FromMilliseconds(300);
    }

    static Envio EnvioEssencial(ServidorFalso srv, SessaoLocal s, ModoEnvio modo, Func<bool, string?>? token = null) =>
        new($"http://127.0.0.1:{srv.Porta}", s, () => new Identidade("123456", "Fulano", "ApelidoFulano", "instalacao-de-teste", "c-abcdefghijkl"), "id-anonimo-de-teste")
        { Token = token ?? (_ => ServidorFalso.TokenPadrao), Bilhete = () => ServidorFalso.BilheteAceito, Modo = modo };

    static void DesligadoSoEssencial()
    {
        EsperasCurtas();
        using var srv = new ServidorFalso();
        // como o app abre no Desligado: a sessão fica só no PC, e a de uma abertura anterior (anônima, de antes dos três estados) também não sobe
        var s = Sessao(100, 10); s.E.NaoEnviar = true; s.Salvar();
        var anterior = Sessao(60, 5); anterior.E.Reduzida = true; anterior.Salvar();
        using var envio = EnvioEssencial(srv, s, ModoEnvio.Desligado);
        envio.Ativo = true;
        envio.Iniciar(new[] { anterior });
        envio.DefinirPerfil(new JsonObject { ["gpu"] = "placa de Fulano" }, "hardware-x");
        envio.Essencial("app_abriu");
        envio.Essencial("live_comecou", "transmite");
        envio.Essencial("falha_grave", "captura_travada");
        envio.Essencial("falha_grave", "C:\\Users\\ana\\jogo.exe travou"); // texto livre: a lista fechada não deixa nem sair do app
        envio.Agora();
        Exigir(Esperar(() => srv.EssencialSomado().Count >= 3, 20_000), "a camada essencial não chegou ao servidor: " + string.Join(",", srv.EssencialSomado().Keys));
        Thread.Sleep(2500); // dá tempo de um pedido do diagnóstico sair, se algum fosse sair
        var somado = srv.EssencialSomado();
        Exigir(somado.GetValueOrDefault("app_abriu") == 1 && somado.GetValueOrDefault("live_comecou.transmite") == 1 && somado.GetValueOrDefault("falha_grave.captura_travada") == 1, "contagens: " + string.Join(", ", somado.Select(kv => kv.Key + "=" + kv.Value)));
        Exigir(somado.Count == 3, "o evento com texto livre chegou ao servidor: " + string.Join(",", somado.Keys));
        // N2: nenhum pedido do diagnóstico (log, eventos, perfil, séries, nem os passos do login) numa sessão inteira
        Exigir(srv.Pedidos == 0 && srv.Credenciais().Length == 0 && srv.Logins().Length == 0 && srv.BytesRecebidos == 0, $"o Desligado fez pedido do diagnóstico: {srv.Pedidos} pedido(s), {srv.BytesRecebidos} bytes");
        Exigir(srv.Cabecalhos().Length == 0 && srv.Tamanho($"{s.Id}|log.txt") == 0 && srv.Tamanho($"{anterior.Id}|log.txt") == 0 && srv.ContarEventos("teste") == 0, "log, perfil ou eventos subiram no Desligado");
        Exigir(s.Cursor(SessaoLocal.Log) == 0 && anterior.Cursor(SessaoLocal.Log) == 0, "cursor andou sem envio");
        var sit = envio.Situacao();
        Exigir(sit.Estado == EstadoEnvio.Neutro && sit.Titulo == "Diagnóstico desligado", "indicador: " + sit.Titulo);
        // fechar no Desligado também não sobe o diagnóstico (nem o custo da telemetria, nem o sessao_fim)
        envio.Fechar(TimeSpan.FromSeconds(2));
        Exigir(srv.Pedidos == 0 && srv.BytesRecebidos == 0, "o fechamento mandou diagnóstico no Desligado");
        // mesma coisa com a sessão completa NÃO marcada (o modo muda no meio da sessão): o modo manda, não a marca
        var viva = Sessao(40, 4);
        using var envio2 = EnvioEssencial(srv, viva, ModoEnvio.Desligado);
        envio2.Ativo = true; envio2.Iniciar(Array.Empty<SessaoLocal>()); envio2.Agora();
        Thread.Sleep(2500);
        envio2.Fechar(TimeSpan.FromSeconds(2));
        Exigir(srv.Pedidos == 0 && srv.Tamanho($"{viva.Id}|log.txt") == 0, "uma sessão sem a marca subiu com o modo Desligado");
    }

    static void EssencialCorpoMinimo()
    {
        EsperasCurtas();
        using var srv = new ServidorFalso();
        var s = Sessao(5, 1);
        using var envio = EnvioEssencial(srv, s, ModoEnvio.Completo);
        envio.Iniciar(Array.Empty<SessaoLocal>());
        envio.Essencial("app_abriu");
        envio.Essencial("live_acabou", "assiste_privada", 4321);
        envio.Essencial("evento_inventado");                 // fora da lista
        envio.Essencial("app_fechou", "detalhe-que-nao-existe"); // detalhe onde não cabe
        envio.Essencial("live_comecou", "jogo do Fulano");   // texto livre no lugar do papel
        Exigir(Esperar(() => srv.Essenciais().Length > 0, 20_000), "nada chegou");
        envio.Fechar(TimeSpan.FromSeconds(2));
        foreach (var (cred, corpo) in srv.Essenciais())
        {
            Exigir(cred == "token:" + ServidorFalso.TokenPadrao, "a camada essencial devia ir com o token da conta: " + cred);
            Exigir(corpo.Select(x => x.Key).All(k => k is "v" or "versao" or "canal" or "plataforma" or "eventos"), "campo fora da lista branca: " + corpo.ToJsonString());
            Exigir(corpo["plataforma"]!.GetValue<string>() == "windows" && corpo["v"]!.GetValue<int>() == 1, "plataforma ou versão do formato: " + corpo.ToJsonString());
            string json = corpo.ToJsonString();
            foreach (var proibido in new[] { "Fulano", "123456", "instalacao-de-teste", "id-anonimo-de-teste", "c-abcdefghijkl", envio.Pc, envio.PcAnonimo, s.Id })
                Exigir(!json.Contains(proibido), "a camada essencial levou " + proibido + ": " + json);
        }
        var somado = srv.EssencialSomado();
        Exigir(somado.Count == 2 && somado.ContainsKey("app_abriu") && somado.ContainsKey("live_acabou.assiste_privada"), "o que chegou: " + string.Join(",", somado.Keys));
        var live = srv.Essenciais().SelectMany(e => e.Corpo["eventos"]!.AsArray()).First(e => e!["evento"]!.GetValue<string>() == "live_acabou")!;
        Exigir(live["valor"]!.GetValue<int>() == 4321, "a duração da live não foi: " + live);
    }

    static void EssencialNoAnonimo()
    {
        EsperasCurtas();
        Anonimizador.Reiniciar();
        using var srv = new ServidorFalso();
        var s = SessaoReduzida();
        using var envio = EnvioEssencial(srv, s, ModoEnvio.Anonimo);
        envio.Iniciar(Array.Empty<SessaoLocal>());
        var antes = DateTime.UtcNow;
        envio.Essencial("app_abriu");
        Exigir(Esperar(() => srv.Essenciais().Length > 0, 20_000), "a camada essencial não subiu no anônimo");
        Exigir(DateTime.UtcNow - antes >= TimeSpan.FromMilliseconds(120), "no anônimo a essencial devia esperar o tempo sorteado, não sair no mesmo instante");
        envio.Fechar(TimeSpan.FromSeconds(2));
        // sempre pela conta, nunca pelo bilhete do anônimo: o envio identificado não pode se ligar ao anônimo por nenhum campo
        foreach (var (cred, corpo) in srv.Essenciais())
        {
            Exigir(cred == "token:" + ServidorFalso.TokenPadrao, "no anônimo a essencial devia ir pelo token da conta, veio: " + cred);
            string json = corpo.ToJsonString();
            foreach (var proibido in new[] { "id-anonimo-de-teste", envio.PcAnonimo, envio.Pc, s.Id, "sessao", "pc", "install", "relogio", "hora", "quando" })
                Exigir(!json.Contains("\"" + proibido + "\"") && !json.Contains(proibido) || proibido.Length < 4, "a essencial do anônimo levou " + proibido + ": " + json);
            Exigir(corpo.Select(x => x.Key).All(k => k is "v" or "versao" or "canal" or "plataforma" or "eventos"), "campo fora da lista branca: " + json);
        }
        // e o diagnóstico anônimo, esse sim, não usa o token
        Exigir(srv.Credenciais().All(c => c.StartsWith("bilhete:")), "o diagnóstico anônimo usou o token: " + string.Join(",", srv.Credenciais()));
        Anonimizador.Reiniciar();
    }

    static void EssencialAnonimoGuarda()
    {
        EsperasCurtas();
        Envio.EsperaEssencialAnonimo = (TimeSpan.FromHours(1), TimeSpan.FromHours(2)); // nada sai sozinho antes de fechar
        using var srv = new ServidorFalso();
        var s = SessaoReduzida();
        using (var envio = EnvioEssencial(srv, s, ModoEnvio.Anonimo))
        {
            envio.Iniciar(Array.Empty<SessaoLocal>());
            envio.Essencial("app_abriu");
            envio.Essencial("live_comecou", "transmite");
            envio.Fechar(TimeSpan.FromSeconds(2));
        }
        Exigir(srv.PedidosEssenciais == 0, "no anônimo, ao fechar, a essencial não pode sair junto com o fim da sessão: " + srv.PedidosEssenciais);
        Exigir(File.Exists(Path.Combine(Pasta.Raiz, "essencial-pendente.json")), "a essencial devia ficar guardada para a próxima abertura");
        // próxima abertura: carrega, espera o tempo sorteado e sobe
        EsperasCurtas();
        var s2 = SessaoReduzida();
        using var envio2 = EnvioEssencial(srv, s2, ModoEnvio.Anonimo);
        envio2.Iniciar(Array.Empty<SessaoLocal>());
        Exigir(Esperar(() => srv.EssencialSomado().Count >= 2, 20_000), "a essencial guardada não subiu na abertura seguinte: " + string.Join(",", srv.EssencialSomado().Keys));
        Exigir(!File.Exists(Path.Combine(Pasta.Raiz, "essencial-pendente.json")), "o arquivo guardado devia sumir depois de carregar");
        envio2.Fechar(TimeSpan.FromSeconds(2));
        var somado = srv.EssencialSomado();
        Exigir(somado.GetValueOrDefault("app_abriu") == 1 && somado.GetValueOrDefault("live_comecou.transmite") == 1, "contagens depois da reabertura: " + string.Join(",", somado.Select(kv => kv.Key + "=" + kv.Value)));
    }

    static void EssencialEsperaFaixaEConta()
    {
        EsperasCurtas();
        using var srv = new ServidorFalso();
        var s = Sessao(10, 2);
        bool logou = false;
        using var envio = EnvioEssencial(srv, s, ModoEnvio.Desligado, _ => logou ? ServidorFalso.TokenPadrao : null);
        envio.AguardandoEssencial = true; // a faixa desta versão do texto ainda não foi vista (quem migrou do estado antigo)
        envio.Iniciar(Array.Empty<SessaoLocal>());
        envio.Essencial("app_abriu");
        Thread.Sleep(1500);
        Exigir(srv.PedidosEssenciais == 0, "a essencial saiu antes de a pessoa ver a faixa");
        Exigir(envio.EssencialPendentes == 1, "a contagem se perdeu enquanto esperava a faixa");
        envio.AguardandoEssencial = false; envio.Agora();
        Thread.Sleep(1500);
        Exigir(srv.PedidosEssenciais == 0, "a essencial saiu sem conta (o token ainda não existe)");
        Exigir(envio.EssencialPendentes == 1, "a contagem se perdeu enquanto esperava a conta");
        logou = true; envio.Agora();
        Exigir(Esperar(() => srv.EssencialSomado().GetValueOrDefault("app_abriu") == 1, 20_000), "depois do login a essencial devia subir: " + string.Join(",", srv.EssencialSomado().Keys));
        envio.Fechar(TimeSpan.FromSeconds(2));
        Exigir(envio.EssencialPendentes == 0, "ficou na fila depois de confirmada");
    }

    static void EssencialFalhaERecupera()
    {
        EsperasCurtas();
        using var srv = new ServidorFalso();
        var s = Sessao(10, 2);
        using var envio = EnvioEssencial(srv, s, ModoEnvio.Completo);
        envio.Iniciar(Array.Empty<SessaoLocal>());
        // a camada essencial falha (500 duas vezes); o indicador do diagnóstico não pode ficar vermelho por causa dela
        srv.EssencialFila.Enqueue(500); srv.EssencialFila.Enqueue(500);
        envio.Essencial("app_abriu");
        bool viuErro = false;
        Exigir(Esperar(() => srv.EssencialSomado().GetValueOrDefault("app_abriu") == 1, 60_000, () => viuErro |= envio.UltimoErro != null), "não recuperou depois do 500");
        Exigir(srv.PedidosEssenciais >= 3, "devia ter tentado de novo: " + srv.PedidosEssenciais);
        Exigir(!viuErro, "uma falha da camada essencial apareceu como erro do diagnóstico: " + envio.UltimoErro);
        Exigir(srv.EssencialSomado().GetValueOrDefault("app_abriu") == 1, "contou a mesma abertura mais de uma vez: " + srv.EssencialSomado().GetValueOrDefault("app_abriu"));
        envio.Fechar(TimeSpan.FromSeconds(2));
    }

    static void EssencialRenovaEDescarta()
    {
        EsperasCurtas();
        using var srv = new ServidorFalso();
        var s = Sessao(10, 2);
        var pediuRenovar = new List<bool>();
        string atual = "tok-velho";
        using var envio = EnvioEssencial(srv, s, ModoEnvio.Desligado, renovar => { lock (pediuRenovar) pediuRenovar.Add(renovar); if (renovar) atual = ServidorFalso.TokenPadrao; return atual; });
        envio.Iniciar(Array.Empty<SessaoLocal>());
        envio.Essencial("app_abriu");
        Exigir(Esperar(() => srv.EssencialSomado().GetValueOrDefault("app_abriu") == 1, 60_000), "depois do 401 devia renovar o token e subir");
        lock (pediuRenovar) Exigir(pediuRenovar.Contains(true), "não pediu token novo depois do 401");
        // 400: o Worker não aceitou o conteúdo; repetir não resolve, então o lote some (e a fila não trava)
        srv.EssencialFila.Enqueue(400);
        envio.Essencial("app_fechou");
        Exigir(Esperar(() => envio.EssencialPendentes == 0, 30_000), "o lote recusado com 400 ficou na fila para sempre");
        Exigir(srv.EssencialSomado().GetValueOrDefault("app_fechou") == 0, "o lote recusado foi contado");
        envio.Essencial("live_comecou", "assiste");
        Exigir(Esperar(() => srv.EssencialSomado().GetValueOrDefault("live_comecou.assiste") == 1, 30_000), "depois de descartar o lote a fila devia seguir");
        envio.Fechar(TimeSpan.FromSeconds(2));
    }

    static void EssencialAgrupa()
    {
        EsperasCurtas();
        using var srv = new ServidorFalso();
        var s = Sessao(5, 1);
        using var envio = EnvioEssencial(srv, s, ModoEnvio.Completo);
        // tudo na fila antes de o laço rodar, para sair junto
        Envio.EsperaEssencial = TimeSpan.FromSeconds(2);
        for (int i = 0; i < 30; i++) envio.Essencial("falha_grave", "captura_travada");
        for (int i = 0; i < 25; i++) envio.Essencial("live_acabou", "transmite", 60 + i); // com valor: um item por evento, 25 > 20 por pedido
        envio.Iniciar(Array.Empty<SessaoLocal>());
        Exigir(Esperar(() => srv.EssencialSomado().GetValueOrDefault("falha_grave.captura_travada") == 30 && srv.EssencialSomado().GetValueOrDefault("live_acabou.transmite") == 25, 60_000),
            "as contagens não bateram: " + string.Join(",", srv.EssencialSomado().Select(kv => kv.Key + "=" + kv.Value)));
        envio.Fechar(TimeSpan.FromSeconds(2));
        foreach (var (_, corpo) in srv.Essenciais())
        {
            Exigir(corpo["eventos"]!.AsArray().Count <= 20, "mais de 20 eventos num pedido");
            Exigir(corpo.ToJsonString().Length <= 4096, "corpo acima de 4096 bytes");
        }
        Exigir(srv.PedidosEssenciais >= 2, "25 eventos com valor não cabem em um pedido de 20: " + srv.PedidosEssenciais);
    }

    // ---------- cifra do envio (spec 018, 7.2; plano A9) ----------

    static JsonObject PerfilCompleto() => new()
    {
        ["estavel"] = new JsonObject
        {
            ["plataforma"] = "windows",
            ["cpu"] = new JsonObject { ["nome"] = "AMD Ryzen 5 5600", ["nucleos"] = 6, ["threads"] = 12 },
            ["ram"] = new JsonObject { ["total_gb"] = 16 },
            ["windows"] = new JsonObject { ["produto"] = "Windows 11 Pro", ["versao"] = "24H2", ["build"] = 26100, ["ubr"] = 2894 },
            ["gpus"] = new JsonArray(new JsonObject { ["nome"] = "NVIDIA GeForce RTX 4060", ["fabricante"] = "NVIDIA", ["driver"] = "32.0.15.6094", ["driver_data"] = "2026-10-01", ["vram_mb"] = 8192, ["integrada"] = false, ["pci"] = "10de:2882" }),
            ["audio"] = new JsonArray(new JsonObject { ["nome"] = "FONES-DA-PESSOA-XYZ" }),
            ["rede"] = new JsonArray(new JsonObject { ["descricao"] = "ADAPTADOR-DE-REDE-XYZ" }),
            ["telas"] = new JsonArray(new JsonObject { ["nome"] = "TELA-XYZ" }),
        },
        ["momento"] = new JsonObject { ["hora_utc"] = "2026-11-26T12:00:00Z", ["programas_que_interferem"] = new JsonArray("PROGRAMA-XYZ.exe") },
    };

    static bool Contem(byte[] bytes, string texto) => bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(texto)) >= 0;

    static void CifraNoEnvio()
    {
        using var srv = new ServidorFalso();
        var s = Sessao(120, 5);
        const string NOTA = "NOTA-PRIVADA-DA-PESSOA-777";
        s.Evento("marcacao", new JsonObject { ["marca"] = "m1", ["por"] = "botão", ["texto"] = NOTA });
        s.Evento("marcacao_nota", new JsonObject { ["marca"] = "m1", ["texto"] = NOTA });
        s.Evento("falha", new JsonObject { ["tipo_falha"] = "outra", ["linha"] = "Falha ao iniciar a transmissão: nenhum encoder funcionou." });
        s.Retrato("relatorios/rel-1.txt", "relatório com " + NOTA);
        s.Retrato("perfil-teste.json", PerfilCompleto().ToJsonString());
        Gravador.Descarregar(TimeSpan.FromSeconds(10));
        using var envio = Criar(srv, s);
        envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>());
        envio.DefinirPerfil(PerfilCompleto(), "hw-teste");
        Exigir(Esperar(() => Converge(s), 30_000), "não confirmou tudo: " + Estado(s));
        envio.Fechar(TimeSpan.FromSeconds(3));
        ExigirIgual(srv, s); // o servidor abriu os quadros com a chave de teste e remontou byte a byte (e não houve pacote em claro)

        // todo pedaço chegou como envelope cifrado, sem texto legível do log, do relatório nem da nota
        foreach (var (nome, _) in s.Arquivos())
            foreach (var bruto in srv.PecasBrutas($"{s.Id}|{nome}"))
                Exigir(bruto.Length > 100 && Encoding.ASCII.GetString(bruto, 0, 4) == "SEC1" && !Contem(bruto, "linha") && !Contem(bruto, NOTA), $"{nome}: pedaço não está cifrado");
        Exigir(Encoding.UTF8.GetString(srv.Conteudo($"{s.Id}|{SessaoLocal.Eventos}")).Contains(NOTA), "o arquivo de eventos, aberto, devia ter a nota (a investigação a fundo precisa dela)");
        Exigir(Encoding.UTF8.GetString(srv.Conteudo($"{s.Id}|relatorios/rel-1.txt")).Contains(NOTA), "o relatório aberto devia ter a nota");

        foreach (var c in srv.Cabecalhos())
        {
            string json = c.ToJsonString();
            Exigir(c["cifra"]?["v"]?.GetValue<int>() == 1 && c["cifra"]?["chave_v"]?.GetValue<int>() == CifraEnvio.VersaoAtual, "cabeçalho sem cifra {v:1, chave_v}: " + c["cifra"]);
            Exigir(!json.Contains(NOTA), "a nota apareceu legível no cabeçalho");
            foreach (var segredo in new[] { "FONES-DA-PESSOA-XYZ", "ADAPTADOR-DE-REDE-XYZ", "TELA-XYZ", "PROGRAMA-XYZ", "10de:2882" })
                Exigir(!json.Contains(segredo), $"{segredo} saiu legível no cabeçalho");
        }
        // o legível: resumo do perfil (CPU, RAM, Windows, GPUs) e a hora, mais o evento enxuto; marcação e nota sem o texto livre
        var comPerfil = srv.Cabecalhos().First(c => c["perfil"] != null);
        var estavel = comPerfil["perfil"]!["estavel"]!;
        Exigir(estavel["cpu"]!["nome"]!.GetValue<string>() == "AMD Ryzen 5 5600" && estavel["ram"]!["total_gb"]!.GetValue<double>() == 16 && estavel["windows"]!["build"]!.GetValue<int>() == 26100
            && estavel["gpus"]![0]!["nome"]!.GetValue<string>() == "NVIDIA GeForce RTX 4060" && comPerfil["perfil"]!["momento"]!["hora_utc"]!.GetValue<string>() == "2026-11-26T12:00:00Z", "o resumo legível do perfil não veio: " + comPerfil["perfil"]);
        Exigir(estavel["audio"] == null && estavel["rede"] == null && estavel["telas"] == null && estavel["gpus"]![0]!["pci"] == null && comPerfil["perfil"]!["momento"]!["programas_que_interferem"] == null, "o perfil legível passou do que a spec deixa: " + comPerfil["perfil"]);
        var aberto = JsonNode.Parse(Encoding.UTF8.GetString(ServidorFalso.Chave.Decifrar(Convert.FromBase64String(comPerfil["perfil_cifrado"]!.GetValue<string>()))))!;
        Exigir(aberto["estavel"]!["audio"]![0]!["nome"]!.GetValue<string>() == "FONES-DA-PESSOA-XYZ" && aberto["momento"]!["programas_que_interferem"]![0]!.GetValue<string>() == "PROGRAMA-XYZ.exe", "o perfil completo, aberto, não bate: " + aberto);
        Exigir(srv.UltimoEvento("marcacao") is { } m && m["marca"]!.GetValue<string>() == "m1" && m["texto"] == null && m["por"]!.GetValue<string>() == "botão", "marcacao legível errada: " + srv.UltimoEvento("marcacao"));
        Exigir(srv.UltimoEvento("marcacao_nota") is { } n && n["marca"]!.GetValue<string>() == "m1" && n["texto"] == null, "marcacao_nota subiu com o texto livre: " + srv.UltimoEvento("marcacao_nota"));
        Exigir(srv.UltimoEvento("falha") is { } f && f["tipo_falha"]!.GetValue<string>() == "outra" && f["linha"]!.GetValue<string>()!.StartsWith("Falha ao iniciar"), "a falha (tipo e linha) tem que continuar legível: " + srv.UltimoEvento("falha"));
        Exigir(srv.ContarEventos("teste") == 5, "os eventos enxutos tinham que chegar legíveis ao índice");
    }

    static void CifraQuadroEForma()
    {
        byte[] claro = Encoding.UTF8.GetBytes("diagnóstico de teste, com acento");
        byte[] q = CifraEnvio.Quadro(claro), q2 = CifraEnvio.Quadro(claro);
        Exigir(Encoding.ASCII.GetString(q, 0, 4) == "SEC1" && q[4] == 0 && q[5] == CifraEnvio.VersaoAtual && q[6] == 0x04, "cabeçalho do quadro fora do contrato");
        Exigir(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(q.AsSpan(83)) == claro.Length + 16 && q.Length == 87 + claro.Length + 16, "tamanho do quadro não bate");
        Exigir(!q.AsSpan(7, 64).SequenceEqual(q2.AsSpan(7, 64)) && !q.AsSpan(71, 12).SequenceEqual(q2.AsSpan(71, 12)), "cada quadro precisa de chave efêmera e IV próprios");
        Exigir(ServidorFalso.Chave.Decifrar(q).AsSpan().SequenceEqual(claro), "decifrou algo diferente");
        Exigir(ServidorFalso.Chave.Decifrar(q.Concat(q2).ToArray()).Length == claro.Length * 2, "dois quadros colados devem abrir em sequência");
        // adulterar qualquer parte (texto, etiqueta, IV, versão que entra no AAD) tem que reprovar a etiqueta do AES-GCM
        foreach (int pos in new[] { 90, q.Length - 1, 75, 5 })
        {
            var ruim = (byte[])q.Clone(); ruim[pos] ^= 1;
            bool falhou = false;
            try { ServidorFalso.Chave.Decifrar(ruim, pos == 5 ? 257 : 1); } catch { falhou = true; }
            Exigir(falhou, $"quadro adulterado no byte {pos} foi aceito");
        }
        // outra chave não abre
        bool outra = false;
        try { new ChaveDeTeste().Decifrar(q); } catch { outra = true; }
        Exigir(outra, "outra chave abriu o quadro");
        // versão da chave de teste vai no quadro e no cabeçalho
        string? antes = Environment.GetEnvironmentVariable(CifraEnvio.VarVersaoTeste);
        try
        {
            Environment.SetEnvironmentVariable(CifraEnvio.VarVersaoTeste, "7");
            var q7 = CifraEnvio.Quadro(claro);
            Exigir(q7[4] == 0 && q7[5] == 7 && CifraEnvio.Cabecalho()["chave_v"]!.GetValue<int>() == 7 && ServidorFalso.Chave.Decifrar(q7, 7).Length == claro.Length, "versão 7 não acompanhou a chave");
        }
        finally { Environment.SetEnvironmentVariable(CifraEnvio.VarVersaoTeste, antes); }
    }

    static void CifraChaveEmbutidaEIgnoraTesteForaDeTeste()
    {
        const string Esperada = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAESEcqlyDQK1cdRXBAOri4KP1K5v5zPhE2ddbvkXtZqrmY8v4pf61ItcEacaZSEtlKVcITlHUA54up7yBltUOogQ==";
        bool teste = Streemu.Net.RelayConfig.ModoTeste;
        try
        {
            Streemu.Net.RelayConfig.ModoTeste = false; // como no app de verdade: a variável de teste é ignorada
            var (v, spki) = CifraEnvio.ChaveAtual();
            Exigir(v == 1 && Convert.ToBase64String(spki) == Esperada, "fora de teste a chave tem que ser a embutida (v1)");
            using var p256 = System.Security.Cryptography.ECDiffieHellman.Create();
            p256.ImportSubjectPublicKeyInfo(spki, out _);
            Exigir(p256.KeySize == 256, "a chave embutida não é P-256");
            bool recusou = false;
            try { ServidorFalso.Chave.Decifrar(CifraEnvio.Quadro(new byte[] { 1, 2, 3 })); } catch { recusou = true; }
            Exigir(recusou, "fora de teste o quadro foi cifrado para a chave de teste");
        }
        finally { Streemu.Net.RelayConfig.ModoTeste = teste; }
        Exigir(Convert.ToBase64String(CifraEnvio.ChaveAtual().Spki) == ServidorFalso.Chave.Spki, "em teste a chave de teste tem que valer");
    }

    static void CifraFalhaNaoCaiParaOClaro()
    {
        using var srv = new ServidorFalso();
        var s = Sessao(40, 3);
        string? antes = Environment.GetEnvironmentVariable(CifraEnvio.VarChaveTeste);
        try
        {
            Environment.SetEnvironmentVariable(CifraEnvio.VarChaveTeste, Convert.ToBase64String(new byte[] { 1, 2, 3, 4 })); // não é uma chave
            using var envio = Criar(srv, s);
            envio.Ativo = true; envio.Iniciar(Array.Empty<SessaoLocal>()); envio.Agora();
            Exigir(Esperar(() => envio.UltimoErro != null, 15_000), "o app não percebeu que não conseguiu cifrar");
            Exigir(envio.UltimoErro!.Contains("não consegui cifrar") && envio.UltimoErro.Contains("nada sai em claro"), "mensagem inesperada: " + envio.UltimoErro);
            Exigir(envio.Situacao().Estado == EstadoEnvio.Falha, "o indicador devia ficar vermelho");
            Exigir(srv.Cabecalhos().Length == 0 && srv.Tamanho($"{s.Id}|log.txt") == 0 && s.Cursor(SessaoLocal.Log) == 0, "algo subiu sem cifra");
            // a chave volta: o envio se recupera sozinho e entrega tudo
            Environment.SetEnvironmentVariable(CifraEnvio.VarChaveTeste, antes);
            envio.Agora();
            Exigir(Esperar(() => Converge(s), 45_000, () => envio.Agora()), "não recuperou depois da chave voltar: " + Estado(s));
            envio.Fechar(TimeSpan.FromSeconds(3));
            ExigirIgual(srv, s);
        }
        finally { Environment.SetEnvironmentVariable(CifraEnvio.VarChaveTeste, antes); }
    }

    static void CifraSessaoAntigaTerminaEmClaro()
    {
        using var srv = new ServidorFalso { AceitaClaro = true };
        var nova = Sessao(30, 3);
        var velha = Sessao(60, 4);
        velha.E.Fechou = "normal"; velha.E.Cifrada = false; velha.SetCursor(SessaoLocal.Log, 10); velha.Salvar(); // de um app antigo que já mandou parte do log em claro
        var intacta = Sessao(20, 2);
        intacta.E.Fechou = "normal"; intacta.E.Cifrada = false; intacta.Salvar(); // de app antigo, mas sem nada enviado: pode subir cifrada
        using var envio = Criar(srv, nova);
        envio.Ativo = true; envio.Iniciar(new[] { velha, intacta });
        Exigir(intacta.E.Cifrada && !velha.E.Cifrada, "a sessão antiga sem nada enviado vira cifrada; a que já começou em claro, não");
        envio.DefinirPerfil(PerfilCompleto(), "hw-teste");
        envio.Agora();
        Exigir(Esperar(() => Converge(nova) && Converge(velha) && Converge(intacta), 40_000, () => envio.Agora()), "não subiu tudo: " + Estado(nova) + " | " + Estado(velha) + " | " + Estado(intacta));
        envio.Fechar(TimeSpan.FromSeconds(3));
        ExigirIgual(srv, nova); ExigirIgual(srv, velha); ExigirIgual(srv, intacta);
        Exigir(srv.PecasBrutas($"{nova.Id}|log.txt").All(b => b[0] == (byte)'S') && srv.PecasBrutas($"{intacta.Id}|log.txt").All(b => b[0] == (byte)'S'), "as sessões de agora têm que subir cifradas");
        Exigir(srv.PecasBrutas($"{velha.Id}|log.txt").All(b => b[0] == 0x1f && b[1] == 0x8b), "a sessão que começou em claro tem que terminar em claro (o servidor não mistura)");
        Exigir(srv.Cabecalhos().Where(c => c["cifra"] == null).All(c => c["perfil"] == null && c["perfil_cifrado"] == null), "o pacote em claro não pode levar o perfil");
        Exigir(srv.Cabecalhos().Any(c => c["cifra"] != null && c["perfil_cifrado"] != null), "o perfil cifrado devia ter ido no pacote cifrado");
    }
}
