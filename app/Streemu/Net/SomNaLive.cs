using System.Text.Json.Nodes;

namespace Streemu.Net;

public enum TipoSomNaLive { Entrou, Saiu }

/// <summary>Um som a mandar: o tipo e os ids (na sala) dos espectadores que devem ouvi-lo.</summary>
public sealed record SomParaEnviar(TipoSomNaLive Tipo, IReadOnlyList<string> Destinatarios);

/// <summary>
/// A regra dos sons da live (spec 014): quem ouve que alguém entrou ou saiu, e quando. Sem rede nem interface; o relógio entra pelo construtor.
/// - Quem causou o evento nunca ouve (a live é uma só, então o som vai por mensagem a cada espectador, não misturado no áudio).
/// - Vários do mesmo tipo em <see cref="Junta"/> viram um som só (o primeiro toca na hora; os seguintes dentro da janela são absorvidos).
/// - A saída espera <see cref="Tolerancia"/>: se a mesma pessoa volta antes, a saída some e a volta não toca (queda de conexão não faz barulho).
/// - Desligado (opção do anfitrião, ou live sem áudio), nada é mandado.
/// Uso: <c>var s = new SomNaLive(() => DateTime.Now) { Ligado = true }; var som = s.Entrou(id, conectados); foreach (var d in s.Coletar(conectados)) ...</c>
/// </summary>
public sealed class SomNaLive
{
    public const string TipoMensagem = "som_na_live";
    public static readonly TimeSpan Junta = TimeSpan.FromSeconds(2), Tolerancia = TimeSpan.FromSeconds(10);

    readonly Func<DateTime> _agora;
    readonly Dictionary<string, DateTime> _saidasPendentes = new(); // id -> quando saiu
    DateTime _ultimoEntrou = DateTime.MinValue, _ultimoSaiu = DateTime.MinValue;
    bool _ligado;

    public SomNaLive(Func<DateTime> agora) => _agora = agora;

    /// <summary>Desligar descarta o que estava esperando (uma saída pendente não toca depois de ligar de novo).</summary>
    public bool Ligado
    {
        get => _ligado;
        set { _ligado = value; if (!value) _saidasPendentes.Clear(); }
    }

    /// <summary>Alguém conectou. Devolve o som de entrada a mandar, ou nulo (desligado, volta rápida, junto com outra entrada, ou ninguém para ouvir).</summary>
    public SomParaEnviar? Entrou(string id, IReadOnlyCollection<string> conectados)
    {
        ExigirId(id);
        if (!_ligado) return null;
        if (_saidasPendentes.Remove(id)) return null; // saiu e voltou dentro da tolerância: nem a saída nem a volta tocam
        var agora = _agora();
        if (agora - _ultimoEntrou < Junta) return null;
        _ultimoEntrou = agora;
        return ParaTodosMenos(TipoSomNaLive.Entrou, id, conectados);
    }

    /// <summary>Alguém desconectou: a saída fica esperando a tolerância (a mesma pessoa pode voltar).</summary>
    public void Saiu(string id)
    {
        ExigirId(id);
        if (_ligado) _saidasPendentes[id] = _agora();
    }

    /// <summary>De tempos em tempos: os sons de saída cuja tolerância acabou (os que caem na mesma junta viram um só).</summary>
    public IReadOnlyList<SomParaEnviar> Coletar(IReadOnlyCollection<string> conectados)
    {
        var prontos = new List<SomParaEnviar>();
        var agora = _agora();
        foreach (string id in _saidasPendentes.Where(p => agora - p.Value >= Tolerancia).OrderBy(p => p.Value).Select(p => p.Key).ToList())
        {
            _saidasPendentes.Remove(id);
            if (agora - _ultimoSaiu < Junta) continue;
            _ultimoSaiu = agora;
            if (ParaTodosMenos(TipoSomNaLive.Saiu, id, conectados) is { } som) prontos.Add(som);
        }
        return prontos;
    }

    /// <summary>A live acabou: nada pendente sobrevive.</summary>
    public void Encerrar() { _saidasPendentes.Clear(); _ultimoEntrou = _ultimoSaiu = DateTime.MinValue; }

    public static JsonObject Mensagem(TipoSomNaLive tipo, string streamId) => new()
    {
        ["tipo"] = TipoMensagem,
        ["som"] = tipo == TipoSomNaLive.Entrou ? "entrou" : "saiu",
        ["sala"] = streamId,
    };

    /// <summary>Lê a mensagem de um anfitrião; falso se o conteúdo não é uma mensagem de som válida (versão futura, streemu).</summary>
    public static bool TentarLer(JsonObject msg, out TipoSomNaLive tipo, out string streamId)
    {
        tipo = default;
        streamId = msg["sala"]?.GetValue<string>() ?? "";
        string? som = msg["som"]?.GetValue<string>();
        if (streamId == "" || som is not ("entrou" or "saiu")) return false;
        tipo = som == "entrou" ? TipoSomNaLive.Entrou : TipoSomNaLive.Saiu;
        return true;
    }

    static SomParaEnviar? ParaTodosMenos(TipoSomNaLive tipo, string causador, IReadOnlyCollection<string> conectados)
    {
        var ouvintes = conectados.Where(c => c != causador).Distinct().ToList();
        return ouvintes.Count == 0 ? null : new SomParaEnviar(tipo, ouvintes);
    }

    static void ExigirId(string id)
    {
        if (string.IsNullOrEmpty(id)) throw new ArgumentException($"id do espectador vazio (recebi '{id}'); esperava o id dele na sala.", nameof(id));
    }
}
