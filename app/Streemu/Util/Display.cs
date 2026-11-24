using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Streemu.Util;

/// <summary>
/// Placas de vídeo e saídas (monitores) vistas pelo DXGI, na MESMA ordem que o ffmpeg usa:
/// o ddagrab captura a saída <c>output_idx</c> do adaptador do dispositivo D3D11 dele
/// (adaptador 0 por padrão, ou o de <c>-init_hw_device d3d11va=nome:N</c>).
/// Motivo: um PC do grupo tem "Parsec Virtual Display Adapter" além da RTX 5070, e notebook pode ter
/// GPU integrada dona do monitor; o índice do monitor no Windows não é o índice do ddagrab.
/// </summary>
internal static class Display
{
    /// <param name="Hdr">
    /// Monitor com o HDR do Windows ligado (IDXGIOutput6::GetDesc1, espaço de cor HDR10). Motivo (teste de 2026-11-06,
    /// um PC do grupo no Bloodborne): a stream saiu bem mais escura que a tela dele; capturar tela HDR como imagem comum faz isso.
    /// </param>
    public sealed record Output(int Adapter, int OutputIdx, string AdapterName, uint VendorId, string DeviceName, IntPtr Monitor, Native.RECT Rect, bool Hdr = false, bool LinearCapture = false);

    delegate int QueryFn(IntPtr self, ref Guid iid, out IntPtr result);

    /// <summary>true se a saída está em HDR (DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020). Falha em silêncio (Windows antigo).</summary>
    static bool IsHdr(IntPtr output)
    {
        var iid6 = new Guid("068346e8-aaec-4b84-add7-137f513f77a1"); // IDXGIOutput6
        if (Slot<QueryFn>(output, 0)(output, ref iid6, out var out6) != 0 || out6 == IntPtr.Zero) return false;
        var desc = Marshal.AllocHGlobal(256);
        try
        {
            // IDXGIOutput6::GetDesc1 (posição 27 na tabela); DXGI_OUTPUT_DESC1.ColorSpace fica no byte 100
            if (Slot<DescFn>(out6, 27)(out6, desc) != 0) return false;
            return Marshal.ReadInt32(desc, 100) == 12;
        }
        catch { return false; }
        finally { Marshal.FreeHGlobal(desc); Release(out6); }
    }

    /// <summary>
    /// Cor avançada do Windows ligada num monitor que NÃO é HDR10: o Desktop Duplication entrega luz linear em 8 bits e a stream sai escura
    /// (ver <see cref="AdvancedColor"/>). Em HDR10 de verdade vale outro caminho (spec 008), então fica de fora. STREEMU_COR=antiga desliga a correção.
    /// </summary>
    static bool LinearCaptureOf(string gdiDeviceName, bool hdr10) =>
        !hdr10 && Environment.GetEnvironmentVariable("STREEMU_COR") != "antiga" && AdvancedColor.IsEnabled(gdiDeviceName);

    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("gdi32.dll")] static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priority);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    delegate int EnumFn(IntPtr self, uint index, out IntPtr result);
    delegate int DescFn(IntPtr self, IntPtr desc);
    delegate uint ReleaseFn(IntPtr self);

    static T Slot<T>(IntPtr obj, int index) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), index * IntPtr.Size));

    static void Release(IntPtr obj) { if (obj != IntPtr.Zero) Slot<ReleaseFn>(obj, 2)(obj); }

    /// <summary>
    /// Todas as saídas de todos os adaptadores. Lista vazia se o DXGI falhar. <paramref name="registrarFalha"/> falso: quem chama de 250 em 250 ms
    /// esperando o monitor voltar (reset do driver) registra a falha uma vez só, em vez de uma linha por olhada.
    /// </summary>
    public static List<Output> Outputs(bool registrarFalha = true)
    {
        var list = new List<Output>();
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        if (CreateDXGIFactory1(ref iid, out var factory) != 0) return list;
        var adapterDesc = Marshal.AllocHGlobal(512);
        var outputDesc = Marshal.AllocHGlobal(256);
        try
        {
            for (uint a = 0; Slot<EnumFn>(factory, 7)(factory, a, out var adapter) == 0; a++) // IDXGIFactory::EnumAdapters
            {
                try
                {
                    string name = "?"; uint vendor = 0;
                    if (Slot<DescFn>(adapter, 8)(adapter, adapterDesc) == 0) // IDXGIAdapter::GetDesc
                    {
                        name = Marshal.PtrToStringUni(adapterDesc, 128).TrimEnd('\0');
                        vendor = (uint)Marshal.ReadInt32(adapterDesc, 256);
                    }
                    for (uint o = 0; Slot<EnumFn>(adapter, 7)(adapter, o, out var output) == 0; o++) // IDXGIAdapter::EnumOutputs
                    {
                        try
                        {
                            if (Slot<DescFn>(output, 7)(output, outputDesc) != 0) continue; // IDXGIOutput::GetDesc
                            // DXGI_OUTPUT_DESC: WCHAR DeviceName[32]; RECT DesktopCoordinates; BOOL Attached; Rotation; HMONITOR
                            string dev = Marshal.PtrToStringUni(outputDesc, 32).TrimEnd('\0');
                            var rect = Marshal.PtrToStructure<Native.RECT>(outputDesc + 64);
                            var mon = Marshal.ReadIntPtr(outputDesc, 64 + 16 + 4 + 4);
                            bool hdr = IsHdr(output);
                            list.Add(new Output((int)a, (int)o, name, vendor, dev, mon, rect, hdr, LinearCaptureOf(dev, hdr)));
                        }
                        finally { Release(output); }
                    }
                }
                finally { Release(adapter); }
            }
        }
        catch (Exception e) { if (registrarFalha) Log.Info("DXGI: não consegui listar as saídas de vídeo: " + e.Message); }
        finally
        {
            Marshal.FreeHGlobal(adapterDesc);
            Marshal.FreeHGlobal(outputDesc);
            Release(factory);
        }
        return list;
    }

    /// <summary>Monitor onde está a janela (o mais próximo, se ela estiver entre dois).</summary>
    public static IntPtr MonitorOf(IntPtr hwnd) => MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);

    public static Output? OutputFor(IntPtr hmonitor) => Outputs().FirstOrDefault(o => o.Monitor == hmonitor);

    /// <summary>Nome GDI do monitor (<c>\\.\DISPLAY1</c>), ou nulo se o Windows não reconhece mais esse identificador.</summary>
    public static string? DeviceNameOf(IntPtr hmonitor)
    {
        var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        return Native.GetMonitorInfo(hmonitor, ref info) && !string.IsNullOrEmpty(info.szDevice) ? info.szDevice : null;
    }

    /// <summary>Como o Windows vê o jogo: tela cheia exclusiva (D3D), sem bordas, ou janela.</summary>
    public static string GameMode(IntPtr hwnd, bool coversMonitor)
    {
        string state = SHQueryUserNotificationState(out int s) == 0 ? s switch
        {
            3 => "Windows diz: app D3D em tela cheia exclusiva",
            2 => "Windows diz: app em tela cheia (ocupado)",
            4 => "Windows diz: apresentação",
            5 => "Windows diz: horário silencioso",
            _ => "Windows diz: nada em tela cheia"
        } : "estado do Windows desconhecido";
        long style = GetWindowLongPtr(hwnd, -16 /* GWL_STYLE */).ToInt64();
        bool caption = (style & 0x00C00000L) == 0x00C00000L; // WS_CAPTION
        string kind = !coversMonitor ? "janela" : caption ? "janela do tamanho do monitor (com barra)" : "tela cheia/sem bordas";
        return $"{kind}; {state}";
    }

    /// <summary>
    /// Pede ao agendador da GPU prioridade alta para o processo. Hipótese do teste da 0.1.8: com o jogo a 100% da GPU,
    /// a captura e o encoder ficam na fila atrás dele (o OBS faz o mesmo quando roda como administrador).
    /// Tenta HIGH (4), depois ABOVE_NORMAL (3); REALTIME (5) precisa de administrador.
    /// Devolve o NTSTATUS da última tentativa (0 = ok) e o nível que pegou.
    /// </summary>
    public static (int Status, string Level) RaiseGpuPriority(Process p)
    {
        int st = -1;
        foreach (var (level, name) in new[] { (4, "alta"), (3, "acima do normal") })
        {
            try { st = D3DKMTSetProcessSchedulingPriorityClass(p.Handle, level); } catch { st = -1; }
            if (st == 0) return (0, name);
        }
        return (st, "");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettingsW(string deviceName, int modeNum, ref DEVMODE mode);

    /// <summary>
    /// Taxa de atualização do monitor agora (Hz), ou nulo. Vai no rótulo da captura: uma TV por HDMI em 4K costuma ficar em 30 Hz (ou com VRR), e aí o
    /// jogo não tem como mandar 60 quadros novos (campo 2026-11-12, um PC do grupo transmitindo da TV 4K por HDMI em 4K60: não havia como saber pelo log).
    /// </summary>
    public static int? RefreshHz(string gdiDeviceName)
    {
        try
        {
            var m = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            return EnumDisplaySettingsW(gdiDeviceName, -1 /* ENUM_CURRENT_SETTINGS */, ref m) && m.dmDisplayFrequency > 1 ? m.dmDisplayFrequency : null;
        }
        catch { return null; }
    }

    /// <summary>Taxa de atualização (Hz) do monitor <paramref name="hmonitor"/>, ou nulo se não der para ler (aí ninguém limita o FPS por ela).</summary>
    public static int? RefreshHz(IntPtr hmonitor)
    {
        try
        {
            var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            return hmonitor != IntPtr.Zero && Native.GetMonitorInfo(hmonitor, ref info) ? RefreshHz(info.szDevice) : null;
        }
        catch { return null; }
    }

    /// <summary>Uso da GPU NVIDIA (%), pelo nvidia-smi. Nulo se não houver (AMD/Intel ou sem driver).</summary>
    public static int? NvidiaUtilization()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=utilization.gpu --format=csv,noheader,nounits")
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            return int.TryParse(o.Split('\n')[0].Trim(), out int v) ? v : null;
        }
        catch { return null; }
    }
}
