using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Streemu.Util;

namespace Streemu.Telemetria;

/// <summary>
/// O que a telemetria grava no disco antes de enviar (spec 009, seção 6): o disco é a fonte da verdade. Cada abertura do app é uma sessão,
/// com a pasta <c>telemetria\&lt;sessao&gt;\</c> (eventos, perfil, relatórios) e o log dela em <c>logs\</c>. O <c>sessao.json</c> guarda até que byte
/// de cada arquivo o servidor confirmou: se o app travar ou o PC desligar, a próxima abertura continua de onde parou.
/// </summary>
public sealed class SessaoLocal
{
    public sealed class Estado
    {
        public string Id { get; set; } = "";
        public long Inicio { get; set; }
        public string? LogPath { get; set; }
        public int Pid { get; set; }
        public long PidInicio { get; set; }
        /// <summary>Nulo = aberta; "normal" = fechou pelo Sair; "nao" = o app morreu sem fechar (descoberto na abertura seguinte).</summary>
        public string? Fechou { get; set; }
        /// <summary>Criada com o "Enviar diagnóstico" desligado: nunca sai do PC, mesmo se a pessoa ligar depois.</summary>
        public bool NaoEnviar { get; set; }
        /// <summary>Sessão aberta com o diagnóstico desligado: sobe anônima (<see cref="Anonimizador"/>): sem identidade, sem nomes e sem o texto livre da pessoa.</summary>
        public bool Reduzida { get; set; }
        public Dictionary<string, long> Cursores { get; set; } = new();
        /// <summary>Até que byte do eventos.ndjson o índice do servidor já recebeu (os eventos também sobem como arquivo).</summary>
        public long Indexados { get; set; }
        public bool Concluida { get; set; }
        /// <summary>Hora do CONTEÚDO (UTC, ms) da última linha já confirmada pelo servidor, por arquivo: âncora para resolver a virada do dia no log (a linha só tem HH:mm:ss).</summary>
        public Dictionary<string, long> Horas { get; set; } = new();
        /// <summary>Versão, commit e canal do app que CRIOU a sessão. Uma sessão antiga que sobe numa abertura nova é do app antigo: o cabeçalho do envio é do app de agora, não serve para ela.</summary>
        public string? Versao { get; set; }
        public string? Commit { get; set; }
        public string? Canal { get; set; }
    }

    public const string Log = "log.txt", Eventos = "eventos.ndjson";
    public string Dir { get; }
    public Estado E { get; }
    readonly object _gate = new();

    SessaoLocal(string dir, Estado e) { Dir = dir; E = e; }

    public string Id => E.Id;
    public bool Aberta => E.Fechou == null;
    /// <summary>Última gravação no disco falhou (disco cheio, antivírus bloqueando a pasta): o indicador fica vermelho. Some na próxima que der certo.</summary>
    public string? FalhaLocal { get; private set; }

    void Gravou() => FalhaLocal = null;
    void NaoGravou(Exception e) => FalhaLocal = e.GetBaseException().Message;

    // ---- perda local: o que não foi gravado por falha de disco também precisa deixar rastro ----
    // Antes, evento ou linha que não gravava sumia: o sinal ficava vermelho e, passada a falha, ficava verde, sem nada dizer que havia um buraco
    // nos dados. Os contadores abaixo viram um evento "perda_local" que sobe junto do próximo envio (montado em memória pelo Envio: o disco pode
    // ainda estar falhando, a rede não). Quem lê a telemetria vê "faltam N eventos entre X e Y" em vez de concluir "não teve erro".
    long _perdaEventos, _perdaLinhas, _perdaRetratos, _falhasSalvar, _perdaVersao, _perdaInformada;
    DateTime? _primeiraPerda, _ultimaPerda;
    string? _motivoPerda;

    public long PerdaEventos { get { lock (_gate) return _perdaEventos; } }
    public long PerdaLinhas { get { lock (_gate) return _perdaLinhas; } }
    public long PerdaRetratos { get { lock (_gate) return _perdaRetratos; } }

    /// <summary>Chamar com o <c>_gate</c> tomado. Só o tipo da exceção vai para o servidor: a mensagem tem o caminho (com o nome do usuário do Windows).</summary>
    void Perdeu(ref long contador, Exception e, int n = 1)
    {
        contador += n; _perdaVersao++;
        _ultimaPerda = DateTime.UtcNow; _primeiraPerda ??= _ultimaPerda;
        _motivoPerda = e.GetBaseException().GetType().Name;
    }

    /// <summary>O evento "perda_local" com os totais acumulados da sessão, ou nulo se nada mudou desde a última vez que o servidor confirmou.</summary>
    public JsonObject? PerdaParaInformar(out long versao)
    {
        lock (_gate)
        {
            versao = _perdaVersao;
            if (versao == _perdaInformada) return null;
            return new JsonObject
            {
                ["tipo"] = "perda_local", ["eid"] = $"{Id[^8..]}-perda-{versao}",
                ["hora"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["hora_utc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), ["sessao"] = Id,
                ["eventos_perdidos"] = _perdaEventos, ["linhas_perdidas"] = _perdaLinhas, ["retratos_perdidos"] = _perdaRetratos,
                ["falhas_ao_salvar_estado"] = _falhasSalvar, ["motivo"] = _motivoPerda,
                ["primeira_utc"] = _primeiraPerda?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), ["ultima_utc"] = _ultimaPerda?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            };
        }
    }

    public void PerdaInformada(long versao) { lock (_gate) if (versao > _perdaInformada) _perdaInformada = versao; }

    public static SessaoLocal Criar(string id, string? logPath, bool naoEnviar, bool reduzida = false)
    {
        string dir = Path.Combine(Pasta.Raiz, id);
        Directory.CreateDirectory(dir);
        using var me = Process.GetCurrentProcess();
        var s = new SessaoLocal(dir, new Estado
        {
            Id = id, Inicio = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), LogPath = logPath, Reduzida = reduzida,
            Pid = me.Id, PidInicio = me.StartTime.ToUniversalTime().Ticks, NaoEnviar = naoEnviar,
            Versao = Program.Version, Commit = BuildInfo.Commit.Length >= 7 ? BuildInfo.Commit[..7] : "", Canal = BuildInfo.Canal,
        });
        s.Salvar();
        return s;
    }

    public static SessaoLocal? Abrir(string dir)
    {
        try
        {
            var e = JsonSerializer.Deserialize<Estado>(File.ReadAllText(Path.Combine(dir, "sessao.json")));
            return e == null || e.Id == "" ? null : new SessaoLocal(dir, e);
        }
        catch { return null; }
    }

    /// <summary>O processo que criou a sessão ainda está aberto (outra instância, nos testes locais)? Aí ela não é "travada", é de outro.</summary>
    public bool DonoVivo()
    {
        Process p;
        try { p = Process.GetProcessById(E.Pid); }
        catch { return false; } // não existe processo com esse número: o dono morreu
        using (p)
        {
            // Existe, mas pode ser outro programa que herdou o número. Se o Windows não deixa ler a hora de início (processo de administrador,
            // de outro usuário), na dúvida é "vivo": a sessão espera a próxima abertura em vez de virar "não fechou normalmente" por engano.
            try { return !p.HasExited && p.StartTime.ToUniversalTime().Ticks == E.PidInicio; }
            catch { return true; }
        }
    }

    public void Salvar()
    {
        lock (_gate)
        {
            try
            {
                string f = Path.Combine(Dir, "sessao.json"), tmp = f + "." + Environment.ProcessId + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(E));
                File.Move(tmp, f, overwrite: true);
                Gravou();
            }
            catch (Exception e) { NaoGravou(e); Perdeu(ref _falhasSalvar, e); }
        }
    }

    /// <summary>Os arquivos desta sessão que sobem: nome no servidor → caminho no disco.</summary>
    public List<(string Nome, string Caminho)> Arquivos()
    {
        var lista = new List<(string, string)>();
        if (E.LogPath != null && File.Exists(E.LogPath)) lista.Add((Log, E.LogPath));
        try
        {
            foreach (var f in Directory.EnumerateFiles(Dir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(Dir, f).Replace('\\', '/');
                if (rel == "sessao.json" || rel.EndsWith(".tmp")) continue;
                lista.Add((rel.ToLowerInvariant(), f));
            }
        }
        catch { }
        return lista;
    }

    /// <summary>Arquivos que só aparecem prontos (gravados inteiros de uma vez): sobem como "fechados".</summary>
    public static bool Retrato(string nome) => nome.StartsWith("perfil-") || nome.StartsWith("relatorios/");

    public long Cursor(string nome) { lock (_gate) return E.Cursores.GetValueOrDefault(nome); }
    public void SetCursor(string nome, long v) { lock (_gate) E.Cursores[nome] = v; }
    /// <summary>Até que hora (UTC, ms) o conteúdo já confirmado deste arquivo vai; antes da primeira confirmação, o início da sessão.</summary>
    public long HoraConteudo(string nome) { lock (_gate) return E.Horas.TryGetValue(nome, out var h) ? h : E.Inicio; }
    public void SetHoraConteudo(string nome, long ms) { lock (_gate) E.Horas[nome] = ms; }

    int _seq;

    /// <summary>Acrescenta um evento (uma linha JSON) ao eventos.ndjson. O "eid" faz o servidor ignorar o mesmo evento reenviado.</summary>
    public void Evento(string tipo, JsonObject? campos = null)
    {
        var o = campos ?? new JsonObject();
        if (E.Reduzida && Anonimizador.NaoGrava(tipo)) return; // texto livre da pessoa não vai nem para o disco no modo anônimo
        o["tipo"] = tipo;
        o["eid"] = $"{Id[^8..]}-{Interlocked.Increment(ref _seq)}-{Environment.TickCount64 % 100000}";
        o["hora"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        o["hora_utc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        o["sessao"] ??= Id;
        Enfileirar(Eventos, o.ToJsonString(), evento: true, urgente: EventoUrgente(tipo));
    }

    /// <summary>Eventos que não esperam o agrupamento da gravação: o que a pessoa marcou, o que o app viu falhar e o que fecha ou suspende a sessão são justamente os que um travamento logo depois deixaria sem rastro. São raros: o custo do disco sem agrupar é desprezível.</summary>
    public static bool EventoUrgente(string tipo) => tipo is "marcacao" or "falha" or "sessao_fim" or "sessao_sem_fim" or "suspensao" or "ui_travada" or "midia_falha";

    /// <summary>Acrescenta linhas inteiras (cada uma com quebra no fim) a um arquivo da sessão; ele sobe aos pedaços, como o log.</summary>
    public void Acrescentar(string nome, string linhas) => Enfileirar(nome, linhas.TrimEnd('\n'), evento: false);

    /// <summary>Fila cheia (<see cref="Gravador"/>): o item não vai para o disco e entra na conta da perda local.</summary>
    public sealed class FilaCheiaException : Exception { public FilaCheiaException() : base("fila de gravação cheia") { } }

    /// <summary>
    /// Quem chama (interface, captura, player, qualquer thread que escreva no log) não espera o disco: o item vai para a fila do <see cref="Gravador"/>,
    /// que grava em lotes numa thread própria. Antes o AppendAllText rodava aqui, na thread de quem chamou.
    /// </summary>
    void Enfileirar(string nome, string linha, bool evento, bool urgente = false)
    {
        if (Gravador.Enfileirar(this, nome, linha, retrato: false, evento, urgente)) return;
        lock (_gate) { if (evento) Perdeu(ref _perdaEventos, new FilaCheiaException()); else Perdeu(ref _perdaLinhas, new FilaCheiaException()); }
    }

    /// <summary>Chamado pela thread do <see cref="Gravador"/>: grava um lote de <paramref name="n"/> linhas (já com a quebra no fim de cada uma).</summary>
    internal void GravarLinhasAgora(string nome, string texto, int n, bool evento)
    {
        lock (_gate)
        {
            try { File.AppendAllText(Path.Combine(Dir, nome), texto, new UTF8Encoding(false)); Gravou(); }
            catch (Exception e)
            {
                NaoGravou(e);
                if (evento) Perdeu(ref _perdaEventos, e, n); else Perdeu(ref _perdaLinhas, e, n);
            }
        }
    }

    /// <summary>Grava um arquivo inteiro de uma vez (temporário + troca), com quebra de linha no fim: perfil e relatórios.</summary>
    public void Retrato(string nome, string conteudo)
    {
        if (!Gravador.Enfileirar(this, nome, conteudo, retrato: true, evento: false)) lock (_gate) Perdeu(ref _perdaRetratos, new FilaCheiaException());
    }

    internal void GravarRetratoAgora(string nome, string conteudo)
    {
        try
        {
            string f = Path.Combine(Dir, nome.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            string tmp = f + ".tmp";
            File.WriteAllText(tmp, conteudo.TrimEnd() + "\n", new UTF8Encoding(false));
            File.Move(tmp, f, overwrite: true);
            Gravou();
        }
        catch (Exception e) { NaoGravou(e); lock (_gate) Perdeu(ref _perdaRetratos, e); }
    }

    /// <summary>Tudo confirmado pelo servidor (arquivos e índice) e a sessão fechada: pode sair do disco depois de alguns dias.</summary>
    public bool TudoEnviado()
    {
        if (Aberta) return false;
        foreach (var (nome, caminho) in Arquivos())
        {
            long tam;
            try { tam = new FileInfo(caminho).Length; } catch { continue; }
            if (Cursor(nome) < tam) return false;
        }
        long ev = 0;
        try { ev = new FileInfo(Path.Combine(Dir, Eventos)).Length; } catch { }
        return E.Indexados >= ev;
    }
}
