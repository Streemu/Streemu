using System.Text.Json.Nodes;
using Streemu.Util;

namespace Streemu.Filme;

/// <summary>
/// A linha do tempo do filme (spec 010, seção 7). O anfitrião é o dono de um único estado {tocando|pausado, posição, instante T no relógio dele};
/// quem assiste aplica esse estado no próprio mpv. Todo comando é "a partir do instante T, da posição P": todos começam juntos em vez de
/// cada um quando a mensagem chega (7.3). Depois, a correção contínua (7.4) mantém cada mpv no ponto certo.
/// Spike S1 (2026-11-11): só a hora marcada já deixa dois mpv a menos de 1 ms; a correção traz 200 ms de atraso para menos de 15 ms em ~5 s.
/// </summary>
public sealed class Sessao : IDisposable
{
    /// <summary>
    /// Folga da hora marcada: a ida e volta + isso (7.3 fala em 150 a 300 ms). Era 300; no teste da interface nova (11/11) o ▶ parecia
    /// travado, e quem recebe atrasado já começa um pouco à frente e é corrigido (AplicarAsync + 7.4).
    /// </summary>
    const int FolgaMs = 150;

    /// <summary>Ninguém mais com o arquivo aberto nem assistindo pela transmissão: não há com quem sincronizar, então tocar e pausar valem na hora, sem hora marcada.</summary>
    bool Sozinho => Anfitriao && _comArquivo.Count == 0 && (FluxoIds?.Invoke().Any() != true);

    /// <summary>
    /// Quem assiste pela transmissão, pelo id da conta (spec 011; o MainForm preenche no anfitrião). Eles seguem a mesma linha do tempo pelo buffer do mpv: contam para a hora marcada
    /// (todos começam juntos) e para a espera do pulo (D4), mas não entram nos <see cref="Amigos"/> (não têm arquivo para ficar "pronto").
    /// </summary>
    public Func<IEnumerable<string>>? FluxoIds { get; set; }

    /// <summary>O estado atual (filme_estado), para quem começa a assistir pela transmissão com o filme já rolando.</summary>
    public JsonObject EstadoAtual() => Estado();

    public bool Anfitriao { get; }
    public string Filme { get; }
    public string Nome { get; }
    public string NomeAnfitriao { get; }
    readonly Mpv _mpv;
    readonly Func<long?> _relogio;
    readonly Func<double> _idaVolta;
    readonly Action<JsonObject> _enviar;
    readonly string _meuNome;
    readonly string _meuId;
    /// <summary>Id → nome de quem já falou nesta sessão: só para o texto de tela (o nome pode mudar e repetir; quem manda é o id).</summary>
    readonly Dictionary<string, string> _nomes = new();
    /// <summary>O nome atual da pessoa na sala do grupo (o MainForm preenche); sem ele vale o último nome que ela mandou.</summary>
    public Func<string, string?>? NomeNaSala { get; set; }
    public string NomeDe(string id) => NomeNaSala?.Invoke(id) ?? (_nomes.TryGetValue(id, out var n) ? n : id);

    /// <summary>
    /// Quem mandou a mensagem: o id que o MainForm carimba a partir da ligação (campo "id"; o app antigo não manda nada e a ligação já diz quem é).
    /// Sem id (mensagem sem ligação, teste), cai no nome, como era.
    /// </summary>
    string Quem(JsonObject m, string padrao = "amigo")
    {
        string nome = m["nome"]?.GetValue<string>() ?? padrao;
        string id = m["id"]?.GetValue<string>() is { Length: > 0 } i ? i : nome;
        _nomes[id] = nome;
        return id;
    }

    public bool Tocando { get; private set; }
    double _pos;
    long _t;
    int _seq;
    public bool TodosControlam { get; private set; }
    /// <summary>Para o anfitrião: quem entrou e o estado de cada um (4.3), pelo id da conta.</summary>
    public Dictionary<string, string> Amigos { get; } = new();
    /// <summary>4.3: o primeiro ▶ do anfitrião espera todos ficarem prontos (padrão); "começar agora" fura a espera.</summary>
    public bool EsperarTodos { get; set; } = true;
    public bool Aguardando { get; private set; }
    double _posInicio;
    /// <summary>Como estava cada um quando o filme começou (relatório do teste).</summary>
    public string? ComecouCom { get; private set; }
    /// <summary>Quem já recebeu as permissões e o estado (quem tem o arquivo aberto, conferindo ou pronto).</summary>
    readonly HashSet<string> _comArquivo = new();
    // D4: depois de pular, espera até 5 s todos carregarem o trecho novo
    readonly HashSet<string> _carregados = new();
    bool _pulo;
    volatile int _meuCarregado;
    public long UltimaEsperaPuloMs { get; private set; } = -1;
    /// <summary>Quem não carregou o trecho (ou o episódio) a tempo no último pulo, separado por vírgula; nulo se todos carregaram (telemetria da fila).</summary>
    public string? UltimoAtraso { get; private set; }
    static readonly int TesteLentoMs = int.TryParse(Environment.GetEnvironmentVariable("STREEMU_TEST_FILME_LENTO"), out var l) ? l : 0;

    public bool TodosProntos => Amigos.Values.All(v => v == "pronto");
    string Lista() => string.Join(",", Amigos.Select(kv => $"{NomeDe(kv.Key)}={kv.Value}"));

    int _gen;
    double _vel = 1;
    bool _alinhado;
    public double UltimoErroMs { get; private set; } = double.NaN;
    readonly List<double> _erros = new();
    int _pulos;

    public event Action? Mudou;
    public event Action<string>? Aviso;

    /// <param name="relogio">Hora do anfitrião em µs (nulo enquanto quem assiste ainda não tem o relógio comum, 7.2).</param>
    public Sessao(bool anfitriao, string filme, string nome, string nomeAnfitriao, string meuNome, Mpv mpv, Func<long?> relogio, Func<double> idaVolta, Action<JsonObject> enviar, string meuId = "")
    {
        Anfitriao = anfitriao; Filme = filme; Nome = nome; NomeAnfitriao = nomeAnfitriao; _meuNome = meuNome; _meuId = meuId;
        _mpv = mpv; _relogio = relogio; _idaVolta = idaVolta; _enviar = enviar;
        if (anfitriao)
        {
            // timer da thread da janela (o _enviar mexe nas ligações do MainForm, que só a thread da tela toca); a Sessão nasce sempre nela
            _reenvio = new System.Windows.Forms.Timer { Interval = EstadoDoFilme.ReenvioMs };
            _reenvio.Tick += (_, _) => ReenviarEstado();
            _reenvio.Start();
        }
    }

    readonly System.Windows.Forms.Timer? _reenvio;
    bool _soltada;

    /// <summary>
    /// Anfitrião: repete o estado atual com o MESMO seq (7.5). Não passa por Publicar/Definir: não muda nada aqui, não toca o mpv, não acorda a transmissão.
    /// Quem está em dia o ignora no Receber; quem perdeu um comando (caso de 2026-11-28: o play depois de uma pausa longa) vê o seq maior e aplica.
    /// </summary>
    public bool ReenviarEstado()
    {
        if (!EstadoDoFilme.DeveReenviar(Anfitriao, _seq, _soltada)) return false;
        _enviar(Estado());
        return true;
    }

    /// <summary>Onde o filme deveria estar agora, pela linha do tempo comum.</summary>
    public double Posicao
    {
        get
        {
            if (!Tocando || _relogio() is not long now) return _pos;
            return _pos + Math.Max(0, now - _t) / 1e6;
        }
    }

    public bool PodeControlar => Anfitriao || TodosControlam;
    /// <summary>Velocidade que a correção contínua (7.4) aplicou no mpv agora (1 = normal).</summary>
    public double Velocidade => _vel;
    /// <summary>O filme já começou alguma vez (o card mostra "pausado" e não "no aguardo").</summary>
    public bool Comecou => _seq > 0;

    // ------------------------------------------------------------------ comandos (anfitrião aplica; quem assiste pede)

    public void Tocar(double? pos = null)
    {
        if (Aguardando) { ComecarAgora(); return; }
        if (Anfitriao && _seq == 0 && EsperarTodos && !TodosProntos)
        {
            Aguardando = true;
            _posInicio = pos ?? _pos;
            Aviso?.Invoke("O filme começa sozinho quando todos estiverem prontos. ▶ de novo começa agora.");
            Mudou?.Invoke();
            return;
        }
        Comando("tocar", pos);
    }

    /// <summary>4.3 "começar agora": quem não está pronto entra quando terminar (a stream do modo B vem na F2).</summary>
    public void ComecarAgora()
    {
        if (!Aguardando) return;
        Aguardando = false;
        ComecouCom = Lista();
        Comando("tocar", _posInicio);
    }

    void TalvezComecar()
    {
        if (!Aguardando || !TodosProntos) return;
        Aguardando = false;
        ComecouCom = Lista();
        Log.Info($"[filme] todos prontos ({ComecouCom}): começando.");
        Comando("tocar", _posInicio);
    }

    public void Pausar() { if (Aguardando) { Aguardando = false; Mudou?.Invoke(); return; } Comando("pausar", null); }
    public void Ir(double pos) => Comando("ir", Math.Max(0, pos));

    void Comando(string acao, double? pos)
    {
        if (!Anfitriao)
        {
            if (AnfitriaoCaiu) return; // sem ligação: o pedido não iria a lugar nenhum
            if (!TodosControlam) { Aviso?.Invoke($"Só {NomeAnfitriao} controla o filme."); return; }
            _enviar(new JsonObject { ["type"] = "filme_pedido", ["filme"] = Filme, ["acao"] = acao, ["pos"] = pos, ["nome"] = _meuNome, ["id"] = _meuId.Length > 0 ? _meuId : null });
            return;
        }
        long now = _relogio() ?? Net.Tunnel.NowUs;
        long t = Sozinho ? now : now + (long)((Math.Max(0, _idaVolta()) + FolgaMs) * 1000);
        switch (acao)
        {
            case "tocar": Publicar(true, pos ?? Posicao, t); break;
            // pausa marcada: todos param no mesmo ponto do filme
            case "pausar" when Tocando: Publicar(false, _pos + (t - _t) / 1e6, t); break;
            // ir para outro ponto tocando: todos pausam no ponto novo, esperam carregar (D4) e voltam juntos
            case "ir" when Tocando: _ = PularAsync(pos ?? Posicao); break;
            case "ir": Publicar(false, pos ?? Posicao, now); break;
        }
    }

    /// <summary>
    /// D4 (spec 010, 17): depois de pular, cada um avisa (filme_carregado) quando o trecho novo carregou. O anfitrião espera todos
    /// os prontos, até 5 s; quem demora mais fica para trás e alcança sozinho pela correção contínua (7.4).
    /// </summary>
    async Task PularAsync(double pos)
    {
        _carregados.Clear();
        Publicar(false, pos, _relogio() ?? Net.Tunnel.NowUs, pulo: true);
        int seq = _seq;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<string> faltam = new();
        while (sw.ElapsedMilliseconds < 5000)
        {
            if (_seq != seq) return; // outro comando passou na frente
            faltam = Amigos.Where(kv => kv.Value == "pronto" && !_carregados.Contains(kv.Key)).Select(kv => kv.Key)
                .Concat((FluxoIds?.Invoke() ?? Enumerable.Empty<string>()).Where(n => !_carregados.Contains(n))).ToList();
            if (faltam.Count == 0 && _meuCarregado == seq) break;
            await Task.Delay(20);
        }
        if (_seq != seq) return;
        UltimaEsperaPuloMs = sw.ElapsedMilliseconds;
        var nomesFaltam = faltam.Select(NomeDe).ToList();
        UltimoAtraso = faltam.Count > 0 ? string.Join(",", nomesFaltam) : null;
        if (faltam.Count > 0) Aviso?.Invoke($"Segui sem esperar {string.Join(", ", nomesFaltam)}: demorou mais de 5 s para carregar.");
        Log.Info($"[filme] pulo: todos carregaram em {UltimaEsperaPuloMs} ms" + (faltam.Count > 0 ? $" (segui sem {string.Join(", ", nomesFaltam)})." : "."));
        Comando("tocar", pos);
    }

    /// <summary>
    /// Fila de episódios: o arquivo novo já está carregado no mpv, parado em 0. Todos pausam em 0, avisam que carregaram (o mesmo D4 do pulo) e o anfitrião toca com a hora
    /// marcada; quem passar de 5 s alcança sozinho pela correção contínua. Só o anfitrião chama. Devolve quando o episódio começou a tocar.
    /// </summary>
    public Task ComecarDaFilaAsync() => Anfitriao ? PularAsync(0) : Task.CompletedTask;

    /// <summary>
    /// A fila passou para o episódio seguinte: esta sessão acabou, mas o mpv, a ligação e quem assiste continuam. Só para o que ela faz (a correção contínua e o que estiver
    /// sendo aplicado) sem fechar o player, que a sessão nova vai usar.
    /// </summary>
    public void Soltar() { _gen++; _seq++; Tocando = false; _soltada = true; _reenvio?.Stop(); _reenvio?.Dispose(); }

    /// <summary>
    /// A sessão do episódio novo herda as escolhas e as pessoas da anterior (todos controlam, quem entrou e o estado de cada um), para ninguém precisar entrar de novo e
    /// o "Na sessão" não esvaziar na troca. Quem não tiver o arquivo novo sai pela própria ligação e o anfitrião o tira da lista.
    /// </summary>
    public void HerdarDe(Sessao velha)
    {
        TodosControlam = velha.TodosControlam;
        EsperarTodos = velha.EsperarTodos;
        FluxoIds = velha.FluxoIds;
        NomeNaSala = velha.NomeNaSala;
        foreach (var kv in velha._nomes) _nomes[kv.Key] = kv.Value;
        foreach (var kv in velha.Amigos) Amigos[kv.Key] = kv.Value;
        foreach (var n in velha._comArquivo) _comArquivo.Add(n);
        foreach (var n in velha._semSinal) _semSinal.Add(n);
    }

    void Publicar(bool tocando, double pos, long t, bool pulo = false)
    {
        _seq++;
        _pulo = pulo;
        Definir(tocando, pos, t, pulo);
        _enviar(Estado());
    }

    JsonObject Estado() => new()
    {
        ["type"] = "filme_estado", ["filme"] = Filme, ["seq"] = _seq, ["tocando"] = Tocando, ["pos"] = _pos, ["t"] = _t, ["todos"] = TodosControlam, ["pulo"] = _pulo,
    };

    public void AmigoSaiu(string id) { _comArquivo.Remove(id); _semSinal.Remove(id); if (Amigos.Remove(id)) { Mudou?.Invoke(); TalvezComecar(); } }

    // ------------------------------------------------------------------ sinal (MainForm.FilmeSinal.cs)

    /// <summary>
    /// Anfitrião: quem está sem sinal (túnel dele perdido), pelo id. Só aparece na lista de pessoas ("Sem sinal"): não muda o estado de ninguém, não pausa os outros
    /// e não atrapalha o "todos prontos" (o <see cref="Amigos"/> segue como está; a pessoa volta sozinha quando o sinal voltar).
    /// </summary>
    readonly HashSet<string> _semSinal = new();
    public bool SemSinalDe(string id) => _semSinal.Contains(id);
    /// <summary>O texto do estado da pessoa na lista, com o "sem sinal" por cima do que ela mandou.</summary>
    public string EstadoVisto(string id, string valor) => _semSinal.Contains(id) ? "sem sinal" : valor;
    public void MarcarSemSinal(string id, bool sem)
    {
        if (sem ? _semSinal.Add(id) : _semSinal.Remove(id)) Mudou?.Invoke();
    }

    /// <summary>Espectador: o anfitrião ficou sem sinal por tempo demais (<see cref="SinalDoAnfitriao.Limite"/>) e a ligação foi encerrada.</summary>
    public bool AnfitriaoCaiu { get; private set; }

    /// <summary>
    /// Espectador: o anfitrião deu por caído. Pausa o mpv aqui, no ponto em que a linha do tempo estava, e para a correção contínua. Não manda nada a ninguém (a ligação
    /// acabou); a janela fica aberta para a pessoa ler o aviso e sair.
    /// </summary>
    public void PararPorQueda()
    {
        if (Anfitriao || AnfitriaoCaiu) return;
        AnfitriaoCaiu = true;
        _pos = Posicao; // congela onde o filme estava: o Posicao com Tocando falso devolve este
        _gen++; Tocando = false; TodosControlam = false; // _gen++ cancela o que estiver sendo aplicado e a correção
        Mudou?.Invoke();
        _ = Task.Run(async () => { await _mpv.Set("pause", true); await _mpv.Set("speed", 1.0); _vel = 1; });
    }

    public void MudarPermissao(bool todos)
    {
        if (!Anfitriao) return;
        TodosControlam = todos;
        _enviar(new JsonObject { ["type"] = "filme_permissoes", ["filme"] = Filme, ["todos"] = todos });
        Mudou?.Invoke();
    }

    /// <summary>Mensagem filme_* do amigo (já na thread da janela).</summary>
    public void Receber(JsonObject m)
    {
        if (m["filme"]?.GetValue<string>() != Filme) return;
        switch (m["type"]?.GetValue<string>())
        {
            case "filme_estado" when !Anfitriao && !AnfitriaoCaiu:
            {
                int seq = m["seq"]?.GetValue<int>() ?? 0;
                bool todosAntes = TodosControlam;
                TodosControlam = m["todos"]?.GetValue<bool>() ?? false;
                // o mesmo seq é a repetição periódica do anfitrião (7.5): quem está em dia não faz nada (nem procura nem muda a velocidade)
                if (!EstadoDoFilme.EhNovo(_seq, seq)) { if (todosAntes != TodosControlam) Mudou?.Invoke(); return; }
                bool tocando = m["tocando"]?.GetValue<bool>() ?? false;
                double posicao = m["pos"]?.GetValue<double>() ?? 0;
                // seq que pulou números: um comando se perdeu pelo caminho e a repetição periódica trouxe o estado de agora (o caso de 2026-11-28)
                string pulou = _seq > 0 && seq > _seq + 1 ? $" (pulou do {_seq}: {seq - _seq - 1} comando(s) não chegaram)" : "";
                Log.Info($"[filme] estado {seq} do anfitrião: {(tocando ? "tocando" : "pausado")} em {posicao:0.#} s{pulou}.");
                _seq = seq;
                Definir(tocando, posicao, m["t"]?.GetValue<long>() ?? 0, m["pulo"]?.GetValue<bool>() ?? false);
                break;
            }
            case "filme_carregado" when Anfitriao:
                if ((m["seq"]?.GetValue<int>() ?? -1) == _seq) _carregados.Add(Quem(m, ""));
                break;
            case "filme_permissoes" when !Anfitriao:
                TodosControlam = m["todos"]?.GetValue<bool>() ?? false;
                Mudou?.Invoke();
                break;
            case "filme_pronto" when Anfitriao:
            {
                // 4.3: cada um é "baixando" (com % e tempo; a vazão que ele mede já mostra o upload dividido entre quem baixa),
                // "conferindo" (tem o arquivo e o app confere contra o manifesto, 4.1) ou "pronto"
                string id = Quem(m);
                bool temArquivo = true;
                if (m["baixando"] is JsonObject b)
                {
                    double eta = b["eta_s"]?.GetValue<double>() ?? -1;
                    Amigos[id] = $"baixando {b["pct"]?.GetValue<int>() ?? 0}%" + (eta >= 0 ? $" ({Progresso.Faltam(eta)})" : "");
                    temArquivo = false;
                }
                else if (m["conferindo"] is JsonObject c) Amigos[id] = $"conferindo {c["pct"]?.GetValue<int>() ?? 0}%";
                else if (m["defeito"] is JsonNode d) Amigos[id] = $"arquivo com {d} pedaço(s) com defeito";
                else
                {
                    temArquivo = m["pronto"]?.GetValue<bool>() ?? false;
                    Amigos[id] = temArquivo ? "pronto" : "outro arquivo";
                }
                // quem abriu o arquivo recebe as permissões (seção 10) e, com o filme rolando, o estado atual para entrar no ponto certo
                // ("primeiro" = acabou de abrir o arquivo: quem fechou o app e voltou recebe de novo)
                if (temArquivo && (_comArquivo.Add(id) | (m["primeiro"]?.GetValue<bool>() ?? false)))
                {
                    _enviar(new JsonObject { ["type"] = "filme_permissoes", ["filme"] = Filme, ["todos"] = TodosControlam });
                    if (_seq > 0) _enviar(Estado());
                }
                Mudou?.Invoke();
                TalvezComecar();
                break;
            }
            case "filme_pedido" when Anfitriao:
                string acao = m["acao"]?.GetValue<string>() ?? "";
                string quem = NomeDe(Quem(m));
                if (!TodosControlam)
                {
                    Aviso?.Invoke($"{quem} pediu para {(acao == "pausar" ? "pausar" : acao == "tocar" ? "continuar" : "pular")}.");
                    return;
                }
                double? pos = m["pos"]?.GetValue<double?>();
                Aviso?.Invoke(acao switch { "pausar" => $"{quem} pausou.", "tocar" => $"{quem} continuou.", _ => $"{quem} foi para {Tempo(pos ?? 0)}." });
                Comando(acao, pos);
                break;
        }
    }

    // ------------------------------------------------------------------ aplicar no mpv

    void Definir(bool tocando, double pos, long t, bool pulo = false)
    {
        Tocando = tocando; _pos = pos; _t = t;
        int gen = ++_gen, seq = _seq;
        Mudou?.Invoke();
        _ = Task.Run(() => AplicarAsync(gen, tocando, pos, t, pulo ? seq : -1));
    }

    /// <param name="pulo">Seq do pulo (D4): depois de carregar o trecho novo, avisa o anfitrião; -1 = não é pulo.</param>
    async Task AplicarAsync(int gen, bool tocando, double pos, long t, int pulo = -1)
    {
        try
        {
            if (_relogio() is not long now) { Aviso?.Invoke("Ainda acertando o relógio com o anfitrião; o filme entra em instantes."); now = await EsperarRelogioAsync(gen); }
            await _mpv.Set("speed", 1.0); _vel = 1;
            if (!tocando)
            {
                if (t > now) await EsperarAte(t);
                if (gen != _gen) return;
                await _mpv.Set("pause", true);
                await _mpv.Seek(pos);
                _falhasAplicar = 0;
                if (pulo >= 0)
                {
                    await _mpv.WaitSeekAsync();
                    if (TesteLentoMs > 0) await Task.Delay(TesteLentoMs); // teste: alguém que demora para carregar
                    if (gen != _gen) return;
                    if (Anfitriao) _meuCarregado = pulo;
                    else _enviar(new JsonObject { ["type"] = "filme_carregado", ["filme"] = Filme, ["seq"] = pulo, ["nome"] = _meuNome, ["id"] = _meuId.Length > 0 ? _meuId : null });
                }
                return;
            }
            if (Sozinho && t <= now)
            {
                // sozinho: despausa na hora; só procura o ponto se o mpv estiver longe dele (procurar à toa custa ~100 ms)
                double atual = await _mpv.NumAsync("time-pos");
                if (double.IsNaN(atual) || Math.Abs(atual - pos) > 0.25) { await _mpv.Seek(pos); await _mpv.WaitSeekAsync(); }
                if (gen != _gen) return;
                await _mpv.Set("pause", false);
                _falhasAplicar = 0;
                // a linha do tempo começa quando o mpv de fato despausou: senão a correção (7.4) acelera à toa para tirar esses ms
                if (gen == _gen && _relogio() is long agora) { _pos = pos; _t = agora; }
                _alinhado = false;
                await CorrigirAsync(gen);
                return;
            }
            // mensagem atrasada (ou quem entrou no meio): recomeça um pouco à frente, já no ponto em que todos estarão
            long inicio = t - now >= 150_000 ? t : now + 400_000;
            await _mpv.Set("pause", true);
            await _mpv.Seek(pos + (inicio - t) / 1e6);
            await _mpv.WaitSeekAsync();
            if (gen != _gen) return;
            await EsperarAte(inicio);
            if (gen != _gen) return;
            await _mpv.Set("pause", false);
            _falhasAplicar = 0;
            _alinhado = false;
            await CorrigirAsync(gen);
        }
        catch (OperationCanceledException) { } // um estado mais novo passou na frente (EsperarRelogioAsync): não é falha
        catch (Exception e)
        {
            // espectador: quem não consegue aplicar o estado do anfitrião fica fora do ponto sem saber; uma falha avulsa só vai para o log, a que se repete vai para a tela
            Log.Info($"[filme] falha ao aplicar o estado: {e.Message}");
            if (!Anfitriao && ++_falhasAplicar >= 2 && gen == _gen)
                Aviso?.Invoke("Não estou conseguindo seguir o filme do anfitrião (o player não responde). Se continuar assim, saia e entre de novo.");
        }
    }

    /// <summary>Falhas seguidas ao aplicar o estado do anfitrião no mpv (zera quando uma aplicação chega ao fim do passo principal).</summary>
    int _falhasAplicar;

    async Task<long> EsperarRelogioAsync(int gen)
    {
        while (gen == _gen) { if (_relogio() is long n) return n; await Task.Delay(100); }
        throw new OperationCanceledException();
    }

    /// <summary>Até o instante (µs no relógio do anfitrião): dorme até perto e gira o último milissegundo (despausar na hora é o que dá o &lt;1 ms do S1).</summary>
    async Task EsperarAte(long alvoUs)
    {
        while (_relogio() is long n && alvoUs - n > 20_000) await Task.Delay((int)Math.Min(200, (alvoUs - n - 15_000) / 1000));
        while (_relogio() is long n && alvoUs - n > 2_000) Thread.Sleep(1);
        while (_relogio() is long n && n < alvoUs) Thread.SpinWait(50);
    }

    /// <summary>
    /// Tabela da 7.4: até 15 ms nada; até 1 s muda a velocidade (±5%, alcança em ~2 s, o mpv mantém o tom); mais de 1 s pula.
    /// A posição vem do audio-pts (contínuo); sem som, do time-pos.
    /// </summary>
    async Task CorrigirAsync(int gen)
    {
        await Task.Delay(300);
        while (gen == _gen && Tocando && !_mpv.Exited)
        {
            long? a0 = _relogio();
            double p = await _mpv.NumAsync("audio-pts");
            long? a1 = _relogio();
            if (double.IsNaN(p)) p = await _mpv.NumAsync("time-pos");
            if (gen != _gen) break;
            if (!double.IsNaN(p) && a0 is long x && a1 is long y)
            {
                double esperado = _pos + ((x + y) / 2 - _t) / 1e6;
                double err = p - esperado;
                UltimoErroMs = err * 1000;
                if (Math.Abs(err) < 0.015) _alinhado = true;
                if (_alinhado) lock (_erros) _erros.Add(Math.Abs(err) * 1000);
                if (Math.Abs(err) > 1)
                {
                    _pulos++; _alinhado = false;
                    Log.Info($"[filme] fora do ponto {err * 1000:N0} ms: pulo.");
                    await _mpv.Set("speed", 1.0); _vel = 1;
                    await _mpv.Seek(esperado + 0.3);
                }
                else
                {
                    double v = Math.Abs(err) > 0.015 ? Math.Clamp(1 - err / 2, 0.95, 1.05) : Math.Abs(err) < 0.005 ? 1 : _vel;
                    if (Math.Abs(v - _vel) >= 0.001) { _vel = v; await _mpv.Set("speed", v); }
                }
            }
            await Task.Delay(250);
        }
    }

    /// <summary>Para o relatório do teste e do diagnóstico: erro contra o relógio comum depois de alinhar.</summary>
    public JsonObject Relatorio()
    {
        double[] e; lock (_erros) e = _erros.OrderBy(v => v).ToArray();
        double Q(double q) => e.Length == 0 ? -1 : Math.Round(e[Math.Min(e.Length - 1, (int)(e.Length * q))], 1);
        return new JsonObject
        {
            ["papel"] = Anfitriao ? "anfitriao" : "espectador", ["seq"] = _seq, ["tocando"] = Tocando, ["amostras"] = e.Length,
            ["erro_mediana_ms"] = Q(0.5), ["erro_p95_ms"] = Q(0.95), ["erro_max_ms"] = e.Length == 0 ? -1 : Math.Round(e[^1], 1), ["pulos"] = _pulos, ["espera_pulo_ms"] = UltimaEsperaPuloMs, ["comecou_com"] = ComecouCom, ["anfitriao_caiu"] = AnfitriaoCaiu,
        };
    }

    public static string Tempo(double s)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, s));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    public void Dispose()
    {
        _gen++;
        _soltada = true;
        _reenvio?.Stop(); _reenvio?.Dispose();
        _mpv.Dispose();
    }
}
