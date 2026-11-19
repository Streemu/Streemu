using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Streemu.Ui.Avatars;
using Streemu.Util;

namespace Streemu.Ui.Multistream;

/// <summary>
/// As mensagens da miniatura de uma transmissão (spec 006): o espectador pede (<c>miniatura_pedir</c>) quando abre o painel "Ao vivo agora" e quem
/// transmite manda um JPEG pequeno de tempos em tempos (<c>miniatura</c>), pelas mensagens de amigo para amigo da sala (o Worker e o túnel não mudam).
/// Uma versão antiga ignora tipos que não conhece, então não vê a miniatura e não quebra. Nunca leva caminho, nome de arquivo nem IP.
/// Uso: <c>room.SendTo(friendId, ThumbnailMessage.Ask(true));</c> / <c>room.SendTo(viewerId, ThumbnailMessage.Image(streamId, jpeg));</c>
/// </summary>
public static class ThumbnailMessage
{
    public const string Type = "miniatura", AskType = "miniatura_pedir";

    public static JsonObject Ask(bool on) => new() { ["tipo"] = AskType, ["ligar"] = on };

    public static JsonObject Image(string streamId, byte[] jpeg) => new()
    {
        ["tipo"] = Type, ["sala"] = streamId, ["hash"] = AvatarMessage.HashOf(jpeg), ["dados"] = Convert.ToBase64String(jpeg)
    };
}

/// <summary>Limites da miniatura (spec 006): JPEG de até 192x108 px e 12 KB, no máximo uma a cada 5 s por transmissão.</summary>
public static class ThumbnailLimits
{
    public const int MaxWidth = 192, MaxHeight = 108, MaxBytes = 12 * 1024, MaxStreams = 32;
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(2); // o transmissor manda a cada 5 s ou mais; quem recebe aceita a partir de 2 s
}

/// <summary>
/// As miniaturas de quem está ao vivo, uma por transmissão (a mais recente). Tudo que chega de um amigo é tratado como não confiável: formato,
/// tamanho, hash, dimensões (conferidas ANTES de decodificar) e o ritmo; o que não passa é ignorado sem erro (o cartão fica sem miniatura).
/// Uso: <c>thumbs.Apply(msg); var bitmap = thumbs.Get(streamId); // nulo se ainda não chegou</c>
/// </summary>
public sealed partial class LiveThumbnails : IDisposable
{
    readonly object _gate = new();
    readonly Dictionary<string, Entry> _byStream = new();
    readonly Func<DateTime> _now;
    bool _disposed;

    sealed record Entry(Bitmap Image, string Hash, DateTime At);

    public LiveThumbnails(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    /// <summary>Disparado quando a miniatura de alguma transmissão mudou (em thread de fundo: quem usa passa para a tela).</summary>
    public event Action? Changed;

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,40}$")] private static partial Regex StreamIdPattern();

    /// <summary>A miniatura mais recente da transmissão, ou nulo. Quem pede não deve descartar o bitmap (ele é desta classe).</summary>
    public Bitmap? Get(string streamId)
    {
        lock (_gate) return _byStream.TryGetValue(streamId, out var e) ? e.Image : null;
    }

    /// <summary>Esquece as miniaturas de quem não está mais ao vivo (<paramref name="liveStreamIds"/> = quem está).</summary>
    public void KeepOnly(ISet<string> liveStreamIds)
    {
        lock (_gate) foreach (var id in _byStream.Keys.Where(id => !liveStreamIds.Contains(id)).ToList()) _byStream.Remove(id);
    }

    public int Count { get { lock (_gate) return _byStream.Count; } }

    /// <summary>Aplica uma mensagem <c>miniatura</c>. Devolve falso se foi ignorada (mal formada, grande demais, rápida demais...). Nunca lança.</summary>
    public bool Apply(JsonObject msg)
    {
        try { return TryApply(msg); }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        {
            Log.Info($"Miniatura recebida e ignorada ({e.GetType().Name}).");
            return false;
        }
    }

    /// <summary>Esquece a miniatura (a transmissão acabou). O bitmap NÃO é descartado na hora: a tela pode estar desenhando ele agora; o coletor libera.</summary>
    public void Forget(string streamId)
    {
        lock (_gate) _byStream.Remove(streamId);
    }

    bool TryApply(JsonObject msg)
    {
        if (msg["sala"]?.GetValue<string>() is not { } streamId || !StreamIdPattern().IsMatch(streamId)) return false;
        if (TooSoon(streamId)) return false;
        lock (_gate) if (_byStream.TryGetValue(streamId, out var previous) && previous.Hash == msg["hash"]?.GetValue<string>()) return false;
        var jpeg = JpegPayload.Read(msg, ThumbnailLimits.MaxBytes);
        if (jpeg == null) return false;
        var image = Decode(jpeg);
        if (image == null) return false;
        if (!Store(streamId, new Entry(image, AvatarMessage.HashOf(jpeg), _now()))) return false;
        Changed?.Invoke();
        return true;
    }

    bool TooSoon(string streamId)
    {
        lock (_gate) return _byStream.TryGetValue(streamId, out var e) && _now() - e.At < ThumbnailLimits.MinInterval;
    }

    /// <summary>Confere as dimensões pelo cabeçalho ANTES de decodificar os pixels, e devolve uma cópia que não depende do fluxo.</summary>
    static Bitmap? Decode(byte[] jpeg)
    {
        using (var header = Image.FromStream(new MemoryStream(jpeg), false, false))
            if (header.Width is < 16 or > ThumbnailLimits.MaxWidth || header.Height is < 9 or > ThumbnailLimits.MaxHeight) return null;
        using var source = Image.FromStream(new MemoryStream(jpeg), false, true);
        return new Bitmap(source);
    }

    /// <summary>Guarda a mais recente de cada transmissão e, passando do teto, tira a mais antiga. Os bitmaps trocados não são descartados na hora (a tela pode estar usando).</summary>
    bool Store(string streamId, Entry entry)
    {
        lock (_gate)
        {
            if (_disposed) { entry.Image.Dispose(); return false; }
            _byStream[streamId] = entry;
            while (_byStream.Count > ThumbnailLimits.MaxStreams) _byStream.Remove(_byStream.OrderBy(p => p.Value.At).First().Key);
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var e in _byStream.Values) e.Image.Dispose();
            _byStream.Clear();
        }
    }
}
