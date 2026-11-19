namespace Streemu.Avisos;

/// <summary>
/// Os amigos cujos avisos de "começou uma live" e "abriu um filme" o usuário calou pelo menu da foto (spec 013).
/// Vale até desfazer. É conveniência, não segurança: o id na sala é escolhido pelo app do amigo. Convites de transmissão privada nunca consultam isto.
/// Uso: <c>var mudos = new AmigosSilenciados(estado.AmigosSilenciados); mudos.Alternar(id); if (!mudos.Contem(id)) Avisar();</c>
/// </summary>
public sealed class AmigosSilenciados
{
    readonly HashSet<string> _ids;

    /// <summary>Avisa quem guarda o estado de que a lista mudou (para gravar no estado.json).</summary>
    public event Action? Mudou;

    public AmigosSilenciados(IEnumerable<string>? idsGuardados)
    {
        _ids = new HashSet<string>(idsGuardados ?? Array.Empty<string>(), StringComparer.Ordinal);
    }

    public bool Contem(string id) => _ids.Contains(id);

    /// <summary>Uma cópia ordenada, pronta para gravar.</summary>
    public IReadOnlyList<string> Ids => _ids.OrderBy(id => id, StringComparer.Ordinal).ToList();

    /// <summary>Cala o amigo se estava ligado, liga de novo se estava calado. Devolve verdadeiro se agora ele está calado.</summary>
    public bool Alternar(string id)
    {
        if (string.IsNullOrEmpty(id)) throw new ArgumentException($"id do amigo vazio; esperava o id dele na sala (recebi '{id}').", nameof(id));
        bool calou = _ids.Add(id);
        if (!calou) _ids.Remove(id);
        Mudou?.Invoke();
        return calou;
    }
}
