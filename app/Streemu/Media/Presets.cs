namespace Streemu.Media;

public sealed record Preset(string Name, int Height, int Fps, int Kbps)
{
    public override string ToString() => $"{Name}  ({Kbps / 1000.0:0.#} Mbps)";

    public static readonly Preset[] All =
    {
        new("720p30", 720, 30, 3000),
        new("720p60", 720, 60, 4500),
        new("1080p30", 1080, 30, 5000),
        new("1080p60", 1080, 60, 8000),
        new("1440p30", 1440, 30, 9000),
        new("1440p60", 1440, 60, 14000),
        new("1440p30 ultrawide", 1440, 30, 12000),
        new("1440p60 ultrawide", 1440, 60, 19000),
        new("2160p30", 2160, 30, 18000),
        new("2160p60", 2160, 60, 30000),
        // "Fonte" da janela Transmitir: tamanho da tela/janela, sem reduzir (até 4K). O bitrate é cortado para o tamanho
        // real por ForOutput; fica no fim da lista para não mudar os índices do STREEMU_TEST_PRESET.
        new("Fonte30", 2160, 30, 18000),
        new("Fonte60", 2160, 60, 30000),
    };

    /// <summary>Alturas da janela Transmitir (0 = Fonte), no estilo do Go Live do Discord.</summary>
    public static readonly int[] PickerHeights = { 720, 1080, 1440, 0 };

    public bool IsSource => Name.StartsWith("Fonte");

    /// <summary>Preset da janela Transmitir: altura (720/1080/1440, 0 = Fonte) e FPS (30/60).</summary>
    public static Preset Pick(int height, int fps)
    {
        fps = fps >= 60 ? 60 : 30;
        string name = height <= 0 ? $"Fonte{fps}" : $"{height}p{fps}";
        return All.First(p => p.Name == name);
    }

    /// <summary>"1080p 60 FPS", "Fonte 60 FPS", como no Discord.</summary>
    public string Short => (IsSource ? "Fonte" : $"{Height}p") + $" {Fps} FPS";

    /// <summary>
    /// Bitrate que cabe na imagem que realmente sai. O preset não aumenta a fonte (2160p60 num monitor 1080p sai em 1080p),
    /// então pedir os 30 Mbps dele só gasta banda e esforço do encoder (campo 2026-11-06, AMD: 44 quadros/s a 30 Mbps
    /// contra 58 a 8 Mbps na mesma fonte). Só diminui; se a saída tem o tamanho do preset ou mais, devolve o próprio preset.
    /// Uso: <c>preset.ForOutput(1920, 1080)</c> no 2160p60 devolve um preset de 7500 kbps.
    /// </summary>
    public Preset ForOutput(int outW, int outH)
    {
        int presetW = Name.Contains("ultrawide") ? 3440 : (int)Math.Round(Height * 16 / 9.0);
        double ratio = (double)outW * outH / ((double)presetW * Height);
        if (ratio >= 1) return this;
        int kbps = Math.Max(1500, (int)Math.Round(Kbps * ratio / 500.0) * 500);
        return kbps >= Kbps ? this : this with { Kbps = kbps };
    }

    /// <summary>Tamanho de saída: altura do preset (sem aumentar a fonte), largura pela proporção da fonte, ambos pares.</summary>
    public (int W, int H) OutputSize(int srcW, int srcH)
    {
        if (srcW <= 0 || srcH <= 0) return (1280, 720);
        int h = Math.Min(Height, srcH);
        int w = (int)Math.Round(srcW * (double)h / srcH);
        return (w & ~1, h & ~1);
    }
}

public enum AudioMode { OnlyApp, AllExceptDiscord, None }

public enum EncoderChoice { Auto, Nvenc, Amf, Qsv, X264, Mf }
