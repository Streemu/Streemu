using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Streemu.Media;
using Streemu.Net;
using Streemu.Ui;
using Streemu.Util;

namespace Streemu;

public sealed partial class MainForm : Form
{
    readonly bool _localMode;
    readonly AppState _state;
    readonly StreamSender _sender;
    /// <summary>Todo segundo: o tique da sala (GroupTick).</summary>
    readonly System.Windows.Forms.Timer _statsTimer = new() { Interval = 1000 };
    readonly CancellationTokenSource _closing = new();

    // ---- controles
    readonly TextBox _nick = new() { Width = 180, MaxLength = 24 };

    readonly ComboBox _source = new Ui.RoundedComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 460 };
    readonly Button _refresh = new() { Text = "Atualizar", AutoSize = true };
    readonly ComboBox _preset = new Ui.RoundedComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    readonly ComboBox _audio = new Ui.RoundedComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    readonly ComboBox _encoder = new Ui.RoundedComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    readonly ComboBox _latency = new Ui.RoundedComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    /// <summary>Último estado do envio ("Não deu para transmitir: ..."): a tela Transmitir o lê para dizer o motivo da falha. Não aparece em nenhuma tela.</summary>
    readonly Label _sendStatus = new() { AutoSize = true, MaximumSize = new Size(640, 0) };

    readonly CheckBox _hwDecode = new Ui.ToggleSwitch { Text = "Decodificar na placa de vídeo", AutoSize = true, Checked = true };

    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = UiTypography.CreateMono(UiTypography.MonoLog), WordWrap = false };
    readonly Button _report = new() { Text = "Copiar relatório", AutoSize = true };
    readonly Button _openLogs = new() { Text = "Abrir pasta de logs", AutoSize = true };
    readonly Button _firewall = new() { Text = "Firewall do Windows...", AutoSize = true };
    FirewallCheck.Result? _fw;

    public MainForm(bool localMode)
    {
        _localMode = localMode;
        if (localMode) RelayConfig.ModoTeste = true; // teste local nunca fala com o servidor real (Net/RelayConfig.cs)
        Text = UiBrand.Name + (localMode ? " (MODO LOCAL)" : "");
        Font = UiTypography.Create(UiTypography.Body); // escala da interface (DESIGN.md: typography.ui); era 9.5 até 2026-11-09
        StartPosition = FormStartPosition.CenterScreen;
        FitToScreen();

        // modo local e teste automático usam estado próprio (o estado salvo da pessoa não pode ser lido nem gravado por teste)
        _state = localMode || TestDir != null ? new AppState() : AppState.Load();
        // cada amigo que clica em Assistir pela sala ganha a sua saída de envio (AddOutput, MainForm.Grupo.cs)
        _sender = new StreamSender(localMode || TestDir != null ? null : new PipelineMemory(_state));

        BuildLayout();
        Theme.Apply(this);
        InitTheme(); // MainForm.Tema.cs
        WireEvents();
        InitGroup();
        InitNick(); // MainForm.Avatar.cs: apelido digitado ou nome do Discord
    }

    // ------------------------------------------------------------------ layout

    /// <summary>
    /// Tamanho que cabe na tela. Antes a janela abria com 820 de altura (≈1025 px a 125% de escala), maior que a tela do
    /// um amigo, e o log ocupava o resto sem barra de rolagem: ele não conseguia chegar no "Copiar relatório".
    /// </summary>
    void FitToScreen()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        double k = DeviceDpi / 96.0;
        // 236 a mais para a barra lateral aberta (spec 006, janela única): o início continua com a largura de sempre
        int w = Math.Min((int)(990 * k), area.Width - 40), h = Math.Min((int)(660 * k), area.Height - 40);
        Size = new Size(w, h);
        MinimumSize = new Size(Math.Min((int)(760 * k), w), _baseMinHeight = Math.Min((int)(440 * k), h));
    }

    void BuildLayout()
    {
        // transmissão (detalhes)
        var t2 = Grid(3);
        var sourceHint = new Label
        {
            AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 8, 3, 8),
            Text = "Jogo: escolha \"Jogo/tela cheia: ...\". O app pega o monitor onde o jogo está direto da placa, mas só mostra o jogo: com Alt+Tab o amigo fica na última imagem do jogo. Monitor: para área de trabalho."
        };
        t2.Controls.Add(Lbl("O que transmitir:"), 0, 0); t2.Controls.Add(_source, 1, 0); t2.Controls.Add(_refresh, 2, 0);
        t2.Controls.Add(sourceHint, 1, 1);
        t2.Controls.Add(Lbl("Qualidade:"), 0, 2); t2.Controls.Add(_preset, 1, 2);
        t2.Controls.Add(Lbl("Áudio:"), 0, 3); t2.Controls.Add(_audio, 1, 3);
        t2.Controls.Add(Lbl("Encoder:"), 0, 4); t2.Controls.Add(_encoder, 1, 4);
        t2.Controls.Add(Lbl("Latência SRT:"), 0, 5); t2.Controls.Add(_latency, 1, 5);
        StretchInputs(t2, _source, _preset, _audio, _encoder, _latency);
        _wrapLabels.Add(sourceHint);

        BuildShell(BuildHomeColumn(), BuildSettingsPage(t2)); // MainForm.Janela.cs: o início + os ajustes + o palco + a barra lateral

        foreach (var p in Preset.All) _preset.Items.Add(p);
        _preset.SelectedIndex = 3; // 1080p60
        _audio.Items.AddRange(new object[] { "Só o app transmitido", "PC todo menos Discord", "Sem áudio" });
        _audio.SelectedIndex = 0;
        _encoder.Items.AddRange(new object[] { "Automático", "NVIDIA (NVENC)", "AMD (AMF)", "Intel (QSV)", "CPU (x264)", "Windows (Media Foundation)" });
        _encoder.SelectedIndex = 0;
        _latency.Items.AddRange(new object[] { "80 ms", "120 ms", "200 ms", "400 ms" });
        _latency.SelectedIndex = 0; // 80 ms: no teste de campo ficou bem mais fluido que 120 ms
        RefreshSources();
    }

    /// <summary>Painel de rolagem de uma vista da janela (início ou ajustes): uma coluna, linhas pela altura do conteúdo.</summary>
    static TableLayoutPanel ScrollingRoot(int rows)
    {
        var root = new BufferedTable { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12, 12, 12, 4), AutoScroll = true };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < rows; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        // janela larga: o conteúdo para numa largura boa de ler e fica encostado na barra lateral (24 de folga), com o resto livre à direita
        root.SizeChanged += (_, _) => { if (!SidePanel.Sliding) FitRootPadding(root); };
        return root;
    }

    static void FitRootPadding(TableLayoutPanel root)
    {
        int left = UiScale.Dp(root, 24); // encostado na barra lateral: quando ela abre ou fecha, o conteúdo acompanha (não fica folga no meio)
        int right = Math.Max(left, root.ClientSize.Width - left - UiScale.Dp(root, MaxContentAt96));
        if (root.Padding.Left != left || root.Padding.Right != right) root.Padding = new Padding(left, root.Padding.Top, right, root.Padding.Bottom);
    }

    /// <summary>Textos que quebram na largura da janela (e não numa largura fixa): a página os refaz quando a janela muda de tamanho.</summary>
    readonly List<Label> _wrapLabels = new();
    readonly List<Control> _stretchInputs = new();
    /// <summary>Largura máxima dos campos de texto e menus de Ajustes (em 96 DPI); em janela estreita eles encolhem para o botão do lado não ser cortado.</summary>
    const int MaxInputAt96 = 440, MinInputAt96 = 200;

    /// <summary>
    /// Todos os campos e menus de uma tabela com a mesma largura (os menus ficavam cada um com a sua, de pontas desencontradas). Largura fixa e
    /// confortável: esticar com a janela cortava a última linha da tabela (visto em teste, 2026-11-09).
    /// </summary>
    void StretchInputs(TableLayoutPanel table, params Control[] inputs)
    {
        foreach (var input in inputs) { input.Width = UiScale.Dp(this, MaxInputAt96); _stretchInputs.Add(input); }
        table.Dock = DockStyle.None; // a tabela encolhe ao conteúdo: o botão ao lado do menu fica junto dele, não na ponta da janela
        table.Anchor = AnchorStyles.Top | AnchorStyles.Left;
    }

    /// <summary>Largura máxima do conteúdo das vistas (início e ajustes), em pixels de 96 DPI.</summary>
    const int MaxContentAt96 = 1100;

    static Card Group(string title) => new(title);
    static Label Lbl(string t) => new() { Text = t, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) };
    static TableLayoutPanel Grid(int cols)
    {
        var t = new TableLayoutPanel { ColumnCount = cols, AutoSize = true, Dock = DockStyle.Fill };
        for (int i = 0; i < cols; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return t;
    }

    // ------------------------------------------------------------------ eventos

    void WireEvents()
    {
        Log.Line += line => Ui(() =>
        {
            if (_log.TextLength > 200_000) _log.Text = _log.Text[^100_000..];
            _log.AppendText(line + Environment.NewLine);
        });
        foreach (var l in Log.Tail(50)) _log.AppendText(l + Environment.NewLine);

        Load += async (_, _) =>
        {
            StartRoom();
            IniciarTelemetria(); // MainForm.Telemetria.cs (depois da sala: ela repassa o "envie agora")
            if (_startHidden && _tray != null) Hide();
            await IniciarAsync(); // MainForm.Abertura.cs
        };
        FormClosing += (_, e) =>
        {
            // fechar a janela deixa o app na bandeja (os amigos continuam vendo você na sala e no Discord)
            if (HideToTrayInsteadOfClosing(e.CloseReason)) { e.Cancel = true; return; }
            ShutdownGroup();
            _closing.Cancel();
            _sender.Stop(); _statsTimer.Stop();
            SaveNick();
            Thread.Sleep(200);
        };

        _refresh.Click += (_, _) => RefreshSources();
        _report.Click += (_, _) => { Clipboard.SetText(BuildReport()); Log.Info("Relatório copiado. Cole no Discord ou mande para análise."); };
        _openLogs.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", Log.Folder) { UseShellExecute = true });
        _firewall.Click += (_, _) => OnFirewallButton(); // MainForm.Firewall.cs

        _sender.Status += s => Ui(() => _sendStatus.Text = s);
        _sender.Stopped += () => Ui(() =>
        {
            SetTransmitInputs(true);
            OnLiveStopped();
        });
        _statsTimer.Tick += (_, _) => GroupTick();
        _statsTimer.Start();
    }

    void Ui(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch { }
    }

    string Nick => string.IsNullOrWhiteSpace(_nick.Text) ? "amigo" : _nick.Text.Trim();

    void RefreshSources()
    {
        var keep = _source.SelectedItem?.ToString();
        _source.Items.Clear();
        foreach (var s in Sources.List()) _source.Items.Add(s);
        var items = _source.Items.Cast<CaptureSource>().ToList();
        int idx = keep == null ? -1 : items.FindIndex(o => o.ToString() == keep);
        // sem escolha anterior: jogo em tela cheia, se houver; senão o monitor principal
        if (idx < 0) idx = items.FindIndex(s => s.IsFullScreen);
        if (idx < 0) idx = items.FindIndex(s => !s.IsWindow && s.IsPrimary);
        _source.SelectedIndex = idx >= 0 ? idx : (items.Count > 0 ? 0 : -1);
    }

    int LatencyMs => int.Parse(((string)_latency.SelectedItem!).Split(' ')[0]);

    void SetTransmitInputs(bool enabled)
    {
        _source.Enabled = _refresh.Enabled = _preset.Enabled = _audio.Enabled = _encoder.Enabled = _latency.Enabled = enabled;
    }

    async Task ToggleTransmitAsync()
    {
        if (_sender.Running)
        {
            EncerrarLive("botão Parar"); // MainForm.MinhaLive.cs
            return;
        }
        if (ContaBloqueia()) return;
        if (_source.SelectedItem is not CaptureSource src) { _sendStatus.Text = "Escolha o que transmitir."; return; }
        var settings = new StreamSender.Settings(
            src,
            (Preset)_preset.SelectedItem!,
            (AudioMode)_audio.SelectedIndex,
            (EncoderChoice)_encoder.SelectedIndex,
            LatencyMs);
        SetTransmitInputs(false);
        try
        {
            await _sender.StartAsync(settings);
            OnLiveStarted(GameName.Of(src));
            AvisarSomDoAmigoNaLive(); // MainForm.AvisoSom.cs
        }
        catch (Exception e)
        {
            _sendStatus.Text = "Não deu para transmitir: " + e.Message;
            Log.Info("Falha ao iniciar a transmissão: " + e.Message);
            SetTransmitInputs(true);
        }
    }

    // ------------------------------------------------------------------ teste automático (variáveis STREEMU_TEST_*)

    static readonly string? TestDir = Environment.GetEnvironmentVariable("STREEMU_TEST_DIR");
    static readonly string TestRole = Environment.GetEnvironmentVariable("STREEMU_TEST_ROLE") ?? "A";

    /// <summary>
    /// O "espectadores" do StreamSender conta as SAÍDAS de envio abertas, e uma saída pode ficar aberta enquanto a ligação dela ainda
    /// conecta ou acabou de cair: a live de um amigo (10/11) dizia 3 com 2 pessoas assistindo. Aqui vale quem assiste de verdade
    /// (as ligações da sala); as saídas ficam em "envios_abertos".
    /// </summary>
    JsonObject? SessionSummaryForReport()
    {
        var o = _sender.SessionSummary();
        if (o == null) return null;
        o["envios_abertos"] = o["espectadores"]?.DeepClone();
        o["espectadores"] = WatchingCount;
        return o;
    }

    /// <param name="foco">Relatório do fim de uma ligação: qual delas (multistream: quem assiste várias lives tem várias em "sala.ligacoes").</param>
    string BuildReport(JsonObject? foco = null)
    {
        var s = _sender.Current;
        var report = new JsonObject
        {
            ["app"] = $"Streemu {Program.Version}",
            ["gerado"] = DateTime.Now.ToString("s"),
            ["foco"] = foco,
            ["windows"] = SystemInfo.Windows,
            ["gpus"] = new JsonArray(SystemInfo.Gpus().Select(g => (JsonNode)g!).ToArray()),
            // a conexão de cada amigo está em "sala.ligacoes"; o bloco "conexao" do túnel do modo por código acabou na spec 022
            ["firewall"] = _fw?.Summary,
            ["transmissao"] = s == null ? null : new JsonObject
            {
                ["fonte"] = s.Source.Label,
                ["preset"] = s.Preset.Name,
                ["saida"] = $"{_sender.OutSize.W}x{_sender.OutSize.H}@{s.Preset.Fps}",
                ["kbps"] = s.Preset.Kbps,
                ["encoder"] = _sender.Chosen?.ToString(),
                ["audio"] = _sender.AudioDescription,
                ["latencia_srt_ms"] = s.LatencyMs,
                ["fonte_nativa"] = $"{_sender.SourceSize.W}x{_sender.SourceSize.H}",
                ["captura"] = _sender.CaptureUsed?.ToString(),
                ["sincronia"] = _sender.SyncSnapshot(),
                // resumo da sessão inteira: o último status (ex.: "0 de 0" no momento em que parou) não dizia nada
                ["sessao"] = SessionSummaryForReport()
            },
            ["sala"] = GroupReport(),
            ["telemetria"] = TelemetriaParaRelatorio(),
            ["log"] = new JsonArray(Log.TailForReport(100, childBudget: 25).Select(l => (JsonNode)l!).ToArray())
        };
        return report.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
