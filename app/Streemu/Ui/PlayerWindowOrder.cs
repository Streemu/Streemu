using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// Põe uma janela nossa (barra de controles, cabeçalho) colada à janela do ffplay: dona dela e logo acima dela na ordem das janelas.
/// Uso: <c>PlayerWindowOrder.Follow(this, ffplay, ref _owner); PlayerWindowOrder.PlaceAbove(this, ffplay);</c>
/// </summary>
internal static class PlayerWindowOrder
{
    /// <summary>Se o player reabriu (volta para a CPU), a janela do ffplay é outra: o dono muda junto (some e minimiza com ela).</summary>
    public static void Follow(Form overlay, IntPtr player, ref IntPtr owner)
    {
        if (player == IntPtr.Zero || player == owner) return;
        Native.SetWindowLongPtr(overlay.Handle, Native.GWLP_HWNDPARENT, player);
        owner = player;
    }

    /// <summary>
    /// Mantém a janela logo ACIMA do ffplay. O vínculo de dono entre processos não segura isso (medido em 2026-11-06: o ffplay ficava por cima e escondia
    /// a barra), e "sempre no topo" a deixaria sobre outros programas. <paramref name="ours"/> são as outras janelas nossas coladas no mesmo ffplay (barra e
    /// cabeçalho): estando todas encostadas nele, em qualquer ordem, nada muda (sem isso as duas trocariam de lugar a cada ciclo).
    /// </summary>
    public static void PlaceAbove(Form overlay, IntPtr player, params IntPtr[] ours)
    {
        var prev = Native.GetWindow(player, Native.GW_HWNDPREV);
        for (var w = prev; w != IntPtr.Zero && (w == overlay.Handle || Array.IndexOf(ours, w) >= 0); w = Native.GetWindow(w, Native.GW_HWNDPREV))
            if (w == overlay.Handle) return;
        Native.SetWindowPos(overlay.Handle, prev, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }
}
