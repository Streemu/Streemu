using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// As peças da página Novidades que não são a lista nem a leitura (plano 027, D5): o botão "Procurar atualização" (o mesmo estilo dos botões do app), o
/// cabeçalho, o cartão "Sua versão" e a faixa "Você está na ...". Cada peça sabe dizer a altura que pede para uma largura; quem as posiciona é a
/// <see cref="NovidadesTela"/>. Todas pintam cores e fontes de <see cref="Theme"/> e <see cref="UiTypography"/> na hora e acompanham o Ctrl + / −.
/// </summary>
static class NovidadesPecas
{
    public const string TextoBotao = "Procurar atualização";
    public const TextFormatFlags Quebra = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;
    public const TextFormatFlags UmaLinha = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;

    /// <summary>Botão neutro, como os de ação do resto do app. Fica por cima de um fundo <see cref="Theme.SurfaceRaised"/> (o dono do botão tem esse fundo).</summary>
    public static Button CriarBotao()
    {
        var botao = new Button { Text = TextoBotao, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
        botao.AccessibleName = TextoBotao;
        Theme.StyleButton(botao, false);
        return botao;
    }

    /// <summary>Largura que o botão pede: o texto e uma folga de cada lado; nunca corta.</summary>
    public static int LarguraDoBotao(Button botao, double k) =>
        TextRenderer.MeasureText(TextoBotao, botao.Font, Size.Empty, UmaLinha & ~TextFormatFlags.EndEllipsis).Width + 2 * (int)Math.Round(16 * k);

    public static void PintarCartao(Graphics g, Rectangle area, int raio)
    {
        using (var fundo = new SolidBrush(Theme.Surface)) g.FillRectangle(fundo, area);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.RoundedPath(new Rectangle(area.X, area.Y, area.Width - 1, area.Height - 1), raio);
        using var raised = new SolidBrush(Theme.SurfaceRaised);
        g.FillPath(raised, path);
    }
}

/// <summary>Título "Novidades" e a linha de apoio, alinhados à esquerda do conjunto da página (que fica centrado).</summary>
public sealed class NovidadesCabecalho : Control
{
    Font? _fTitulo, _fApoio;
    float _fonteBase;
    double _k = 1;
    Rectangle _titulo, _apoio;
    int _x0, _largura = 1;
    bool _soTitulo;

    public NovidadesCabecalho()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
        TabStop = false;
        AccessibleName = NovidadesTela.TextoTitulo;
        AccessibleRole = AccessibleRole.StaticText;
        AccessibleDescription = NovidadesTela.TextoApoio;
        UiScale.WatchFont(this);
    }

    int D(int dp) => (int)Math.Round(dp * _k);

    void Medidas()
    {
        _k = UiScale.Dp(this, 10000) / 10000.0;
        if (_fTitulo != null && _fonteBase == Font.Size) return;
        _fTitulo?.Dispose(); _fApoio?.Dispose();
        _fonteBase = Font.Size;
        _fTitulo = UiTypography.Create(UiTypography.Title * (Font.Size / UiTypography.Body), FontStyle.Bold);
        _fApoio = (Font)Font.Clone();
    }

    /// <summary>Posiciona o texto (esquerda e largura do conjunto) e devolve a altura que o cabeçalho pede. Com <paramref name="soTitulo"/> some a linha de apoio (falta de altura).</summary>
    public int Definir(int x0, int larguraTexto, bool soTitulo)
    {
        Medidas();
        _x0 = x0; _largura = Math.Max(1, larguraTexto); _soTitulo = soTitulo;
        int pad = D(UiMetrics.PagePadding), gap = D(UiMetrics.Gap);
        int tituloH = TextRenderer.MeasureText(NovidadesTela.TextoTitulo, _fTitulo!, new Size(_largura, int.MaxValue), NovidadesPecas.Quebra).Height;
        _titulo = new Rectangle(x0, pad, _largura, tituloH);
        if (soTitulo) { _apoio = Rectangle.Empty; Invalidate(); return _titulo.Bottom + gap / 2; } // sem a linha de apoio o espaço de baixo também encolhe
        int apoioH = TextRenderer.MeasureText(NovidadesTela.TextoApoio, _fApoio!, new Size(_largura, int.MaxValue), NovidadesPecas.Quebra).Height;
        _apoio = new Rectangle(x0, _titulo.Bottom + gap / 2, _largura, apoioH);
        Invalidate();
        return _apoio.Bottom + gap;
    }

    /// <summary>Os retângulos dos textos (cliente). Para o teste.</summary>
    public IReadOnlyList<Rectangle> RetangulosDosTextos() => _apoio.IsEmpty ? [_titulo] : [_titulo, _apoio];

    protected override void OnPaint(PaintEventArgs e)
    {
        using (var fundo = new SolidBrush(Theme.Surface)) e.Graphics.FillRectangle(fundo, e.ClipRectangle);
        Medidas();
        if (_titulo.Width <= 0) return;
        TextRenderer.DrawText(e.Graphics, NovidadesTela.TextoTitulo, _fTitulo!, _titulo, Theme.Text, NovidadesPecas.Quebra | TextFormatFlags.Left);
        if (!_apoio.IsEmpty) TextRenderer.DrawText(e.Graphics, NovidadesTela.TextoApoio, _fApoio!, _apoio, Theme.TextMuted, NovidadesPecas.Quebra | TextFormatFlags.Left);
    }

    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); _fonteBase = 0; Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _fTitulo?.Dispose(); _fApoio?.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>O cartão "Sua versão" da coluna da direita: legenda, o rótulo da versão em mono e o botão "Procurar atualização".</summary>
public sealed class NovidadesCartao : Control
{
    public const string Legenda = "SUA VERSÃO";
    const int Pad = 18, AlturaBotao = 36;
    readonly Button _botao = NovidadesPecas.CriarBotao();
    Font? _fLegenda, _fMono;
    float _fonteBase;
    double _k = 1;
    string _rotulo = "";
    Rectangle _rLegenda, _rRotulo;

    public NovidadesCartao()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.SurfaceRaised; // o botão arredondado limpa o canto com a cor do pai
        TabStop = false;
        AccessibleName = "Sua versão";
        AccessibleRole = AccessibleRole.Grouping;
        Controls.Add(_botao);
        _botao.Click += (_, _) => BotaoClicado?.Invoke();
        UiScale.WatchFont(this);
    }

    public event Action? BotaoClicado;
    public Button Botao => _botao;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Rotulo
    {
        get => _rotulo;
        set { _rotulo = value ?? ""; AccessibleDescription = _rotulo; Organizar(); Invalidate(); }
    }

    int D(int dp) => (int)Math.Round(dp * _k);

    void Medidas()
    {
        _k = UiScale.Dp(this, 10000) / 10000.0;
        if (_fMono != null && _fonteBase == Font.Size) return;
        _fLegenda?.Dispose(); _fMono?.Dispose();
        _fonteBase = Font.Size;
        float r = Font.Size / UiTypography.Body;
        _fLegenda = UiTypography.Create(UiTypography.Note * r, FontStyle.Bold);
        _fMono = UiTypography.CreateMono(UiTypography.MonoBody * r);
    }

    (Rectangle Legenda, Rectangle Rotulo, Rectangle Botao, int Altura) Medir(int largura)
    {
        Medidas();
        int pad = D(Pad), gap = D(UiMetrics.Gap), inner = Math.Max(1, largura - 2 * pad);
        int legH = TextRenderer.MeasureText(Legenda, _fLegenda!, new Size(inner, int.MaxValue), NovidadesPecas.Quebra).Height;
        var legenda = new Rectangle(pad, pad, inner, legH);
        int rotH = _rotulo.Length == 0 ? 0 : TextRenderer.MeasureText(_rotulo, _fMono!, new Size(inner, int.MaxValue), NovidadesPecas.Quebra).Height;
        var rotulo = new Rectangle(pad, legenda.Bottom + gap / 2, inner, rotH);
        var botao = new Rectangle(pad, rotulo.Bottom + gap, inner, D(AlturaBotao));
        return (legenda, rotulo, botao, botao.Bottom + pad);
    }

    /// <summary>A altura que o cartão pede para esta largura, em pixels.</summary>
    public int AlturaPara(int largura) => Medir(largura).Altura;

    void Organizar()
    {
        if (Width <= 0) return;
        var m = Medir(Width);
        _rLegenda = m.Legenda; _rRotulo = m.Rotulo;
        if (_botao.Bounds != m.Botao) _botao.Bounds = m.Botao;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Medidas();
        NovidadesPecas.PintarCartao(e.Graphics, ClientRectangle, D(UiMetrics.Radius));
        TextRenderer.DrawText(e.Graphics, Legenda, _fLegenda!, _rLegenda, Theme.AccentSoft, NovidadesPecas.Quebra | TextFormatFlags.Left);
        if (_rRotulo.Height > 0) TextRenderer.DrawText(e.Graphics, _rotulo, _fMono!, _rRotulo, Theme.Text, NovidadesPecas.Quebra | TextFormatFlags.Left);
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Organizar(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); _fonteBase = 0; Organizar(); Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _fLegenda?.Dispose(); _fMono?.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>
/// A faixa "Versão 0.5.1" dos modos médio e estreito: sempre uma linha só, em <see cref="Theme.SurfaceRaised"/>, com o número da versão e o botão
/// "Procurar atualização" à direita. Se o texto e o botão não cabem lado a lado, o botão vira só o ícone de atualizar (com a dica), nunca desce de linha.
/// O rótulo completo ("0.5.1 · build de ...") fica na dica.
/// </summary>
public sealed class NovidadesFaixa : Control
{
    public const string Prefixo = "Versão ";
    const int PadX = 14, PadY = 10, AlturaBotao = 34;
    readonly Button _botao = NovidadesPecas.CriarBotao();
    readonly ToolTip _dica = new();
    string _rotulo = "";
    double _k = 1;
    Rectangle _rTexto;
    bool _soIcone;

    public NovidadesFaixa()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.SurfaceRaised;
        TabStop = false;
        AccessibleRole = AccessibleRole.Grouping;
        Controls.Add(_botao);
        _botao.Click += (_, _) => BotaoClicado?.Invoke();
        // o ícone é pintado por cima do botão, na cor de agora (o Theme.StyleButton já pintou o fundo)
        _botao.Paint += (_, e) =>
        {
            if (!_soIcone) return;
            int lado = Math.Max(1, (int)Math.Round(_botao.Height * .5));
            UiIcons.Draw(e.Graphics, UiIcon.Update, new Rectangle((_botao.Width - lado) / 2, (_botao.Height - lado) / 2, lado, lado), Theme.NeutralText);
        };
        UiScale.WatchFont(this);
    }

    public event Action? BotaoClicado;
    public Button Botao => _botao;

    /// <summary>O botão está só com o ícone (a faixa estreita demais para o texto dele). Para o teste.</summary>
    public bool SoIcone => _soIcone;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Rotulo
    {
        get => _rotulo;
        set
        {
            _rotulo = value ?? "";
            string dica = $"Você está na {_rotulo}. Clique para ver se tem versão nova.";
            AccessibleName = Texto; AccessibleDescription = dica;
            _dica.SetToolTip(this, dica); _dica.SetToolTip(_botao, dica);
            Organizar(); Invalidate();
        }
    }

    /// <summary>Só o número ("0.5.1"): o resto do rótulo (data do build, canal) vai para a dica.</summary>
    string Texto
    {
        get
        {
            string numero = _rotulo.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            return Prefixo + numero;
        }
    }

    int D(int dp) => (int)Math.Round(dp * _k);

    (Rectangle Texto, Rectangle Botao, bool SoIcone, int Altura) Medir(int largura)
    {
        _k = UiScale.Dp(this, 10000) / 10000.0;
        int padX = D(PadX), padY = D(PadY), gap = D(UiMetrics.Gap), inner = Math.Max(1, largura - 2 * padX);
        int bh = D(AlturaBotao), bwCheio = Math.Min(inner, NovidadesPecas.LarguraDoBotao(_botao, _k));
        var natural = TextRenderer.MeasureText(Texto, Font, Size.Empty, NovidadesPecas.UmaLinha & ~TextFormatFlags.EndEllipsis);
        bool soIcone = natural.Width + gap + bwCheio > inner;
        int bw = soIcone ? Math.Min(bh, inner) : bwCheio;
        var botao = new Rectangle(padX + inner - bw, padY, bw, bh);
        var texto = new Rectangle(padX, padY + (bh - natural.Height) / 2, Math.Max(1, inner - bw - gap), natural.Height);
        return (texto, botao, soIcone, bh + 2 * padY);
    }

    /// <summary>A altura que a faixa pede (a mesma para qualquer largura: é uma linha só), em pixels.</summary>
    public int AlturaPara(int largura) => Medir(largura).Altura;

    void Organizar()
    {
        if (Width <= 0) return;
        var m = Medir(Width);
        _rTexto = m.Texto; _soIcone = m.SoIcone;
        _botao.Text = m.SoIcone ? "" : NovidadesPecas.TextoBotao;
        if (_botao.Bounds != m.Botao) _botao.Bounds = m.Botao;
        _botao.Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        NovidadesPecas.PintarCartao(e.Graphics, ClientRectangle, UiScale.Dp(this, UiMetrics.Radius));
        if (_rTexto.Width > 0) TextRenderer.DrawText(e.Graphics, Texto, Font, _rTexto, Theme.Text, NovidadesPecas.UmaLinha | TextFormatFlags.Left);
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Organizar(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Organizar(); Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _dica.Dispose();
        base.Dispose(disposing);
    }
}
