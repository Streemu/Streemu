using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// Lista de escolha (ComboBox) com o desenho do <see cref="CategoryPicker"/>: cantos arredondados, borda fina e seta de linha. O ComboBox do Windows, mesmo
/// plano, desenhava um quadrado branco na seta e cantos retos que destoavam do resto do app (pedido de 2026-11-16). A parte fechada é pintada aqui,
/// por cima do WM_PAINT; a lista aberta, teclado e acessibilidade continuam os do Windows.
/// Uso: <c>readonly ComboBox _preset = new RoundedComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };</c>
/// </summary>
public sealed class RoundedComboBox : ComboBox
{
    const int WmPaint = 0x000F, WmPrintClient = 0x0318;
    bool _hover;

    public RoundedComboBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        Cursor = Cursors.Hand;
        ItemHeight = 26;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ItemHeight = UiScale.Dp(this, 26); // a mesma altura do botão Atualizar ao lado
    }

    protected override void WndProc(ref Message m)
    {
        // WM_PRINTCLIENT: DrawToBitmap (fotos dos testes) pede a pintura num HDC dado, sem passar pelo WM_PAINT
        if (m.Msg == WmPrintClient) { using var printed = Graphics.FromHdc(m.WParam); DrawClosed(printed); return; }
        if (m.Msg != WmPaint) { base.WndProc(ref m); return; }
        var paint = new PaintStruct();
        var hdc = BeginPaint(Handle, ref paint);
        try { using var g = Graphics.FromHdc(hdc); DrawClosed(g); }
        finally { EndPaint(Handle, ref paint); }
    }

    /// <summary>A parte fechada: fundo de campo, borda (destaque com foco ou aberta), texto e a seta.</summary>
    void DrawClosed(Graphics screen)
    {
        // 24 bpp (sem canal alfa) de propósito: em bitmap com alfa o Windows desliga o ClearType e o texto escuro sobre fundo claro sai grosso e borrado
        using var buffer = new Bitmap(Math.Max(1, Width), Math.Max(1, Height), System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(buffer))
        {
            g.Clear(Parent?.BackColor ?? Theme.Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool active = Focused || DroppedDown;
            using var shape = Theme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), UiScale.Dp(this, UiMetrics.SmallRadius));
            using var fill = new SolidBrush(Theme.SurfaceInput);
            using var edge = new Pen(active ? Theme.Accent : _hover ? Theme.TextMuted : Theme.Border, active ? 2 : 1);
            g.FillPath(fill, shape);
            g.DrawPath(edge, shape);
            int pad = UiScale.Dp(this, UiMetrics.Gap), arrow = UiScale.Dp(this, 16);
            UiIcons.Draw(g, UiIcon.ChevronDown, new Rectangle(Width - pad - arrow, (Height - arrow) / 2, arrow, arrow), Enabled ? Theme.Text : Theme.TextMuted);
        }
        screen.DrawImageUnscaled(buffer, 0, 0);
        // O texto vai direto para o contexto da tela, NÃO para o bitmap de cima: o TextRenderer num bitmap perde o ClearType e usa tons de cinza, e texto
        // escuro sobre fundo claro sai grosso e borrado (tema claro, vídeo de 2026-11-18).
        int textPad = UiScale.Dp(this, UiMetrics.Gap), textArrow = UiScale.Dp(this, 16);
        var text = new Rectangle(textPad, 0, Math.Max(1, Width - textPad * 2 - textArrow), Height);
        TextRenderer.DrawText(screen, SelectedItem == null ? "" : GetItemText(SelectedItem), Font, text, Enabled ? Theme.Text : Theme.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    /// <summary>Itens da lista aberta na paleta do app (a escolhida em destaque), em vez do azul e branco do Windows.</summary>
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        // A área fechada (ComboBoxEdit) já é pintada inteira por DrawClosed: desenhar o item de novo aqui escrevia o mesmo texto 4 px ao lado, por cima,
        // e o texto saía com cara de negrito borrado, mais visível no tema claro (vídeo de 2026-11-18).
        if (e.Index < 0 || (e.State & DrawItemState.ComboBoxEdit) != 0) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using var back = new SolidBrush(selected ? Theme.Accent : Theme.SurfaceInput);
        e.Graphics.FillRectangle(back, e.Bounds);
        var text = Rectangle.FromLTRB(e.Bounds.X + UiScale.Dp(this, 8), e.Bounds.Y, e.Bounds.Right, e.Bounds.Bottom);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, text, selected ? Theme.AccentText : Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); Invalidate(); }
    protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }

    [StructLayout(LayoutKind.Sequential)]
    struct PaintStruct
    {
        public IntPtr Hdc;
        public bool Erase;
        public int Left, Top, Right, Bottom;
        public bool Restore, IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
    }

    [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr hwnd, ref PaintStruct paint);
    [DllImport("user32.dll")] static extern bool EndPaint(IntPtr hwnd, ref PaintStruct paint);
}
