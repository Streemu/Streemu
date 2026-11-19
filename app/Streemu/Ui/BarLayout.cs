namespace Streemu.Ui;

/// <summary>Onde ficam as três zonas da barra do player: faixa de pessoas, controles centrais e ações da direita.</summary>
public readonly record struct BarZones(int RosterX, int RosterWidth, int CenterX, int RightX, bool ShowQuality);

/// <summary>
/// Posição das zonas da barra, no estilo do Discord: faixa de pessoas à esquerda, controles (mudo, volume, qualidade) NO CENTRO
/// da barra e tela cheia/fechar à direita. A conta vive aqui, sem tela, para ser testada em toda largura.
/// Se o centro não cabe de forma simétrica, ele é empurrado para a esquerda (nunca por cima das ações); se ainda assim não cabe,
/// o selo de qualidade some antes de qualquer controle ser cortado.
/// Uso: <c>var z = BarLayout.Compute(width: 1280, centerFull: 380, centerShort: 230, right: 100, gap: 8);</c>
/// </summary>
public static class BarLayout
{
    public static BarZones Compute(int width, int centerFull, int centerShort, int right, int gap)
    {
        var full = Place(width, centerFull, right, gap, showQuality: true);
        return Fits(full, centerFull, gap) ? full : Place(width, centerShort, right, gap, showQuality: false);
    }

    static BarZones Place(int width, int center, int right, int gap, bool showQuality)
    {
        int rightX = Math.Max(0, width - right);
        int centerX = (width - center) / 2;                          // no meio da barra
        if (centerX + center + gap > rightX) centerX = rightX - gap - center; // não cabe simétrico: encosta antes das ações
        centerX = Math.Max(0, centerX);
        int rosterWidth = Math.Max(0, centerX - 2 * gap);
        return new BarZones(gap, rosterWidth, centerX, rightX, showQuality);
    }

    static bool Fits(BarZones z, int center, int gap) => z.CenterX >= gap && z.CenterX + center + gap <= z.RightX;

    /// <summary>
    /// Volume, qualidade e os botões num grupo só, no meio da barra; a faixa de pessoas à esquerda até o grupo. Com o centro no meio e as ações na
    /// ponta direita, numa barra larga os controles ficavam longe demais uns dos outros (pedido de 2026-11-16). Sem lugar no meio, o grupo encosta
    /// à direita; ainda sem lugar, o selo de qualidade some.
    /// </summary>
    /// <example><code>var z = BarLayout.Grouped(width: 1600, centerFull: 380, centerShort: 230, right: 180, gap: 8);</code></example>
    public static BarZones Grouped(int width, int centerFull, int centerShort, int right, int gap)
    {
        var full = PlaceGroup(width, centerFull, right, gap, showQuality: true);
        return full.CenterX >= gap && full.RightX + right <= width ? full : PlaceGroup(width, centerShort, right, gap, showQuality: false);
    }

    static BarZones PlaceGroup(int width, int center, int right, int gap, bool showQuality)
    {
        int group = center + 2 * gap + right;
        int x = Math.Max(0, Math.Min((width - group) / 2, width - group - gap));
        return new BarZones(gap, Math.Max(0, x - 2 * gap), x, x + center + 2 * gap, showQuality);
    }
}
