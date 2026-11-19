using System.Text.RegularExpressions;

namespace Streemu.Ui.Avatars;

/// <summary>Baixa bytes de um endereço, com teto de tamanho e de tempo. Fica atrás de uma interface para os testes não dependerem da internet.</summary>
public interface IAvatarDownloader
{
    /// <summary>Os bytes, ou nulo se falhou (sem rede, erro do servidor, grande demais, demorou demais). Nunca lança por falha de rede.</summary>
    Task<byte[]?> GetAsync(Uri url, int maxBytes, CancellationToken cancel);
}

/// <summary>Implementação com HttpClient. O tempo limite vale para o download inteiro, não só para a primeira resposta.</summary>
public sealed class HttpAvatarDownloader : IAvatarDownloader
{
    static readonly HttpClient Client = CreateClient();
    readonly TimeSpan _timeout;

    public HttpAvatarDownloader(TimeSpan? timeout = null) => _timeout = timeout ?? TimeSpan.FromSeconds(6);

    static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { MaxAutomaticRedirections = 3 }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Streemu");
        return client;
    }

    public async Task<byte[]?> GetAsync(Uri url, int maxBytes, CancellationToken cancel)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(_timeout);
        try
        {
            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > maxBytes) return null;
            return await ReadAtMost(await response.Content.ReadAsStreamAsync(limit.Token), maxBytes, limit.Token);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException) { return null; }
    }

    /// <summary>Lê no máximo <paramref name="maxBytes"/>; se o corpo é maior (o servidor não avisou o tamanho), desiste sem ler o resto.</summary>
    static async Task<byte[]?> ReadAtMost(Stream body, int maxBytes, CancellationToken cancel)
    {
        using var result = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await body.ReadAsync(buffer, cancel)) > 0)
        {
            if (result.Length + n > maxBytes) return null;
            result.Write(buffer, 0, n);
        }
        return result.ToArray();
    }
}

/// <summary>O endereço público do avatar de uma conta do Discord (CDN), com o id e o código conferidos antes de virarem parte da URL.</summary>
public static partial class DiscordAvatarUrl
{
    [GeneratedRegex(@"^[0-9]{15,22}$")] private static partial Regex IdPattern();
    [GeneratedRegex(@"^(a_)?[0-9a-f]{32}$")] private static partial Regex CodePattern();

    /// <summary>
    /// Id numérico (15 a 22 dígitos) e código de 32 hex (com "a_" na frente se o avatar é animado). Qualquer outra coisa é recusada:
    /// o par pode vir de um amigo pela sala, e não pode levar o app a pedir um endereço que ele não escolheu.
    /// Uso: <c>if (DiscordAvatarUrl.TryBuild(id, code, out var url)) await downloader.GetAsync(url, ...);</c>
    /// </summary>
    public static bool TryBuild(string? id, string? code, out Uri url)
    {
        url = null!;
        if (id == null || code == null || !IdPattern().IsMatch(id) || !CodePattern().IsMatch(code)) return false;
        string ext = code.StartsWith("a_", StringComparison.Ordinal) ? "gif" : "png";
        url = new Uri($"https://cdn.discordapp.com/avatars/{id}/{code}.{ext}?size=64");
        return true;
    }
}
