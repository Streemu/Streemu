using System.Text.Json.Nodes;

namespace Streemu.Net;

/// <summary>
/// Várias salas de grupo vistas como uma só (spec 019, etapa 8). O resto do app continua falando com uma <see cref="ISala"/>: quem está online em
/// QUALQUER grupo aparece uma vez só, uma live aparece para todos os grupos em que a pessoa está, e o pedido de "quero assistir" sai pelo grupo
/// em que os dois estão. Salas entram e saem com o app aberto, sem derrubar as outras (criar, entrar e sair de grupo).
/// Sem nenhuma sala dentro ela é a "sala vazia": é o que o app usa antes de a conta estar de pé e sem conta (sem conexão, sem amigos).
/// </summary>
public sealed class SalaUnida : ISala
{
    sealed record Entrada(string GrupoId, string Nome, ISala Sala, Action Desligar);

    /// <summary>Quanto juntar de avisos de mudança num só: três salas mudando juntas não refazem a tela três vezes.</summary>
    public static readonly TimeSpan JuntarMudancas = TimeSpan.FromMilliseconds(100);

    readonly object _gate = new();
    readonly List<Entrada> _salas = new();
    readonly string _meuId;
    string _nick;
    LiveInfo? _live;
    System.Threading.Timer? _timer;
    bool _agendado;
    bool _descartada;

    public SalaUnida(string meuId, string nick) { _meuId = meuId; _nick = nick; }

    public string MyId => _meuId;
    public event Action? Changed;
    public event Action<string, string, JsonObject>? Message;
    public event Action<string, JsonObject?>? Undeliverable;
    public event Action? TelemetryNow;
    public event Action? AtividadesMudaram;
    public event Action? EncerrarLive;
    public event Action<string>? LiveEncerrada;
    /// <summary>Só o "sessao" (a sessão da conta acabou) sobe como <see cref="Encerrada"/>: vale para todos os grupos. Removido e saiu são de UM grupo: ver <see cref="GrupoEncerrado"/>.</summary>
    public event Action<string>? Encerrada;
    /// <summary>Um grupo acabou para esta pessoa (removido, saiu): (id do grupo, motivo). A sala dele já foi tirada daqui.</summary>
    public event Action<string, string>? GrupoEncerrado;

    Entrada[] Copia() { lock (_gate) return _salas.ToArray(); }

    /// <summary>Os grupos que estão aqui, na ordem em que entraram.</summary>
    public IReadOnlyList<(string Id, string Nome)> Grupos => Copia().Select(e => (e.GrupoId, e.Nome)).ToList();
    public int Quantidade { get { lock (_gate) return _salas.Count; } }
    public bool Tem(string grupoId) { lock (_gate) return _salas.Any(e => e.GrupoId == grupoId); }

    /// <summary>Põe uma sala. A nova já recebe o apelido e a live de agora. Com o mesmo id de grupo, troca a antiga.</summary>
    public void Adicionar(string grupoId, string nome, ISala sala)
    {
        Action aoMudar = Mudou;
        Action<string, string, JsonObject> aoMensagem = (de, ap, msg) => Message?.Invoke(de, ap, msg);
        Action<string, JsonObject?> aoNaoEntregue = (para, msg) => AoNaoEntregue(sala, para, msg);
        Action aoTelemetria = () => TelemetryNow?.Invoke();
        Action aoAtividade = () => AtividadesMudaram?.Invoke();
        Action<string> aoEncerrar = motivo => AoEncerrar(grupoId, sala, motivo);
        Action aoEncerrarLive = () => EncerrarLive?.Invoke();
        Action<string> aoLiveEncerrada = conta => LiveEncerrada?.Invoke(conta);
        sala.Changed += aoMudar; sala.Message += aoMensagem; sala.Undeliverable += aoNaoEntregue; sala.TelemetryNow += aoTelemetria; sala.Encerrada += aoEncerrar; sala.AtividadesMudaram += aoAtividade;
        sala.EncerrarLive += aoEncerrarLive; sala.LiveEncerrada += aoLiveEncerrada;
        Action desligar = () => { sala.Changed -= aoMudar; sala.Message -= aoMensagem; sala.Undeliverable -= aoNaoEntregue; sala.TelemetryNow -= aoTelemetria; sala.Encerrada -= aoEncerrar; sala.AtividadesMudaram -= aoAtividade; sala.EncerrarLive -= aoEncerrarLive; sala.LiveEncerrada -= aoLiveEncerrada; };
        var nova = new Entrada(grupoId, nome, sala, desligar);
        Entrada? antiga;
        lock (_gate)
        {
            if (_descartada) { desligar(); sala.Dispose(); return; }
            antiga = _salas.FirstOrDefault(e => e.GrupoId == grupoId);
            if (antiga != null) _salas.Remove(antiga);
            _salas.Add(nova);
        }
        if (antiga != null) { antiga.Desligar(); antiga.Sala.Dispose(); }
        sala.SetNick(_nick);
        if (_live != null) sala.SetLive(_live);
        Mudou();
    }

    /// <summary>Tira e desliga a sala do grupo. As outras continuam.</summary>
    public bool Remover(string grupoId)
    {
        Entrada? e;
        lock (_gate)
        {
            e = _salas.FirstOrDefault(x => x.GrupoId == grupoId);
            if (e == null) return false;
            _salas.Remove(e);
        }
        e.Desligar();
        e.Sala.Dispose();
        Mudou();
        return true;
    }

    /// <summary>Muda só o nome mostrado do grupo (o dono renomeou).</summary>
    public void Renomear(string grupoId, string nome)
    {
        lock (_gate)
        {
            int i = _salas.FindIndex(e => e.GrupoId == grupoId);
            if (i >= 0) _salas[i] = _salas[i] with { Nome = nome };
        }
        Mudou();
    }

    void AoEncerrar(string grupoId, ISala sala, string motivo)
    {
        if (motivo == "sessao") { Encerrada?.Invoke(motivo); return; }
        // a sala que acabou só sai daqui se ainda é a daquele grupo (uma troca de sala pode ter chegado antes)
        Entrada? e;
        lock (_gate)
        {
            e = _salas.FirstOrDefault(x => x.GrupoId == grupoId && ReferenceEquals(x.Sala, sala));
            if (e != null) _salas.Remove(e);
        }
        if (e == null) return;
        e.Desligar();
        e.Sala.Dispose();
        GrupoEncerrado?.Invoke(grupoId, motivo);
        Mudou();
    }

    void AoNaoEntregue(ISala sala, string para, JsonObject? msg)
    {
        // uma sala não achou o destinatário, mas ele pode estar em outra: só vale o aviso se nenhuma outra o tem conectado
        foreach (var e in Copia())
            if (!ReferenceEquals(e.Sala, sala) && e.Sala.Connected && e.Sala.IsPresent(para)) return;
        Undeliverable?.Invoke(para, msg);
    }

    // ---- vista unida

    public bool Connected => Copia().Any(e => e.Sala.Connected);

    public string Status
    {
        get
        {
            var salas = Copia();
            if (salas.Length == 0) return "Você ainda não está em nenhum grupo.";
            var queFalha = salas.FirstOrDefault(e => !e.Sala.Connected);
            if (queFalha != null) return queFalha.Sala.Status;
            return salas.Length == 1 ? salas[0].Sala.Status : $"Em {salas.Length} grupos.";
        }
    }

    /// <summary>Uma linha por pessoa. Entre linhas da mesma pessoa vale a que está ao vivo; sem live, a do primeiro grupo.</summary>
    public IReadOnlyList<Member> Members
    {
        get
        {
            var porId = new Dictionary<string, Member>();
            var ordem = new List<string>();
            foreach (var e in Copia())
                foreach (var m in e.Sala.Members)
                {
                    if (!porId.TryGetValue(m.Id, out var ja)) { porId[m.Id] = m; ordem.Add(m.Id); }
                    else if (ja.Live == null && m.Live != null) porId[m.Id] = m;
                }
            return MesmaPessoa.Juntar(ordem.Select(i => porId[i]).ToList(), MyId); // a mesma pessoa com ids diferentes (em um grupo ou em dois) vira uma linha só
        }
    }

    public IEnumerable<Member> Friends => Members.Where(m => m.Id != MyId);

    /// <summary>Quem está guardado em algum grupo e não está online em NENHUM (online num grupo basta para não ser "ausente").</summary>
    public IReadOnlyList<Ausente> Ausentes
    {
        get
        {
            var salas = Copia();
            var online = new HashSet<string>(salas.SelectMany(e => e.Sala.Members).Select(m => m.Id));
            var vistos = new HashSet<string>();
            var r = new List<Ausente>();
            foreach (var e in salas)
                foreach (var a in e.Sala.Ausentes)
                    if (!online.Contains(a.Id) && vistos.Add(a.Id)) r.Add(a);
            return r;
        }
    }

    /// <summary>As atividades de todos os grupos juntas, da mais nova para a mais antiga (no máximo 50), cada uma marcada com o grupo de onde veio.</summary>
    public IReadOnlyList<Atividade> Atividades =>
        Copia().SelectMany(e => e.Sala.Atividades.Select(a => a with { GrupoId = e.GrupoId }))
            .OrderByDescending(a => a.Hora).ThenByDescending(a => a.Id).Take(50).ToList();

    /// <summary>"assistindo" sai só pelo(s) grupo(s) em que o alvo está ao vivo (o servidor de outro grupo recusaria); "filme" sai por todos.</summary>
    public void EnviarAtividade(string tipo, string? alvo = null)
    {
        foreach (var e in Copia())
        {
            if (tipo == "assistindo" && !(e.Sala.Members.Any(m => m.Id == alvo && m.Live != null))) continue;
            e.Sala.EnviarAtividade(tipo, alvo);
        }
    }

    public bool IsPresent(string id) => Copia().Any(e => e.Sala.IsPresent(id));
    // se a junção escondeu a linha, cada sala ainda a acha pelo id (uma ligação aberta com esse id não perde o dono)
    public Member? Find(string id) => Members.FirstOrDefault(m => m.Id == id) ?? Copia().Select(e => e.Sala.Find(id)).FirstOrDefault(m => m != null);
    public Member? FindByStream(string streamId) => Members.FirstOrDefault(m => m.Live?.StreamId == streamId) ?? Copia().Select(e => e.Sala.FindByStream(streamId)).FirstOrDefault(m => m != null);

    /// <summary>Em quais grupos (ids) a pessoa está, online ou guardada.</summary>
    public IReadOnlyList<string> GruposDe(string id) =>
        Copia().Where(e => e.Sala.Members.Any(m => m.Id == id) || e.Sala.Ausentes.Any(a => a.Id == id)).Select(e => e.GrupoId).ToList();

    /// <summary>
    /// O grupo (id) em que <paramref name="donoId"/> transmite a live <paramref name="live"/> (o <c>sala</c> do <c>ao_vivo</c>), para a denúncia (spec 029, E3). Vale o primeiro grupo
    /// em que a sala mostra essa live no ar com esse dono. Sem isso (a live acabou de sumir da lista), só se a pessoa está em UM grupo conectado: com dois não dá para
    /// adivinhar, e o servidor descartaria a denúncia do grupo errado. Nulo = não sei.
    /// </summary>
    public string? GrupoDaLive(string donoId, string live)
    {
        var salas = Copia();
        var comLive = salas.Where(e => e.Sala.Find(donoId)?.Live?.StreamId == live).Select(e => e.GrupoId).ToList();
        if (comLive.Count > 0) return comLive[0];
        var dele = GruposDe(donoId);
        return dele.Count == 1 ? dele[0] : null;
    }

    // ---- o que o app manda

    public void SetNick(string nick)
    {
        _nick = nick;
        foreach (var e in Copia()) e.Sala.SetNick(nick);
    }

    public void SetLive(LiveInfo? live)
    {
        _live = live;
        foreach (var e in Copia()) e.Sala.SetLive(live);
    }

    public Task SendTo(string id, JsonObject msg)
    {
        var por = Copia().FirstOrDefault(e => e.Sala.Connected && e.Sala.IsPresent(id));
        if (por != null) return por.Sala.SendTo(id, msg);
        // ninguém o tem conectado agora: o aviso de "não entregue" sobe uma vez só, como numa sala única
        Undeliverable?.Invoke(id, msg);
        return Task.CompletedTask;
    }

    public Task SendTelemetryNow(IEnumerable<string> ids)
    {
        var lista = ids.ToList();
        var tarefas = new List<Task>();
        foreach (var e in Copia())
        {
            var daSala = lista.Where(i => e.Sala.IsPresent(i)).ToList();
            if (daSala.Count > 0) tarefas.Add(e.Sala.SendTelemetryNow(daSala));
        }
        return Task.WhenAll(tarefas);
    }

    // ---- Changed: no máximo um disparo a cada JuntarMudancas

    void Mudou()
    {
        lock (_gate)
        {
            if (_agendado || _descartada) return;
            _agendado = true;
            _timer ??= new System.Threading.Timer(_ => Disparar());
            _timer.Change(JuntarMudancas, Timeout.InfiniteTimeSpan);
        }
    }

    void Disparar()
    {
        lock (_gate) { _agendado = false; if (_descartada) return; }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        Entrada[] salas;
        lock (_gate) { _descartada = true; salas = _salas.ToArray(); _salas.Clear(); _timer?.Dispose(); }
        foreach (var e in salas) { e.Desligar(); e.Sala.Dispose(); }
    }
}
