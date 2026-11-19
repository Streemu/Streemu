using Streemu.Util;

namespace Streemu.Media;

/// <summary>
/// Como o ffmpeg pega a imagem. Dois caminhos:
///   - gfxcapture (Windows Graphics Capture): janela ou monitor; serve para jogo em janela e para a área de trabalho;
///   - ddagrab (Desktop Duplication): o monitor inteiro, direto da saída da placa; é o que o OBS usa em "captura de tela".
/// Os dois entregam texturas D3D11 que vão direto para o encoder (NVENC, AMF mapeado, Media Foundation).
/// </summary>
public sealed record CaptureSpec(string Name, string Description, bool Dda, string Graph, int Adapter, int Width, int Height, string? CountGraph = null)
{
    public override string ToString() => $"{Name} ({Description})";

    /// <summary>
    /// A mesma captura, mas entregando SÓ quadros novos (sem o ddagrab repetir): é o que a sonda e o sensor usam para contar quantos quadros a tela
    /// realmente produz. A captura que vai para o encoder repete quadros (ver <see cref="Capture.Dda"/>), então contar nela não diz nada.
    /// </summary>
    public CaptureSpec ForCounting() => CountGraph == null ? this : this with { Graph = CountGraph };

    /// <summary>Captura o monitor inteiro (ddagrab ou gfxcapture do monitor): quem usa precisa esconder o Alt+Tab de quem assiste (trava de foco).</summary>
    public bool CapturesMonitor => Dda || Name == "gfxcapture-monitor";

    /// <summary>
    /// Argumentos do vídeo. <paramref name="inputs"/> vai antes das outras entradas; <paramref name="outputs"/>
    /// vai depois de todas as entradas. <paramref name="vf"/> é o filtro de vídeo do encoder (pode ser nulo).
    /// Devolve quantas entradas de arquivo o vídeo ocupou (0 no ddagrab, que roda no filter_complex).
    /// </summary>
    public int Args(string? vf, List<string> inputs, List<string> outputs)
    {
        if (!Dda)
        {
            inputs.AddRange(Tools.InputQueueArgs);
            inputs.AddRange(new[] { "-probesize", "32", "-analyzeduration", "0", "-f", "lavfi", "-i", Graph });
            outputs.AddRange(new[] { "-map", "0:v:0" });
            if (vf != null) outputs.AddRange(new[] { "-vf", vf });
            return 1;
        }
        // ddagrab no filter_complex: assim ele recebe o dispositivo D3D11 do adaptador dono do monitor
        // (com "-f lavfi -i" ele sempre abre o adaptador 0, que pode ser o display virtual do Parsec ou a GPU integrada).
        inputs.AddRange(new[] { "-init_hw_device", $"d3d11va=dda:{Adapter}", "-filter_hw_device", "dda" });
        outputs.AddRange(new[] { "-filter_complex", Graph + (vf != null ? "," + vf : "") + "[vcap]", "-map", "[vcap]" });
        return 0;
    }
}

public static class Capture
{
    /// <summary>
    /// O padrão do gfxcapture é recortar: se a janela do jogo muda de tamanho DEPOIS de a captura abrir (Alt+Tab troca 720p por 1080p), o quadro fica no
    /// tamanho inicial e mostra só um pedaço da imagem, como um zoom (relato de campo no Dragon's Dogma 2, 2026-11-08; medido: diferença 27,9 com crop, 3,0
    /// com scale). scale_aspect escala para o tamanho inicial sem deformar (com a mesma proporção fica igual ao scale).
    /// </summary>
    const string ResizeMode = "resize_mode=scale_aspect";

    /// <summary>gfxcapture de uma janela (sem width/height: na captura do Windows eles CORTAM a imagem em vez de reduzir).</summary>
    public static CaptureSpec Window(CaptureSource src, int fps)
    {
        var (w, h) = src.CurrentSize();
        string graph = $"gfxcapture=hwnd={src.Handle.ToInt64()}:max_framerate={fps}:capture_cursor=1:{ResizeMode}";
        return new("gfxcapture-janela", src.Label, false, graph, 0, w & ~1, h & ~1, CountGraphFor(graph));
    }

    /// <summary>
    /// No ffmpeg com dup_frames no gfxcapture (ver <see cref="Tools.GfxCaptureRepeats"/>) a captura principal repete o quadro e o vigia ficaria cego: a
    /// janela travada pareceria a 60/s, e a sonda da janela daria ~60 numa tela de load, trocando o ddagrab pela janela à toa. Como no ddagrab, quem conta
    /// usa dup_frames=0. No ffmpeg antigo a opção não existe (e o ffmpeg principal já conta os repetidos): sem CountGraph, sem sensor.
    /// </summary>
    static string? CountGraphFor(string graph) => Tools.GfxCaptureRepeats ? graph + ":dup_frames=0" : null;

    public static CaptureSpec Monitor(IntPtr hmonitor, int fps, string label)
    {
        var info = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
        Native.GetMonitorInfo(hmonitor, ref info);
        string graph = $"gfxcapture=hmonitor={hmonitor.ToInt64()}:max_framerate={fps}:capture_cursor=1:{ResizeMode}";
        if (Display.RefreshHz(info.szDevice) is int hz) label += $"; {hz} Hz";
        return new("gfxcapture-monitor", label, false, graph, 0, info.rcMonitor.Width & ~1, info.rcMonitor.Height & ~1, CountGraphFor(graph));
    }

    /// <summary>
    /// ddagrab do monitor. dup_frames=0: só entrega quadro novo (o "-fps_mode cfr" do encoder completa os 60),
    /// assim o "N quadros novos/s" continua honesto. Nulo se o DXGI não achar a saída desse monitor.
    /// </summary>
    public static CaptureSpec? Dda(IntPtr hmonitor, int fps, string label)
    {
        var o = Display.OutputFor(hmonitor);
        if (o == null) return null;
        // Cor avançada do Windows (docs/DESCOBERTAS.md, 2026-11-08): o ddagrab entrega luz linear em 8 bits e a stream sai escura. O gfxcapture do
        // monitor já entrega sRGB certo e custa o mesmo (55% contra 47% de um núcleo a 1080p60); converter os 16 bits do ddagrab custava 1,5 a 3,5 núcleos.
        if (o.LinearCapture)
            return Monitor(hmonitor, fps, label + "; cor avançada do Windows: captura do monitor pelo Windows (gfxcapture) para a cor sair certa");
        // dup_frames=1: com a tela PARADA (vídeo pausado no mpv/Jellyfin, menu) o ddagrab não entrega nada; com dup_frames=0 o ffmpeg (-fps_mode cfr) ficava
        // sem soltar quadro nenhum (e sem áudio, que espera o vídeo na mistura) e, ao a tela mexer de novo, despejava TODOS os quadros repetidos de uma vez:
        // 770 quadros a ~40 Mbps em 3 s, o "gargalar e pular frame" depois de pausar (medido em 2026-11-08). Repetindo no ddagrab a saída é contínua.
        // Em troca o ffmpeg não sabe mais quantos quadros são novos: quem conta é o FrameSensor, com o CountGraph (dup_frames=0).
        string graph = $"ddagrab=output_idx={o.OutputIdx}:framerate={fps}:draw_mouse=1";
        string hz = Display.RefreshHz(o.DeviceName) is int r ? $"; {o.DeviceName} {o.Rect.Width}x{o.Rect.Height} a {r} Hz" : "";
        return new("ddagrab", $"{label}; saída {o.OutputIdx} da placa {o.Adapter} \"{o.AdapterName}\"{hz}" + (o.Hdr ? "; monitor em HDR" : ""), true,
            graph + ":dup_frames=1", o.Adapter, o.Rect.Width & ~1, o.Rect.Height & ~1, graph + ":dup_frames=0");
    }

    /// <summary>
    /// Caminho padrão para cada fonte. Jogo em tela cheia/sem bordas: ddagrab do monitor onde ele está
    /// (ver docs/DESCOBERTAS.md, 2026-11-06: o gfxcapture da janela entregou 5 quadros novos/s no Cyberpunk).
    /// Monitor (aba Telas): também ddagrab. Quem escolhe o monitor quase sempre está jogando nele, e o gfxcapture do monitor com jogo em tela cheia
    /// recebe só os quadros que o Windows compõe: ~1 quadro novo/s no Risk of Rain 2 em 4K num PC do grupo (2026-11-12, mediana 1, 139 s sem nenhum),
    /// enquanto o ddagrab do mesmo jogo, minutos depois, deu ~60/s. Com a cor avançada do Windows o <see cref="Dda"/> já devolve o gfxcapture.
    /// Janela comum: gfxcapture. STREEMU_CAPTURE=wgc|dda força um dos dois.
    /// </summary>
    public static CaptureSpec For(CaptureSource src, int fps)
    {
        string? force = Environment.GetEnvironmentVariable("STREEMU_CAPTURE");
        if (WantsDda(force, src.IsWindow, PreferDda(src)))
        {
            var mon = src.IsWindow ? Display.MonitorOf(src.Handle) : src.Handle;
            var dda = Dda(mon, fps, src.Label);
            if (dda != null) return dda;
            Log.Info("ddagrab: não achei a saída de vídeo desse monitor; usando a captura do Windows (gfxcapture).");
        }
        return src.IsWindow ? Window(src, fps) : Monitor(src.Handle, fps, src.Label);
    }

    /// <summary>A regra do <see cref="For"/>: ddagrab para jogo em tela cheia e para monitor; gfxcapture para janela comum; STREEMU_CAPTURE manda em tudo.</summary>
    internal static bool WantsDda(string? force, bool isWindow, bool fullScreenNow)
    {
        force = force?.ToLowerInvariant();
        return force == "dda" || (force != "wgc" && (!isWindow || fullScreenNow));
    }

    /// <summary>Jogo em tela cheia (visto AGORA, não quando foi escolhido na lista): captura o monitor.</summary>
    internal static bool PreferDda(CaptureSource src) => src.IsWindow && src.IsFullScreenNow();

    /// <summary>
    /// Abre uma captura NOVA à parte (sem encoder, sem rede) por ~<paramref name="seconds"/> s e devolve a mediana de
    /// quadros novos por segundo. Serve para separar "a captura da transmissão travou" de "o jogo está mandando pouco".
    /// </summary>
    public static double Probe(CaptureSpec cap, double seconds)
    {
        var stats = new FrameStats(60);
        var a = new List<string> { "-hide_banner", "-loglevel", "warning", "-stats", "-stats_period", "0.5" };
        var outputs = new List<string>();
        cap.ForCounting().Args(null, a, outputs);
        a.AddRange(outputs);
        a.AddRange(new[] { "-fps_mode", "passthrough", "-c:v", "wrapped_avframe", "-f", "null", "-" });
        using var p = Tools.Start(Tools.Ffmpeg, a, "sonda", redirectStdin: false, onStderr: l => stats.Feed(l));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // conta só depois do primeiro quadro (abrir a captura leva ~0,5 s)
        while (sw.Elapsed.TotalSeconds < seconds + 4 && !p.HasExited && stats.Intervals < seconds * 2) Thread.Sleep(100);
        Tools.KillQuietly(p);
        return stats.Intervals == 0 ? 0 : stats.NewFrames().Median;
    }
}
