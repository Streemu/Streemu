using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// A parte da tela de entrada que só muda com o tamanho da janela: fundo, brilho roxo, linhas do topo e do rodapé e o cartão com a sombra.
/// Fica numa imagem guardada, para o brilho (um degradê de tela cheia) não ser refeito a cada pintura.
/// </summary>
internal static class EntradaFundo
{
    public static Bitmap Criar(EntradaMedidas m)
    {
        var imagem = new Bitmap(Math.Max(1, m.Area.Width), Math.Max(1, m.Area.Height), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(imagem);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(ThemePalette.Dark.Backdrop);
        Brilho(g, m);
        using (var linha = new Pen(ThemePalette.Dark.Neutral))
        {
            g.DrawLine(linha, 0, m.LinhaCabecalho, m.Area.Width, m.LinhaCabecalho);
            g.DrawLine(linha, 0, m.LinhaRodape, m.Area.Width, m.LinhaRodape);
        }
        Sombra(g, m);
        Cartao(g, m);
        return imagem;
    }

    /// <summary>Elipse que clareia do centro para a borda (1 − t², medido no desenho), só entre as duas linhas.</summary>
    static void Brilho(Graphics g, EntradaMedidas m)
    {
        var area = new Rectangle(0, m.LinhaCabecalho + 1, m.Area.Width, m.LinhaRodape - m.LinhaCabecalho - 1);
        var estado = g.Save();
        g.SetClip(area);
        using var caminho = new GraphicsPath();
        caminho.AddEllipse(m.Brilho);
        using var pincel = new PathGradientBrush(caminho) { CenterColor = ThemePalette.Dark.LoginGlow, SurroundColors = [ThemePalette.Dark.Backdrop] };
        pincel.InterpolationColors = CurvaDoBrilho();
        g.FillPath(pincel, caminho);
        g.Restore(estado);
    }

    static ColorBlend CurvaDoBrilho()
    {
        const int passos = 10;
        var mistura = new ColorBlend(passos + 1) { Positions = new float[passos + 1], Colors = new Color[passos + 1] };
        for (int i = 0; i <= passos; i++)
        {
            float posicao = i / (float)passos, fracao = posicao * (2 - posicao); // posição 0 = borda, 1 = centro
            mistura.Positions[i] = posicao;
            mistura.Colors[i] = Misturar(ThemePalette.Dark.Backdrop, ThemePalette.Dark.LoginGlow, fracao);
        }
        return mistura;
    }

    static Color Misturar(Color de, Color para, float fracao) => Color.FromArgb(
        de.R + (int)((para.R - de.R) * fracao), de.G + (int)((para.G - de.G) * fracao), de.B + (int)((para.B - de.B) * fracao));

    static void Sombra(Graphics g, EntradaMedidas m)
    {
        int deslocamento = m.RaioCartao / 2;
        for (int i = 11; i >= 0; i--)
        {
            var caixa = Rectangle.Inflate(m.Cartao, i * 2, i * 2);
            caixa.Offset(0, deslocamento);
            using var caminho = Theme.RoundedPath(caixa, m.RaioCartao + i * 2);
            using var pincel = new SolidBrush(ThemePalette.Dark.LoginShadow(7));
            g.FillPath(pincel, caminho);
        }
    }

    static void Cartao(Graphics g, EntradaMedidas m)
    {
        var caixa = new Rectangle(m.Cartao.X, m.Cartao.Y, m.Cartao.Width - 1, m.Cartao.Height - 1);
        using var caminho = Theme.RoundedPath(caixa, m.RaioCartao);
        using var pincel = new SolidBrush(ThemePalette.Dark.LoginCard);
        using var borda = new Pen(ThemePalette.Dark.CardBorder);
        g.FillPath(pincel, caminho);
        g.DrawPath(borda, caminho);
    }
}
