using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// Oferta discreta no canto superior da janela: a live está abaixo de 60 FPS e dá para trocar para 30. Sem nada de urgente (borda comum do card,
/// ícone no tom de destaque, não de aviso): a pessoa aceita ou recusa e a live segue como está até lá. A regra de quando aparece é de
/// <c>Media/SugestaoDe30Fps.cs</c>. Uso: <c>card.Mostrar(36); card.Aceitou += ...; card.Recusou += ...;</c>
/// </summary>
public sealed class SugestaoFpsCard : Control
{
    const int LarguraAt96 = 320, PadAt96 = 14, IconeAt96 = 22, RaioAt96 = 8;
    readonly Button _aceitar = new() { Text = "Usar 30 FPS", AutoSize = true, AccessibleName = "Trocar a live para 30 FPS" };
    readonly Button _recusar = new() { Text = "Agora não", AutoSize = true, AccessibleName = "Manter a live em 60 FPS" };
    readonly Font _titulo = UiTypography.Create(UiTypography.Heading, FontStyle.Bold), _apoio = UiTypography.Create(UiTypography.Caption);
    readonly LinkLabel _desligar = new() { Text = "Não mostrar no jogo", AutoSize = true, Visible = false, AccessibleName = "Parar de avisar por cima do jogo" };
    int _quadros;
    bool _compacto, _linkNaLinha;
    string? _mensagem;

    public event Action? Aceitou;
    public event Action? Recusou;
    /// <summary>"Não mostrar no jogo": só existe no card que fica por cima do jogo (<see cref="ComOpcaoDeDesligar"/>).</summary>
    public event Action? Desligou;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ComOpcaoDeDesligar { get => _compacto; set { _compacto = value; _desligar.Visible = value && _mensagem == null; Size = TamanhoIdeal(); PerformLayout(); } }

    public SugestaoFpsCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
        Theme.StyleButton(_aceitar, true);
        Theme.StyleButton(_recusar, false);
        _aceitar.Click += (_, _) => Aceitou?.Invoke();
        _recusar.Click += (_, _) => Recusou?.Invoke();
        _desligar.Font = _apoio;
        _desligar.LinkColor = Theme.TextMuted; _desligar.ActiveLinkColor = Theme.Text; _desligar.LinkBehavior = LinkBehavior.HoverUnderline;
        _desligar.LinkClicked += (_, _) => Desligou?.Invoke();
        Controls.Add(_aceitar); Controls.Add(_recusar); Controls.Add(_desligar);
        Size = TamanhoIdeal();
    }

    string? _dica;

    // sobre o jogo o texto é curto (a pessoa está jogando): o número e o atalho; na janela, a explicação inteira
    string Apoio => _compacto
        ? $"Chegam ~{_quadros} quadros/s. " + (_dica ?? "Em 30 FPS fica mais estável.")
        : $"Chegam ~{_quadros} quadros por segundo. Em 30 FPS a imagem fica mais estável e o PC trabalha menos." + (_dica == null ? "" : " " + _dica);

    /// <summary>
    /// Atualiza o número mostrado e recalcula o tamanho (a escala da tela muda a altura). <paramref name="dica"/> é uma frase a mais
    /// (o atalho do teclado, quando o card está sobre o jogo e o mouse não está à mão).
    /// </summary>
    public void Mostrar(int quadrosPorSegundo, string? dica = null)
    {
        _quadros = quadrosPorSegundo; _dica = dica;
        SairDaMensagem();
        AccessibleName = $"Live abaixo de 60 FPS. {Apoio}";
        Size = TamanhoIdeal();
        PerformLayout(); Invalidate();
    }

    /// <summary>
    /// Troca o card por uma frase curta (retorno de "Não mostrar no jogo" ou de "Live em 30 FPS"): some os botões e o link, o ícone fica.
    /// <see cref="Mostrar"/> volta ao card normal.
    /// </summary>
    public void MostrarMensagem(string texto)
    {
        _mensagem = texto;
        _aceitar.Visible = _recusar.Visible = _desligar.Visible = false;
        AccessibleName = texto;
        Size = TamanhoIdeal();
        PerformLayout(); Invalidate();
    }

    void SairDaMensagem()
    {
        if (_mensagem == null) return;
        _mensagem = null;
        _aceitar.Visible = _recusar.Visible = true;
        _desligar.Visible = _compacto;
    }

    Size TamanhoIdeal()
    {
        int pad = UiScale.Dp(this, PadAt96), largura = UiScale.Dp(this, LarguraAt96);
        int texto = largura - 2 * pad - UiScale.Dp(this, IconeAt96) - UiScale.Dp(this, 10);
        if (_mensagem != null)
        {
            int frase = TextRenderer.MeasureText(_mensagem, _apoio, new Size(texto, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
            return new Size(largura, 2 * pad + Math.Max(frase, UiScale.Dp(this, IconeAt96)));
        }
        int apoio = TextRenderer.MeasureText(Apoio, _apoio, new Size(texto, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
        int botao = UiScale.Dp(this, UiMetrics.ActionHeight);
        // "Não mostrar no jogo" divide a linha dos botões quando cabe na largura; senão fica embaixo
        _linkNaLinha = false;
        int link = 0;
        if (_desligar.Visible)
        {
            int dosBotoes = _aceitar.GetPreferredSize(Size.Empty).Width + _recusar.GetPreferredSize(Size.Empty).Width + UiScale.Dp(this, 8);
            int dele = TextRenderer.MeasureText(_desligar.Text, _apoio, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
            _linkNaLinha = dosBotoes + UiScale.Dp(this, 12) + dele <= largura - 2 * pad;
            if (!_linkNaLinha) link = _apoio.Height + UiScale.Dp(this, 8);
        }
        return new Size(largura, pad + _titulo.Height + UiScale.Dp(this, 4) + apoio + UiScale.Dp(this, 10) + botao + link + pad);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int pad = UiScale.Dp(this, PadAt96), y = Height - pad - _aceitar.Height;
        if (_desligar.Visible && !_linkNaLinha)
        {
            _desligar.Location = new Point(pad, Height - pad - _desligar.Height);
            y = _desligar.Top - UiScale.Dp(this, 8) - _aceitar.Height;
        }
        _aceitar.Location = new Point(pad, y);
        _recusar.Location = new Point(_aceitar.Right + UiScale.Dp(this, 8), y);
        if (_desligar.Visible && _linkNaLinha) _desligar.Location = new Point(_recusar.Right + UiScale.Dp(this, 12), y + (_aceitar.Height - _desligar.Height) / 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface); // os cantos arredondados mostram o fundo de trás
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int pad = UiScale.Dp(this, PadAt96), icone = UiScale.Dp(this, IconeAt96);
        using (var path = Theme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), UiScale.Dp(this, RaioAt96)))
        {
            using (var fill = new SolidBrush(Theme.SurfaceRaised)) g.FillPath(fill, path);
            using (var pen = new Pen(Theme.CardBorder)) g.DrawPath(pen, path);
        }
        UiIcons.Draw(g, UiIcon.Activity, new Rectangle(pad, pad, icone, icone), Theme.Accent);
        int left = pad + icone + UiScale.Dp(this, 10), largura = Math.Max(1, Width - left - pad);
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        if (_mensagem != null)
        {
            TextRenderer.DrawText(g, _mensagem, _apoio, new Rectangle(left, pad, largura, Height - 2 * pad), Theme.Text, flags | TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter);
            return;
        }
        TextRenderer.DrawText(g, "Sua live está abaixo de 60 FPS", _titulo, new Rectangle(left, pad, largura, _titulo.Height + 2), Theme.Text, flags | TextFormatFlags.EndEllipsis);
        int topoApoio = pad + _titulo.Height + UiScale.Dp(this, 4);
        TextRenderer.DrawText(g, Apoio, _apoio, new Rectangle(left, topoApoio, largura, _aceitar.Top - topoApoio), Theme.TextMuted, flags | TextFormatFlags.WordBreak);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _desligar.Dispose(); _titulo.Dispose(); _apoio.Dispose(); }
        base.Dispose(disposing);
    }
}
