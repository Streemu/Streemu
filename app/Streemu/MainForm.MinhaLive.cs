using Streemu.Media;
using Streemu.Ui;
using Streemu.Ui.Multistream;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// Quem transmite vê que está no ar e pode ver a própria live. (1) O selo "● AO VIVO" sobre a tela (<see cref="LiveBadge"/>, fora da transmissão) e a linha
/// "Minha live" na barra lateral. (2) A prévia: o mesmo vídeo que sai para os amigos, lido do encoder (<see cref="StreamSender.AttachPreview"/>), sem nova
/// conexão, captura nem encoder, na janela de streams (e, de lá, na janela pequena). Fica fora do MainForm.cs para dar menos conflito.
/// </summary>
public sealed partial class MainForm
{
    LiveBadge? _badge;
    Viewer? _myLive;
    bool _senderPaused;
    // Interruptores, deslizante e lista arredondada em vez dos controles quadrados do Windows (pedido de 2026-11-16): os mesmos do resto do app.
    readonly CheckBox _badgeOn = new ToggleSwitch { Text = "Mostrar um sinal de \"ao vivo\" sobre a tela enquanto eu transmito", AutoSize = true };
    readonly Segmented _badgeStyle = new("Bolinha", "Pílula com texto");
    readonly Segmented _badgeSize = new("Pequeno", "Médio", "Grande");
    readonly VolumeSlider _badgeOpacity = new() { Minimum = 30, Width = 260, Height = 30, Margin = new Padding(0, 2, 0, 0), AccessibleName = "Opacidade do sinal" };
    readonly Label _badgeOpacityText = new() { AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(8, 8, 0, 0) };
    readonly CheckBox _badgeBlink = new ToggleSwitch { Text = "Piscar (a cada segundo)", AutoSize = true };
    readonly Button _badgeTest = new() { Text = "Ver como fica (5 s)", AutoSize = true };
    readonly ComboBox _badgeCorner = new RoundedComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    readonly CheckBox _openMyLive = new ToggleSwitch { Text = "Abrir a minha live na janela de streams quando eu começar a transmitir", AutoSize = true };
    readonly CheckBox _somNaLiveOn = new ToggleSwitch { Text = "Tocar um som nos espectadores quando alguém entrar ou sair da minha live", AutoSize = true };

    /// <summary>Liga o selo e as opções (uma vez, ao montar a janela). Em teste automático o selo só aparece com STREEMU_TEST_SELO=1.</summary>
    void InitMinhaLive()
    {
        _badgeOn.Checked = !_state.LiveBadgeHidden;
        _badgeCorner.Items.AddRange(new object[] { "Cima, à esquerda", "Cima, à direita", "Baixo, à esquerda", "Baixo, à direita" });
        _badgeCorner.SelectedIndex = Math.Clamp(_state.LiveBadgeCorner, 0, 3);
        _openMyLive.Checked = _state.OpenMyLiveOnStart;
        if (Environment.GetEnvironmentVariable("STREEMU_TEST_SOM_NA_LIVE") == "desligado") _state.SomNaLiveDesligado = true; // teste: a opção desligada, sem gravar (o estado de teste não é salvo)
        _somNaLiveOn.Checked = !_state.SomNaLiveDesligado;
        _badgeStyle.Selected = BadgeStyles.FromSaved(_state.LiveBadgeStyle) == BadgeStyle.Pill ? 1 : 0;
        _badgeSize.Selected = Math.Clamp(_state.LiveBadgeSize, 0, 2);
        _badgeOpacity.Size = new Size(UiScale.Dp(this, 260), UiScale.Dp(this, 30)); // o controle desenhado à mão não é escalado pelas Configurações
        _badgeOpacity.Value = Math.Clamp(_state.LiveBadgeOpacity, 30, 100);
        _badgeOpacityText.Text = $"{_badgeOpacity.Value}%";
        _badgeBlink.Checked = _state.LiveBadgeBlink;
        _badgeOn.CheckedChanged += (_, _) => { _state.LiveBadgeHidden = !_badgeOn.Checked; _state.Save(); _badge?.Run(_badgeOn.Checked); };
        _badgeStyle.SelectedChanged += _ => SaveBadgeLook();
        _badgeSize.SelectedChanged += _ => SaveBadgeLook();
        _badgeOpacity.ValueChanged += _ => { _badgeOpacityText.Text = $"{_badgeOpacity.Value}%"; SaveBadgeLook(); };
        _badgeBlink.CheckedChanged += (_, _) => SaveBadgeLook();
        _badgeCorner.SelectedIndexChanged += (_, _) => SaveBadgeLook();
        _badgeTest.Click += (_, _) => _badge?.ShowSample();
        _openMyLive.CheckedChanged += (_, _) => { _state.OpenMyLiveOnStart = _openMyLive.Checked; _state.Save(); };
        _somNaLiveOn.CheckedChanged += (_, _) => { _state.SomNaLiveDesligado = !_somNaLiveOn.Checked; if (!Automated) _state.Save(); SomDaLive.Ligado = SomNaLivePermitido; }; // MainForm.SomNaLive.cs
        _sender.FocusHeld += held => _senderPaused = held;
        if (Automated && Environment.GetEnvironmentVariable("STREEMU_TEST_SELO") is not ("1" or "visivel")) return;
        _badge = new LiveBadge(ReadBadgeState) { Look = BadgeLookFromState() };
        _badge.Blocked += Notify; // tela cheia exclusiva: o selo não aparece, então o app diz por quê (barra de status e, com a janela escondida, a bandeja)
        _badge.Run(_badgeOn.Checked);
        // teste: STREEMU_TEST_SELO_LOOK="estilo,tamanho,opacidade,piscar,canto" mostra um exemplo do selo por 60 s (sem gravar nada)
        if (Environment.GetEnvironmentVariable("STREEMU_TEST_SELO_LOOK")?.Split(',') is [var st, var sz, var op, var bl, var co] && int.TryParse(st, out var i1) && int.TryParse(sz, out var i2) && int.TryParse(op, out var i3) && int.TryParse(co, out var i5))
        {
            _badge.Look = new BadgeLook(BadgeStyles.FromSaved(i1), i2, i3, bl == "1", (BadgeCorner)i5);
            _badge.ShowSample(60);
        }
    }

    BadgeLook BadgeLookFromState() => new(BadgeStyles.FromSaved(_state.LiveBadgeStyle), _state.LiveBadgeSize, _state.LiveBadgeOpacity, _state.LiveBadgeBlink,
        (BadgeCorner)Math.Clamp(_state.LiveBadgeCorner, 0, 3));

    /// <summary>Guarda a escolha do usuário e já aplica no selo (se ele está à vista, muda na hora; o botão "Ver como fica" mostra um exemplo).</summary>
    void SaveBadgeLook()
    {
        _state.LiveBadgeStyle = (int)(_badgeStyle.Selected == 1 ? BadgeStyle.Pill : BadgeStyle.Dot); _state.LiveBadgeSize = _badgeSize.Selected; _state.LiveBadgeOpacity = _badgeOpacity.Value;
        _state.LiveBadgeBlink = _badgeBlink.Checked; _state.LiveBadgeCorner = Math.Max(0, _badgeCorner.SelectedIndex);
        _state.Save();
        if (_badge != null) { _badge.Look = BadgeLookFromState(); _badge.ShowSample(3); }
    }

    /// <summary>As opções da página Aparência dos Ajustes, em dois grupos: o sinal de "ao vivo" sobre a tela e o que acontece com a minha live.</summary>
    (Control[] Sinal, Control[] Live) MinhaLiveOpcoes()
    {
        Control Row(string label, params Control[] items)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
            // o tamanho mínimo segura a altura: sem ele o rótulo terminava com altura 0 no ajuste de largura das Configurações e "Estilo:", "Tamanho:"...
            // não apareciam (visto em 2026-11-16)
            var size = new Size(UiScale.Dp(this, 110), UiScale.Dp(this, 30));
            row.Controls.Add(new Label { Text = label, AutoSize = false, Size = size, MinimumSize = size, TextAlign = ContentAlignment.MiddleLeft, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Bottom, Margin = new Padding(UiScale.Dp(this, 24), 0, 8, 0), ForeColor = Theme.TextMuted }); // altura da linha: o texto fica no meio dos botões
            foreach (var item in items) row.Controls.Add(item);
            return row;
        }
        var opacity = Row("Opacidade:", _badgeOpacity, _badgeOpacityText);
        var test = Row("", _badgeTest);
        Control[] sinal = [_badgeOn, Row("Estilo:", _badgeStyle), Row("Tamanho:", _badgeSize), opacity, Row("Lugar:", _badgeCorner), Row("", _badgeBlink), test,
            Streemu.Ui.AjusteBlocos.Nota("O sinal é só para você: ele não vai para a transmissão. Com o jogo em tela cheia exclusiva o Windows não deixa nada por cima dele, então ele só aparece sobre jogo em janela ou sem bordas. A barra lateral e a bandeja mostram que você está ao vivo de qualquer jeito. A prévia da sua live usa o vídeo que já está sendo enviado (não abre outra conexão); ela só custa decodificar esse vídeo.", alinhadaAoInterruptor: false)];
        Control[] live = [_openMyLive, _somNaLiveOn, Streemu.Ui.AjusteBlocos.Nota("O som toca no Streemu de quem assiste (quem acabou de entrar não o ouve) e o sino de silenciar dele o cala. Quem está numa versão antiga não ouve.")];
        return (sinal, live);
    }

    LiveBadgeState? ReadBadgeState()
    {
        if (!_sender.Running || _sender.Current is not { } c) return null;
        return new LiveBadgeState(MonitorBounds(c.Source), WatchingCount, DateTimeOffset.FromUnixTimeMilliseconds(_liveSinceMs), _senderPaused);
    }

    /// <summary>O monitor onde a live acontece: o do jogo (captura de janela) ou o próprio monitor escolhido.</summary>
    static Rectangle MonitorBounds(CaptureSource source)
    {
        var monitor = source.IsWindow ? Display.MonitorOf(source.Handle) : source.Handle;
        var info = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
        Native.GetMonitorInfo(monitor, ref info);
        return Rectangle.FromLTRB(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom);
    }

    SideSelf? SelfForSide() => _sender.Running ? new SideSelf(_liveGame, WatchingCount, _myLive?.Running == true) : null;

    /// <summary>A live começou ou acabou: abre a prévia sozinha se o usuário pediu (e fecha ao acabar).</summary>
    void OnLiveChanged(bool wentLive, bool ended)
    {
        if (ended) { _myLive?.Stop(); return; }
        if (!wentLive) return;
        string? test = Environment.GetEnvironmentVariable("STREEMU_TEST_MINHA_LIVE");
        if (!_openMyLive.Checked && test is not ("1" or "alternar")) return;
        _ = Task.Delay(1500).ContinueWith(_ => Ui(() => { if (_myLive == null && _sender.Running) ToggleMyLive(); })); // espera o encoder soltar os primeiros quadros
        if (test == "alternar") // teste: fecha em 9 s e abre de novo em 15 s, como dois cliques no "Minha live"
            foreach (int s in new[] { 9, 15 }) _ = Task.Delay(s * 1000).ContinueWith(_ => Ui(ToggleMyLive));
    }

    /// <summary>
    /// A única porta para parar a própria live (barra lateral, Início, bandeja): diagnóstico de UX de 13/11, item 3. Antes o Início e a bandeja paravam
    /// direto e a barra pedia confirmação sempre. Agora confirma só quando há alguém assistindo (parar derruba essas pessoas); sem ninguém, para na hora.
    /// </summary>
    void EncerrarLive(string origem)
    {
        if (!_sender.Running) return;
        int quantos = WatchingCount;
        string quem = quantos == 1 ? " 1 pessoa está assistindo." : $" {quantos} pessoas estão assistindo.";
        bool confirmou = quantos == 0 || Automated || MessageBox.Show(this, "Encerrar a sua transmissão?" + quem, UiBrand.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        if (!confirmou) return;
        Log.Info($"Live encerrada por: {origem}.");
        _sender.Stop();
    }

    /// <summary>"Ver minha live" / "Fechar a prévia" (linha da barra lateral).</summary>
    void ToggleMyLive()
    {
        if (_myLive != null) { _myLive.Stop(); return; }
        if (!_sender.Running) { Notify("Você não está ao vivo."); return; }
        if (_playerStage is { Full: true }) { Notify($"Já são {PlayerStage.MaxStreams} streams na janela. Feche uma para ver a sua live."); return; }
        // aviso curto e sem bloquear: quem pediu a prévia quer ver (teste de primeiro uso, 2026-11-19)
        if (MirrorRisk() is { } why) Notify(why);
        StartMyLive();
    }

    /// <summary>
    /// Transmitindo o MONITOR inteiro e com a janela do Streemu nele, a prévia entra na própria transmissão (efeito espelho, e os amigos a veem): o
    /// ffplay é de outro processo e o Windows não deixa marcá-lo como fora da captura (testado: acesso negado). Com a captura só da janela do jogo não há problema.
    /// Era um bloqueio ("Prévia desligada"). O único motivo registrado é visual (nada nos docs indica custo de CPU nem queda do encoder: a prévia lê o vídeo
    /// já codificado, sem nova captura). Agora só avisa.
    /// </summary>
    string? MirrorRisk()
    {
        if (CapturedMonitor() is not { } monitor || Display.MonitorOf(Handle) != monitor) return null;
        return $"Você transmite a tela inteira: a prévia aparece dentro da transmissão (efeito espelho) e os amigos a veem. Para evitar, mova o {UiBrand.Name} para outro monitor ou transmita só a janela do jogo.";
    }

    /// <summary>O monitor inteiro que está sendo transmitido (captura de monitor, ou do jogo em tela cheia pelo ddagrab); nulo se só a janela de um app vai ao ar.</summary>
    IntPtr? CapturedMonitor()
    {
        if (_sender.Current is not { } c || _sender.CaptureUsed is not { } cap) return null;
        bool wholeMonitor = cap.Dda || !c.Source.IsWindow || cap.Name.Contains("monitor", StringComparison.OrdinalIgnoreCase);
        if (!wholeMonitor) return null;
        return c.Source.IsWindow ? Display.MonitorOf(c.Source.Handle) : c.Source.Handle;
    }

    /// <summary>A janela pequena fica sempre por cima da tela: transmitindo o monitor inteiro ela entraria na transmissão (o ffplay não pode ser marcado como fora da captura).</summary>
    string? SmallWindowGuard() => CapturedMonitor() == null ? null
        : $"A janela pequena ficaria sempre por cima da tela e apareceria na transmissão (você transmite a tela inteira). Deixe a prévia no palco do {UiBrand.Name}, em outro monitor, ou transmita só a janela do jogo.";

    void StartMyLive()
    {
        bool hw = _hwDecode.Checked && Environment.TickCount64 >= Interlocked.Read(ref _vulkanRetryAt) && !SystemInfo.HasRtx50;
        var v = new Viewer { UseHardware = hw, SmallWindowGuard = SmallWindowGuard };
        v.Exited += () => Ui(() => { if (ReferenceEquals(_myLive, v)) _myLive = null; RefreshSide(); });
        _myLive = v;
        var stage = _playerStage ??= new PlayerStage(this);
        stage.Add(v, () => $"Você · {_liveGame} · sua live (prévia)", MyAvatarImage);
        v.StartLocal(sink => _sender.AttachPreview(sink), $"{UiBrand.Name} — minha live");
        PlayerOverlay.ShowFor(v, () => LiveQuality, () => Array.Empty<RosterEntry>(), new AppStateMiniStore(_state, !Automated), text => Ui(() => Notify(text)), stage);
        Log.Info("Prévia da própria live aberta (lê o vídeo já codificado; sem nova conexão).");
        RefreshSide();
    }
}
