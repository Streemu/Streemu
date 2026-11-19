using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// Uma arte embutida (<see cref="UiBrand"/>) quadrada, centrada no espaço do controle. Só decodifica quando aparece e reduz uma vez por tamanho; ao
/// esconder libera tudo, porque o Início vazio é o único lugar dela e o app passa a maior parte do tempo com alguém ao vivo ou na bandeja.
/// </summary>
public sealed class ArtView : Control
{
    readonly string _name;
    Bitmap? _source, _scaled;

    public ArtView(string name)
    {
        _name = name;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        TabStop = false;
        Theme.Changed += OnThemeChanged;
    }

    /// <summary>No tema claro a Mumu é a versão clara (ver <see cref="UiBrand.ThemedName"/>); a decodificada vale só para o tema em que foi lida.</summary>
    void OnThemeChanged()
    {
        Release();
        Invalidate();
    }

    /// <summary>Se a arte já foi decodificada (para o teste conferir que esconder libera).</summary>
    public bool Loaded => _source != null;

    protected override void OnPaint(PaintEventArgs e)
    {
        int side = Math.Min(Width, Height);
        if (side <= 0) return;
        try { _source ??= UiBrand.LoadArt(UiBrand.ThemedName(_name)); }
        catch (Exception ex) { Log.Info($"Arte {_name} não carregou: {ex.Message}"); Visible = false; return; }
        if (_scaled == null || _scaled.Width != side) { _scaled?.Dispose(); _scaled = UiBrand.Scale(_source, side); }
        e.Graphics.DrawImage(_scaled, (Width - side) / 2, (Height - side) / 2, side, side);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) Release();
    }

    void Release()
    {
        _scaled?.Dispose(); _scaled = null;
        _source?.Dispose(); _source = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Theme.Changed -= OnThemeChanged; Release(); }
        base.Dispose(disposing);
    }
}
