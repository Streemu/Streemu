using Streemu.Net;
using Streemu.Util;
using UpdateState = Streemu.Ui.UpdateState;

namespace Streemu;

/// <summary>
/// Atualização automática (docs/specs/004-atualizacao-automatica.md): faixa no topo da janela com "Atualizar agora",
/// item na bandeja e balão. Arquivo à parte para não aumentar o MainForm.Grupo.cs, que outros PRs também mexem.
/// </summary>
public sealed partial class MainForm
{
    /// <summary>Aberto pela atualização (--atualizado): avisa e limpa a versão anterior.</summary>
    public static bool JustUpdated { get; set; }

    UpdateInfo? _update;
    string? _updateNotifiedTree;
    bool _updating;
    /// <summary>Quantas vezes a atualização automática falhou seguidas; passando de 3 ela para (a faixa com o botão continua) para não reabrir o app em ciclo.</summary>
    int _autoUpdateFailures;
    const int AutoUpdateGiveUpAfter = 3;
    /// <summary>STREEMU_SEM_AUTOATUALIZAR=1 só avisa, como antes (para depurar o próprio atualizador).</summary>
    static bool AutoUpdateEnabled => Environment.GetEnvironmentVariable("STREEMU_SEM_AUTOATUALIZAR") != "1";
    readonly FlowLayoutPanel _updateBar = new() { Dock = DockStyle.Top, AutoSize = true, Visible = false, WrapContents = true, Padding = new Padding(12, 8, 12, 8) };
    readonly Label _updateText = new() { AutoSize = true, ForeColor = Theme.AccentText, Padding = new Padding(0, 7, 10, 0), Font = UiTypography.Create(UiTypography.Caption, FontStyle.Bold) };
    readonly Button _updateButton = new() { Text = "Atualizar agora", AutoSize = true, Font = UiTypography.Create(UiTypography.Caption, FontStyle.Bold), Cursor = Cursors.Hand };
    readonly ToolTip _updateTip = new() { AutoPopDelay = 30000 };
    /// <summary>Sempre visível no rodapé: procura atualização na hora, sem esperar a conferência automática.</summary>
    readonly Button _checkUpdate = new() { Text = "Procurar atualização", AutoSize = true };

    /// <summary>Canal da atualização, em Configurações > Sistema (nomes em inglês de propósito: são os termos que quem testa app já conhece).</summary>
    readonly ComboBox _canalAtualizacao = new Ui.RoundedComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };

    /// <summary>A opção em Configurações > Sistema. Trocar de canal procura atualização na hora, no canal novo.</summary>
    Control CanalAtualizacaoOpcao()
    {
        _canalAtualizacao.Items.AddRange(["Nightly", "Stable"]);
        _canalAtualizacao.SelectedIndex = Updater.NormalizarCanal(_state.CanalAtualizacao) == Updater.CanalStable ? 1 : 0;
        _canalAtualizacao.SelectedIndexChanged += async (_, _) =>
        {
            string canal = _canalAtualizacao.SelectedIndex == 1 ? Updater.CanalStable : Updater.CanalNightly;
            if (canal == Updater.NormalizarCanal(_state.CanalAtualizacao)) return;
            _state.CanalAtualizacao = canal;
            if (!Automated) _state.Save();
            Log.Info($"Atualização: canal trocado para {canal}.");
            ShowUpdate(null); // o aviso que estava na tela era do outro canal
            if (Automated || !Updater.Enabled || RelayConfig.Load(_state) is not { } cfg) return;
            try
            {
                var u = await Updater.CheckAsync(cfg, canal, _closing.Token);
                ShowUpdate(u);
                if (u != null) await UpdateWhenIdleAsync(_closing.Token);
            }
            catch (Exception e) when (!_closing.IsCancellationRequested) { Log.Info($"Atualização: não consegui verificar o canal {canal} agora ({e.Message}). Tento de novo em 10 min."); }
        };
        return _canalAtualizacao;
    }

    /// <summary>Chamado no fim do InitGroup.</summary>
    void InitUpdater()
    {
        _checkUpdate.Click += async (_, _) => await CheckUpdateNowAsync();
        _side.UpdateClicked += () => _ = AtualizarPelaBarraAsync();
        _side.BellClicked += MostrarMenuSino; // MainForm.Silencio.cs
        _side.AtividadeClicked += MostrarAtividades; // MainForm.Atividades.cs
        SinalAtualizacao();
        _updateTip.SetToolTip(_checkUpdate, $"Você está na {Updater.CurrentLabel}. Clique para ver se tem versão nova.");
        _updateBar.BackColor = Theme.Accent;
        _updateButton.FlatStyle = FlatStyle.Flat;
        _updateButton.FlatAppearance.BorderSize = 0;
        _updateButton.BackColor = Theme.AccentText; // botão claro sobre a faixa roxa
        _updateButton.ForeColor = Theme.Accent;
        _updateButton.Padding = new Padding(10, 3, 10, 3);
        _updateBar.Controls.Add(_updateText);
        _updateBar.Controls.Add(_updateButton);
        _updateButton.Click += async (_, _) => await UpdateNowAsync();
        // por último na coleção = encaixado primeiro: a faixa fica no topo, acima do card do grupo
        Controls.Add(_updateBar);
        _updateBar.SendToBack();

        // a versão fica em Configurações > Sistema e no log; a barra de título mostra só o nome (mockups do Streemu: sem versão nem build)
        Log.Info("Versão: " + Updater.CurrentLabel);
        if (JustUpdated) Load += (_, _) => Notify($"{UiBrand.Name} atualizado: {Updater.CurrentLabel}.");
        // a troca do exe não renova o cache de ícones do Explorer (atalho e botão ficavam com o ícone antigo, 2026-11-19): uma vez por atualização, fora da thread da tela
        if (JustUpdated && !Automated && Environment.ProcessPath is { } exeAtual) _ = Task.Run(() => SeloBarraTarefas.RenovarCacheDeIcones(exeAtual));
        // teste automático não atualiza (nem apaga o exe antigo); STREEMU_TEST_ATUALIZAR=1 liga o atualizador também nos testes (contra um servidor falso: STREEMU_ATUALIZACAO_URL)
        if (Automated && Environment.GetEnvironmentVariable("STREEMU_TEST_ATUALIZAR") != "1") return;
        // o exe anterior (Streemu.old.exe) só some depois que a versão anterior fechou de vez
        _ = Task.Delay(TimeSpan.FromSeconds(JustUpdated ? 60 : 20)).ContinueWith(_ => Updater.CleanupOld());
        if (!Updater.Enabled) { Log.Info("Atualização automática desligada neste build (desenvolvimento)."); return; }
        Updater.Preload();
        _ = UpdateLoopAsync(_closing.Token);
    }

    /// <summary>
    /// Confere 3 s depois de abrir e a cada 10 min, e ATUALIZA SOZINHO assim que não estiver transmitindo, assistindo nem com um pedido de assistir
    /// pendente: todo mundo fica sempre na última versão da main (spec 004, seção 7). Sem servidor ou sem rede: só o log, nada na cara.
    /// </summary>
    async Task UpdateLoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch { return; }
        bool loggedFailure = false;
        while (!ct.IsCancellationRequested)
        {
            var cfg = RelayConfig.Load(_state);
            if (cfg != null)
            {
                try
                {
                    var u = await Updater.CheckAsync(cfg, _state.CanalAtualizacao, ct);
                    loggedFailure = false;
                    Ui(() => ShowUpdate(u));
                    if (u != null) await UpdateWhenIdleAsync(ct);
                    // sem exe novo, o ffmpeg (com exe novo, quem decide o ffmpeg é ele, depois de reabrir)
                    else await UpdateFfmpegWhenIdleAsync(cfg, ct);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    if (!loggedFailure) Log.Info("Atualização: não consegui verificar agora (" + e.Message + "). Tento de novo em 10 min.");
                    loggedFailure = true;
                }
            }
            try { await Task.Delay(TimeSpan.FromMinutes(10), ct); } catch { return; }
        }
    }

    /// <summary>Parado = sem transmissão, sem player e sem pedido de assistir vindo do Discord (atualizar reabre o app e perderia o pedido).</summary>
    bool Idle => !Busy && _pendingWatch == null;

    /// <summary>
    /// Espera ficar parado (confere a cada 20 s; a faixa com o botão "Atualizar agora" fica à vista) e aplica a versão mais nova, perguntando de novo ao
    /// servidor na hora para não aplicar uma que já foi superada. Depois de 3 falhas seguidas desiste (o botão continua).
    /// </summary>
    async Task UpdateWhenIdleAsync(CancellationToken ct)
    {
        if (!AutoUpdateEnabled || _autoUpdateFailures >= AutoUpdateGiveUpAfter) return;
        while (!Idle || _updating)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch { return; }
            if (!_updateBar.IsHandleCreated) return;
        }
        var cfg = RelayConfig.Load(_state);
        var fresh = cfg == null ? null : await Updater.CheckAsync(cfg, _state.CanalAtualizacao, ct);
        if (fresh == null) { Ui(() => ShowUpdate(null)); return; }
        Ui(() => { ShowUpdate(fresh); _ = UpdateNowAsync(alreadyConfirmed: true, automatic: true); });
    }

    /// <summary>
    /// Troca o ffmpeg\ffmpeg.exe e o ffmpeg\ffplay.exe pelos que este exe quer (spec 004, seção 8), com a mesma regra do exe: só baixa e só troca parado.
    /// Sem nada na tela: só o log ([ffmpeg-atualizacao] e [ffplay-atualizacao]) e o ffmpeg_versao da telemetria.
    /// </summary>
    async Task UpdateFfmpegWhenIdleAsync(RelayConfig.Settings cfg, CancellationToken ct)
    {
        if (!AutoUpdateEnabled) return;
        foreach (var up in BinarioUpdater.Todos)
        {
            if (up.GaveUp) continue;
            if (await Task.Run(up.Pending, ct) is not { } pin) continue;
            while (!Idle || _updating)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch { return; }
                if (!_updateBar.IsHandleCreated) return;
            }
            await up.RunAsync(cfg, pin, () => Idle && !_updating, OnUiAsync, ct);
        }
    }

    /// <summary>Roda <paramref name="f"/> na thread da janela e devolve o resultado (falso se a janela já fechou).</summary>
    async Task<bool> OnUiAsync(Func<bool> f)
    {
        if (IsDisposed || !IsHandleCreated) return false;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Ui(() => { try { done.SetResult(f()); } catch (Exception e) { done.SetException(e); } });
        try { return await done.Task.WaitAsync(_closing.Token); } catch (OperationCanceledException) { return false; }
    }

    bool _procurandoAtualizacao;

    /// <summary>Botão da barra lateral (pedido de 11/11): com versão nova, atualiza (como o "Atualizar agora" da faixa); sem, procura na hora.</summary>
    async Task AtualizarPelaBarraAsync()
    {
        if (_update != null) await UpdateNowAsync();
        else await CheckUpdateNowAsync();
    }

    /// <summary>Leva o estado da atualização para o botão da barra lateral (e da barra flutuante).</summary>
    void SinalAtualizacao()
    {
        var estado = _updating ? UpdateState.Updating : _procurandoAtualizacao ? UpdateState.Checking : _update != null ? UpdateState.Available : UpdateState.Idle;
        string detalhe = _update is { } u && !_updating
            ? $"Versão nova: {u.Versao} de {u.Quando}. Clique para atualizar agora (o {UiBrand.Name} fecha e abre de novo em alguns segundos)."
            : $"Você está na {Updater.CurrentLabel}. Clique para ver se tem versão nova.";
        _side.SetUpdate(estado, detalhe);
        _flyout?.Panel.SetUpdate(estado, detalhe);
        SinalSilencio(); // o ponto do sino depende de haver versão nova
    }

    void ShowUpdate(UpdateInfo? u)
    {
        if (_updating) return;
        _update = u;
        SinalAtualizacao();
        _updateBar.Visible = FaixaDeAtualizacaoVisivel; // some enquanto as notificações estão silenciadas
        if (u == null) return;
        _updateText.Text = $"Nova versão do {UiBrand.Name} ({u.Versao} · {u.Quando}). " + (AutoUpdateEnabled ? "Atualiza sozinho quando você não estiver transmitindo nem assistindo. " : "") + "Passe o mouse para ver o que mudou.";
        _updateTip.SetToolTip(_updateText, "O que mudou:\n" + u.Notas);
        _updateTip.SetToolTip(_updateButton, $"Baixa, troca e reabre o {UiBrand.Name} (alguns segundos).");
        if (_updateNotifiedTree == u.Arvore) return;
        _updateNotifiedTree = u.Arvore;
        Log.Info($"Atualização disponível: {u.Versao} de {u.Quando} (agora: {Updater.CurrentLabel}). O que mudou:\n{u.Notas}");
        if (!AutoUpdateEnabled)
            MostrarBalao(6000, $"Atualização do {UiBrand.Name}", $"Versão nova de {u.Quando}. Abra o {UiBrand.Name} e clique em \"Atualizar agora\".", ToolTipIcon.Info);
    }

    // filme aberto, baixando ou servindo o arquivo também: a atualização reinicia o app e derrubaria o filme de todos
    bool Busy => _streamAbrindo || _streamTela is { IsDisposed: false }
        || _sender.Running || _links.Count > 0 || _filmeForm is { Ocupado: true } || _filmeEnvios.Count > 0;

    /// <summary>Botão "Procurar atualização": confere agora e diz o resultado, seja qual for.</summary>
    async Task CheckUpdateNowAsync()
    {
        if (_updating || _procurandoAtualizacao) return;
        if (!Updater.Enabled)
        {
            MessageBox.Show(this, $"Este {UiBrand.Name} foi compilado neste PC ({Updater.CurrentLabel}) e não se atualiza sozinho.\n\n" +
                $"Para receber atualizações, use o {UiBrand.Name} do instalador ou do zip da release.", UiBrand.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var cfg = RelayConfig.Load(_state);
        if (cfg == null)
        {
            MessageBox.Show(this, "Modo de teste sem servidor: não dá para procurar atualização (falta STREEMU_ATUALIZACAO_URL).",
                UiBrand.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _checkUpdate.Enabled = false;
        _checkUpdate.Text = "Procurando...";
        _procurandoAtualizacao = true; SinalAtualizacao();
        UpdateInfo? u;
        try { u = await Updater.CheckAsync(cfg, _state.CanalAtualizacao, _closing.Token); }
        catch (Exception e)
        {
            Log.Info("Procurar atualização: " + e.Message);
            MessageBox.Show(this, $"Não consegui procurar atualização: {e.Message}.\n\nConfira a internet e tente de novo daqui a pouco.",
                UiBrand.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        finally
        {
            _checkUpdate.Enabled = true;
            _checkUpdate.Text = "Procurar atualização";
            _procurandoAtualizacao = false; SinalAtualizacao();
        }
        ShowUpdate(u);
        if (u == null)
        {
            Log.Info($"Procurar atualização: já está na mais nova ({Updater.CurrentLabel}).");
            MessageBox.Show(this, $"Você já está na versão mais nova ({Updater.CurrentLabel}).", UiBrand.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string notas = string.Join("\n", u.Notas.Split('\n').Take(12));
        string aviso = Busy ? "\n\nA transmissão e os players abertos param por alguns segundos." : "";
        if (MessageBox.Show(this, $"Tem versão nova: {u.Versao} de {u.Quando}.\n\nO que mudou:\n{notas}\n\nAtualizar agora? O {UiBrand.Name} fecha e abre de novo sozinho.{aviso}",
                $"Atualização do {UiBrand.Name}", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            await UpdateNowAsync(alreadyConfirmed: true);
    }

    async Task UpdateNowAsync(bool alreadyConfirmed = false, bool automatic = false)
    {
        if (_update is not { } u || _updating) return;
        if (!alreadyConfirmed && Busy && MessageBox.Show(this,
                $"Atualizar fecha o {UiBrand.Name} por alguns segundos: a transmissão e os players abertos param. Continuar?",
                UiBrand.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _updating = true;
        _updateButton.Enabled = false;
        SinalAtualizacao();
        try
        {
            var progress = new Progress<int>(p => _updateButton.Text = $"Baixando {p}%...");
            string zip = await Updater.DownloadAsync(u, progress, _closing.Token);
            if (automatic && !Idle)
            {
                // alguém começou a transmitir/assistir durante o download: não derruba; o pacote fica guardado e a próxima conferência tenta de novo
                Log.Info("Atualização automática adiada: o app ficou ocupado durante o download.");
                _updating = false; _updateButton.Enabled = true; _updateButton.Text = "Atualizar agora";
                SinalAtualizacao();
                return;
            }
            _updateButton.Text = "Instalando...";
            bool hidden = !Visible || WindowState == FormWindowState.Minimized;
            await Task.Run(() => Updater.ApplyAndRestart(zip, u.Commit, restart: true, hidden: hidden));
            _autoUpdateFailures = 0;
            ExitApp();
        }
        catch (Exception e)
        {
            _updating = false;
            _updateButton.Enabled = true;
            _updateButton.Text = "Tentar de novo";
            SinalAtualizacao();
            Log.Info("Atualização falhou: " + e);
            if (automatic) { _autoUpdateFailures++; return; } // sozinho e sem barulho: o botão "Tentar de novo" fica na faixa
            Notify("Não consegui atualizar: " + Updater.Describe(e) + (Updater.MissingPart(e) != null
                ? $". Saia pela bandeja e abra o {UiBrand.Name} de novo; se continuar, rode o instalador de novo."
                : ". Tente de novo; se continuar, baixe o zip da release."));
        }
    }

    /// <summary>Primeiro item da bandeja quando há versão nova.</summary>
    void AddUpdateMenuItem(ContextMenuStrip menu)
    {
        if (_update is not { } u || _updating) return;
        menu.Items.Add(new ToolStripMenuItem($"Atualizar o {UiBrand.Name} ({u.Versao} · {u.Quando})", null, async (_, _) => { ShowMain(); await UpdateNowAsync(); })
            { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
    }
}
