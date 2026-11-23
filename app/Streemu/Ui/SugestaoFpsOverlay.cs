using System.Runtime.InteropServices;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// O card de <see cref="SugestaoFpsCard"/> por cima do jogo, para quem joga em tela cheia e não vai dar Alt+Tab (campo 2026-11-23: o card dentro da janela do
/// Streemu não foi visto). Mesmas regras do <see cref="LiveBadge"/>: não ativa (o jogo segue com teclado e mouse), fica fora do Alt+Tab, é marcada como
/// "fora da captura" (senão o aviso iria PARA A TRANSMISSÃO e os amigos o veriam) e volta ao topo quando o jogo se põe por cima. Em tela cheia exclusiva
/// o Windows não deixa nada aparecer; quem usa decide isso antes (o aviso então sai pela bandeja). Some sozinho depois de <see cref="Duracao"/> sem recusar nada.
/// Uso: <c>if (overlay.Mostrar(monitor, 36, "Ou aperte Ctrl+Alt+3.")) ...; overlay.Aceitou += ...;</c>
/// </summary>
public sealed class SugestaoFpsOverlay : Form
{
    public static readonly TimeSpan Duracao = TimeSpan.FromSeconds(25);
    const int WsExNoActivate = 0x08000000, WsExToolWindow = 0x80, MargemAt96 = 24, RaioAt96 = 8;
    const uint WdaExcludeFromCapture = 0x11, SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoActivate = 0x10, SwpShowWindow = 0x40;
    static readonly IntPtr HwndTopmost = new(-1);
    readonly SugestaoFpsCard _card = new();
    readonly System.Windows.Forms.Timer _topo = new() { Interval = 500 }, _expira = new() { Interval = (int)Duracao.TotalMilliseconds };
    bool _foraDaCaptura;

    public event Action? Aceitou, Recusou, Expirou;

    public SugestaoFpsOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = $"{UiBrand.Name}: oferta de 30 FPS";
        BackColor = Theme.Surface;
        _card.Location = Point.Empty; // sem encaixe: o tamanho do card manda no da janela, e não o contrário
        _card.Aceitou += () => Aceitou?.Invoke();
        _card.Recusou += () => Recusou?.Invoke();
        Controls.Add(_card);
        _ = _card.Handle; // com a janela criada o card mede na escala certa da tela
        _ = Handle; // cria a janela escondida para marcar "fora da captura" antes de aparecer
        _topo.Tick += (_, _) => { if (Visible) SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow); };
        _expira.Tick += (_, _) => { _expira.Stop(); Esconder(); Expirou?.Invoke(); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= WsExNoActivate | WsExToolWindow; return cp; }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // só em teste: sem a marca, a captura de tela do teste consegue fotografar a janela (o mesmo gancho do selo ao vivo)
        if (Environment.GetEnvironmentVariable("STREEMU_TEST_SELO") == "visivel") { _foraDaCaptura = true; return; }
        _foraDaCaptura = SetWindowDisplayAffinity(Handle, WdaExcludeFromCapture);
        if (!_foraDaCaptura) Log.Info("[qualidade] este Windows não deixou marcar a oferta de 30 FPS como fora da captura; ela não será mostrada sobre o jogo (para não aparecer na transmissão).");
    }

    /// <summary>
    /// Mostra o card no canto superior direito de <paramref name="monitor"/>, sem tirar o foco do jogo. Devolve falso se não pode (sem a marca de "fora da
    /// captura" não mostra: o aviso sairia na live).
    /// </summary>
    public bool Mostrar(Rectangle monitor, int quadrosPorSegundo, string? dica)
    {
        if (!_foraDaCaptura) return false;
        _card.Mostrar(quadrosPorSegundo, dica);
        ClientSize = _card.Size;
        using (var path = Theme.RoundedPath(new Rectangle(0, 0, Width, Height), UiScale.Dp(this, RaioAt96))) Region = new Region(path);
        int margem = UiScale.Dp(this, MargemAt96);
        Location = new Point(Math.Max(monitor.Left, monitor.Right - Width - margem), monitor.Top + margem);
        if (!Visible) Show(); // ShowWithoutActivation: aparece sem tirar o foco do jogo
        SetWindowPos(Handle, HwndTopmost, Left, Top, Width, Height, SwpNoActivate | SwpShowWindow);
        _expira.Stop(); _expira.Start(); _topo.Start();
        return true;
    }

    public void Esconder()
    {
        _topo.Stop(); _expira.Stop();
        if (Visible) Hide();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _topo.Dispose(); _expira.Dispose(); }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
