using System.Text.Json.Nodes;

namespace Streemu.Net;

/// <summary>Quem e qual live se denuncia: o grupo (id de 8 caracteres), o <c>sala</c> do <c>ao_vivo</c> do membro e o nome para o título da janela.</summary>
public sealed record DenunciaAlvo(string Grupo, string Live, string Apelido);

/// <summary>As categorias da janela de denúncia (spec 029, 6.1), na ordem em que aparecem.</summary>
public enum CategoriaDenuncia { AbusoInfantil, Odio, Golpe, Assedio, Outro }

/// <summary>
/// Regras puras da denúncia (spec 029, seção 6 e contrato da E3): categorias, corpo do <c>POST /denuncia</c> e o que pode ou não ir junto.
/// Nada aqui escreve em log nem em telemetria (spec 029, 15.16): quem chama também não pode.
/// </summary>
public static class Denuncia
{
    public const int TextoMax = 500;

    /// <summary>Texto da janela e código que o servidor entende, nesta ordem.</summary>
    public static readonly IReadOnlyList<(CategoriaDenuncia Categoria, string Rotulo, string Codigo)> Categorias =
    [
        (CategoriaDenuncia.AbusoInfantil, "Abuso infantil", "abuso_infantil"),
        (CategoriaDenuncia.Odio, "Ódio ou nazismo", "odio"),
        (CategoriaDenuncia.Golpe, "Golpe ou fraude", "golpe"),
        (CategoriaDenuncia.Assedio, "Assédio ou ameaça", "assedio"),
        (CategoriaDenuncia.Outro, "Outro", "outro"),
    ];

    public const string Agradecimento = "Recebemos sua denúncia. Quem transmite não fica sabendo que foi você.";
    public const string SemAlvo = "Não dá para denunciar esta live agora.";

    public static string Codigo(CategoriaDenuncia categoria) => Categorias.First(c => c.Categoria == categoria).Codigo;

    /// <summary>Abuso infantil nunca leva imagem: não se captura, não se guarda e não se manda (spec 029, seção 10).</summary>
    public static bool PodeAnexarImagem(CategoriaDenuncia? categoria) => categoria != CategoriaDenuncia.AbusoInfantil;

    /// <summary>O texto sem espaços nas pontas e com no máximo <see cref="TextoMax"/> caracteres (sem cortar um par substituto ao meio). Vazio vira nulo.</summary>
    public static string? LimparTexto(string? texto)
    {
        var t = (texto ?? "").Trim();
        if (t.Length == 0) return null;
        if (t.Length > TextoMax)
        {
            int fim = TextoMax;
            if (char.IsHighSurrogate(t[fim - 1])) fim--;
            t = t[..fim].TrimEnd();
        }
        return t;
    }

    /// <summary>
    /// O corpo do <c>POST /denuncia</c>: <c>{grupo, live, categoria, texto?, captura?}</c>. <paramref name="texto"/> e <paramref name="captura"/> só entram se existirem;
    /// em abuso infantil a imagem é largada mesmo que venha (o servidor também a descartaria, mas ela nem deve sair do PC).
    /// </summary>
    public static JsonObject MontarCorpo(DenunciaAlvo alvo, CategoriaDenuncia categoria, string? texto, byte[]? captura)
    {
        var corpo = new JsonObject { ["grupo"] = alvo.Grupo, ["live"] = alvo.Live, ["categoria"] = Codigo(categoria) };
        if (LimparTexto(texto) is { } t) corpo["texto"] = t;
        if (captura is { Length: > 0 } && PodeAnexarImagem(categoria)) corpo["captura"] = Convert.ToBase64String(captura);
        return corpo;
    }
}
