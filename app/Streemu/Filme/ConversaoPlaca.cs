using Streemu.Util;

namespace Streemu.Filme;

/// <summary>
/// Recompressão do filme pela transmissão inteira na placa (AMD): decodifica pela Vulkan, reduz e converte HDR→SDR no libplacebo e comprime no h264_amf.
/// Por quê (docs/DESCOBERTAS.md, 2026-11-20): num Ryzen 5 3600 + RX 6750 XT o caminho na CPU gastava 2,6-4 núcleos e o decodificador da placa
/// ficava em 73-100% junto com o mpv 4K do anfitrião; em campo o envio caiu a 0,85x e o mpv do próprio anfitrião perdia quadros. Na placa: 0,1-0,2 núcleo e
/// o decodificador em 35-52%. Só AMD por enquanto: é o único caso medido (NVIDIA RTX 50 já teve problema com Vulkan na reprodução).
/// </summary>
public static class ConversaoPlaca
{
    /// <summary>Desliga sozinho se o ffmpeg não abrir por este caminho (<see cref="Recuar"/>). <c>STREEMU_FILME_CONVERSAO_PLACA=0</c> desliga, <c>=1</c> força.</summary>
    public static volatile bool Ligada = Inicial(Environment.GetEnvironmentVariable("STREEMU_FILME_CONVERSAO_PLACA"), SystemInfo.Gpus());

    /// <summary>Ligada por padrão só com placa AMD conhecida (lista vazia não conta como AMD, ao contrário do StreamSender.HasAmdGpu).</summary>
    /// <example><code>bool ligar = ConversaoPlaca.Inicial(null, new[] { "AMD Radeon RX 6750 XT" }); // true</code></example>
    internal static bool Inicial(string? variavel, IReadOnlyCollection<string> gpus)
    {
        if (variavel == "0") return false;
        if (variavel == "1") return true;
        return gpus.Any(g => g.Contains("AMD", StringComparison.OrdinalIgnoreCase) || g.Contains("Radeon", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Antes do <c>-i</c>: os quadros decodificados ficam na memória da placa, prontos para o libplacebo.</summary>
    public static string[] Entrada() => new[] { "-init_hw_device", "vulkan", "-hwaccel", "vulkan", "-hwaccel_output_format", "vulkan" };

    /// <summary>Filtro e encoder do vídeo. O libplacebo também aplica o Dolby Vision (RPU) que o zscale ignorava.</summary>
    /// <example><code>a.AddRange(ConversaoPlaca.Video(720, "3000k"));</code></example>
    public static string[] Video(int alto, string kbps) => new[]
    {
        "-vf", $"libplacebo=w=-2:h='min({alto},ih)':colorspace=bt709:color_primaries=bt709:color_trc=bt709:range=tv:tonemapping=hable:format=yuv420p,hwdownload,format=yuv420p",
        "-c:v", "h264_amf", "-usage", "transcoding", "-quality", "balanced", "-rc", "cbr", "-vbaq", "true",
        "-b:v", kbps, "-maxrate", kbps, "-bufsize", "2M", "-g", "48", "-bf", "0",
        // sem os dois o h264_amf gera um único IDR e quem entra depois do início não decodifica (mesmo defeito medido no StreamSender, 2026-11-09)
        "-force_key_frames", "expr:gte(t,n_forced*2)", "-forced_idr", "1",
    };
}
