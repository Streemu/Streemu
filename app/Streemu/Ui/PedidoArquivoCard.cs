using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// O que o card mostra. <paramref name="Alerta"/> = o aviso de cota do relay (ícone de atenção no lugar do avatar).
/// <paramref name="Pasta"/> nula = sem a linha "Salvar em".
/// </summary>
public sealed record CardDoPedido(string Pedido, string Letra, int Cor, bool Alerta, string Titulo, string Apoio,
    IReadOnlyList<(string Nome, string Tamanho)> Arquivos, string? Pasta, IReadOnlyList<AcaoDaLinha> Acoes)
{
    public bool Igual(CardDoPedido? o) => o != null && Pedido == o.Pedido && Titulo == o.Titulo && Apoio == o.Apoio && Pasta == o.Pasta
        && Alerta == o.Alerta && Arquivos.SequenceEqual(o.Arquivos) && Acoes.SequenceEqual(o.Acoes);
}

/// <summary>
/// O pedido de arquivo no Início (spec 020, mockup 2): quem manda, o que, quanto, onde vai ficar e os botões. Também o "pedir de novo"
/// de quem mandou e o aviso de cota do relay (D1), no mesmo desenho. Borda de destaque (card-selected-border): é algo esperando você.
/// Uso: <c>card.Mostrar(dados); card.Acao += (pedido, codigo) => ...;</c>
/// </summary>
public sealed class PedidoArquivoCard : Control
{
    const int RadiusAt96 = 8, PadAt96 = 16, AvatarAt96 = 40, MaxLinhas = 4;
    readonly List<Button> _botoes = new();
    readonly Button _trocar = new() { Text = "Trocar", AutoSize = true, AccessibleName = "Trocar a pasta de recebidos", Visible = false };
    readonly Font _titulo = UiTypography.Create(UiTypography.Heading, FontStyle.Bold), _apoio = UiTypography.Create(UiTypography.Caption), _mono = UiTypography.CreateMono(UiTypography.Note);
    CardDoPedido? _dados;

    public event Action<string, string>? Acao;
    public event Action? TrocarPasta;

    public PedidoArquivoCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
        Theme.StyleButton(_trocar, false);
        _trocar.Click += (_, _) => TrocarPasta?.Invoke();
        Controls.Add(_trocar);
    }

    public string? Pedido => _dados?.Pedido;

    public void Mostrar(CardDoPedido dados)
    {
        if (dados.Igual(_dados)) return;
        bool trocou = _dados == null || !_dados.Acoes.SequenceEqual(dados.Acoes);
        _dados = dados;
        AccessibleName = $"{dados.Titulo}. {dados.Apoio}";
        if (trocou) MontarBotoes(dados.Acoes);
        _trocar.Visible = dados.Pasta != null;
        Height = AlturaPara(dados);
        PerformLayout(); Invalidate();
    }

    void MontarBotoes(IReadOnlyList<AcaoDaLinha> acoes)
    {
        foreach (var b in _botoes) { Controls.Remove(b); b.Dispose(); }
        _botoes.Clear();
        foreach (var acao in acoes)
        {
            var b = new Button { Text = acao.Texto, AutoSize = true, AccessibleName = acao.Texto };
            Theme.StyleButton(b, acao.Principal);
            b.Click += (_, _) => { if (_dados != null) Acao?.Invoke(_dados.Pedido, acao.Codigo); };
            _botoes.Add(b); Controls.Add(b);
        }
    }

    int Linhas(CardDoPedido d) => Math.Min(d.Arquivos.Count, MaxLinhas) + (d.Arquivos.Count > MaxLinhas ? 1 : 0);
    int AlturaDaLinha => _mono.Height + UiScale.Dp(this, 8);
    // o apoio pode quebrar em 2 linhas (o aviso de cota quebra): a lista e os botões começam abaixo das duas, nunca por cima
    int TopoDaLista => UiScale.Dp(this, PadAt96) + Math.Max(UiScale.Dp(this, AvatarAt96), _titulo.Height + 2 * _apoio.Height + UiScale.Dp(this, 4)) + UiScale.Dp(this, 10);

    int AlturaPara(CardDoPedido d)
    {
        int pad = UiScale.Dp(this, PadAt96), botao = UiScale.Dp(this, UiMetrics.ActionHeight);
        int y = TopoDaLista + Linhas(d) * (AlturaDaLinha + UiScale.Dp(this, 4));
        if (d.Pasta != null) y += botao + UiScale.Dp(this, 6);
        return y + botao + pad;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_dados is not { } d) return;
        int pad = UiScale.Dp(this, PadAt96), x = pad;
        foreach (var b in _botoes) { b.Location = new Point(x, Height - pad - b.Height); x = b.Right + UiScale.Dp(this, 8); }
        int botao = _botoes.Count > 0 ? _botoes[0].Height : UiScale.Dp(this, UiMetrics.ActionHeight);
        if (d.Pasta != null) _trocar.Location = new Point(Width - pad - _trocar.Width, Height - pad - botao - UiScale.Dp(this, 6) - _trocar.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_dados is not { } d) return;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface); // os cantos arredondados mostram o fundo de trás
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int pad = UiScale.Dp(this, PadAt96), avatar = UiScale.Dp(this, AvatarAt96);
        using (var path = Theme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), UiScale.Dp(this, RadiusAt96)))
        {
            using (var fill = new SolidBrush(Theme.SurfaceRaised)) g.FillPath(fill, path);
            using (var pen = new Pen(d.Alerta ? Theme.Warn : Theme.CardSelectedBorder)) g.DrawPath(pen, path);
        }
        var icone = new Rectangle(pad, pad, avatar, avatar);
        if (d.Alerta) UiIcons.Draw(g, UiIcon.Warning, icone, Theme.Warn);
        else DesenharAvatar(g, d, icone);
        int left = icone.Right + UiScale.Dp(this, 12), largura = Math.Max(1, Width - left - pad);
        const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, d.Titulo, _titulo, new Rectangle(left, pad, largura, _titulo.Height + 2), Theme.Text, flags);
        TextRenderer.DrawText(g, d.Apoio, _apoio, new Rectangle(left, pad + _titulo.Height + UiScale.Dp(this, 2), largura, _apoio.Height * 2 + 2), Theme.TextMuted, flags | TextFormatFlags.WordBreak);
        DesenharArquivos(g, d, pad);
        if (d.Pasta != null) DesenharPasta(g, d.Pasta, pad);
    }

    void DesenharAvatar(Graphics g, CardDoPedido d, Rectangle r)
    {
        using (var b = new SolidBrush(Theme.AvatarPalette[Math.Abs(d.Cor) % Theme.AvatarPalette.Length])) g.FillEllipse(b, r);
        using var pen = new Pen(Theme.Ok, UiScale.Dp(this, 2));
        g.DrawEllipse(pen, Rectangle.Inflate(r, 1, 1));
        TextRenderer.DrawText(g, d.Letra, _titulo, r, Theme.AccentText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    void DesenharArquivos(Graphics g, CardDoPedido d, int pad)
    {
        int y = TopoDaLista, largura = Width - 2 * pad;
        var mostrar = d.Arquivos.Take(MaxLinhas).ToList();
        if (d.Arquivos.Count > MaxLinhas) mostrar.Add(($"e mais {d.Arquivos.Count - MaxLinhas} arquivo(s)", ""));
        foreach (var (nome, tamanho) in mostrar)
        {
            var linha = new Rectangle(pad, y, largura, AlturaDaLinha);
            using (var path = Theme.RoundedPath(linha, UiScale.Dp(this, UiMetrics.SmallRadius))) using (var b = new SolidBrush(Theme.SurfaceInput)) g.FillPath(b, path);
            var dentro = Rectangle.Inflate(linha, -UiScale.Dp(this, 10), 0);
            TextRenderer.DrawText(g, tamanho, _mono, dentro, Theme.TextMuted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            int w = TextRenderer.MeasureText(g, tamanho, _mono, Size.Empty, TextFormatFlags.NoPadding).Width + UiScale.Dp(this, 12);
            TextRenderer.DrawText(g, nome, _apoio, new Rectangle(dentro.X, dentro.Y, Math.Max(1, dentro.Width - w), dentro.Height), Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            y += AlturaDaLinha + UiScale.Dp(this, 4);
        }
    }

    void DesenharPasta(Graphics g, string pasta, int pad)
    {
        var r = new Rectangle(pad, _trocar.Top, Math.Max(1, _trocar.Left - pad - UiScale.Dp(this, 8)), _trocar.Height);
        TextRenderer.DrawText(g, $"Salvar em {pasta}", _apoio, r, Theme.TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _titulo.Dispose(); _apoio.Dispose(); _mono.Dispose(); }
        base.Dispose(disposing);
    }
}
