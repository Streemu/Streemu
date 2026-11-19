using Streemu.Util;

namespace Streemu.Ui.Avatars;

/// <summary>
/// O avatar do Discord da conta conectada: baixa a imagem pública da CDN, decodifica e guarda no <see cref="AvatarCache"/> (a mesma
/// imagem não é baixada duas vezes). Se a conta muda no meio de um download, o antigo é cancelado e vale o último.
/// Falha de rede nunca vira erro para o usuário: o avatar só fica nulo e a tela mostra a inicial colorida.
/// O id e o código da conta nunca vão para o log.
/// Uso: <c>_ = source.SetAccountAsync(discord.DiscordUserId, discord.DiscordAvatarCode); source.Changed += refresh;</c>
/// </summary>
public sealed class DiscordAvatarSource
{
    readonly IAvatarDownloader _downloader;
    readonly AvatarCache _cache;
    readonly object _gate = new();
    CancellationTokenSource? _current;
    string? _key;

    public DiscordAvatarSource(IAvatarDownloader downloader, AvatarCache cache)
    {
        _downloader = downloader;
        _cache = cache;
    }

    /// <summary>O avatar atual (nulo se a conta não tem avatar próprio, ainda não chegou ou o download falhou).</summary>
    public AvatarImage? Image
    {
        get { lock (_gate) return _key == null ? null : _cache.Get(_key); }
    }

    /// <summary>Disparado quando <see cref="Image"/> mudou (em thread de fundo: quem usa precisa passar para a tela).</summary>
    public event Action? Changed;

    /// <summary>Passa a usar essa conta. Id ou código nulos ou inválidos (conta sem avatar próprio) limpam o avatar.</summary>
    public async Task SetAccountAsync(string? id, string? code)
    {
        bool valid = DiscordAvatarUrl.TryBuild(id, code, out var url);
        string? key = valid ? "discord:" + code : null;
        CancellationToken token;
        lock (_gate)
        {
            _current?.Cancel();
            _current = new CancellationTokenSource();
            token = _current.Token;
            _key = key;
        }
        Changed?.Invoke();
        if (key == null || _cache.Contains(key)) return;
        await Download(url, key, token);
    }

    async Task Download(Uri url, string key, CancellationToken token)
    {
        var bytes = await _downloader.GetAsync(url, (int)AvatarLimits.RemoteMaxBytes, token);
        if (token.IsCancellationRequested) return;
        var load = bytes == null ? null : AvatarDecoder.Load(bytes, AvatarOrigin.Remote);
        if (load?.Image == null)
        {
            Log.Info("Não consegui o avatar do Discord (sem rede, conta sem avatar ou imagem inválida); uso a inicial colorida.");
            return;
        }
        _cache.Put(key, load.Image);
        Changed?.Invoke();
    }
}
