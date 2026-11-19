using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Streemu.Util;

/// <summary>Um relógio por thread de UI, parado quando não há transições. Só retém controles enquanto animam.</summary>
public sealed class UiMotion : IDisposable
{
    public const int HoverMs = 140, PressMs = 80, FrameMs = 16, ThemeMs = 260;
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SystemParametersInfo(uint action, uint param, out int value, uint flags);
    public static bool Enabled => SystemParametersInfo(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, out int enabled, 0) && enabled != 0;
    [ThreadStatic] static HashSet<UiMotion>? _active;
    [ThreadStatic] static System.Windows.Forms.Timer? _clock;
    readonly Control _owner;
    readonly Action _changed;
    float _from, _target;
    long _started;
    int _duration;
    bool _disposed;
    public float Value { get; private set; }

    public UiMotion(Control owner, Action changed)
    {
        _owner = owner; _changed = changed;
        owner.VisibleChanged += VisibilityChanged; owner.Disposed += OwnerDisposed;
    }
    public void Snap(float value) { _target = value; Finish(); }
    bool CanAnimate => !_owner.IsDisposed && _owner.IsHandleCreated && _owner.Visible && _owner.FindForm()?.WindowState != FormWindowState.Minimized;
    public void To(float target, int milliseconds = HoverMs)
    {
        if (_disposed) return;
        if (_target == target && Value == target) return;
        _from = Value; _target = target; _duration = Math.Max(1, milliseconds); _started = Stopwatch.GetTimestamp();
        if (!CanAnimate || !Enabled) { Finish(); return; }
        _active ??= new(); _active.Add(this);
        if (_clock == null) { _clock = new() { Interval = FrameMs }; _clock.Tick += Tick; }
        _clock.Start();
    }
    static void Tick(object? sender, EventArgs e)
    {
        bool enabled = Enabled;
        foreach (var motion in _active!.ToArray())
        {
            if (!motion.CanAnimate || !enabled) { motion.Finish(); continue; }
            double t = Math.Min(1, Stopwatch.GetElapsedTime(motion._started).TotalMilliseconds / motion._duration);
            motion.Value = motion._from + (motion._target - motion._from) * (float)(1 - Math.Pow(1 - t, 3));
            motion._changed();
            if (t >= 1) motion.Remove();
        }
    }
    void Finish() { Value = _target; Remove(); if (!_owner.IsDisposed) _changed(); }
    void Remove()
    {
        _active?.Remove(this);
        if (_active?.Count == 0 && _clock != null) { _clock.Stop(); _clock.Dispose(); _clock = null; }
    }
    void VisibilityChanged(object? s, EventArgs e) { if (!CanAnimate) Finish(); }
    void OwnerDisposed(object? s, EventArgs e) => Dispose();
    public void Dispose() { _disposed = true; Remove(); _owner.VisibleChanged -= VisibilityChanged; _owner.Disposed -= OwnerDisposed; }
    public static Color Blend(Color a, Color b, float amount) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));
}
