using Streemu.Ui.Avatars;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// Lista rolável de todo mundo da sala, aberta pelo "+N" da faixa (cabe até 99 pessoas: mostra 8 linhas e rola o resto).
/// Não rouba o foco (o teclado continua no player). Quem abre decide quando fecha.
/// Uso: <c>popup.ShowAbove(overlay, chipScreenBounds, entries);</c>
/// </summary>
public sealed class RosterPopup : Form
{
    const int RowAt96 = 28, WidthAt96 = 240, MaxVisibleRows = 8;
    const int WsExNoActivate = 0x08000000, WsExToolWindow = 0x80;

    readonly ListBox _list = new()
    {
        DrawMode = DrawMode.OwnerDrawFixed, BorderStyle = BorderStyle.None, IntegralHeight = false,
        Dock = DockStyle.Fill, BackColor = Theme.SurfaceRaised, ForeColor = Theme.Text, TabStop = false,
    };
    readonly System.Windows.Forms.Timer _clock = new() { Interval = 100 };
    IReadOnlyList<RosterEntry> _entries = Array.Empty<RosterEntry>();
    int _hoverRow = -1;
    /// <summary>Quem foi clicado para ficar animado (GIF parado por padrão; clicar de novo solta).</summary>
    string? _pinned;

    public RosterPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Neutral;
        Padding = new Padding(1);
        _list.ItemHeight = LogicalToDeviceUnits(RowAt96);
        _list.DrawItem += DrawRow;
        _list.MouseMove += (_, e) => SetHoverRow(_list.IndexFromPoint(e.Location));
        _list.MouseLeave += (_, _) => SetHoverRow(-1);
        _list.MouseClick += (_, e) => TogglePin(_list.IndexFromPoint(e.Location));
        _clock.Tick += (_, _) => { DropStaleHover(); InvalidateAnimatedRows(); };
        Controls.Add(_list);
        // A barra de controles cria a lista junto com ela e só a abre bem depois: sem janela criada, a troca de tema (ThemeSwitch) não a encontra e a
        // lista abria com as cores do tema antigo (2026-11-20).
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

    public void SetEntries(IReadOnlyList<RosterEntry> entries)
    {
        if (_entries.SequenceEqual(entries)) return;
        _entries = entries;
        int top = _list.TopIndex;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in entries) _list.Items.Add(e.Nick);
        _list.TopIndex = Math.Min(top, Math.Max(0, _list.Items.Count - 1));
        _list.EndUpdate();
    }

    /// <summary>Abre acima do "+N" (o rodapé do vídeo), sem passar da borda do vídeo nem da tela.</summary>
    public void ShowAbove(Form owner, Rectangle chipOnScreen, IReadOnlyList<RosterEntry> entries)
    {
        SetEntries(entries);
        int rows = Math.Clamp(entries.Count, 1, MaxVisibleRows);
        var size = new Size(LogicalToDeviceUnits(WidthAt96), rows * _list.ItemHeight + 2);
        var screen = Screen.FromRectangle(chipOnScreen).WorkingArea;
        int left = Math.Max(screen.Left, owner.Left), right = Math.Min(screen.Right, owner.Right); // na horizontal, dentro do vídeo
        int x = Math.Clamp(chipOnScreen.X, left, Math.Max(left, right - size.Width));
        Bounds = new Rectangle(x, Math.Max(screen.Top, chipOnScreen.Top - size.Height - 6), size.Width, size.Height);
        if (!Visible) Show(owner); // dono = a barra: fica sempre acima dela
    }

    /// <summary>
    /// Um único repaint por tick, só na área das linhas com GIF que estão na tela. Invalidar linha por linha gerava um repaint
    /// COMPLETO da lista para cada GIF (2 ms cada): com 3 GIFs eram ~5% de um núcleo (medido em 2026-11-07).
    /// </summary>
    void InvalidateAnimatedRows()
    {
        var area = Rectangle.Empty;
        int last = Math.Min(_entries.Count, _list.TopIndex + MaxVisibleRows);
        for (int i = _list.TopIndex; i < last; i++)
        {
            if (_entries[i].Avatar is not { Animated: true } || !IsAnimating(i)) continue;
            var row = _list.GetItemRectangle(i);
            area = area.IsEmpty ? row : Rectangle.Union(area, row);
        }
        if (!area.IsEmpty) _list.Invalidate(area);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) { _hoverRow = -1; _pinned = null; }
        _clock.Enabled = Visible;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _clock.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Solta o hover se o mouse já não está na lista (o evento "saiu" não vem se a lista some com o mouse em cima).</summary>
    void DropStaleHover()
    {
        if (_hoverRow >= 0 && !PointerProbe.IsOver(_list, _list.ClientRectangle)) SetHoverRow(-1);
    }

    bool IsAnimating(int index) => index >= 0 && index < _entries.Count && (index == _hoverRow || _entries[index].Key == _pinned);

    /// <summary>O GIF da linha sob o mouse anima; ao sair volta ao primeiro quadro (repinta a linha que saiu e a que entrou).</summary>
    void SetHoverRow(int row)
    {
        if (row < 0 || row >= _entries.Count) row = -1;
        if (row == _hoverRow) return;
        int old = _hoverRow;
        _hoverRow = row;
        foreach (var i in new[] { old, row }) if (i >= 0 && i < _list.Items.Count) _list.Invalidate(_list.GetItemRectangle(i));
    }

    void TogglePin(int row)
    {
        if (row < 0 || row >= _entries.Count || _entries[row].Avatar is not { Animated: true }) return;
        _pinned = _pinned == _entries[row].Key ? null : _entries[row].Key;
        _list.Invalidate(_list.GetItemRectangle(row));
    }

    void DrawRow(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _entries.Count) return;
        var entry = _entries[e.Index];
        using (var back = new SolidBrush(Theme.SurfaceRaised)) e.Graphics.FillRectangle(back, e.Bounds);
        int dot = e.Bounds.Height - LogicalToDeviceUnits(10);
        var dotRect = new Rectangle(e.Bounds.X + LogicalToDeviceUnits(8), e.Bounds.Y + (e.Bounds.Height - dot) / 2, dot, dot);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        AvatarBadge.Draw(e.Graphics, dotRect, entry.Avatar, entry.Nick, IsAnimating(e.Index) ? AvatarBadge.NowMs : 0);
        DrawLabel(e, entry, dotRect);
    }

    void DrawLabel(DrawItemEventArgs e, RosterEntry entry, Rectangle dotRect)
    {
        const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        int liveWidth = LogicalToDeviceUnits(52);
        var text = new Rectangle(dotRect.Right + LogicalToDeviceUnits(8), e.Bounds.Y, e.Bounds.Right - dotRect.Right - LogicalToDeviceUnits(8) - liveWidth, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, entry.Nick + (entry.IsMe ? " (você)" : ""), e.Font, text, Theme.Text, flags);
        if (!entry.Live) return;
        TextRenderer.DrawText(e.Graphics, "LIVE", e.Font, new Rectangle(text.Right, e.Bounds.Y, liveWidth, e.Bounds.Height), Theme.Error, flags);
    }
}
