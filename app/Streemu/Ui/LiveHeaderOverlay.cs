using Streemu.Ui.Multistream;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// Cabeçalho da live POR CIMA do alto do vídeo, quando ela é a única no palco. Aparece inteiro (foto, nome, jogo, AO VIVO) junto com a barra de controles e,
/// quando a barra some, encolhe para só a pílula "● AO VIVO" no canto (pedido de 2026-11-16: cabeçalho e barra fixos tiravam a imersão).
/// É uma janela à parte pelo mesmo motivo da barra: o ffplay desenha com a placa e fica por cima de tudo que é desenhado na janela do app.
/// Uso: <c>header.Place(stage.HeaderOver(viewer), barraVisivel, viewer.Window.Handle, barra.Handle);</c> a cada ciclo da barra.
/// </summary>
internal sealed class LiveHeaderOverlay : Form
{
    const int WsExNoActivate = 0x08000000, WsExToolWindow = 0x80;
    StageCaption? _caption;
    bool _full;
    IntPtr _owner;

    public LiveHeaderOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.SurfaceInput;
        Opacity = 0.93; // o mesmo da barra de controles
        Font = UiTypography.Create(UiTypography.Body);
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        _ = Handle;
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

    /// <summary>Mostra o cabeçalho inteiro (<paramref name="full"/>) ou só a pílula; <paramref name="caption"/> nulo esconde. <paramref name="bar"/>: a barra de controles do mesmo player.</summary>
    public void Place(StageCaption? caption, bool full, IntPtr player, IntPtr bar = default)
    {
        if (caption == null || player == IntPtr.Zero) { if (Visible) Hide(); return; }
        bool changed = caption != _caption || full != _full;
        _caption = caption; _full = full;
        var bounds = full ? caption.Area : LiveCaptionPainter.PillBounds(caption.Area, Dp);
        if (bounds.IsEmpty) { if (Visible) Hide(); return; } // vídeo estreito demais até para o ponto vermelho
        if (Bounds != bounds) Bounds = bounds;
        if (changed) { ShapeAsPill(!full); Invalidate(); }
        PlayerWindowOrder.Follow(this, player, ref _owner);
        if (!Visible) Show();
        PlayerWindowOrder.PlaceAbove(this, player, bar);
    }

    int Dp(int at96) => UiScale.Dp(this, at96);

    /// <summary>Só a pílula: a janela vira a própria pílula arredondada, sem o retângulo de fundo em volta sobre o vídeo.</summary>
    void ShapeAsPill(bool pill)
    {
        var old = Region;
        if (pill) { using var path = Theme.RoundedPath(new Rectangle(Point.Empty, Size), Height / 2); Region = new Region(path); }
        else Region = null;
        old?.Dispose();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!_full && _caption != null) ShapeAsPill(true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Só a pílula: o recorte por região não tem serrilhado suave, então o fundo aparece nas bordas dela sobre o vídeo (contorno branco no claro).
        // Com o fundo da própria cor da pílula, a borda some.
        e.Graphics.Clear(_full ? Theme.SurfaceInput : Theme.Error);
        if (_caption == null) return;
        if (_full) LiveCaptionPainter.Draw(e.Graphics, ClientRectangle, _caption with { Area = ClientRectangle }, Font, Dp, onStage: false);
        else LiveCaptionPainter.DrawPill(e.Graphics, ClientRectangle, Font, Dp);
    }
}
