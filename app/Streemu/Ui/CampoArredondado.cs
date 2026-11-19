using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// Um campo de texto de uma linha com cantos arredondados de verdade: o TextBox fica sem borda dentro, e este controle pinta o fundo, a borda inteira
/// (com o destaque roxo quando o campo tem o foco) e os cantos. O recorte por <c>Region</c> que o tema usava nos TextBox comia partes da borda
/// nativa (topo e lado direito quebrados, visto em 2026-11-18). Os eventos e o texto continuam no TextBox que quem chama já tem.
/// Uso: <c>var campo = new CampoArredondado(caixaDeTexto);</c> e coloque <c>campo</c> na tela no lugar da caixa.
/// </summary>
public sealed class CampoArredondado : Control
{
    const int PadHorizontalAt96 = 12, PadVerticalAt96 = 8, RaioAt96 = 10;
    readonly TextBox _caixa;
    readonly int _larguraAt96;

    /// <param name="larguraAt96">A largura do campo em dp (360 é a de sempre); a página limita ao que couber.</param>
    public CampoArredondado(TextBox caixa, int larguraAt96 = 360)
    {
        _larguraAt96 = larguraAt96;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint, true);
        _caixa = caixa;
        _caixa.BorderStyle = BorderStyle.None;
        _caixa.Margin = Padding.Empty;
        Controls.Add(_caixa);
        _caixa.GotFocus += (_, _) => Invalidate();
        _caixa.LostFocus += (_, _) => Invalidate();
        _caixa.FontChanged += (_, _) => PerformLayout();
        Width = larguraAt96; // antes do handle: janelas com tamanho automático medem o campo na criação
        PerformLayout();
    }

    /// <summary>A largura pedida do campo, na escala do monitor.</summary>
    public int LarguraPadrao => UiScale.Dp(this, _larguraAt96);

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Width = LarguraPadrao; PerformLayout(); }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        int padX = UiScale.Dp(this, PadHorizontalAt96), padY = UiScale.Dp(this, PadVerticalAt96);
        int altura = _caixa.PreferredHeight + padY * 2;
        if (Height != altura) Height = altura;
        _caixa.SetBounds(padX, padY, Math.Max(1, Width - padX * 2), _caixa.PreferredHeight);
        base.OnLayout(levent);
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _caixa.Focus(); } // clicar na margem também põe o cursor no texto

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.SurfaceRaised); // os cantos mostram o cartão de trás
        g.SmoothingMode = SmoothingMode.AntiAlias;
        _caixa.BackColor = Theme.SurfaceInput; // o fundo e o texto do TextBox acompanham o tema mesmo em janelas que o tema não pintou
        _caixa.ForeColor = Theme.Text;
        using var caminho = Theme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), UiScale.Dp(this, RaioAt96));
        using (var fundo = new SolidBrush(Theme.SurfaceInput)) g.FillPath(fundo, caminho);
        using var borda = new Pen(_caixa.Focused ? Theme.Accent : Theme.Border, Math.Max(1, UiScale.Dp(this, _caixa.Focused ? 2 : 1)));
        g.DrawPath(borda, caminho);
    }
}
