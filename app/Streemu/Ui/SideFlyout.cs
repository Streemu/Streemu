namespace Streemu.Ui;

/// <summary>
/// A barra lateral aberta por cima do conteúdo, para quando ela está recolhida em trilho: aparece ao passar o mouse sobre o trilho e some quando o mouse sai
/// (quem decide é a janela principal). É uma janela à parte, dependente da principal, porque os vídeos (ffplay) são janelas por cima da principal e uma
/// barra desenhada dentro dela ficaria escondida atrás deles. Não rouba o foco, para o jogo ou o vídeo não perderem o teclado.
/// Abre deslizando da esquerda e ficando visível (150 ms) e fecha ao contrário (110 ms), em vez de brotar de uma vez.
/// Uso: <c>flyout.Panel.FriendClicked += ...; flyout.ShowAt(bounds, owner); flyout.Dismiss();</c>
/// </summary>
public sealed class SideFlyout : Form
{
    const int WsExNoActivate = 0x08000000, WsExToolWindow = 0x80;
    const int SlideAt96 = 40, OpenMs = 150, CloseMs = 110, FrameMs = 15;

    readonly System.Windows.Forms.Timer _anim = new() { Interval = FrameMs };
    readonly System.Diagnostics.Stopwatch _clock = new();
    double _lastMs;
    Rectangle _home;
    double _progress;
    bool _opening;

    /// <summary>O mesmo desenho da barra aberta, ligado aos mesmos eventos da barra da janela principal.</summary>
    public SidePanel Panel { get; } = new() { Dock = DockStyle.Fill };

    /// <summary>Está fechando (a animação de saída roda): se o mouse voltar, a janela principal pede para abrir de novo.</summary>
    public bool Closing => Visible && !_opening;

    public SideFlyout()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Util.Theme.SurfaceRaised;
        Text = $"{Util.UiBrand.Name}: barra lateral";
        Controls.Add(Panel);
        _anim.Tick += (_, _) => Step();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= WsExNoActivate | WsExToolWindow; return cp; }
    }

    /// <summary>
    /// Abre a barra por cima de tudo da janela principal. Sem "sempre por cima" ela ficava ATRÁS dos vídeos (que são janelas do ffplay, também dependentes
    /// da principal): a faixa da direita da barra era coberta por eles (visto em teste com 2 streams, 2026-11-09). Só fica por cima enquanto está aberta.
    /// </summary>
    public void ShowAt(Rectangle bounds, Form owner)
    {
        _home = bounds;
        if (!Visible)
        {
            _progress = 0;
            Apply();
            TopMost = true;
            Show(owner);
        }
        _opening = true;
        StartClock();
    }

    /// <summary>Reinicia o relógio da animação: o progresso anda pelo TEMPO que passou, não por tique, para não alongar quando a tela atrasa um quadro.</summary>
    void StartClock()
    {
        _clock.Restart();
        _lastMs = 0;
        _anim.Start();
    }

    /// <summary>Fecha com a animação de saída (some sozinha no fim).</summary>
    public void Dismiss()
    {
        if (!Visible) return;
        _opening = false;
        StartClock();
    }

    void Step()
    {
        double duration = _opening ? OpenMs : CloseMs, now = _clock.Elapsed.TotalMilliseconds;
        _progress = Math.Clamp(_progress + (_opening ? 1 : -1) * (now - _lastMs) / duration, 0, 1);
        _lastMs = now;
        Apply();
        if (_opening && _progress >= 1) _anim.Stop();
        if (!_opening && _progress <= 0) { _anim.Stop(); Hide(); }
    }

    /// <summary>Posição e transparência de agora: sai da esquerda (40 px) e vai ficando visível, começando rápido e terminando suave.</summary>
    void Apply()
    {
        double eased = 1 - Math.Pow(1 - _progress, 3);
        int dx = (int)Math.Round((1 - eased) * LogicalToDeviceUnits(SlideAt96));
        Bounds = new Rectangle(_home.Left - dx, _home.Top, _home.Width, _home.Height);
        Opacity = eased;
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) { TopMost = false; _anim.Stop(); } // fechada, volta a ser uma janela comum (não fica por cima de outros programas)
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _anim.Stop(); _anim.Dispose(); }
        base.Dispose(disposing);
    }
}
