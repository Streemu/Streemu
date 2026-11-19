using System.Drawing.Drawing2D;
using System.Text.Json.Nodes;
using Streemu.Ui.Avatars;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// Cartão de uma sessão na lista do Cinema (mockup "Cinema Sessões", 2026-11-15): a capa em cima; embaixo o nome do filme, o anfitrião
/// com o estado à direita e, depois de um traço, quem está assistindo e o "Entrar".
/// </summary>
sealed class CinemaSessionCard : Control
{
    const int Pad = 18, DetailsHeight = 168, AvatarSize = 30, HostAvatar = 28;
    readonly Button _enter = new() { Text = "Entrar" };
    readonly Func<string, AvatarImage?> _avatarOf;
    readonly Func<JsonObject, Image?> _coverOf;
    Font _title = null!, _body = null!, _initials = null!;
    JsonObject _announcement = new();
    public event Action<JsonObject>? Picked;

    public CinemaSessionCard(Func<string, AvatarImage?> avatarOf, Func<JsonObject, Image?> coverOf)
    {
        _avatarOf = avatarOf;
        _coverOf = coverOf;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.SurfaceRaised; ForeColor = Theme.Text;
        Theme.StyleButton(_enter, true);
        Controls.Add(_enter);
        _enter.Click += (_, _) => Picked?.Invoke((JsonObject)_announcement.DeepClone());
        MakeFonts();
        FontChanged += (_, _) => { ReleaseFonts(); MakeFonts(); LayoutButton(); };
    }

    /// <summary>Altura do cartão para a largura dada: a capa acompanha a largura (como no mockup) e o resto é fixo.</summary>
    public static int HeightFor(Control owner, int width) => PosterHeight(owner, width) + UiScale.Dp(owner, DetailsHeight);

    static int PosterHeight(Control owner, int width) => Math.Clamp(width * 42 / 100, UiScale.Dp(owner, 110), UiScale.Dp(owner, 280));

    public void SetAnnouncement(JsonObject a)
    {
        _announcement = a;
        _enter.AccessibleName = $"Entrar em {Name()}, com {Host()}";
        Invalidate();
    }

    string Name() => (string?)_announcement["nome"] ?? "Filme";
    string Host() => (string?)_announcement["anfitriao"] ?? "Um amigo";

    /// <summary>Quem aparece embaixo: o anfitrião e quem já entrou (campo "quem" do anúncio; anfitrião antigo não manda, e aí só ele aparece).</summary>
    List<string> Watching()
    {
        var names = new List<string> { Host() };
        if (_announcement["quem"] is JsonArray who)
            foreach (var n in who) if ((string?)n is { Length: > 0 } nick && !names.Contains(nick)) names.Add(nick);
        return names;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutButton(); }

    void LayoutButton()
    {
        int p = UiScale.Dp(this, Pad), h = UiScale.Dp(this, 42), w = UiScale.Dp(this, 132);
        _enter.Font = _body;
        _enter.SetBounds(Math.Max(p, Width - p - w), Height - p - h + UiScale.Dp(this, 2), Math.Min(w, Math.Max(1, Width - 2 * p)), h);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int p = UiScale.Dp(this, Pad), poster = PosterHeight(this, Width);
        using var shape = Theme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), UiScale.Dp(this, 12));
        using (var fill = new SolidBrush(Theme.SurfaceRaised)) g.FillPath(fill, shape);
        CinemaPoster.Draw(g, new Rectangle(0, 0, Width, poster), shape, (string?)_announcement["filme"] ?? Name(), _coverOf(_announcement));
        using (var edge = new Pen(Theme.CardBorder, UiScale.Dp(this, 1))) g.DrawPath(edge, shape);
        int y = poster + UiScale.Dp(this, 12), line = UiScale.Dp(this, 36);
        TextRenderer.DrawText(g, Name(), _title, new Rectangle(p, y, Width - 2 * p, line), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        DrawHostRow(g, p, y + line + UiScale.Dp(this, 2));
        int divider = y + line + UiScale.Dp(this, 46);
        using (var pen = new Pen(Theme.Separator)) g.DrawLine(pen, p, divider, Width - p, divider);
        DrawWatching(g, p, _enter.Top + _enter.Height / 2);
    }

    void DrawHostRow(Graphics g, int x, int y)
    {
        int size = UiScale.Dp(this, HostAvatar), row = UiScale.Dp(this, 34);
        AvatarBadge.Draw(g, new Rectangle(x, y + (row - size) / 2, size, size), _avatarOf(Host()), Host(), 0);
        var status = CinemaStatus.Of((string?)_announcement["estado"]);
        int statusWidth = Math.Min(status.Width(g, this, _body), Width / 2);
        int nameX = x + size + UiScale.Dp(this, 12);
        TextRenderer.DrawText(g, Host(), _body, new Rectangle(nameX, y, Math.Max(0, Width - x - statusWidth - nameX - UiScale.Dp(this, 12)), row), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        status.Draw(g, this, new Rectangle(Width - x - statusWidth, y, statusWidth, row), _body, alignRight: true);
    }

    // as bolinhas de quem assiste param antes do "Entrar"; quem não cabe vira "+N"
    void DrawWatching(Graphics g, int x, int cy)
    {
        int size = UiScale.Dp(this, AvatarSize), gap = UiScale.Dp(this, 8), limit = _enter.Left - gap;
        var names = Watching();
        for (int i = 0; i < names.Count; i++)
        {
            int left = x + i * (size + gap);
            bool last = left + 2 * size + gap > limit && i < names.Count - 1;
            var area = new Rectangle(left, cy - size / 2, size, size);
            if (left + size > limit) return;
            if (last)
            {
                using (var brush = new SolidBrush(Theme.Neutral)) g.FillEllipse(brush, area);
                TextRenderer.DrawText(g, $"+{names.Count - i}", _initials, area, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                return;
            }
            AvatarBadge.Draw(g, area, _avatarOf(names[i]), names[i], 0);
        }
    }

    void MakeFonts()
    {
        _title = new Font(Font.FontFamily, Font.Size * 1.5f, FontStyle.Bold);
        _body = new Font(Font.FontFamily, Font.Size * 1.1f);
        _initials = new Font(Font.FontFamily, Font.Size * 0.85f, FontStyle.Bold);
    }

    void ReleaseFonts() { _title.Dispose(); _body.Dispose(); _initials.Dispose(); }

    protected override void Dispose(bool disposing) { if (disposing) ReleaseFonts(); base.Dispose(disposing); }
}
