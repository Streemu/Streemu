using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// O "Para quem" da aba Enviar (desenho de 2026-11-18): um botão com o avatar, o apelido e a seta que abre a lista dos amigos online.
/// Pintado na hora com o tema de agora. Uso: <c>seletor.Mostrar(amigos, idEscolhido); seletor.Escolheu += id => ...;</c>
/// </summary>
public sealed class SeletorDeAmigo : Control
{
    const int AlturaAt96 = 50, PadAt96 = 14, AvatarAt96 = 30, MinLarguraAt96 = 190;
    readonly Font _fonte = UiTypography.Create(UiTypography.Body, FontStyle.Bold);
    IReadOnlyList<AmigoParaArquivo> _amigos = [];
    string? _escolhido;
    bool _hover;

    public event Action<string>? Escolheu;

    public SeletorDeAmigo()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true; Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.ComboBox; AccessibleName = "Para quem enviar";
    }

    public void Mostrar(IReadOnlyList<AmigoParaArquivo> amigos, string? escolhido) { _amigos = amigos; _escolhido = escolhido; Invalidate(); }

    AmigoParaArquivo? Atual => _amigos.FirstOrDefault(a => a.Id == _escolhido);

    public override Size GetPreferredSize(Size proposed)
    {
        int pad = UiScale.Dp(this, PadAt96);
        int texto = Atual is { } a ? TextRenderer.MeasureText(a.Apelido, _fonte).Width : TextRenderer.MeasureText("Escolher amigo", _fonte).Width;
        int largura = pad + UiScale.Dp(this, AvatarAt96) + pad + texto + pad + UiScale.Dp(this, 16) + pad;
        return new Size(Math.Max(UiScale.Dp(this, MinLarguraAt96), largura), UiScale.Dp(this, AlturaAt96));
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnClick(EventArgs e) { base.OnClick(e); AbrirLista(); }
    protected override bool IsInputKey(Keys key) => key is Keys.Enter or Keys.Space or Keys.Down || base.IsInputKey(key);
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode is Keys.Enter or Keys.Space or Keys.Down) { AbrirLista(); e.Handled = true; } }

    // A lista só se esconde ao fechar e é descartada aqui, quando o seletor morre: descartar no meio do fechamento derrubou o app (2026-11-18).
    ListaDeAmigosPopup? _lista;

    void AbrirLista()
    {
        if (_amigos.Count == 0 || FindForm() is not { } janela) return;
        if (_lista == null) { _lista = new ListaDeAmigosPopup(); _lista.Escolheu += id => Escolheu?.Invoke(id); }
        if (_lista.AcabouDeFechar) return; // o clique que fechou a lista por perder o foco não a reabre na mesma hora
        _lista.Mostrar(janela, RectangleToScreen(ClientRectangle), _amigos, _escolhido);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        DesenharFundo(g);
        int pad = UiScale.Dp(this, PadAt96), lado = UiScale.Dp(this, AvatarAt96);
        int x = pad;
        if (Atual is { } amigo) x = DesenharAvatar(g, amigo, x, lado) + pad;
        string texto = Atual?.Apelido ?? "Escolher amigo";
        int seta = UiScale.Dp(this, 16);
        var area = new Rectangle(x, 0, Math.Max(1, Width - x - pad - seta - pad), Height);
        TextRenderer.DrawText(g, texto, _fonte, area, Atual != null ? Theme.Text : Theme.TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        UiIcons.Draw(g, UiIcon.ChevronDown, new Rectangle(Width - pad - seta, (Height - seta) / 2, seta, seta), Theme.TextMuted);
    }

    void DesenharFundo(Graphics g)
    {
        using var path = Theme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), UiScale.Dp(this, 12));
        using (var fundo = new SolidBrush(_hover ? Theme.NeutralHover : Theme.Neutral)) g.FillPath(fundo, path);
        using var borda = new Pen(Focused ? Theme.Accent : Theme.Border, UiScale.Dp(this, Focused ? 2 : 1));
        g.DrawPath(borda, path);
    }

    int DesenharAvatar(Graphics g, AmigoParaArquivo amigo, int x, int lado)
    {
        var r = new Rectangle(x, (Height - lado) / 2, lado, lado);
        using (var cor = new SolidBrush(Theme.AvatarPalette[Math.Abs(amigo.Cor) % Theme.AvatarPalette.Length])) g.FillEllipse(cor, r);
        TextRenderer.DrawText(g, RosterStrip.Initial(amigo.Apelido), _fonte, r, Theme.AccentText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        int ponto = UiScale.Dp(this, 9);
        var online = new Rectangle(r.Right - ponto + UiScale.Dp(this, 2), r.Bottom - ponto + UiScale.Dp(this, 2), ponto, ponto);
        using (var anel = new SolidBrush(Theme.Neutral)) g.FillEllipse(anel, Rectangle.Inflate(online, 2, 2));
        using (var verde = new SolidBrush(Theme.Ok)) g.FillEllipse(verde, online);
        return r.Right;
    }

    protected override void Dispose(bool disposing) { if (disposing) { _lista?.Dispose(); _fonte.Dispose(); } base.Dispose(disposing); }
}
