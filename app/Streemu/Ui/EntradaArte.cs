using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>A Mumu da tela de entrada (<c>Arte/mumu-controle.png</c>, a mesma de docs/arte). A original é grande; cada tamanho de janela ganha uma cópia reduzida.</summary>
internal static class EntradaArte
{
    const string Nome = "mumu-controle.png";
    static Bitmap? _original;

    /// <summary>Cópia no tamanho pedido. Só na thread da tela; quem chama descarta a cópia.</summary>
    public static Bitmap Mumu(Size tamanho)
    {
        _original ??= UiBrand.LoadArt(Nome);
        var copia = new Bitmap(Math.Max(1, tamanho.Width), Math.Max(1, tamanho.Height), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(copia);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(_original, 0, 0, copia.Width, copia.Height);
        return copia;
    }
}
