using Streemu.Media;
using Streemu.Net;
using Streemu.Ui.Multistream;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// Barra de controles do player (mudo, volume, qualidade, tela cheia, fechar) sobre a janela do ffplay.
/// O ffplay desenha com a placa e não aceita ser filho de outra janela (plano 002, spike), então a barra é uma janela
/// à parte, DONA da dele (some e minimiza junto, nunca fica atrás) e sem roubar o foco (o teclado do ffplay, como F e Esc,
/// continua funcionando). Ela fica por cima do fim do vídeo, só aparece com o mouse mexendo sobre ele (ou com a roda e o M) e some depois de 2 s parado.
/// Uso: <c>PlayerOverlay.ShowFor(viewer, () =&gt; "1080p 60 FPS", () =&gt; RosterEntry.FromWatchers(room, streamerId, watcherIds));</c> logo depois de <c>viewer.Start(...)</c>.
/// </summary>
public sealed class PlayerOverlay : Form
{
    const int BarHeightAt96Dpi = 44;
    const int WsExNoActivate = 0x08000000, WsExToolWindow = 0x80;
    const string GlyphVolume = UiGlyphs.Volume, GlyphMuted = UiGlyphs.Muted, GlyphFullscreen = UiGlyphs.Fullscreen, GlyphClose = UiGlyphs.Close;
    const string GlyphSmall = UiGlyphs.SmallWindow, GlyphNormal = UiGlyphs.NormalWindow;
    /// <summary>
    /// Sair da tela cheia usa o \u00EDcone de recolher (E73F), n\u00E3o o de expandir: com o mesmo desenho nos dois estados, a \u00FAnica sa\u00EDda da tela cheia do palco
    /// parecia "entrar de novo" (avalia\u00E7\u00E3o da Tela 16, 13/11). O texto vai na dica e no nome acess\u00EDvel, porque o glifo sozinho n\u00E3o diz nada.
    /// </summary>
    const string GlyphExitFullscreen = UiGlyphs.ExitFullscreen;
    /// <summary>Spec 002 (B5): os controles somem 2 s depois do último movimento do mouse.</summary>
    static readonly TimeSpan HideAfter = TimeSpan.FromSeconds(2);

    readonly Viewer _viewer;
    readonly Func<string> _quality;
    readonly Func<IReadOnlyList<RosterEntry>> _people;
    readonly RosterStrip _roster = new() { BackColor = Theme.SurfaceInput, Margin = Padding.Empty };
    readonly FlowLayoutPanel _center = Zone(), _right = Zone();
    readonly RosterPopup _popup = new();
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 100 };
    readonly Button _mute = IconButton(GlyphVolume), _fullscreen = IconButton(GlyphFullscreen), _close = IconButton(GlyphClose), _mini = IconButton(GlyphSmall);
    readonly Button _emphasis = new() { Text = "Destacar", FlatStyle = FlatStyle.Flat, BackColor = Theme.SurfaceInput, ForeColor = Theme.Text, Cursor = Cursors.Hand, TabStop = false, Margin = new Padding(2, 6, 2, 0), Font = UiTypography.Create(UiTypography.Caption), Visible = false };
    readonly ToolTip _tip = new() { InitialDelay = 300 };
    readonly IMiniPlayerStore? _store;
    readonly Action<string>? _notify;
    readonly PlayerInput _input;
    readonly IDisposable _hotkey;
    readonly PlayerStage? _stage;
    readonly Button _liveButton = IconButton(UiGlyphs.Sidebar);
    /// <summary>Bandeira vermelha "Denunciar esta live" (spec 029, E3): desenhada em vetor (o Segoe MDL2 não tem bandeira cheia), só com um <see cref="ReportHook"/>.</summary>
    readonly Button _report = IconButton("");
    readonly ReportHook? _reportHook;
    bool _reportAvailable = true, _reportHover;
    readonly LiveHeaderOverlay _header = new(); // com uma live só no palco: cabeçalho por cima do vídeo, que some junto com a barra
    /// <summary>Teste (STREEMU_TEST_PANEL=1): abre o painel "Ao vivo agora" sozinho assim que o vídeo aparece.</summary>
    bool _openPanelForTest = Environment.GetEnvironmentVariable("STREEMU_TEST_PANEL") == "1";
    IntPtr _focusBack, _focusBackFromWindow;
    bool _small;
    IntPtr _topmostFor, _shapedFor;
    bool _smallByReopen;
    Rectangle? _normalBounds, _smallRect;
    Point _dragFrom, _dragWindowFrom;
    bool _dragging, _dragMoved;
    readonly VolumeSlider _volume = new() { Width = 110, Height = 32, Margin = new Padding(4, 6, 4, 0), TabStop = false }; // sem foco: o teclado continua com o ffplay
    readonly Label _percent = Caption(58), _badge = Caption(0);
    IntPtr _owner;
    bool? _fullscreenShown;
    Point _lastCursor;
    DateTime _lastMove = DateTime.MinValue;

    /// <summary>
    /// A denúncia desta live, entregue por quem sabe de quem ela é (o <c>MainForm</c>): <see cref="Target"/> diz grupo e live (nulo = não dá agora), <see cref="Open"/> abre a janela
    /// e entrega a função que tira a imagem (nulo se não há player para capturar; só é chamada depois do Enviar) e <see cref="Notice"/> mostra um aviso curto SEM escrever no log
    /// (spec 029, 15.16: o <c>notify</c> normal loga tudo que mostra).
    /// </summary>
    public sealed record ReportHook(Func<DenunciaAlvo?> Target, Action<DenunciaAlvo, Func<byte[]?>?> Open, Action<string> Notice);

    /// <summary>Cria a barra para este player; ela se fecha sozinha quando o player fecha. <paramref name="report"/> liga o botão de denunciar (a prévia da própria live não tem).</summary>
    public static PlayerOverlay ShowFor(Viewer viewer, Func<string> quality, Func<IReadOnlyList<RosterEntry>> people, IMiniPlayerStore? mini = null, Action<string>? notify = null, PlayerStage? stage = null, ReportHook? report = null)
    {
        var overlay = new PlayerOverlay(viewer, quality, people, mini, notify, stage, report);
        viewer.Exited += () => { if (overlay.IsHandleCreated) overlay.BeginInvoke(overlay.Close); };
        return overlay;
    }

    PlayerOverlay(Viewer viewer, Func<string> quality, Func<IReadOnlyList<RosterEntry>> people, IMiniPlayerStore? mini, Action<string>? notify, PlayerStage? stage, ReportHook? report)
    {
        _reportHook = report;
        _store = mini;
        _notify = notify;
        _stage = stage;
        _viewer = viewer;
        _input = new PlayerInput(viewer.Audio, () => Cursor.Position, () => viewer.Window.TryGetBounds(out var b) ? b : null,
                                 p => _popup.Visible && _popup.Bounds.Contains(p), // sobre a lista de pessoas a roda é dela
                                 () => [viewer.Window.Handle, IsHandleCreated ? Handle : IntPtr.Zero, _header.Handle]);
        _quality = quality;
        _people = people;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.SurfaceInput;
        Opacity = 0.93;
        BuildLayout();
        WireEvents();
        _hotkey = PlayerHotkey.Attach(ToggleSmallFromHotkey, text => { Log.Info(text); notify?.Invoke(text); });
        _ = Handle; // cria a janela escondida: o relógio roda sem ela aparecer
        _tick.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExNoActivate | WsExToolWindow;
            return cp;
        }
    }

    static Button IconButton(string glyph) => new()
    {
        Text = glyph, Font = UiTypography.CreateSymbols(UiTypography.Symbol), Width = 40, Height = 32, FlatStyle = FlatStyle.Flat,
        BackColor = Theme.SurfaceInput, ForeColor = Theme.Text, Cursor = Cursors.Hand, Margin = new Padding(2, 6, 2, 0), TabStop = false,
    };

    static Label Caption(int width) => new()
    {
        AutoSize = false, Width = width, Height = 32, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.TextMuted,
        Font = UiTypography.Create(UiTypography.Caption), Margin = new Padding(4, 6, 4, 0),
    };

    void BuildLayout()
    {
        // tamanhos em pixels de 96 DPI viram pixels reais (a barra abre em monitores com escala de 125% a 200%)
        foreach (var b in new[] { _mute, _fullscreen, _close, _mini, _liveButton, _report }) { b.Width = LogicalToDeviceUnits(40); b.Height = LogicalToDeviceUnits(32); }
        _volume.Width = LogicalToDeviceUnits(110); _volume.Height = LogicalToDeviceUnits(32);
        _percent.Width = LogicalToDeviceUnits(52); _badge.Width = LogicalToDeviceUnits(150);
        _percent.Height = _badge.Height = LogicalToDeviceUnits(32);
        _mute.FlatAppearance.BorderSize = _fullscreen.FlatAppearance.BorderSize = _close.FlatAppearance.BorderSize = _mini.FlatAppearance.BorderSize = _liveButton.FlatAppearance.BorderSize = _report.FlatAppearance.BorderSize = 0;
        // só ícones, com o texto na dica (pedido de 2026-11-16: "Barra", "Tela cheia" e "Sair da live" escritos poluíam a barra); sair continua vermelho
        _close.ForeColor = Theme.Error;
        _close.FlatAppearance.MouseOverBackColor = Theme.Error;
        _close.MouseEnter += (_, _) => _close.ForeColor = Theme.AccentText;
        _close.MouseLeave += (_, _) => _close.ForeColor = Theme.Error;
        Describe(_mini, MiniTip);
        Describe(_close, _viewer.IsPreview ? "Fechar a prévia" : "Parar de assistir esta live");
        Describe(_liveButton, "Mostrar ou esconder a barra lateral");
        // bandeira vermelha, como o "sair": no mouse por cima o fundo fica vermelho e o desenho branco; a prévia da própria live (sem dono para denunciar) não tem o botão
        _report.Visible = _reportHook != null && !_viewer.IsPreview;
        _report.FlatAppearance.MouseOverBackColor = Theme.Error;
        _report.MouseEnter += (_, _) => { _reportHover = true; _report.Invalidate(); };
        _report.MouseLeave += (_, _) => { _reportHover = false; _report.Invalidate(); };
        _report.Paint += (_, e) => PaintFlag(e.Graphics);
        Describe(_report, "Denunciar esta live");
        _volume.AccessibleName = "Volume";
        _volume.BackColor = Theme.SurfaceInput;
        _right.Padding = new Padding(0, 0, LogicalToDeviceUnits(8), 0);
        // a prévia da própria live é sem som (o jogo já toca no PC): sem mudo nem volume
        _center.Controls.AddRange(_viewer.IsPreview ? new Control[] { _badge } : new Control[] { _mute, _volume, _percent, _badge });
        _liveButton.Visible = _stage != null;
        _emphasis.Width = LogicalToDeviceUnits(80); _emphasis.Height = LogicalToDeviceUnits(32); _emphasis.FlatAppearance.BorderSize = 0;
        _right.Controls.AddRange(new Control[] { _emphasis, _liveButton, _mini, _fullscreen, _report, _close });
        Controls.AddRange(new Control[] { _roster, _center, _right });
        RefreshAudio();
        RefreshFullscreen();
        LayoutZones();
    }

    /// <summary>Dica e nome acessível do mesmo texto: os botões de ícone não têm rótulo visível.</summary>
    void Describe(Control control, string text)
    {
        if (control.AccessibleName == text) return;
        control.AccessibleName = text;
        _tip.SetToolTip(control, text);
    }

    /// <summary>
    /// O que o botão de tela cheia faz agora. No palco ele vale para o palco inteiro (o estado é conhecido); fora dele, aperta o F do ffplay, que alterna
    /// sem avisar o app, então a dica fala em alternar.
    /// </summary>
    public static (string Glyph, string Text) FullscreenLook(bool onStage, bool stageFullscreen) =>
        !onStage ? (GlyphFullscreen, "Tela cheia ou voltar à janela (F)")
        : stageFullscreen ? (GlyphExitFullscreen, "Sair da tela cheia")
        : (GlyphFullscreen, "Tela cheia");

    /// <summary>Troca o ícone e a dica quando o palco entra ou sai da tela cheia (comparado a cada ciclo; só mexe no botão quando muda).</summary>
    void RefreshFullscreen()
    {
        bool onStage = _stage is { Active: true }, full = onStage && _stage!.Fullscreen;
        bool? now = onStage ? full : null;
        if (_fullscreenShown == now && _fullscreen.AccessibleName != null) return;
        _fullscreenShown = now;
        var (glyph, text) = FullscreenLook(onStage, full);
        _fullscreen.Text = glyph;
        Describe(_fullscreen, text);
        if (_center != null) LayoutZones();
    }

    /// <summary>Painel sem encaixe: quem posiciona é <see cref="LayoutZones"/>, para o centro ficar no meio da barra.</summary>
    static FlowLayoutPanel Zone() => new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_center != null) LayoutZones();
    }

    /// <summary>Faixa à esquerda; volume, qualidade e os botões juntos num grupo no meio da barra (conta em <see cref="BarLayout"/>).</summary>
    void LayoutZones()
    {
        ShedButtonsForWidth();
        _badge.Visible = true; // mede com o selo para saber quanto o centro ocupa por inteiro
        int full = _center.PreferredSize.Width, badge = _badge.Width + _badge.Margin.Horizontal;
        var z = BarLayout.Grouped(ClientSize.Width, full, full - badge, _right.PreferredSize.Width, LogicalToDeviceUnits(8));
        _badge.Visible = z.ShowQuality;
        _center.Location = new Point(z.CenterX, (ClientSize.Height - _center.Height) / 2);
        _right.Location = new Point(z.RightX, (ClientSize.Height - _right.Height) / 2);
        _roster.Visible = z.RosterWidth >= LogicalToDeviceUnits(40);
        _roster.SetBounds(z.RosterX, 0, z.RosterWidth, ClientSize.Height);
    }

    /// <summary>
    /// Barra estreita (vídeo estreito, como ao lado da coluna do Transmitir): sem lugar para tudo, os botões secundários saem na ordem barra lateral, janela pequena
    /// e tela cheia; sair da live e o volume ficam sempre. Voltam sozinhos quando a barra alarga.
    /// </summary>
    void ShedButtonsForWidth()
    {
        var optional = new Control[] { _liveButton, _mini, _fullscreen };
        _liveButton.Visible = _stage != null; _mini.Visible = true; _fullscreen.Visible = true;
        int needed = LogicalToDeviceUnits(230); // volume e um pouco de folga
        foreach (var button in optional)
        {
            if (ClientSize.Width <= 0 || _right.PreferredSize.Width + needed <= ClientSize.Width) break;
            button.Visible = false;
        }
    }

    void WireEvents()
    {
        _tick.Tick += (_, _) => OnTick();
        if (_stage != null) _stage.Placed += FollowStage;
        _mute.Click += (_, _) => _viewer.Audio.ToggleMuted();
        _volume.ValueChanged += OnVolumeDragged;
        _fullscreen.Click += (_, _) => { if (_stage is { Active: true }) _stage.ToggleFullscreen(); else _viewer.Window.PressKey(Keys.F); };
        _mini.Click += (_, _) => ToggleSmall();
        _liveButton.Click += (_, _) => ToggleLiveNow();
        _report.Click += (_, _) => OnReport();
        _emphasis.Click += (_, _) => ToggleEmphasis();
        WireDrag(this);
        _close.Click += (_, _) => _viewer.Stop();
        _roster.OverflowClicked += TogglePeopleList;
        _viewer.Audio.Changed += RefreshAudio;
        _input.Used += () => _lastMove = DateTime.Now; // roda ou M sem mexer o mouse: a barra aparece para mostrar o volume novo
        FormClosed += (_, _) => { if (_stage != null) _stage.Placed -= FollowStage; _tick.Stop(); _viewer.Audio.Changed -= RefreshAudio; _popup.Dispose(); _tip.Dispose(); _input.Dispose(); _hotkey.Dispose(); _header.Dispose(); };
    }

    void OnVolumeDragged(int percent)
    {
        _viewer.Audio.SetPercent(percent);
        if (_viewer.Audio.Muted && percent > 0) _viewer.Audio.SetMuted(false); // arrastar desmuta, como no Discord
    }

    void RefreshAudio()
    {
        var audio = _viewer.Audio;
        _mute.Text = audio.Muted ? GlyphMuted : GlyphVolume;
        _mute.ForeColor = audio.Muted ? Theme.Error : Theme.Text;
        _volume.Muted = audio.Muted;
        _volume.Value = audio.Percent;
        _percent.Text = audio.Muted ? "mudo" : $"{audio.Percent}%";
        Describe(_mute, audio.Muted ? "Ativar o som (M)" : "Silenciar (M)");
    }

    void OnTick()
    {
        bool stageHidden = !_small && _stage is { HostHidden: true };
        if (stageHidden || !VideoRect(out var video) || _viewer.Window.IsMinimized || _viewer.Window.IsHidden) { HideBar(); _header.Place(null, false, IntPtr.Zero); return; }
        FollowOwner();
        KeepSmallOnTop();
        KeepSmallShape();
        KeepLiveNow();
        GiveFocusBack();
        _input.Update(); // roda e M só valem com o mouse sobre o player (plano 002, T6)
        var cursor = Cursor.Position;
        bool onList = _popup.Visible && _popup.Bounds.Contains(cursor);
        if (onList) _lastMove = DateTime.Now; // rolando a lista com a roda: não some no meio
        bool onVideo = video.Contains(cursor), onBar = Visible && (Bounds.Contains(cursor) || onList);
        if (cursor != _lastCursor && (onVideo || onBar)) _lastMove = DateTime.Now;
        _lastCursor = cursor;
        bool recent = DateTime.Now - _lastMove < HideAfter || _volume.Capture || _dragging;
        // Barra sempre à vista numa faixa embaixo do vídeo (eab521a, 2026-11-15) tirava a imersão (2026-11-16): voltou a sumir também no palco.
        if ((onVideo || onBar) && recent) ShowBar(video); else HideBar();
        _header.Place(HeaderCaption, Visible, _viewer.Window.Handle, Handle);
    }

    /// <summary>Cabeçalho por cima do vídeo: só com esta live sozinha no palco (com várias, cada uma tem o seu numa faixa do palco).</summary>
    StageCaption? HeaderCaption => _small ? null : _stage?.HeaderOver(_viewer);

    /// <summary>
    /// Onde está o vídeo. Na janela de streams vale o lugar que o palco acabou de dar: o ffplay muda de posição de forma assíncrona e ler a janela dele
    /// logo depois devolve a posição de antes (a barra ficava para trás quando a janela do app era arrastada).
    /// </summary>
    bool VideoRect(out Rectangle video)
    {
        if (!_small && _stage?.TargetOf(_viewer) is { } target) { video = target; return true; }
        return _viewer.Window.TryGetClientBounds(out video);
    }

    /// <summary>Onde a barra fica agora: no fim do vídeo, ou logo acima da barra de tarefas do Windows quando ela está à vista por cima do fim do vídeo.</summary>
    Rectangle BarBounds(Rectangle video)
    {
        var bar = BarRect(video, DeviceDpi);
        var probe = new Point(video.X + video.Width / 2, video.Bottom - 2);
        if (TaskbarCover.At(probe) is not { } taskbar || taskbar.Top <= video.Y + video.Height / 2) return bar;
        return bar with { Y = Math.Max(video.Y, taskbar.Top - bar.Height) };
    }

    /// <summary>
    /// A barra fica por cima do fim do vídeo, no palco e na janela pequena, e nunca mais alta que ele: num vídeo 32:9 de peça pequena do destaque ela
    /// passava do alto do vídeo e cobria a faixa do nome (bateria de estresse, 2026-11-20). Ex.: <c>PlayerOverlay.BarRect(video, 144)</c>.
    /// </summary>
    public static Rectangle BarRect(Rectangle video, int dpi)
    {
        int height = Math.Min(video.Height, (int)(BarHeightAt96Dpi * dpi / 96f));
        return new Rectangle(video.X, video.Bottom - height, video.Width, height);
    }

    /// <summary>O palco mexeu nos vídeos (janela arrastada, redimensionada, barra lateral deslizando): a barra, se está à vista, vai junto na hora.</summary>
    void FollowStage()
    {
        _header.Place(HeaderCaption, Visible, _viewer.Window.Handle, Handle);
        if (!Visible || !VideoRect(out var video)) return;
        var bounds = BarBounds(video);
        if (Bounds != bounds) Bounds = bounds;
    }

    void FollowOwner() => PlayerWindowOrder.Follow(this, _viewer.Window.Handle, ref _owner);

    void ShowBar(Rectangle video)
    {
        Bounds = BarBounds(video);
        RefreshReport();
        _badge.Text = _quality();
        var people = _people();
        _roster.SetEntries(people);
        RefreshPeopleList(people);
        if (!Visible) Show();
        PlaceAbovePlayer();
    }

    /// <summary>Com a lista aberta: acompanha quem entra e sai, e fecha se o "+N" deixou de existir (todos cabem na faixa).</summary>
    void RefreshPeopleList(IReadOnlyList<RosterEntry> people)
    {
        if (!_popup.Visible) return;
        if (_roster.Hidden == 0) _popup.Hide();
        else _popup.ShowAbove(this, _roster.RectangleToScreen(_roster.ChipBounds), people);
    }

    void TogglePeopleList()
    {
        if (_popup.Visible) { _popup.Hide(); return; }
        _popup.ShowAbove(this, _roster.RectangleToScreen(_roster.ChipBounds), _people());
    }

    void PlaceAbovePlayer() => PlayerWindowOrder.PlaceAbove(this, _viewer.Window.Handle, _header.Handle);

    // ------------------------------------------------------------------ denunciar a live (spec 029, E3)

    /// <summary>
    /// Sem grupo ou live para denunciar (a live acabou de sair da lista, ou a sala não sabe de quem é), a bandeira fica apagada e a dica diz isso. O botão continua clicável
    /// de propósito: um botão desabilitado do WinForms não mostra dica, e o clique repete o motivo num aviso.
    /// </summary>
    void RefreshReport()
    {
        if (_reportHook == null || !_report.Visible) return;
        bool available = _reportHook.Target() != null;
        if (available == _reportAvailable && _report.AccessibleName != null) return;
        _reportAvailable = available;
        Describe(_report, available ? "Denunciar esta live" : Denuncia.SemAlvo);
        _report.Invalidate();
    }

    /// <summary>
    /// Nada é capturado ao clicar: a janela abre sem imagem e recebe a função de captura, que ela só chama depois do Enviar (spec 029, 10: em abuso infantil nenhuma imagem é tirada).
    /// Sem janela de player (nem ffplay) não há o que capturar e a janela abre sem a caixa da imagem.
    /// </summary>
    void OnReport()
    {
        if (_reportHook == null) return;
        if (_reportHook.Target() is not { } alvo) { _reportHook.Notice(Denuncia.SemAlvo); return; }
        _reportHook.Open(alvo, _viewer.Window.Handle != IntPtr.Zero ? CapturarPlayer : null);
    }

    /// <summary>Esconde a barra e o cabeçalho, tira a imagem do player e mostra a barra de novo (ela só voltaria sozinha quando o mouse mexesse).</summary>
    byte[]? CapturarPlayer()
    {
        var window = _viewer.Window.Handle;
        if (window == IntPtr.Zero) return null;
        _viewer.Window.TryGetClientBounds(out var video);
        try { return DenunciaCaptura.Capturar(window, video, () => { HideBar(); _header.Place(null, false, IntPtr.Zero); }); }
        finally { RestoreBarAfterCapture(); }
    }

    void RestoreBarAfterCapture()
    {
        _lastMove = DateTime.Now; // conta como mouse recente: a barra volta e some pelo tempo de sempre
        if (IsDisposed) return;
        if (VideoRect(out var video)) ShowBar(video);
        _header.Place(HeaderCaption, Visible, _viewer.Window.Handle, Handle);
    }

    /// <summary>A bandeira (cabo e bandeirola cheia, na grade de 24 do <see cref="UiIcons"/>), vermelha; branca com o mouse por cima, apagada quando não dá para denunciar.</summary>
    void PaintFlag(Graphics g)
    {
        int size = LogicalToDeviceUnits(18);
        var color = !_reportAvailable ? Theme.TextMuted : _reportHover ? Theme.AccentText : Theme.Error;
        var saved = g.Save();
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TranslateTransform((_report.Width - size) / 2f, (_report.Height - size) / 2f);
        g.ScaleTransform(size / 24f, size / 24f);
        using var pen = new Pen(color, 2f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        using var brush = new SolidBrush(color);
        g.DrawLine(pen, 5, 3, 5, 22);
        g.FillPolygon(brush, [new PointF(5, 4), new PointF(20, 4), new PointF(16.5f, 9), new PointF(20, 14), new PointF(5, 14)]);
        g.Restore(saved);
    }

    // ------------------------------------------------------------------ painel "Ao vivo agora" (spec 006, M5)

    /// <summary>
    /// Abre ou fecha o painel. Quem posiciona o painel e redimensiona os vídeos é a <see cref="PlayerStage"/> (a janela de streams): o painel ocupa uma
    /// faixa própria e o vídeo encolhe para o resto, então nada fica por cima de nada. Na janela pequena não há painel.
    /// </summary>
    void ToggleLiveNow()
    {
        if (_stage == null || _small) return;
        _stage.TogglePanel();
    }

    void KeepLiveNow()
    {
        if (_openPanelForTest && _stage != null && _viewer.Window.TryGetBounds(out _)) { _openPanelForTest = false; _stage.OpenPanel(); }
        _liveButton.ForeColor = _stage is { PanelOpen: true } ? Theme.Accent : Theme.Text;
        bool several = _stage is { Count: >= 2 };
        if (_emphasis.Visible != several) { _emphasis.Visible = several; LayoutZones(); }
        bool mine = _stage?.IsEmphasized(_viewer) == true && _stage.EmphasisShown; // pedido sem lugar na janela: a tela é a grade, o botão não diz "Grade"
        _emphasis.ForeColor = mine ? Theme.Accent : Theme.Text;
        _emphasis.Text = mine ? "Grade" : "Destacar";
        Describe(_emphasis, mine ? "Voltar à grade com todas do mesmo tamanho" : "Deixar esta live maior que as outras");
        RefreshFullscreen();
    }

    /// <summary>
    /// Destaca esta live (ou volta à grade). Sem lugar para as outras continuarem assistíveis, o palco fica na grade e guarda o pedido: avisa o porquê,
    /// senão o clique pareceria não fazer nada.
    /// </summary>
    void ToggleEmphasis()
    {
        if (_stage == null) return;
        _stage.ToggleEmphasis(_viewer);
        if (_stage.IsEmphasized(_viewer) && !_stage.EmphasisShown)
            _notify?.Invoke("Não há espaço para destacar esta live nesta janela: aumente a janela ou diminua a escala da interface (Ctrl -).");
    }

    // ------------------------------------------------------------------ janela pequena (plano 002, T5)

    /// <summary>
    /// Alterna entre a janela normal e a pequena. A pequena é o ffplay REABERTO num tamanho menor (ele não escala por fora), no canto
    /// da tela, sempre por cima, arrastável pela barra; lembra onde ficou. A imagem some por alguns segundos até o próximo quadro-chave.
    /// </summary>
    /// <summary>A dica do botão mostra a tecla que vale NESTE PC (o Ctrl+Alt+M pode estar tomado e o atalho ter caído para J ou K).</summary>
    string MiniTip => (_small ? "Voltar ao tamanho normal" : "Janela pequena") + (PlayerHotkey.Current is { } k ? $" ({k})" : "");

    void ToggleSmall()
    {
        if (!_small && _viewer.SmallWindowGuard?.Invoke() is { } why) { Log.Info(why); _notify?.Invoke(why); return; }
        if (_stage is { Count: >= 2 } && !_small) return; // com várias streams o palco manda no tamanho de cada uma
        if (_small) RememberPosition();
        _small = !_small;
        _mini.Text = _small ? GlyphNormal : GlyphSmall;
        Describe(_mini, MiniTip);
        _topmostFor = IntPtr.Zero;
        _stage?.SetSmall(_viewer, _small); // a janela pequena sai do palco (e volta quando a normal volta)
        var target = _small ? MiniPlayerPlacement.Choose(_store?.Load(), Screen.AllScreens.Select(s => s.WorkingArea), Cursor.Position, DeviceDpi / 96.0) : (Rectangle?)null;
        if (target is { } t && _viewer.VideoAspect is { } aspect) target = MiniPlayerPlacement.MatchAspect(t, aspect);
        bool canResize = _viewer.Window.Handle != IntPtr.Zero && !_viewer.Reopening;
        if (!canResize || (!_small && _smallByReopen)) ToggleByReopen(target);
        else ToggleByResize(target);
    }

    /// <summary>Sem janela ainda, ou ela veio sem moldura de uma reabertura: o único jeito é reabrir o ffplay (a imagem some até o próximo quadro-chave).</summary>
    void ToggleByReopen(Rectangle? target)
    {
        _smallByReopen = _small;
        _viewer.Borderless = _small;
        _viewer.Reopen(target);
    }

    /// <summary>
    /// Troca de tamanho SEM reabrir o ffplay (spec 006, M3): a moldura sai por estilo e a janela é redimensionada por fora, então a imagem
    /// continua e quem transmite nem percebe (antes, reabrir derrubava a conexão por alguns segundos).
    /// </summary>
    void ToggleByResize(Rectangle? target)
    {
        _smallByReopen = false;
        if (_small && target is { } r)
        {
            _viewer.Window.TryGetBounds(out var normal);
            _normalBounds = MiniPlayerPlacement.LooksFullscreen(normal, Screen.AllScreens.Select(s => s.WorkingArea)) ? null : normal;
            _smallRect = r;
            ApplySmallShape(r);
            return;
        }
        _smallRect = null;
        _viewer.Window.SetFrame(true);
        _viewer.Window.SetBounds(_normalBounds ?? DefaultNormalBounds());
    }

    void ApplySmallShape(Rectangle r)
    {
        _viewer.Window.SetFrame(false);
        _viewer.Window.SetBounds(r);
        _shapedFor = _viewer.Window.Handle;
    }

    static Rectangle DefaultNormalBounds()
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        return new Rectangle(area.X + (area.Width - 1280) / 2, area.Y + (area.Height - 720) / 2, 1280, 720);
    }

    /// <summary>
    /// Mantém a janela pequena do jeito certo: sair da tela cheia do ffplay devolve a moldura (medido: estilo 0x16CF0000, 662x416), e um
    /// ffplay novo (volta para a CPU) abre normal. Nos dois casos tira a moldura e põe de volta onde estava.
    /// </summary>
    void KeepSmallShape()
    {
        var hwnd = _viewer.Window.Handle;
        if (!_small || _smallByReopen || _smallRect is not { } r || hwnd == IntPtr.Zero) return;
        // o ffplay só diz o tamanho do vídeo depois de abrir: aí a janela pequena passa para a proporção dele (sem esticar a imagem)
        if (_viewer.VideoAspect is { } aspect && MiniPlayerPlacement.MatchAspect(r, aspect) is var fit && fit != r
            && _viewer.Window.TryGetBounds(out var current) && !MiniPlayerPlacement.LooksFullscreen(current, Screen.AllScreens.Select(s => s.WorkingArea)))
        {
            _smallRect = fit;
            ApplySmallShape(fit);
            return;
        }
        if (hwnd == _shapedFor && !_viewer.Window.HasFrame) return;
        if (_viewer.Window.TryGetBounds(out var now) && MiniPlayerPlacement.LooksFullscreen(now, Screen.AllScreens.Select(s => s.WorkingArea)) && hwnd == _shapedFor) return;
        ApplySmallShape(r);
    }

    /// <summary>
    /// Atalho global: alterna a janela e DEVOLVE o foco a quem tinha (o jogo). O ffplay novo abre pegando o foco; sem isto o jogo
    /// perderia o teclado a cada troca (spec 002, B11). O atalho conta como entrada do usuário, então o Windows deixa devolver.
    /// </summary>
    void ToggleSmallFromHotkey()
    {
        _focusBack = Native.GetForegroundWindow();
        _focusBackFromWindow = _viewer.Window.Handle;
        ToggleSmall();
    }

    /// <summary>Quando a janela nova do ffplay aparece, devolve o foco a quem estava em primeiro plano ao apertar o atalho.</summary>
    void GiveFocusBack()
    {
        var hwnd = _viewer.Window.Handle;
        if (_focusBack == IntPtr.Zero || hwnd == IntPtr.Zero || hwnd == _focusBackFromWindow) return;
        if (Native.GetForegroundWindow() != _focusBack) PlayerHotkey.RestoreForeground(_focusBack);
        _focusBack = IntPtr.Zero;
    }

    /// <summary>A janela nova do ffplay nasce sem "sempre por cima": aplica quando ela aparece (e de novo se o player trocar de janela).</summary>
    void KeepSmallOnTop()
    {
        var hwnd = _viewer.Window.Handle;
        if (!_small || hwnd == IntPtr.Zero || hwnd == _topmostFor) return;
        _viewer.Window.SetTopMost(true);
        _topmostFor = hwnd;
    }

    void RememberPosition()
    {
        if (_store == null || !_viewer.Window.TryGetBounds(out var bounds)) return;
        if (MiniPlayerPlacement.LooksFullscreen(bounds, Screen.AllScreens.Select(s => s.WorkingArea))) return; // em tela cheia não há o que lembrar
        _store.Save(bounds);
    }

    /// <summary>Na janela pequena, arrastar a barra (fora dos botões) move o ffplay, e a barra vai junto.</summary>
    void WireDrag(Control control)
    {
        if (control is Button or VolumeSlider) return;
        control.MouseDown += (_, e) => BeginDrag(e);
        control.MouseMove += (_, e) => ContinueDrag(e);
        control.MouseUp += (_, e) => EndDrag(e);
        foreach (Control child in control.Controls) WireDrag(child);
    }

    void BeginDrag(MouseEventArgs e)
    {
        if (!_small || e.Button != MouseButtons.Left || !_viewer.Window.TryGetBounds(out var bounds)) return;
        _dragging = true;
        _dragMoved = false;
        _dragFrom = Cursor.Position;
        _dragWindowFrom = bounds.Location;
    }

    void ContinueDrag(MouseEventArgs e)
    {
        if (!_dragging) return;
        var now = Cursor.Position;
        if (now == _dragFrom && !_dragMoved) return;
        _dragMoved = true;
        _viewer.Window.MoveTo(new Point(_dragWindowFrom.X + now.X - _dragFrom.X, _dragWindowFrom.Y + now.Y - _dragFrom.Y));
        _lastMove = DateTime.Now;
        OnTick(); // a barra acompanha na hora, sem esperar o próximo ciclo
    }

    void EndDrag(MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        if (_dragMoved) { RememberPosition(); if (_viewer.Window.TryGetBounds(out var moved)) _smallRect = moved; } // um clique sem arrastar não grava nada
    }

    void HideBar()
    {
        _popup.Hide();
        if (Visible) Hide();
    }
}
