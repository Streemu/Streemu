using System.Drawing.Drawing2D;
using Streemu.Ui.Avatars;
using Streemu.Util;

namespace Streemu.Ui.Multistream;

/// <summary>
/// A fileira de avatares de "Ao vivo agora" na home (modelo escolhido em 2026-11-09): cada pessoa ao vivo com o avatar em anel vermelho, o nome e o jogo,
/// todas do mesmo tamanho e com o mesmo espaço. Quem já está em tela ganha o anel de destaque. O GIF anda só sob o mouse, no mesmo ritmo da lista.
/// Uso: <c>row.Picked += card =&gt; Alternar(card.FriendId); row.SetCards(cards);</c>
/// </summary>
public sealed class LiveAvatarRow : Control
{
    const int CellAt96 = 116, CellHeightAt96 = 112, AvatarAt96 = 52;
    IReadOnlyList<LiveCard> _cards = Array.Empty<LiveCard>();
    int _hover = -1;
    readonly System.Windows.Forms.Timer _clock = new() { Interval = 100 };

    public event Action<LiveCard>? Picked;

    public LiveAvatarRow()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        Visible = false;
        _clock.Tick += (_, _) => Invalidate();
    }

    int Dp(int at96) => UiScale.Dp(this, at96);
    int Columns => Math.Max(1, Width / Dp(CellAt96));
    int Rows => (_cards.Count + Columns - 1) / Columns;

    public void SetCards(IReadOnlyList<LiveCard> cards)
    {
        if (cards.Count == _cards.Count && cards.Zip(_cards).All(p => p.First.SameAs(p.Second))) return;
        _cards = cards;
        Resize();
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); if (_cards.Count > 0) Resize(); }

    new void Resize()
    {
        Visible = _cards.Count > 0;
        int height = Rows * Dp(CellHeightAt96);
        if (Height != height) Height = height;
        Invalidate();
    }

    Rectangle CellRect(int i) => new((i % Columns) * Dp(CellAt96), (i / Columns) * Dp(CellHeightAt96), Dp(CellAt96), Dp(CellHeightAt96));

    int IndexAt(Point p) { for (int i = 0; i < _cards.Count; i++) if (CellRect(i).Contains(p)) return i; return -1; }

    void UpdateClock() => _clock.Enabled = _hover >= 0 && _hover < _cards.Count && _cards[_hover].Avatar is { Animated: true } && Visible;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int now = IndexAt(e.Location);
        if (now == _hover) return;
        _hover = now;
        Cursor = now >= 0 ? Cursors.Hand : Cursors.Default;
        UpdateClock();
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; UpdateClock(); Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int i = IndexAt(e.Location);
        if (e.Button == MouseButtons.Left && i >= 0) Picked?.Invoke(_cards[i]);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _clock.Stop(); _clock.Dispose(); }
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < _cards.Count; i++) DrawCell(g, _cards[i], CellRect(i), i == _hover);
    }

    void DrawCell(Graphics g, LiveCard card, Rectangle cell, bool hover)
    {
        var inner = Rectangle.Inflate(cell, -Dp(3), -Dp(3));
        if (hover) { using var path = Theme.RoundedPath(inner, Dp(10)); using var back = new SolidBrush(Theme.SurfaceRaised); g.FillPath(back, path); }
        int size = Dp(AvatarAt96);
        var av = new Rectangle(cell.X + (cell.Width - size) / 2, cell.Y + Dp(10), size, size);
        AvatarBadge.Draw(g, av, card.Avatar, card.Nick, hover && card.Avatar is { Animated: true } ? AvatarBadge.NowMs : 0);
        using (var ring = new Pen(card.Watching ? Theme.Accent : Theme.Error, Dp(3))) g.DrawEllipse(ring, Rectangle.Inflate(av, Dp(3), Dp(3)));
        var name = new Rectangle(cell.X + Dp(4), av.Bottom + Dp(10), cell.Width - Dp(8), Dp(20));
        using var bold = new Font(Font, FontStyle.Bold);
        const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        TextRenderer.DrawText(g, card.Nick, bold, name, card.Watching ? Theme.TextMuted : Theme.Text, flags);
        TextRenderer.DrawText(g, card.Watching ? "em tela" : card.Game, Font, new Rectangle(name.X, name.Bottom, name.Width, Dp(18)), Theme.TextMuted, flags);
    }
}
