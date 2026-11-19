using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// O campo "000 000" da tela de entrada: caixa escura de cantos arredondados com borda de 2 px e o texto centralizado.
/// A borda clareia com o foco. A edição é de uma <see cref="TextBox"/> sem borda por dentro, para teclado, colar e leitor de tela continuarem normais.
/// </summary>
internal sealed class CampoDeCodigo : Control
{
    readonly TextBox _caixa = new()
    {
        BorderStyle = BorderStyle.None, TextAlign = HorizontalAlignment.Center, PlaceholderText = "000 000", MaxLength = 24,
        BackColor = ThemePalette.Dark.LoginField, ForeColor = ThemePalette.Dark.Text,
    };

    /// <summary>Enter dentro do campo.</summary>
    public event Action? Enviar;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Raio { get; set; } = 15;
    public string Valor => _caixa.Text;

    public CampoDeCodigo()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = ThemePalette.Dark.LoginCard;
        _caixa.AccessibleName = "Código de acesso";
        Controls.Add(_caixa);
        _caixa.GotFocus += (_, _) => Invalidate();
        _caixa.LostFocus += (_, _) => Invalidate();
        _caixa.KeyDown += AoTeclar;
    }

    public void Limpar() => _caixa.Clear();
    public void Focar() => _caixa.Focus();
    public void Fonte(Font fonte) { _caixa.Font = fonte; PerformLayout(); }

    void AoTeclar(object? origem, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter) return;
        e.SuppressKeyPress = true;
        Enviar?.Invoke();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int recuo = Height / 4;
        _caixa.SetBounds(recuo, (Height - _caixa.PreferredHeight) / 2, Math.Max(1, Width - 2 * recuo), _caixa.PreferredHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int borda = Math.Max(2, Height / 34);
        var caixa = new Rectangle(borda / 2, borda / 2, Width - borda, Height - borda);
        using var caminho = Theme.RoundedPath(caixa, Raio);
        using var fundo = new SolidBrush(ThemePalette.Dark.LoginField);
        using var contorno = new Pen(_caixa.Focused ? ThemePalette.Dark.AccentSoft : ThemePalette.Dark.CardBorder, borda);
        g.FillPath(fundo, caminho);
        g.DrawPath(contorno, caminho);
    }
}
