using System.Management;

namespace Streemu.Util;

public static class SystemInfo
{
    static string[]? _gpus;

    public static string[] Gpus()
    {
        if (_gpus != null) return _gpus;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController");
            _gpus = searcher.Get().Cast<ManagementObject>()
                .Select(o => $"{o["Name"]} (driver {o["DriverVersion"]})").ToArray();
        }
        catch (Exception e) { _gpus = new[] { "desconhecida: " + e.Message }; }
        return _gpus;
    }

    /// <summary>
    /// Placa RTX série 50: o decodificador Vulkan dela deixa a imagem borrada/em blocos (dois PCs do grupo, RTX 5070, 2026-11-09; o teste Vulkan de um deles
    /// mostra só a 5070 como dispositivo, então não é o Parsec). Com "Decodificar" desmarcado fica nítido.
    /// </summary>
    public static bool HasRtx50 => Gpus().Any(g => System.Text.RegularExpressions.Regex.IsMatch(g, @"RTX\s*50\d\d"));

    public static string Windows => $"{Environment.OSVersion.VersionString} (build {Environment.OSVersion.Version.Build})";
    public static bool ProcessAudioSupported => Environment.OSVersion.Version.Build >= 19041;
}
