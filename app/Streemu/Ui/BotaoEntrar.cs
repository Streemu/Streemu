using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// O botão roxo "Entrar no Streemu →" da tela de entrada. Continua sendo um <see cref="Button"/> (teclado, Enter e leitor de tela de graça);
/// só a pintura é nossa: cantos arredondados, texto um pouco à esquerda do meio e a seta no canto.
/// </summary>
internal sealed class BotaoEntrar : Button
{
    bool _sobre;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Raio { get; set; } = 12;

    public BotaoEntrar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = ThemePalette.Dark.LoginCard;
        Cursor = Cursors.Hand;
        Text = "Entrar no Streemu";
    }

    protected override void OnMouseEnter(EventArgs e) { _sobre = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _sobre = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(ThemePalette.Dark.LoginCard);
        var area = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var caminho = Theme.RoundedPath(area, Raio))
        using (var pincel = new SolidBrush(!Enabled ? ThemePalette.Dark.LoginDisabled : _sobre ? ThemePalette.Dark.AccentHover : ThemePalette.Dark.Accent))
            g.FillPath(pincel, caminho);
        int zonaSeta = Height * 24 / 63;
        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width - zonaSeta, Height), Theme.AccentText,
            TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        Seta(g);
        if (Focused && ShowFocusCues) Anel(g, area);
    }

    void Seta(Graphics g)
    {
        float unidade = Height / 63f, x = Width - 31 * unidade, y = Height / 2f;
        using var caneta = new Pen(Theme.AccentText, Math.Max(1.5f, 1.6f * unidade)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLine(caneta, x - 8 * unidade, y, x + 8 * unidade, y);
        g.DrawLines(caneta, [new PointF(x + 3 * unidade, y - 5 * unidade), new PointF(x + 8 * unidade, y), new PointF(x + 3 * unidade, y + 5 * unidade)]);
    }

    void Anel(Graphics g, Rectangle area)
    {
        area.Inflate(-2, -2);
        using var caminho = Theme.RoundedPath(area, Raio);
        using var caneta = new Pen(ThemePalette.Dark.AccentSoft, 2);
        g.DrawPath(caneta, caminho);
    }
}
