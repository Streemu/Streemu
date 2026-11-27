using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// O botão em pílula da tela Apoiar (plano 035, D7): um <see cref="Button"/> de verdade (Tab, Enter, Espaço, leitor de tela e dica) pintado à mão, com cantos de raio
/// metade da altura. O principal é roxo (<see cref="Theme.Accent"/>) com um coração à esquerda e o ícone de link externo à direita; o <see cref="Secundario"/> é neutro e só tem texto.
/// Tudo é medido em <see cref="U"/> (o mesmo da <see cref="ApoiarLayout"/>), então a fonte e os ícones acompanham a tela. Pinta o próprio fundo com a cor do balão
/// (<see cref="Theme.SurfaceRaised"/>), onde ele fica, para os cantos arredondados não deixarem quina.
/// </summary>
public sealed class BotaoPilula : Button
{
    float _u = 14;
    bool _secundario, _sobre;

    public BotaoPilula()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    /// <summary>A unidade da tela, em pixels; muda a fonte, os ícones e a folga.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float U { get => _u; set { if (Math.Abs(_u - value) < .01f) return; _u = value; Invalidate(); } }

    /// <summary>Neutro, só texto (o "Copiar link" do aviso de falha).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Secundario { get => _secundario; set { if (_secundario == value) return; _secundario = value; Invalidate(); } }

    protected override void OnMouseEnter(EventArgs e) { _sobre = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _sobre = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.SurfaceRaised);
        if (Width < 2 || Height < 2) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var area = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var pilula = Theme.RoundedPath(area, Math.Max(1, area.Height / 2)))
        {
            using var fundo = new SolidBrush(_secundario ? (_sobre ? Theme.Selection : Theme.SurfaceInput) : _sobre ? Theme.AccentHover : Theme.Accent);
            g.FillPath(fundo, pilula);
            if (_secundario) { using var borda = new Pen(Theme.CardBorder); g.DrawPath(borda, pilula); }
        }
        var cor = _secundario ? Theme.Text : Theme.AccentText;
        var fonte = ApoiarFontes.Obter(_u * (_secundario ? .88f : 1.02f), ApoiarEstilo.Semibold);
        var texto = TextRenderer.MeasureText(Text, fonte, Size.Empty, ApoiarTela.FormatoLinha);
        if (_secundario)
            TextRenderer.DrawText(g, Text, fonte, ClientRectangle, cor, ApoiarTela.FormatoLinha | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        else
        {
            int icone = (int)Math.Round(ApoiarLayout.BotaoIcone * _u), espaco = (int)Math.Round(ApoiarLayout.BotaoEspaco * _u);
            int total = icone + espaco + texto.Width + espaco + icone, x = (Width - total) / 2, topo = (Height - icone) / 2;
            UiIcons.Draw(g, UiIcon.Heart, new Rectangle(x, topo, icone, icone), cor);
            TextRenderer.DrawText(g, Text, fonte, new Rectangle(x + icone + espaco, 0, texto.Width, Height), cor, ApoiarTela.FormatoLinha | TextFormatFlags.VerticalCenter);
            UiIcons.Draw(g, UiIcon.ExternalLink, new Rectangle(x + total - icone, topo, icone, icone), cor);
        }
        if (Focused && ShowFocusCues)
        {
            using var anel = new Pen(Theme.Text, Math.Max(1.5f, _u / 9f));
            var dentro = Rectangle.Inflate(area, -(int)Math.Ceiling(anel.Width), -(int)Math.Ceiling(anel.Width));
            using var caminho = Theme.RoundedPath(dentro, Math.Max(1, dentro.Height / 2));
            g.DrawPath(anel, caminho);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) { }
}
