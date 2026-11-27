using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// O que os cartões da aba da Mumu (spec 031) têm em comum: fundo arredondado <see cref="Theme.SurfaceRaised"/> com borda fina, legenda em versalete na cor de destaque
/// e textos pintados direto no cartão. Os textos não são Labels: o Label pintava uma faixa de fundo atrás do texto (pedido de 2026-11-27).
/// </summary>
public abstract class MumuCartaoBase : Panel
{
    protected const int PadDp = 20;
    protected const TextFormatFlags Quebra = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
    protected const TextFormatFlags Linha = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
    Font? _fLegenda, _fPequena, _fForte;
    float _fonteBase;
    protected double K = 1;

    protected MumuCartaoBase(string legenda)
    {
        Legenda = legenda;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.SurfaceRaised;
        AccessibleRole = AccessibleRole.Grouping;
        AccessibleName = legenda;
        Theme.Changed += AoMudarTema;
    }

    public string Legenda { get; }

    protected int D(int dp) => (int)Math.Round(dp * K);

    /// <summary>Atualiza a escala e as fontes (acompanham o Ctrl + / − pela fonte da janela) antes de medir.</summary>
    protected void Preparar()
    {
        K = UiScale.Dp(this, 10000) / 10000.0;
        if (_fLegenda != null && _fonteBase == Font.Size) return;
        _fLegenda?.Dispose(); _fPequena?.Dispose(); _fForte?.Dispose();
        _fonteBase = Font.Size;
        float esc = Font.Size / UiTypography.Body;
        _fLegenda = UiTypography.Create(UiTypography.Note * esc, FontStyle.Bold);
        _fPequena = UiTypography.Create(UiTypography.Caption * esc);
        _fForte = UiTypography.CreateSemibold(UiTypography.Body * 1.08f * esc);
    }

    protected Font FonteLegenda => _fLegenda ?? Font;
    protected Font FontePequena => _fPequena ?? Font;
    protected Font FonteForte => _fForte ?? Font;

    protected static int Medir(string texto, Font fonte, int largura) =>
        texto.Length == 0 ? 0 : TextRenderer.MeasureText(texto, fonte, new Size(Math.Max(1, largura), int.MaxValue), Quebra).Height;

    /// <summary>A legenda no alto do cartão; devolve onde o conteúdo começa.</summary>
    protected int AlturaDaLegenda() => D(PadDp) + FonteLegenda.Height + D(UiMetrics.Gap) * 2 / 3;

    protected void PintarLegenda(Graphics g)
    {
        TextRenderer.DrawText(g, Legenda.ToUpperInvariant(), FonteLegenda, new Rectangle(D(PadDp), D(PadDp), Width - 2 * D(PadDp), FonteLegenda.Height), Theme.AccentSoft, Linha);
    }

    protected virtual void AoMudarTema()
    {
        BackColor = Theme.SurfaceRaised;
        Invalidate(true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var fundo = new SolidBrush(Parent?.BackColor ?? Theme.Surface)) g.FillRectangle(fundo, ClientRectangle);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), D(UiMetrics.Radius + 4));
        using (var raised = new SolidBrush(Theme.SurfaceRaised)) g.FillPath(raised, path);
        using (var borda = new Pen(Theme.CardBorder)) g.DrawPath(borda, path);
        g.SmoothingMode = SmoothingMode.None;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Theme.Changed -= AoMudarTema; _fLegenda?.Dispose(); _fPequena?.Dispose(); _fForte?.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Um cartão simples da aba da Mumu: legenda, texto, um botão opcional e uma nota pequena opcional (ex.: "Onde nos encontrar": o Instagram e o X em breve).
/// Só diz a altura que pede para uma largura (<see cref="AlturaPara"/>); quem posiciona é a <see cref="MumuTela"/>.
/// </summary>
public sealed class MumuCartao : MumuCartaoBase
{
    const int AlturaBotaoDp = 38;
    readonly Button _botao = new() { AutoSize = false, Visible = false };
    string _texto = "", _nota = "";
    Rectangle _areaTexto, _areaNota;

    public MumuCartao(string legenda) : base(legenda)
    {
        Controls.Add(_botao);
        Theme.StyleButton(_botao, true);
        _botao.Click += (_, _) => BotaoClicado?.Invoke();
    }

    public event Action? BotaoClicado;
    public Button Botao => _botao;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Texto { get => _texto; set { _texto = value; AtualizarNome(); Invalidate(); } }

    /// <summary>Linha pequena e apagada embaixo do botão (vazio: sem nota).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Nota { get => _nota; set { _nota = value; AtualizarNome(); Invalidate(); } }

    /// <summary>Texto do botão; vazio esconde o botão.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string TextoDoBotao
    {
        get => _botao.Text;
        set { _botao.Text = value; _botao.AccessibleName = value; _botao.Visible = value.Length > 0; }
    }

    void AtualizarNome() => AccessibleDescription = _nota.Length > 0 ? _texto + " " + _nota : _texto;

    protected override void AoMudarTema()
    {
        Theme.StyleButton(_botao, true);
        base.AoMudarTema();
    }

    /// <summary>A altura que o cartão pede para esta largura, em pixels; também posiciona o botão.</summary>
    public int AlturaPara(int largura)
    {
        Preparar();
        int pad = D(PadDp), gap = D(UiMetrics.Gap), inner = Math.Max(1, largura - 2 * pad);
        int y = AlturaDaLegenda();
        _areaTexto = new Rectangle(pad, y, inner, Medir(_texto, Font, inner));
        y = _areaTexto.Bottom;
        if (_botao.Visible)
        {
            int w = Math.Min(inner, TextRenderer.MeasureText(_botao.Text, _botao.Font).Width + D(48));
            _botao.Bounds = new Rectangle(pad, y + gap, w, D(AlturaBotaoDp));
            y = _botao.Bottom;
        }
        if (_nota.Length > 0) { _areaNota = new Rectangle(pad, y + gap * 2 / 3, inner, Medir(_nota, FontePequena, inner)); y = _areaNota.Bottom; }
        return y + pad;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        PintarLegenda(e.Graphics);
        TextRenderer.DrawText(e.Graphics, _texto, Font, _areaTexto, Theme.Text, Quebra);
        if (_nota.Length > 0) TextRenderer.DrawText(e.Graphics, _nota, FontePequena, _areaNota, Theme.TextMuted, Quebra);
    }
}
