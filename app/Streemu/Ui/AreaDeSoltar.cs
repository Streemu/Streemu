using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// A área tracejada de "Arraste os arquivos para cá" da aba Enviar (spec 020, desenho de 2026-11-18): ícone, texto, botão "Escolher arquivos" e o
/// gato dormindo na gaveta à direita (a versão clara no tema claro). Tudo é pintado na hora com o tema de agora; só o botão é um controle de verdade.
/// Uso: <c>area.Escolher.Click += ...;</c> (o arrastar e soltar é ligado por quem hospeda, no próprio controle).
/// </summary>
public sealed class AreaDeSoltar : Control
{
    const int AlturaAt96 = 250, PadAt96 = 28, IconeAt96 = 56, EspacoAt96 = 16;
    readonly Font _titulo = UiTypography.Create(UiTypography.Heading, FontStyle.Bold), _apoio = UiTypography.Create(UiTypography.Body);
    readonly ArtView _gato = new(UiBrand.FileCabinetCat);

    public Button Escolher { get; } = new() { Text = "Escolher arquivos", AutoSize = true, AccessibleName = "Escolher arquivos" };

    public AreaDeSoltar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        AllowDrop = true;
        Controls.AddRange([_gato, Escolher]);
        foreach (Control filho in Controls) EncaminharSoltar(filho); // soltar em cima do gato ou do botão vale como soltar na área
        Theme.Changed += EstilizarBotao;
        // o Theme.Apply da janela devolve todo botão ao estilo neutro (fundo e depois texto): o botão volta ao roxo toda vez
        Escolher.BackColorChanged += (_, _) => { if (Escolher.BackColor != Theme.Accent) EstilizarBotao(); };
        Escolher.ForeColorChanged += (_, _) => { if (Escolher.ForeColor != Theme.AccentText) EstilizarBotao(); };
        EstilizarBotao();
    }

    public int AlturaPadrao => UiScale.Dp(this, AlturaAt96);

    void EstilizarBotao() => Theme.StyleButton(Escolher, true);

    void EncaminharSoltar(Control filho)
    {
        filho.AllowDrop = true;
        filho.DragEnter += (_, e) => OnDragEnter(e);
        filho.DragDrop += (_, e) => OnDragDrop(e);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int pad = UiScale.Dp(this, PadAt96), espaco = UiScale.Dp(this, EspacoAt96), icone = UiScale.Dp(this, IconeAt96);
        int textoX = pad + icone + espaco;
        var apoio = TextRenderer.MeasureText(ApoioTexto, _apoio);
        var titulo = TextRenderer.MeasureText(TituloTexto, _titulo);
        Escolher.Location = new Point(textoX + Math.Max(apoio.Width, titulo.Width) + 2 * espaco, (Height - Escolher.Height) / 2);
        int lado = Math.Max(1, Height * 78 / 100); // a arte (1024 px) já vem cortada rente ao gato: 78% da altura dá o mesmo tamanho visual de antes, com mais detalhe
        _gato.SetBounds(Width - pad - lado, (Height - lado) / 2, lado, lado);
        _gato.Visible = _gato.Left > Escolher.Right + espaco; // janela estreita: o gato cede o lugar ao botão em vez de cobri-lo
    }

    const string TituloTexto = "Arraste os arquivos para cá", ApoioTexto = "ou escolha no computador";

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        DesenharBorda(g);
        DesenharIcone(g);
        DesenharTextos(g);
    }

    void DesenharBorda(Graphics g)
    {
        using var pen = new Pen(Theme.Border, UiScale.Dp(this, 2)) { DashStyle = DashStyle.Dash };
        using var path = Theme.RoundedPath(new Rectangle(1, 1, Width - 3, Height - 3), UiScale.Dp(this, 14));
        g.DrawPath(pen, path);
    }

    void DesenharIcone(Graphics g)
    {
        int lado = UiScale.Dp(this, IconeAt96);
        var caixa = new Rectangle(UiScale.Dp(this, PadAt96), (Height - lado) / 2, lado, lado);
        using (var path = Theme.RoundedPath(caixa, UiScale.Dp(this, 14))) using (var fundo = new SolidBrush(Theme.Neutral)) g.FillPath(fundo, path);
        int seta = lado * 4 / 9;
        UiIcons.Draw(g, UiIcon.Upload, new Rectangle(caixa.X + (lado - seta) / 2, caixa.Y + (lado - seta) / 2, seta, seta), Theme.TextMuted);
    }

    void DesenharTextos(Graphics g)
    {
        int x = UiScale.Dp(this, PadAt96 + IconeAt96 + EspacoAt96), alto = _titulo.Height + _apoio.Height + UiScale.Dp(this, 4);
        int topo = (Height - alto) / 2;
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, TituloTexto, _titulo, new Point(x, topo), Theme.Text, flags);
        TextRenderer.DrawText(g, ApoioTexto, _apoio, new Point(x, topo + _titulo.Height + UiScale.Dp(this, 4)), Theme.TextMuted, flags);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Theme.Changed -= EstilizarBotao; _titulo.Dispose(); _apoio.Dispose(); }
        base.Dispose(disposing);
    }
}
