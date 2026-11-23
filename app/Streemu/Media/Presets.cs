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
        // BETA 90/120 FPS (acrescentados no fim para não mudar os índices do STREEMU_TEST_PRESET). Limitados a 1440p: acima disso o
        // encoder e a banda não sustentam taxa alta com segurança. "Fonte" a 90/120 também sai em 1440p no máximo (OutputSize só limita).
        new("720p90", 720, 90, 6000),
        new("720p120", 720, 120, 7000),
        new("1080p90", 1080, 90, 11000),
        new("1080p120", 1080, 120, 13000),
        new("1440p90", 1440, 90, 19000),
        new("1440p120", 1440, 120, 23000),
        new("Fonte90", 1440, 90, 19000),
        new("Fonte120", 1440, 120, 23000),
    };

    /// <summary>Alturas da janela Transmitir (0 = Fonte), no estilo do Go Live do Discord.</summary>
    public static readonly int[] PickerHeights = { 720, 1080, 1440, 0 };

    public bool IsSource => Name.StartsWith("Fonte");

    /// <summary>FPS da janela Transmitir. 90 e 120 são beta (ver <see cref="IsBeta"/>).</summary>
    public static readonly int[] PickerFps = { 30, 60, 90, 120 };

    /// <summary>Acima de 60 FPS é beta: só até 1440p e ainda em validação com usuários reais.</summary>
    public static bool IsBeta(int fps) => fps > 60;

    public bool Beta => IsBeta(Fps);

    /// <summary>Preset da janela Transmitir: altura (720/1080/1440, 0 = Fonte) e FPS (30/60/90/120; valores intermediários caem para baixo). Acima de 60 FPS a altura é limitada a 1440p.</summary>
    public static Preset Pick(int height, int fps)
    {
        fps = fps >= 120 ? 120 : fps >= 90 ? 90 : fps >= 60 ? 60 : 30;
        if (fps > 60 && height > 1440) height = 1440;
        string name = height <= 0 ? $"Fonte{fps}" : $"{height}p{fps}";
        return All.First(p => p.Name == name);
    }

    /// <summary>
    /// Mesmo preset com outro FPS (o bitrate vem da tabela). Usado para não pedir à captura mais quadros que o monitor tem.
    /// Se a combinação não existe (ex.: ultrawide a 90), cai no preset comum da mesma altura.
    /// </summary>
    public Preset WithFps(int fps)
    {
        if (fps == Fps) return this;
        string name = IsSource ? $"Fonte{fps}" : Name.Contains("ultrawide") ? $"{Height}p{fps} ultrawide" : $"{Height}p{fps}";
        return All.FirstOrDefault(p => p.Name == name) ?? Pick(IsSource ? 0 : Height, fps);
    }

    /// <summary>
    /// FPS que vale a pena pedir num monitor de <paramref name="hz"/> Hz: o maior de <see cref="PickerFps"/> que cabe, nunca menos que 30; nunca aumenta.
    /// Sem a taxa (nulo) não limita. A folga de 1 Hz existe porque 59,94 Hz aparece como 59 (e 119,88 como 119): sem ela um monitor "60" cairia para 30.
    /// </summary>
    public static int FpsForRefresh(int requested, int? hz)
    {
        if (hz is not { } h || requested <= h + 1) return requested;
        return PickerFps.Where(f => f <= h + 1).DefaultIfEmpty(30).Max();
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
