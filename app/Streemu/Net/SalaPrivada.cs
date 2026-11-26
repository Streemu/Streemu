using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Streemu.Net;

/// <summary>Um amigo convidado para a transmissão privada. <paramref name="Codigo"/> só existe no PC do anfitrião e no do convidado.</summary>
public sealed record Convidado(string Id, string Apelido, string Codigo);

/// <summary>Resultado de <see cref="SalaPrivada.Convidar"/>: ou o convidado, ou o motivo (frase pronta para mostrar ao usuário).</summary>
public sealed record ResultadoConvite(Convidado? Convidado, string? Motivo)
{
    public bool Ok => Convidado != null;
}

/// <summary>
/// Quem pode entrar na minha transmissão privada (spec 012). A lista de convidados e os códigos vivem só no PC do anfitrião, e a privada
/// não chama <c>GroupRoom.SetLive</c>, então o grupo nunca a vê. Desde a spec 029 (emenda A, 17.3.1) o servidor da sala sabe que ela existe:
/// o app a declara (<see cref="MensagemDeclaracao"/>) para a moderação, e a sala não difunde a declaração a ninguém.
/// O id na sala é escolhido pelo próprio app (relay/worker.js, <c>m.id</c>), então qualquer membro pode se passar por outro;
/// por isso cada convite leva um código aleatório e o pedido de assistir só vale com o código do convidado certo.
/// Uso: <c>privada.Iniciar(streamId); var r = privada.Convidar(id, "Ana"); room.SendTo(id, privada.MensagemConvite(r.Convidado!, "Bruno"));</c>
/// </summary>
public sealed class SalaPrivada
{
    /// <summary>Pessoas na sala contando o anfitrião (decisão D1); número único para subir quando medirmos o upload.</summary>
    public const int Limite = 6;
    public const string TipoConvite = "convite_privada";
    public const string TipoRemocao = "remover_privada";
    public const string TipoResposta = "convite_resposta";
    /// <summary>Mensagem do app à sala (campo <c>t</c>, não <c>tipo</c>): declara ou encerra a privada do anfitrião.</summary>
    public const string TDeclaracao = "privada";
    /// <summary>Mensagem do app à sala: o convidado abriu o player da privada do anfitrião.</summary>
    public const string TAssistindo = "assistindo_privada";

    readonly Func<string, bool> _estaNaSala;
    readonly Func<string> _novoCodigo;
    readonly int _limite;
    readonly Action<string?>? _declarar;
    readonly Dictionary<string, Convidado> _convidados = new();
    string? _streamId;

    /// <param name="estaNaSala">Pergunta se o amigo está online agora (<c>GroupRoom.IsPresent</c>); quem está offline não recebe convite guardado.</param>
    /// <param name="novoCodigo">Gerador do código de convite; o padrão são 128 bits aleatórios.</param>
    /// <param name="declarar">Avisa a sala: o id da stream ao ligar e <c>null</c> ao desligar (a sala do grupo repassa só ao servidor, nunca aos amigos).</param>
    public SalaPrivada(Func<string, bool> estaNaSala, Func<string>? novoCodigo = null, int limite = Limite, Action<string?>? declarar = null)
    {
        _estaNaSala = estaNaSala;
        _novoCodigo = novoCodigo ?? CodigoAleatorio;
        _limite = limite;
        _declarar = declarar;
    }

    public bool Ligada => _streamId != null;
    /// <summary>O id da stream enquanto a privada está ligada (é o que o convidado e a declaração levam).</summary>
    public string? StreamId => _streamId;
    public IReadOnlyCollection<Convidado> Convidados => _convidados.Values;

    public void Iniciar(string streamId)
    {
        _convidados.Clear();
        _streamId = streamId;
        Declarar(streamId);
    }

    /// <summary>A stream acabou: todos os convites e códigos morrem junto, e a sala é avisada de que a privada acabou.</summary>
    public void Encerrar()
    {
        var estava = _streamId != null;
        _convidados.Clear();
        _streamId = null;
        if (estava) Declarar(null);
    }

    // a declaração é um extra para o servidor: se o envio falhar, a transmissão privada não pode cair por isso
    void Declarar(string? id)
    {
        try { _declarar?.Invoke(id); }
        catch { /* sem log aqui: este arquivo compila sozinho no teste, e o GroupRoom já registra falha de envio */ }
    }

    public ResultadoConvite Convidar(string id, string apelido)
    {
        if (!Ligada) return Recusa("A transmissão privada não está ligada.");
        if (_convidados.TryGetValue(id, out var jaConvidado)) return new ResultadoConvite(jaConvidado, null);
        if (!_estaNaSala(id)) return Recusa($"{apelido} não está online. Convide quem está na sala.");
        if (_convidados.Count + 1 >= _limite) return Recusa($"A sala está cheia: no máximo {_limite} pessoas, contando você.");
        var convidado = new Convidado(id, apelido, _novoCodigo());
        _convidados[id] = convidado;
        return new ResultadoConvite(convidado, null);
    }

    public bool Remover(string id) => _convidados.Remove(id);

    /// <summary>Só o id (sem código): para o que não carrega código, como mandar a miniatura a quem já foi convidado.</summary>
    public bool EstaNaLista(string id) => Ligada && _convidados.ContainsKey(id);

    /// <summary>Verdadeiro só se o id está na lista <b>e</b> o código bate com o dele (comparação de tempo constante).</summary>
    public bool Permite(string id, string? codigo)
    {
        if (!Ligada || codigo == null || !_convidados.TryGetValue(id, out var convidado)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(convidado.Codigo), Encoding.UTF8.GetBytes(codigo));
    }

    public JsonObject MensagemConvite(Convidado convidado, string apelidoAnfitriao) => new()
    {
        ["tipo"] = TipoConvite,
        ["sala"] = StreamIdOuErro(),
        ["anfitriao"] = apelidoAnfitriao,
        ["codigo_convite"] = convidado.Codigo,
    };

    /// <summary>O convidado recusou: o anfitrião o tira da lista. Quem aceita manda <see cref="MensagemAceite"/> só para o servidor registrar; para o anfitrião o pedido de assistir já diz.</summary>
    public static JsonObject MensagemRecusa(string streamId) => new() { ["tipo"] = TipoResposta, ["sala"] = streamId, ["aceito"] = false };

    /// <summary>O convidado aceitou: o servidor registra o aceite (spec 029, 17.3.1, item 3). O anfitrião continua sabendo pelo pedido de assistir.</summary>
    public static JsonObject MensagemAceite(string streamId) => new() { ["tipo"] = TipoResposta, ["sala"] = streamId, ["aceito"] = true };

    public JsonObject MensagemRemocao() => new() { ["tipo"] = TipoRemocao, ["sala"] = StreamIdOuErro() };

    /// <summary>Declaração à sala: <c>{t:"privada", id}</c> ao ligar e <c>{t:"privada", id:null}</c> ao desligar. Vai só ao servidor (nunca no <c>para</c>).</summary>
    public static JsonObject MensagemDeclaracao(string? streamId) => new() { ["t"] = TDeclaracao, ["id"] = streamId };

    /// <summary>O convidado abriu o player da privada de <paramref name="alvo"/> (a conta do anfitrião): <c>{t:"assistindo_privada", alvo, id}</c>.</summary>
    public static JsonObject MensagemAssistindo(string alvo, string streamId) => new() { ["t"] = TAssistindo, ["alvo"] = alvo, ["id"] = streamId };

    string StreamIdOuErro() => _streamId ?? throw new InvalidOperationException("A transmissão privada não está ligada; esperado: chamar Iniciar(streamId) antes de montar mensagens.");

    static ResultadoConvite Recusa(string motivo) => new(null, motivo);

    static string CodigoAleatorio() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}
