using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Streemu.Util;

namespace Streemu.Telemetria;

/// <summary>
/// A parte do <see cref="CaminhoMidia"/> que precisa do app: versões, placas e monitores. Tudo roda na thread da fila do CaminhoMidia, nunca na
/// da interface nem no laço da captura. Sem coleta nova: as placas vêm do DXGI (o mesmo que o ddagrab usa para escolher a placa) e do
/// <see cref="SystemInfo.GpusLidas"/>, a lista do WMI que a live já leu ao escolher o encoder. Nada de nome de janela, jogo, caminho ou IP.
/// </summary>
public static class ContextoMidia
{
    /// <summary>Liga os eventos de mídia à sessão da telemetria (MainForm). <paramref name="grava"/> nulo desliga.</summary>
    public static void Ligar(Action<string, JsonObject>? grava)
    {
        CaminhoMidia.Versoes = Versoes;
        CaminhoMidia.ProvedorDoPlayer = Player;
        CaminhoMidia.Ligar(grava);
    }

    /// <summary>Versão do app e do binário de cada lado (a do pin conferido por SHA-256; em Debug com binário de teste ela diz o pin, não o arquivo).</summary>
    static JsonObject? Versoes(string lado)
    {
        var o = new JsonObject { ["versao"] = CaminhoMidia.Curto(Program.Version, 24) };
        if (lado == CaminhoMidia.Host) o["ffmpeg"] = VersaoDo(Binario.Ffmpeg);
        else { o["ffplay"] = VersaoDo(Binario.Ffplay); o["mpv"] = VersaoDo(Binario.Mpv); }
        return o;
    }

    static string? VersaoDo(Binario b)
    {
        try { return CaminhoMidia.Curto(Binarios.Pin(b)?.Versao, 40); }
        catch { return null; }
    }

    static readonly Regex Wmi = new(@"^(?<nome>.*) \(driver (?<driver>[^)]*)\)$");

    /// <summary>"NVIDIA GeForce RTX 5070 (driver 32.0.15.6094)" → (nome, driver), como o <see cref="SystemInfo.Gpus"/> monta.</summary>
    public static (string Nome, string? Driver) LerGpu(string wmi)
    {
        var m = Wmi.Match(wmi);
        return m.Success ? (m.Groups["nome"].Value.Trim(), m.Groups["driver"].Value.Trim()) : (wmi.Trim(), null);
    }

    /// <summary>Fabricante pelo id PCI do DXGI; sem ele, pelo nome. Lista fechada: nvidia, amd, intel, microsoft, outro.</summary>
    public static string Fabricante(uint vendorId, string? nome)
    {
        switch (vendorId)
        {
            case 0x10DE: return "nvidia";
            case 0x1002: case 0x1022: return "amd";
            case 0x8086: return "intel";
            case 0x1414: return "microsoft";
        }
        string n = (nome ?? "").ToLowerInvariant();
        if (n.Contains("nvidia") || n.Contains("geforce") || n.Contains("quadro")) return "nvidia";
        if (n.Contains("amd") || n.Contains("radeon")) return "amd";
        if (n.Contains("intel")) return "intel";
        if (n.Contains("microsoft")) return "microsoft";
        return "outro";
    }

    /// <summary>A placa como o perfil a descreve (nome, fabricante, driver, integrada), com o driver do WMI quando o nome bate.</summary>
    public static JsonObject? Adaptador(string? nome, uint vendorId, IReadOnlyList<string>? gpus)
    {
        string? limpo = CaminhoMidia.Curto(nome);
        if (limpo == null) return null;
        string? driver = null;
        foreach (var g in gpus ?? Array.Empty<string>())
        {
            var (n, d) = LerGpu(g);
            if (string.Equals(CaminhoMidia.Curto(n), limpo, StringComparison.OrdinalIgnoreCase)) { driver = CaminhoMidia.Curto(d, 32); break; }
        }
        return new JsonObject
        {
            ["nome"] = limpo, ["fabricante"] = Fabricante(vendorId, limpo), ["driver"] = driver, ["integrada"] = Perfil.PareceIntegrada(limpo, null),
        };
    }

    /// <summary>
    /// A placa que codifica, pelo encoder: o NVENC só roda na NVIDIA, o AMF na AMD (a dedicada, se houver as duas) e o Quick Sync na Intel. Media Foundation
    /// codifica onde está a textura da captura e o x264 é a CPU: nesses dois, nulo (vale o <c>adaptador_captura</c>).
    /// </summary>
    public static string? PlacaDoEncoder(string encoder, IReadOnlyList<string>? gpus)
    {
        string? fab = encoder switch { "nvenc" => "nvidia", "amf" => "amd", "qsv" => "intel", _ => null };
        if (fab == null) return null;
        var nomes = (gpus ?? Array.Empty<string>()).Select(g => LerGpu(g).Nome).Where(n => Fabricante(0, n) == fab).ToList();
        return nomes.FirstOrDefault(n => !Perfil.PareceIntegrada(n, null)) ?? nomes.FirstOrDefault();
    }

    /// <summary>Código do preset (o nome dele, que é fixo no Presets.cs); outro nome vira "outro".</summary>
    public static string Preset(string? nome) =>
        nome != null && Media.Preset.All.Any(p => p.Name == nome) ? nome.Replace(' ', '_') : "outro";

    /// <summary>
    /// O caminho do envio desta tentativa. Quem chama passa só números e códigos (o laço da captura não espera nada); a placa é resolvida aqui, na fila.
    /// <paramref name="placaDda"/>: o adaptador do ddagrab (-1 no gfxcapture, que usa a placa dona do <paramref name="monitor"/>).
    /// </summary>
    public static Func<JsonObject?> Envio(string capturaNome, int placaDda, IntPtr monitor, string encoderFfmpeg, string caminho,
        (int W, int H) fonte, (int W, int H) saida, int fps, int kbps, string? preset, string escolha, bool lembrado, int fpsPedido = 0, int? monitorHz = null) => () =>
    {
        string encoder = CaminhoMidia.Encoder(encoderFfmpeg);
        var gpus = SystemInfo.GpusLidas;
        List<Display.Output> saidas;
        try { saidas = Display.Outputs(); } catch { saidas = new(); }
        var dona = placaDda >= 0 ? saidas.FirstOrDefault(o => o.Adapter == placaDda) : saidas.FirstOrDefault(o => o.Monitor == monitor);
        var adCaptura = dona != null ? Adaptador(dona.AdapterName, dona.VendorId, gpus) : null;
        var adEncoder = PlacaDoEncoder(encoder, gpus) is string pe ? Adaptador(pe, 0, gpus)
            : encoder == "mf" ? adCaptura?.DeepClone() as JsonObject : null;
        return new JsonObject
        {
            ["captura"] = CaminhoMidia.Captura(capturaNome), ["encoder"] = encoder, ["caminho"] = caminho, ["codec"] = CaminhoMidia.Codec(encoderFfmpeg),
            ["escala"] = fonte != saida, ["fonte"] = CaminhoMidia.Resolucao(fonte.W, fonte.H), ["resolucao"] = CaminhoMidia.Resolucao(saida.W, saida.H),
            ["fps"] = Math.Clamp(fps, 0, 1000), ["kbps"] = Math.Clamp(kbps, 0, 1_000_000), ["preset"] = Preset(preset), ["escolha"] = escolha, ["lembrado"] = lembrado,
            // 90/120 FPS é beta: o pedido, o que saiu e a taxa do monitor mostram se o limite pelo monitor agiu ("fps" é o efetivo)
            ["fps_pedido"] = Math.Clamp(fpsPedido > 0 ? fpsPedido : fps, 0, 1000), ["fps_efetivo"] = Math.Clamp(fps, 0, 1000),
            ["monitor_hz"] = monitorHz is { } hz ? Math.Clamp(hz, 0, 1000) : null, ["beta"] = Streemu.Media.Preset.IsBeta(fpsPedido > 0 ? fpsPedido : fps),
            ["adaptador"] = adEncoder, ["adaptador_captura"] = adCaptura,
        };
    };

    /// <summary>
    /// O caminho da reprodução. A placa é a dona do monitor onde o player abre (<paramref name="onde"/>; nulo = o principal): o ffplay e o mpv não dizem
    /// qual usam, e num notebook híbrido o Vulkan pode escolher a dedicada. É uma pista, não a prova (TELEMETRIA.md).
    /// </summary>
    public static Func<JsonObject?> Player(string decodificador, string renderizador, bool previa, Rectangle? onde) => () =>
    {
        JsonObject? ad = null;
        try
        {
            var tela = onde is { } r ? Screen.FromRectangle(r) : Screen.PrimaryScreen;
            var dona = tela == null ? null : Display.Outputs().FirstOrDefault(o => string.Equals(o.DeviceName, tela.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (dona != null) ad = Adaptador(dona.AdapterName, dona.VendorId, SystemInfo.GpusLidas);
        }
        catch { }
        return new JsonObject
        {
            ["decodificador"] = CaminhoMidia.DaLista(decodificador, CaminhoMidia.Decodificadores),
            ["renderizador"] = CaminhoMidia.DaLista(renderizador, CaminhoMidia.Renderizadores),
            ["codec"] = "h264", ["previa"] = previa, ["adaptador"] = ad,
        };
    };
}
