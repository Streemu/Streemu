namespace Streemu.Ui.Avatars;

/// <summary>
/// Um avatar já pronto para desenhar: quadros quadrados de <see cref="AvatarLimits.RenderSize"/> px, recortados em círculo,
/// com os tempos de cada quadro (um só quadro se for imagem parada). É imutável: a tela e a rede podem lê-lo ao mesmo tempo.
/// Uso: <c>g.DrawImage(avatar.FrameAt(elapsedMs), rect);</c>
/// </summary>
public sealed class AvatarImage : IDisposable
{
    readonly Bitmap[] _frames;
    readonly int[] _endsAtMs;

    public AvatarImage(Bitmap[] frames, int[] delaysMs)
    {
        if (frames.Length == 0) throw new ArgumentException("um avatar precisa de pelo menos 1 quadro (recebi 0)", nameof(frames));
        if (frames.Length > 1 && delaysMs.Length != frames.Length)
            throw new ArgumentException($"{frames.Length} quadros pedem {frames.Length} tempos, recebi {delaysMs.Length}", nameof(delaysMs));
        _frames = frames;
        _endsAtMs = Accumulate(frames.Length > 1 ? delaysMs : Array.Empty<int>());
    }

    public int FrameCount => _frames.Length;
    /// <summary>Tempo de cada quadro em ms (vazio se parado).</summary>
    public IReadOnlyList<int> DelaysMs => _endsAtMs.Select((end, i) => end - (i == 0 ? 0 : _endsAtMs[i - 1])).ToArray();
    public bool Animated => _frames.Length > 1;
    /// <summary>Duração de uma volta inteira do GIF (0 se parado).</summary>
    public int LoopMs => _endsAtMs.Length == 0 ? 0 : _endsAtMs[^1];
    /// <summary>Memória que os quadros ocupam (32 bits por pixel), para o teto do <see cref="AvatarCache"/>.</summary>
    public long Bytes => (long)_frames.Length * AvatarLimits.RenderSize * AvatarLimits.RenderSize * 4;

    /// <summary>O quadro que deve aparecer depois de <paramref name="elapsedMs"/> ms (o GIF repete em laço).</summary>
    public Bitmap FrameAt(long elapsedMs)
    {
        if (!Animated || LoopMs <= 0) return _frames[0];
        long t = elapsedMs % LoopMs;
        int i = Array.FindIndex(_endsAtMs, end => t < end);
        return _frames[i < 0 ? 0 : i];
    }

    static int[] Accumulate(int[] delays)
    {
        var ends = new int[delays.Length];
        int sum = 0;
        for (int i = 0; i < delays.Length; i++) ends[i] = sum += delays[i];
        return ends;
    }

    public void Dispose()
    {
        foreach (var f in _frames) f.Dispose();
    }
}
