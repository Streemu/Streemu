using System.Text.Json.Nodes;
using Streemu.Util;

namespace Streemu.Ui.Avatars;

/// <summary>
/// Os avatares dos amigos da sala: valida o que chega pela mensagem `avatar` e guarda no <see cref="AvatarCache"/> (a mesma imagem
/// de dois amigos vale uma vez só). Tudo que vem de fora é tratado como não confiável: formato, tamanho, hash e URL do Discord
/// são conferidos, e o que não passa é ignorado sem erro (o amigo continua com a inicial colorida).
/// Uso: <c>await friends.ApplyAsync(fromId, msg); var image = friends.ImageOf(fromId);</c>
/// </summary>
public sealed class FriendAvatars
{
    readonly IAvatarDownloader _downloader;
    readonly AvatarCache _cache;
    readonly object _gate = new();
    readonly Dictionary<string, string> _keyByFriend = new();
    readonly Dictionary<string, CancellationTokenSource> _pending = new();

    public FriendAvatars(IAvatarDownloader downloader, AvatarCache cache)
    {
        _downloader = downloader;
        _cache = cache;
    }

    /// <summary>Disparado quando o avatar de alguém mudou (em thread de fundo: quem usa passa para a tela).</summary>
    public event Action? Changed;

    public AvatarImage? ImageOf(string friendId)
    {
        lock (_gate) return _keyByFriend.TryGetValue(friendId, out var key) ? _cache.Get(key) : null;
    }

    /// <summary>Os ids que têm avatar agora (para o relatório de teste).</summary>
    public IReadOnlyList<string> FriendsWithAvatar()
    {
        lock (_gate) return _keyByFriend.Where(p => _cache.Contains(p.Value)).Select(p => p.Key).ToList();
    }

    /// <summary>
    /// Esquece quem SAIU da sala (e cancela o download dele em andamento). Quem chama passa só os que saíram, não "os que não estão
    /// na lista agora": a mensagem de avatar de um amigo novo pode chegar ANTES da lista de membros que o inclui, e apagá-lo aí o
    /// deixaria sem avatar para sempre (ele não reenvia).
    /// </summary>
    public void Forget(IEnumerable<string> leftIds)
    {
        lock (_gate)
            foreach (var id in leftIds) Forget(id);
    }

    /// <summary>Aplica a mensagem de um amigo. Mensagem malformada (campo com o tipo errado, por exemplo) é ignorada, nunca lança.</summary>
    public async Task ApplyAsync(string friendId, JsonObject msg)
    {
        try
        {
            switch (msg["fonte"]?.GetValue<string>())
            {
                case "nenhum": Replace(friendId, null); break;
                case "imagem": ApplyImage(friendId, msg); break;
                case "discord": await ApplyDiscordAsync(friendId, msg); break;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            Log.Info($"Mensagem de avatar malformada de um amigo ({e.GetType().Name}); ignorei.");
        }
    }

    void ApplyImage(string friendId, JsonObject msg)
    {
        var bytes = DecodeImagePayload(msg);
        if (bytes == null) return;
        string key = "img:" + msg["hash"]!.GetValue<string>();
        if (!_cache.Contains(key))
        {
            var load = msg["formato"]!.GetValue<string>() == "tira" ? AvatarFilmstrip.Decode(bytes) : AvatarDecoder.Load(bytes, AvatarOrigin.Remote);
            if (load.Image == null) { Log.Info("Um amigo mandou um avatar que não abriu; ignorei."); return; }
            _cache.Put(key, load.Image);
        }
        Replace(friendId, key);
    }

    /// <summary>Os bytes da imagem se o tamanho, o formato e o hash batem; nulo se algo estiver errado (nada é decodificado antes).</summary>
    static byte[]? DecodeImagePayload(JsonObject msg)
    {
        if (msg["dados"]?.GetValue<string>() is not { } base64 || msg["hash"]?.GetValue<string>() is not { } hash) return null;
        if (base64.Length > AvatarLimits.RemoteMaxBytes * 4 / 3 + 8) return null;
        if (msg["formato"]?.GetValue<string>() is not ("png" or "gif" or "tira")) return null;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { return null; }
        return bytes.Length <= AvatarLimits.RemoteMaxBytes && AvatarMessage.HashOf(bytes) == hash ? bytes : null;
    }

    async Task ApplyDiscordAsync(string friendId, JsonObject msg)
    {
        if (!DiscordAvatarUrl.TryBuild(msg["id"]?.GetValue<string>(), msg["codigo"]?.GetValue<string>(), out var url)) return;
        string key = "discord:" + msg["codigo"]!.GetValue<string>();
        if (_cache.Contains(key)) { Replace(friendId, key); return; }
        var cancel = StartDownload(friendId);
        var bytes = await _downloader.GetAsync(url, (int)AvatarLimits.RemoteMaxBytes, cancel.Token);
        if (cancel.IsCancellationRequested || bytes == null) return;
        var load = AvatarDecoder.Load(bytes, AvatarOrigin.Remote);
        if (load.Image == null) return;
        _cache.Put(key, load.Image);
        Replace(friendId, key);
    }

    CancellationTokenSource StartDownload(string friendId)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(friendId, out var old)) old.Cancel();
            return _pending[friendId] = new CancellationTokenSource();
        }
    }

    /// <summary>Troca o avatar do amigo e cancela o download antigo dele (a mensagem mais nova vale).</summary>
    void Replace(string friendId, string? key)
    {
        lock (_gate)
        {
            if (_pending.Remove(friendId, out var old)) old.Cancel();
            if (key == null) _keyByFriend.Remove(friendId); else _keyByFriend[friendId] = key;
        }
        Changed?.Invoke();
    }

    void Forget(string friendId)
    {
        if (_pending.Remove(friendId, out var old)) old.Cancel();
        _keyByFriend.Remove(friendId);
    }
}
