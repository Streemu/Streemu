using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Streemu.Util;

namespace Streemu.Telemetria;

/// <summary>
/// Uso da placa de vídeo por segundo durante a live, pelos contadores de desempenho do Windows ("GPU Engine", os mesmos do Gerenciador de Tarefas):
/// 3D da placa inteira, 3D só do jogo e o codificador de vídeo (NVENC/AMF/QSV). Vai para o quadros.csv.
/// Motivo (telemetria de 2026-11-12, Risk of Rain 2 em 4K60 pela TV num PC do grupo): a live alternava ~6 s a 60 quadros novos/s com ~6 s a 0–8/s, e até uma
/// captura de teste nova recebia ~2/s. Sem o uso da placa não dava para separar "o jogo parou de mandar quadros" de "a placa está a 100% e a captura
/// e o encoder ficam na fila". PDH, não WMI (regra 9 do AGENTS.md); lido numa thread própria, com prioridade baixa, só enquanto há live.
/// Funciona em qualquer placa (NVIDIA, AMD, Intel); sem os contadores (Windows antigo, contador corrompido) as colunas ficam vazias.
/// </summary>
public sealed partial class UsoGpu : IDisposable
{
    /// <param name="Decoder">O decodificador de vídeo da placa: a prévia da própria live (ffplay) decodifica a stream 4K na MESMA placa.</param>
    /// <param name="App3d">3D dos processos do Streemu (o app, os ffmpeg da captura/sensor/envio e o ffplay da prévia).</param>
    /// <param name="Dwm3d">3D do compositor do Windows (dwm). Motivo (PC do grupo, jogo em 3440x1440, 12/11): 3D da placa a 98% com o jogo usando 76%; o jogo
    /// caiu de 130 para 84 fps e não dava para dizer se os 22% eram dos nossos ffmpeg, do DWM (a captura do monitor pode obrigá-lo a compor mais) ou de overlays.</param>
    /// <param name="AppCpu">CPU dos processos do Streemu, em % de UM núcleo (100 = um núcleo inteiro). O caminho que copia os quadros para a CPU e reduz
    /// lá gasta ~70–80% de um núcleo a 1080p e mais a 3440x1440: num jogo que depende da CPU, é isso que pesa, não a placa.</param>
    /// <param name="JogoCpu">CPU do jogo, em % de um núcleo (o jogo sozinho; serve para comparar com o Streemu).</param>
    public readonly record struct Amostra(int Total3d, int Jogo3d, int Encoder, int Decoder, int App3d, int Dwm3d, int AppCpu, int JogoCpu);

    // CPU acumulada de cada processo na leitura anterior (pid → tempo de CPU), para tirar a diferença de 1 s
    Dictionary<int, TimeSpan> _cpuAntes = new();
    DateTime _cpuEm = DateTime.MinValue;

    (int App, int Jogo) Cpu(int? pidJogo)
    {
        var agora = new Dictionary<int, TimeSpan>();
        foreach (int pid in _pidsApp.Append(pidJogo ?? -1))
        {
            if (pid < 0) continue;
            try { using var p = System.Diagnostics.Process.GetProcessById(pid); agora[pid] = p.TotalProcessorTime; } catch { }
        }
        var em = DateTime.Now;
        double seg = (em - _cpuEm).TotalSeconds;
        double app = 0, jogo = 0;
        if (seg > 0.2 && seg < 5)
            foreach (var (pid, t) in agora)
            {
                if (!_cpuAntes.TryGetValue(pid, out var antes) || t < antes) continue;
                double pct = (t - antes).TotalSeconds / seg * 100;
                if (pid == pidJogo) jogo += pct; else app += pct;
            }
        _cpuAntes = agora; _cpuEm = em;
        return ((int)Math.Round(app), (int)Math.Round(jogo));
    }

    // pid → de quem é (app/dwm), atualizado a cada 5 s: GetProcessesByName a cada segundo custaria mais que a leitura toda
    HashSet<int> _pidsApp = new(), _pidsDwm = new();
    DateTime _pidsEm = DateTime.MinValue;

    void AtualizarPids()
    {
        if (DateTime.Now - _pidsEm < TimeSpan.FromSeconds(5)) return;
        _pidsEm = DateTime.Now;
        static HashSet<int> De(params string[] nomes)
        {
            var s = new HashSet<int>();
            foreach (var n in nomes)
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(n)) { s.Add(p.Id); p.Dispose(); }
            return s;
        }
        var app = De("ffmpeg", "ffplay");
        app.Add(Environment.ProcessId);
        _pidsApp = app;
        _pidsDwm = De("dwm");
    }

    readonly Func<int?> _pidDoJogo;
    volatile bool _parar;
    Amostra? _ultima;
    DateTime _ultimaEm;

    public UsoGpu(Func<int?> pidDoJogo)
    {
        _pidDoJogo = pidDoJogo;
        new Thread(Laco) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "telemetria-gpu" }.Start();
    }

    /// <summary>A leitura mais recente, se tiver menos de 3 s (nulo = sem leitura confiável agora).</summary>
    public Amostra? Agora => _ultima is { } a && DateTime.Now - _ultimaEm < TimeSpan.FromSeconds(3) ? a : null;

    public void Dispose() => _parar = true;

    // engtype_(.+): na AMD o motor de vídeo é "Video Codec 0" (com espaço e número); o (\w+) antigo parava em "Video" e as colunas de decodificador/encoder
    // ficavam vazias em toda placa AMD (achado na RX 6750 XT, docs/DESCOBERTAS.md 2026-11-20)
    [GeneratedRegex(@"pid_(\d+)_luid_(\w+)_(\w+)_phys_(\d+)_eng_(\d+)_engtype_(.+)$", RegexOptions.IgnoreCase)] private static partial Regex InstanciaRx();

    /// <summary>Tipo do motor sem espaços nem o número do fim: "Video Codec 0" → "VideoCodec", "3D" → "3D".</summary>
    /// <example><code>UsoGpu.TipoDoMotor("Video Codec 0") == "VideoCodec"</code></example>
    internal static string TipoDoMotor(string bruto)
    {
        string t = bruto.Trim();
        int fim = t.Length;
        while (fim > 0 && (char.IsDigit(t[fim - 1]) || t[fim - 1] == ' ')) fim--;
        string sem = t[..fim].Replace(" ", "");
        return sem.Length == 0 ? t : sem; // "3D" termina em letra; um tipo só de dígitos fica como veio
    }

    void Laco()
    {
        IntPtr query = IntPtr.Zero;
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) { Log.Info("[telemetria] uso da placa: contadores do Windows indisponíveis; as colunas da GPU ficam vazias."); return; }
            if (PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out var counter) != 0)
            { Log.Info("[telemetria] uso da placa: este Windows não tem o contador \"GPU Engine\"; as colunas da GPU ficam vazias."); return; }
            PdhCollectQueryData(query); // a porcentagem precisa de duas leituras
            while (!_parar)
            {
                Thread.Sleep(1000);
                if (_parar) break;
                if (PdhCollectQueryData(query) != 0) continue;
                var a = Ler(counter, _pidDoJogo());
                if (a != null) { _ultima = a; _ultimaEm = DateTime.Now; }
            }
        }
        catch (Exception e) { Log.Info("[telemetria] uso da placa: parei de ler (" + e.Message + ")."); }
        finally { if (query != IntPtr.Zero) PdhCloseQuery(query); }
    }

    Amostra? Ler(IntPtr counter, int? pidJogo)
    {
        uint bytes = 0, itens = 0;
        int st = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref bytes, ref itens, IntPtr.Zero);
        if (st != PDH_MORE_DATA || bytes == 0) return null;
        IntPtr buf = Marshal.AllocHGlobal((int)bytes);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref bytes, ref itens, buf) != 0) return null;
            // cada motor da placa (luid + phys + eng) soma o uso de todos os processos nele; o "3D" do Gerenciador de Tarefas é o motor 3D mais ocupado
            var porMotor = new Dictionary<string, double>();
            var tipoDoMotor = new Dictionary<string, string>();
            double jogo = 0, app = 0, dwm = 0;
            AtualizarPids();
            int tam = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>();
            for (int i = 0; i < itens; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>(buf + i * tam);
                if (item.CStatus != 0 || item.szName == IntPtr.Zero) continue;
                var m = InstanciaRx().Match(Marshal.PtrToStringUni(item.szName) ?? "");
                if (!m.Success) continue;
                string tipo = TipoDoMotor(m.Groups[6].Value);
                string motor = $"{m.Groups[2].Value}_{m.Groups[3].Value}_{m.Groups[4].Value}_{m.Groups[5].Value}";
                porMotor[motor] = porMotor.GetValueOrDefault(motor) + item.Value;
                tipoDoMotor[motor] = tipo;
                if (tipo.Equals("3D", StringComparison.OrdinalIgnoreCase))
                {
                    int pid = int.Parse(m.Groups[1].Value);
                    if (pidJogo == pid) jogo += item.Value;
                    else if (_pidsApp.Contains(pid)) app += item.Value;
                    else if (_pidsDwm.Contains(pid)) dwm += item.Value;
                }
            }
            int Max(params string[] tipos) => (int)Math.Round(Math.Min(100, porMotor.Where(x => tipos.Contains(tipoDoMotor[x.Key], StringComparer.OrdinalIgnoreCase)).Select(x => x.Value).DefaultIfEmpty(0).Max()));
            static int Pct(double v) => (int)Math.Round(Math.Min(100, v));
            var (appCpu, jogoCpu) = Cpu(pidJogo);
            // na AMD codificar e decodificar são o mesmo motor ("VideoCodec"): as duas colunas mostram o mesmo uso
            return new Amostra(Max("3D"), Pct(jogo), Max("VideoEncode", "VideoCodec"), Max("VideoDecode", "VideoCodec"), Pct(app), Pct(dwm), appCpu, jogoCpu);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    const uint PDH_FMT_DOUBLE = 0x00000200;
    const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    [StructLayout(LayoutKind.Sequential)]
    struct PDH_FMT_COUNTERVALUE_ITEM_DOUBLE
    {
        public IntPtr szName;
        public uint CStatus;
        public double Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr items);
    [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr query);
}
