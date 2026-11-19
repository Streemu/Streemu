using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using Streemu.Media;
using Streemu.Net;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
    /// Preparação compartilhada pela Home e pela página interna. Selecionar fonte nunca transmite;
    /// somente SubmitRequested entrega a intenção ao comando da sessão.
/// </summary>
public sealed class GoLiveView : UserControl
{
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);

    List<CaptureSource> _sources = new();
    readonly CaptureSource? _current;
    readonly string? _lastProcess;
    bool _loaded, _busy;
    readonly bool _changing;
    bool _inline;
    bool _showBack;
    /// <summary>
    /// <paramref name="showBack"/>: na coluna ao lado das lives (preparação com o palco aberto) o painel também leva o link "← Voltar às lives" no alto, como no desenho.
    /// </summary>
    public void SetInline(bool inline, bool showBack = false) { if (_inline == inline && _showBack == showBack) return; _inline = inline; _showBack = showBack; PerformLayout(); }
    static readonly SemaphoreSlim ThumbnailGate = new(2);
    readonly CancellationTokenSource _lifetime = new();
    public event Action? SubmitRequested, BackRequested;
    readonly System.Windows.Forms.Timer _watch = new() { Interval = 1500 };
    Label? _emptyHint;
    bool _watching;
    readonly FlowLayoutPanel _grid = new() { Dock = DockStyle.Fill, AutoScroll = false, WrapContents = true, Padding = new Padding(4) };
    readonly Segmented _tabs = new("Aplicativos", "Telas");
    readonly Segmented _resSeg = new("720p", "1080p", "1440p", "Original");
    readonly Segmented _fpsSeg = new("30", "60"); // o rótulo "FPS" já está no título da seção, como no desenho
    // a ordem na tela é a do desenho (PC sem Discord primeiro); o valor é o AudioMode de sempre (0 = só o app, 1 = PC sem Discord, 2 = sem som)
    readonly OptionList _audioSeg = new(("PC sem Discord", UiIcon.Monitor, 1), ("Só o app escolhido", UiIcon.Gamepad, 0), ("Sem som", UiIcon.SpeakerOff, 2)) { CenterContent = true };
    readonly Segmented _privacySeg = new("Aberta ao grupo", "Só convidados");
    readonly FlowLayoutPanel _inviteesPanel = new() { AutoSize = true, WrapContents = true, Visible = false, Margin = new Padding(0, 4, 0, 4), MaximumSize = new Size(560, 0) };
    readonly List<(CheckBox Box, string Id)> _inviteBoxes = new();
    Label? _inviteesCaption;
    readonly Label _summary = new() { AutoSize = false, AutoEllipsis = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 30, TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.GrayText };
    readonly Button _go;
    readonly List<Tile> _tiles = new();
    bool _showScreens;
    int _height, _fps;

    public CaptureSource? SelectedSource { get; private set; }
    public Preset SelectedPreset => Preset.Pick(_height, _fps);
    public int AudioIndex => _audioSeg.Selected;
    /// <summary>Spec 012: a live é só para convidados (D6: a janela sempre abre em "Aberta ao grupo").</summary>
    public bool Privada => _privacySeg.Selected == 1;
    public IReadOnlyList<string> ConvidadosIds => _inviteBoxes.Where(b => b.Box.Checked).Select(b => b.Id).ToList();

    /// <param name="current">Fonte atual (transmitindo) ou nula.</param>
    /// <param name="lastProcess">Processo da última transmissão, para selecionar de novo.</param>
    /// <param name="friendsOnline">Amigos na sala para a escolha dos convidados; nulo ao trocar (a privacidade vale para a transmissão inteira).</param>
    /// <param name="convidarId">Id de um amigo para abrir já em "Só convidados" com ele marcado (menu da foto, spec 013); ignorado se ele não está em <paramref name="friendsOnline"/>.</param>
    public GoLiveView(CaptureSource? current, string? lastProcess, int height, int fps, int audio, bool changing, IReadOnlyList<Member>? friendsOnline = null, string? convidarId = null)
    {
        _current = current; _lastProcess = lastProcess; _changing = changing;
        Dock = DockStyle.Fill;
        _grid.Dock = DockStyle.None;
        Font = new Font(UiTypography.Family, UiTypography.Body);
        _height = height; _fps = fps;

        _title.Text = changing ? "Trocar o que transmitir" : "Transmitir";
        _title.Font = UiTypography.TitleFont();
        _showLabel.Font = new Font(Font, FontStyle.Bold);
        UiScale.WatchFont(this); UiScale.WatchFont(_title);
        // os três seletores são do mesmo tipo e começam na mesma coluna: a janela fica simétrica (antes eram botões soltos de larguras diferentes)
        _tabs.Visible = false; // as telas e as janelas moram na mesma lista (sem abas)
        _summary.Visible = false;
        _resSeg.StretchColumns = 2; _fpsSeg.StretchColumns = 2; _privacySeg.StretchColumns = 2;
        _resSeg.Selected = Math.Max(0, Array.IndexOf(Preset.PickerHeights, height));
        _fpsSeg.Selected = fps >= 60 ? 1 : 0;
        _audioSeg.Selected = audio is >= 0 and <= 2 ? audio : 0;
        _resSeg.SelectedChanged += i => { _height = Preset.PickerHeights[i]; UpdateSummary(); };
        _fpsSeg.SelectedChanged += i => { _fps = i == 1 ? 60 : 30; UpdateSummary(); };

        AddOption(_options, 0, "Qualidade", _resSeg);
        AddOption(_options, 1, "FPS", _fpsSeg);
        AddOption(_options, 2, "Som", _audioSeg);
        if (!changing && friendsOnline != null) AddPrivacyOptions(_options, friendsOnline, convidarId);
        _back.Click += (_, _) => { if (!_busy) BackRequested?.Invoke(); };
        _go = new Button { Text = changing ? "Aplicar alteração" : "Começar a transmitir", AccessibleName = changing ? "Aplicar alteração" : "Começar a transmitir" };
        _go.Click += (_, _) => { if (!_busy && SelectedSource != null) SubmitRequested?.Invoke(); };
        _footer.Controls.Add(_go);
        _body.Controls.AddRange([_title, _showLabel, _grid, _more, _options, _summary, _failure]);
        _more.Click += (_, _) => { _showAllTiles = !_showAllTiles; PerformLayout(); };
        Controls.AddRange([_body, _footer, _back]);
        ScrollSkin.Attach(_body);
        Theme.Paint(this, new HashSet<Button> { _go });
        _back.ForeColor = Theme.AccentSoft;
        _back.KeyDown += (_, e) => { if (e.KeyCode is Keys.Enter or Keys.Space) { _back.Focus(); if (!_busy) BackRequested?.Invoke(); e.Handled = true; } };
        _grid.BackColor = Theme.Surface;
        _go.Enabled = false;
        _watch.Tick += (_, _) => WatchSources();
        VisibleChanged += (_, _) => UpdateActivity();
        HandleCreated += (_, _) => { UpdateActivity(); ShowBroadcastIcon(); };
    }

    readonly Panel _body = new() { AutoScroll = true };
    readonly Panel _footer = new();
    /// <summary>"← Voltar às lives" é um link, não um botão (desenho): texto roxo claro, sem fundo, alinhado à esquerda da coluna.</summary>
    readonly Label _back = new() { Text = "←  Voltar às lives", AccessibleName = "Voltar às lives", AccessibleRole = AccessibleRole.PushButton, Cursor = Cursors.Hand, TextAlign = ContentAlignment.MiddleLeft, TabStop = true };
    readonly Label _title = new() { AutoSize = true };
    /// <summary>"O que mostrar" acima da lista de telas e janelas, como no desenho.</summary>
    readonly Label _showLabel = new() { AutoSize = true, Text = "O que mostrar" };
    /// <summary>
    /// Como no desenho, a lista mostra só as 4 primeiras peças (telas primeiro) e o resto fica atrás de "Mostrar mais": o painel não vira uma página enorme com muitas janelas abertas.
    /// A escolhida sempre aparece (se ela está além da quarta, a lista abre inteira).
    /// </summary>
    const int CollapsedTiles = 4;
    readonly Label _more = new() { AutoSize = true, Cursor = Cursors.Hand, Visible = false, ForeColor = Theme.AccentSoft, TabStop = true, AccessibleRole = AccessibleRole.PushButton };
    bool _showAllTiles;
    readonly NoticeBox _failure = new();
    readonly TableLayoutPanel _options = new() { ColumnCount = 1, AutoSize = true };
    bool _layout;

    public void SetBusy(bool busy)
    {
        _busy = busy;
        _body.Enabled = _back.Enabled = !busy;
        _go.Enabled = !busy && SelectedSource != null;
        _go.Text = busy ? "Preparando…" : _changing ? "Aplicar alteração" : "Começar a transmitir";
        UpdateActivity();
    }
    public void SetFailure(string? message)
    {
        _failure.Message = message ?? "";
        _failure.Visible = !string.IsNullOrEmpty(message);
        _retry = _failure.Visible;
        _go.Text = _busy ? "Preparando…" : _retry ? "Tentar novamente" : _changing ? "Aplicar alteração" : "Começar a transmitir"; // como no desenho: depois da falha o botão grande vira "Tentar novamente"
        ShowBroadcastIcon();
        PerformLayout();
        if (_failure.Visible) _body.ScrollControlIntoView(_failure);
    }
    public void Invite(string id) => PreSelecionar(id);
    public void RefreshFriends(IReadOnlyList<Member> friends)
    {
        if (_changing) return;
        var signature = string.Join("\n", friends.Select(f => f.Id + ":" + f.Nick));
        if (signature == _friendsSignature) return;
        _friendsSignature = signature;
        var selected = ConvidadosIds.ToHashSet();
        foreach (Control child in _inviteesPanel.Controls.Cast<Control>().ToArray()) child.Dispose();
        _inviteBoxes.Clear();
        FillInvitees(friends);
        foreach (var pair in _inviteBoxes) pair.Box.Checked = selected.Contains(pair.Id);
        Theme.Paint(_inviteesPanel, []);
    }
    string? _friendsSignature;
    void UpdateActivity()
    {
        _watch.Enabled = Visible && IsHandleCreated && FindForm()?.WindowState != FormWindowState.Minimized && !_busy && Environment.GetEnvironmentVariable("STREEMU_TEST_UI_ONLY") != "1";
        if (_watch.Enabled && !_loaded) WatchSources();
    }
    public void RefreshActivity() => UpdateActivity();
    internal void ApplyAppearance() { Theme.StyleButton(_go, true); ShowBroadcastIcon(); }

    /// <summary>O botão grande do desenho leva o ícone de transmissão antes do texto; só em "Começar a transmitir" (trocar a fonte não é começar).</summary>
    bool _retry;

    void ShowBroadcastIcon()
    {
        _go.Image?.Dispose();
        _go.Image = null;
        if (_changing || !IsHandleCreated) return;
        int size = S(22);
        var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap)) { g.SmoothingMode = SmoothingMode.AntiAlias; UiIcons.Draw(g, _retry ? UiIcon.Update : UiIcon.Broadcast, new Rectangle(0, 0, size, size), Theme.AccentText); }
        _go.Image = bitmap;
        _go.ImageAlign = ContentAlignment.MiddleCenter;
        _go.TextImageRelation = TextImageRelation.ImageBeforeText;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!_inline) return;
        using var line = new Pen(Theme.Neutral);
        e.Graphics.DrawLine(line, 0, 0, 0, Height);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_layout || _go == null) return;
        _layout = true;
        try
        {
            int pad = S(UiMetrics.PagePadding), gap = S(UiMetrics.Gap);
            bool page = !_inline;
            _grid.BackColor = Theme.Surface; // os cartões ficam direto sobre o fundo (página e painel do Início), sem placa atrás da lista; os cartões também limpam com Surface
            foreach (var choice in new[] { _resSeg, _fpsSeg, _privacySeg }) choice.Outline = page;
            _audioSeg.Outline = page;
            int width = Math.Max(1, Math.Min(S(page ? 520 : UiMetrics.PreparationMaxWidth), ClientSize.Width - pad * 2)); // a coluna da página é estreita e centrada, como no desenho
            _back.Visible = !_inline || _showBack;
            int outerX = (ClientSize.Width - width) / 2;
            _back.Bounds = new Rectangle(outerX, gap, Math.Min(width, TextRenderer.MeasureText(_back.Text, _back.Font).Width + S(16)), S(UiMetrics.ActionHeight)); // só o texto é clicável
            _footer.Bounds = new Rectangle(0, Math.Max(_back.Bottom, ClientSize.Height - S(64)), ClientSize.Width, S(64));
            _go.Bounds = new Rectangle(outerX, gap, width, S(UiMetrics.ActionHeight));
            int bodyTop = !_inline || _showBack ? _back.Bottom + gap : 0;
            int edge = _inline ? 1 : 0; // no painel do Início uma linha fina separa a lista de lives do Transmitir, como no desenho
            _body.Bounds = new Rectangle(edge, bodyTop, ClientSize.Width - edge, Math.Max(0, _footer.Top - bodyTop));
            int available = Math.Max(1, Math.Min(width, _body.ClientSize.Width - pad * 2 - SystemInformation.VerticalScrollBarWidth));
            int x = (_body.ClientSize.Width - available) / 2;
            _back.Left = x; _go.Left = x; _go.Width = available; // a página tem uma só coluna: o link, os cartões, as opções e o botão grande começam e terminam nas mesmas bordas
            int y = pad + _body.AutoScrollPosition.Y;
            _title.MaximumSize = new Size(available, 0);
            _title.Location = new Point(x, y); y += _title.PreferredHeight + gap;
            _showLabel.Visible = !_changing;
            if (_showLabel.Visible) { _showLabel.Location = new Point(x, y); y += _showLabel.PreferredHeight + S(6); }
            // duas colunas de cartões que ocupam a largura toda (uma só quando o painel é estreito)
            int inner = available - S(8);
            int columns = inner >= S(200) ? 2 : 1;
            // com mais de 4 peças a lista pode precisar rolar: já deixa a barra de rolagem fora das colunas (reservar só depois de ela aparecer fazia a 2ª coluna não caber e a lista ficar numa coluna só)
            int tileWidth = TileWidthFor(inner, columns);
            int shown = ShownTiles();
            for (int i = 0; i < _tiles.Count; i++) { _tiles[i].Visible = i < shown; _tiles[i].Compact = !_inline; _tiles[i].FitWidth(tileWidth); }
            int used = Math.Min(columns, Math.Max(1, _tiles.Count)) * (tileWidth + TileMargin);
            _grid.Padding = new Padding(Math.Max(S(4), (available - used) / 2), S(4), S(4), S(4));
            SettingsLayout.Constrain(_options, available);
            _grid.Bounds = new Rectangle(x, y, available, SourceListHeight(available, tileWidth, shown)); y += _grid.Height + gap;
            _more.Visible = _tiles.Count > CollapsedTiles && !SelectedBeyondCollapsed();
            if (_more.Visible)
            {
                _more.Text = _showAllTiles ? "Mostrar menos" : $"Mostrar mais {_tiles.Count - CollapsedTiles}";
                _more.Location = new Point(x + Math.Max(0, (available - _more.PreferredWidth) / 2), y - gap / 2); y += _more.PreferredHeight;
            }
            _options.Location = new Point(x, y);
            // a altura vem de onde termina o último controle visível: o PreferredSize da tabela deixava um vazio de ~500 px no fim da página com os controles novos (mais altos)
            int optionsHeight = _options.Controls.Cast<Control>().Where(c => c.Visible).Select(c => c.Bottom + c.Margin.Bottom).DefaultIfEmpty(0).Max();
            _options.MaximumSize = new Size(available, Math.Max(1, optionsHeight)); // sem isso a tabela ficava ~500 px mais alta que as opções e a rolagem da página ganhava um vazio no fim
            y += optionsHeight + gap;
            if (_summary.Visible) { _summary.AutoSize = true; _summary.MaximumSize = new Size(available, 0); _summary.Location = new Point(pad, y); y += _summary.PreferredHeight + gap; }
            _failure.FitWidth(available); _failure.Location = new Point(x, y);
            _body.AutoScrollMinSize = new Size(0, y - _body.AutoScrollPosition.Y + (_failure.Visible ? _failure.Height + gap : 0));
        }
        finally { _layout = false; }
    }
    /// <summary>
    /// A lista de telas/janelas ocupava a altura de duas fileiras mesmo com uma fonte só (diagnóstico de UX, item 10, que o #138 resolveu na janela antiga).
    /// Aqui ela encolhe até as fileiras que as fontes pedem (no mínimo uma) e para no teto de antes; quem passa disso rola dentro da lista.
    /// </summary>
    /// <summary>A margem da peça é a que o WinForms já escalou pelo DPI (não 12 px fixos): era a diferença que fazia a segunda coluna não caber.</summary>
    int TileMargin => _tiles.Count > 0 ? _tiles[0].Margin.Horizontal : S(12);

    int TileWidthFor(int inner, int columns) => Math.Max(S(96), inner / columns - TileMargin);

    int TileHeightFor(int tileWidth) => Math.Max(1, tileWidth - 8) * 9 / 16 + 8 + (_tiles.Count > 0 ? _tiles[0].Margin.Vertical : S(12));

    /// <summary>A lista mostra todas as peças de uma vez (como no desenho) e quem rola é a página: uma lista rolando dentro de outra rolagem era o que dava a sensação de pouca responsividade.</summary>
    bool SelectedBeyondCollapsed() => _tiles.FindIndex(t => t.Selected) >= CollapsedTiles;

    int ShownTiles() => _showAllTiles || SelectedBeyondCollapsed() ? _tiles.Count : Math.Min(_tiles.Count, CollapsedTiles);

    int SourceListHeight(int available, int tileWidth, int shown)
    {
        int cell = tileWidth + TileMargin;
        int perRow = Math.Max(1, (available - _grid.Padding.Horizontal) / Math.Max(1, cell));
        int tileHeight = (_tiles.Count > 0 ? _tiles[0].Height : Math.Max(1, tileWidth - S(10)) * 9 / 16 + S(10) + Font.Height + S(16)) + S(12);
        int rows = Math.Max(1, (shown + perRow - 1) / perRow);
        return rows * tileHeight + _grid.Padding.Vertical + S(4);
    }

    void ChooseInitialSource()
    {
        SelectedSource = _current != null ? _sources.FirstOrDefault(s => s.Handle == _current.Handle && s.IsWindow == _current.IsWindow) : null;
        SelectedSource ??= _lastProcess != null ? _sources.FirstOrDefault(s => s.IsWindow && s.ProcessName.Equals(_lastProcess, StringComparison.OrdinalIgnoreCase) && s.IsFullScreen) : null;
        SelectedSource ??= _sources.FirstOrDefault(s => s.IsFullScreen);
        SelectedSource ??= _lastProcess != null ? _sources.FirstOrDefault(s => s.IsWindow && s.ProcessName.Equals(_lastProcess, StringComparison.OrdinalIgnoreCase)) : null;
        SelectedSource ??= _sources.FirstOrDefault(s => !s.IsWindow && s.IsPrimary);
        ShowTab(SelectedSource is { IsWindow: false });
    }

    int S(int px) => UiScale.Dp(this, px);

    /// <summary>Linha "PRIVADA" e, só quando ela está ligada, a lista dos amigos online para marcar (até 5, o anfitrião é o sexto).</summary>
    void AddPrivacyOptions(TableLayoutPanel options, IReadOnlyList<Member> friends, string? convidarId)
    {
        AddOption(options, 3, "Privacidade", _privacySeg);
        _inviteesCaption = new Label { Text = "Convidados", AutoSize = true, Anchor = AnchorStyles.None, Visible = false, ForeColor = Theme.TextMuted, Margin = new Padding(0, UiMetrics.Gap, 0, UiMetrics.Space) };
        options.Controls.Add(_inviteesCaption, 0, options.RowCount++);
        options.Controls.Add(_inviteesPanel, 0, options.RowCount++);
        FillInvitees(friends);
        _privacySeg.SelectedChanged += i =>
        {
            bool on = i == 1;
            _inviteesCaption.Visible = _inviteesPanel.Visible = on;
            PerformLayout();
        };
        PreSelecionar(convidarId);
    }

    /// <summary>Liga "Só convidados" e marca o amigo; se ele já saiu da sala, a janela abre como sempre (aberta, ninguém marcado).</summary>
    public void PreSelecionar(string? convidarId)
    {
        if (convidarId == null) return;
        var alvo = _inviteBoxes.FirstOrDefault(b => b.Id == convidarId).Box;
        if (alvo == null) return;
        _privacySeg.Selected = 1;
        alvo.Checked = true;
    }

    void FillInvitees(IReadOnlyList<Member> friends)
    {
        int max = SalaPrivada.Limite - 1;
        if (friends.Count == 0) _inviteesPanel.Controls.Add(new Label { Text = "Nenhum amigo online agora. Dá para convidar depois, com a live no ar.", AutoSize = true, ForeColor = SystemColors.GrayText });
        foreach (var friend in friends.OrderBy(f => f.Nick, StringComparer.CurrentCultureIgnoreCase))
        {
            var box = new CheckBox { Text = friend.Nick, AutoSize = true, Margin = new Padding(0, 2, 16, 2) };
            box.CheckedChanged += (_, _) =>
            {
                if (_inviteBoxes.Count(b => b.Box.Checked) <= max) return;
                box.Checked = false;
                _summary.Text = $"A sala é de {SalaPrivada.Limite} pessoas contando você: no máximo {max} convidados.";
            };
            _inviteBoxes.Add((box, friend.Id));
            _inviteesPanel.Controls.Add(box);
        }
    }

    /// <summary>Uma linha de opção: a etiqueta numa coluna fixa e o seletor sempre começando no mesmo ponto.</summary>
    static void AddOption(TableLayoutPanel table, int row, string caption, Control control)
    {
        var label = new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Text, Margin = new Padding(0, UiMetrics.Gap, 0, UiMetrics.Space) };
        label.Font = new Font(label.Font, FontStyle.Bold);
        control.AccessibleName = caption;
        table.Controls.Add(label, 0, table.RowCount++);
        table.Controls.Add(control, 0, table.RowCount++);
    }

    /// <summary>O que vai ao ar, numa linha: a tela ou janela escolhida, a resolução, os quadros e o teto de bitrate.</summary>
    void UpdateSummary()
    {
        var p = Preset.Pick(_height, _fps);
        string quality = $"{(_height == 0 ? "Original" : _height + "p")} · {_fps} FPS · até {p.Kbps / 1000.0:0.#} Mbps";
        string? what = _tiles.FirstOrDefault(t => t.Selected)?.Caption;
        _summary.Text = what == null ? quality : $"{what} · {quality}";
    }

    void ShowTab(bool screens)
    {
        _showScreens = screens;
        _grid.SuspendLayout();
        foreach (var t in _tiles) t.Dispose();
        _tiles.Clear();
        _grid.Controls.Clear();
        _emptyHint = null;
        foreach (var s in ScreensFirst(_sources)) AddTile(s);
        ShowEmptyHint();
        _grid.ResumeLayout();
        PerformLayout(); // a altura da lista acompanha quantas fontes a aba tem
        _go.Enabled = !_busy && SelectedSource != null;
        UpdateSummary();
    }

    void AddTile(CaptureSource s)
    {
        string caption = s.IsWindow ? WindowTitle(s) : ScreenCaption(s);
        var tile = new Tile(s, caption, S(UiMetrics.SourceWidth), S(UiMetrics.SourceHeight)) { Selected = s == SelectedSource };
        tile.Click += (_, _) => Select(tile);
        // Selecionar uma miniatura nunca começa a transmissão.
        _tiles.Add(tile);
        _grid.Controls.Add(tile);
        LoadThumbnail(tile);
    }

    /// <summary>As telas primeiro e as janelas depois, cada grupo na ordem de sempre (o OrderBy do LINQ é estável).</summary>
    static IEnumerable<CaptureSource> ScreensFirst(IEnumerable<CaptureSource> sources) => sources.OrderBy(s => s.IsWindow ? 1 : 0);

    /// <summary>"Tela inteira" quando há um monitor só; com vários, "Tela 1 (principal)", "Tela 2"...</summary>
    string ScreenCaption(CaptureSource s)
    {
        var screens = _sources.Where(x => !x.IsWindow).ToList();
        if (screens.Count <= 1) return "Tela inteira";
        return $"Tela {screens.IndexOf(s) + 1}" + (s.IsPrimary ? " (principal)" : "");
    }

    /// <summary>A mensagem de "nada aqui" só existe enquanto não há nenhuma peça.</summary>
    void ShowEmptyHint()
    {
        if (_tiles.Count > 0)
        {
            if (_emptyHint != null) { _grid.Controls.Remove(_emptyHint); _emptyHint.Dispose(); _emptyHint = null; }
            return;
        }
        if (_emptyHint != null) return;
        _emptyHint = new Label { Text = "Nenhuma tela ou janela encontrada. Abra o jogo e ele aparece aqui.", AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(8) };
        _grid.Controls.Add(_emptyHint);
    }

    // ------------------------------------------------------------------ lista em tempo real

    /// <summary>
    /// Confere de tempos em tempos o que está aberto: quem abre um jogo ou programa com esta janela já aberta o vê aparecer (e some quando fecha), sem
    /// precisar fechar e reabrir. Só mexe no que mudou: as miniaturas que já existem ficam como estão e a seleção é mantida.
    /// </summary>
    async void WatchSources()
    {
        if (_watching || IsDisposed || !Visible || _busy) return;
        _watching = true;
        try
        {
            var fresh = await Task.Run(Sources.List);
            if (IsDisposed || !Visible) return;
            if (!_loaded) { _sources = fresh; ChooseInitialSource(); _loaded = true; }
            else SyncSources(fresh);
        }
        catch (Exception ex) { Log.Info($"[transmitir] não consegui atualizar a lista de janelas: {ex.Message}"); }
        finally { _watching = false; }
    }

    void SyncSources(List<CaptureSource> fresh)
    {
        if (fresh.Select(s => (s.Handle, s.IsWindow, s.Label)).SequenceEqual(_sources.Select(s => (s.Handle, s.IsWindow, s.Label)))) return;
        // quem já estava na lista continua sendo o mesmo objeto (a seleção e a peça apontam para ele)
        var merged = fresh.Select(f => _sources.FirstOrDefault(o => o.Handle == f.Handle && o.IsWindow == f.IsWindow) ?? f).ToList();
        var shown = ScreensFirst(merged).ToList();
        _sources = merged;
        int shownBefore = _tiles.Count;
        _grid.SuspendLayout();
        foreach (var gone in _tiles.Where(t => !shown.Contains(t.Source)).ToList()) { _tiles.Remove(gone); _grid.Controls.Remove(gone); gone.Dispose(); }
        foreach (var s in shown.Where(s => _tiles.All(t => t.Source != s))) AddTile(s);
        ShowEmptyHint();
        for (int i = 0; i < shown.Count; i++) _grid.Controls.SetChildIndex(_tiles.First(t => t.Source == shown[i]), i);
        _grid.ResumeLayout();
        if (_tiles.Count != shownBefore) PerformLayout(); // jogo abriu ou fechou: a lista cresce ou encolhe junto
        if (SelectedSource != null && !merged.Contains(SelectedSource)) SelectedSource = null; // o app escolhido fechou
        _go.Enabled = !_busy && SelectedSource != null;
        UpdateSummary();
    }

    void Select(Tile tile)
    {
        SelectedSource = tile.Source;
        foreach (var t in _tiles) t.Selected = t == tile;
        _go.Enabled = !_busy;
        UpdateSummary();
    }

    static string WindowTitle(CaptureSource s)
    {
        var sb = new StringBuilder(256);
        Native.GetWindowText(s.Handle, sb, sb.Capacity);
        var t = sb.ToString().Trim();
        return t.Length > 0 ? t : s.ProcessName;
    }

    /// <summary>
    /// Miniatura em segundo plano: PrintWindow com PW_RENDERFULLCONTENT (pega janelas DirectX na maioria dos casos);
    /// tela pela área de trabalho. Se vier preta (jogo em tela cheia exclusiva, janela minimizada), mostra o ícone do jogo.
    /// </summary>
    async void LoadThumbnail(Tile tile)
    {
        if (Environment.GetEnvironmentVariable("STREEMU_TEST_UI_ONLY") == "1") return;
        int w = tile.ThumbSize.Width, h = tile.ThumbSize.Height;
        var src = tile.Source;
        var token = _lifetime.Token;
        Bitmap? thumbnail = null;
        try
        {
            await ThumbnailGate.WaitAsync(token);
            try
            {
                if (token.IsCancellationRequested || tile.IsDisposed) return;
                thumbnail = await Task.Run(() =>
                {
                    Bitmap? shot = null;
                    try { shot = src.IsWindow ? CaptureWindow(src.Handle) : CaptureMonitor(src.Handle); } catch { }
                    using (shot) return shot != null && !IsBlank(shot) ? Fit(shot, w, h) : IconThumb(src, w, h);
                });
                if (!tile.IsDisposed && !token.IsCancellationRequested) { tile.Image = thumbnail; thumbnail = null; }
            }
            finally { ThumbnailGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Info("[transmitir] miniatura indisponível: " + ex.Message); }
        finally { thumbnail?.Dispose(); }
    }

    static Bitmap? CaptureWindow(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) return null;
        Native.GetWindowRect(hwnd, out var r);
        if (r.Width <= 0 || r.Height <= 0) return null;
        var bmp = new Bitmap(r.Width, r.Height);
        using var g = Graphics.FromImage(bmp);
        IntPtr hdc = g.GetHdc();
        bool ok = PrintWindow(hwnd, hdc, 2);
        g.ReleaseHdc(hdc);
        if (!ok) { bmp.Dispose(); return null; }
        return bmp;
    }

    static Bitmap CaptureMonitor(IntPtr monitor)
    {
        var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        Native.GetMonitorInfo(monitor, ref info);
        var r = info.rcMonitor;
        var bmp = new Bitmap(r.Width, r.Height);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(r.Width, r.Height));
        return bmp;
    }

    static bool IsBlank(Bitmap b)
    {
        int bright = 0;
        for (int i = 1; i <= 8; i++)
            for (int j = 1; j <= 8; j++)
            {
                var c = b.GetPixel(b.Width * i / 9, b.Height * j / 9);
                if (c.R + c.G + c.B > 30) bright++;
            }
        return bright < 3;
    }

    static Bitmap Fit(Bitmap src, int w, int h)
    {
        var dst = new Bitmap(w, h);
        using var g = Graphics.FromImage(dst);
        g.Clear(Theme.VideoMargin);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        double k = Math.Min((double)w / src.Width, (double)h / src.Height);
        int dw = (int)(src.Width * k), dh = (int)(src.Height * k);
        g.DrawImage(src, (w - dw) / 2, (h - dh) / 2, dw, dh);
        return dst;
    }

    static Bitmap? IconThumb(CaptureSource s, int w, int h)
    {
        var dst = new Bitmap(w, h);
        using var g = Graphics.FromImage(dst);
        g.Clear(Theme.SurfaceRaised);
        try
        {
            var exe = s.IsWindow ? GameName.ExePath(s.ProcessId) : null;
            using var icon = exe != null ? Icon.ExtractAssociatedIcon(exe) : null;
            if (icon != null)
            {
                int size = Math.Min(48, h - 16);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                using var bitmap = icon.ToBitmap();
                g.DrawImage(bitmap, (w - size) / 2, (h - size) / 2, size, size);
            }
        }
        catch { }
        return dst;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed) { _lifetime.Cancel(); _lifetime.Dispose(); _watch.Dispose(); _title.Font.Dispose(); Font.Dispose(); }
        base.Dispose(disposing);
    }

    /// <summary>Miniatura + nome, com borda de destaque quando selecionada.</summary>
    sealed class Tile : Control
    {
        Image? _image;
        bool _selected, _hover;
        public CaptureSource Source { get; }
        public Size ThumbSize { get; }
        public string Caption { get; }

        public Tile(CaptureSource source, string caption, int w, int h)
        {
            Source = source; Caption = caption; ThumbSize = new Size(w, h);
            AccessibleName = caption; AccessibleRole = AccessibleRole.RadioButton; TabStop = true;
            Size = new Size(w + 10, h + 10 + Font.Height + 16);
            Margin = new Padding(6);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            MouseEnter += (_, _) => { _hover = true; Invalidate(); };
            MouseLeave += (_, _) => { _hover = false; Invalidate(); };
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Image? Image { get => _image; set { _image?.Dispose(); _image = value; Invalidate(); } }
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Selected { get => _selected; set { _selected = value; Invalidate(); } }

        /// <summary>
        /// Na página cheia (janela estreita ou "Transmitir" aberto sozinho) a peça é um cartão só com ícone e nome, sem miniatura, como no desenho; no painel do Início continua com a miniatura.
        /// </summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Compact { get; set; }
        bool Horizontal => Width < Font.Height * 12; // cartão estreito: ícone ao lado do nome; largo: ícone em cima
        int IconSize => Font.Height * 2;

        public void FitWidth(int width)
        {
            if (!Compact) { Size = new Size(width, ThumbHeightFor(width) + CaptionHeight + 10); return; }
            bool horizontal = width < Font.Height * 12;
            Size = new Size(width, horizontal ? IconSize + Font.Height + 10 : IconSize + Font.Height * 2 + 18);
        }
        /// <summary>A miniatura ocupa a largura do cartão menos a borda; a legenda (ícone e nome) fica numa linha embaixo dela, como no desenho.</summary>
        static int ThumbHeightFor(int width) => Math.Max(1, width - 10) * 9 / 16;
        int CaptionHeight => Font.Height + 16;
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Space or Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = e.SuppressKeyPress = true; }
            base.OnKeyDown(e);
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var card = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            if (Compact) { PaintCompact(g, card); return; }
            using (var cardPath = Rounded(card, 10))
            {
                using (var bg = new SolidBrush(Theme.SurfaceInput)) g.FillPath(bg, cardPath);
                using var pen = new Pen(_selected ? Theme.Accent : _hover || Focused ? Theme.NeutralHover : Theme.Neutral, _selected ? 3 : 2);
                g.DrawPath(pen, cardPath);
            }
            var thumbRect = new Rectangle(card.X + 4, card.Y + 4, Math.Max(1, card.Width - 8), Math.Max(1, card.Width - 8) * 9 / 16);
            using (var round = Rounded(thumbRect, 7))
            {
                using (var bg = new SolidBrush(Theme.SurfaceRaised)) g.FillPath(bg, round);
                if (_image != null) { g.SetClip(round); g.DrawImage(_image, thumbRect); g.ResetClip(); }
            }
            DrawCaption(g, thumbRect);
            if (_selected) DrawCheck(g, thumbRect);
        }

        void PaintCompact(Graphics g, Rectangle card)
        {
            using (var path = Rounded(card, 10))
            {
                using (var bg = new SolidBrush(_selected ? Theme.CardSelected : _hover ? Theme.SurfaceRaised : Theme.SurfaceInput)) g.FillPath(bg, path);
                using var pen = new Pen(_selected ? Theme.CardSelectedBorder : Theme.CardBorder, _selected ? 2.5f : 2);
                g.DrawPath(pen, path);
            }
            var icon = Source.IsWindow ? UiIcon.Window : UiIcon.Monitor;
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            if (Horizontal)
            {
                int x = card.X + 14;
                UiIcons.Draw(g, icon, new Rectangle(x, card.Y + (card.Height - IconSize) / 2, IconSize, IconSize), Theme.Text);
                TextRenderer.DrawText(g, Caption, Font, new Rectangle(x + IconSize + 12, card.Y, Math.Max(1, card.Right - x - IconSize - 22), card.Height), Theme.Text, flags | TextFormatFlags.Left);
            }
            else
            {
                UiIcons.Draw(g, icon, new Rectangle(card.X + (card.Width - IconSize) / 2, card.Y + 9, IconSize, IconSize), Theme.Text);
                TextRenderer.DrawText(g, Caption, Font, new Rectangle(card.X + 8, card.Y + 9 + IconSize + 2, Math.Max(1, card.Width - 16), Font.Height + 6), Theme.Text, flags | TextFormatFlags.HorizontalCenter);
            }
            if (_selected) DrawCheck(g, new Rectangle(card.X, card.Y, card.Width + 2, card.Height));
        }

        /// <summary>A legenda fica embaixo da miniatura, com o ícone de tela ou de janela e o nome em negrito.</summary>
        void DrawCaption(Graphics g, Rectangle thumb)
        {
            var row = new Rectangle(thumb.X, thumb.Bottom, thumb.Width, Math.Max(1, Height - 4 - thumb.Bottom));
            int icon = Font.Height;
            UiIcons.Draw(g, Source.IsWindow ? UiIcon.Window : UiIcon.Monitor, new Rectangle(row.X + 6, row.Y + (row.Height - icon) / 2, icon, icon), Theme.Text);
            using var bold = new Font(Font, FontStyle.Bold);
            TextRenderer.DrawText(g, Caption, bold, new Rectangle(row.X + 6 + icon + 8, row.Y, Math.Max(1, row.Width - icon - 20), row.Height), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }

        /// <summary>O selo de "esta é a fonte" no canto de cima à direita.</summary>
        static void DrawCheck(Graphics g, Rectangle thumb)
        {
            var badge = new Rectangle(thumb.Right - 26, thumb.Y + 6, 20, 20);
            using (var fill = new SolidBrush(Theme.Accent)) g.FillEllipse(fill, badge);
            UiIcons.Draw(g, UiIcon.Check, Rectangle.Inflate(badge, -4, -4), Color.White);
        }

        static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _image?.Dispose(); _image = null; }
            base.Dispose(disposing);
        }
    }
}
