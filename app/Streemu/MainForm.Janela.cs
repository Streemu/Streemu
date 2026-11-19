using Streemu.Net;
using Streemu.Ui;
using Streemu.Ui.Multistream;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// A janela única (spec 006, modelo A + C, 2026-11-09): a janela principal tem a barra lateral à esquerda (aberta na home, recolhida em trilho de avatares
/// quando há stream) e, à direita, ou a home ou o palco com os vídeos. Os vídeos são janelas do ffplay dependentes desta janela, postas sobre o palco
/// pelo <see cref="PlayerStage"/>; quem transmite (host) não muda em nada. Fica fora do MainForm.Grupo.cs para dar menos conflito.
/// </summary>
public sealed partial class MainForm : IStageHost
{
    readonly SidePanel _side = new();
    readonly StageCanvas _stageCanvas = new() { Dock = DockStyle.Fill, Visible = false };
    readonly LiveCardsView _liveCards = new() { Width = 360, Margin = new Padding(0, 6, 0, 6) };
    readonly BufferedPanel _homeHost = new() { Dock = DockStyle.Fill };
    readonly BufferedPanel _settingsHost = new() { Dock = DockStyle.Fill, Visible = false };
    bool _stageView, _settingsView, _fullscreenStage, _sideWasCollapsed;
    bool _settingsRailActive, _sideBeforeSettings;
    int _lastStreamCount;
    FormBorderStyle _borderBeforeFullscreen;
    FormWindowState _stateBeforeFullscreen;
    bool _thumbsAsked;
    DateTime _nextThumbAsk = DateTime.MinValue;
    SideFlyout? _flyout;
    readonly CheckBox _pinSidebar = new Ui.ToggleSwitch { Text = "Manter a barra lateral sempre aberta", AutoSize = true };
    readonly CheckBox _hoverSidebar = new Ui.ToggleSwitch { Text = "Abrir a barra ao passar o mouse", AutoSize = true };
    bool SidebarPinned => _state.SidebarPinned;
    bool SidebarHover => _state.SidebarHover;
    readonly BufferedPanel _gruposHost = new() { Dock = DockStyle.Fill, Visible = false };
    readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 80 };
    DateTime _hoverSince = DateTime.MinValue;

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    /// <summary>Monta a janela: o início (os controles de sempre), o palco e a barra lateral. A ordem de adição importa para o encaixe (Fill primeiro).</summary>
    void BuildShell(Control home, Control settings)
    {
        Text = UiBrand.Name + (_localMode ? " (MODO LOCAL)" : "");
        _homeHost.Controls.Add(home);
        _settingsHost.Controls.Add(settings);
        Controls.Add(_homeHost);
        Controls.Add(_settingsHost);
        Controls.Add(_filmeHost); // modo filme (MainForm.Filme.cs)
        Controls.Add(_arquivosHost); // enviar arquivo (MainForm.ArquivosTela.cs, spec 020)
        Controls.Add(_gruposHost); // grupos (MainForm.Grupos.cs)
        Controls.Add(_stageCanvas);
        Controls.Add(_side);
        _side.HomeClicked += () => ShowView(stage: false);
        _side.StageClicked += () => ShowView(stage: true);
        _side.SettingsClicked += ShowSettings;
        _side.TransmitClicked += OpenTransmission;
        _side.ProfileClicked += () => ShowSettingsAt("Perfil");
        _side.SelfClicked += ToggleMyLive; // MainForm.MinhaLive.cs
        _side.StopLiveClicked += () => EncerrarLive("barra lateral");
        _side.ToggleClicked += ToggleSidebar;
        _side.FriendClicked += friend => ToggleWatchFriend(friend.Id);
        LigarMenuAmigo(_side); // MainForm.MenuAmigo.cs
        _stageCanvas.CaptionDragged += (from, at, done) => CaptionDragged?.Invoke(from, at, done);
        _liveCards.Picked += PickLiveCard;
        _side.Slid += FinishSlide;
        _side.SizeChanged += (_, _) => _playerStage?.Relayout(); // a barra desliza: os vídeos acompanham a cada passo
        InitPreparation();
        InitScale(); // Ctrl + / Ctrl - (MainForm.Escala.cs)
        InitMinhaLive(); // selo AO VIVO e opções (MainForm.MinhaLive.cs)
        InitFilme(); // modo filme (MainForm.Filme.cs)
        InitArquivosTela(); // enviar arquivo (MainForm.ArquivosTela.cs)
        _pinSidebar.Checked = _state.SidebarPinned;
        _pinSidebar.CheckedChanged += (_, _) => SetSidebarPinned(_pinSidebar.Checked);
        _hoverSidebar.Checked = _state.SidebarHover;
        _hoverSidebar.Enabled = !_state.SidebarPinned; // fixa aberta, a barra nunca vira trilho: o mouse não tem o que abrir
        _hoverSidebar.CheckedChanged += (_, _) => SetSidebarHover(_hoverSidebar.Checked);
        if (_state.SidebarPinned) _side.Collapsed = false;
        _hoverTimer.Tick += (_, _) => TickFlyout();
        _hoverTimer.Start();
        LocationChanged += (_, _) => _playerStage?.Relayout(); // os vídeos acompanham a janela
        SizeChanged += (_, _) => { FitSidebarToWidth(); _playerStage?.Relayout(); };
        VisibleChanged += (_, _) => _playerStage?.Relayout();
        Shown += (_, _) => { CorrigirOrdemDoEncaixe(); AjustarAlturaMinima(); };
        VigiarOrdemDoEncaixe();
        DpiChanged += (_, _) => BeginInvoke(AjustarAlturaMinima); // outro monitor: a barra mede de novo no DPI dele
        // teste: abre direto nos ajustes ou na janela Transmitir (para conferir o layout sem clicar)
        string? testView = Environment.GetEnvironmentVariable("STREEMU_TEST_VIEW");
        if (Environment.GetEnvironmentVariable("STREEMU_TEST_SIZE")?.Split('x') is [var tw, var th] && int.TryParse(tw, out var sw) && int.TryParse(th, out var sh))
            Shown += (_, _) => { StartPosition = FormStartPosition.Manual; Location = new Point(20, 20); Size = new Size(sw, sh); }; // teste: abre num tamanho dado (largura x altura)
        if (testView == "ajustes") Shown += (_, _) => ShowSettings();
        if (testView?.StartsWith("ajustes:") == true) Shown += (_, _) => BeginInvoke(() => ShowSettingsAt(testView[8..])); // teste: abre os Ajustes já numa seção (ex.: ajustes:Aparência)
        if (int.TryParse(Environment.GetEnvironmentVariable("STREEMU_TEST_PREPARAR_APOS"), out var prepararApos)) Shown += (_, _) => BeginInvoke(async () => { await Task.Delay(prepararApos * 1000); OpenGoLive(changing: false); }); // teste: abre a preparação N s depois, já com o palco de lives aberto
        if (testView == "grupos") Shown += (_, _) => BeginInvoke(async () => { for (int i = 0; i < 50 && _gruposTela == null; i++) await Task.Delay(200); MostrarGrupos(); }); // teste: abre a tela Grupos (ela só existe depois de o servidor dizer que tem contas)
        if (testView == "transmitir") Shown += (_, _) => BeginInvoke(() => OpenGoLive(changing: false));
        if (Environment.GetEnvironmentVariable("STREEMU_TEST_FOTO") is { Length: > 0 } fotoArquivo) Shown += (_, _) => BeginInvoke(async () => await TesteFotoDaJanelaAsync(fotoArquivo)); // MainForm.BarraTeste.cs
        if (testView == "encerrar") Shown += (_, _) => BeginInvoke(async () => await TesteEncerrarBarraAsync()); // MainForm.BarraTeste.cs
    }

    bool _ordemAgendada;

    /// <summary>
    /// O encaixe do WinForms vai do último filho para o primeiro, então a barra lateral precisa vir DEPOIS dos painéis que enchem o resto (Início,
    /// Configurações, Cinema, palco). Ao criar os handles quando a janela aparece, o próprio WinForms reordena os filhos (CreateControl →
    /// UpdateChildZOrder → SetWindowPos → UpdateChildControlIndex) e a barra ia para antes deles: o Cinema pegava a janela inteira e ficava embaixo da
    /// barra (print de 2026-11-16). A ordem é refeita sempre que um layout acontece fora dela.
    /// </summary>
    bool OrdemDoEncaixeErrada()
    {
        if (_side.Parent != this) return false;
        int side = Controls.GetChildIndex(_side);
        return Controls.Cast<Control>().Any(c => c.Dock == DockStyle.Fill && Controls.GetChildIndex(c) > side)
            || FaixasDoTopo.Any(f => f.Parent == this && Controls.GetChildIndex(f) < side);
    }

    /// <summary>As faixas de largura toda no topo (atualização e diagnóstico): encaixam antes da barra lateral, em cima dela.</summary>
    Control[] FaixasDoTopo => [_updateBar, _faixaDiagnostico];

    void CorrigirOrdemDoEncaixe()
    {
        if (!OrdemDoEncaixeErrada()) return;
        Log.Info("Ordem do encaixe trocada pelo WinForms: a barra lateral volta para depois dos painéis.");
        Controls.SetChildIndex(_side, Controls.Count - 1);
        foreach (var f in FaixasDoTopo) if (f.Parent == this) Controls.SetChildIndex(f, Controls.Count - 1);
        PerformLayout();
    }

    void VigiarOrdemDoEncaixe() => Layout += (_, _) =>
    {
        if (_ordemAgendada || !IsHandleCreated || !OrdemDoEncaixeErrada()) return;
        _ordemAgendada = true; // mudar a ordem dentro do próprio layout o refaria em cascata: corrige logo depois
        BeginInvoke(() => { _ordemAgendada = false; CorrigirOrdemDoEncaixe(); });
    };

    // ------------------------------------------------------------------ IStageHost

    public Rectangle? VideoArea
    {
        get
        {
            if (!IsHandleCreated || !Visible || WindowState == FormWindowState.Minimized) return null;
            int left = _side.Visible ? _side.Width : 0, column = _preparationView && _prepColumn ? PrepColumnWidth : 0; // a coluna da preparação fica de fora dos vídeos
            return RectangleToScreen(new Rectangle(left, 0, Math.Max(0, ClientSize.Width - left - column), ClientSize.Height));
        }
    }

    public bool StageShown => _stageView;
    public bool Fullscreen => _fullscreenStage;
    public bool SidebarOpen => !_side.Collapsed;
    public int Dp(int at96) => UiScale.Dp(this, at96);

    public void StreamsChanged(int count)
    {
        bool first = count > 0 && _lastStreamCount == 0;
        if (count > _lastStreamCount && !_preparationView) { _stageView = true; _settingsView = false; } // quem acabou de abrir uma stream quer vê-la
        _lastStreamCount = count;
        if (first && !SidebarPinned) _side.Collapsed = true; // a barra recolhe para dar a largura ao vídeo (o botão « » reabre; "fixar" nos ajustes desliga isto)
        if (first && !Automated && (!Visible || WindowState == FormWindowState.Minimized)) ShowMain();
        if (count == 0)
        {
            _side.Collapsed = false;
            _stageView = false; _prepColumn = false;
            if (_fullscreenStage) ToggleFullscreen();
        }
        ApplyView();
    }

    public event Action<int, Point, bool>? CaptionDragged;
    public void HighlightCaption(int index) => _stageCanvas.HighlightCaption(index);

    public void ShowCaptions(IReadOnlyList<StageCaption> captions)
    {
        _stageCanvas.Message = "Nenhuma stream à vista. Quem está ao vivo aparece na barra à esquerda.";
        _stageCanvas.SetCaptions(captions);
    }

    bool _wasNarrow, _sideAutoCollapsed;
    int _baseMinHeight; // a altura mínima de sempre (MainForm.FitToScreen); a barra lateral pode pedir mais

    /// <summary>
    /// A janela não fica mais baixa que a barra lateral precisa para as linhas não se cobrirem (com live no ar e lives em tela, já encolhidas;
    /// <see cref="SidePanel.MinimumHeight"/>). Recalculada na abertura, na troca de escala (Ctrl +) e de DPI; nunca passa da área útil da tela.
    /// </summary>
    void AjustarAlturaMinima()
    {
        if (!IsHandleCreated || WindowState == FormWindowState.Minimized || _fullscreenStage) return;
        int moldura = Height - ClientSize.Height, faixa = FaixasDoTopo.Where(f => f.Visible).Sum(f => f.Height);
        int teto = Screen.FromControl(this).WorkingArea.Height - 40;
        int altura = Math.Min(teto, Math.Max(_baseMinHeight, _side.MinimumHeight() + moldura + faixa));
        if (MinimumSize.Height != altura) MinimumSize = new Size(MinimumSize.Width, altura);
    }

    /// <summary>
    /// Janela estreita: a barra lateral vira trilho sozinha e devolve a largura ao conteúdo; larga de novo, ela reabre (se foi a janela que a recolheu).
    /// Quem clica no « » decide até a janela cruzar o limite de novo.
    /// </summary>
    void FitSidebarToWidth()
    {
        if (_fullscreenStage || SidebarPinned) return;
        bool narrow = ClientSize.Width < Dp(900);
        if (narrow == _wasNarrow) return;
        _wasNarrow = narrow;
        if (_settingsView) return; // Configurações conserva o trilho; o conteúdo adapta as categorias.
        if (narrow && !_side.Collapsed) { _side.Collapsed = true; _sideAutoCollapsed = true; }
        else if (!narrow && _sideAutoCollapsed && _lastStreamCount == 0) { _side.Collapsed = false; _sideAutoCollapsed = false; }
    }

    /// <summary>"Manter a barra lateral sempre aberta": guarda a escolha e abre a barra agora (ela não recolhe mais sozinha).</summary>
    void SetSidebarPinned(bool pinned)
    {
        _state.SidebarPinned = pinned;
        _hoverSidebar.Enabled = !pinned;
        if (!Automated) _state.Save();
        if (!pinned) return;
        _sideAutoCollapsed = false;
        HideFlyout();
        _side.Collapsed = false;
    }

    /// <summary>"Abrir a barra ao passar o mouse": guarda a escolha. Desligada, só o « » abre a barra; o TickFlyout e o TickBarraNasConfiguracoes fecham o que o mouse abriu.</summary>
    void SetSidebarHover(bool hover)
    {
        _state.SidebarHover = hover;
        if (!Automated) _state.Save();
    }

    public void ToggleSidebar()
    {
        if (SidebarPinned && !_side.Collapsed) return; // fixada: o « » não recolhe
        if (FixarAbertaPeloMouse()) return; // aberta pelo mouse nas Configurações: o « a deixa aberta
        _sideAutoCollapsed = false;
        if (_settingsView) { TrocarBarraSemDeslizar(!_side.Collapsed); return; } // nas Configurações deslizar piscava (MainForm.BarraConfiguracoes.cs)
        _side.Collapsed = !_side.Collapsed;
        PerformLayout();
        _playerStage?.Relayout();
    }

    /// <summary>Tela cheia do palco: a janela principal ocupa o monitor e a barra lateral some; de novo, tudo volta como estava.</summary>
    public void ToggleFullscreen()
    {
        if (!_fullscreenStage)
        {
            if (!_stageView && !_filmeView) return;
            _stateBeforeFullscreen = WindowState; _borderBeforeFullscreen = FormBorderStyle; _sideWasCollapsed = _side.Collapsed;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            _side.Visible = false;
        }
        else
        {
            _side.Visible = true;
            _side.Collapsed = _sideWasCollapsed;
            FormBorderStyle = _borderBeforeFullscreen;
            WindowState = _stateBeforeFullscreen;
        }
        _fullscreenStage = !_fullscreenStage;
        PerformLayout();
        _playerStage?.Relayout();
    }

    // ------------------------------------------------------------------ vistas

    /// <summary>Alterna entre o início e o palco (a barra lateral tem os dois botões). Sem stream aberta o palco não existe.</summary>
    void ShowView(bool stage)
    {
        if (stage && (_playerStage?.Count ?? 0) == 0) return;
        if (_preparationBusy) return;
        _preparationView = false;
        _stageView = stage;
        _settingsView = false;
        _filmeView = _arquivosView = _gruposView = false;
        ApplyView();
    }

    /// <summary>A engrenagem: todos os ajustes (apelido, avatar, Discord, estatísticas, opções avançadas, relatório, logs, firewall, atualização).</summary>
    void ShowSettings()
    {
        if (_preparationBusy) return;
        _preparationView = false;
        _settingsView = true;
        _filmeView = _arquivosView = _gruposView = false;
        _stageView = false; // os vídeos estacionam fora da tela e continuam tocando
        ApplyView();
    }

    // A troca de tela pinta uma vez só, no fim, e a barra muda de largura na hora junto com ela (sem deslizar): antes, ao sair das Configurações,
    // a barra reabria deslizando e a página nova aparecia ~0,3 s com pedaços da anterior (vídeo de 2026-11-16).
    void ApplyView()
    {
        using var pausa = new PinturaPausada(this);
        _abertaPeloMouse = false; // trocar de tela pela barra aberta com o mouse: quem decide a largura agora é a tela nova
        if (_settingsView && !_settingsRailActive && !SidebarPinned)
        {
            _sideBeforeSettings = _side.Collapsed;
            _settingsRailActive = true;
            _side.SnapCollapsed(true);
        }
        else if (!_settingsView && _settingsRailActive)
        {
            _settingsRailActive = false;
            _side.SnapCollapsed(!SidebarPinned && (_lastStreamCount > 0 || _wasNarrow || _sideBeforeSettings));
        }
        if (_filmeView) _gruposView = false; // o Cinema (MainForm.Filme.cs) não zera esta vista: a tela de Grupos não pode sobrar por baixo dele
        _homeHost.Visible = !_stageView && !_settingsView && !_filmeView && !_preparationView && !_arquivosView && !_gruposView;
        _preparationHost.Visible = _preparationView;
        _filmeHost.Visible = _filmeView && !_stageView && !_settingsView; // o filme continua tocando escondido
        // a preparação da live passa por cima e, ao voltar, a tela de arquivos reaparece (o _arquivosView não muda enquanto isso)
        _arquivosHost.Visible = _arquivosView && !_stageView && !_settingsView && !_filmeView && !_preparationView;
        _gruposHost.Visible = _gruposView && !_stageView && !_settingsView && !_filmeView && !_preparationView;
        _settingsHost.Visible = _settingsView;
        _stageCanvas.Visible = _stageView;
        PlacePreparation();
        CorrigirOrdemDoEncaixe(); // mostrar um painel pela 1a vez cria o handle dele, e o WinForms pode trocar a ordem nessa hora
        PerformLayout();
        RefreshSide();
        _playerStage?.Relayout();
    }

    /// <summary>Quem está na sala, ao vivo ou online, para a barra lateral e o trilho.</summary>
    void RefreshSide()
    {
        var friends = FiltrarPorGrupo(_room?.Friends ?? Enumerable.Empty<Member>()) // seletor de grupo da barra (MainForm.Grupos.cs)
            .OrderByDescending(f => f.Live != null).ThenBy(f => NomeNaTela(f))
            .Select(f => new SideFriend(f.Id, NomeNaTela(f), f.Live?.Game ?? "online", f.Live != null, IsWatching(f.Id), _friendAvatars?.ImageOf(f.Id)))
            .ToList();
        friends.AddRange(AusentesParaBarra()); // por último, em ordem alfabética (MainForm.Apelidos.cs); a sala antiga não tem ausentes
        GuardarNomesReais();
        var view = _preparationView ? SideView.Transmit : _settingsView ? SideView.Settings : _stageView ? SideView.Stage : _filmeView ? SideView.Filme
            : _arquivosView ? SideView.Arquivos : _gruposView ? SideView.Grupos : SideView.Home;
        var self = SelfForSide();
        _side.SetProfile(Nick, MyAvatarImage());
        _side.SetData(friends, _playerStage?.Count ?? 0, view, _groupStatus.Text, self);
        _flyout?.Panel.SetProfile(Nick, MyAvatarImage());
        _flyout?.Panel.SetData(friends, _playerStage?.Count ?? 0, view, _groupStatus.Text, self);
    }

    bool IsWatching(string friendId) => _links.Values.Any(l => !l.IAmSender && l.PeerId == friendId);

    /// <summary>Clique num amigo ao vivo (na barra, no trilho ou num cartão): coloca a stream no palco; se já está, tira.</summary>
    void ToggleWatchFriend(string friendId)
    {
        var existing = _links.Values.FirstOrDefault(l => !l.IAmSender && l.PeerId == friendId);
        if (existing != null) { EndLink(existing.SessionId, tellPeer: true, why: null); return; }
        if (_playerStage is { Full: true }) { Notify($"Já são {PlayerStage.MaxStreams} streams na janela. Feche uma para abrir outra."); return; }
        if (_room?.Find(friendId) is { Live: not null } member) _ = WatchAsync(member, "clique no amigo ao vivo (barra ou menu)");
    }

    /// <summary>
    /// A cada segundo: atualiza os cartões e a barra, e mantém as miniaturas chegando (o transmissor só manda por 45 s depois de cada pedido) enquanto os
    /// cartões estão à vista no início; sai da tela, para de pedir.
    /// </summary>
    void KeepLiveCards()
    {
        SetLiveCards();
        AtualizarAtividades(); // MainForm.Atividades.cs
        AtualizarConvites(); // MainForm.PrivadaConvidado.cs
        SinalSilencio(); // MainForm.Silencio.cs: o prazo pode ter acabado sozinho
        RefreshSide();
        bool want = Visible && WindowState != FormWindowState.Minimized && _homeHost.Visible && _liveCards.Visible;
        if (want && DateTime.Now >= _nextThumbAsk) { _nextThumbAsk = DateTime.Now.AddSeconds(30); _thumbsAsked = true; AskThumbnails(true); }
        else if (!want && _thumbsAsked) { _thumbsAsked = false; _nextThumbAsk = DateTime.MinValue; AskThumbnails(false); }
    }

    /// <summary>Os cartões grandes e a fileira de avatares da home mostram as mesmas pessoas.</summary>
    void SetLiveCards()
    {
        var cards = LiveCards();
        _liveCards.SetCards(cards);
        UpdateLiveSummary(_room?.Friends.ToList() ?? []);
    }

    /// <summary>Os blocos da home ocupam a largura que a janela tem (menos a folga dos lados): margens iguais e texto que quebra na largura certa.</summary>
    /// <summary>A barra parou: refaz o que ficou esperando durante o deslize (folgas, larguras dos cartões e quebras de linha) na largura final.</summary>
    void FinishSlide()
    {
        foreach (var host in new Control[] { _homeHost, _settingsHost })
            if (host.Controls.Count > 0 && host.Controls[0] is TableLayoutPanel root) FitRootPadding(root);
        _homePage?.PerformLayout();
        if (_settingsHost.Controls.Count > 0) WrapLabelsTo(_settingsHost.Controls[0]);
    }

    // ------------------------------------------------------------------ trilho que abre ao passar o mouse

    /// <summary>
    /// Com a barra recolhida em trilho, passar o mouse sobre ele (por ~0,2 s) abre a barra completa por cima do conteúdo, sem empurrar nada; tirar o mouse
    /// fecha. O botão « » do alto dela fixa a barra aberta. Só abre quando é a nossa janela que está sob o mouse (não quando outro programa cobre o trilho).
    /// Não abre com vídeo na tela nem com um menu da barra aberto (ver <see cref="FlutuantePermitida"/>), e depois de um menu da barra só volta a abrir
    /// quando o mouse sai do trilho e volta.
    /// </summary>
    void TickFlyout()
    {
        if (!IsHandleCreated) return;
        if (_settingsView) { TickBarraNasConfiguracoes(); return; }
        if (!SidebarHover) // desligado nos Ajustes: o mouse não abre a barra; se já estava aberta, fecha
        {
            _hoverSince = DateTime.MinValue;
            if (_flyout is { Visible: true, Closing: false }) _flyout.Dismiss();
            return;
        }
        var cursor = Cursor.Position;
        bool canShow = FlutuantePermitida();
        var rail = canShow ? _side.RectangleToScreen(_side.ClientRectangle) : Rectangle.Empty;
        bool shown = _flyout is { Visible: true };
        bool overFlyout = shown && _flyout!.Bounds.Contains(cursor);
        bool overRail = !rail.IsEmpty && rail.Contains(cursor) && IsOurWindowAt(cursor);
        if (_esperaSairDoTrilho) { if (overRail) overRail = false; else _esperaSairDoTrilho = false; }
        if (overRail || (overFlyout && canShow))
        {
            if (shown && !_flyout!.Closing) return;
            if (shown) { ShowFlyout(rail); return; } // o mouse voltou enquanto a barra fechava: reabre na hora
            if (_hoverSince == DateTime.MinValue) _hoverSince = DateTime.Now;
            else if (DateTime.Now - _hoverSince > TimeSpan.FromMilliseconds(180)) ShowFlyout(rail);
            return;
        }
        _hoverSince = DateTime.MinValue;
        if (shown && !_flyout!.Closing) _flyout.Dismiss();
    }

    bool _menuDaBarraAberto, _esperaSairDoTrilho;

    /// <summary>
    /// A barra flutuante pode abrir? Só com a barra recolhida em trilho e a janela à vista, e nunca:
    /// com vídeo na tela (lives, multistream, Transmitir em coluna ao lado das lives, player do Cinema), porque ela cobria o vídeo e a barra de
    /// controles dele (relato de 2026-11-16); ali o « » abre a barra encaixada, empurrando o vídeo;
    /// com um menu aberto pela barra (amigo, sino), porque ela é "sempre por cima" e abria em cima do menu com o mouse ainda no trilho.
    /// Nas Configurações ela também não abre: ali o mouse abre a barra encaixada (MainForm.BarraConfiguracoes.cs).
    /// </summary>
    bool FlutuantePermitida() => _side.Visible && _side.Collapsed && Visible && WindowState != FormWindowState.Minimized && !_fullscreenStage
        && !_menuDaBarraAberto && !VideoNaTela() && !_settingsView;

    bool VideoNaTela() => (_stageView || _preparationView && _prepColumn) && (_playerStage?.Count ?? 0) > 0
        || _filmeView && _filmeForm is { VideoNaTela: true };

    /// <summary>Um menu da barra (amigo, sino) abriu: a barra flutuante fecha e só volta depois que ele fechar e o mouse sair do trilho.</summary>
    void SegurarBarraFlutuante(ContextMenuStrip menu)
    {
        _menuDaBarraAberto = true;
        HideFlyout();
        menu.Closed += (_, _) => { _menuDaBarraAberto = false; _esperaSairDoTrilho = true; };
    }

    bool IsOurWindowAt(Point screenPoint)
    {
        var root = GetAncestor(WindowFromPoint(screenPoint), 2); // GA_ROOT
        return root == Handle || (_flyout != null && root == _flyout.Handle);
    }

    void ShowFlyout(Rectangle rail)
    {
        var flyout = EnsureFlyout();
        RefreshSide();
        flyout.ShowAt(new Rectangle(rail.Left, rail.Top, UiScale.Dp(this, SidePanel.OpenAt96), rail.Height), this);
    }

    void HideFlyout()
    {
        _hoverSince = DateTime.MinValue;
        _flyout?.Dismiss();
    }

    SideFlyout EnsureFlyout()
    {
        if (_flyout != null) return _flyout;
        var flyout = new SideFlyout();
        var panel = flyout.Panel;
        panel.HomeClicked += () => { HideFlyout(); ShowView(stage: false); };
        panel.FilmeVisivel = _side.FilmeVisivel;
        panel.FilmeTexto = _side.FilmeTexto;
        panel.FilmeClicked += () => { HideFlyout(); AbrirFilme(); };
        panel.ArquivosVisivel = _side.ArquivosVisivel;
        panel.ArquivosClicked += () => MostrarArquivos(); // MainForm.ArquivosTela.cs
        panel.GruposVisivel = _side.GruposVisivel;
        panel.GruposClicked += MostrarGrupos; // MainForm.Grupos.cs
        panel.StageClicked += () => { HideFlyout(); ShowView(stage: true); };
        panel.SettingsClicked += () => { HideFlyout(); ShowSettings(); };
        panel.ToggleClicked += () => { HideFlyout(); ToggleSidebar(); }; // o « » da barra flutuante a fixa aberta
        panel.FriendClicked += friend => ToggleWatchFriend(friend.Id);
        panel.FriendAvatarClicked += (amigo, foto) => { HideFlyout(); MostrarMenuAmigo(amigo, foto); };
        panel.TransmitClicked += () => { HideFlyout(); OpenTransmission(); };
        panel.ProfileClicked += () => { HideFlyout(); ShowSettingsAt("Perfil"); };
        panel.SelfClicked += () => { HideFlyout(); ToggleMyLive(); };
        panel.StopLiveClicked += () => { HideFlyout(); EncerrarLive("barra lateral"); };
        panel.ProblemClicked += () => { HideFlyout(); MarcarProblema(pedirNota: true); }; // MainForm.Telemetria.cs
        panel.DiagClicked += () => { HideFlyout(); ShowSettingsAt("Diagnóstico"); };
        panel.UpdateClicked += () => { HideFlyout(); _ = AtualizarPelaBarraAsync(); }; // MainForm.Atualizacao.cs
        panel.BellClicked += r => { HideFlyout(); MostrarMenuSino(r); }; // MainForm.Silencio.cs
        panel.AtividadeClicked += r => { HideFlyout(); MostrarAtividades(r); }; // MainForm.Atividades.cs
        panel.ThemeClicked += _ => { HideFlyout(); ToggleTheme(Rectangle.Empty); }; // MainForm.Tema.cs
        panel.GroupFilterClicked += r => { HideFlyout(); MostrarMenuGrupos(r); }; // MainForm.Grupos.cs
        _flyout = flyout;
        SinalAtualizacao(); // a barra flutuante nasce já com o estado da atualização
        SinalSilencio(forcar: true);
        _ultimaAtividade = ""; // a barra flutuante nasce já com o ponto da atividade (no próximo segundo)
        TickTelemetria(); // a barra flutuante nasce já com o indicador do diagnóstico
        return flyout;
    }
}
