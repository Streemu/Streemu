using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

public enum TomDoSelo { Nenhum, Ok, Atencao, Neutro }

/// <summary>Um botão da linha: o texto, se é a ação principal (destaque) e o código que a tela recebe no clique.</summary>
public sealed record AcaoDaLinha(string Texto, bool Principal, string Codigo);

/// <summary>
/// O que uma linha mostra. <paramref name="Letra"/> nula = sem avatar (a lista de arquivos escolhidos). <paramref name="Pct"/> nulo = sem barra.
/// </summary>
public sealed record DadosDaLinha(string? Letra, int Cor, string Titulo, string Subtitulo, string? Selo, TomDoSelo Tom, int? Pct, IReadOnlyList<AcaoDaLinha> Acoes)
{
    public bool Igual(DadosDaLinha? o) => o != null && Letra == o.Letra && Cor == o.Cor && Titulo == o.Titulo && Subtitulo == o.Subtitulo
        && Selo == o.Selo && Tom == o.Tom && Pct == o.Pct && Acoes.SequenceEqual(o.Acoes);
}

/// <summary>
/// Uma linha das telas de arquivo (spec 020, mockup "Enviar" e "Histórico"): avatar com a inicial, título, linha de apoio, selo de estado,
/// barra de progresso e até três botões à direita. Cores e cantos do DESIGN.md (surface-raised, rounded.card).
/// Uso: <c>linha.Mostrar(dados); linha.Acao += codigo => ...;</c>
/// </summary>
public sealed class LinhaDeArquivo : Control
{
    const int RadiusAt96 = 8, PadAt96 = 14, AvatarAt96 = 34, AlturaAt96 = 58, AlturaComBarraAt96 = 66;
    readonly List<Button> _botoes = new();
    readonly Font _negrito, _apoio, _selo;
    DadosDaLinha? _dados;

    public event Action<string>? Acao;

    public LinhaDeArquivo()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        _negrito = UiTypography.Create(UiTypography.Body, FontStyle.Bold);
        _apoio = UiTypography.Create(UiTypography.Caption);
        _selo = UiTypography.Create(UiTypography.Caption, FontStyle.Bold);
        BackColor = Theme.Surface;
        Height = UiScale.Dp(this, AlturaAt96);
    }

    public void Mostrar(DadosDaLinha dados)
    {
        if (dados.Igual(_dados)) return;
        bool trocouBotoes = _dados == null || !_dados.Acoes.SequenceEqual(dados.Acoes);
        _dados = dados;
        Height = UiScale.Dp(this, dados.Pct != null ? AlturaComBarraAt96 : AlturaAt96);
        AccessibleName = $"{dados.Titulo}. {dados.Subtitulo}. {dados.Selo}";
        if (trocouBotoes) MontarBotoes(dados.Acoes);
        Invalidate();
    }

    void MontarBotoes(IReadOnlyList<AcaoDaLinha> acoes)
    {
        foreach (var b in _botoes) { Controls.Remove(b); b.Dispose(); }
        _botoes.Clear();
        foreach (var acao in acoes)
        {
            var b = new Button { Text = acao.Texto, AutoSize = true, AccessibleName = acao.Texto };
            Theme.StyleButton(b, acao.Principal);
            b.Click += (_, _) => Acao?.Invoke(acao.Codigo);
            _botoes.Add(b); Controls.Add(b);
        }
        PerformLayout();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int pad = UiScale.Dp(this, PadAt96), x = Width - pad;
        for (int i = _botoes.Count - 1; i >= 0; i--)
        {
            var b = _botoes[i];
            x -= b.Width;
            b.Location = new Point(x, (Height - b.Height) / 2);
            x -= UiScale.Dp(this, 6);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_dados is not { } d) return;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface); // os cantos arredondados mostram o fundo de trás, não restos do desenho anterior
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int pad = UiScale.Dp(this, PadAt96);
        using (var path = Theme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), UiScale.Dp(this, RadiusAt96)))
        using (var fill = new SolidBrush(Theme.SurfaceRaised)) g.FillPath(fill, path);
        int left = pad;
        if (d.Letra != null) left = DesenharAvatar(g, d, pad) + pad;
        int right = (_botoes.Count > 0 ? _botoes[0].Left : Width) - pad;
        if (d.Selo != null) right = DesenharSelo(g, d, right) - UiScale.Dp(this, 8);
        DesenharTextos(g, d, left, right);
        if (d.Pct is int pct) DesenharBarra(g, left, right, pct);
    }

    int DesenharAvatar(Graphics g, DadosDaLinha d, int pad)
    {
        int size = UiScale.Dp(this, AvatarAt96);
        var r = new Rectangle(pad, (Height - size) / 2, size, size);
        using (var b = new SolidBrush(Theme.AvatarPalette[Math.Abs(d.Cor) % Theme.AvatarPalette.Length])) g.FillEllipse(b, r);
        TextRenderer.DrawText(g, d.Letra, _negrito, r, Theme.AccentText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        return r.Right;
    }

    int DesenharSelo(Graphics g, DadosDaLinha d, int right)
    {
        var sz = TextRenderer.MeasureText(g, d.Selo, _selo, Size.Empty, TextFormatFlags.NoPadding);
        int h = sz.Height + UiScale.Dp(this, 8), w = sz.Width + UiScale.Dp(this, 20);
        var box = new Rectangle(right - w, (Height - h) / 2, w, h);
        using (var path = Theme.RoundedPath(box, h / 2)) using (var b = new SolidBrush(Theme.SurfaceInput)) g.FillPath(b, path);
        var cor = d.Tom switch { TomDoSelo.Ok => Theme.Ok, TomDoSelo.Atencao => Theme.Warn, _ => Theme.TextMuted };
        TextRenderer.DrawText(g, d.Selo, _selo, box, cor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        return box.Left;
    }

    void DesenharTextos(Graphics g, DadosDaLinha d, int left, int right)
    {
        int largura = Math.Max(1, right - left), alto = _negrito.Height + _apoio.Height + UiScale.Dp(this, 2);
        int top = d.Pct != null ? UiScale.Dp(this, 10) : (Height - alto) / 2;
        const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, d.Titulo, _negrito, new Rectangle(left, top, largura, _negrito.Height + 2), Theme.Text, flags);
        TextRenderer.DrawText(g, d.Subtitulo, _apoio, new Rectangle(left, top + _negrito.Height + UiScale.Dp(this, 2), largura, _apoio.Height + 2), Theme.TextMuted, flags);
    }

    void DesenharBarra(Graphics g, int left, int right, int pct)
    {
        int h = UiScale.Dp(this, 6), y = Height - UiScale.Dp(this, 14);
        var trilho = new Rectangle(left, y, Math.Max(1, right - left), h);
        using (var path = Theme.RoundedPath(trilho, h / 2)) using (var b = new SolidBrush(Theme.SurfaceInput)) g.FillPath(b, path);
        int cheio = trilho.Width * Math.Clamp(pct, 0, 100) / 100;
        if (cheio < h) return;
        using (var path = Theme.RoundedPath(new Rectangle(left, y, cheio, h), h / 2)) using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _negrito.Dispose(); _apoio.Dispose(); _selo.Dispose(); }
        base.Dispose(disposing);
    }
}
