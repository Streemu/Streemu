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
/// Quem pode entrar na minha transmissão privada (spec 012). A lista vive só no PC do anfitrião: o servidor da sala nunca
/// sabe que a stream existe, porque a privada não chama <c>GroupRoom.SetLive</c>.
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

    readonly Func<string, bool> _estaNaSala;
    readonly Func<string> _novoCodigo;
    readonly int _limite;
    readonly Dictionary<string, Convidado> _convidados = new();
    string? _streamId;

    /// <param name="estaNaSala">Pergunta se o amigo está online agora (<c>GroupRoom.IsPresent</c>); quem está offline não recebe convite guardado.</param>
    /// <param name="novoCodigo">Gerador do código de convite; o padrão são 128 bits aleatórios.</param>
    public SalaPrivada(Func<string, bool> estaNaSala, Func<string>? novoCodigo = null, int limite = Limite)
    {
        _estaNaSala = estaNaSala;
        _novoCodigo = novoCodigo ?? CodigoAleatorio;
        _limite = limite;
    }

    public bool Ligada => _streamId != null;
    public IReadOnlyCollection<Convidado> Convidados => _convidados.Values;

    public void Iniciar(string streamId)
    {
        _convidados.Clear();
        _streamId = streamId;
    }

    /// <summary>A stream acabou: todos os convites e códigos morrem junto.</summary>
    public void Encerrar()
    {
        _convidados.Clear();
        _streamId = null;
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

    /// <summary>O convidado recusou: o anfitrião o tira da lista. Quem aceita não manda nada; o pedido de assistir já diz.</summary>
    public static JsonObject MensagemRecusa(string streamId) => new() { ["tipo"] = TipoResposta, ["sala"] = streamId, ["aceito"] = false };

    public JsonObject MensagemRemocao() => new() { ["tipo"] = TipoRemocao, ["sala"] = StreamIdOuErro() };

    string StreamIdOuErro() => _streamId ?? throw new InvalidOperationException("A transmissão privada não está ligada; esperado: chamar Iniciar(streamId) antes de montar mensagens.");

    static ResultadoConvite Recusa(string motivo) => new(null, motivo);

    static string CodigoAleatorio() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}
