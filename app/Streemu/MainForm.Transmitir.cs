using Streemu.Ui;
using Streemu.Media;
using Streemu.Util;

namespace Streemu;

public sealed partial class MainForm
{
    readonly BufferedPanel _preparationHost = new() { Dock = DockStyle.Fill, Visible = false };
    GoLiveView? _preparation;
    HomeView? _homePage;
    bool _preparationView, _preparationChanging, _preparationBusy, _editingTransmission, _placingPreparation;
    SideView _beforePreparation = SideView.Home;
    readonly FlowLayoutPanel _liveControl = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
    readonly OwnLiveSummary _ownLiveStatus = new();
    readonly Button _ownPreview = new() { Text = "Ver sua prévia", AutoSize = true };
    readonly Button _changeSource = new() { Text = "Trocar fonte ou qualidade", AutoSize = true };
    readonly Button _backFromLive = new() { Text = "Voltar às lives", AutoSize = true };

    void InitPreparation()
    {
        Controls.Add(_preparationHost);
        Controls.SetChildIndex(_preparationHost, 0); // Fill depois da lateral no encaixe reverso do WinForms.
        _liveControl.Padding = new Padding(Dp(UiMetrics.PagePadding));
        _liveControl.Controls.AddRange([_backFromLive, _ownLiveStatus, _ownPreview, _changeSource, _convidadosBtn, _stopLive]);
        _backFromLive.Click += (_, _) => ReturnFromPreparation();
        _ownPreview.Click += (_, _) => ToggleMyLive();
        _changeSource.Click += (_, _) => OpenGoLive(true);
        Theme.Paint(_liveControl, new HashSet<Button>());
        Disposed += (_, _) => { if (_preparation is { IsDisposed: false }) _preparation.Dispose(); if (!_liveControl.IsDisposed) _liveControl.Dispose(); _hoverTimer.Dispose(); _liveThumbs.Dispose(); _flyout?.Dispose(); };
        _homePage!.PreparationSpaceChanged += PlacePreparation;
        VisibleChanged += (_, _) => PlacePreparation();
        SizeChanged += (_, _) => PlacePreparation();
        PlacePreparation();
    }

    void OpenGoLive(bool changing, string? convidarId = null)
    {
        if (_preparationBusy) return;
        if (ContaBloqueia()) return;
        if (!Visible && IsHandleCreated) ShowMain();
        if (_fullscreenStage) ToggleFullscreen();
        if (!_preparationView) _beforePreparation = _stageView ? SideView.Stage : _settingsView ? SideView.Settings : _filmeView ? SideView.Filme : SideView.Home;
        _preparationView = true;
        _editingTransmission = changing && _sender.Running;
        _prepColumn = ColumnPossible();
        _stageView = _prepColumn; _settingsView = _filmeView = false;
        EnsurePreparation(changing);
        _preparation!.RefreshFriends(ComNomesDaTela(_room?.Friends ?? []));
        if (convidarId != null) _preparation.Invite(convidarId);
        ApplyView();
    }

    /// <summary>
    /// Transmitir/Sua transmissão da barra lateral, de qualquer tela (inclusive com o palco aberto): é a mesma linha que o #137 chamou de "Transmitir" na
    /// barra, aqui com uma linha só para os dois estados. O teste da barra (STREEMU_TEST_VIEW=encerrar) só conta o clique.
    /// </summary>
    void OpenTransmission()
    {
        if (_testeSemJanelaTransmitir) return;
        if (!_sender.Running) { OpenGoLive(false); return; }
        if (_preparationBusy) return;
        if (_fullscreenStage) ToggleFullscreen();
        if (!_preparationView) _beforePreparation = _stageView ? SideView.Stage : _settingsView ? SideView.Settings : _filmeView ? SideView.Filme : SideView.Home;
        _preparationView = true; _prepColumn = ColumnPossible(); _stageView = _prepColumn; _settingsView = _filmeView = false;
        _editingTransmission = false;
        ApplyView();
    }

    void EnsurePreparation(bool changing)
    {
        if (_preparation != null && _preparationChanging == changing) return;
        _preparation?.Dispose();
        _preparationChanging = changing;
        var current = changing ? _sender.Current : null;
        var view = new GoLiveView(current?.Source, _state.GoLiveProcess, current?.Preset.IsSource == true ? 0 : current?.Preset.Height ?? _state.GoLiveHeight,
            current?.Preset.Fps ?? _state.GoLiveFps, current != null ? (int)current.Audio : _state.GoLiveAudio, changing, _room?.Friends.ToList() ?? []);
        _preparation = view;
        view.BackRequested += ReturnFromPreparation;
        view.SubmitRequested += async () => await SubmitPreparationAsync(view);
    }

    async Task SubmitPreparationAsync(GoLiveView view)
    {
        if (_preparationBusy || view.SelectedSource is not CaptureSource source) return;
        if (!_preparationView) _beforePreparation = SideView.Home;
        _preparationBusy = true; view.SetBusy(true); view.SetFailure(null);
        bool success = false;
        try
        {
            if (!source.StillExists()) { view.SetFailure("A janela escolhida fechou. Abra o jogo e selecione a janela novamente."); return; }
            RememberGoLive(source, view.SelectedPreset, view.AudioIndex);
            if (_preparationChanging && _sender.Running && _sender.Current is { } current)
            {
                var next = current with { Source = source, Preset = view.SelectedPreset, Audio = (AudioMode)view.AudioIndex };
                success = await ApplyPreparationChangeAsync(next);
            }
            else
            {
                _comecarPrivada = view.Privada; _convidarAoComecar = view.ConvidadosIds.ToList();
                await StartWithAsync(source, view.SelectedPreset, view.AudioIndex);
                success = _sender.Running;
            }
            if (!success && !view.IsDisposed) view.SetFailure(_preparationChanging
                ? _sender.Running
                    ? "Não foi possível trocar a fonte ou a qualidade. A transmissão continua como estava. Confira a janela escolhida e tente novamente."
                    : "A transmissão parou durante a troca. Confira a fonte escolhida e tente começar novamente."
                : _sendStatus.Text + " Confira a fonte escolhida e tente novamente.");
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex) { if (!view.IsDisposed) view.SetFailure("Não foi possível preparar a transmissão. " + ex.Message + " Confira a fonte e tente novamente."); }
        finally { _preparationBusy = false; if (!view.IsDisposed) view.SetBusy(false); }
        if (success && !IsDisposed)
        {
            ReturnFromPreparation();
            // Uma live nova sempre começa pública; só o rascunho atual conserva a escolha ao voltar ou falhar.
            _preparation?.Dispose(); _preparation = null;
        }
    }

    async Task<bool> ApplyPreparationChangeAsync(StreamSender.Settings next)
    {
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed() => finished.TrySetResult(_sender.Running && _sender.Current is { } actual
            && actual.Source.Handle == next.Source.Handle && actual.Source.IsWindow == next.Source.IsWindow
            && actual.Preset.Fps == next.Preset.Fps && actual.Preset.Height == next.Preset.Height && actual.Audio == next.Audio);
        void Stopped() => finished.TrySetResult(false);
        _sender.Reconfigured += Changed; _sender.Stopped += Stopped;
        try { _sender.Reconfigure(next); return await finished.Task.WaitAsync(_closing.Token); }
        finally { _sender.Reconfigured -= Changed; _sender.Stopped -= Stopped; }
    }

    void ReturnFromPreparation()
    {
        if (_preparationBusy) return;
        _preparationView = false; _prepColumn = false;
        _stageView = _beforePreparation == SideView.Stage && (_playerStage?.Count ?? 0) > 0;
        _settingsView = _beforePreparation == SideView.Settings;
        _filmeView = _beforePreparation == SideView.Filme;
        ApplyView();
    }

    void PlacePreparation()
    {
        if (_homePage == null || _placingPreparation || IsDisposed) return;
        _placingPreparation = true;
        try { PlacePreparationCore(); }
        finally { _placingPreparation = false; }
    }

    /// <summary>Os botões do painel "Sua transmissão" ocupam a largura toda, como no desenho, e "Parar transmissão" é o vermelho (a ação que derruba a live).</summary>
    void FitLiveControlWidths()
    {
        int width = Math.Max(Dp(160), _liveControl.ClientSize.Width - _liveControl.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
        _ownLiveStatus.FitWidth(width);
        foreach (var button in new[] { _backFromLive, _ownPreview, _changeSource, _convidadosBtn, _stopLive })
        {
            button.AutoSize = false;
            button.Size = new Size(width, Dp(UiMetrics.ActionHeight));
        }
        _stopLive.BackColor = Theme.Error;
        _stopLive.FlatAppearance.MouseOverBackColor = ControlPaint.Light(Theme.Error, .15f);
        _stopLive.ForeColor = Theme.AccentText;
    }

    /// <summary>
    /// Preparação em coluna (desenho): com o palco de lives aberto e janela larga, "Transmitir" não cobre as lives: o painel vira uma coluna à direita e
    /// o palco fica com o resto (<see cref="VideoArea"/>). Sem espaço, ou sem lives, continua a página cheia de sempre.
    /// </summary>
    bool _prepColumn;

    bool ColumnPossible() => _stageView && (_playerStage?.Count ?? 0) > 0 && ClientSize.Width - (_side.Visible ? _side.Width : 0) >= Dp(UiMetrics.PreparationColumnWidth + 40 + 520); // coluna + ao menos 520 dp para os vídeos

    int PrepColumnWidth => Dp(UiMetrics.PreparationColumnWidth + 40);

    void PlaceHostAsColumnOrPage()
    {
        if (_preparationView && _prepColumn)
        {
            _preparationHost.Dock = DockStyle.None;
            int left = _side.Visible ? _side.Width : 0, width = PrepColumnWidth;
            _preparationHost.Bounds = new Rectangle(ClientSize.Width - width, 0, width, ClientSize.Height);
            _preparationHost.BringToFront();
        }
        else if (_preparationHost.Dock != DockStyle.Fill) { _preparationHost.Dock = DockStyle.Fill; Controls.SetChildIndex(_preparationHost, 0); }
    }

    void PlacePreparationCore()
    {
        var home = _homePage!;
        bool active = _sender.Running;
        PlaceHostAsColumnOrPage();
        Control? target = _preparationView ? _preparationHost : _homeHost.Visible && home.HasPreparationSpace ? home.PreparationHost : null;
        if (target == null) { if (_preparation != null) _preparation.Visible = false; _liveControl.Visible = false; return; }
        if (active && !_editingTransmission && !_preparationBusy)
        {
            if (_preparation != null) _preparation.Visible = false;
            if (_liveControl.Parent != target) target.Controls.Add(_liveControl);
            _liveControl.Visible = true;
            _ownLiveStatus.Update(_liveGame, LiveQuality, WatchingCount);
            FitLiveControlWidths();
            _ownPreview.Text = _myLive?.Running == true ? "Fechar sua prévia" : "Ver sua prévia";
            _convidadosBtn.Visible = Privada.Ligada;
            _stopLive.Visible = true;
            return;
        }
        _liveControl.Visible = false;
        if (!_preparationBusy) EnsurePreparation(active && _editingTransmission);
        if (_preparation!.Parent != target) target.Controls.Add(_preparation);
        _preparation.SetInline(target != _preparationHost || _prepColumn, showBack: _prepColumn);
        _preparation.Visible = true;
        _preparation.RefreshActivity();
        _preparation.BringToFront();
    }
}
