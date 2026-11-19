using System.ComponentModel;

namespace Streemu.Ui;

/// <summary>
/// Barra de volume do player (0 a 100) no tema do app. O TrackBar do Windows ignora as cores do tema. Serve também à opacidade do selo nas Configurações
/// (<see cref="Minimum"/> 30), no lugar do TrackBar quadrado com marquinhas (pedido de 2026-11-16). Setas e Page Up/Down mudam o valor com o foco nela.
/// Uso: <c>slider.ValueChanged += v =&gt; audio.SetPercent(v);</c>
/// </summary>
public sealed class VolumeSlider : Control
{
    const int TrackHeightAt96 = 4;
    const int ThumbSizeAt96 = 12;
    bool _dragging;
    int _value = 100, _minimum;

    public event Action<int>? ValueChanged;

    public VolumeSlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.Slider;
    }

    int ThumbSize => LogicalToDeviceUnits(ThumbSizeAt96);
    int TrackHeight => Math.Max(2, LogicalToDeviceUnits(TrackHeightAt96));

    /// <summary>O menor valor (o maior é sempre 100). Ex.: <c>new VolumeSlider { Minimum = 30 }</c> para a opacidade.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Minimum
    {
        get => _minimum;
        set { _minimum = Math.Clamp(value, 0, 99); _value = Math.Max(_value, _minimum); Invalidate(); }
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Muted { get; set; }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set { _value = Math.Clamp(value, _minimum, 100); AccessibleDescription = $"{_value}%"; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int y = Height / 2, left = ThumbSize / 2, span = Width - ThumbSize;
        int x = left + span * (Muted ? 0 : _value - _minimum) / (100 - _minimum);
        using var rail = new SolidBrush(Util.Theme.Neutral);
        using var fill = new SolidBrush(Muted ? Util.Theme.Error : Util.Theme.Accent);
        e.Graphics.FillRectangle(rail, left, y - TrackHeight / 2, span, TrackHeight);
        e.Graphics.FillRectangle(fill, left, y - TrackHeight / 2, x - left, TrackHeight);
        e.Graphics.FillEllipse(Brushes.White, x - ThumbSize / 2, y - ThumbSize / 2, ThumbSize, ThumbSize);
        if (Focused && ShowFocusCues) { using var ring = new Pen(Util.Theme.Accent, 2); e.Graphics.DrawEllipse(ring, x - ThumbSize / 2 - 2, y - ThumbSize / 2 - 2, ThumbSize + 4, ThumbSize + 4); }
    }

    protected override void OnMouseDown(MouseEventArgs e) { if (CanFocus && GetStyle(ControlStyles.Selectable) && TabStop) Focus(); _dragging = true; Capture = true; SetFromMouse(e.X); }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int step = e.KeyCode switch { Keys.Left or Keys.Down => -5, Keys.Right or Keys.Up => 5, Keys.PageDown => -10, Keys.PageUp => 10, _ => 0 };
        if (step == 0) { base.OnKeyDown(e); return; }
        SetValue(_value + step);
        e.Handled = true;
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnMouseMove(MouseEventArgs e) { if (_dragging) SetFromMouse(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { _dragging = false; Capture = false; }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        SetValue(_value + (e.Delta > 0 ? 5 : -5));
    }

    void SetFromMouse(int x)
    {
        int left = ThumbSize / 2, span = Math.Max(1, Width - ThumbSize);
        SetValue(_minimum + (int)Math.Round((x - left) * (100.0 - _minimum) / span));
    }

    void SetValue(int value)
    {
        int v = Math.Clamp(value, _minimum, 100);
        if (v == _value && !Muted) return;
        _value = v;
        AccessibleDescription = $"{v}%";
        Invalidate();
        ValueChanged?.Invoke(v);
    }
}
