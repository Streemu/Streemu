using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Streemu.Util;

namespace Streemu.Telemetria;

/// <summary>Quem está enviando (lido a cada pacote; pode mudar com o app aberto).</summary>
public sealed record Identidade(string? DiscordId, string? DiscordNome, string? Apelido, string? Install, string? Conta = null);

/// <summary>O que o indicador da barra lateral mostra: verde (✓), vermelho (✗) ou cinza (desligado/preparando).</summary>
public enum EstadoEnvio { Neutro, Ok, Falha }

/// <summary>Situação do diagnóstico para a tela: o estado, uma frase curta e o detalhe (dica ao passar o mouse e Ajustes).</summary>
public sealed record SituacaoEnvio(EstadoEnvio Estado, string Titulo, string Detalhe);

/// <summary>
/// Envia ao Worker do grupo, por posição no arquivo, o que as sessões gravaram (spec 009, seção 6). Regras que evitam perder ou duplicar:
/// cada pedaço sai do cursor (byte já confirmado) até o fim atual do arquivo, cortado na última quebra de linha; o cursor só anda com a
/// confirmação do servidor; se o servidor diz outro número (resposta perdida, cursor velho), o cursor vira esse número e o próximo envio
/// sai de lá, já com as linhas novas. Roda numa thread própria de prioridade baixa e nunca derruba o app.
/// </summary>
public sealed class Envio : IDisposable
{
    const int PedacoMax = 512 * 1024, PacoteMax = 1536 * 1024, EventosPorPacote = 200;
    static readonly int[] Espera = { 2, 5, 15, 60, 300 };

    readonly Uri _url;
    /// <summary>Verdadeiro enquanto o envio está sem credencial (sem conta, ou conta sem token/bilhete no momento): nenhum arquivo sobe e o sinal fica neutro, nunca verde nem vermelho.</summary>
    volatile bool _semCredencial;
    readonly Func<Identidade> _quem;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    readonly AutoResetEvent _acordar = new(false);
    readonly object _rodada = new();
    readonly List<SessaoLocal> _sessoes = new();
    readonly Thread _thread;
    volatile bool _parar, _urgente;
    int _falhas;
    DateTime _ultimoContato = DateTime.MinValue, _ultimoAvisoLog = DateTime.MinValue;
    JsonObject? _perfilPendente;
    string? _hw;

    public SessaoLocal Atual { get; }
    public string Pc { get; } = Ids.Pc;
    /// <summary>Transmitindo ou assistindo: envia a cada <see cref="RitmoAtivo"/> s. Atualizado pela tela.</summary>
    public volatile bool Ativo;
    /// <summary>Desligado nos ajustes: não envia nada (e as sessões criadas assim nunca sobem).</summary>
    public volatile bool Habilitado = true;
    /// <summary>Diagnóstico desligado em Ajustes: continua subindo, mas anônimo (<see cref="Anonimizador"/>): id de PC e de instalação próprios, sem Discord, apelido nem hardware, com os nomes trocados por pessoa-N.</summary>
    public bool Reduzido => !Habilitado;
    /// <summary>Instalação nova: nada sobe até a pessoa fechar o aviso do diagnóstico (<see cref="TextoDiagnostico"/>). Continua gravando no disco; só o envio espera.</summary>
    public volatile bool Aguardando;
    readonly string _idAnonimo;
    /// <summary>O id do PC no modo anônimo: não sai do MachineGuid, então o servidor não liga esse PC ao que já conhece pelo nome.</summary>
    public string PcAnonimo { get; }
    public int RitmoAtivo { get; private set; } = 5;
    public int RitmoParado { get; private set; } = 60;
    /// <summary>Relógio do Worker menos o deste PC (ms), medido a cada envio: alinha a linha do tempo de PCs diferentes.</summary>
    public long? RelogioDifMs { get; private set; }
    public DateTime? UltimoOk { get; private set; }
    public string? UltimoErro { get; private set; }
    public long BytesEnviados { get; private set; }

    /// <summary>
    /// Arquivo que o servidor não aceita ("recusados" na resposta) ou cujo ponto não bate com o do disco (confirmado maior que o arquivo, arquivo
    /// local menor que o confirmado). Ele sai do envio e os outros seguem: antes o app repetia o pedido até 20 vezes por rodada, de tempos em tempos,
    /// sem nunca conseguir. Recusa do servidor volta a ser tentada com espera crescente (<see cref="EsperaRecusa"/>, dobrando até 1 h); ponto
    /// inconsistente não: nenhum reenvio resolve (o servidor só grava a partir do byte que ele já tem), então fica até a próxima abertura do app.
    /// </summary>
    sealed class Recusa { public string Motivo = ""; public bool Permanente; public int Vezes; public DateTime Proxima; }
    readonly ConcurrentDictionary<string, Recusa> _recusas = new();
    /// <summary>Espera da primeira nova tentativa de um arquivo recusado pelo servidor (dobra a cada recusa, no máximo 1 h). Os testes encurtam.</summary>
    public static TimeSpan EsperaRecusa { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Arquivos fora do envio agora, para a tela e os testes: sessão, arquivo e por quê.</summary>
    public IReadOnlyList<(string Sessao, string Arquivo, string Motivo)> Recusados =>
        _recusas.Select(kv => { int b = kv.Key.IndexOf('|'); return (kv.Key[..b], kv.Key[(b + 1)..], kv.Value.Motivo); }).OrderBy(x => x.Item1).ThenBy(x => x.Item2).ToList();

    void Recusar(SessaoLocal s, string nome, string motivo, bool permanente)
    {
        string chave = $"{s.Id}|{nome}";
        bool nova = !_recusas.TryGetValue(chave, out var r);
        r ??= new Recusa();
        r.Vezes++; r.Motivo = motivo; r.Permanente = permanente;
        r.Proxima = permanente ? DateTime.MaxValue : DateTime.UtcNow + TimeSpan.FromTicks((long)Math.Min(TimeSpan.FromHours(1).Ticks, EsperaRecusa.Ticks * Math.Pow(2, r.Vezes - 1)));
        _recusas[chave] = r;
        if (!nova) return; // uma linha de log e um evento por arquivo, não um por tentativa
        Log.Info($"[telemetria] o servidor não aceita o arquivo {nome} da sessão {s.Id} ({motivo}); os outros arquivos seguem.");
        Atual.Evento("envio_recusado", new JsonObject { ["arquivo"] = nome, ["sessao_afetada"] = s.Id, ["motivo"] = motivo, ["permanente"] = permanente });
    }

    bool EmEspera(SessaoLocal s, string nome) => _recusas.TryGetValue($"{s.Id}|{nome}", out var r) && DateTime.UtcNow < r.Proxima;

    readonly DateTime _criado = DateTime.UtcNow;
    /// <summary>Fim da última rodada em que o servidor confirmou TUDO o que existia no disco quando ela começou (UTC).</summary>
    DateTime _emDiaEm = DateTime.MinValue;
    /// <summary>Até quando o laço deve dar sinal de vida (espera atual + uma rodada inteira + folga). Passou disso: o envio parou.</summary>
    DateTime _vivoAte = DateTime.UtcNow.AddMinutes(2);
    DateTime _proximaTentativa = DateTime.MinValue;

    /// <summary>
    /// Situação fiel do envio para o indicador (spec 009): verde SÓ quando o servidor confirmou ter gravado tudo o que este PC tinha para
    /// mandar, há pouco tempo. "Não deu erro" não basta: se o servidor não confirma, se o laço parou ou se o disco não grava, é vermelho.
    /// </summary>
    public SituacaoEnvio Situacao()
    {
        var agora = DateTime.UtcNow;
        if (Aguardando)
            return new(EstadoEnvio.Neutro, "Diagnóstico aguardando", "O diagnóstico só começa a subir depois que você fechar o aviso sobre ele (na janela do Streemu).");
        if (Atual.E.NaoEnviar)
            return new(EstadoEnvio.Neutro, "Diagnóstico desligado", "Você desligou nesta sessão: nada mais sai do PC até abrir o Streemu de novo. Ao abrir, sobe só o mínimo (versão e falhas graves, sem log, sem perfil do PC e sem seu nome). Para ligar tudo: Ajustes > Diagnóstico > \"Enviar diagnóstico\".");
        if (Atual.FalhaLocal is string disco)
            return new(EstadoEnvio.Falha, "Diagnóstico falhando", $"Não consigo gravar o diagnóstico no disco ({disco}). Disco cheio ou pasta bloqueada pelo antivírus?");
        if (Log.FalhaGravacao is string log)
            return new(EstadoEnvio.Falha, "Diagnóstico falhando", $"O log do app parou de ser gravado ({log}). Disco cheio ou pasta bloqueada pelo antivírus?");
        if (!_thread.IsAlive || agora > _vivoAte)
            return new(EstadoEnvio.Falha, "Diagnóstico parado", $"O envio automático parou de rodar. Feche o {UiBrand.Name} pela bandeja (Sair) e abra de novo.");
        if (_semCredencial)
            return new(EstadoEnvio.Neutro, "Diagnóstico aguardando a conta", "Sem conta o diagnóstico não sobe: ele fica guardado no PC e sobe depois que você entrar. Só os passos do login (sem nome, e-mail nem código) vão agora, para a gente ver se o login está quebrado. Entre na sua conta para ligar tudo.");
        var fora = Recusados;
        if (fora.Count > 0)
        {
            string nomes = string.Join(", ", fora.Take(3).Select(x => x.Arquivo)) + (fora.Count > 3 ? "..." : "");
            return new(EstadoEnvio.Falha, "Diagnóstico incompleto", $"O servidor não aceita {fora.Count} arquivo(s) do diagnóstico ({nomes}): {fora[0].Motivo}. O resto segue sendo enviado. Avise a equipe do Streemu.");
        }
        if (UltimoErro != null)
        {
            int em = (int)Math.Max(1, (_proximaTentativa - agora).TotalSeconds);
            return new(EstadoEnvio.Falha, "Diagnóstico falhando", $"O último envio falhou: {UltimoErro.TrimEnd('.', ' ')}. Nada se perde: fica guardado no PC e tento de novo sozinho em ~{em} s.");
        }
        // prazo: o contato mais espaçado (60 s parado) + uma rodada inteira (20 s) + folga. Ao vivo os envios saem a cada 5 s e uma falha já
        // vira vermelho na hora (UltimoErro); o prazo só pega o caso "sem erro, mas sem confirmação". Prazo pelo ritmo ATIVO dava vermelho
        // falso no instante em que a pessoa começava a transmitir (a última confirmação de quando estava parada podia ter 60 s).
        var prazo = TimeSpan.FromSeconds(RitmoParado + 20 + 15);
        if (_emDiaEm == DateTime.MinValue)
            return agora - _criado < TimeSpan.FromSeconds(90)
                ? new(EstadoEnvio.Neutro, "Preparando diagnóstico", "Fazendo o primeiro envio.")
                : new(EstadoEnvio.Falha, "Diagnóstico falhando", "Faz mais de 90 s que abri e o servidor ainda não confirmou o primeiro envio. Tento de novo sozinho.");
        if (agora - _emDiaEm > prazo)
            return new(EstadoEnvio.Falha, "Diagnóstico atrasado", $"O servidor não confirma o que mando desde {_emDiaEm.ToLocalTime():HH:mm:ss}. Fica guardado no PC e tento de novo sozinho.");
        string contato = UltimoOk is DateTime ok ? $" Último contato com o servidor: {ok:HH:mm:ss}." : "";
        if (Reduzido) return new(EstadoEnvio.Neutro, "Diagnóstico anônimo", "Você desligou o diagnóstico: o app ainda manda log e dados do PC para consertar bugs, mas sem o seu nome, o seu Discord nem o nome dos amigos que ele conhece (trocados por pessoa-N). Para mandar identificado: Ajustes > Diagnóstico > \"Enviar diagnóstico\".");
        return new(EstadoEnvio.Ok, "Diagnóstico em dia", $"O servidor está no ar e confirmou tudo o que este PC gravou ({_emDiaEm.ToLocalTime():HH:mm:ss}).{contato} Viu um bug? Marque o problema.");
    }

    /// <summary>Token de acesso da conta Streemu (o parâmetro pede um novo, depois de um 401). Nulo ou devolvendo nulo = sem conta: nada sobe, só os eventos do login (<see cref="LoginSemConta"/>).
    /// Só o envio NORMAL usa; o anônimo nunca manda o token.</summary>
    public Func<bool, string?>? Token { get; set; }
    /// <summary>Bilhete do dia da conta, que o envio ANÔNIMO usa no lugar do token (prova que é do grupo sem dizer quem).</summary>
    public Func<string?>? Bilhete { get; set; }
    bool _renovarToken;

    public Envio(string servidor, SessaoLocal atual, Func<Identidade> quem, string? idAnonimo = null)
    {
        _idAnonimo = string.IsNullOrWhiteSpace(idAnonimo) ? Guid.NewGuid().ToString("N") : idAnonimo;
        PcAnonimo = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("streemu-anonimo:" + _idAnonimo)))[..16].ToLowerInvariant();
        _url = new Uri(servidor.TrimEnd('/') + "/telemetria/enviar");
        _quem = quem; Atual = atual;
        _sessoes.Add(atual);
        _thread = new Thread(Laco) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "telemetria" };
    }

    /// <summary>A sessão foi apagada do disco (teto local, <see cref="Manutencao.Limpar"/>): sai da lista, senão o envio ficaria olhando uma pasta que não existe.</summary>
    public void Esquecer(string sessaoId) { lock (_sessoes) _sessoes.RemoveAll(s => s.Id == sessaoId && s != Atual); }

    public void Iniciar(IEnumerable<SessaoLocal> anteriores)
    {
        // sessões de aberturas anteriores no modo contrário (anônimas com o diagnóstico ligado, ou completas com ele desligado) nunca sobem:
        // subir a anônima pelo id real ligaria uma ao outro, e a completa é de quando a pessoa ainda não tinha desligado
        var uteis = new List<SessaoLocal>();
        foreach (var s in anteriores)
        {
            if (s.E.Reduzida == Reduzido) uteis.Add(s);
            else if (!s.E.NaoEnviar) { s.E.NaoEnviar = true; s.Salvar(); }
        }
        lock (_sessoes) _sessoes.AddRange(uteis);
        _thread.Start();
    }

    /// <summary>Perfil do PC para o índice (só quando mudou desde o último aceito) e a impressão do hardware.</summary>
    public void DefinirPerfil(JsonObject? perfilParaIndice, string? hw)
    {
        _hw = hw;
        _perfilPendente = perfilParaIndice;
        Agora();
    }

    /// <summary>Envia já (agrupando o que chegar no próximo ~1 s): marcação, falha, fim de live, "envie agora" do admin.</summary>
    public void Agora() { _urgente = true; _acordar.Set(); }

    void Laco()
    {
        while (!_parar)
        {
            int segundos = _falhas > 0 ? Espera[Math.Min(_falhas, Espera.Length) - 1] : Ativo ? RitmoAtivo : RitmoParado;
            if (_falhas > 0) segundos = (int)(segundos * (0.8 + Random.Shared.NextDouble() * 0.4));
            segundos = Math.Max(1, segundos);
            _proximaTentativa = DateTime.UtcNow.AddSeconds(segundos);
            _vivoAte = _proximaTentativa.AddSeconds(25 + 30); // + uma rodada inteira (20 s) + folga
            bool acordou = _acordar.WaitOne(TimeSpan.FromSeconds(segundos));
            if (_parar) break;
            if (acordou && _urgente && _falhas == 0) Thread.Sleep(800); // agrupa o que vier junto (ex.: marcação + relatório)
            _urgente = false;
            try { Rodada(TimeSpan.FromSeconds(20)); }
            catch (Exception e) { Falhou(e.GetBaseException().Message); }
            if (!_parar && DateTime.UtcNow - _ultimoCusto >= IntervaloCusto) RegistrarCusto(); // fechando: o Fechar grava o último custo antes do sessao_fim
        }
    }

    static readonly TimeSpan IntervaloCusto = TimeSpan.FromMinutes(5);
    DateTime _ultimoCusto = DateTime.UtcNow;

    /// <summary>Grava o evento <c>telemetria_custo</c> (o que a telemetria gastou neste PC desde o anterior; ver <see cref="Custo"/>). A cada 5 min e ao fechar.</summary>
    public void RegistrarCusto()
    {
        _ultimoCusto = DateTime.UtcNow;
        Atual.Evento("telemetria_custo", Custo.Delta());
    }

    /// <summary>Envia tudo o que há de novo, em quantos pacotes precisar. Devolve falso se algo falhou (fica para a próxima).</summary>
    bool Rodada(TimeSpan limite)
    {
        if (Aguardando) return true; // ainda não leu o aviso: nada sai (e não conta como falha)
        if (!TemCredencial())
        {
            // spec 022 (Q2-B): sem conta não sobe arquivo, só os eventos do login. Não é falha: o sinal fica neutro.
            _semCredencial = true;
            LoginSemConta.EnviarPendentes(_http, new Uri(_url.AbsoluteUri[..^"enviar".Length] + "login"), Pc, Program.Version, BuildInfo.Commit.Length >= 7 ? BuildInfo.Commit[..7] : "", limite);
            return true;
        }
        _semCredencial = false;
        if (!Monitor.TryEnter(_rodada, limite)) return false;
        try
        {
            var ate = DateTime.UtcNow + limite;
            // o que as outras threads puseram na fila de gravação chega ao disco antes de eu olhar os arquivos (a fila existe para a interface e a mídia não esperarem; esta thread pode)
            Gravador.Descarregar(TimeSpan.FromMilliseconds(500));
            for (int i = 0; i < 20 && DateTime.UtcNow < ate; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                var p = Montar();
                Custo.Montou(Stopwatch.GetTimestamp() - t0);
                // Parado e sem novidade, ainda assim fala com o servidor a cada ~60 s: o verde do sinal promete que o servidor está
                // confirmando AGORA, e com contato só a cada 5 min ele ficava verde com o servidor fora do ar (visto no teste de 2026-11-10).
                bool contato = DateTime.UtcNow - _ultimoContato >= TimeSpan.FromSeconds(Math.Max(10, RitmoParado - 2));
                if (p.Pecas.Count == 0 && p.Eventos.Count == 0 && p.Perda == null && _perfilPendente == null && !contato) return true;
                long r0 = Stopwatch.GetTimestamp(), b0 = BytesEnviados;
                bool enviou;
                try { enviou = Mandar(p, ate - DateTime.UtcNow); }
                catch { Custo.Rede(Stopwatch.GetTimestamp() - r0, 0, false); throw; }
                Custo.Rede(Stopwatch.GetTimestamp() - r0, BytesEnviados - b0, enviou);
                if (!enviou) return false;
                // em dia = o servidor confirmou cada pedaço até o fim que mandei e não sobrou nada que já existia (senão o laço manda o resto)
                bool tudo = p.Pecas.All(x => x.S.Cursor(x.Nome) == x.Fim);
                if (tudo && !p.TemMais) { _emDiaEm = DateTime.UtcNow; return true; }
            }
            // 20 voltas ou 20 s e o servidor ainda não confirmou tudo: é falha (o indicador fica vermelho), não "em dia"
            Falhou("o servidor não confirmou tudo o que mandei");
            return false;
        }
        finally { Monitor.Exit(_rodada); }
    }

    sealed record Peca(SessaoLocal S, string Nome, string Caminho, long Inicio, long Fim, byte[] Gz, bool Fechado, long? H0 = null, long? H1 = null);
    sealed record Pacote(List<Peca> Pecas, List<(SessaoLocal S, long Ate, JsonObject Ev)> Eventos, bool TemMais, JsonObject? Perda = null, long PerdaVersao = 0);

    Pacote Montar()
    {
        var pecas = new List<Peca>();
        var eventos = new List<(SessaoLocal, long, JsonObject)>();
        long total = 0;
        bool mais = false;
        List<SessaoLocal> sessoes;
        // reduzido: só as sessões abertas com o diagnóstico desligado; o que ficou pendente de uma sessão completa não sobe mais
        lock (_sessoes) sessoes = _sessoes.Where(s => !s.E.NaoEnviar && !s.E.Concluida && s.E.Reduzida == Reduzido).ToList();
        if (Reduzido && !Anonimizador.Pronto) sessoes.Clear(); // antes de a sala trazer os amigos um nome ainda desconhecido vazaria
        foreach (var s in sessoes)
        {
            foreach (var (nome, caminho) in s.Arquivos())
            {
                if (total >= PacoteMax) { mais = true; break; }
                if (EmEspera(s, nome)) continue;
                long cur = s.Cursor(nome), tam;
                try { tam = new FileInfo(caminho).Length; } catch { continue; }
                if (tam < cur)
                {
                    // O servidor já tem `cur` bytes e o arquivo daqui encolheu (trocado, truncado, apagado e refeito). Mandar de novo "do zero"
                    // não resolve: ele só grava a partir do byte que já tem e responderia `cur`, e o app ficava nesse vai e volta (até 20 pedidos por rodada).
                    Recusar(s, nome, $"cursor inconsistente: o servidor tem {cur} bytes e o arquivo daqui tem {tam}", permanente: true);
                    continue;
                }
                if (tam == cur) continue;
                var (bytes, usados) = Ler(caminho, cur, tam, aceitaSemQuebra: !s.Aberta || SessaoLocal.Retrato(nome));
                if (usados == 0) continue;
                if (cur + usados < tam) mais = true;
                string texto = Higiene.Limpar(Encoding.UTF8.GetString(bytes));
                if (s.E.Reduzida) texto = Anonimizador.Aplicar(texto);
                byte[] gz = Gzip(Encoding.UTF8.GetBytes(texto));
                var (h0, h1) = HoraDoConteudo.De(nome, texto, s.HoraConteudo(nome), doZero: cur == 0); // a hora do CONTEÚDO, não a do envio
                bool fechado = SessaoLocal.Retrato(nome) || (!s.Aberta && cur + usados == tam);
                pecas.Add(new Peca(s, nome, caminho, cur, cur + usados, gz, fechado, h0, h1));
                total += gz.Length;
            }
            if (eventos.Count < EventosPorPacote) LerEventos(s, eventos);
        }
        if (eventos.Count >= EventosPorPacote) mais = true; // sobrou evento para o próximo pacote
        // o que a sessão atual deixou de gravar no disco sobe em memória, mesmo com o disco ainda falhando (ver SessaoLocal.PerdaParaInformar)
        long perdaVersao = 0;
        var perda = Atual.PerdaParaInformar(out perdaVersao);
        return new Pacote(pecas, eventos, mais, perda, perdaVersao);
    }

    /// <summary>Lê do cursor até o fim (no máximo <see cref="PedacoMax"/>), cortando na última quebra de linha: linha pela metade espera o próximo envio.</summary>
    static (byte[] Bytes, int Usados) Ler(string caminho, long de, long ate, bool aceitaSemQuebra)
    {
        int n = (int)Math.Min(PedacoMax, ate - de);
        var buf = new byte[n];
        using (var f = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            f.Seek(de, SeekOrigin.Begin);
            int lidos = 0;
            while (lidos < n) { int r = f.Read(buf, lidos, n - lidos); if (r <= 0) break; lidos += r; }
            n = lidos;
        }
        int corte = Array.LastIndexOf(buf, (byte)'\n', Math.Max(0, n - 1)) + 1;
        if (corte == 0 && (aceitaSemQuebra || n == PedacoMax)) corte = n; // linha gigante ou arquivo já fechado: vai como está
        if (corte < n && aceitaSemQuebra && de + n == ate) corte = n;
        return (buf[..corte], corte);
    }

    void LerEventos(SessaoLocal s, List<(SessaoLocal, long, JsonObject)> saida)
    {
        string f = Path.Combine(s.Dir, SessaoLocal.Eventos);
        long de = s.E.Indexados, tam;
        try { tam = new FileInfo(f).Length; } catch { return; }
        if (tam <= de) return;
        var (bytes, _) = Ler(f, de, tam, aceitaSemQuebra: false);
        long pos = de;
        foreach (var linha in Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            if (saida.Count >= EventosPorPacote) return;
            pos += Encoding.UTF8.GetByteCount(linha) + 1;
            if (pos > de + bytes.Length) return; // o pedaço depois da última quebra
            if (string.IsNullOrWhiteSpace(linha)) continue;
            try
            {
                var o = JsonNode.Parse(Higiene.Limpar(linha)) as JsonObject;
                if (o != null && s.E.Reduzida) o = Anonimizador.Evento(o);
                if (o != null) saida.Add((s, pos, o));
            }
            catch { }
        }
    }

    /// <summary>401 com a credencial da conta: renova na próxima tentativa (o servidor não aceitou o token ou o bilhete).</summary>
    string RenovarDepoisDe401() { _renovarToken = true; return "o servidor não aceitou a sessão da conta (renovo e tento de novo)"; }

    /// <summary>Há token (envio normal) ou bilhete (anônimo)? Sem nenhum dos dois o servidor não aceita arquivo.</summary>
    bool TemCredencial()
    {
        try { return (Reduzido ? Bilhete?.Invoke() : Token?.Invoke(false)) != null; } catch { return false; }
    }

    bool Mandar(Pacote p, TimeSpan limite)
    {
        bool credencialDaConta;
        var quem = Reduzido ? new Identidade(null, null, null, _idAnonimo) : _quem(); // anônimo: id próprio, sem Discord nem apelido
        var perfil = _perfilPendente;
        var cab = new JsonObject
        {
            ["v"] = 1, ["pc"] = Reduzido ? PcAnonimo : Pc, ["hw"] = Reduzido ? null : _hw, ["sessao"] = Atual.Id, ["install"] = quem.Install,
            ["conta"] = quem.Conta, ["discord_id"] = quem.DiscordId, ["discord_nome"] = quem.DiscordNome, ["apelido"] = quem.Apelido,
            ["versao"] = Program.Version, ["commit"] = BuildInfo.Commit.Length >= 7 ? BuildInfo.Commit[..7] : "", ["canal"] = BuildInfo.Canal,
            // o perfil do índice também passa pelo Higiene: em 11/11 o MAC do Wi-Fi chegou ao pc.json do servidor por aqui, não só pelo arquivo perfil-*.json
            ["relogio"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["ativo"] = Ativo, ["perfil"] = perfil == null ? null : JsonNode.Parse(Reduzido ? Anonimizador.Aplicar(Higiene.Limpar(perfil.ToJsonString())) : Higiene.Limpar(perfil.ToJsonString())),
            ["pecas"] = new JsonArray(p.Pecas.Select(x => (JsonNode)new JsonObject
            {
                ["sessao"] = x.S.Id, ["arquivo"] = x.Nome, ["inicio"] = x.Inicio, ["fim"] = x.Fim, ["tam"] = x.Gz.Length,
                ["versao"] = x.S.E.Versao, ["commit"] = x.S.E.Commit, ["canal"] = x.S.E.Canal, // do app que criou a sessão (não o de agora)
                ["h0"] = HoraDoConteudo.Iso(x.H0), ["h1"] = HoraDoConteudo.Iso(x.H1), ["fechado"] = x.Fechado,
            }).ToArray()),
            ["eventos"] = new JsonArray(p.Eventos.Select(e => e.Ev.DeepClone()).Concat(p.Perda == null ? Array.Empty<JsonNode>() : new JsonNode[] { p.Perda.DeepClone() }).ToArray()),
        };
        byte[] json = Encoding.UTF8.GetBytes(cab.ToJsonString());
        var corpo = new MemoryStream();
        corpo.Write(new[] { (byte)(json.Length >> 24), (byte)(json.Length >> 16), (byte)(json.Length >> 8), (byte)json.Length });
        corpo.Write(json);
        foreach (var x in p.Pecas) corpo.Write(x.Gz);

        using var req = new HttpRequestMessage(HttpMethod.Post, _url) { Content = new ByteArrayContent(corpo.ToArray()) };
        // credencial: conta (token) no envio normal, bilhete no anônimo. O token NUNCA vai no anônimo. Sem nenhum dos dois nada sobe (a senha do grupo acabou).
        string? token = null, bilhete = null;
        try { if (!Reduzido) { token = Token?.Invoke(_renovarToken); _renovarToken = false; } else bilhete = Bilhete?.Invoke(); } catch { }
        credencialDaConta = token != null || bilhete != null;
        if (token != null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        else if (bilhete != null) req.Headers.Add("x-streemu-bilhete", bilhete);
        else { _semCredencial = true; return false; } // a credencial sumiu entre a checagem da rodada e aqui
        req.Headers.Add("x-streemu-pc", Pc);
        long t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var cts = new CancellationTokenSource(limite < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : limite);
        using var resp = _http.Send(req, cts.Token);
        long t1 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string texto = new StreamReader(resp.Content.ReadAsStream()).ReadToEnd();
        if (!resp.IsSuccessStatusCode)
        {
            Falhou(resp.StatusCode switch
            {
                HttpStatusCode.Unauthorized when credencialDaConta => RenovarDepoisDe401(),
                HttpStatusCode.Unauthorized => "o servidor não aceitou o envio sem conta (entre na sua conta)",
                HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable => "o servidor do grupo ainda não tem a telemetria (quem cuida do Worker precisa publicar a versão nova)",
                var c => $"o servidor respondeu {(int)c}: {texto[..Math.Min(160, texto.Length)]}"
            });
            return false;
        }

        var r = JsonNode.Parse(texto) as JsonObject ?? throw new InvalidOperationException("resposta vazia");
        if (r["servidor"]?.GetValue<long>() is long servidor)
        {
            long dif = servidor - (t0 + t1) / 2;
            if (RelogioDifMs is not long antes || Math.Abs(antes - dif) > 1000) Atual.Evento("relogio", new JsonObject { ["dif_ms"] = dif, ["ida_e_volta_ms"] = t1 - t0 });
            RelogioDifMs = dif;
        }
        if (r["ritmo"] is JsonObject ritmo)
        {
            RitmoAtivo = Math.Clamp(ritmo["ativo_s"]?.GetValue<int>() ?? 5, 2, 120);
            RitmoParado = Math.Clamp(ritmo["parado_s"]?.GetValue<int>() ?? 60, 10, 3600);
        }
        var tocadas = new HashSet<SessaoLocal>();
        // Cada peça mandada tem que sair com um destino: confirmada, recusada ou inconsistente. Só vale a resposta de uma peça que EU mandei (antes
        // valia a de qualquer arquivo da sessão), e um número que não cabe no arquivo, negativo ou que não é número nunca vira cursor.
        var respondidas = new HashSet<string>();
        var problemas = new List<string>();
        bool avancou = false;
        string Chave(Peca x) => $"{x.S.Id}|{x.Nome}";
        if (r["confirmados"] is JsonObject conf)
            foreach (var (chave, valor) in conf)
            {
                var peca = p.Pecas.FirstOrDefault(x => Chave(x) == chave);
                if (peca == null) continue;
                respondidas.Add(chave);
                if (valor is not JsonValue jv || !jv.TryGetValue<long>(out long novo) || novo < 0) { problemas.Add($"{peca.Nome}: confirmado inválido ({valor?.ToJsonString() ?? "nulo"})"); continue; }
                long tamLocal; try { tamLocal = new FileInfo(peca.Caminho).Length; } catch { tamLocal = long.MaxValue; }
                if (novo > tamLocal) { Recusar(peca.S, peca.Nome, $"cursor inconsistente: o servidor diz ter {novo} bytes e o arquivo daqui tem {tamLocal}", permanente: true); continue; }
                if (peca.S.Cursor(peca.Nome) != novo) avancou = true;
                peca.S.SetCursor(peca.Nome, novo);
                if (novo == peca.Fim && peca.H1 is long hc) peca.S.SetHoraConteudo(peca.Nome, hc);
                if (novo > peca.Inicio) _recusas.TryRemove(chave, out _); // voltou a aceitar
                tocadas.Add(peca.S);
            }
        if (r["recusados"] is JsonArray recusados)
            foreach (var item in recusados)
            {
                string chave = item is JsonValue v && v.TryGetValue<string>(out var cs) ? cs : "";
                var peca = p.Pecas.FirstOrDefault(x => Chave(x) == chave);
                if (peca == null) continue;
                respondidas.Add(chave);
                Recusar(peca.S, peca.Nome, "o servidor recusou o arquivo (nome ou sessão que ele não aceita)", permanente: false);
            }
        foreach (var x in p.Pecas) if (!respondidas.Contains(Chave(x))) problemas.Add($"{x.Nome}: o servidor não disse o que fez com ele");
        foreach (var g in p.Eventos.GroupBy(e => e.S))
        {
            long ate = g.Max(e => e.Ate);
            if (g.Key.E.Indexados != ate) avancou = true;
            g.Key.E.Indexados = ate; tocadas.Add(g.Key);
        }
        if (perfil != null && ReferenceEquals(perfil, _perfilPendente)) { _perfilPendente = null; PerfilAceito?.Invoke(perfil); avancou = true; }
        if (p.Perda != null) Atual.PerdaInformada(p.PerdaVersao);
        foreach (var s in tocadas)
        {
            if (s != Atual && s.TudoEnviado()) s.E.Concluida = true;
            s.Salvar();
        }
        BytesEnviados += corpo.Length;
        _ultimoContato = DateTime.UtcNow;
        UltimoOk = DateTime.Now;
        if (problemas.Count > 0)
        {
            Falhou("resposta do servidor sem sentido: " + string.Join("; ", problemas.Take(2)));
            return false;
        }
        // Mandou pedaço e nada andou (o servidor confirmou o mesmo ponto de antes): repetir na hora só gasta pedido, a rodada sai com falha e espera.
        if (p.Pecas.Count > 0 && !avancou && !p.Pecas.Any(x => _recusas.ContainsKey(Chave(x))))
        {
            Falhou($"o servidor confirmou o mesmo ponto sem avançar ({p.Pecas[0].Nome})");
            return false;
        }
        UltimoErro = null;
        if (_falhas > 0) Log.Info("[telemetria] envio voltou a funcionar.");
        _falhas = 0;
        return true;
    }

    /// <summary>O índice do servidor aceitou este perfil (quem chama guarda o hash para não reenviar sem mudança).</summary>
    public event Action<JsonObject>? PerfilAceito;

    void Falhou(string motivo)
    {
        _falhas++;
        UltimoErro = motivo;
        // no log no máximo uma vez a cada 10 min: sem internet o dia inteiro não pode encher o log de avisos
        if (DateTime.Now - _ultimoAvisoLog > TimeSpan.FromMinutes(10))
        {
            _ultimoAvisoLog = DateTime.Now;
            Log.Info($"[telemetria] envio falhou ({motivo}); fica guardado no PC e tento de novo sozinho.");
        }
    }

    static byte[] Gzip(byte[] dados)
    {
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(dados);
        return ms.ToArray();
    }

    /// <summary>Última rodada ao fechar o app: espera no máximo <paramref name="limite"/> para não segurar o fechamento.</summary>
    public void Fechar(TimeSpan limite)
    {
        _parar = true;
        _acordar.Set();
        // o custo vem antes do fim: sessao_fim é a última linha da sessão (o servidor e o testes-locais contam com isso; filme-fechamento-curto pegou o custo 1 ms depois)
        if (Habilitado && !Atual.E.NaoEnviar) RegistrarCusto();
        if (Atual.Aberta) Atual.Evento("sessao_fim");
        Atual.E.Fechou = "normal";
        Atual.Salvar();
        Gravador.Descarregar(TimeSpan.FromSeconds(1)); // o que ainda está na fila de gravação vai para o disco antes de o app sair
        if (Atual.E.NaoEnviar) return;
        try { Rodada(limite); } catch { }
        Atual.Salvar();
        Gravador.Descarregar(TimeSpan.FromSeconds(1)); // a rodada também grava (relógio, recusa): o que sobrou na fila vai para o disco
    }

    public void Dispose() { _parar = true; _acordar.Set(); _http.Dispose(); }
}
