using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui.Avatars;

/// <summary>
/// Desenha um avatar redondo: a imagem (animada se for GIF) ou, sem imagem, o círculo colorido com a inicial do apelido.
/// GIF fica PARADO no primeiro quadro e só anima com o mouse em cima ou depois de um clique (clicar de novo solta): com muitos amigos
/// com GIF, nada se mexe sozinho. A faixa e a lista reaproveitam <see cref="Draw"/>.
/// Uso: <c>var badge = new AvatarBadge(() =&gt; apelido) { Size = new Size(36, 36), Image = store.Image };</c>
/// </summary>
public sealed class AvatarBadge : Control
{
    const int FrameMs = 100;
    readonly Func<string> _nick;
    readonly System.Windows.Forms.Timer _clock = new() { Interval = FrameMs };
    /// <summary>Um relógio só para todos os desenhos: o mesmo GIF fica no mesmo quadro na faixa, na lista e no card.</summary>
    static readonly Stopwatch Shared = Stopwatch.StartNew();
    AvatarImage? _image;
    bool _hover, _pinned;

    public AvatarBadge(Func<string> nick)
    {
        _nick = nick;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _clock.Tick += (_, _) => { DropStaleHover(); Invalidate(); };
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public AvatarImage? Image
    {
        get => _image;
        set { _image = value; Cursor = value is { Animated: true } ? Cursors.Hand : Cursors.Default; UpdateClock(); Invalidate(); }
    }

    /// <summary>Milissegundos no relógio compartilhado: define o quadro do GIF.</summary>
    public static long NowMs => Shared.ElapsedMilliseconds;

    /// <summary>Desenha o avatar (ou a inicial colorida) dentro de <paramref name="bounds"/>.</summary>
    public static void Draw(Graphics g, Rectangle bounds, AvatarImage? image, string nick, long elapsedMs)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (image != null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(image.FrameAt(elapsedMs), bounds);
            return;
        }
        using (var fill = new SolidBrush(RosterStrip.ColorOf(nick))) g.FillEllipse(fill, bounds);
        using var font = UiTypography.Create(Math.Max(7f, bounds.Height * 0.3f * 72f / 96f), FontStyle.Bold, GraphicsUnit.Point);
        // PreserveGraphicsClipping: na lista rolável da lateral a inicial do amigo cortado na borda vazava por cima do rodapé
        TextRenderer.DrawText(g, RosterStrip.Initial(nick), font, bounds, Theme.OnMedia,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        Draw(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), _image, _nick(), Animating ? NowMs : 0);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) { _hover = false; _pinned = false; }
        UpdateClock();
    }

    /// <summary>Solta o hover se o mouse já não está sobre o badge (o evento "saiu" não vem se a janela some com o mouse em cima).</summary>
    void DropStaleHover()
    {
        if (!_hover || PointerProbe.IsOver(this, ClientRectangle)) return;
        _hover = false;
        UpdateClock();
    }

    bool Animating => _image is { Animated: true } && (_hover || _pinned);

    void UpdateClock() => _clock.Enabled = Animating && Visible;

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        UpdateClock();
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        UpdateClock();
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (_image is not { Animated: true }) return;
        _pinned = !_pinned;
        UpdateClock();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _clock.Dispose();
        base.Dispose(disposing);
    }
}
