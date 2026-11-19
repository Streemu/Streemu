using System.Diagnostics;
using System.Drawing.Imaging;

namespace Streemu.Util;

/// <summary>
/// A transição suave entre os temas: uma foto da janela cobre a área cliente enquanto o tema novo é aplicado por baixo, e some aos poucos.
/// Animar a cor de cada controle travaria (centenas de repinturas), e a foto custa uma cópia de tela. A foto é um controle filho da própria janela
/// (uma janela separada por cima não apareceu na gravação de 2026-11-18, e a troca saía seca), pode ter um buraco
/// (o botão de tema, que anima o puxador ao vivo, e que continua clicável). Sem foto (janela minimizada) a troca é seca.
/// Uso: <c>ThemeFade.Run(form, () => aplicarTema(), holeOnScreen: botao);</c>
/// </summary>
internal sealed class ThemeFade : Control
{
    const int FadeMs = 320, FrameMs = 15;
    readonly Bitmap _photo;
    readonly Stopwatch _clock = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = FrameMs };
    readonly ImageAttributes _blend = new();
    float _opacity = 1;

    ThemeFade(Bitmap photo, Rectangle bounds, Rectangle? holeInside)
    {
        _photo = photo;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        TabStop = false;
        Bounds = bounds;
        if (holeInside is { } hole) ApplyHole(hole);
        _timer.Tick += (_, _) => Step();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        float[][] matrix = [[1, 0, 0, 0, 0], [0, 1, 0, 0, 0], [0, 0, 1, 0, 0], [0, 0, 0, _opacity, 0], [0, 0, 0, 0, 1]];
        _blend.SetColorMatrix(new ColorMatrix(matrix));
        e.Graphics.DrawImage(_photo, new Rectangle(0, 0, Width, Height), 0, 0, _photo.Width, _photo.Height, GraphicsUnit.Pixel, _blend);
    }

    void ApplyHole(Rectangle hole)
    {
        using var everything = new Region(new Rectangle(0, 0, Width, Height));
        everything.Exclude(hole);
        Region = everything;
    }

    void Step()
    {
        double t = Math.Min(1, _clock.Elapsed.TotalMilliseconds / FadeMs);
        _opacity = (float)Math.Pow(1 - t, 2);
        if (t >= 1) { Finish(); return; }
        Invalidate();
        Update();
    }

    void Finish()
    {
        _timer.Stop();
        Parent?.Controls.Remove(this);
        Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _photo.Dispose(); _blend.Dispose(); }
        base.Dispose(disposing);
    }

    static bool CanFade(Form owner) =>
        owner.IsHandleCreated && owner.Visible && owner.WindowState != FormWindowState.Minimized && owner.ClientSize.Width > 0 && owner.ClientSize.Height > 0;

    /// <summary>Foto da janela, <paramref name="change"/> por baixo dela e o sumir. <paramref name="holeOnScreen"/> (coordenadas da tela) fica sem foto.</summary>
    public static void Run(Form owner, Action change, Rectangle? holeOnScreen = null)
    {
        ThemeFade? fade = null;
        try { if (CanFade(owner)) fade = Cover(owner, holeOnScreen); }
        catch (Exception ex) { Log.Info($"Transição de tema sem foto: {ex.Message}"); }
        try { change(); owner.Update(); }
        catch { fade?.Finish(); throw; }
        if (fade == null) return;
        fade._clock.Start(); fade._timer.Start();
    }

    static ThemeFade Cover(Form owner, Rectangle? holeOnScreen)
    {
        var area = owner.RectangleToScreen(owner.ClientRectangle);
        var photo = new Bitmap(area.Width, area.Height);
        using (var g = Graphics.FromImage(photo)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
        Rectangle? hole = holeOnScreen is { } h ? new Rectangle(h.X - area.X, h.Y - area.Y, h.Width, h.Height) : null;
        var fade = new ThemeFade(photo, owner.ClientRectangle, hole);
        owner.Controls.Add(fade);
        fade.BringToFront();
        fade.Update();
        return fade;
    }
}
