using System.Runtime.InteropServices;

namespace Streemu.Util;

/// <summary>
/// Pergunta ao Windows se um monitor está com a "cor avançada" ligada (o gerenciamento automático de cor do Windows 11 ou o HDR).
/// Motivo (2026-11-08, um PC com AMD): com a cor avançada ligada o Desktop Duplication entrega a imagem em luz LINEAR guardada em 8 bits,
/// e a stream sai bem mais escura que a tela (luma 13,6 contra 58,9), mesmo com o DXGI dizendo "SDR". Ver docs/DESCOBERTAS.md.
/// Uso: <c>bool linear = AdvancedColor.IsEnabled(@"\\.\DISPLAY1");</c>
/// </summary>
public static class AdvancedColor
{
    const uint OnlyActivePaths = 2;
    const int GetSourceName = 1, GetAdvancedColorInfo = 9;

    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct Header { public int Type; public int Size; public Luid Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential)] struct PathSource { public Luid Adapter; public uint Id; public uint ModeIndex; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] struct Rational { public uint Numerator, Denominator; }
    [StructLayout(LayoutKind.Sequential)]
    struct PathTarget
    {
        public Luid Adapter; public uint Id; public uint ModeIndex; public int Technology, Rotation, Scaling;
        public Rational Refresh; public int ScanLineOrdering, Available; public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)] struct DisplayPath { public PathSource Source; public PathTarget Target; public uint Flags; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SourceName { public Header Header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string GdiName; }
    [StructLayout(LayoutKind.Sequential)] struct ColorInfo { public Header Header; public uint Value; public int Encoding; public uint BitsPerColor; }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] DisplayPath[] pathArray, ref uint modes, IntPtr modeArray, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SourceName info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref ColorInfo info);

    /// <summary>Verdadeiro se o monitor com esse nome do Windows (ex.: <c>\\.\DISPLAY1</c>) está com a cor avançada ligada. Falha em silêncio (falso).</summary>
    public static bool IsEnabled(string gdiDeviceName)
    {
        try { return Query(gdiDeviceName); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException) { return false; }
    }

    static bool Query(string gdiDeviceName)
    {
        if (GetDisplayConfigBufferSizes(OnlyActivePaths, out uint pathCount, out uint modeCount) != 0) return false;
        var paths = new DisplayPath[pathCount];
        var modes = Marshal.AllocHGlobal((int)modeCount * 64); // não lemos os modos, mas a função exige o buffer
        try
        {
            if (QueryDisplayConfig(OnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return false;
            for (int i = 0; i < pathCount; i++)
                if (NameOf(paths[i]) == gdiDeviceName) return ColorEnabled(paths[i]);
            return false;
        }
        finally { Marshal.FreeHGlobal(modes); }
    }

    static string NameOf(DisplayPath path)
    {
        var info = new SourceName { Header = new Header { Type = GetSourceName, Size = Marshal.SizeOf<SourceName>(), Adapter = path.Source.Adapter, Id = path.Source.Id } };
        return DisplayConfigGetDeviceInfo(ref info) == 0 ? info.GdiName : "";
    }

    static bool ColorEnabled(DisplayPath path)
    {
        var info = new ColorInfo { Header = new Header { Type = GetAdvancedColorInfo, Size = Marshal.SizeOf<ColorInfo>(), Adapter = path.Target.Adapter, Id = path.Target.Id } };
        return DisplayConfigGetDeviceInfo(ref info) == 0 && (info.Value & 2) != 0; // bit 1 = advancedColorEnabled
    }
}
