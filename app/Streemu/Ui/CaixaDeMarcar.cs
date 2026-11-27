using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// Caixa de marcar desenhada no tema do Streemu: quadradinho de cantos arredondados, cheio de roxo com o visto branco quando marcada e anel de destaque com o foco do teclado.
/// Continua sendo um CheckBox (Espaço marca, Tab passa, o leitor de tela lê o estado). O fundo é o do cartão onde ela está: a CheckBox do Windows pintava uma faixa
/// atrás do texto e a caixinha padrão não seguia o tema (pedido de 2026-11-27, aba da Mumu).
/// </summary>
public sealed class CaixaDeMarcar : CheckBox
{
    const int LadoDp = 18, TextoDp = 10;
    bool _sobre;

    public CaixaDeMarcar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        AutoSize = false;
        UseMnemonic = false;
        Cursor = Cursors.Hand;
    }

    /// <summary>A cor por trás (a superfície do cartão); a caixa não pinta faixa própria.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Fundo { get; set; } = Theme.SurfaceRaised;

    protected override void OnMouseEnter(EventArgs e) { _sobre = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _sobre = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Fundo);
        int lado = UiScale.Dp(this, LadoDp), raio = Math.Max(2, UiScale.Dp(this, 5)), anel = Math.Max(2, UiScale.Dp(this, 3));
        var caixa = new Rectangle(anel, (Height - lado) / 2, lado, lado);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Focused && ShowFocusCues)
        {
            var fora = Rectangle.Inflate(caixa, anel - 1, anel - 1);
            using var pathAnel = Theme.RoundedPath(fora, raio + anel - 1);
            using var penAnel = new Pen(Color.FromArgb(150, Theme.Accent), Math.Max(1.5f, anel * .7f));
            g.DrawPath(penAnel, pathAnel);
        }
        using (var path = Theme.RoundedPath(caixa, raio))
        {
            if (Checked)
            {
                using var cheio = new SolidBrush(!Enabled ? Theme.Neutral : _sobre ? Theme.AccentHover : Theme.Accent);
                g.FillPath(cheio, path);
                float t = lado / 18f;
                using var visto = new Pen(Theme.AccentText, Math.Max(1.6f, 2.2f * t)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(visto, [new PointF(caixa.X + 4.5f * t, caixa.Y + 9.5f * t), new PointF(caixa.X + 7.8f * t, caixa.Y + 12.6f * t), new PointF(caixa.X + 13.5f * t, caixa.Y + 5.8f * t)]);
            }
            else
            {
                using var dentro = new SolidBrush(Theme.SurfaceInput);
                g.FillPath(dentro, path);
                using var borda = new Pen(_sobre && Enabled ? Theme.Accent : Theme.Border, Math.Max(1f, UiScale.Dp(this, 15) / 10f));
                g.DrawPath(borda, path);
            }
        }
        g.SmoothingMode = SmoothingMode.None;
        int xt = caixa.Right + UiScale.Dp(this, TextoDp);
        var area = new Rectangle(xt, 0, Math.Max(1, Width - xt), Height);
        TextRenderer.DrawText(g, Text, Font, area, Enabled ? ForeColor : Theme.TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }
}
