namespace Streemu.Media;

/// <summary>
/// Fila limitada do PCM. Na sobrecarga preserva o mais recente; o BufferedWaveProvider preservava o começo e descartava o som novo.
/// Na retomada de uma escrita bloqueada, o buraco vem ANTES do som recente, mantendo a duração do relógio sem tocar som velho no lugar do atual.
/// Não espera encher e não acrescenta latência no caminho saudável.
/// </summary>
sealed class LiveAudioBuffer
{
    readonly object _gate = new();
    readonly byte[] _data;
    readonly int _align;
    int _read, _count;
    long _dropped, _silence;
    public LiveAudioBuffer(int capacity, int align) { _align = align; _data = new byte[capacity / align * align]; }

    public (int Queued, long Dropped, long Silence) Counts { get { lock (_gate) return (_count, _dropped, _silence); } }

    public void Push(ReadOnlySpan<byte> source)
    {
        source = source[..(source.Length / _align * _align)];
        lock (_gate)
        {
            if (source.Length > _data.Length) { _dropped += source.Length - _data.Length; source = source[^_data.Length..]; }
            int discard = Math.Max(0, _count + source.Length - _data.Length);
            Skip(discard); _dropped += discard;
            int tail = (_read + _count) % _data.Length;
            int first = Math.Min(source.Length, _data.Length - tail);
            source[..first].CopyTo(_data.AsSpan(tail)); source[first..].CopyTo(_data);
            _count += source.Length;
        }
    }

    public int Read(Span<byte> destination, long behind)
    {
        lock (_gate)
        {
            destination.Clear();
            // Um atraso de 1 s com 0,5 s guardado: silêncio nos primeiros 0,5 s; som recente nos últimos 0,5 s.
            int silenceFirst = behind > _data.Length / 5 ? (int)Math.Min(destination.Length, Math.Max(0, behind - _count)) : 0;
            silenceFirst = silenceFirst / _align * _align;
            int n = Math.Min(destination.Length - silenceFirst, _count);
            int first = Math.Min(n, _data.Length - _read);
            _data.AsSpan(_read, first).CopyTo(destination[silenceFirst..]);
            _data.AsSpan(0, n - first).CopyTo(destination[(silenceFirst + first)..]);
            Skip(n); _silence += destination.Length - n;
            return n;
        }
    }

    void Skip(int bytes) { _read = (_read + bytes) % _data.Length; _count -= bytes; }
    public int KeepNewest(int bytes)
    {
        lock (_gate)
        {
            int discard = Math.Max(0, _count - bytes / _align * _align);
            Skip(discard); _dropped += discard; return discard;
        }
    }
    public void Clear() { lock (_gate) { _read = _count = 0; } }
}
