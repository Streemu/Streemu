using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>O que o selo mostra agora: em que monitor, quantos assistem, desde quando e se a transmissão está pausada (o jogo saiu da frente).</summary>
public sealed record LiveBadgeState(Rectangle Monitor, int Watching, DateTimeOffset Since, bool Paused);

public enum BadgeCorner { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>
/// Como o selo se apresenta: só a bolinha (padrão) ou a pílula com texto. Os números são os gravados no estado; o 1 era a "bolinha com número" de quem
/// assiste, tirada a pedido de 2026-11-16: quem a tinha escolhido volta para a bolinha (<see cref="BadgeStyles.FromSaved"/>).
/// </summary>
public enum BadgeStyle { Dot = 0, Pill = 2 }

/// <summary>Converte o estilo gravado no estado. Ex.: <c>BadgeStyles.FromSaved(state.LiveBadgeStyle)</c>.</summary>
public static class BadgeStyles
{
    public static BadgeStyle FromSaved(int saved) => saved == (int)BadgeStyle.Pill ? BadgeStyle.Pill : BadgeStyle.Dot;
}

/// <summary>As escolhas do usuário para o selo (Ajustes > Transmissão). Tamanho: 0 pequeno, 1 médio, 2 grande. Opacidade de 30 a 100 %.</summary>
public sealed record BadgeLook(BadgeStyle Style = BadgeStyle.Dot, int Size = 1, int OpacityPercent = 75, bool Blink = false, BadgeCorner Corner = BadgeCorner.TopLeft);

/// <summary>
/// O sinal de que você está ao vivo, sobre a tela de quem transmite: sem ele, o único aviso era o som de entrada e saída de espectador. Por padrão é só uma
/// <b>bolinha vermelha translúcida</b> num canto (visível sem atrapalhar); o usuário escolhe o estilo, o tamanho, a opacidade, se pisca e o canto.
/// É uma janela pequena do próprio Streemu, sempre por cima, que NÃO pega clique nem foco (o jogo continua recebendo tudo) e é marcada
/// <c>WDA_EXCLUDEFROMCAPTURE</c>: some da captura, então os amigos não a veem (medido em 2026-11-10 com o ddagrab: uma janela comum aparece na captura do
/// monitor, a marcada não). Só aparece sobre jogo em janela ou sem bordas; em tela cheia exclusiva o Windows não deixa nada por cima do jogo, e o app (barra
/// lateral e bandeja) continua mostrando que a live está no ar: o selo avisa isso uma vez por live (<see cref="Blocked"/>), em vez de sumir sem explicação.
/// "Sempre por cima" só vale contra janelas comuns: jogo em tela cheia (DXGI, SDL2) vira "sempre por cima" ao ganhar o foco e passa por cima do selo
/// (campo 2026-11-11; reproduzido com uma janela que faz o mesmo). Por isso o selo confere a cada 250 ms se alguma janela ficou sobre ele e volta
/// para cima (<see cref="KeepOnTop"/>).
/// Uso: <c>var badge = new LiveBadge(() =&gt; estado); badge.Look = new BadgeLook(BadgeStyle.Dot); badge.Run(true);</c> (a função diz o estado; nula esconde).
/// </summary>
public sealed class LiveBadge : Form
{
    const int WsExNoActivate = 0x08000000, WsExToolWindow = 0x80, WsExTransparent = 0x20, WsExLayered = 0x80000;
    const uint WdaExcludeFromCapture = 0x11;
    const int MarginAt96 = 16;
    static readonly int[] DotAt96 = { 14, 20, 28 }, PillFontPt = { 9, 10, 12 };

    const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoActivate = 0x10, SwpShowWindow = 0x40;
    static readonly IntPtr HwndTopmost = new(-1);
    const int QunsRunningD3dFullScreen = 3;
    const int CoveredTicksToWarn = 5;

    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

    readonly Func<LiveBadgeState?> _read;
    readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };
    /// <summary>
    /// Traz o selo de volta para cima de tudo: pedir "sempre por cima" uma vez só não basta. O jogo (principalmente sem bordas) se põe no topo
    /// da camada "sempre por cima" cada vez que volta ao foco depois de um Alt+Tab e passa a cobrir o selo, que continua "visível" mas atrás
    /// dele (reportado em 2026-11-11: o selo só aparecia ao começar a live). Reproduzido com uma janela por cima chegando depois.
    /// </summary>
    readonly System.Windows.Forms.Timer _raise = new() { Interval = 250 };
    readonly HashSet<uint> _loggedCover = new();
    LiveBadgeState? _state, _sample;
    DateTime _sampleUntil = DateTime.MinValue;
    bool _tick, _enabled, _excluded, _warned, _exclusive;
    int _coveredTicks;
    BadgeLook _look = new();

    /// <summary>O selo não consegue ficar à vista (jogo em tela cheia exclusiva, ou um jogo que insiste em ficar por cima). Chega uma vez por live, com a frase
    /// para o usuário.</summary>
    public event Action<string>? Blocked;

    /// <summary>As escolhas do usuário; muda na hora se o selo está à vista.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public BadgeLook Look
    {
        get => _look;
        set { _look = value with { Size = Math.Clamp(value.Size, 0, 2), OpacityPercent = Math.Clamp(value.OpacityPercent, 30, 100) }; if (_clock.Enabled) Refresh_(); }
    }

    public LiveBadge(Func<LiveBadgeState?> read)
    {
        _read = read;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.Black; // o que fica fora do desenho é cortado pela forma (Region); o desenho cobre tudo
        Text = $"{UiBrand.Name}: selo ao vivo";
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        _ = Handle; // cria a janela escondida para aplicar a marca de "fora da captura" antes de aparecer
        _clock.Tick += (_, _) => Refresh_();
        _raise.Tick += (_, _) => KeepOnTop();
    }

    /// <summary>
    /// Sobe o selo ao topo da camada "sempre por cima" sem ativá-lo nem mexer no foco (o jogo continua recebendo teclado e mouse), mas só se alguma janela
    /// ficou sobre ele: jogo em tela cheia entra na camada ao ganhar o foco (DXGI em tela cheia, SDL2 em tela cheia com foco). Em tela cheia exclusiva não
    /// briga com o jogo (lá nada aparece por cima, e reposicionar uma janela sobre ele 4 vezes por segundo pode atrapalhar): o usuário recebe o aviso
    /// (<see cref="Blocked"/>).
    /// </summary>
    void KeepOnTop()
    {
        if (!Visible || !IsHandleCreated) { _raise.Stop(); return; }
        if (_exclusive) return;
        var above = CoveredBy();
        if (above == IntPtr.Zero) return;
        LogCover(above);
        SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    /// <summary>A primeira janela à vista acima do selo (na ordem do Windows) que cobre o lugar dele; zero se nenhuma. Ignora as escondidas pelo DWM (apps da
    /// loja suspensos) e as que deixam o clique passar (sobreposições transparentes, como a da NVIDIA e a do Discord), que não tampam o selo.</summary>
    IntPtr CoveredBy()
    {
        var me = Bounds;
        for (var h = Native.GetWindow(Handle, Native.GW_HWNDPREV); h != IntPtr.Zero; h = Native.GetWindow(h, Native.GW_HWNDPREV))
        {
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) continue;
            if ((Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64() & WsExTransparent) != 0) continue;
            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            if (!Native.GetWindowRect(h, out var r) || !Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom).IntersectsWith(me)) continue;
            return h;
        }
        return IntPtr.Zero;
    }

    static bool SameProcess(IntPtr a, IntPtr b)
    {
        if (a == IntPtr.Zero || b == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(a, out uint pa); Native.GetWindowThreadProcessId(b, out uint pb);
        return pa == pb;
    }

    /// <summary>Uma linha por programa e por live: qual janela tampou o selo e se ela é "sempre por cima" (a prova, no log, de quem passou por cima).</summary>
    void LogCover(IntPtr h)
    {
        Native.GetWindowThreadProcessId(h, out uint pid);
        if (!_loggedCover.Add(pid)) return;
        string name;
        try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); name = p.ProcessName; } catch { name = "?"; }
        bool topmost = (Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64() & 0x8 /* WS_EX_TOPMOST */) != 0;
        bool front = Native.GetForegroundWindow() == h;
        Log.Info($"[selo] uma janela de {name} ({(topmost ? "sempre por cima" : "comum")}{(front ? ", em primeiro plano" : "")}) ficou sobre o selo; o selo voltou para cima.");
    }

    /// <summary>
    /// A cada segundo, numa live de verdade: o jogo está em tela cheia exclusiva (o Windows não deixa nada por cima) ou insiste em tampar o selo? Avisa uma vez
    /// por live, para o selo não sumir sem explicação.
    /// </summary>
    void CheckBlocked()
    {
        bool exclusive = SHQueryUserNotificationState(out int quns) == 0 && quns == QunsRunningD3dFullScreen;
        if (exclusive != _exclusive)
        {
            _exclusive = exclusive;
            Log.Info(exclusive ? "[selo] Windows diz: jogo em tela cheia exclusiva; nada aparece por cima dele, então o selo não fica à vista."
                               : "[selo] o jogo saiu da tela cheia exclusiva; o selo volta a ficar por cima.");
        }
        // ainda tampado no relógio seguinte, pelo programa em primeiro plano = o jogo se pôs por cima de novo (o menu Iniciar ou a barra de tarefas não contam)
        _coveredTicks = CoveredBy() is var above && above != IntPtr.Zero && SameProcess(above, Native.GetForegroundWindow()) ? _coveredTicks + 1 : 0;
        if (_warned) return;
        string? why = exclusive
            ? "Seu jogo está em tela cheia exclusiva: nesse modo o Windows não deixa o sinal de \"ao vivo\" aparecer por cima dele."
            : _coveredTicks >= CoveredTicksToWarn ? "Seu jogo insiste em ficar por cima de tudo, então o sinal de \"ao vivo\" pode sumir atrás dele." : null;
        if (why == null) return;
        _warned = true;
        Log.Info($"[selo] aviso ao usuário: {(exclusive ? "tela cheia exclusiva" : $"tampado {_coveredTicks} s seguidos")}.");
        Blocked?.Invoke(why + " Para ver o sinal, ponha o jogo em \"janela sem bordas\". A live continua no ar: a barra lateral e a bandeja mostram isso.");
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= WsExNoActivate | WsExToolWindow | WsExTransparent | WsExLayered; return cp; }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (Environment.GetEnvironmentVariable("STREEMU_TEST_SELO") == "visivel") _excluded = true; // só em teste: sem a marca, para a captura de tela do teste poder fotografar o selo
        else if (!SetWindowDisplayAffinity(Handle, WdaExcludeFromCapture))
            Log.Info("[selo] este Windows não deixou marcar o selo como fora da captura; ele não será mostrado (para não aparecer na transmissão).");
        else _excluded = true;
    }

    /// <summary>Liga ou desliga o selo de verdade (o relógio de 1 s reavalia o estado e a posição).</summary>
    public void Run(bool on)
    {
        _enabled = on;
        UpdateClock();
        if (on) Refresh_();
        else if (DateTime.Now >= _sampleUntil) Hide();
    }

    /// <summary>Mostra o selo por alguns segundos no monitor principal, mesmo sem transmitir, para o usuário ver como ficou a escolha dele.</summary>
    public void ShowSample(int seconds = 5)
    {
        _sample = new LiveBadgeState(Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080), 2, DateTimeOffset.UtcNow.AddMinutes(-12), Paused: false);
        _sampleUntil = DateTime.Now.AddSeconds(seconds);
        UpdateClock();
        Refresh_();
    }

    void UpdateClock() => _clock.Enabled = _enabled || DateTime.Now < _sampleUntil;

    void Refresh_()
    {
        bool sampling = DateTime.Now < _sampleUntil;
        var before = _state;
        _state = !_excluded ? null : sampling ? _sample : _enabled ? _read() : null; // sem a marca de "fora da captura" os amigos veriam o selo: melhor não mostrar
        if (_state == null) { if (Visible) Hide(); UpdateClock(); return; }
        if (before == null || before.Since != _state.Since) { _warned = false; _exclusive = false; _coveredTicks = 0; _loggedCover.Clear(); } // live nova: avisa e registra de novo
        _tick = !_tick;
        var size = Measure();
        Size = size;
        using (var path = Shape(new Rectangle(Point.Empty, size))) Region = new Region(path);
        Location = Place(_state.Monitor, size);
        // piscar = alterna a opacidade a cada segundo. Nunca 1,0: com opacidade total o WinForms tira o estilo "layered" da janela e ela deixaria de ser transparente
        // ao clique (o jogo deixaria de receber o mouse nesse canto).
        Opacity = Math.Min(0.99, _look.OpacityPercent / 100.0 * (_look.Blink && !_tick && !_state.Paused ? 0.55 : 1.0));
        if (!sampling) CheckBlocked();
        if (!Visible) Show();
        KeepOnTop();
        _raise.Start();
        Invalidate();
    }

    GraphicsPath Shape(Rectangle r)
    {
        var path = new GraphicsPath();
        if (_look.Style == BadgeStyle.Pill) { using var p = Theme.RoundedPath(r, r.Height / 2); path.AddPath(p, false); }
        else path.AddEllipse(r);
        return path;
    }

    Point Place(Rectangle monitor, Size size)
    {
        int m = LogicalToDeviceUnits(MarginAt96);
        int x = _look.Corner is BadgeCorner.TopLeft or BadgeCorner.BottomLeft ? monitor.Left + m : monitor.Right - m - size.Width;
        int y = _look.Corner is BadgeCorner.TopLeft or BadgeCorner.TopRight ? monitor.Top + m : monitor.Bottom - m - size.Height;
        return new Point(x, y);
    }

    int DotDiameter() => LogicalToDeviceUnits(DotAt96[_look.Size]);

    string Detail()
    {
        if (_state == null) return "";
        if (_state.Paused) return "pausado";
        var time = DateTimeOffset.UtcNow - _state.Since;
        string clock = time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes}:{time.Seconds:00}";
        string watching = _state.Watching switch { 0 => "ninguém ainda", 1 => "1 assistindo", var n => $"{n} assistindo" };
        return $"{watching} · {clock}";
    }

    Font PillFont(FontStyle style) => UiTypography.Create(PillFontPt[_look.Size], style);

    Size Measure()
    {
        if (_look.Style != BadgeStyle.Pill) { int d = DotDiameter(); return new Size(d, d); }
        using var bold = PillFont(FontStyle.Bold);
        using var plain = PillFont(FontStyle.Regular);
        var a = TextRenderer.MeasureText("AO VIVO", bold, Size.Empty, TextFormatFlags.NoPadding);
        var b = TextRenderer.MeasureText(Detail(), plain, Size.Empty, TextFormatFlags.NoPadding);
        int pad = LogicalToDeviceUnits(12), dot = LogicalToDeviceUnits(18);
        return new Size(pad + dot + a.Width + LogicalToDeviceUnits(10) + b.Width + pad, Math.Max(a.Height, b.Height) + LogicalToDeviceUnits(14));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(_state?.Paused == true ? Theme.Neutral : Theme.Error);
        if (_look.Style == BadgeStyle.Pill) { DrawPill(g); return; }
        // um aro claro fino: a bolinha vermelha continua visível sobre fundo escuro, claro ou vermelho
        float w = Math.Max(1.5f, LogicalToDeviceUnits(2));
        using (var edge = new Pen(Theme.MediaEdge, w))
            g.DrawEllipse(edge, new RectangleF(w / 2, w / 2, Width - w - 1, Height - w - 1));
    }

    void DrawPill(Graphics g)
    {
        int pad = LogicalToDeviceUnits(12), d = LogicalToDeviceUnits(9);
        var dot = new Rectangle(pad, (Height - d) / 2, d, d);
        using (var white = new SolidBrush(_tick || _state?.Paused == true ? Theme.OnMedia : Theme.OnMediaDim)) g.FillEllipse(white, dot); // a bolinha pisca, como em gravação
        using var bold = PillFont(FontStyle.Bold);
        using var plain = PillFont(FontStyle.Regular);
        var live = new Rectangle(dot.Right + LogicalToDeviceUnits(8), 0, Width, Height);
        var liveSize = TextRenderer.MeasureText("AO VIVO", bold, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "AO VIVO", bold, live, Theme.OnMedia, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        var detail = new Rectangle(live.X + liveSize.Width + LogicalToDeviceUnits(10), 0, Width, Height);
        TextRenderer.DrawText(g, Detail(), plain, detail, Theme.OnMediaSoft, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _clock.Stop(); _clock.Dispose(); _raise.Stop(); _raise.Dispose(); }
        base.Dispose(disposing);
    }
}
