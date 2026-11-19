using Streemu.Util;

namespace Streemu.Ui.Multistream;

/// <summary>Um cartão de quem está ao vivo: quem transmite, o jogo, a miniatura (nula até chegar) e se já está em tela.</summary>
public sealed record LiveCard(string FriendId, string Nick, string Game, string Quality, Bitmap? Thumbnail, bool Watching, Streemu.Ui.Avatars.AvatarImage? Avatar = null)
{
    /// <summary>Igual para a tela não redesenhar à toa: a miniatura conta pela referência (cada uma nova é um objeto novo).</summary>
    public bool SameAs(LiveCard other) => FriendId == other.FriendId && Nick == other.Nick && Game == other.Game && Quality == other.Quality
                                           && ReferenceEquals(Thumbnail, other.Thumbnail) && Watching == other.Watching && ReferenceEquals(Avatar, other.Avatar);
}

/// <summary>
/// Os cartões "Ao vivo agora" da home (spec 006): miniatura, nome, jogo e qualidade, com o botão Assistir. Todos têm o mesmo tamanho, as colunas
/// ocupam a largura inteira com o mesmo espaço entre elas e nas bordas, e cada texto cabe numa linha (com reticências), para o botão ficar sempre na
/// mesma posição. A altura se ajusta ao número de linhas. Quem já está em tela fica apagado.
/// Uso: <c>view.Picked += card =&gt; Assistir(card.FriendId); view.SetCards(cards);</c>
/// </summary>
public sealed class LiveCardsView : Control
{
    const int MinCardAt96 = UiMetrics.CardMinWidth, GapAt96 = UiMetrics.Gap, TextAt96 = UiMetrics.CardDetailsHeight, ButtonAt96 = UiMetrics.ActionHeight, InnerAt96 = UiMetrics.Gap;
    readonly UiFonts _fonts;
    int _focus;

    IReadOnlyList<LiveCard> _cards = Array.Empty<LiveCard>();
    int _hover = -1;

    public event Action<LiveCard>? Picked;

    public LiveCardsView()
    {
        _fonts = new UiFonts(this);
        TabStop = true; AccessibleName = "Lives disponíveis";
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        Visible = false;
    }

    int Dp(int at96) => UiScale.Dp(this, at96);
    int Gap => Dp(GapAt96);
    int Columns => Math.Max(1, Math.Min(_cards.Count, (Width + Gap) / (Dp(MinCardAt96) + Gap)));
    /// <summary>Em área larga (ultrawide) uma ou duas lives não esticam: o cartão fica de tamanho normal (~420 dp), à esquerda, como no desenho; em janela estreita continua enchendo a largura.</summary>
    int MaxCardWidth => _cards.Count <= 2 ? (Width >= Dp(900) ? 420 : UiMetrics.FeaturedCardMaxWidth) : UiMetrics.CardMaxWidth;
    int CardWidth => Math.Max(1, Math.Min(Dp(MaxCardWidth), (Width - Gap * (Columns - 1)) / Columns));
    /// <summary>A faixa de baixo do cartão (avatar, nome, jogo e o botão ao lado) tem uma altura só; antes o botão ia numa linha própria de largura inteira.</summary>
    const int DetailsAt96 = 68, StackedDetailsAt96 = 120, StackBelowAt96 = 440;
    /// <summary>Cartão estreito (janela pequena): o botão Assistir desce para uma linha só dele, na largura toda, como no desenho da tela menor; nos largos ele fica ao lado do nome.</summary>
    // Só empilha com UMA coluna estreita (janela pequena). Em tela larga/ultrawide os cartões ficam lado a lado e, mesmo estreitos, mantêm o botão ao lado do nome (desenho)
    bool Stacked => Columns == 1 && CardWidth < Dp(StackBelowAt96);
    int AvatarSize => Dp(CardWidth < Dp(330) ? 34 : 40);
    int CardHeight => CardWidth * 9 / 16 + Dp(Stacked ? StackedDetailsAt96 : DetailsAt96);
    int Rows => (_cards.Count + Columns - 1) / Columns;

    public void SetCards(IReadOnlyList<LiveCard> cards)
    {
        if (cards.Count == _cards.Count && cards.Zip(_cards).All(p => p.First.SameAs(p.Second))) return;
        _cards = cards;
        Resize();
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); if (_cards.Count > 0) Resize(); }

    /// <summary>A altura acompanha o número de linhas; sem ninguém ao vivo o cartão some.</summary>
    new void Resize()
    {
        Visible = _cards.Count > 0;
        int height = Rows * CardHeight + Math.Max(0, Rows - 1) * Gap;
        if (Height != height) Height = height;
        Invalidate();
    }

    Rectangle CardRect(int i)
    {
        int col = i % Columns, row = i / Columns;
        int usedColumns = Math.Min(Columns, _cards.Count - row * Columns);
        int offset = Width >= Dp(900) ? 0 : Math.Max(0, (Width - (usedColumns * CardWidth + (usedColumns - 1) * Gap)) / 2); // à esquerda em área larga, como no desenho
        return new Rectangle(offset + col * (CardWidth + Gap), row * (CardHeight + Gap), CardWidth, CardHeight);
    }

    int IndexAt(Point p) { for (int i = 0; i < _cards.Count; i++) if (CardRect(i).Contains(p)) return i; return -1; }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int now = IndexAt(e.Location);
        if (now == _hover) return;
        _hover = now;
        Cursor = now >= 0 && !_cards[now].Watching ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int i = IndexAt(e.Location);
        if (e.Button == MouseButtons.Left && i >= 0) { Focus(); _focus = i; Picked?.Invoke(_cards[i]); }
    }

    protected override bool IsInputKey(Keys keys) => (keys & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keys);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        int delta = e.KeyCode switch { Keys.Left => -1, Keys.Right => 1, Keys.Up => -Columns, Keys.Down => Columns, _ => 0 };
        if (delta != 0 && _cards.Count > 0)
        {
            _focus = Math.Clamp(_focus + delta, 0, _cards.Count - 1);
            AccessibleDescription = _cards[_focus].Nick + " · " + _cards[_focus].Game;
            if (Parent is ScrollableControl scroller) scroller.AutoScrollPosition = new Point(0, Math.Max(0, Top - scroller.AutoScrollPosition.Y + CardRect(_focus).Top));
            Invalidate(); e.Handled = e.SuppressKeyPress = true;
        }
        else if (e.KeyCode is Keys.Enter or Keys.Space && _cards.Count > 0) { Picked?.Invoke(_cards[Math.Clamp(_focus, 0, _cards.Count - 1)]); e.Handled = e.SuppressKeyPress = true; }
        base.OnKeyDown(e);
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void Dispose(bool disposing) { if (disposing) _fonts.Dispose(); base.Dispose(disposing); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Surface);
        for (int i = 0; i < _cards.Count; i++)
        {
            var rect = CardRect(i);
            if (!rect.IntersectsWith(e.ClipRectangle)) continue;
            DrawCard(e.Graphics, _cards[i], rect, i == _hover);
            if (Focused && _focus == i) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(rect, -Dp(3), -Dp(3)));
        }
    }

    void DrawCard(Graphics g, LiveCard card, Rectangle r, bool hover)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var shape = Theme.RoundedPath(r, Dp(UiMetrics.Radius));
        using (var body = new SolidBrush(Theme.SurfaceRaised)) g.FillPath(body, shape);
        g.SetClip(shape); // a miniatura e o véu seguem o canto arredondado do cartão
        var thumb = new Rectangle(r.X, r.Y, r.Width, r.Width * 9 / 16);
        if (card.Thumbnail is { } bmp)
        {
            using var margin = new SolidBrush(Theme.VideoMargin); g.FillRectangle(margin, thumb);
            double scale = Math.Min(thumb.Width / (double)bmp.Width, thumb.Height / (double)bmp.Height);
            int w = (int)(bmp.Width * scale), h = (int)(bmp.Height * scale);
            g.DrawImage(bmp, new Rectangle(thumb.X + (thumb.Width - w) / 2, thumb.Y + (thumb.Height - h) / 2, w, h));
        } else DrawPlaceholder(g, card, thumb);
        DrawLiveBadge(g, thumb);
        if (card.Watching) { using var veil = new SolidBrush(Theme.WatchedVeil); g.FillRectangle(veil, thumb); }
        g.ResetClip();
        DrawDetails(g, card, r, thumb, hover);
        using var border = new Pen(hover && !card.Watching ? Theme.Accent : Theme.Neutral, Math.Max(1f, Dp(2) * 0.75f));
        g.DrawPath(border, shape);
    }

    /// <summary>A faixa de baixo: avatar, nome em cima do jogo (ou "Você assiste") e o botão na ponta, tudo centrado na altura da faixa.</summary>
    void DrawDetails(Graphics g, LiveCard card, Rectangle r, Rectangle thumb, bool hover)
    {
        int inner = Dp(InnerAt96), band = Stacked ? Dp(StackedDetailsAt96 - 52) : r.Bottom - thumb.Bottom, avatarSize = AvatarSize;
        Streemu.Ui.Avatars.AvatarBadge.Draw(g, new Rectangle(r.X + inner, thumb.Bottom + (band - avatarSize) / 2, avatarSize, avatarSize), card.Avatar, card.Nick, 0);
        var button = ButtonRect(card, r, thumb);
        DrawButton(g, button, card.Watching, hover);
        int textX = r.X + inner + avatarSize + inner, textWidth = Math.Max(1, (Stacked ? r.Right : button.X) - inner - textX), lineHeight = Dp(20);
        int top = thumb.Bottom + (band - 2 * lineHeight) / 2;
        var color = card.Watching ? Theme.TextMuted : Theme.Text;
        TextRenderer.DrawText(g, card.Nick, _fonts.Bold, new Rectangle(textX, top, textWidth, lineHeight), color, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        string line2 = card.Watching ? "Assistindo" : card.Game;
        TextRenderer.DrawText(g, line2, Font, new Rectangle(textX, top + lineHeight, textWidth, lineHeight), Theme.TextMuted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    Rectangle ButtonRect(LiveCard card, Rectangle r, Rectangle thumb)
    {
        int inner = Dp(InnerAt96), height = Dp(ButtonAt96), band = r.Bottom - thumb.Bottom;
        if (Stacked) return new Rectangle(r.X + inner, r.Bottom - inner - height, r.Width - 2 * inner, height);
        int width = TextRenderer.MeasureText(card.Watching ? "Voltar à live" : "Assistir", Font).Width + Dp(36);
        return new Rectangle(r.Right - inner - width, thumb.Bottom + (band - height) / 2, width, height);
    }

    void DrawButton(Graphics g, Rectangle r, bool watching, bool hover)
    {
        using var fill = new SolidBrush(watching ? Theme.Neutral : hover ? Theme.AccentHover : Theme.Accent);
        using (var path = Theme.RoundedPath(r, Dp(7))) g.FillPath(fill, path);
        TextRenderer.DrawText(g, watching ? "Voltar à live" : "Assistir", Font, r, Theme.AccentText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    /// <summary>Sem miniatura ainda (ou versão antiga do amigo): fundo escuro com a inicial, como a faixa de pessoas.</summary>
    void DrawPlaceholder(Graphics g, LiveCard card, Rectangle thumb)
    {
        using var back = new SolidBrush(Theme.SurfaceInput);
        g.FillRectangle(back, thumb);
        var big = _fonts.Initial;
        string initial = card.Nick.Length > 0 ? card.Nick[..1].ToUpperInvariant() : "?";
        TextRenderer.DrawText(g, initial, big, thumb, Theme.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    void DrawLiveBadge(Graphics g, Rectangle thumb)
    {
        var badge = new Rectangle(thumb.X + Dp(12), thumb.Y + Dp(12), Dp(84), Dp(26));
        using var fill = new SolidBrush(Theme.Error);
        using (var path = Theme.RoundedPath(badge, badge.Height / 2)) g.FillPath(fill, path); // pílula, como no desenho
        var small = _fonts.Small;
        TextRenderer.DrawText(g, "AO VIVO", small, badge, Theme.AccentText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}
