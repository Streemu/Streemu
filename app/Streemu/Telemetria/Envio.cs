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

/// <summary>
/// Os três estados do diagnóstico (spec 018, D14): <b>Completo</b> (identificado), <b>Anonimo</b> (escolha da pessoa, só em Ajustes) e <b>Desligado</b>
/// (nenhum diagnóstico sai do PC; segue só a camada essencial, <see cref="Envio.Essencial"/>).
/// </summary>
public enum ModoEnvio { Completo, Anonimo, Desligado }

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

    readonly Uri _url, _urlEssencial, _urlApagar;
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
    /// <summary>O que a pessoa escolheu em Ajustes > Diagnóstico (spec 018, D14). Desligado não faz nenhum pedido do diagnóstico: só a camada essencial sobe (<see cref="Essencial"/>).</summary>
    public volatile ModoEnvio Modo = ModoEnvio.Completo;
    /// <summary>Diagnóstico anônimo, escolhido pela pessoa (<see cref="Anonimizador"/>): id de PC e de instalação próprios, sem Discord, apelido nem hardware, com os nomes trocados por pessoa-N.</summary>
    public bool Reduzido => Modo == ModoEnvio.Anonimo;

    /// <summary>
    /// O estado de uma abertura nova, pelo que está salvo. Quem tinha desligado antes dos três estados (só <c>TelemetriaDesligada</c>) cai em Desligado:
    /// o app nunca liga o anônimo sozinho (spec 018, D12 e D14). Ex.: <c>{"TelemetriaDesligada":true}</c> → Desligado.
    /// </summary>
    public static ModoEnvio ModoDosAjustes(AppState s) => s.TelemetriaModo switch
    {
        "anonimo" => ModoEnvio.Anonimo,
        "desligado" => ModoEnvio.Desligado,
        "completo" => ModoEnvio.Completo,
        _ => s.TelemetriaDesligada ? ModoEnvio.Desligado : ModoEnvio.Completo,
    };

    /// <summary>Grava o estado escolhido nas duas formas: a nova e a antiga, que versões anteriores ainda leem.</summary>
    public static void GravarModo(AppState s, ModoEnvio modo)
    {
        s.TelemetriaModo = modo switch { ModoEnvio.Anonimo => "anonimo", ModoEnvio.Desligado => "desligado", _ => "completo" };
        s.TelemetriaDesligada = modo != ModoEnvio.Completo;
    }
    /// <summary>Instalação nova: nada sobe até a pessoa fechar o aviso do diagnóstico (<see cref="TextoDiagnostico"/>). Continua gravando no disco; só o envio espera.</summary>
    public volatile bool Aguardando;
    readonly string _idAnonimo;
    /// <summary>O id do PC no modo anônimo: não sai do MachineGuid, então o servidor não liga esse PC ao que já conhece pelo nome.</summary>
    public string PcAnonimo { get; }
    /// <summary>
    /// O id do PC como o servidor o conhece neste estado: o real no Ligado e no Desligado, o anônimo (<see cref="PcAnonimo"/>) no Anônimo. É o que vai no log,
    /// no relatório e no cabeçalho <c>x-streemu-pc</c>: o id real nunca aparece em nada que sobe no Anônimo (spec 018, D16).
    /// </summary>
    public string PcParaLog => Reduzido ? PcAnonimo : Pc;
    /// <summary>
    /// A chave local do anônimo (o ID aleatório do <c>estado.json</c>), que prova ao servidor que o PC anônimo é seu: o servidor chega a <see cref="PcAnonimo"/>
    /// por SHA-256 dela (<c>relay/telemetria.js</c>, <c>pcDoAnonimo</c>), então ela só sai nos pedidos de exportar e apagar o que o anônimo mandou, nunca num
    /// envio de diagnóstico. Antes ela ia como <c>install</c> no cabeçalho de todo envio anônimo, ligando todos os pacotes por um GUID cru (N5).
    /// </summary>
    public string ChaveLocalAnonima => _idAnonimo;
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
            return new(EstadoEnvio.Neutro, "Diagnóstico aguardando", "O diagnóstico só começa a subir depois que você responder à faixa sobre ele, no topo da janela do Streemu (ela aparece depois de você entrar num grupo).");
        if (Modo == ModoEnvio.Desligado)
            return new(EstadoEnvio.Neutro, "Diagnóstico desligado", "Nenhum diagnóstico sai do PC: nem log, nem dados do PC, nem séries. Segue só o essencial, que não desliga: sua conta e seus grupos, a moderação e a contagem de uso e de falhas graves (só o tipo e quantas vezes, sem guardar quem foi). Só sobe se você marcar um problema e apertar \"Enviar só isto\" na janela que aparece, depois de ver o que vai. Para ajudar a achar bugs sempre: Ajustes > Diagnóstico > \"Enviar diagnóstico\" ou, sem dizer quem você é, \"Enviar diagnóstico anônimo\".");
        if (Atual.E.NaoEnviar)
            return new(EstadoEnvio.Neutro, "Diagnóstico começa na próxima abertura", "Você ligou o diagnóstico com o Streemu aberto: esta sessão fica de fora e o envio começa quando você abrir o Streemu de novo.");
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
        if (Reduzido) return new(EstadoEnvio.Neutro, "Diagnóstico anônimo", "Você escolheu o diagnóstico anônimo: o app manda log e dados do PC para consertar bugs, mas sem o seu nome, o seu Discord nem o nome dos amigos que ele conhece (trocados por pessoa-N). Anônimo não é anonimato completo: cruzando os dados, dá para chegar em você. Para mandar identificado: Ajustes > Diagnóstico > \"Enviar diagnóstico\".");
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
        _urlEssencial = new Uri(servidor.TrimEnd('/') + "/telemetria/essencial");
        _urlApagar = new Uri(servidor.TrimEnd('/') + "/telemetria/apagar");
        _quem = quem; Atual = atual;
        _sessoes.Add(atual);
        _thread = new Thread(Laco) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "telemetria" };
    }

    /// <summary>A sessão foi apagada do disco (teto local, <see cref="Manutencao.Limpar"/>): sai da lista, senão o envio ficaria olhando uma pasta que não existe.</summary>
    public void Esquecer(string sessaoId) { lock (_sessoes) _sessoes.RemoveAll(s => s.Id == sessaoId && s != Atual); }

    public void Iniciar(IEnumerable<SessaoLocal> anteriores)
    {
        // sessões de aberturas anteriores no modo contrário (anônimas com o diagnóstico ligado, ou completas no anônimo) nunca sobem:
        // subir a anônima pelo id real ligaria uma ao outro, e a completa é de quando a pessoa ainda se identificava. Desligado: nenhuma sobe,
        // nem a anônima que ficou pendente de antes dos três estados (spec 018, D14: desligado não manda diagnóstico).
        var uteis = new List<SessaoLocal>();
        foreach (var s in anteriores)
        {
            // sessão de app antigo que ainda não mandou arquivo nenhum pode subir cifrada; a que já começou em claro termina em claro (o servidor não mistura os dois num arquivo)
            if (!s.E.Cifrada && s.E.Cursores.Values.All(c => c == 0)) { s.E.Cifrada = true; s.Salvar(); }
            if (Modo != ModoEnvio.Desligado && s.E.Reduzida == Reduzido) uteis.Add(s);
            else if (!s.E.NaoEnviar) { s.E.NaoEnviar = true; s.Salvar(); }
        }
        lock (_sessoes) _sessoes.AddRange(uteis);
        CarregarEssencialPendente();
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
            // a camada essencial não espera o ritmo do diagnóstico (até 60 s parado): acorda quando o envio dela está para vencer
            DateTime essQuando; lock (_essencial) essQuando = _essencialQuando;
            if (essQuando != DateTime.MaxValue) segundos = Math.Min(segundos, Math.Max(1, (int)Math.Ceiling((essQuando - DateTime.UtcNow).TotalSeconds)));
            _proximaTentativa = DateTime.UtcNow.AddSeconds(segundos);
            _vivoAte = _proximaTentativa.AddSeconds(25 + 30); // + uma rodada inteira (20 s) + folga
            bool acordou = _acordar.WaitOne(TimeSpan.FromSeconds(segundos));
            if (_parar) break;
            if (acordou && _urgente && _falhas == 0) Thread.Sleep(800); // agrupa o que vier junto (ex.: marcação + relatório)
            _urgente = false;
            try { Rodada(TimeSpan.FromSeconds(20)); }
            catch (Exception e) { Falhou(e.GetBaseException().Message); }
            try { RodadaEssencial(TimeSpan.FromSeconds(10), aoFechar: false); } catch { } // a camada essencial nunca derruba o laço nem mexe no sinal do diagnóstico
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

    // ---------------------------------------------------------------- camada essencial (spec 018, D12, D20, D38)
    //
    // Uso e falhas graves, contados na chegada: o Worker soma 1 e descarta o evento (POST /telemetria/essencial, relay/telemetria.js). Vale nos três
    // estados, por um envio SEPARADO do diagnóstico, sempre pela conta (token de acesso). O corpo tem só versão, canal, plataforma e uma lista fechada
    // de eventos com tipo e contagem: nada de id aleatório do anônimo, de PC, de sessão, de hora (o Worker conta o dia na chegada), de log, perfil,
    // hardware, nome de jogo ou de amigo. No Anônimo ela ainda espera um tempo sorteado e, ao fechar o app, a próxima abertura, para o envio
    // identificado não sair no mesmo instante do anônimo.

    /// <summary>Os eventos que a camada essencial aceita (a mesma lista do Worker, <c>EVENTOS_ESSENCIAIS</c>): lista fechada, sem texto livre.</summary>
    public static bool EventoEssencialValido(string evento, string? detalhe) => evento switch
    {
        "app_abriu" or "app_fechou" or "feature_sala" or "feature_filme" or "feature_arquivo" => detalhe == null,
        "live_comecou" or "live_acabou" => detalhe is "transmite" or "assiste" or "transmite_privada" or "assiste_privada",
        "falha_grave" => detalhe != null && detalhe.Length is >= 1 and <= 32 && detalhe.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'),
        _ => false,
    };

    sealed record EventoEssencial(string Evento, string? Detalhe, int? Valor);
    readonly List<EventoEssencial> _essencial = new();
    /// <summary>Quando a próxima rodada da camada essencial pode sair (UTC; MaxValue = nada na fila).</summary>
    DateTime _essencialQuando = DateTime.MaxValue;
    int _essencialFalhas;
    bool _essencialRenovar, _essencialAvisouRecusa;
    const int EssencialMax = 400, EssencialEventosPorPedido = 20, EssencialPedidosPorRodada = 5;
    /// <summary>Espera entre o primeiro evento da fila e o envio, fora do anônimo (agrupa o que vier junto). Os testes encurtam.</summary>
    public static TimeSpan EsperaEssencial { get; set; } = TimeSpan.FromSeconds(20);
    /// <summary>No anônimo a espera é sorteada neste intervalo, para o envio identificado da essencial não coincidir com o do diagnóstico anônimo. Os testes encurtam.</summary>
    public static (TimeSpan Min, TimeSpan Max) EsperaEssencialAnonimo { get; set; } = (TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10));
    /// <summary>Nada da camada essencial sobe enquanto a pessoa não viu o aviso desta versão do texto (faixa, <see cref="TextoDiagnostico.VersaoDoAviso"/>), como o diagnóstico.</summary>
    public volatile bool AguardandoEssencial;
    public int EssencialPendentes { get { lock (_essencial) return _essencial.Count; } }
    static string ArquivoEssencial => Path.Combine(Pasta.Raiz, "essencial-pendente.json");

    TimeSpan SortearEspera()
    {
        if (Modo != ModoEnvio.Anonimo) return EsperaEssencial;
        var (min, max) = EsperaEssencialAnonimo;
        return min + TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * Math.Max(0, (max - min).TotalMilliseconds));
    }

    /// <summary>Anota um evento da camada essencial; sobe na próxima rodada, sozinho. Fora da lista fechada (<see cref="EventoEssencialValido"/>) é descartado.</summary>
    public void Essencial(string evento, string? detalhe = null, int? valor = null)
    {
        if (!EventoEssencialValido(evento, detalhe)) return;
        bool primeiro = false;
        lock (_essencial)
        {
            if (_essencial.Count >= EssencialMax) _essencial.RemoveAt(0); // sem conta por muito tempo: o mais velho sai, a fila não cresce sem fim
            _essencial.Add(new(evento, detalhe, valor is < 0 ? 0 : valor is > 10_000_000 ? 10_000_000 : valor));
            if (_essencialQuando == DateTime.MaxValue) { _essencialQuando = DateTime.UtcNow + SortearEspera(); primeiro = true; }
        }
        if (primeiro) try { _acordar.Set(); } catch (ObjectDisposedException) { } // só para o laço marcar a hora do envio; não é urgente
    }

    /// <summary>No anônimo, ao fechar o app, o que não subiu fica num arquivo e sobe na próxima abertura (depois da espera sorteada).</summary>
    void GuardarEssencialPendente()
    {
        try
        {
            EventoEssencial[] itens;
            lock (_essencial) itens = _essencial.ToArray();
            if (itens.Length == 0) return;
            var arr = new JsonArray(itens.Select(e => (JsonNode)new JsonObject { ["e"] = e.Evento, ["d"] = e.Detalhe, ["v"] = e.Valor }).ToArray());
            Directory.CreateDirectory(Pasta.Raiz);
            File.WriteAllText(ArquivoEssencial, arr.ToJsonString());
        }
        catch { }
    }

    void CarregarEssencialPendente()
    {
        try
        {
            if (!File.Exists(ArquivoEssencial)) return;
            var arr = JsonNode.Parse(File.ReadAllText(ArquivoEssencial)) as JsonArray;
            File.Delete(ArquivoEssencial);
            if (arr == null) return;
            foreach (var n in arr)
                if (n is JsonObject o && o["e"]?.GetValue<string>() is string e) Essencial(e, o["d"]?.GetValue<string>(), o["v"]?.GetValue<int>());
        }
        catch { }
    }

    /// <summary>
    /// Sobe a fila da camada essencial (até 5 pedidos de 20 eventos). Falha dela nunca mexe no sinal do diagnóstico (<see cref="UltimoErro"/>): tenta de novo
    /// com espera crescente. Recusa 400 (o Worker não aceitou o conteúdo) descarta o lote: repetir não resolve.
    /// </summary>
    void RodadaEssencial(TimeSpan limite, bool aoFechar)
    {
        // uma rodada por vez: o laço e o Fechar não podem pegar a mesma fila e contar duas vezes (ao fechar espera a do laço, no máximo o limite)
        if (!Monitor.TryEnter(_essencialRodada, aoFechar ? limite : TimeSpan.Zero)) return;
        try { RodadaEssencialSerie(limite, aoFechar); } finally { Monitor.Exit(_essencialRodada); }
    }

    readonly object _essencialRodada = new();

    void RodadaEssencialSerie(TimeSpan limite, bool aoFechar)
    {
        if (Aguardando || AguardandoEssencial) { AdiarEssencial(); return; }
        var ate = DateTime.UtcNow + limite;
        for (int volta = 0; volta < EssencialPedidosPorRodada && DateTime.UtcNow < ate; volta++)
        {
            var usados = new List<EventoEssencial>();
            var itens = new List<(EventoEssencial Primeiro, int N)>();
            lock (_essencial)
            {
                if (_essencial.Count == 0) { _essencialQuando = DateTime.MaxValue; return; }
                if (!aoFechar && DateTime.UtcNow < _essencialQuando) return;
                foreach (var e in _essencial)
                {
                    int i = e.Valor == null ? itens.FindIndex(x => x.Primeiro.Valor == null && x.Primeiro.Evento == e.Evento && x.Primeiro.Detalhe == e.Detalhe && x.N < 100) : -1;
                    if (i >= 0) itens[i] = (itens[i].Primeiro, itens[i].N + 1);
                    else if (itens.Count < EssencialEventosPorPedido) itens.Add((e, 1));
                    else continue; // fica para o próximo pedido
                    usados.Add(e);
                }
            }
            string? token = null;
            try { token = Token?.Invoke(_essencialRenovar); } catch { }
            _essencialRenovar = false;
            if (token == null) { AdiarEssencial(); return; } // sem conta: guarda para depois do login
            var corpo = new JsonObject
            {
                ["v"] = 1, ["versao"] = Program.Version, ["canal"] = BuildInfo.Canal, ["plataforma"] = "windows",
                ["eventos"] = new JsonArray(itens.Select(x =>
                {
                    var o = new JsonObject { ["evento"] = x.Primeiro.Evento };
                    if (x.Primeiro.Detalhe != null) o["detalhe"] = x.Primeiro.Detalhe;
                    if (x.Primeiro.Valor != null) o["valor"] = x.Primeiro.Valor;
                    if (x.N > 1) o["n"] = x.N;
                    return (JsonNode)o;
                }).ToArray()),
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, _urlEssencial) { Content = new StringContent(corpo.ToJsonString(), Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using var cts = new CancellationTokenSource(limite < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : limite);
            HttpStatusCode status;
            try { using var resp = _http.Send(req, cts.Token); status = resp.StatusCode; }
            catch { EssencialFalhou(); return; }
            if ((int)status is >= 200 and < 300)
            {
                lock (_essencial) foreach (var e in usados) _essencial.Remove(e);
                _essencialFalhas = 0;
                continue;
            }
            if (status == HttpStatusCode.BadRequest || status == HttpStatusCode.RequestEntityTooLarge)
            {
                lock (_essencial) foreach (var e in usados) _essencial.Remove(e);
                if (!_essencialAvisouRecusa) { _essencialAvisouRecusa = true; Log.Info($"[telemetria] o servidor não aceitou {usados.Count} contagem(ns) do uso do app ({(int)status}); descartei esse lote."); }
                continue;
            }
            if (status == HttpStatusCode.Unauthorized) _essencialRenovar = true;
            EssencialFalhou();
            return;
        }
    }

    /// <summary>Em espera (faixa não vista, ou sem conta): olha de novo daqui a pouco, sem o laço ficar girando a cada segundo.</summary>
    public static TimeSpan ReavaliarEssencial { get; set; } = TimeSpan.FromSeconds(10);

    void AdiarEssencial()
    {
        lock (_essencial) if (_essencial.Count > 0) _essencialQuando = DateTime.UtcNow + ReavaliarEssencial;
    }

    void EssencialFalhou()
    {
        _essencialFalhas++;
        lock (_essencial) _essencialQuando = DateTime.UtcNow + TimeSpan.FromSeconds(Espera[Math.Min(_essencialFalhas, Espera.Length) - 1]);
    }

    /// <summary>Envia tudo o que há de novo, em quantos pacotes precisar. Devolve falso se algo falhou (fica para a próxima).</summary>
    bool Rodada(TimeSpan limite)
    {
        if (Aguardando) return true; // ainda não leu o aviso: nada sai (e não conta como falha)
        if (Modo == ModoEnvio.Desligado) return true; // spec 018, D14: desligado não faz pedido de diagnóstico (nem os passos do login). A camada essencial tem a rodada dela
        if (!TemCredencial())
        {
            // spec 022 (Q2-B): sem conta não sobe arquivo, só os eventos do login. Não é falha: o sinal fica neutro.
            _semCredencial = true;
            LoginSemConta.EnviarPendentes(_http, new Uri(_url.AbsoluteUri[..^"enviar".Length] + "login"), PcParaLog, Program.Version, BuildInfo.Commit.Length >= 7 ? BuildInfo.Commit[..7] : "", limite);
            return true;
        }
        _semCredencial = false;
        if (!Monitor.TryEnter(_rodada, limite)) return false;
        try
        {
            var ate = DateTime.UtcNow + limite;
            // o que as outras threads puseram na fila de gravação chega ao disco antes de eu olhar os arquivos (a fila existe para a interface e a mídia não esperarem; esta thread pode)
            Gravador.Descarregar(TimeSpan.FromMilliseconds(500));
            bool concluiu = false;
            for (int i = 0; i < 20 && DateTime.UtcNow < ate; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                var p = Montar(cifrar: true);
                // o que sobrou de uma sessão de app antigo, já começada em claro, sobe num pacote só dele quando não há nada cifrado para mandar
                if (p.Pecas.Count == 0 && p.Eventos.Count == 0 && p.Perda == null) { var antigo = Montar(cifrar: false); if (antigo.Pecas.Count > 0 || antigo.Eventos.Count > 0) p = antigo; }
                Custo.Montou(Stopwatch.GetTimestamp() - t0);
                // Parado e sem novidade, ainda assim fala com o servidor a cada ~60 s: o verde do sinal promete que o servidor está
                // confirmando AGORA, e com contato só a cada 5 min ele ficava verde com o servidor fora do ar (visto no teste de 2026-11-10).
                bool contato = DateTime.UtcNow - _ultimoContato >= TimeSpan.FromSeconds(Math.Max(10, RitmoParado - 2));
                if (p.Pecas.Count == 0 && p.Eventos.Count == 0 && p.Perda == null && _perfilPendente == null && !contato) { concluiu = true; break; }
                long r0 = Stopwatch.GetTimestamp(), b0 = BytesEnviados;
                bool enviou;
                try { enviou = Mandar(p, ate - DateTime.UtcNow); }
                catch { Custo.Rede(Stopwatch.GetTimestamp() - r0, 0, false); throw; }
                Custo.Rede(Stopwatch.GetTimestamp() - r0, BytesEnviados - b0, enviou);
                if (!enviou) return false;
                // em dia = o servidor confirmou cada pedaço até o fim que mandei e não sobrou nada que já existia (senão o laço manda o resto)
                bool tudo = p.Pecas.All(x => x.S.Cursor(x.Nome) == x.Fim);
                if (tudo && !p.TemMais) { _emDiaEm = DateTime.UtcNow; concluiu = true; break; }
            }
            // 20 voltas ou 20 s e o servidor ainda não confirmou tudo: é falha (o indicador fica vermelho), não "em dia"
            if (!concluiu) { Falhou("o servidor não confirmou tudo o que mandei"); return false; }
            return RodadaDoGravador(ate);
        }
        finally { Monitor.Exit(_rodada); }
    }

    /// <summary>
    /// A série do gravador local (<see cref="GravadorLocal"/>) sobe num passe só dela, DEPOIS do envio normal: pacote sem eventos nem perfil, para que o
    /// "Apagar" do pacote leve só a série e não eventos de outro assunto que viajariam junto. Só o que um disparo liberou (<see cref="SessaoLocal.Teto"/>).
    /// </summary>
    bool RodadaDoGravador(DateTime ate)
    {
        List<SessaoLocal> sessoes;
        lock (_sessoes) sessoes = _sessoes.Where(s => !s.E.NaoEnviar && !s.E.Concluida && s.E.Reduzida == Reduzido).ToList();
        // a fila de gravação já chegou ao disco (início da Rodada): o teto pedido por um disparo vira "o fim do arquivo agora"
        foreach (var s in sessoes) if (s.E.TetoPendente) s.FixarTetos();
        for (int i = 0; i < 20 && DateTime.UtcNow < ate; i++)
        {
            var p = Montar(cifrar: true, gravador: true);
            if (p.Pecas.Count == 0) return true;
            long r0 = Stopwatch.GetTimestamp(), b0 = BytesEnviados;
            bool enviou;
            try { enviou = Mandar(p, ate - DateTime.UtcNow, gravador: true); }
            catch { Custo.Rede(Stopwatch.GetTimestamp() - r0, 0, false); throw; }
            Custo.Rede(Stopwatch.GetTimestamp() - r0, BytesEnviados - b0, enviou);
            if (!enviou) return false;
            if (p.Pecas.All(x => x.S.Cursor(x.Nome) == x.Fim) && !p.TemMais) return true;
        }
        Falhou("o servidor não confirmou toda a série da live");
        return false;
    }

    /// <summary><c>Gz</c> são os bytes que vão no pacote: o gzip do pedaço, ou o quadro cifrado dele (<see cref="CifraEnvio"/>) quando o pacote é cifrado. Início e fim são sempre posições no arquivo local (que no PC pode estar cifrado pelo <see cref="CofreLocal"/>), nunca no que sobe.</summary>
    sealed record Peca(SessaoLocal S, string Nome, string Caminho, long Inicio, long Fim, byte[] Gz, bool Fechado, long? H0 = null, long? H1 = null, string? Pacote = null);
    sealed record Pacote(List<Peca> Pecas, List<(SessaoLocal S, long Ate, JsonObject Ev)> Eventos, bool TemMais, JsonObject? Perda = null, long PerdaVersao = 0, bool Cifrado = true);

    /// <summary>Monta o próximo pacote. <paramref name="cifrar"/>: só as sessões que sobem cifradas (as de agora); falso: só as de app antigo que já começaram em claro (sem perfil nem perda: nada novo sai em claro). <paramref name="gravador"/>: o passe da série (sempre cifrado).</summary>
    /// <param name="soIsto">
    /// Só estes arquivos da sessão atual, como anônimo, sem eventos nem perfil: a marcação com o diagnóstico Desligado depois de a pessoa confirmar
    /// (<see cref="EnviarSoIsto"/>). Nulo = o envio de sempre. A sessão atual sempre sobe cifrada.
    /// </param>
    Pacote Montar(bool cifrar, bool gravador = false, IReadOnlyCollection<string>? soIsto = null)
    {
        var pecas = new List<Peca>();
        var eventos = new List<(SessaoLocal, long, JsonObject)>();
        long total = 0;
        bool mais = false;
        List<SessaoLocal> sessoes;
        // reduzido: só as sessões abertas com o diagnóstico desligado; o que ficou pendente de uma sessão completa não sobe mais
        lock (_sessoes) sessoes = _sessoes.Where(s => !s.E.NaoEnviar && !s.E.Concluida && s.E.Reduzida == Reduzido && s.E.Cifrada == cifrar).ToList();
        if (Reduzido && !Anonimizador.Pronto) sessoes.Clear(); // antes de a sala trazer os amigos um nome ainda desconhecido vazaria
        if (soIsto != null) sessoes = Anonimizador.Pronto ? new List<SessaoLocal> { Atual } : new List<SessaoLocal>(); // a sessão do Desligado é "não enviar": aqui só vai o que a pessoa confirmou
        foreach (var s in sessoes)
        {
            foreach (var (nome, caminho) in s.Arquivos())
            {
                if (soIsto != null) { if (!soIsto.Contains(nome)) continue; }
                else if (SessaoLocal.Serie(nome) != gravador) continue; // a série sobe no passe do gravador (RodadaDoGravador); o resto, no normal
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
                long real = tam;
                tam = s.Limite(nome, tam); // série: só até onde um disparo liberou (gravador local)
                if (tam <= cur) continue;
                var (bytes, usados) = Ler(caminho, cur, tam, aceitaSemQuebra: !s.Aberta || SessaoLocal.Retrato(nome));
                if (usados == 0) continue;
                if (cur + usados < tam) mais = true;
                // o amigo vira amigo-N nos três estados (D32); no Anônimo o Anonimizador troca o resto (a própria pessoa) por pessoa-N depois
                // o arquivo no PC está cifrado linha a linha (CofreLocal, A8); o cursor e o início/fim são posições NESSE arquivo, e o que sobe é o texto em claro
                string texto = Pseudonimos.Aplicar(Higiene.Limpar(CofreLocal.DecifrarLinhas(bytes)));
                if (s.E.Reduzida || soIsto != null) texto = Anonimizador.Aplicar(texto);
                byte[] gz = Gzip(Encoding.UTF8.GetBytes(texto));
                if (cifrar) gz = CifraEnvio.Quadro(gz); // falha aqui vira falha do envio (sinal vermelho); nunca cai para o claro
                var (h0, h1) = HoraDoConteudo.De(nome, texto, s.HoraConteudo(nome), doZero: cur == 0); // a hora do CONTEÚDO, não a do envio
                bool fechado = SessaoLocal.Retrato(nome) || (!s.Aberta && cur + usados == real); // série cortada no teto ainda não terminou
                pecas.Add(new Peca(s, nome, caminho, cur, cur + usados, gz, fechado, h0, h1, gravador || soIsto != null ? s.Pacote(nome) : null));
                total += gz.Length;
            }
            if (!gravador && soIsto == null && eventos.Count < EventosPorPacote) LerEventos(s, eventos);
        }
        if (eventos.Count >= EventosPorPacote) mais = true; // sobrou evento para o próximo pacote
        if (gravador || soIsto != null) return new Pacote(pecas, eventos, mais, Cifrado: cifrar); // o passe do gravador leva só a série; o envio único, só os arquivos escolhidos
        // o que a sessão atual deixou de gravar no disco sobe em memória, mesmo com o disco ainda falhando (ver SessaoLocal.PerdaParaInformar)
        long perdaVersao = 0;
        var perda = cifrar ? Atual.PerdaParaInformar(out perdaVersao) : null;
        return new Pacote(pecas, eventos, mais, perda, perdaVersao, cifrar);
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
                var o = JsonNode.Parse(Pseudonimos.Aplicar(Higiene.Limpar(CofreLocal.TextoDaLinha(linha)))) as JsonObject;
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

    /// <param name="anonimoAgora">Manda como anônimo mesmo com <see cref="Modo"/> diferente de Anônimo: só o envio único do Desligado (<see cref="EnviarSoIsto"/>).</param>
    bool Mandar(Pacote p, TimeSpan limite, bool gravador = false, bool anonimoAgora = false)
    {
        bool credencialDaConta, anon = Reduzido || anonimoAgora;
        // Anônimo: só o id de PC próprio. Nem `install` (era a chave local como GUID cru em todo pacote), nem hw, conta, apelido. O Discord não sobe em
        // estado nenhum (D27): a conta identifica a pessoa, e o Identidade ainda leva os campos só para o resto do app não mudar.
        var quem = anon ? new Identidade(null, null, null, null) : _quem();
        var perfil = p.Cifrado && !gravador ? _perfilPendente : null; // o passe do gravador não leva o perfil do índice, e o pacote de sessão antiga (em claro) também não
        // o perfil do índice também passa pelo Higiene: em 11/11 o MAC do Wi-Fi chegou ao pc.json do servidor por aqui, não só pelo arquivo perfil-*.json; o mesmo conjunto enxuto nos dois estados (D15, N4)
        var perfilLimpo = perfil == null ? null : JsonNode.Parse(anon ? Anonimizador.Aplicar(Higiene.Limpar(perfil.ToJsonString())) : Higiene.Limpar(perfil.ToJsonString())) as JsonObject;
        var cab = new JsonObject
        {
            ["v"] = 1, ["pc"] = anon ? PcAnonimo : Pc, ["hw"] = anon ? null : _hw, ["sessao"] = Atual.Id, ["install"] = quem.Install,
            ["conta"] = quem.Conta, ["apelido"] = quem.Apelido,
            ["versao"] = Program.Version, ["commit"] = BuildInfo.Commit.Length >= 7 ? BuildInfo.Commit[..7] : "", ["canal"] = BuildInfo.Canal,
            // cifrado: em `perfil` só o resumo que o painel lê; o completo vai em `perfil_cifrado` (spec 018, 7.2)
            ["relogio"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["ativo"] = Ativo, ["perfil"] = perfilLimpo == null ? null : p.Cifrado ? CifraEnvio.PerfilLegivel(perfilLimpo) : perfilLimpo,
            ["pecas"] = new JsonArray(p.Pecas.Select(x => (JsonNode)new JsonObject
            {
                ["sessao"] = x.S.Id, ["arquivo"] = x.Nome, ["inicio"] = x.Inicio, ["fim"] = x.Fim, ["tam"] = x.Gz.Length,
                ["versao"] = x.S.E.Versao, ["commit"] = x.S.E.Commit, ["canal"] = x.S.E.Canal, // do app que criou a sessão (não o de agora)
                ["h0"] = HoraDoConteudo.Iso(x.H0), ["h1"] = HoraDoConteudo.Iso(x.H1), ["fechado"] = x.Fechado,
                ["pacote"] = x.Pacote, // só a série do gravador leva (é o que o Apagar apaga); nas outras peças vai nulo e o Worker ignora
            }).ToArray()),
            // a nota do "Marcar problema" é texto livre: no índice (legível) ela não vai, só dentro do arquivo de eventos e do relatório, cifrados
            ["eventos"] = new JsonArray(p.Eventos.Select(e => CifraEnvio.EventoLegivel(e.Ev)).Concat(p.Perda == null ? Array.Empty<JsonNode>() : new JsonNode[] { p.Perda.DeepClone() }).ToArray()),
        };
        if (p.Cifrado)
        {
            cab["cifra"] = CifraEnvio.Cabecalho();
            if (perfilLimpo != null && CifraEnvio.PerfilCifrado(perfilLimpo) is string pc) cab["perfil_cifrado"] = pc;
        }
        byte[] json = Encoding.UTF8.GetBytes(cab.ToJsonString());
        var corpo = new MemoryStream();
        corpo.Write(new[] { (byte)(json.Length >> 24), (byte)(json.Length >> 16), (byte)(json.Length >> 8), (byte)json.Length });
        corpo.Write(json);
        foreach (var x in p.Pecas) corpo.Write(x.Gz);

        using var req = new HttpRequestMessage(HttpMethod.Post, _url) { Content = new ByteArrayContent(corpo.ToArray()) };
        // credencial: conta (token) no envio normal, bilhete no anônimo. O token NUNCA vai no anônimo. Sem nenhum dos dois nada sobe (a senha do grupo acabou).
        string? token = null, bilhete = null;
        try { if (!anon) { token = Token?.Invoke(_renovarToken); _renovarToken = false; } else bilhete = Bilhete?.Invoke(); } catch { }
        credencialDaConta = token != null || bilhete != null;
        if (token != null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        else if (bilhete != null) req.Headers.Add("x-streemu-bilhete", bilhete);
        else { _semCredencial = true; return false; } // a credencial sumiu entre a checagem da rodada e aqui
        // o servidor guarda tudo sob este id (o Worker chega ao PC do anônimo pelo SHA-256 da chave local, igual ao PcAnonimo): o real no Anônimo ligaria o pacote ao PC
        req.Headers.Add("x-streemu-pc", anon ? PcAnonimo : Pc);
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
                if (gravador && peca.Pacote is string pct && novo > peca.Inicio)
                    HistoricoEnvios.Registrar(pct, peca.S.Motivo(peca.Nome), anon ? nameof(ModoEnvio.Anonimo) : Modo.ToString(), peca.S.Id, peca.Nome, novo - peca.Inicio);
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
        if (Modo == ModoEnvio.Completo && !Atual.E.NaoEnviar) RegistrarCusto();
        if (Atual.Aberta) Atual.Evento("sessao_fim");
        Atual.E.Fechou = "normal";
        Atual.Salvar();
        Gravador.Descarregar(TimeSpan.FromSeconds(1)); // o que ainda está na fila de gravação vai para o disco antes de o app sair
        // a camada essencial sai ao fechar (o "app_fechou" e a live que acabou agora). No anônimo ela espera a próxima abertura, guardada num arquivo:
        // sair junto com o envio anônimo ligaria os dois pelo horário (spec 018, seção 2.1)
        // Fora do anônimo, o que não subiu (faixa ainda não vista, sem conta, rede fora) também fica no arquivo, senão a fila em memória se perde
        if (Modo != ModoEnvio.Anonimo) try { RodadaEssencial(TimeSpan.FromSeconds(Math.Min(2, limite.TotalSeconds)), aoFechar: true); } catch { }
        GuardarEssencialPendente();
        if (Atual.E.NaoEnviar || Modo == ModoEnvio.Desligado) return;
        try { Rodada(limite); } catch { }
        Atual.Salvar();
        Gravador.Descarregar(TimeSpan.FromSeconds(1)); // a rodada também grava (relógio, recusa): o que sobrou na fila vai para o disco
    }

    // ---------------------------------------------------------------- marcação com o diagnóstico Desligado (spec 018, seção 6)

    /// <summary>Quanto de cada arquivo ainda não subiu (o que "Enviar só isto" levaria agora). Só lê tamanhos; arquivo que não existe fica de fora.</summary>
    public List<(string Arquivo, long Bytes)> TamanhosDoEnvioUnico(IEnumerable<string> nomes)
    {
        var lista = new List<(string, long)>();
        var quero = nomes.ToHashSet();
        foreach (var (nome, caminho) in Atual.Arquivos())
        {
            if (!quero.Contains(nome)) continue;
            long tam; try { tam = new FileInfo(caminho).Length; } catch { continue; }
            long falta = tam - Atual.Cursor(nome);
            if (falta > 0) lista.Add((nome, falta));
        }
        return lista;
    }

    /// <summary>
    /// Com o diagnóstico Desligado nada sobe sozinho; isto é o que sobe SE a pessoa viu a janela da marcação e apertou "Enviar só isto": só os arquivos de
    /// <paramref name="nomes"/> da sessão atual, uma vez, como anônimo (id de PC anônimo e bilhete da conta, nunca o token; nomes trocados por pessoa-N; sem
    /// eventos para o índice nem perfil). Cada arquivo leva o pacote que <see cref="SessaoLocal.Pacotear"/> deu, então o "Apagar" funciona como nos outros envios.
    /// O modo salvo não muda e o laço normal continua parado. Bloqueia (fala com o servidor); nunca lança: devolve a frase para a tela, com o próximo passo.
    /// </summary>
    public (bool Ok, string Mensagem) EnviarSoIsto(IReadOnlyCollection<string> nomes)
    {
        int falhasAntes = _falhas; string? erroAntes = UltimoErro;
        bool tomou = false;
        try
        {
            if (Modo != ModoEnvio.Desligado) return (false, "O diagnóstico não está desligado: a marcação sobe pelo envio normal.");
            if (Aguardando) return (false, "Responda primeiro à faixa sobre o diagnóstico, no topo da janela do Streemu. Nada foi enviado.");
            if (!Anonimizador.Pronto) return (false, "O Streemu ainda está carregando os nomes dos amigos da sala para tirá-los do envio. Tente de novo em alguns segundos. Nada foi enviado.");
            string? bilhete = null; try { bilhete = Bilhete?.Invoke(); } catch { }
            if (bilhete == null) return (false, "Sem conexão com a conta agora: entre na sua conta e tente de novo. Nada foi enviado.");
            if (!Monitor.TryEnter(_rodada, TimeSpan.FromSeconds(5))) return (false, "O envio está ocupado agora. Tente de novo em alguns segundos. Nada foi enviado.");
            tomou = true;
            Gravador.Descarregar(TimeSpan.FromMilliseconds(500)); // o que as outras threads ainda tinham na fila chega ao disco antes de eu olhar os arquivos
            Atual.FixarTetos(); // a série só sobe até onde está agora
            var ate = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            long bytes0 = BytesEnviados;
            for (int i = 0; i < 20 && DateTime.UtcNow < ate; i++)
            {
                var p = Montar(cifrar: true, soIsto: nomes);
                if (p.Pecas.Count == 0) break;
                if (!Mandar(p, ate - DateTime.UtcNow, gravador: true, anonimoAgora: true))
                    return (false, $"Não consegui enviar ({(UltimoErro ?? "o servidor não respondeu").TrimEnd('.', ' ')}). Pode ter subido só uma parte: o que subiu aparece em Ajustes > Diagnóstico, onde dá para apagar. Tente de novo mais tarde.");
                if (p.Pecas.All(x => x.S.Cursor(x.Nome) == x.Fim) && !p.TemMais) break;
            }
            long enviados = BytesEnviados - bytes0;
            Log.Info($"[telemetria] com o diagnóstico desligado, a pessoa confirmou o envio só desta marcação ({enviados} bytes, anônimo).");
            return (true, enviados == 0 ? "Não havia nada novo para enviar." : "Enviado, sem o seu nome nem o dos amigos. Em Ajustes > Diagnóstico dá para ver o que foi e apagar do servidor.");
        }
        catch (Exception e) { return (false, "Não consegui falar com o servidor (" + e.GetBaseException().Message + "). Pode ter subido só uma parte; veja em Ajustes > Diagnóstico. Tente de novo mais tarde."); }
        finally
        {
            if (tomou) Monitor.Exit(_rodada);
            _falhas = falhasAntes; UltimoErro = erroAntes; // o envio único não mexe no sinal nem na espera do laço
        }
    }

    // ---------------------------------------------------------------- "Ver o que é enviado" e "Apagar" (spec 018, D37)

    /// <summary>Um arquivo na prévia: o que sobe, o que fica só no PC. <paramref name="Sobe"/> falso = não sai deste PC agora.</summary>
    public sealed record ItemPrevia(string Sessao, string Arquivo, long Bytes, bool Sobe, string Motivo);

    /// <summary>
    /// O que o app enviaria AGORA e o que fica só no PC, arquivo por arquivo (só o que ainda falta subir; o que já subiu está em <see cref="HistoricoEnvios"/>).
    /// Vale nos três estados: no Desligado nada sobe e a lista mostra o que existe no PC. Só lê tamanhos: não monta, não lê nem manda nada.
    /// </summary>
    public List<ItemPrevia> Previa()
    {
        var itens = new List<ItemPrevia>();
        List<SessaoLocal> sessoes;
        lock (_sessoes) sessoes = _sessoes.ToList();
        foreach (var s in sessoes)
        {
            bool elegivel = Modo != ModoEnvio.Desligado && !s.E.NaoEnviar && !s.E.Concluida && s.E.Reduzida == Reduzido;
            string foraDoEnvio = Modo == ModoEnvio.Desligado ? "o diagnóstico está desligado" : s.E.NaoEnviar ? "sessão aberta com o diagnóstico desligado" : s.E.Reduzida != Reduzido ? "sessão de outro estado do diagnóstico" : "já enviado";
            foreach (var (nome, caminho) in s.Arquivos())
            {
                long tam; try { tam = new FileInfo(caminho).Length; } catch { continue; }
                long cur = s.Cursor(nome);
                if (SessaoLocal.Serie(nome))
                {
                    long sobe = Math.Max(0, s.Limite(nome, tam) - cur), fica = Math.Max(0, tam - Math.Max(cur, s.Limite(nome, tam)));
                    if (elegivel && sobe > 0) itens.Add(new(s.Id, nome, sobe, true, "série liberada por um disparo (marcação, falha grave, problema na live ou pedido de um amigo)"));
                    if (fica > 0) itens.Add(new(s.Id, nome, fica, false, elegivel ? "série da live: fica no PC por 7 dias e só sobe se algo disparar" : foraDoEnvio));
                    continue;
                }
                long falta = Math.Max(0, tam - cur);
                if (falta > 0) itens.Add(new(s.Id, nome, falta, elegivel, elegivel ? "sobe no próximo envio" : foraDoEnvio));
            }
        }
        return itens.OrderBy(i => i.Sobe ? 0 : 1).ThenBy(i => i.Sessao).ThenBy(i => i.Arquivo).ToList();
    }

    /// <summary>
    /// Apaga do servidor um pacote do histórico (<c>POST /telemetria/apagar</c>), no estado em que ele subiu: o identificado com o token da conta e o PC; o
    /// anônimo com a chave local e o bilhete (ou o token). O servidor responde em etapas (<c>completo: false</c>) e o pedido se repete até acabar. Devolve a
    /// frase que a tela mostra, com o próximo passo se falhar. Bloqueia: chamar fora da thread da tela.
    /// </summary>
    public (bool Ok, string Mensagem) ApagarPacote(string pacote, string modoDoPacote)
    {
        try
        {
            bool anonimo = modoDoPacote == nameof(ModoEnvio.Anonimo);
            string? token = null, bilhete = null;
            try { token = Token?.Invoke(false); bilhete = anonimo ? Bilhete?.Invoke() : null; } catch { }
            if (anonimo ? token == null && bilhete == null : token == null)
                return (false, anonimo ? "Sem conexão com a conta agora: entre na sua conta e tente de novo. O pacote continua no servidor." : "Entre na sua conta para apagar um envio identificado. O pacote continua no servidor.");
            for (int i = 0; i < 8; i++)
            {
                var corpo = anonimo ? new JsonObject { ["pacote"] = pacote, ["chave"] = ChaveLocalAnonima } : new JsonObject { ["pacote"] = pacote, ["pc"] = Pc };
                using var req = new HttpRequestMessage(HttpMethod.Post, _urlApagar) { Content = new StringContent(corpo.ToJsonString(), Encoding.UTF8, "application/json") };
                if (anonimo && bilhete != null) req.Headers.Add("x-streemu-bilhete", bilhete);
                else req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var resp = _http.Send(req, cts.Token);
                string texto = new StreamReader(resp.Content.ReadAsStream()).ReadToEnd();
                if (resp.StatusCode == HttpStatusCode.Forbidden) return (false, "O servidor não reconheceu este pacote como seu (talvez já tenha sido apagado, ou foi enviado por outra conta).");
                if (!resp.IsSuccessStatusCode) return (false, $"O servidor respondeu {(int)resp.StatusCode} ao apagar. Tente de novo mais tarde; o pacote continua no servidor.");
                var r = JsonNode.Parse(texto) as JsonObject;
                if (r?["completo"]?.GetValue<bool>() == true) return (true, "Apagado do servidor.");
            }
            return (false, "O servidor apagou só uma parte. Aperte Apagar de novo para terminar.");
        }
        catch (Exception e) { return (false, "Não consegui falar com o servidor (" + e.GetBaseException().Message + "). Tente de novo mais tarde; o pacote continua no servidor."); }
    }

    public void Dispose() { _parar = true; _acordar.Set(); _http.Dispose(); }
}
