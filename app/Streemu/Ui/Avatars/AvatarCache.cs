namespace Streemu.Ui.Avatars;

/// <summary>
/// Guarda os avatares prontos por chave (o hash da imagem), com teto de memória: passou do teto, saem os usados há mais tempo.
/// A rede coloca e a tela lê ao mesmo tempo, por isso tudo passa por um trinco.
/// Uma imagem que sai do cache NÃO é descartada na hora (a tela pode estar desenhando ela neste instante); o coletor de streemu
/// libera quando ninguém mais a usa, e o teto conta só o que está guardado.
/// Uso: <c>cache.Put(hash, image); var img = cache.Get(hash);</c>
/// </summary>
public sealed class AvatarCache
{
    public const long DefaultMaxBytes = 30L * 1024 * 1024;

    readonly object _gate = new();
    readonly Dictionary<string, LinkedListNode<Entry>> _byKey = new();
    readonly LinkedList<Entry> _recentFirst = new();
    readonly long _maxBytes;
    long _bytes;

    sealed record Entry(string Key, AvatarImage Image);

    public AvatarCache(long maxBytes = DefaultMaxBytes)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "o teto de memória precisa ser maior que 0");
        _maxBytes = maxBytes;
    }

    public int Count { get { lock (_gate) return _byKey.Count; } }
    public long Bytes { get { lock (_gate) return _bytes; } }

    public bool Contains(string key)
    {
        lock (_gate) return _byKey.ContainsKey(key);
    }

    /// <summary>O avatar guardado (e o marca como usado agora), ou nulo.</summary>
    public AvatarImage? Get(string key)
    {
        lock (_gate)
        {
            if (!_byKey.TryGetValue(key, out var node)) return null;
            _recentFirst.Remove(node);
            _recentFirst.AddFirst(node);
            return node.Value.Image;
        }
    }

    public void Put(string key, AvatarImage image)
    {
        lock (_gate)
        {
            RemoveLocked(key);
            _byKey[key] = _recentFirst.AddFirst(new Entry(key, image));
            _bytes += image.Bytes;
            while (_bytes > _maxBytes && _recentFirst.Count > 1) RemoveLocked(_recentFirst.Last!.Value.Key); // nunca tira o que acabou de entrar
        }
    }

    public void Remove(string key)
    {
        lock (_gate) RemoveLocked(key);
    }

    void RemoveLocked(string key)
    {
        if (!_byKey.Remove(key, out var node)) return;
        _recentFirst.Remove(node);
        _bytes -= node.Value.Image.Bytes;
    }
}
