using System.Text.Json.Nodes;
using Streemu.Ui.Avatars;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// Avatares entre amigos (plano 005, T5): manda o meu avatar a TODOS da sala (não só a quem assiste) quando ele muda e quando
/// alguém novo entra, e guarda os que chegam. O envio é simétrico (quem chega manda o seu; quem já estava manda ao que chegou), mas o
/// amigo só sabe que eu "cheguei" se me viu sair, e a lista segura quem sumiu por 10 s: por isso quem abre o app também PEDE o avatar de quem ainda
/// não tem (<see cref="AskMissingAvatars"/>). Fica fora do MainForm.Grupo.cs para dar menos conflito.
/// </summary>
public sealed partial class MainForm
{
    FriendAvatars? _friendAvatars;
    /// <summary>O que cada amigo já recebeu de mim (a assinatura da última mensagem), para não repetir.</summary>
    readonly Dictionary<string, string> _avatarSentTo = new();
    /// <summary>Quando pedi o avatar de cada amigo, para não pedir de novo a cada piscada da lista (no máximo 1 vez a cada 30 s).</summary>
    readonly Dictionary<string, DateTime> _avatarAskedAt = new();
    /// <summary>Quem estava na sala na última atualização: a diferença para a atual diz quem saiu.</summary>
    HashSet<string> _avatarRoomIds = new();
    /// <summary>Quem era membro do grupo na última atualização (online ou guardado como ausente); a diferença para a atual diz quem deixou de ser membro.</summary>
    HashSet<string> _avatarMembros = new();

    /// <summary>Chamado quando a lista da sala muda: quem deixou de ser membro é esquecido, quem chegou recebe o meu avatar e eu peço o de quem ainda não tenho.</summary>
    void OnRoomMembersForAvatars()
    {
        if (_room == null) return;
        var present = _room.Friends.Select(f => f.Id).ToHashSet();
        var arrived = present.Where(id => !_avatarRoomIds.Contains(id)).ToList();
        // Plano 019, etapa 7: quem fechou o app continua sendo membro (aparece em OFFLINE, com a foto); só é esquecido quem DEIXOU DE SER MEMBRO
        // (não está online nem em Ausentes). Na sala antiga Ausentes é vazia e vale o de antes. O pedido e o envio continuam só a quem está online.
        var membros = new HashSet<string>(present);
        foreach (var a in _room.Ausentes) membros.Add(a.Id);
        _friendAvatars?.Forget(_avatarMembros.Where(id => !membros.Contains(id)).ToList());
        _avatarMembros = membros;
        _avatarRoomIds = present;
        AskMissingAvatars(arrived);
        AnnounceAvatar();
    }

    /// <summary>
    /// Pede o avatar de quem eu ainda não tenho. "Quem chega manda o seu" não basta: o amigo só esquece que já me mandou quando me vê sair da lista, e a lista
    /// segura quem sumiu por 10 s (AwayGrace). A atualização automática fecha e reabre o app em poucos segundos, então ele nunca parece ter saído: o amigo acha
    /// que eu já tenho o avatar dele e eu, com a memória vazia, ficava só com a inicial colorida (2026-11-11: três reaberturas seguidas, avatares sumindo).
    /// </summary>
    void AskMissingAvatars(IEnumerable<string> arrived)
    {
        if (_room is not { Connected: true } || _friendAvatars == null) return;
        var now = DateTime.UtcNow;
        foreach (var id in arrived.Where(id => _friendAvatars.ImageOf(id) == null && (!_avatarAskedAt.TryGetValue(id, out var at) || now - at > TimeSpan.FromSeconds(30))))
        {
            _avatarAskedAt[id] = now;
            _ = _room.SendTo(id, AvatarMessage.Ask());
        }
    }

    /// <summary>
    /// A sala trocou: os pedidos e envios feitos na anterior não valem. (Antes da spec 022 o app abria primeiro a sala antiga, pela senha, e trocava para a da
    /// conta: era a MESMA sala do servidor, com outro id, e em meio segundo o app via os amigos, pedia a foto deles por ali e a resposta chegava à conexão que
    /// já ia fechar.) O pedido ficava marcado e, na sala da conta, barrado por 30 s sem repetir; o amigo também não reenviava (não me viu sair).
    /// Um amigo ficou sem a foto dos amigos até eles reabrirem o app (telemetria de 2026-11-18).
    /// </summary>
    void RecomecarAvataresDaSala()
    {
        _avatarAskedAt.Clear();
        _avatarSentTo.Clear();
    }

    /// <summary>O amigo abriu o app e quer o meu avatar: esquece que já mandei e manda de novo.</summary>
    void OnAvatarAsked(string from)
    {
        _avatarSentTo.Remove(from);
        AnnounceAvatar();
    }

    void ApplyFriendAvatar(string from, string nick, JsonObject msg) => _ = ApplyFriendAvatarAsync(from, nick, msg);

    /// <summary>Aplica e registra no log quando o avatar do amigo passou a valer (sem id nem arquivo: só o apelido).</summary>
    async Task ApplyFriendAvatarAsync(string from, string nick, JsonObject msg)
    {
        if (_friendAvatars == null) return;
        await _friendAvatars.ApplyAsync(from, msg);
        if (_friendAvatars.ImageOf(from) != null) Log.Info($"Avatar de {nick} recebido.");
    }

    /// <summary>Manda o avatar atual a quem ainda não o tem. Sem sala conectada não faz nada (e não marca ninguém como atendido).</summary>
    void AnnounceAvatar()
    {
        if (_room is not { Connected: true } || _myAvatar == null) return;
        string signature = AvatarSignature();
        var friends = _room.Friends.ToList();
        foreach (var gone in _avatarSentTo.Keys.Where(id => friends.All(f => f.Id != id)).ToList()) _avatarSentTo.Remove(gone);
        foreach (var friend in friends.Where(f => !_avatarSentTo.TryGetValue(f.Id, out var sent) || sent != signature))
        {
            bool hadAnother = _avatarSentTo.ContainsKey(friend.Id);
            _avatarSentTo[friend.Id] = signature;
            if (signature == "none" && !hadAnother) continue; // quem nunca recebeu nada já mostra a inicial colorida
            _ = _room.SendTo(friend.Id, BuildAvatarMessage()); // um objeto novo por amigo: um JsonNode só pode ter um "pai" (reusar lançava a partir do 2º amigo)
        }
    }

    /// <summary>Quem vale (spec 005): imagem do PC, depois o Discord (se a caixa está ligada e a conta tem avatar), depois nenhum.</summary>
    string AvatarSignature()
    {
        if (_myAvatar!.Hash != null) return "img:" + _myAvatar.Hash;
        return DiscordAccountToShare() is var (_, code) ? "discord:" + code : "none";
    }

    JsonObject BuildAvatarMessage()
    {
        if (_myAvatar!.Payload is { } payload) return AvatarMessage.Image(payload, _myAvatar.PayloadFormat, _myAvatar.Hash!);
        return DiscordAccountToShare() is var (id, code) ? AvatarMessage.Discord(id, code) : AvatarMessage.None();
    }

    (string Id, string Code)? DiscordAccountToShare()
    {
        if (!_useDiscordAvatar.Checked || _discord is not { Connected: true } d) return null;
        return DiscordAvatarUrl.TryBuild(d.DiscordUserId, d.DiscordAvatarCode, out _) ? (d.DiscordUserId!, d.DiscordAvatarCode!) : null;
    }
}
