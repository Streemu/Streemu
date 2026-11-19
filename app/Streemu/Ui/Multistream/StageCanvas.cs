using Streemu.Util;

namespace Streemu.Ui.Multistream;

/// <summary>
/// O fundo do palco na janela principal: preto, com a legenda no alto de cada vídeo e uma mensagem quando não há vídeo à vista. Os vídeos são janelas do
/// ffplay por cima dele (ver <see cref="PlayerStage"/>); aqui só se desenha o que fica entre e acima deles.
/// Uso: <c>canvas.SetCaptions(areasEmPixelsDeTela); canvas.Message = "Nenhuma stream em tela";</c>
/// </summary>
public sealed class StageCanvas : Control
{
    const int SpinMs = 50;
    IReadOnlyList<StageCaption> _captions = Array.Empty<StageCaption>();
    IReadOnlyList<StageLoading> _loading = Array.Empty<StageLoading>();
    // O giro sai do relógio e não de um contador: se o timer atrasar, o arco continua na posição certa. O timer só roda com algo carregando e a tela à vista.
    readonly System.Windows.Forms.Timer _spin = new() { Interval = SpinMs };
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    string _message = "";
    int _dragIndex = -1, _drop = -1;

    /// <summary>Arrastar a legenda de um vídeo: (índice da legenda, posição do mouse em pixels de tela, soltou?). Quem decide o que fazer é o palco.</summary>
    public event Action<int, Point, bool>? CaptionDragged;

    public StageCanvas()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.VideoMargin;
        _spin.Tick += (_, _) => { foreach (var l in _loading) if (!l.TimedOut) Invalidate(RectangleToClient(l.Area)); };
    }

    /// <summary>
    /// Os vídeos que a pessoa abriu e ainda não mostraram o primeiro quadro (a janela do ffplay só aparece com ele): sem isto o palco ficava preto, com o
    /// "Nenhuma stream à vista" de quem não escolheu nada (teste de primeiro uso, 2026-11-19). Áreas em pixels de TELA.
    /// </summary>
    public void SetLoading(IReadOnlyList<StageLoading> loading)
    {
        if (loading.SequenceEqual(_loading)) return;
        _loading = loading;
        UpdateSpin();
        Invalidate();
    }

    void UpdateSpin() => _spin.Enabled = Visible && IsHandleCreated && _loading.Any(l => !l.TimedOut);

    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); UpdateSpin(); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateSpin(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _spin.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Texto no meio quando não há vídeo (ex.: a stream foi para a janela pequena).</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Message
    {
        get => _message;
        set { if (value == _message) return; _message = value; Invalidate(); }
    }

    /// <summary>As legendas ficam no alto de cada vídeo (áreas em pixels de TELA).</summary>
    public void SetCaptions(IReadOnlyList<StageCaption> captions)
    {
        if (captions.SequenceEqual(_captions)) return;
        _captions = captions;
        Invalidate();
    }

    /// <summary>Legenda que vai receber o vídeo arrastado (destacada); -1 limpa. O vídeo em si é outra janela, então o destaque fica na legenda.</summary>
    public void HighlightCaption(int index)
    {
        if (index == _drop) return;
        _drop = index;
        Invalidate();
    }

    int CaptionAt(Point client)
    {
        for (int i = 0; i < _captions.Count; i++) if (RectangleToClient(_captions[i].Area).Contains(client)) return i;
        return -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragIndex = CaptionAt(e.Location);
        if (_dragIndex >= 0) Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragIndex >= 0) { Cursor = Cursors.SizeAll; CaptionDragged?.Invoke(_dragIndex, PointToScreen(e.Location), false); return; }
        Cursor = CaptionAt(e.Location) >= 0 ? Cursors.SizeAll : Cursors.Default; // a legenda é a alça para trocar o vídeo de lugar
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragIndex < 0) return;
        int from = _dragIndex;
        _dragIndex = -1;
        Capture = false;
        CaptionDragged?.Invoke(from, PointToScreen(e.Location), true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.VideoMargin);
        if (_message.Length > 0 && _captions.Count == 0 && _loading.Count == 0)
            TextRenderer.DrawText(e.Graphics, _message, Font, ClientRectangle, Theme.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        for (int i = 0; i < _captions.Count; i++)
        {
            var r = RectangleToClient(_captions[i].Area);
            if (i == _drop) { e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; using var path = Theme.RoundedPath(r, UiScale.Dp(this, 4)); using var back = new SolidBrush(Theme.DropTarget); e.Graphics.FillPath(back, path); }
            LiveCaptionPainter.Draw(e.Graphics, r, _captions[i], Font, n => UiScale.Dp(this, n));
        }
        foreach (var l in _loading) DrawLoading(e.Graphics, RectangleToClient(l.Area), l);
    }

    /// <summary>O gato com um arco girando em volta e a frase embaixo; sem altura para tudo, só o arco e a frase. Parado e sem arco quando já passou do tempo.</summary>
    void DrawLoading(Graphics g, Rectangle r, StageLoading l)
    {
        if (r.Width < 40 || r.Height < 40) return;
        int gap = UiScale.Dp(this, 12);
        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
        int textWidth = Math.Max(1, Math.Min(r.Width - 2 * gap, UiScale.Dp(this, 460)));
        var textSize = TextRenderer.MeasureText(g, l.Text, Font, new Size(textWidth, 0), flags);
        int ring = Math.Min(UiScale.Dp(this, 150), r.Height - textSize.Height - 3 * gap);
        bool art = ring >= UiScale.Dp(this, 60);
        if (!art) ring = Math.Min(UiScale.Dp(this, 36), Math.Max(0, r.Height - textSize.Height - 3 * gap));
        int block = (ring > 0 ? ring + gap : 0) + textSize.Height;
        int top = r.Y + Math.Max(gap, (r.Height - block) / 2);
        var ringBox = new Rectangle(r.X + (r.Width - ring) / 2, top, ring, ring);
        if (ring > 0)
        {
            if (art) UiBrand.DrawMark(g, Rectangle.Inflate(ringBox, -ring / 5, -ring / 5));
            if (!l.TimedOut)
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int w = Math.Max(2, UiScale.Dp(this, 4));
                var arc = Rectangle.Inflate(ringBox, -w, -w);
                using (var track = new Pen(Theme.Border, w)) g.DrawEllipse(track, arc);
                float angle = (float)(_clock.ElapsedMilliseconds % 1000) * 0.36f;
                using var head = new Pen(Theme.Accent, w) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
                g.DrawArc(head, arc, angle - 90, 100);
            }
        }
        var textBox = new Rectangle(r.X + (r.Width - textWidth) / 2, top + (ring > 0 ? ring + gap : 0), textWidth, textSize.Height + gap);
        TextRenderer.DrawText(g, l.Text, Font, textBox, l.TimedOut ? Theme.Text : Theme.TextMuted, flags);
    }
}
