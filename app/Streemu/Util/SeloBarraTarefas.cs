using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Streemu.Util;

/// <summary>
/// Selo da atividade no canto do botão da barra de tarefas (<c>ITaskbarList3.SetOverlayIcon</c>). Medido em 2026-11-19: trocar o <c>Form.Icon</c>
/// não muda o botão, porque o Windows o desenha com o ícone do atalho do Menu Iniciar, lido do cache de ícones do Explorer. O selo é a forma
/// que o Windows oferece de mostrar estado ali, e não depende desse cache. Escolha e desenho (testáveis, sem COM) ficam separados da chamada ao Explorer.
/// </summary>
public static class SeloBarraTarefas
{
    /// <summary>Ocioso não tem selo: o botão fica só com o ícone do app.</summary>
    public static bool TemSelo(AtividadeIcone atividade) => atividade != AtividadeIcone.Ocioso;

    /// <summary>Texto lido por leitores de tela e mostrado como dica do selo.</summary>
    public static string Descricao(AtividadeIcone atividade) => atividade switch
    {
        AtividadeIcone.Transmitindo => "Transmitindo",
        AtividadeIcone.Assistindo => "Assistindo uma live",
        AtividadeIcone.Cinema => "No Cinema",
        _ => "",
    };

    static readonly Dictionary<(AtividadeIcone, int), Icon> _cache = new();

    /// <summary>
    /// O selo no tamanho pedido (16 px; 32 px em tela de DPI alto), ou null para Ocioso. Cada um é criado uma vez e fica vivo o processo todo:
    /// o Explorer copia o ícone ao receber o handle, mas reaproveitar evita um handle GDI novo a cada mudança de atividade.
    /// </summary>
    public static Icon? Criar(AtividadeIcone atividade, int tamanho)
    {
        if (!TemSelo(atividade)) return null;
        lock (_cache)
        {
            if (_cache.TryGetValue((atividade, tamanho), out var pronto)) return pronto;
            using var bmp = Desenhar(atividade, tamanho);
            using var png = new MemoryStream();
            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            // um .ico de uma imagem só (PNG embutido) dá um Icon exatamente do tamanho pedido, sem GetHicon
            using var ico = new MemoryStream();
            using (var w = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)1);
                w.Write((byte)tamanho); w.Write((byte)tamanho); w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32); w.Write((int)png.Length); w.Write(22);
                w.Write(png.ToArray());
            }
            ico.Position = 0;
            return _cache[(atividade, tamanho)] = new Icon(ico);
        }
    }

    /// <summary>
    /// Forma simples e grossa (a 16 px detalhe vira ruído), com halo escuro e contorno branco para aparecer em barra clara e escura:
    /// ponto vermelho (live), triângulo de play (assistindo) e claquete (Cinema), nas cores do tema.
    /// </summary>
    public static Bitmap Desenhar(AtividadeIcone atividade, int tamanho)
    {
        var bmp = new Bitmap(tamanho, tamanho, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float s = tamanho / 16f;
        using var forma = new GraphicsPath();
        Color cor;
        switch (atividade)
        {
            case AtividadeIcone.Transmitindo:
                forma.AddEllipse(3.2f * s, 3.2f * s, 9.6f * s, 9.6f * s);
                cor = Theme.Error;
                break;
            case AtividadeIcone.Assistindo:
                forma.AddPolygon(new[] { new PointF(4.2f * s, 2.6f * s), new PointF(13.2f * s, 8f * s), new PointF(4.2f * s, 13.4f * s) });
                cor = Theme.Accent;
                break;
            default: // Cinema: claquete = corpo e tampa levemente inclinada
                forma.AddRectangle(new RectangleF(2.6f * s, 7f * s, 10.8f * s, 6.6f * s));
                forma.AddPolygon(new[] { new PointF(2.6f * s, 5.4f * s), new PointF(3.4f * s, 2.4f * s), new PointF(13.4f * s, 4.2f * s), new PointF(13.4f * s, 6.2f * s) });
                cor = Theme.Accent;
                break;
        }
        int halo = 150;
        using (var p = new Pen(Color.FromArgb(halo, Color.Black), 3.6f * s) { LineJoin = LineJoin.Round }) g.DrawPath(p, forma);
        using (var p = new Pen(Color.White, 2.2f * s) { LineJoin = LineJoin.Round }) g.DrawPath(p, forma);
        using (var b = new SolidBrush(cor)) g.FillPath(b, forma);
        if (atividade == AtividadeIcone.Cinema)
        {
            // listras da tampa: sem elas vira um retângulo qualquer
            using var p = new Pen(Color.White, 1.3f * s);
            g.DrawLine(p, 6f * s, 2.9f * s, 5.2f * s, 5.9f * s);
            g.DrawLine(p, 9.8f * s, 3.6f * s, 9f * s, 6.4f * s);
        }
        return bmp;
    }

    // ---- COM (só aqui; falha vira Log.Info e nunca janela) ----

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarList3
    {
        // ITaskbarList
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr hwnd);
        [PreserveSig] int DeleteTab(IntPtr hwnd);
        [PreserveSig] int ActivateTab(IntPtr hwnd);
        [PreserveSig] int SetActiveAlt(IntPtr hwnd);
        // ITaskbarList2
        [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        // ITaskbarList3: a ordem da vtable precisa ser exata, por isso os métodos sem uso também estão declarados
        [PreserveSig] int SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        [PreserveSig] int SetProgressState(IntPtr hwnd, int flags);
        [PreserveSig] int RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
        [PreserveSig] int UnregisterTab(IntPtr hwndTab);
        [PreserveSig] int SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        [PreserveSig] int SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint reserved);
        [PreserveSig] int ThumbBarAddButtons(IntPtr hwnd, uint buttons, IntPtr pButtons);
        [PreserveSig] int ThumbBarUpdateButtons(IntPtr hwnd, uint buttons, IntPtr pButtons);
        [PreserveSig] int ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
        [PreserveSig] int SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string? description);
    }

    static ITaskbarList3? _lista;
    static bool _semBarra;

    /// <summary>
    /// Põe (ou tira, com null) o selo no botão da janela. Só funciona depois do Explorer criar o botão (mensagem TaskbarButtonCreated);
    /// quem chama reaplica nessa hora. Devolve o HRESULT (0 = S_OK), ou -1 se o COM não existe (Windows sem barra, servidor sem Explorer).
    /// </summary>
    public static int Aplicar(IntPtr hwnd, AtividadeIcone atividade, int tamanho)
    {
        if (_semBarra || hwnd == IntPtr.Zero) return -1;
        try
        {
            if (_lista == null)
            {
                var tipo = Type.GetTypeFromCLSID(new Guid("56FDF344-FD6D-11d0-958A-006097C9A090"));
                var lista = (ITaskbarList3)Activator.CreateInstance(tipo!)!;
                int init = lista.HrInit();
                if (init < 0) { Log.Info($"Selo da barra de tarefas: HrInit falhou (0x{init:X8})."); _semBarra = true; return init; }
                _lista = lista;
            }
            var icone = Criar(atividade, tamanho);
            int hr = _lista.SetOverlayIcon(hwnd, icone?.Handle ?? IntPtr.Zero, icone == null ? null : Descricao(atividade));
            Log.Info($"Selo da barra de tarefas: {atividade} → 0x{hr:X8}.");
            return hr;
        }
        catch (Exception ex)
        {
            Log.Info($"Selo da barra de tarefas indisponível: {ex.Message}");
            _lista = null; _semBarra = true;
            return -1;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr change);

    /// <summary>Número da mensagem que o Explorer manda quando cria (ou recria, ao reiniciar) o botão da janela.</summary>
    public static readonly uint MensagemBotaoCriado = RegisterWindowMessage("TaskbarButtonCreated");

    /// <summary>Se o app roda como administrador, o Explorer (sem elevação) teria a mensagem barrada; MSGFLT_ALLOW = 1.</summary>
    public static void PermitirMensagem(IntPtr hwnd)
    {
        try { if (MensagemBotaoCriado != 0) ChangeWindowMessageFilterEx(hwnd, MensagemBotaoCriado, 1, IntPtr.Zero); } catch { }
    }

    [DllImport("shell32.dll")] static extern void SHChangeNotify(int evento, uint flags, IntPtr item1, IntPtr item2);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern void SHChangeNotify(int evento, uint flags, string item1, IntPtr item2);

    /// <summary>
    /// Pede ao Explorer que releia os ícones. A atualização automática troca o Streemu.exe no mesmo caminho, e o Explorer segue desenhando o
    /// atalho e o botão com o ícone antigo guardado no cache (visto em 2026-11-19). Chamado uma vez depois de cada atualização, fora da thread da tela
    /// (a notificação é enviada a todas as janelas do sistema e pode demorar).
    /// </summary>
    public static void RenovarCacheDeIcones(string exe)
    {
        try
        {
            SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0 /* SHCNF_IDLIST */, IntPtr.Zero, IntPtr.Zero);
            SHChangeNotify(0x00002000 /* SHCNE_UPDATEITEM */, 0x0005 /* SHCNF_PATHW */, exe, IntPtr.Zero);
            Log.Info("Cache de ícones do Explorer renovado depois da atualização.");
        }
        catch (Exception ex) { Log.Info($"Cache de ícones não renovou: {ex.Message}"); }
    }
}
