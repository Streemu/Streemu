using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// A entrada numa sessão do Cinema (mockup "Cinema Formas de entrada", 2026-11-15): capa, nome, anfitrião e estado em cima; "Como você quer
/// assistir?" e uma caixa por jeito de entrar, com o texto à esquerda e o botão à direita (embaixo, numa coluna estreita). Os botões são os
/// da FilmeTela (o clique continua lá); este controle só desenha e posiciona, e acerta a própria altura pela largura.
/// </summary>
public sealed class CinemaEntryView : Control
{
    /// <summary>Um jeito de entrar: título, explicação e o botão que faz.</summary>
    public sealed record Option(string Title, string Description, Button Action);

    readonly Button[] _actions;
    string _title = "", _host = "", _heading = "", _note = "", _posterKey = "";
    CinemaStatus? _status;
    bool _poster;
    Image? _cover;
    IReadOnlyList<Option> _options = [];
    Font _titleFont = null!, _subFont = null!, _headingFont = null!, _optionFont = null!, _bodyFont = null!, _buttonFont = null!;
    // calculado no Arrange e usado no OnPaint
    Rectangle _posterArea, _titleArea, _hostArea, _statusArea, _headingArea, _noteArea;
    readonly List<(Rectangle Box, Rectangle Title, Rectangle Text)> _boxes = new();

    /// <example><code>var entrada = new CinemaEntryView([assistir, baixar, escolher]);</code></example>
    public CinemaEntryView(Button[] actions)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface; ForeColor = Theme.Text;
        _actions = actions;
        Controls.AddRange(actions);
        MakeFonts();
        FontChanged += (_, _) => { ReleaseFonts(); MakeFonts(); Arrange(); };
    }

    /// <summary>O topo: com <paramref name="poster"/> falso (criar sessão) não há capa nem anfitrião. <paramref name="cover"/> nulo = o degradê.</summary>
    public void ShowHeader(string title, string host, CinemaStatus? status, string posterKey, bool poster, Image? cover = null)
    {
        if (_title == title && _host == host && _status == status && _posterKey == posterKey && _poster == poster && _cover == cover) return;
        _title = title; _host = host; _status = status; _posterKey = posterKey; _poster = poster; _cover = cover;
        Arrange();
    }

    /// <summary>As caixas, na ordem; os botões fora da lista somem. <paramref name="note"/> é a linha apagada embaixo (aviso, sem rede).</summary>
    public void ShowOptions(string heading, IReadOnlyList<Option> options, string note)
    {
        bool same = _heading == heading && _note == note && _options.SequenceEqual(options);
        _heading = heading; _note = note; _options = options;
        foreach (var b in _actions) b.Visible = options.Any(o => o.Action == b);
        if (!same) Arrange();
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Arrange(); }

    void Arrange()
    {
        if (Width <= 0) return;
        int gap = UiScale.Dp(this, 12), y = UiScale.Dp(this, 8);
        y = ArrangeHeader(y) + UiScale.Dp(this, 36);
        _headingArea = new Rectangle(0, y, Width, TextHeight(_heading, _headingFont, Width));
        y = _headingArea.Bottom + UiScale.Dp(this, 20);
        _boxes.Clear();
        foreach (var o in _options) y = ArrangeBox(o, y) + UiScale.Dp(this, 18);
        _noteArea = new Rectangle(0, y - gap, Width, _note.Length == 0 ? 0 : TextHeight(_note, _bodyFont, Width));
        int height = _noteArea.Bottom + UiScale.Dp(this, 24);
        if (Height != height) Height = height;
        Invalidate();
    }

    // capa à esquerda e os textos ao lado; numa coluna estreita a capa fica em cima, na largura toda, e os textos embaixo
    int ArrangeHeader(int y)
    {
        bool stacked = Width < UiScale.Dp(this, 480);
        int posterWidth = !_poster ? 0 : stacked ? Width : Width < UiScale.Dp(this, 640) ? UiScale.Dp(this, 200) : UiScale.Dp(this, 300);
        int posterHeight = Math.Min(posterWidth * 55 / 100, UiScale.Dp(this, 200));
        _posterArea = new Rectangle(0, y, posterWidth, posterHeight);
        int x = posterWidth > 0 && !stacked ? posterWidth + UiScale.Dp(this, 28) : 0, textWidth = Math.Max(1, Width - x);
        int titleHeight = TextHeight(_title, _titleFont, textWidth), line = UiScale.Dp(this, 34);
        int block = titleHeight + (_host.Length > 0 ? line : 0) + (_status != null ? line + UiScale.Dp(this, 6) : 0);
        int top = stacked ? y + (posterHeight > 0 ? posterHeight + UiScale.Dp(this, 16) : 0) : y + Math.Max(0, (posterHeight - block) / 2);
        _titleArea = new Rectangle(x, top, textWidth, titleHeight);
        _hostArea = new Rectangle(x, _titleArea.Bottom, textWidth, _host.Length > 0 ? line : 0);
        _statusArea = new Rectangle(x, _hostArea.Bottom + UiScale.Dp(this, 6), textWidth, _status != null ? line : 0);
        return Math.Max(_posterArea.Bottom, _statusArea.Bottom);
    }

    // texto à esquerda e botão à direita; sem espaço para os dois lado a lado, o botão desce e ocupa a largura da caixa
    int ArrangeBox(Option o, int y)
    {
        int pad = UiScale.Dp(this, 24), buttonWidth = UiScale.Dp(this, 230), buttonHeight = UiScale.Dp(this, 48);
        bool stacked = Width < UiScale.Dp(this, 600);
        int textWidth = Math.Max(1, Width - 2 * pad - (stacked ? 0 : buttonWidth + pad));
        int titleHeight = TextHeight(o.Title, _optionFont, textWidth), descHeight = TextHeight(o.Description, _bodyFont, textWidth);
        int textHeight = titleHeight + UiScale.Dp(this, 6) + descHeight;
        int inner = stacked ? textHeight + UiScale.Dp(this, 16) + buttonHeight : Math.Max(textHeight, buttonHeight);
        var box = new Rectangle(0, y, Width - 1, inner + 2 * pad);
        int textTop = stacked ? y + pad : y + pad + (inner - textHeight) / 2;
        var title = new Rectangle(pad, textTop, textWidth, titleHeight);
        var text = new Rectangle(pad, title.Bottom + UiScale.Dp(this, 6), textWidth, descHeight);
        o.Action.Font = _buttonFont;
        if (stacked) o.Action.SetBounds(pad, text.Bottom + UiScale.Dp(this, 16), Width - 2 * pad, buttonHeight);
        else o.Action.SetBounds(Width - pad - buttonWidth, y + pad + (inner - buttonHeight) / 2, buttonWidth, buttonHeight);
        _boxes.Add((box, title, text));
        return box.Bottom;
    }

    int TextHeight(string text, Font font, int width) => text.Length == 0 ? 0
        : TextRenderer.MeasureText(text, font, new Size(Math.Max(1, width), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_posterArea.Width > 0)
        {
            using var shape = Theme.RoundedPath(_posterArea, UiScale.Dp(this, 10));
            CinemaPoster.Draw(g, _posterArea, shape, _posterKey, _cover);
            using var edge = new Pen(Theme.CardBorder, UiScale.Dp(this, 1));
            g.DrawPath(edge, shape);
        }
        const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.Left;
        TextRenderer.DrawText(g, _title, _titleFont, _titleArea, Theme.Text, Wrap);
        TextRenderer.DrawText(g, _host, _subFont, _hostArea, Theme.TextMuted, Wrap | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        _status?.Draw(g, this, _statusArea, _subFont, alignRight: false);
        TextRenderer.DrawText(g, _heading, _headingFont, _headingArea, Theme.Text, Wrap);
        using var border = new Pen(Theme.CardBorder, UiScale.Dp(this, 1));
        for (int i = 0; i < _boxes.Count && i < _options.Count; i++)
        {
            var (box, title, text) = _boxes[i];
            using (var shape = Theme.RoundedPath(box, UiScale.Dp(this, 12))) g.DrawPath(border, shape);
            TextRenderer.DrawText(g, _options[i].Title, _optionFont, title, Theme.Text, Wrap);
            TextRenderer.DrawText(g, _options[i].Description, _bodyFont, text, Theme.Text, Wrap);
        }
        TextRenderer.DrawText(g, _note, _bodyFont, _noteArea, Theme.TextMuted, Wrap);
    }

    void MakeFonts()
    {
        var family = Font.FontFamily;
        _titleFont = new Font(family, Font.Size * 1.9f, FontStyle.Bold);
        _subFont = new Font(family, Font.Size * 1.25f);
        _headingFont = new Font(family, Font.Size * 1.75f, FontStyle.Bold);
        _optionFont = new Font(family, Font.Size * 1.5f, FontStyle.Bold);
        _bodyFont = new Font(family, Font.Size * 1.12f);
        _buttonFont = new Font(family, Font.Size * 1.15f, FontStyle.Bold);
    }

    void ReleaseFonts()
    {
        foreach (var f in new[] { _titleFont, _subFont, _headingFont, _optionFont, _bodyFont, _buttonFont }) f.Dispose();
    }

    protected override void Dispose(bool disposing) { if (disposing) ReleaseFonts(); base.Dispose(disposing); }
}
