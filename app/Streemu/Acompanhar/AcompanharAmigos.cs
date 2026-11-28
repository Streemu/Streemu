namespace Streemu.Acompanhar;

/// <summary>O que o app deve fazer agora por causa de um amigo acompanhado (spec 038).</summary>
public enum TipoAcaoAcompanhar
{
    /// <summary>O amigo abriu live dentro do prazo e posso entrar: entra sozinho.</summary>
    Entrar,
    /// <summary>O amigo abriu live, mas já estou assistindo outra coisa (D3): só avisa, com Entrar. A espera continua valendo.</summary>
    AvisarLiveAberta,
    /// <summary>O prazo acabou sem o amigo voltar: avisa uma vez que parou de esperar.</summary>
    DesistiuDeEsperar,
}

public readonly record struct AcaoAcompanhar(TipoAcaoAcompanhar Tipo, string AmigoId);

/// <summary>Como a sala e a tela veem um amigo agora: tem live aberta? eu a estou assistindo?</summary>
public readonly record struct AmigoVisto(string Id, bool TemLive, bool EuAssisto);

/// <summary>Espera em andamento por um amigo. <see cref="Ate"/> nulo = "até o amigo voltar", sem prazo.</summary>
public readonly record struct EsperaDoAmigo(DateTime Desde, DateTime? Ate);

/// <summary>
/// "Continuar assistindo" (spec 038): decide, a cada olhada na sala, quando esperar, entrar sozinho, só avisar ou desistir. Regra pura:
/// o relógio e o prazo entram por parâmetro, e quem age (entrar na live, mostrar aviso) é o chamador. Nada aqui guarda dado em servidor.
/// A espera só começa quando o amigo FECHA uma live que eu assistia com o app aberto; fechar e abrir o app nunca continua uma espera (critério 10).
/// </summary>
public sealed class AcompanharAmigos
{
    /// <summary>
    /// Depois de eu entrar (ou de o amigo fechar a live), a ligação demora a subir e a sala demora a atualizar. Nesse tempo "não estou assistindo"
    /// não quer dizer "fechei a live": sem esta folga, a entrada automática se desfazia sozinha antes de a imagem chegar.
    /// </summary>
    internal static readonly TimeSpan FolgaSemAssistir = TimeSpan.FromSeconds(30);

    enum Fase { Parado, Assistindo, Esperando }

    sealed class Situacao
    {
        public Fase Fase;
        public DateTime? SemAssistirDesde;
        public DateTime? Desde;
        public DateTime? Ate;
        public bool Avisou;
    }

    readonly Func<DateTime> _agora;
    readonly IReadOnlyDictionary<string, int> _acompanhados;
    readonly Func<int> _prazoPadraoMinutos;
    readonly Dictionary<string, Situacao> _situacoes = new();
    DateTime _ultimaEntrada = DateTime.MinValue;

    /// <param name="acompanhados">id do amigo → minutos (0 = até voltar; negativo = usar o padrão). É a mesma lista do estado salvo: marcar ou desmarcar vale na hora.</param>
    public AcompanharAmigos(Func<DateTime> agora, IReadOnlyDictionary<string, int> acompanhados, Func<int> prazoPadraoMinutos)
    {
        _agora = agora;
        _acompanhados = acompanhados;
        _prazoPadraoMinutos = prazoPadraoMinutos;
    }

    /// <summary>A espera em andamento por este amigo, ou nulo se não estou esperando por ele.</summary>
    public EsperaDoAmigo? EsperaDe(string amigoId) =>
        _situacoes.TryGetValue(amigoId, out var s) && s.Fase == Fase.Esperando ? new EsperaDoAmigo(s.Desde!.Value, s.Ate) : null;

    /// <summary>Para de esperar este amigo agora (botão "Parar de esperar", "Sair" depois da entrada automática, ou eu mesmo fechei a live dele). A marca continua: a próxima live que ele fechar volta a esperar.</summary>
    public void Parar(string amigoId) => _situacoes.Remove(amigoId);

    /// <summary>Olha a sala. <paramref name="possoEntrar"/>: não estou assistindo outra live nem jogando em tela cheia.</summary>
    public IReadOnlyList<AcaoAcompanhar> Atualizar(IEnumerable<AmigoVisto> amigos, bool possoEntrar)
    {
        var agora = _agora();
        EsquecerQuemSaiuDaLista();
        var vistos = amigos.ToDictionary(a => a.Id);
        var acoes = new List<AcaoAcompanhar>();
        foreach (var id in _acompanhados.Keys)
        {
            var visto = vistos.TryGetValue(id, out var v) ? v : new AmigoVisto(id, false, false);
            bool livre = possoEntrar && agora - _ultimaEntrada >= FolgaSemAssistir;
            var acao = Avancar(SituacaoDe(id), visto, livre, agora);
            if (acao is null) continue;
            if (acao.Value.Tipo == TipoAcaoAcompanhar.Entrar) _ultimaEntrada = agora;
            acoes.Add(acao.Value);
        }
        return acoes;
    }

    Situacao SituacaoDe(string id)
    {
        if (!_situacoes.TryGetValue(id, out var s)) _situacoes[id] = s = new Situacao();
        return s;
    }

    void EsquecerQuemSaiuDaLista()
    {
        foreach (var id in _situacoes.Keys.Where(k => !_acompanhados.ContainsKey(k)).ToList()) _situacoes.Remove(id);
    }

    AcaoAcompanhar? Avancar(Situacao s, AmigoVisto amigo, bool livre, DateTime agora) => s.Fase switch
    {
        Fase.Parado => DoParado(s, amigo),
        Fase.Assistindo => DoAssistindo(s, amigo, agora),
        _ => DoEsperando(s, amigo, livre, agora),
    };

    static AcaoAcompanhar? DoParado(Situacao s, AmigoVisto amigo)
    {
        if (amigo.TemLive && amigo.EuAssisto) { s.Fase = Fase.Assistindo; s.SemAssistirDesde = null; }
        return null;
    }

    AcaoAcompanhar? DoAssistindo(Situacao s, AmigoVisto amigo, DateTime agora)
    {
        if (!amigo.TemLive) { ComecarEspera(s, amigo.Id, agora); return null; }
        if (amigo.EuAssisto) { s.SemAssistirDesde = null; return null; }
        s.SemAssistirDesde ??= agora;
        if (agora - s.SemAssistirDesde >= FolgaSemAssistir) s.Fase = Fase.Parado; // fechei a live dele eu mesmo: não insistir
        return null;
    }

    void ComecarEspera(Situacao s, string amigoId, DateTime agora)
    {
        int minutos = MinutosDe(amigoId);
        s.Fase = Fase.Esperando;
        s.Desde = agora;
        s.Ate = minutos == 0 ? null : agora.AddMinutes(minutos);
        s.Avisou = false;
    }

    int MinutosDe(string amigoId)
    {
        int proprio = _acompanhados[amigoId];
        return proprio < 0 ? _prazoPadraoMinutos() : proprio;
    }

    AcaoAcompanhar? DoEsperando(Situacao s, AmigoVisto amigo, bool livre, DateTime agora)
    {
        if (amigo.TemLive) return DaLiveAbertaNaEspera(s, amigo, livre, agora);
        if (s.Ate is null || agora < s.Ate) return null;
        s.Fase = Fase.Parado;
        return new AcaoAcompanhar(TipoAcaoAcompanhar.DesistiuDeEsperar, amigo.Id);
    }

    static AcaoAcompanhar? DaLiveAbertaNaEspera(Situacao s, AmigoVisto amigo, bool livre, DateTime agora)
    {
        if (amigo.EuAssisto) { EntrouEmAssistir(s); return null; } // cliquei em Entrar eu mesmo
        if (livre) { EntrouEmAssistir(s); s.SemAssistirDesde = agora; return new AcaoAcompanhar(TipoAcaoAcompanhar.Entrar, amigo.Id); }
        if (s.Avisou) return null;
        s.Avisou = true;
        return new AcaoAcompanhar(TipoAcaoAcompanhar.AvisarLiveAberta, amigo.Id);
    }

    static void EntrouEmAssistir(Situacao s) { s.Fase = Fase.Assistindo; s.SemAssistirDesde = null; }
}
