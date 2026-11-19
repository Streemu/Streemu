namespace Streemu.Ui.Multistream;

/// <summary>Onde fica a barra com as outras streams na ênfase (spec 006): à direita, à esquerda, embaixo, ou <c>Auto</c> (a que deixa a principal maior).</summary>
public enum BarSide { Auto, Right, Left, Below }

/// <summary>
/// Onde fica cada peça (vídeo) na janela de streams (spec 006), como conta pura: sem janela, sem ffplay, só retângulos.
/// Toda peça é 16:9, cabe na área e não se sobrepõe a outra; o vídeo de cada uma fica o MAIOR possível sem distorcer.
/// Dois modos: <see cref="Grid"/> (todas do mesmo tamanho, na disposição de colunas x linhas que deixa cada vídeo maior) e
/// <see cref="Emphasis"/> (uma peça grande e as outras menores ao lado ou embaixo, como no desenho do grupo).
/// O resultado vem na ordem das peças: o retângulo de índice i é da peça i.
/// Uso: <c>var rects = TileLayout.Grid(4, new Rectangle(0, 0, 1280, 720), gap: 8);</c>
/// </summary>
public static class TileLayout
{
    public const double Aspect = 16.0 / 9;
    /// <summary>Na ênfase, cada peça pequena tem pelo menos esta fração da largura da grande (senão a faixa das pequenas encolhe demais).</summary>
    const double MinSideToMain = 0.30;
    const int ScanStep = 4;

    /// <summary>A faixa das peças pequenas: ao lado (<c>Below</c> falso) ou embaixo, com a largura (ou altura) dela e o tamanho de cada uma.</summary>
    readonly record struct Split(bool Below, int Band, int MainWidth, Size SideTile);

    /// <summary>
    /// Todas as peças do mesmo tamanho. Escolhe o número de colunas que dá o maior vídeo (empate: mais colunas, janela larga fica lado a lado);
    /// a última linha incompleta fica centralizada, e o conjunto fica centralizado na área.
    /// </summary>
    public static IReadOnlyList<Rectangle> Grid(int count, Rectangle area, int gap)
    {
        if (count <= 0) return Array.Empty<Rectangle>();
        var (columns, size) = BestColumns(count, area.Size, gap);
        return PlaceGrid(count, columns, size, area, gap);
    }

    /// <summary>
    /// A peça <paramref name="emphasized"/> fica grande; as outras ficam menores, do mesmo tamanho entre si, numa barra à direita, à esquerda ou embaixo
    /// (<paramref name="bar"/>; <see cref="BarSide.Auto"/> escolhe a que deixa a grande maior), em mais de uma coluna ou linha se forem muitas. Escolhe a
    /// barra mais estreita em que as pequenas ainda são pelo menos 30% da grande, o que deixa a grande o maior possível. Com 1 peça é a grade; se a área
    /// não comporta a ênfase, cai para a grade.
    /// </summary>
    public static IReadOnlyList<Rectangle> Emphasis(int count, int emphasized, Rectangle area, int gap, BarSide bar = BarSide.Auto)
    {
        if (count <= 0) return Array.Empty<Rectangle>();
        if (count == 1) return Grid(1, area, gap);
        var split = ChooseSplit(count - 1, area, gap, bar);
        if (split == null) return Grid(count, area, gap);
        var rects = PlaceEmphasis(count, Math.Clamp(emphasized, 0, count - 1), split.Value, area, gap);
        return bar == BarSide.Left ? Mirror(rects, area) : rects;
    }

    static Split? ChooseSplit(int sideCount, Rectangle area, int gap, BarSide bar)
    {
        if (bar == BarSide.Below) return BestSplit(sideCount, area, gap, below: true);
        var side = BestSplit(sideCount, area, gap, below: false);
        if (bar != BarSide.Auto) return side;
        var below = BestSplit(sideCount, area, gap, below: true);
        return side == null ? below : below == null ? side : below.Value.MainWidth > side.Value.MainWidth ? below : side;
    }

    /// <summary>A barra à esquerda é a da direita espelhada na horizontal dentro da área.</summary>
    static IReadOnlyList<Rectangle> Mirror(IReadOnlyList<Rectangle> rects, Rectangle area) =>
        rects.Select(r => new Rectangle(2 * area.X + area.Width - r.X - r.Width, r.Y, r.Width, r.Height)).ToList();

    /// <summary>O maior vídeo 16:9 que cabe numa célula (largura inteira; a altura nunca passa da célula).</summary>
    static Size FitTile(double cellWidth, double cellHeight)
    {
        int height = Math.Max(0, (int)Math.Floor(cellHeight));
        int width = Math.Max(0, (int)Math.Floor(Math.Min(cellWidth, height * Aspect)));
        return new Size(width, (int)(width / Aspect));
    }

    static (int Columns, Size Tile) BestColumns(int count, Size area, int gap)
    {
        (int, Size) best = (1, Size.Empty);
        for (int columns = 1; columns <= count; columns++)
        {
            int rows = (count + columns - 1) / columns;
            var tile = FitTile((area.Width - gap * (columns - 1)) / (double)columns, (area.Height - gap * (rows - 1)) / (double)rows);
            if (tile.Width >= best.Item2.Width) best = (columns, tile); // >= : no empate fica o de mais colunas
        }
        return best;
    }

    static IReadOnlyList<Rectangle> PlaceGrid(int count, int columns, Size tile, Rectangle area, int gap)
    {
        int rows = (count + columns - 1) / columns;
        int top = area.Y + (area.Height - (rows * tile.Height + gap * (rows - 1))) / 2;
        var rects = new List<Rectangle>(count);
        for (int row = 0; row < rows; row++)
        {
            int inRow = row < rows - 1 ? columns : count - columns * (rows - 1);
            int left = area.X + (area.Width - (inRow * tile.Width + gap * (inRow - 1))) / 2;
            for (int i = 0; i < inRow; i++)
                rects.Add(new Rectangle(left + i * (tile.Width + gap), top + row * (tile.Height + gap), tile.Width, tile.Height));
        }
        return rects;
    }

    /// <summary>A menor faixa (ao lado ou embaixo) em que as pequenas passam de 30% da grande; nulo se nenhuma serve.</summary>
    static Split? BestSplit(int sideCount, Rectangle area, int gap, bool below)
    {
        int length = below ? area.Height : area.Width;
        for (int band = ScanStep; band < length - gap; band += ScanStep)
        {
            int mainWidth = MainWidthFor(area.Size, gap, band, below);
            var (_, tile) = BestColumns(sideCount, BandSize(area.Size, band, below), gap);
            if (tile.Width > 0 && mainWidth > 0 && tile.Width >= MinSideToMain * mainWidth) return new Split(below, band, mainWidth, tile);
        }
        return null;
    }

    static int MainWidthFor(Size area, int gap, int band, bool below) => below
        ? Math.Min(area.Width, (int)Math.Floor((area.Height - gap - band) * Aspect))
        : Math.Min(area.Width - gap - band, (int)Math.Floor(area.Height * Aspect));

    static Size BandSize(Size area, int band, bool below) => below ? new Size(area.Width, band) : new Size(band, area.Height);

    static IReadOnlyList<Rectangle> PlaceEmphasis(int count, int emphasized, Split split, Rectangle area, int gap)
    {
        var bandSize = BandSize(area.Size, split.Band, split.Below);
        var main = FitTile(split.MainWidth, split.Below ? area.Height - gap - split.Band : area.Height);
        var side = PlaceGrid(count - 1, BestColumns(count - 1, bandSize, gap).Columns, split.SideTile, new Rectangle(Point.Empty, bandSize), gap);
        var origin = split.Below ? BelowOrigin(main, side, area, gap) : BesideOrigin(main, side, area, gap);
        var rects = new Rectangle[count];
        rects[emphasized] = new Rectangle(origin.Main, main);
        int next = 0;
        for (int i = 0; i < count; i++)
            if (i != emphasized) rects[i] = new Rectangle(origin.Side(side[next++]), side[next - 1].Size);
        return rects;
    }

    /// <summary>Onde começam a grande e as pequenas quando elas vão ao lado: o conjunto todo centralizado na área.</summary>
    static (Point Main, Func<Rectangle, Point> Side) BesideOrigin(Size main, IReadOnlyList<Rectangle> side, Rectangle area, int gap)
    {
        int sideLeft = side.Min(r => r.Left), sideWidth = side.Max(r => r.Right) - sideLeft;
        int dx = area.X + (area.Width - (main.Width + gap + sideWidth)) / 2;
        return (new Point(dx, area.Y + (area.Height - main.Height) / 2), r => new Point(dx + main.Width + gap + (r.X - sideLeft), area.Y + r.Y));
    }

    /// <summary>Idem, com as pequenas embaixo da grande.</summary>
    static (Point Main, Func<Rectangle, Point> Side) BelowOrigin(Size main, IReadOnlyList<Rectangle> side, Rectangle area, int gap)
    {
        int sideTop = side.Min(r => r.Top), sideHeight = side.Max(r => r.Bottom) - sideTop;
        int dy = area.Y + (area.Height - (main.Height + gap + sideHeight)) / 2;
        return (new Point(area.X + (area.Width - main.Width) / 2, dy), r => new Point(area.X + r.X, dy + main.Height + gap + (r.Y - sideTop)));
    }
}
