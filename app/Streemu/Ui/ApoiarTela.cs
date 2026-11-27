using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>Fontes da tela Apoiar por tamanho em pixels e estilo, guardadas para o desenho e o medidor do layout não criarem uma fonte por pintura. Só na thread da tela.</summary>
static class ApoiarFontes
{
    static readonly Dictionary<(int Decimos, ApoiarEstilo Estilo), Font> Guardadas = new();

    public static Font Obter(float pixels, ApoiarEstilo estilo)
    {
        var chave = ((int)Math.Round(pixels * 10), estilo);
        if (Guardadas.TryGetValue(chave, out var fonte)) return fonte;
        if (Guardadas.Count >= 48) { foreach (var velha in Guardadas.Values) velha.Dispose(); Guardadas.Clear(); } // a escala muda de vez em quando; poucos tamanhos vivem ao mesmo tempo
        return Guardadas[chave] = estilo switch
        {
            ApoiarEstilo.Semibold => UiTypography.CreateSemibold(chave.Item1 / 10f, GraphicsUnit.Pixel),
            ApoiarEstilo.Assinatura => UiTypography.CreateHandwriting(chave.Item1 / 10f, GraphicsUnit.Pixel),
            _ => UiTypography.Create(chave.Item1 / 10f, FontStyle.Regular, GraphicsUnit.Pixel),
        };
    }
}

/// <summary>
/// A tela "Apoiar" (plano 035): a Mumu e o Streemu pondo uma moeda no pote, um balão com o recado e o botão que abre a página de apoio no navegador. O app não mostra
/// valor nem forma de pagamento (o site faz isso). Um controle só, desenhado à mão (arte, balão com bico num caminho único, texto, coração, assinatura); o botão é a
/// exceção e é um <see cref="BotaoPilula"/> filho. As posições vêm da <see cref="ApoiarLayout"/>, que mede tudo numa unidade que acompanha a área: grande em 1080p,
/// maior em 1440p e 4K, e cabe inteira, sem rolagem, na janela mínima. Todas as cores vêm de <see cref="Theme"/>.
/// </summary>
public sealed class ApoiarTela : Control
{
    public const TextFormatFlags FormatoLinha = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.Left;
    const string Dica = "Abre streemu.com.br/apoie no navegador";

    readonly BotaoPilula _botao = new() { Text = ApoiarLayout.TextoBotao, AccessibleName = ApoiarLayout.TextoBotao };
    readonly BotaoPilula _copiar = new() { Text = ApoiarLayout.TextoCopiar, AccessibleName = "Copiar o endereço da página de apoio", Secundario = true, Visible = false };
    readonly ToolTip _dica = new();
    readonly System.Windows.Forms.Timer _refazArte = new() { Interval = 150 };
    ApoiarGeometria? _g;
    bool _falhou;
    Bitmap? _fonte, _arte;
    Size _arteTamanho;

    /// <summary>Abre o endereço no navegador; lança se não conseguir. O teste troca por um falso (nunca abre navegador de verdade).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<string> AbrirEndereco { get; set; } = url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    /// <summary>Copia um texto para a área de transferência; o teste troca por um falso.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<string> CopiarTexto { get; set; } = texto => Clipboard.SetText(texto);

    public ApoiarTela()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
        TabStop = false;
        AccessibleName = "Apoiar o Streemu";
        AccessibleDescription = ApoiarLayout.Recado;
        AccessibleRole = AccessibleRole.Pane;
        _botao.Click += (_, _) => Abrir();
        _copiar.Click += (_, _) => Copiar();
        _dica.SetToolTip(_botao, Dica);
        Controls.Add(_botao);
        Controls.Add(_copiar);
        _refazArte.Tick += (_, _) => { _refazArte.Stop(); LiberarArte(); Invalidate(); };
        Theme.Changed += AoMudarOTema;
        UiScale.WatchFont(this);
    }

    /// <summary>Se o último clique em "Apoiar no site" não conseguiu abrir o navegador (aparece o aviso com "Copiar link").</summary>
    public bool Falhou => _falhou;
    /// <summary>O botão principal (para o teste conferir o foco e clicar sem mouse).</summary>
    public BotaoPilula Botao => _botao;
    public BotaoPilula BotaoCopiar => _copiar;
    /// <summary>A última disposição calculada.</summary>
    public ApoiarGeometria? Geometria => _g;

    /// <summary>Tamanho de um texto de uma linha, o mesmo que o desenho usa (o medidor que o <see cref="ApoiarLayout"/> recebe).</summary>
    public static Size Medir(string texto, float pixels, ApoiarEstilo estilo) =>
        TextRenderer.MeasureText(texto, ApoiarFontes.Obter(pixels, estilo), Size.Empty, FormatoLinha);

    /// <summary>O clique em "Apoiar no site": abre a página; se o Windows não deixar, mostra o aviso embaixo do botão (D7).</summary>
    public void Abrir()
    {
        try { AbrirEndereco(UiBrand.UrlApoio); Log.Info("Apoiar: abriu a página de apoio no navegador."); DefinirFalha(false); }
        catch (Exception ex) { Log.Info("Apoiar: não consegui abrir o navegador: " + ex.Message); DefinirFalha(true); }
    }

    void Copiar()
    {
        try { CopiarTexto(ApoiarLayout.EnderecoCurto); }
        catch (Exception ex) { Log.Info("Apoiar: não copiou para a área de transferência: " + ex.Message); }
    }

    void DefinirFalha(bool falhou)
    {
        if (_falhou == falhou) return;
        _falhou = falhou;
        PerformLayout();
        Invalidate();
    }

    float Dp1 => UiScale.Dp(this, 1000) / 1000f;

    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); PerformLayout(); Invalidate(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); LiberarArte(); PerformLayout(); Invalidate(); }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
        var g = _g = ApoiarLayout.Calcular(ClientSize, Dp1, Medir, _falhou);
        _botao.U = _copiar.U = g.U;
        _botao.Bounds = g.Botao;
        _copiar.Visible = _falhou;
        if (_falhou) _copiar.Bounds = g.AvisoBotao;
        Invalidate();
    }

    void AoMudarOTema() { BackColor = Theme.Surface; Invalidate(true); }

    // ------------------------------------------------------------------ desenho

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_g == null) PerformLayout();
        var g = e.Graphics;
        var geo = _g;
        if (geo == null) return;
        DesenharArte(g, geo.Arte);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var caminho = CaminhoDoBalao(geo))
        {
            using var fundo = new SolidBrush(Theme.SurfaceRaised);
            using var borda = new Pen(Theme.CardBorder);
            g.FillPath(fundo, caminho);
            g.DrawPath(borda, caminho);
        }
        foreach (var linha in geo.Recado) Texto(g, linha, geo.TamRecado, ApoiarEstilo.Normal, Theme.Text);
        UiIcons.Draw(g, UiIcon.Heart, geo.Coracao, Theme.Accent); // no lugar do 💜 do mockup: o GDI desenha emoji em preto e branco
        Texto(g, geo.Assinatura, geo.TamAssinatura, ApoiarEstilo.Assinatura, Theme.AccentSoft);
        foreach (var linha in geo.Aviso) Texto(g, linha, geo.TamAviso, ApoiarEstilo.Normal, Theme.Text);
    }

    static void Texto(Graphics g, ApoiarLinha linha, float pixels, ApoiarEstilo estilo, Color cor)
    {
        if (linha.Texto.Length == 0) return;
        TextRenderer.DrawText(g, linha.Texto, ApoiarFontes.Obter(pixels, estilo), linha.Area, cor, FormatoLinha | TextFormatFlags.VerticalCenter);
    }

    /// <summary>O balão e o bico num caminho só, para a borda não ter emenda entre os dois.</summary>
    static GraphicsPath CaminhoDoBalao(ApoiarGeometria geo)
    {
        var b = new Rectangle(geo.Balao.X, geo.Balao.Y, geo.Balao.Width - 1, geo.Balao.Height - 1);
        int raio = Math.Max(2, (int)Math.Round(.6f * geo.U)), d = raio * 2;
        var bico = geo.Bico;
        var p = new GraphicsPath();
        p.AddArc(b.Left, b.Top, d, d, 180, 90);
        if (geo.LadoDoBico == ApoiarBico.Cima) { p.AddLine(bico.Left, b.Top, bico.Left + bico.Width / 2, bico.Top); p.AddLine(bico.Left + bico.Width / 2, bico.Top, bico.Right, b.Top); }
        p.AddArc(b.Right - d, b.Top, d, d, 270, 90);
        p.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90);
        p.AddArc(b.Left, b.Bottom - d, d, d, 90, 90);
        if (geo.LadoDoBico == ApoiarBico.Esquerda) { p.AddLine(b.Left, bico.Bottom, bico.Left, bico.Top + bico.Height / 2); p.AddLine(bico.Left, bico.Top + bico.Height / 2, b.Left, bico.Top); }
        p.CloseFigure();
        return p;
    }

    /// <summary>
    /// A arte no tamanho certo, em alta qualidade, guardada. Durante o arrasto da borda da janela o tamanho muda a cada quadro: aí desenha a cópia antiga esticada
    /// (rápido) e refaz em alta qualidade 150 ms depois da última mudança.
    /// </summary>
    void DesenharArte(Graphics g, Rectangle destino)
    {
        if (destino.Width <= 0 || destino.Height <= 0) return;
        if (_fonte == null)
        {
            try { _fonte = UiBrand.LoadArt("mumu-apoio.png"); }
            catch (Exception ex) { Log.Info("Apoiar: a arte não carregou: " + ex.Message); return; }
        }
        if (_arte == null || _arteTamanho != destino.Size)
        {
            if (_arte != null && _arteTamanho.Width > 0)
            {
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.DrawImage(_arte, destino);
                _refazArte.Stop(); _refazArte.Start();
                return;
            }
            _arte?.Dispose();
            _arte = new Bitmap(destino.Width, destino.Height, PixelFormat.Format32bppPArgb);
            using (var ag = Graphics.FromImage(_arte))
            {
                ag.InterpolationMode = InterpolationMode.HighQualityBicubic;
                ag.PixelOffsetMode = PixelOffsetMode.HighQuality;
                ag.CompositingQuality = CompositingQuality.HighQuality;
                ag.DrawImage(_fonte, new Rectangle(Point.Empty, destino.Size));
            }
            _arteTamanho = destino.Size;
        }
        g.DrawImage(_arte, destino);
    }

    void LiberarArte() { _arte?.Dispose(); _arte = null; _arteTamanho = Size.Empty; }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) return;
        _refazArte.Stop();
        LiberarArte();
        _fonte?.Dispose(); _fonte = null; // como o ArtView: a arte só fica na memória enquanto a tela aparece
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= AoMudarOTema;
            _refazArte.Dispose(); _dica.Dispose();
            LiberarArte(); _fonte?.Dispose(); _fonte = null;
        }
        base.Dispose(disposing);
    }
}
