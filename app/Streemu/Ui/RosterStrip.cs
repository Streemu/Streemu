using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Globalization;
using Streemu.Ui.Avatars;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// Faixa de participantes do player: círculos com a inicial do apelido, sobrepostos, com o selo LIVE em quem transmite.
/// Se não couber todo mundo, o resto vira um "+N" (clicável, abre a lista inteira). Cabem de 0 a 99 pessoas em qualquer
/// largura: a conta é de <see cref="RosterLayout"/>.
/// Uso: <c>strip.SetEntries(RosterEntry.FromWatchers(room, streamerId, watcherIds));</c>
/// </summary>
public sealed class RosterStrip : Control
{
    const int AvatarAt96 = 28, StepAt96 = 20, ChipAt96 = 44, RingAt96 = 2, GapAt96 = 4;
    static Color[] Palette => Theme.AvatarPalette;

    readonly ToolTip _tip = new() { InitialDelay = 250 };
    IReadOnlyList<RosterEntry> _entries = Array.Empty<RosterEntry>();
    readonly System.Windows.Forms.Timer _clock = new() { Interval = 100 };
    int _shown, _hidden, _hover = int.MinValue;
    /// <summary>Quem foi clicado para ficar animado (GIF parado por padrão; clicar de novo solta).</summary>
    string? _pinned;

    /// <summary>Clique no "+N".</summary>
    public event Action? OverflowClicked;

    public RosterStrip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _clock.Tick += (_, _) => { DropStaleHover(); InvalidateAnimating(); };
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Hidden => _hidden;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Shown => _shown;

    int Avatar => LogicalToDeviceUnits(AvatarAt96);
    int Step => LogicalToDeviceUnits(StepAt96);
    int Chip => LogicalToDeviceUnits(ChipAt96);
    int Gap => LogicalToDeviceUnits(GapAt96);
    int EdgeMargin => LogicalToDeviceUnits(RingAt96); // o anel do LIVE passa 2 dip da borda do avatar

    /// <summary>Retângulo do "+N" em coordenadas do controle (vazio se todos cabem).</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Rectangle ChipBounds => _hidden == 0 ? Rectangle.Empty
        : new Rectangle(EdgeMargin + RosterLayout.WidthOf(_shown, Avatar, Step) + Gap, (Height - Avatar) / 2, Chip, Avatar);

    public void SetEntries(IReadOnlyList<RosterEntry> entries)
    {
        if (_entries.SequenceEqual(entries)) return;
        _entries = entries;
        Relayout();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout();
    }

    void Relayout()
    {
        (_shown, _hidden) = RosterLayout.Fit(_entries.Count, Width - EdgeMargin, Avatar, Step, Chip + Gap);
        UpdateClock();
        Invalidate();
    }

    /// <summary>GIF só anima com o mouse em cima ou fixado por clique: o relógio só roda se há um assim entre os que cabem.</summary>
    void UpdateClock() => _clock.Enabled = Visible && Enumerable.Range(0, _shown).Any(i => _entries[i].Avatar is { Animated: true } && IsAnimating(i));

    bool IsAnimating(int index) => index == _hover || _entries[index].Nick == _pinned;

    /// <summary>Solta o hover se o mouse já não está na faixa (o evento "saiu" não vem quando a barra some com o mouse em cima).</summary>
    void DropStaleHover()
    {
        if (_hover < 0 || PointerProbe.IsOver(this, ClientRectangle)) return;
        _hover = int.MinValue;
        UpdateClock();
        Invalidate();
    }

    /// <summary>
    /// Repinta só a região dos avatares que estão animando (com folga para o anel e o selo LIVE), não a faixa inteira: o custo de
    /// um repaint na tela é por área, e só o GIF sob o mouse muda de quadro.
    /// </summary>
    void InvalidateAnimating()
    {
        var area = Rectangle.Empty;
        for (int i = 0; i < _shown; i++)
        {
            if (_entries[i].Avatar is not { Animated: true } || !IsAnimating(i)) continue;
            var r = Rectangle.Inflate(AvatarRect(i), LogicalToDeviceUnits(RingAt96 + 1), LogicalToDeviceUnits(RingAt96 + 8));
            area = area.IsEmpty ? r : Rectangle.Union(area, r);
        }
        if (!area.IsEmpty) Invalidate(area);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) { _hover = int.MinValue; _pinned = null; } // a barra sumiu: nada fica "preso" animando para a próxima vez
        UpdateClock();
    }

    /// <summary>Cor estável do apelido (não usa GetHashCode, que muda a cada execução).</summary>
    public static Color ColorOf(string nick)
    {
        int hash = nick.Aggregate(17, (h, c) => unchecked(h * 31 + char.ToLowerInvariant(c)));
        return Palette[(int)((uint)hash % (uint)Palette.Length)];
    }

    /// <summary>Primeira letra visível do apelido ("?" se vazio), inteira mesmo se for emoji.</summary>
    public static string Initial(string nick)
    {
        var parts = StringInfo.GetTextElementEnumerator(nick.Trim());
        return parts.MoveNext() ? parts.GetTextElement().ToUpperInvariant() : "?";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < _shown; i++) DrawAvatar(e.Graphics, _entries[i], AvatarRect(i), IsAnimating(i));
        if (_hidden > 0) DrawChip(e.Graphics);
    }

    Rectangle AvatarRect(int index) => new(EdgeMargin + index * Step, (Height - Avatar) / 2, Avatar, Avatar);

    void DrawAvatar(Graphics g, RosterEntry entry, Rectangle r, bool animate)
    {
        int ring = LogicalToDeviceUnits(RingAt96);
        using (var cut = new SolidBrush(BackColor)) g.FillEllipse(cut, Rectangle.Inflate(r, ring, ring)); // separa do avatar da esquerda
        AvatarBadge.Draw(g, r, entry.Avatar, entry.Nick, animate ? AvatarBadge.NowMs : 0);
        if (entry.Live) DrawLive(g, r);
    }

    void DrawLive(Graphics g, Rectangle avatar)
    {
        using (var pen = new Pen(Theme.Error, LogicalToDeviceUnits(2))) g.DrawEllipse(pen, avatar);
        int w = LogicalToDeviceUnits(28), h = LogicalToDeviceUnits(12);
        var pill = new Rectangle(avatar.X + (avatar.Width - w) / 2, avatar.Bottom - h / 2, w, h);
        using (var red = new SolidBrush(Theme.Error)) g.FillRectangle(red, pill);
        DrawCentered(g, "LIVE", 6.5f, pill, Theme.OnMedia);
    }

    void DrawChip(Graphics g)
    {
        var r = ChipBounds;
        using (var fill = new SolidBrush(_hover == int.MaxValue ? Theme.NeutralHover : Theme.Neutral)) g.FillRectangle(fill, r);
        DrawCentered(g, $"+{_hidden}", 9f, r, Theme.OnMedia);
    }

    static void DrawCentered(Graphics g, string text, float points, Rectangle bounds, Color color)
    {
        using var font = UiTypography.Create(points, FontStyle.Bold);
        const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, text, font, bounds, color, flags);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int hover = HitTest(e.Location);
        if (hover == _hover) return;
        _hover = hover;
        Cursor = hover == int.MaxValue || IsAnimatedAvatar(hover) ? Cursors.Hand : Cursors.Default;
        UpdateClock();
        _tip.SetToolTip(this, hover >= 0 && hover < _entries.Count ? TooltipOf(_entries[hover]) : "");
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = int.MinValue;
        UpdateClock();
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        int hit = HitTest(e.Location);
        if (hit == int.MaxValue) { OverflowClicked?.Invoke(); return; }
        if (!IsAnimatedAvatar(hit)) return;
        _pinned = _pinned == _entries[hit].Nick ? null : _entries[hit].Nick;
        UpdateClock();
        Invalidate();
    }

    bool IsAnimatedAvatar(int index) => index >= 0 && index < _shown && _entries[index].Avatar is { Animated: true };

    /// <summary>Índice do avatar sob o ponto, <see cref="int.MaxValue"/> para o "+N", <see cref="int.MinValue"/> para nada.</summary>
    int HitTest(Point p)
    {
        if (_hidden > 0 && ChipBounds.Contains(p)) return int.MaxValue;
        for (int i = _shown - 1; i >= 0; i--) if (AvatarRect(i).Contains(p)) return i;
        return int.MinValue;
    }

    static string TooltipOf(RosterEntry e) => e.Nick + (e.Live ? " · ao vivo" : "") + (e.IsMe ? " (você)" : "");

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tip.Dispose(); _clock.Dispose(); }
        base.Dispose(disposing);
    }
}
