using System.Diagnostics;
using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// A lista de amigos do "Para quem" (Enviar arquivo): um cartão de cantos arredondados que desce do seletor com um leve fade, com avatar, apelido,
/// destaque no item sob o mouse e uma marca no escolhido. Fecha ao perder o foco, com Esc ou ao escolher; setas e Enter funcionam.
/// É uma janela sem borda (como o <see cref="AtividadesPopup"/>) e NUNCA é descartada ao fechar: só se esconde, e quem a criou a descarta no fim
/// (descartar um menu ainda se fechando derrubou o app em 2026-11-18). Cores lidas na hora de pintar (tema claro e escuro).
/// Uso: <c>popup.Escolheu += id => ...; popup.Mostrar(dono, seletorNaTela, amigos, idEscolhido);</c>
/// </summary>
public sealed class ListaDeAmigosPopup : Form
{
    const int ItemAt96 = 44, PadAt96 = 6, RaioAt96 = 14, RaioItemAt96 = 10, AvatarAt96 = 30, DescidaAt96 = 8, EspacoAt96 = 6, LarguraMinAt96 = 220, MaxItens = 6;
    const int AnimacaoMs = 150, QuadroMs = 15;

    readonly Font _fonte = UiTypography.Create(UiTypography.Body), _fonteNegrito = UiTypography.Create(UiTypography.Body, FontStyle.Bold);
    readonly System.Windows.Forms.Timer _animacao = new() { Interval = QuadroMs };
    readonly Stopwatch _relogio = new();
    IReadOnlyList<AmigoParaArquivo> _amigos = [];
    string? _escolhido;
    int _destaque = -1, _rolagem, _topoFinal;
    long _fechouEm;

    public event Action<string>? Escolheu;

    public ListaDeAmigosPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        Text = "Escolher amigo";
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        _animacao.Tick += (_, _) => PassoDaAnimacao();
        Deactivate += (_, _) => Fechar();
    }

    /// <summary>Fechou agora há pouco por perder o foco: o clique no próprio seletor que a fechou não deve reabri-la.</summary>
    public bool AcabouDeFechar => Environment.TickCount64 - _fechouEm < 300;

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; } // WS_EX_TOOLWINDOW: fora do Alt+Tab
    }

    /// <summary>Abre embaixo do seletor (ou em cima, se não couber) e anima a entrada.</summary>
    public void Mostrar(Form dono, Rectangle seletorNaTela, IReadOnlyList<AmigoParaArquivo> amigos, string? escolhido)
    {
        _amigos = amigos; _escolhido = escolhido; _rolagem = 0;
        _destaque = amigos.ToList().FindIndex(a => a.Id == escolhido);
        var tamanho = new Size(Math.Max(seletorNaTela.Width, Dim(LarguraMinAt96)), Dim(PadAt96) * 2 + Dim(ItemAt96) * Math.Min(amigos.Count, MaxItens));
        var tela = Screen.FromRectangle(seletorNaTela).WorkingArea;
        int embaixo = seletorNaTela.Bottom + Dim(EspacoAt96);
        _topoFinal = embaixo + tamanho.Height <= tela.Bottom ? embaixo : Math.Max(tela.Top, seletorNaTela.Top - Dim(EspacoAt96) - tamanho.Height);
        int x = Math.Clamp(seletorNaTela.Left, tela.Left, Math.Max(tela.Left, tela.Right - tamanho.Width));
        Bounds = new Rectangle(x, _topoFinal - Dim(DescidaAt96), tamanho.Width, tamanho.Height);
        ArredondarCantos();
        Opacity = 0;
        if (!Visible) Show(dono);
        Activate();
        _relogio.Restart(); _animacao.Start();
    }

    int Dim(int valorAt96) => LogicalToDeviceUnits(valorAt96);

    void ArredondarCantos()
    {
        using var caminho = Theme.RoundedPath(new Rectangle(0, 0, Width, Height), Dim(RaioAt96));
        Region = new Region(caminho); // o Control descarta a região antiga ao receber a nova; descartá-la antes deixava uma inválida guardada (erro "Parameter is not valid", 2026-11-24)
    }

    void PassoDaAnimacao()
    {
        double t = Math.Min(1, _relogio.Elapsed.TotalMilliseconds / AnimacaoMs), suave = 1 - Math.Pow(1 - t, 3);
        Opacity = suave;
        Top = _topoFinal - (int)Math.Round(Dim(DescidaAt96) * (1 - suave));
        if (t >= 1) _animacao.Stop();
    }

    void Fechar()
    {
        if (!Visible) return;
        _animacao.Stop();
        _fechouEm = Environment.TickCount64;
        Hide();
    }

    void Escolher(int indice)
    {
        if (indice < 0 || indice >= _amigos.Count) return;
        string id = _amigos[indice].Id;
        Fechar();
        Escolheu?.Invoke(id);
    }

    Rectangle AreaDoItem(int indice) => new(Dim(PadAt96), Dim(PadAt96) + indice * Dim(ItemAt96) - _rolagem, Width - Dim(PadAt96) * 2, Dim(ItemAt96));

    int IndiceEm(Point ponto)
    {
        int indice = (ponto.Y - Dim(PadAt96) + _rolagem) / Dim(ItemAt96);
        return ponto.Y >= Dim(PadAt96) && ponto.Y < Height - Dim(PadAt96) && indice >= 0 && indice < _amigos.Count ? indice : -1;
    }

    int RolagemMaxima => Math.Max(0, _amigos.Count * Dim(ItemAt96) - (Height - Dim(PadAt96) * 2));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int indice = IndiceEm(e.Location);
        Cursor = indice >= 0 ? Cursors.Hand : Cursors.Default;
        if (indice != _destaque) { _destaque = indice; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_destaque != -1) { _destaque = -1; Invalidate(); } }
    protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); Escolher(IndiceEm(e.Location)); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _rolagem = Math.Clamp(_rolagem - Math.Sign(e.Delta) * Dim(ItemAt96), 0, RolagemMaxima);
        Invalidate();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape: Fechar(); return true;
            case Keys.Down: Mover(1); return true;
            case Keys.Up: Mover(-1); return true;
            case Keys.Enter: Escolher(_destaque); return true;
            default: return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    void Mover(int passo)
    {
        if (_amigos.Count == 0) return;
        _destaque = _destaque < 0 ? (passo > 0 ? 0 : _amigos.Count - 1) : (_destaque + passo + _amigos.Count) % _amigos.Count;
        int topo = _destaque * Dim(ItemAt96), vista = Height - Dim(PadAt96) * 2;
        if (topo < _rolagem) _rolagem = topo;
        else if (topo + Dim(ItemAt96) > _rolagem + vista) _rolagem = topo + Dim(ItemAt96) - vista;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.SurfaceRaised);
        g.SetClip(new Rectangle(0, Dim(PadAt96), Width, Height - Dim(PadAt96) * 2));
        for (int i = 0; i < _amigos.Count; i++)
        {
            var area = AreaDoItem(i);
            if (area.Bottom >= 0 && area.Top <= Height) DesenharItem(g, _amigos[i], area, i == _destaque);
        }
        g.ResetClip();
        using var contorno = Theme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), Dim(RaioAt96));
        using var caneta = new Pen(Theme.Border, Math.Max(1, Dim(1)));
        g.DrawPath(caneta, contorno);
    }

    void DesenharItem(Graphics g, AmigoParaArquivo amigo, Rectangle area, bool destacado)
    {
        bool escolhido = amigo.Id == _escolhido;
        if (destacado || escolhido)
        {
            using var caminho = Theme.RoundedPath(area, Dim(RaioItemAt96));
            using var fundo = new SolidBrush(destacado ? Theme.NeutralHover : Theme.OptionSelection); // no tema claro Neutral e CardSelected quase não se distinguem do fundo
            g.FillPath(fundo, caminho);
        }
        int lado = Dim(AvatarAt96), folga = Dim(10);
        var avatar = new Rectangle(area.X + folga, area.Y + (area.Height - lado) / 2, lado, lado);
        using (var cor = new SolidBrush(Theme.AvatarPalette[Math.Abs(amigo.Cor) % Theme.AvatarPalette.Length])) g.FillEllipse(cor, avatar);
        TextRenderer.DrawText(g, RosterStrip.Initial(amigo.Apelido), _fonteNegrito, avatar, Theme.AccentText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        int marca = escolhido ? Dim(18) : 0, textoX = avatar.Right + folga;
        var texto = new Rectangle(textoX, area.Y, Math.Max(1, area.Right - textoX - folga - (marca > 0 ? marca + folga : 0)), area.Height);
        TextRenderer.DrawText(g, amigo.Apelido, escolhido ? _fonteNegrito : _fonte, texto, Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        if (escolhido) UiIcons.Draw(g, UiIcon.Check, new Rectangle(area.Right - folga - marca, area.Y + (area.Height - marca) / 2, marca, marca), Theme.Accent);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _animacao.Dispose(); _fonte.Dispose(); _fonteNegrito.Dispose(); Region?.Dispose(); }
        base.Dispose(disposing);
    }
}
