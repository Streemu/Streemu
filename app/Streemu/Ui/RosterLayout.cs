namespace Streemu.Ui;

/// <summary>
/// Quantos avatares cabem na faixa e quantos viram o "+N". A faixa tem largura variável (o player pode ser pequeno) e
/// de 0 a 99 pessoas, então a conta vive aqui, sem tela, para ser testada.
/// Uso: <c>var (shown, hidden) = RosterLayout.Fit(count: 40, width: 600, avatar: 28, step: 20, chip: 44);</c>
/// </summary>
public static class RosterLayout
{
    /// <summary>
    /// Avatares se sobrepõem: cada um ocupa <paramref name="step"/> de largura e o último ocupa <paramref name="avatar"/> inteiro.
    /// Se todos cabem, nenhum fica escondido; senão sobra espaço para o chip "+N" e o resto vai para ele.
    /// </summary>
    public static (int Shown, int Hidden) Fit(int count, int width, int avatar, int step, int chip)
    {
        if (count <= 0) return (0, 0);
        if (WidthOf(count, avatar, step) <= width) return (count, 0);
        int shown = Math.Clamp((width - chip - (avatar - step)) / step, 0, count - 1);
        return (shown, count - shown);
    }

    /// <summary>Largura de <paramref name="shown"/> avatares sobrepostos.</summary>
    public static int WidthOf(int shown, int avatar, int step) => shown <= 0 ? 0 : (shown - 1) * step + avatar;
}
