using System.Text.Json.Nodes;
using Streemu.Ui.Multistream;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// Miniaturas de quem está ao vivo (spec 006, M4/M5). Quem assiste pede (<c>miniatura_pedir</c>) a quem está transmitindo e guarda o JPEG que
/// volta (<c>miniatura</c>); quem transmite liga o gerador (<see cref="Media.StreamSender.WantThumbnail"/>) e manda a cada espectador que pediu
/// nos últimos 45 s. Fica fora do MainForm.Grupo.cs para dar menos conflito.
/// </summary>
public sealed partial class MainForm
{
    readonly LiveThumbnails _liveThumbs = new();
    /// <summary>Quem pediu a minha miniatura e até quando (renovado a cada pedido; o painel pede de novo a cada ~30 s).</summary>
    readonly Dictionary<string, DateTime> _thumbAskers = new();

    void InitThumbnails() => _sender.ThumbnailReady += jpeg => Ui(() => SendThumbnailToAskers(jpeg));

    /// <summary>Um amigo pediu (ou parou de pedir) a miniatura da minha transmissão. Sem transmissão rodando, ignora.</summary>
    void OnThumbnailAsk(string from, JsonObject msg)
    {
        if (msg["ligar"]?.GetValue<bool>() != true) { _thumbAskers.Remove(from); return; }
        if (Privada.Ligada && !Privada.Permite(from, msg["codigo_convite"]?.GetValue<string>())) return; // privada: só convidado, com o código dele
        if (!_sender.Running) return;
        _thumbAskers[from] = DateTime.UtcNow + Media.ThumbnailFeeder.Lifetime;
        _sender.WantThumbnail();
        if (_sender.LatestThumbnail is { } last) SendThumbnail(from, last); // não deixa o cartão vazio até a próxima (a cada 8 s)
    }

    void SendThumbnailToAskers(byte[] jpeg)
    {
        foreach (var gone in _thumbAskers.Where(p => p.Value < DateTime.UtcNow).Select(p => p.Key).ToList()) _thumbAskers.Remove(gone);
        foreach (var id in _thumbAskers.Keys.ToList()) SendThumbnail(id, jpeg);
    }

    void SendThumbnail(string to, byte[] jpeg)
    {
        // _streamId, não o estado da sala: na privada a sala não sabe da live, mas o convidado ainda recebe a miniatura
        if (_room is not { Connected: true } || _streamId is not { } streamId) return;
        if (Privada.Ligada && !Privada.EstaNaLista(to)) return;
        _ = _room.SendTo(to, ThumbnailMessage.Image(streamId, jpeg));
    }

    /// <summary>Uma miniatura chegou de um amigo.</summary>
    readonly HashSet<string> _thumbnailDecoding = new();
    void OnThumbnailReceived(JsonObject msg)
    {
        if (msg["sala"]?.GetValue<string>() is not { } id || _thumbnailDecoding.Count >= ThumbnailLimits.MaxStreams || !_thumbnailDecoding.Add(id)) return;
        _ = DecodeLiveThumbnailAsync(id, msg);
    }

    async Task DecodeLiveThumbnailAsync(string id, JsonObject msg)
    {
        try { if (await Task.Run(() => _liveThumbs.Apply(msg)) && !IsDisposed) SetLiveCards(); }
        finally { _thumbnailDecoding.Remove(id); }
    }

    /// <summary>Pede (ou para de pedir) a miniatura de todos que estão ao vivo; o painel "Ao vivo agora" chama ao abrir, a cada ~30 s e ao fechar.</summary>
    void AskThumbnails(bool on)
    {
        if (_room is not { Connected: true }) return;
        foreach (var friend in _room.Friends.Where(f => f.Live != null))
            _ = _room.SendTo(friend.Id, ThumbnailMessage.Ask(on));
    }

    /// <summary>"Ver todos": abre, na janela de streams, quem está ao vivo e ainda não está em tela, até o limite de 4.</summary>
    void WatchAllLive()
    {
        if (_room == null || InicioMudouAgora()) return;
        int room = PlayerStage.MaxStreams - (_playerStage?.Count ?? 0);
        foreach (var friend in _room.Friends.Where(f => f.Live != null && !_links.Values.Any(l => !l.IAmSender && l.PeerId == f.Id)).Take(Math.Max(0, room)))
            _ = WatchAsync(friend, "botão Assistir todos juntos");
    }

    IReadOnlyList<LiveCard> LiveCards()
    {
        if (_room == null) return Array.Empty<LiveCard>();
        return _room.Friends.Where(f => f.Live != null).OrderBy(f => f.Nick)
            .Select(f => new LiveCard(f.Id, NomeNaTela(f), f.Live!.Game, f.Live.Quality, _liveThumbs.Get(f.Live.StreamId),
                                      _links.Values.Any(l => !l.IAmSender && l.PeerId == f.Id), _friendAvatars?.ImageOf(f.Id))).ToList();
    }

    /// <summary>
    /// O Início mudou de lugar há pouco (um convite ou pedido saiu do topo e o resto subiu)? Então o clique que está chegando foi dado
    /// para o que estava ali antes, não para o que está ali agora: ignora (relato de 2026-11-15: o segundo clique no Aceitar abriu a live de outro amigo).
    /// </summary>
    bool InicioMudouAgora()
    {
        if (_homePage is not { TopChangedRecently: true }) return false;
        Log.Info("Clique ignorado: o Início acabou de mudar de lugar.");
        return true;
    }

    void PickLiveCard(LiveCard card)
    {
        if (InicioMudouAgora()) return;
        if (card.Watching) { ShowView(stage: true); return; }
        if (_playerStage is { Full: true }) { Notify($"Já são {PlayerStage.MaxStreams} streams na janela. Feche uma para abrir outra."); return; }
        if (_room?.Find(card.FriendId) is { Live: not null } member) _ = WatchAsync(member, "cartão do Início");
    }

    /// <summary>Quem parou de transmitir perde a miniatura (o cartão some junto com o "ao vivo").</summary>
    void ForgetEndedThumbnails()
    {
        if (_room == null) return;
        var live = _room.Friends.Where(f => f.Live != null).Select(f => f.Live!.StreamId).ToHashSet();
        foreach (var friend in _room.Friends.Where(f => f.Live == null)) _thumbAskers.Remove(friend.Id);
        _liveThumbs.KeepOnly(live);
    }
}
