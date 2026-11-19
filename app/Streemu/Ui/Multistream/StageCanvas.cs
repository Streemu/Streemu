using Streemu.Util;

namespace Streemu.Ui.Multistream;

/// <summary>
/// O fundo do palco na janela principal: preto, com a legenda no alto de cada vídeo e uma mensagem quando não há vídeo à vista. Os vídeos são janelas do
/// ffplay por cima dele (ver <see cref="PlayerStage"/>); aqui só se desenha o que fica entre e acima deles.
/// Uso: <c>canvas.SetCaptions(areasEmPixelsDeTela); canvas.Message = "Nenhuma stream em tela";</c>
/// </summary>
public sealed class StageCanvas : Control
{
    IReadOnlyList<StageCaption> _captions = Array.Empty<StageCaption>();
    string _message = "";
    int _dragIndex = -1, _drop = -1;

    /// <summary>Arrastar a legenda de um vídeo: (índice da legenda, posição do mouse em pixels de tela, soltou?). Quem decide o que fazer é o palco.</summary>
    public event Action<int, Point, bool>? CaptionDragged;

    public StageCanvas()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.VideoMargin;
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
        if (_message.Length > 0 && _captions.Count == 0)
            TextRenderer.DrawText(e.Graphics, _message, Font, ClientRectangle, Theme.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        for (int i = 0; i < _captions.Count; i++)
        {
            var r = RectangleToClient(_captions[i].Area);
            if (i == _drop) { e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; using var path = Theme.RoundedPath(r, UiScale.Dp(this, 4)); using var back = new SolidBrush(Theme.DropTarget); e.Graphics.FillPath(back, path); }
            LiveCaptionPainter.Draw(e.Graphics, r, _captions[i], Font, n => UiScale.Dp(this, n));
        }
    }
}
