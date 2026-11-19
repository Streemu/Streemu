using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// O cartão das páginas de Configurações redesenhadas (Perfil, 2026-11-18): fundo elevado, borda fina e cantos de 14 dp, como as telas novas.
/// É um GroupBox de propósito: o <see cref="Theme"/> pinta o fundo de tudo que está dentro de um GroupBox com a cor elevada, então os rótulos e
/// interruptores filhos ficam do mesmo tom do cartão sem trabalho extra. Ocupa a largura que a página der e só a altura segue o conteúdo
/// (um único filho, colado no topo dentro da margem interna). Uso: <c>var cartao = new CartaoDeAjuste(); cartao.Controls.Add(conteudo);</c>
/// </summary>
public sealed class CartaoDeAjuste : GroupBox
{
    const int RaioAt96 = 14, PadAt96 = 20;
    bool _medindo;

    public CartaoDeAjuste()
    {
        AutoSize = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Padding = new Padding(PadAt96);
    }

    /// <summary>O GroupBox reserva no topo a faixa do título (mesmo vazio); este cartão não tem título, então a área útil é só a margem interna.</summary>
    public override Rectangle DisplayRectangle =>
        new(Padding.Left, Padding.Top, Math.Max(0, Width - Padding.Horizontal), Math.Max(0, Height - Padding.Vertical));

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); AplicarMargem(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); AplicarMargem(); }

    void AplicarMargem()
    {
        Padding = new Padding(UiScale.Dp(this, PadAt96));
        PerformLayout();
    }

    /// <summary>O filho fica colado no topo da área útil (dentro da margem) e o cartão tem a altura dele mais a margem.</summary>
    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (!_medindo && Controls.Count > 0)
        {
            _medindo = true;
            try
            {
                var filho = Controls[0];
                filho.Dock = DockStyle.Top;
                int altura = filho.GetPreferredSize(new Size(Math.Max(1, Width - Padding.Horizontal), 0)).Height + Padding.Vertical;
                if (Height != altura) Height = altura;
            }
            finally { _medindo = false; }
        }
        base.OnLayout(levent);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface); // os cantos mostram o fundo da página, não resto de pintura anterior
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var caminho = Theme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), UiScale.Dp(this, RaioAt96));
        using (var fundo = new SolidBrush(Theme.SurfaceRaised)) g.FillPath(fundo, caminho);
        using var borda = new Pen(Theme.CardBorder, Math.Max(1, UiScale.Dp(this, 1)));
        g.DrawPath(borda, caminho);
    }
}
