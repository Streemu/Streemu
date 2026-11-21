using Streemu.Net;
using Streemu.Ui.Avatars;

namespace Streemu.Ui;

/// <summary>Uma pessoa na faixa de participantes do player.</summary>
public sealed record RosterEntry(string Nick, bool Live, bool IsMe, AvatarImage? Avatar = null, string Id = "")
{
    /// <summary>Quem a linha é: o id da conta (o nome só aparece na tela e pode repetir). Sem id (dados de teste), o nome.</summary>
    public string Key => Id.Length > 0 ? Id : Nick;

    /// <summary>Máximo de pessoas que a faixa mostra (o grupo é pequeno; passar disso é erro de dados, não de uso).</summary>
    public const int MaxEntries = 99;

    /// <summary>Quem transmite primeiro, depois eu, depois os outros por ordem alfabética. Corta em <see cref="MaxEntries"/>.</summary>
    public static IReadOnlyList<RosterEntry> Ordered(IEnumerable<RosterEntry> entries) =>
        entries.OrderByDescending(e => e.Live).ThenByDescending(e => e.IsMe)
               .ThenBy(e => e.Nick, StringComparer.CurrentCultureIgnoreCase).Take(MaxEntries).ToList();

    /// <summary>
    /// Quem assiste a UMA transmissão: o transmissor (ao vivo), quem a lista do transmissor diz que assiste, e eu. Não é o grupo todo:
    /// amigo que está na sala mas não abriu o player não entra. Quem saiu da sala some. Antes da lista chegar, só transmissor e eu.
    /// </summary>
    public static IReadOnlyList<RosterEntry> FromWatchers(ISala? room, string streamerId, IReadOnlySet<string> watcherIds,
                                                                      Func<string, AvatarImage?> avatarOf, Func<Member, string>? nomeNaTela = null) =>
        room == null ? Array.Empty<RosterEntry>()
                     : Ordered(room.Members.Where(m => m.Id == streamerId || m.Id == room.MyId || watcherIds.Contains(m.Id))
                                           .Select(m => new RosterEntry(nomeNaTela?.Invoke(m) ?? m.Nick, m.Id == streamerId, m.Id == room.MyId, avatarOf(m.Id), m.Id)));
}
