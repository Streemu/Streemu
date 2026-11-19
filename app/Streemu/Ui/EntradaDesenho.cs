using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>Os desenhos e textos da tela de entrada por cima da imagem de fundo (<see cref="EntradaFundo"/>). Os textos dos botões, do campo e dos links são dos próprios controles.</summary>
internal static class EntradaDesenho
{
    const TextFormatFlags Base = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;

    public static void Tudo(Graphics g, EntradaMedidas m, EntradaFontes f, Bitmap? mumu, string dica, bool dicaEhErro)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Cabecalho(g, m, f);
        Rodape(g, m, f);
        if (mumu != null) Esquerda(g, m, f, mumu);
        Cartao(g, m, f, dica, dicaEhErro);
    }

    public static int Largura(string texto, Font fonte) =>
        TextRenderer.MeasureText(texto, fonte, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;

    static void Texto(Graphics g, string texto, Font fonte, Color cor, Rectangle caixa, TextFormatFlags extra = TextFormatFlags.Left) =>
        TextRenderer.DrawText(g, texto, fonte, caixa, cor, Base | extra);

    static Rectangle Linha(int x, int centroY, int largura, Font fonte) => new(x, centroY - fonte.Height / 2, largura, fonte.Height);

    static void Cabecalho(Graphics g, EntradaMedidas m, EntradaFontes f)
    {
        UiBrand.DrawWordmark(g, m.Marca, light: false);
        Frase(g, m, f);
    }

    static void Frase(Graphics g, EntradaMedidas m, EntradaFontes f)
    {
        const string texto = "Seu espaço para jogar junto";
        int largura = Largura(texto, f.Frase), direita = m.Area.Width - m.MargemDireita, esquerda = direita - largura;
        Texto(g, texto, f.Frase, ThemePalette.Dark.TextMuted, Linha(esquerda, m.CabecalhoCentroY, largura, f.Frase));
        int ponto = m.Marca.Height * 11 / 36;
        using var verde = new SolidBrush(ThemePalette.Dark.LoginOnline);
        g.FillEllipse(verde, esquerda - ponto - ponto * 6 / 11, m.CabecalhoCentroY - ponto / 2 - 1, ponto, ponto);
    }

    static void Rodape(Graphics g, EntradaMedidas m, EntradaFontes f)
    {
        const string texto = "Uma conta para você e seus amigos se acharem.";
        Texto(g, texto, f.Rodape, ThemePalette.Dark.TextMuted, Linha(m.MargemEsquerda, m.RodapeCentroY, Largura(texto, f.Rodape) + 4, f.Rodape));
    }

    static void Esquerda(Graphics g, EntradaMedidas m, EntradaFontes f, Bitmap mumu)
    {
        g.DrawImage(mumu, m.Mascote);
        // numa janela estreita a caixa fica mais curta que a frase: quebra em duas linhas (de cima), em vez de cortar com reticências
        TextRenderer.DrawText(g, "Chega aí! Junte-se à galera!", f.Heroi, m.Heroi, ThemePalette.Dark.Text, TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
    }

    static void Cartao(Graphics g, EntradaMedidas m, EntradaFontes f, string dica, bool dicaEhErro)
    {
        Texto(g, "Boas-vindas ao Streemu", f.Titulo, ThemePalette.Dark.Text, m.Titulo);
        if (m.ComCodigo) Texto(g, "Código de acesso", f.Rotulo, ThemePalette.Dark.Text, m.Rotulo);
        Texto(g, "Ainda não tem conta?", f.Fino, ThemePalette.Dark.TextMuted, m.Conta);
        if (dica.Length > 0)
            TextRenderer.DrawText(g, dica, f.Fino, m.Dica, dicaEhErro ? ThemePalette.Dark.Error : ThemePalette.Dark.TextMuted,
                TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.Top);
    }
}
