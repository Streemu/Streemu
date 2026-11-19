using System.Drawing.Drawing2D;
using System.Text.Json.Nodes;
using Streemu.Ui.Avatars;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// Descoberta e entrada do Cinema (mockups "Cinema Sessões" e "Cinema Formas de entrada", 2026-11-15); não possui ligações, arquivos ou
/// players. Na lista: o título, "+ Criar sessão" e os cartões. Na entrada: o link "Voltar ao Cinema" e o painel de entrada da FilmeTela.
/// </summary>
public sealed class CinemaView : UserControl
{
    const int CardMinWidth = 300, CardMaxWidth = 640, DetailMaxWidth = 880;
    readonly Label _title = new() { Text = "Cinema", AutoSize = true };
    readonly Button _create = new() { Text = "Criar sessão", AccessibleName = "Criar sessão" };
    // "← Voltar ao Cinema" é um link (texto roxo claro, sem fundo), como o "Voltar às lives" da tela Transmitir
    readonly Button _back = new() { Text = "←  Voltar ao Cinema", AccessibleName = "Voltar ao Cinema", TextAlign = ContentAlignment.MiddleLeft, Cursor = Cursors.Hand };
    readonly Button _resume = new() { Text = "Voltar à sessão", Visible = false };
    readonly Label _empty = new() { AutoSize = false, ForeColor = Theme.TextMuted };
    readonly CinemaEmptyState _ready = new() { Visible = false };
    readonly Panel _scroll = new() { AutoScroll = true, Dock = DockStyle.Fill };
    readonly Panel _header = new() { Dock = DockStyle.Top };
    readonly Dictionary<string, CinemaSessionCard> _cards = new();
    readonly Control _detail;
    readonly Font _heading = UiTypography.TitleFont();
    bool _showDetail;
    string? _emptyText;
    readonly record struct Summary(string? Host, string? Film, string? Name, string? Nick, string? State, int? People, string? Who, string? Cover);
    Summary[] _lastSessions = [];
    bool _lastDetail, _lastOccupied, _hasData;
    string? _lastOffline;
    bool _arranging;
    public event Action<JsonObject>? SessionPicked;
    public event Action? CreatePicked, BackPicked, ResumePicked;

    /// <summary>A foto de um amigo pelo apelido (nulo = a inicial colorida, como no resto do app).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string, AvatarImage?> AvatarOf { get; set; } = _ => null;

    /// <summary>A capa de uma sessão pelo anúncio (spec 017; nulo = o degradê). A imagem é de quem a guarda: o cartão só desenha.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<JsonObject, Image?> CoverOf { get; set; } = _ => null;

    /// <summary>Uma capa chegou: o anúncio é o mesmo (o <see cref="SetSessions"/> não redesenha), então os cartões redesenham aqui.</summary>
    public void RefreshCovers() { foreach (var card in _cards.Values) card.Invalidate(); }

    public CinemaView(Control detail)
    {
        Dock = DockStyle.Fill; BackColor = Theme.Surface; ForeColor = Theme.Text;
        _detail = detail; _title.Font = _heading;
        _header.BackColor = BackColor; _scroll.BackColor = BackColor;
        _header.Controls.AddRange([_title, _create, _back, _resume]);
        _scroll.Controls.AddRange([_detail, _empty, _ready]);
        ScrollSkin.Attach(_scroll);
        Controls.Add(_scroll); Controls.Add(_header);
        Theme.StyleButton(_create, true); Theme.StyleButton(_resume, false);
        StyleLink(_back);
        _create.Click += (_, _) => CreatePicked?.Invoke();
        _back.Click += (_, _) => BackPicked?.Invoke();
        _resume.Click += (_, _) => ResumePicked?.Invoke();
        _header.Resize += (_, _) => Arrange();
        _scroll.ClientSizeChanged += (_, _) => Arrange();
        _detail.SizeChanged += (_, _) => ArrangeDetail();
        HandleCreated += (_, _) => ShowCinemaIcon();
        DpiChangedAfterParent += (_, _) => ShowCinemaIcon();
    }

    static void StyleLink(Button b)
    {
        b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0; b.UseVisualStyleBackColor = false;
        b.BackColor = Theme.Surface; b.FlatAppearance.MouseOverBackColor = Theme.Surface; b.FlatAppearance.MouseDownBackColor = Theme.Surface;
        b.ForeColor = Theme.AccentSoft;
        b.MouseEnter += (_, _) => b.ForeColor = Theme.Text;
        b.MouseLeave += (_, _) => b.ForeColor = Theme.AccentSoft;
    }

    // mesmo desenho do "Começar a transmitir" (pedido de 2026-11-16): ícone de 22 dp antes do texto, mas o rolo do Cinema da barra lateral
    void ShowCinemaIcon()
    {
        _create.Image?.Dispose();
        int size = UiScale.Dp(this, 22);
        var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap)) { g.SmoothingMode = SmoothingMode.AntiAlias; UiIcons.Draw(g, UiIcon.Cinema, new Rectangle(0, 0, size, size), Theme.AccentText); }
        _create.Image = bitmap;
        _create.ImageAlign = ContentAlignment.MiddleCenter;
        _create.TextImageRelation = TextImageRelation.ImageBeforeText;
    }

    public void SetSessions(IReadOnlyList<JsonObject> announcements, bool detail, bool occupied, string? offline)
    {
        var summaries = announcements.Select(a => new Summary((string?)a["de"], (string?)a["filme"], (string?)a["nome"], (string?)a["anfitriao"],
            (string?)a["estado"], (int?)a["pessoas"], a["quem"]?.ToJsonString(), (string?)a["capa"])).ToArray();
        if (_hasData && _lastDetail == detail && _lastOccupied == occupied && _lastOffline == offline && _lastSessions.SequenceEqual(summaries)) return;
        _hasData = true; _lastSessions = summaries; _lastDetail = detail; _lastOccupied = occupied; _lastOffline = offline;
        _showDetail = detail;
        _title.Visible = _create.Visible = !detail; _back.Visible = detail; _resume.Visible = occupied;
        // sem sessões: a Mumu e o convite; fora da sala, só o motivo (não há sessão a criar nem a esperar)
        bool none = !detail && announcements.Count == 0;
        _detail.Visible = detail; _empty.Visible = none && offline != null; _ready.Visible = none && offline == null;
        string emptyText = offline ?? "";
        if (_emptyText != emptyText) { _empty.Text = emptyText; _emptyText = emptyText; }
        var keep = new HashSet<string>();
        foreach (var a in announcements)
        {
            // Comprimento evita colisões caso um identificador contenha o separador.
            string host = (string?)a["de"] ?? "", key = $"{host.Length}:{host}{(string?)a["filme"]}";
            keep.Add(key);
            if (!_cards.TryGetValue(key, out var card))
            {
                card = new CinemaSessionCard(nick => AvatarOf(nick), a => CoverOf(a)); _cards.Add(key, card); _scroll.Controls.Add(card);
                card.Picked += announcement => SessionPicked?.Invoke(announcement);
            }
            card.SetAnnouncement(a); card.Visible = !detail;
        }
        foreach (var key in _cards.Keys.Where(k => !keep.Contains(k)).ToArray())
        { var card = _cards[key]; _cards.Remove(key); _scroll.Controls.Remove(card); card.Dispose(); }
        Arrange();
    }

    void Arrange()
    {
        if (IsDisposed || _arranging) return;
        _arranging = true;
        try
        {
            ArrangeHeader();
            _scroll.SuspendLayout();
            ArrangeCards();
            ArrangeDetail();
            _scroll.ResumeLayout();
        }
        finally { _arranging = false; }
        // Ao encolher a janela, a área rolável guardava a medida antiga da tela vazia e mostrava uma barra de rolagem sem nada para rolar
        // (DisplayRectangle 1164x613 numa área de 710x431, --cinema-check 2026-11-16); refazer o layout dela recalcula as barras.
        if (_ready.Visible && _scroll.DisplayRectangle.Size != _scroll.ClientSize) _scroll.PerformLayout();
    }

    // lista: "Cinema" à esquerda e "+ Criar sessão" à direita (o "Voltar à sessão" ao lado); estreito, os botões descem
    // entrada: só o link "Voltar ao Cinema" (e o "Voltar à sessão" à direita)
    void ArrangeHeader()
    {
        int pad = UiScale.Dp(this, UiMetrics.PagePadding), gap = UiScale.Dp(this, UiMetrics.Gap);
        int action = UiScale.Dp(this, UiMetrics.ActionHeight), width = ClientSize.Width;
        int createWidth = Math.Min(Math.Max(1, width - 2 * pad), UiScale.Dp(this, 200)), resumeWidth = Math.Min(Math.Max(1, width - 2 * pad), UiScale.Dp(this, 180));
        int buttons = (_showDetail ? 0 : createWidth) + (_resume.Visible ? resumeWidth + gap : 0);
        bool narrow = width - 2 * pad - buttons < (_showDetail ? UiScale.Dp(this, 200) : _title.PreferredWidth + gap);
        _title.Location = new Point(pad, pad + (action - _title.PreferredHeight) / 2);
        _back.SetBounds(pad, pad, Math.Min(Math.Max(1, width - 2 * pad), TextRenderer.MeasureText(_back.Text, _back.Font).Width + UiScale.Dp(this, 24)), action);
        int rowY = narrow ? pad + action + gap : pad, right = width - pad;
        if (!_showDetail) { _create.SetBounds(narrow ? pad : right - createWidth, rowY, createWidth, action); right = _create.Left - gap; }
        if (_resume.Visible)
        {
            bool below = narrow && !_showDetail;
            _resume.SetBounds(narrow ? pad : right - resumeWidth, below ? rowY + action + gap : rowY, resumeWidth, action);
        }
        int bottom = new[] { _title.Bottom, _back.Visible ? _back.Bottom : 0, _create.Visible ? _create.Bottom : 0, _resume.Visible ? _resume.Bottom : 0 }.Max();
        _header.Height = bottom + gap;
    }

    // duas sessões ocupam a largura toda (como no mockup); mais sessões fazem mais colunas até o cartão ficar com a largura mínima
    void ArrangeCards()
    {
        int pad = UiScale.Dp(this, UiMetrics.PagePadding), gap = UiScale.Dp(this, 20);
        int available = Math.Max(1, _scroll.ClientSize.Width - pad * 2);
        int min = UiScale.Dp(this, CardMinWidth), max = UiScale.Dp(this, CardMaxWidth);
        int columns = Math.Max(1, Math.Min(Math.Max(1, _cards.Count), (available + gap) / (min + gap)));
        int cardWidth = Math.Min(max, (available - gap * (columns - 1)) / columns);
        int height = CinemaSessionCard.HeightFor(this, cardWidth), index = 0;
        foreach (var card in _cards.Values)
        {
            card.SetBounds(pad + index % columns * (cardWidth + gap) + _scroll.AutoScrollPosition.X,
                index / columns * (height + gap) + _scroll.AutoScrollPosition.Y, cardWidth, height);
            index++;
        }
        _empty.SetBounds(pad, 0, available, UiScale.Dp(this, 100));
        // ocupa a altura que sobra; numa janela baixa cresce até o texto caber, e a página rola
        _ready.SetBounds(pad, _scroll.AutoScrollPosition.Y, available, Math.Max(_scroll.ClientSize.Height - pad, _ready.MinimumContentHeight(available)));
    }

    // a entrada é uma coluna no meio da página; ela mesma acerta a altura pela largura
    void ArrangeDetail()
    {
        if (!_showDetail) return;
        int pad = UiScale.Dp(this, UiMetrics.PagePadding);
        int available = Math.Max(1, _scroll.ClientSize.Width - pad * 2);
        int width = Math.Min(UiScale.Dp(this, DetailMaxWidth), available);
        _detail.SetBounds(pad + (available - width) / 2 + _scroll.AutoScrollPosition.X, _scroll.AutoScrollPosition.Y, width, _detail.Height);
    }

    protected override void Dispose(bool disposing) { if (disposing) { _heading.Dispose(); _create.Image?.Dispose(); } base.Dispose(disposing); }
}
