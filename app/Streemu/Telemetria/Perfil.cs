using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Streemu.Util;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace Streemu.Telemetria;

/// <summary>
/// Perfil do PC (spec 009, seção 5.1): tudo o que já explicou ou pode explicar um bug que só acontece numa máquina (RX 570 sem AMF, notebook com
/// Intel + GTX, HDR de um amigo, cor avançada, NAT da operadora). "estavel" muda raramente (o hash dele decide se o índice recebe de novo);
/// "momento" é o retrato da hora (bateria, Wi-Fi, ajustes gráficos do Windows). Roda em segundo plano: o WMI leva até alguns segundos.
/// Nada de IP, MAC, nome do PC, nome do usuário nem SSID. O conjunto é o enxuto da spec 018 (seção 5): sem fabricante e modelo do PC, pentes de RAM,
/// KB e data de instalação do Windows; rede, áudio, plano de energia, pasta do app e argumentos de abertura só em forma geral. Programas que interferem e
/// antivírus saem do perfil de rotina e ficam só em <see cref="SobDemanda"/> (sobem com a A6, que decide quando e em quais estados).
/// </summary>
public static partial class Perfil
{
    public static JsonObject Coletar()
    {
        var estavel = new JsonObject
        {
            ["app"] = Tenta(App),
            ["windows"] = Tenta(Windows),
            ["maquina"] = Tenta(Maquina),
            ["cpu"] = Tenta(Cpu),
            ["ram"] = Tenta(Ram),
            ["gpus"] = Tenta(Gpus),
            ["saidas_de_video"] = Tenta(Saidas),
            ["monitores"] = Tenta(Monitores),
            ["preferencia_de_gpu"] = Tenta(PreferenciaGpu),
            ["audio"] = Tenta(Audio),
            ["rede"] = Tenta(Rede),
        };
        // HAGS, Modo de Jogo, Game DVR e captura Xbox ficam aqui, não no "estavel" (D26): mudar um deles não pode gerar perfil novo para o índice
        var momento = new JsonObject
        {
            ["hora_utc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["energia"] = Tenta(Energia),
            ["wifi"] = Tenta(Wifi),
            ["windows_jogo"] = Tenta(WindowsJogo),
        };
        return new JsonObject { ["estavel"] = estavel, ["momento"] = momento };
    }

    /// <summary>Hash do que é estável: muda só com driver novo, monitor novo, Windows atualizado, troca de peça...</summary>
    public static string Hash(JsonObject perfil) => Ids.Hash(perfil["estavel"]?.ToJsonString() ?? "");

    static JsonNode? Tenta(Func<JsonNode?> f)
    {
        try { return f(); }
        catch (Exception e) { return new JsonObject { ["erro"] = e.GetBaseException().Message }; }
    }

    /// <summary>
    /// Consulta WMI com tempo limite: em PC com o WMI corrompido a consulta podia ficar parada para sempre e o perfil nunca sair (estourar o
    /// tempo vira exceção, que entra no perfil como "erro"). Percorre com foreach, sem LINQ e sem "Rewindable = false": o ToList do .NET 10
    /// conta a coleção antes de copiar, contar o WMI já a percorre, e com Rewindable = false a cópia saía VAZIA, sem erro (2026-11-10: CPU e
    /// GPUs sumiram do perfil de todas as instâncias do teste).
    /// </summary>
    internal static List<ManagementObject> Wmi(string consulta, string escopo = @"root\cimv2")
    {
        using var s = new ManagementObjectSearcher(new ManagementScope(escopo), new ObjectQuery(consulta),
            new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(10), ReturnImmediately = true });
        var lista = new List<ManagementObject>();
        using var resultado = s.Get();
        foreach (ManagementObject o in resultado) lista.Add(o);
        return lista;
    }

    /// <summary>Todo PC tem processador, memória e placa de vídeo: lista vazia aqui é falha da consulta, nunca "não tem" (sem isso o perfil mentia).</summary>
    static List<ManagementObject> WmiObrigatorio(string consulta, string oQue)
    {
        var l = Wmi(consulta);
        if (l.Count == 0) throw new InvalidOperationException($"o WMI não listou nenhum(a) {oQue}");
        return l;
    }

    static RegistryKey? Hklm(string caminho) => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(caminho);

    static JsonNode App()
    {
        string pasta = AppContext.BaseDirectory.TrimEnd('\\');
        return new JsonObject
        {
            ["versao"] = Program.Version, ["commit"] = BuildInfo.Commit.Length >= 7 ? BuildInfo.Commit[..7] : "", ["canal"] = BuildInfo.Canal,
            // só se está instalado (pasta do instalador: Programs\Streemu); o caminho em si não sobe
            ["instalado"] = pasta.Contains(@"\Programs\Streemu", StringComparison.OrdinalIgnoreCase),
            // sem caminho: só diz se é o que vem com o app e bate com o SHA-256 esperado ("embutido"), um de teste ("teste") ou se não pode ser usado ("invalido")
            ["ffmpeg"] = Binarios.Origem(Binario.Ffmpeg), ["ffplay"] = Binarios.Origem(Binario.Ffplay), ["mpv"] = Binarios.Origem(Binario.Mpv),
            ["ffmpeg_versao"] = VersaoFfmpeg(),
        };
    }

    static string? VersaoFfmpeg()
    {
        try
        {
            if (Media.Tools.FfmpegOuNulo is not string ffmpeg) return null;
            using var p = Process.Start(new ProcessStartInfo(ffmpeg, "-hide_banner -version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            if (p == null) return null;
            string primeira = p.StandardOutput.ReadLine() ?? "";
            p.WaitForExit(3000);
            return primeira.Replace("ffmpeg version ", "").Split(' ')[0];
        }
        catch { return null; }
    }

    static JsonNode Windows()
    {
        using var k = Hklm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        int build = int.TryParse(k?.GetValue("CurrentBuild")?.ToString(), out var b) ? b : Environment.OSVersion.Version.Build;
        string produto = k?.GetValue("ProductName")?.ToString() ?? "";
        if (build >= 22000) produto = produto.Replace("Windows 10", "Windows 11"); // o registro continua dizendo "10" no 11
        return new JsonObject
        {
            // DisplayVersion ("24H2") só existe a partir do 20H2; antes disso o nome era ReleaseId ("2004")
            ["produto"] = produto, ["edicao"] = k?.GetValue("EditionID")?.ToString(),
            ["versao"] = k?.GetValue("DisplayVersion")?.ToString() ?? k?.GetValue("ReleaseId")?.ToString(),
            // o UBR já diz "uma atualização recente quebrou"; a lista de KBs e a data de instalação do Windows saíram (spec 018, 5.4)
            ["build"] = build, ["ubr"] = k?.GetValue("UBR") is int ubr ? ubr : null,
            ["idioma"] = CultureInfo.InstalledUICulture.Name,
            ["processo_64bits"] = Environment.Is64BitProcess,
        };
    }

    /// <summary>Ajustes gráficos e de jogo do Windows (D26): ficam no "momento", porque a pessoa os muda de vez em quando e isso não é PC novo.</summary>
    static JsonNode WindowsJogo()
    {
        using var gd = Hklm(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
        using var gameBar = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar");
        using var dvr = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore");
        using var captura = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\GameDVR");
        return new JsonObject
        {
            // 2 = ligado, 1 = desligado, ausente = o padrão do driver
            ["agendamento_gpu_hardware"] = gd?.GetValue("HwSchMode") is int h ? (h == 2 ? "ligado" : "desligado") : "padrão",
            ["modo_de_jogo"] = gameBar?.GetValue("AutoGameModeEnabled") is int gm ? gm == 1 : null,
            ["game_dvr"] = dvr?.GetValue("GameDVR_Enabled") is int gd2 ? gd2 == 1 : null,
            ["captura_xbox"] = captura?.GetValue("AppCaptureEnabled") is int ac ? ac == 1 : null,
        };
    }

    static JsonNode Maquina()
    {
        var cs = WmiObrigatorio("SELECT PCSystemType FROM Win32_ComputerSystem", "sistema").First();
        var chassi = Wmi("SELECT ChassisTypes FROM Win32_SystemEnclosure").FirstOrDefault()?["ChassisTypes"] as ushort[];
        // notebook: PCSystemType 2 (mobile) ou chassi 8–10, 14, 31, 32 (portátil, laptop, notebook, sub-notebook, conversível, destacável)
        bool notebook = Convert.ToInt32(cs?["PCSystemType"] ?? 0) == 2 || (chassi?.Any(c => c is 8 or 9 or 10 or 14 or 31 or 32) ?? false);
        return new JsonObject
        {
            // sem fabricante nem modelo do PC (spec 018, 5.4): ninguém lê, e o modelo aponta para o aparelho
            ["tipo"] = notebook ? "notebook" : "desktop",
            ["tem_bateria"] = SystemInformation.PowerStatus.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery,
        };
    }

    static JsonNode Cpu()
    {
        var p = WmiObrigatorio("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor", "processador");
        var c = p.First();
        return new JsonObject
        {
            ["nome"] = c["Name"]?.ToString()?.Trim(), ["nucleos"] = p.Sum(x => Convert.ToInt32(x["NumberOfCores"])),
            ["threads"] = p.Sum(x => Convert.ToInt32(x["NumberOfLogicalProcessors"])), ["clock_base_mhz"] = Convert.ToInt32(c["MaxClockSpeed"]),
            ["soquetes"] = p.Count,
        };
    }

    static JsonNode Ram()
    {
        var pentes = WmiObrigatorio("SELECT Capacity FROM Win32_PhysicalMemory", "pente de memória");
        // só o total: pentes, clock e RAM livre saíram (spec 018, 5.4)
        return new JsonObject { ["total_gb"] = Math.Round(pentes.Sum(x => Convert.ToDouble(x["Capacity"])) / (1L << 30), 1) };
    }

    static JsonNode Gpus()
    {
        // VRAM de verdade vem do registro do driver: o AdapterRAM do WMI é de 32 bits e trava em 4 GB
        var vram = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using (var classe = Hklm(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
            foreach (var sub in classe?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                try
                {
                    using var k = classe!.OpenSubKey(sub);
                    if (k?.GetValue("DriverDesc") is string desc && k.GetValue("HardwareInformation.qwMemorySize") is long q) vram[desc] = q;
                }
                catch { }
            }
        var arr = new JsonArray();
        foreach (var o in WmiObrigatorio("SELECT Name, AdapterCompatibility, DriverVersion, DriverDate, PNPDeviceID, VideoProcessor, Status FROM Win32_VideoController", "placa de vídeo"))
        {
            string nome = o["Name"]?.ToString() ?? "?";
            string pnp = o["PNPDeviceID"]?.ToString() ?? "";
            var ven = System.Text.RegularExpressions.Regex.Match(pnp, @"VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})");
            long? mem = vram.TryGetValue(nome, out var v) ? v : null;
            bool integrada = PareceIntegrada(nome, mem);
            arr.Add(new JsonObject
            {
                ["nome"] = nome, ["fabricante"] = o["AdapterCompatibility"]?.ToString(), ["driver"] = o["DriverVersion"]?.ToString(),
                ["driver_nvidia"] = DriverNvidia(o["DriverVersion"]?.ToString(), nome),
                ["driver_data"] = o["DriverDate"] is string dd && dd.Length >= 8 ? $"{dd[..4]}-{dd[4..6]}-{dd[6..8]}" : null,
                ["pci"] = ven.Success ? $"{ven.Groups[1].Value}:{ven.Groups[2].Value}" : null,
                ["vram_mb"] = mem is long m2 ? m2 >> 20 : null, ["integrada"] = integrada, ["situacao"] = o["Status"]?.ToString(),
            });
        }
        return arr;
    }

    /// <summary>Placa integrada pelo nome (e pela VRAM, quando se sabe). A mesma regra no perfil e no caminho de mídia (<see cref="ContextoMidia"/>), para o painel cruzar os dois.</summary>
    internal static bool PareceIntegrada(string nome, long? vram)
    {
        string n = nome.ToLowerInvariant();
        return n.Contains("uhd") || n.Contains("iris") || n.Contains("hd graphics") || (n.Contains("radeon") && n.Contains("graphics") && !n.Contains("rx"))
               || (vram is long mm && mm < 1L << 30);
    }

    /// <summary>32.0.15.6094 → 560.94 (o número que a NVIDIA mostra; é o que se procura em fórum e changelog).</summary>
    static string? DriverNvidia(string? versao, string nome)
    {
        if (versao == null || !nome.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return null;
        var p = versao.Split('.');
        if (p.Length != 4 || p[2].Length < 2) return null;
        string d = (p[2][^1..] + p[3]).PadLeft(5, '0');
        return $"{int.Parse(d[..3])}.{d[3..]}";
    }

    /// <summary>Qual placa é dona de qual monitor (na ordem do ddagrab). Notebook híbrido: o monitor costuma estar na integrada, não na GTX.</summary>
    static JsonNode Saidas() => new JsonArray(Display.Outputs().Select(o => (JsonNode)new JsonObject
    {
        ["placa"] = o.Adapter, ["placa_nome"] = o.AdapterName, ["fabricante_id"] = o.VendorId.ToString("X4"), ["saida"] = o.OutputIdx,
        ["monitor"] = o.DeviceName, ["resolucao"] = $"{o.Rect.Right - o.Rect.Left}x{o.Rect.Bottom - o.Rect.Top}",
        ["hdr"] = o.Hdr, ["cor_avancada_sem_hdr"] = o.LinearCapture,
    }).ToArray());

    static JsonNode Monitores()
    {
        var arr = new JsonArray();
        foreach (var s in Screen.AllScreens)
        {
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            bool ok = EnumDisplaySettings(s.DeviceName, -1, ref dm);
            uint dpi = 96;
            try { GetDpiForMonitor(MonitorFromPoint(new Point(s.Bounds.Left + 1, s.Bounds.Top + 1), 2), 0, out dpi, out _); } catch { }
            arr.Add(new JsonObject
            {
                ["nome"] = s.DeviceName, ["principal"] = s.Primary, ["resolucao"] = $"{s.Bounds.Width}x{s.Bounds.Height}",
                ["hz"] = ok ? dm.dmDisplayFrequency : null, ["bits"] = s.BitsPerPixel, ["escala_pct"] = (int)Math.Round(dpi / 96.0 * 100),
            });
        }
        return arr;
    }

    static JsonNode PreferenciaGpu()
    {
        // Configurações > Sistema > Tela > Gráficos: "GpuPreference=1" economia (integrada), 2 alto desempenho
        var o = new JsonObject();
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
        foreach (var nome in k?.GetValueNames() ?? Array.Empty<string>())
        {
            string n = nome.ToLowerInvariant();
            if (n.Contains("streemu") || n.EndsWith("ffmpeg.exe") || n.EndsWith("ffplay.exe"))
                o[Path.GetFileName(nome)] = k!.GetValue(nome)?.ToString(); // só o nome do exe: a pasta (que a Higiene mascara fora de C:\Users) não importa para a preferência
        }
        return o;
    }

    static JsonNode Audio()
    {
        using var e = new MMDeviceEnumerator();
        // Só o formato (Hz, bits, canais) e a quantidade de saídas sobem; o NOME do fone e do microfone não (spec 018, 5.2: o caso do grave estourado se
        // resolveu pelo formato). Os nomes só são ensinados à Higiene, que os tira de qualquer linha de log em que apareçam.
        var nomes = new List<string>();
        try { foreach (var d in e.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active)) using (d) nomes.Add(d.FriendlyName); } catch { }
        Higiene.RegistrarDispositivos(nomes);
        JsonNode? Disp(DataFlow f, Role r)
        {
            try
            {
                using var d = e.GetDefaultAudioEndpoint(f, r);
                Higiene.RegistrarDispositivos(new[] { d.FriendlyName });
                using var cliente = d.CreateAudioClient();
                var fmt = cliente.MixFormat;
                return new JsonObject { ["hz"] = fmt.SampleRate, ["bits"] = fmt.BitsPerSample, ["canais"] = fmt.Channels, ["formato"] = fmt.Encoding.ToString() };
            }
            catch (Exception ex) { return new JsonObject { ["erro"] = Higiene.Limpar(ex.Message) }; }
        }
        return new JsonObject
        {
            ["saida_padrao"] = Disp(DataFlow.Render, Role.Multimedia),
            ["saida_comunicacao"] = Disp(DataFlow.Render, Role.Communications),
            ["microfone_padrao"] = Disp(DataFlow.Capture, Role.Communications),
            ["saidas_ativas"] = e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Count,
        };
    }

    static readonly string[] Vpn = { "radmin", "zerotier", "hamachi", "tailscale", "wireguard", "openvpn", "tap-windows", "nordlynx", "proton", "warp", "fortinet", "cisco anyconnect" };

    static JsonNode Rede()
    {
        var arr = new JsonArray();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (n.OperationalStatus != OperationalStatus.Up) continue;
            string d = n.Description.ToLowerInvariant();
            // só cabo ou Wi-Fi, a velocidade e se é VPN (spec 018, 5.2): a descrição do adaptador, o IPv4 e o gateway saíram. A descrição só serve aqui para reconhecer a VPN.
            arr.Add(new JsonObject
            {
                ["tipo"] = n.NetworkInterfaceType switch { NetworkInterfaceType.Wireless80211 => "wifi", NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT => "cabo", _ => "outro" },
                ["mbps"] = n.Speed > 0 ? n.Speed / 1_000_000 : null, ["vpn"] = Vpn.Any(d.Contains),
            });
        }
        return arr;
    }

    /// <summary>
    /// Antivírus pelo nome do processo (a lista de processos já é lida para os programas que interferem). Antes era a consulta WMI do
    /// SecurityCenter2: "exe sem assinatura que pergunta qual antivírus está instalado e envia para a internet" é o padrão que a heurística
    /// do Kaspersky e do Defender procura em programa que rouba dados, e um falso positivo põe o Streemu inteiro em quarentena.
    /// </summary>
    static readonly (string Processo, string Nome)[] Antivirus_ =
    {
        ("msmpeng", "Microsoft Defender"), ("avp", "Kaspersky"), ("avpui", "Kaspersky"), ("avastsvc", "Avast"), ("aswengsrv", "Avast"),
        ("avgsvc", "AVG"), ("bdservicehost", "Bitdefender"), ("bdagent", "Bitdefender"), ("ekrn", "ESET"), ("mcshield", "McAfee"),
        ("mfemms", "McAfee"), ("nortonsvc", "Norton"), ("ns", "Norton"), ("mbamservice", "Malwarebytes"), ("avguard", "Avira"),
        ("psuaservice", "Panda"), ("wrsa", "Webroot"), ("sophoshealth", "Sophos"), ("360tray", "360 Total Security"), ("kvmonxp", "Jiangmin"),
    };

    static JsonNode Antivirus()
    {
        var achados = new SortedSet<string>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string n = p.ProcessName.ToLowerInvariant();
                foreach (var (proc, nome) in Antivirus_) if (n == proc) achados.Add(nome);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return new JsonArray(achados.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
    }

    static JsonNode Energia()
    {
        var p = new SYSTEM_POWER_STATUS();
        bool ok = GetSystemPowerStatus(ref p);
        return new JsonObject
        {
            // sem a % da bateria (não muda diagnóstico) e com o plano só como categoria (o nome pode ser do fabricante), spec 018, 5.2
            ["na_tomada"] = ok ? p.ACLineStatus == 1 : null,
            ["economia_de_bateria"] = ok ? p.SystemStatusFlag == 1 : null, ["plano"] = PlanoDeEnergia(),
        };
    }

    /// <summary>
    /// O plano de energia ativo como categoria: <c>economia</c>, <c>equilibrado</c>, <c>desempenho</c> ou <c>outro</c> (plano do fabricante ou criado
    /// pela pessoa). Vem do GUID do plano pela função do Windows (antes abria o powercfg.exe: outro programa a menos para o antivírus estranhar).
    /// </summary>
    static string? PlanoDeEnergia()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var guid) != 0) return null;
        try { return CategoriaDoPlano(Marshal.PtrToStructure<Guid>(guid)); }
        catch { return null; }
        finally { LocalFree(guid); }
    }

    /// <summary>Os planos padrão do Windows pelo GUID; qualquer outro é "outro" (só o nome do plano de um fabricante já diz a marca do PC).</summary>
    internal static string CategoriaDoPlano(Guid plano) => plano.ToString() switch
    {
        "a1841308-3541-4fab-bc81-f71556f20b4a" => "economia",
        "381b4222-f694-41f0-9685-ff5bb260df2e" => "equilibrado",
        "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" or "e9a42b02-d5df-448d-aa00-03f14749eb61" or "ded574b5-45a0-4f42-8737-46345c09c238" => "desempenho",
        _ => "outro",
    };

    /// <summary>
    /// "netsh wlan show interfaces" (rótulos no idioma do Windows). Sai tudo menos nome da rede, endereços físicos e perfil.
    /// Só roda com Wi-Fi ligado (desktop no cabo não abre o netsh à toa) e, no Windows 11 24H2 ou mais novo, só se a localização já estiver
    /// liberada para apps da área de trabalho: lá o netsh passou a exigir essa permissão, e o diagnóstico nunca pode pedir nada à pessoa.
    /// </summary>
    static JsonNode? Wifi()
    {
        bool temWifi = NetworkInterface.GetAllNetworkInterfaces().Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && n.OperationalStatus == OperationalStatus.Up);
        if (!temWifi) return null;
        if (Environment.OSVersion.Version.Build >= 26100 && !LocalizacaoLiberada())
            return new JsonObject { ["sem_detalhes"] = "localização do Windows desligada para apps da área de trabalho (o Windows 11 24H2 exige para ler o Wi-Fi)" };
        using var proc = Process.Start(new ProcessStartInfo("netsh", "wlan show interfaces")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
        if (proc == null) return null;
        using var ms = new MemoryStream();
        proc.StandardOutput.BaseStream.CopyTo(ms);
        proc.WaitForExit(3000);
        return WifiDaSaida(ms.ToArray());
    }

    static readonly System.Text.Encoding Utf8Estrito = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true);

    /// <summary>
    /// Os bytes do netsh viram texto: UTF-8 estrito primeiro (o Windows 11 26300 respondeu assim em 11/11 e os rótulos chegaram
    /// quebrados na página 850, "Endere├ºo f├¡sico", deixando o MAC passar), e a página OEM do console (850 em português) como reserva.
    /// </summary>
    internal static string DecodificarConsole(byte[] bytes)
    {
        try { return Utf8Estrito.GetString(bytes); }
        catch (System.Text.DecoderFallbackException) { }
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        return System.Text.Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage).GetString(bytes);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex Guid();

    /// <summary>
    /// Duas barreiras: o rótulo (SSID, endereço físico, perfil, GUID, nome) e o VALOR. Um valor com cara de MAC/BSSID ou de GUID
    /// nunca sai, mesmo que o rótulo venha num idioma ou numa codificação que o filtro do rótulo não conhece.
    /// </summary>
    internal static JsonObject? WifiDaSaida(byte[] bytes)
    {
        // Spec 018, 5.2: do netsh só sobem a banda (2,4 / 5 / 6 GHz) e o sinal em %, que explica perda em P2P. A descrição da placa, o canal, a taxa e o
        // tipo de rádio ninguém lê. As barreiras de antes ficam: um rótulo de identidade ou um valor com cara de MAC/GUID nunca entra, em qualquer idioma.
        string? banda = null; int? sinal = null, canal = null;
        foreach (var linha in DecodificarConsole(bytes).Split('\n'))
        {
            int i = linha.IndexOf(':');
            if (i <= 0) continue;
            string chave = linha[..i].Trim(), valor = linha[(i + 1)..].Trim();
            string c = chave.ToLowerInvariant();
            if (c.Contains("ssid") || c.Contains("físico") || c.Contains("fisico") || c.Contains("physical") || c.Contains("perfil") || c.Contains("profile")
                || c.Contains("guid") || c == "nome" || c == "name" || chave.Length > 40) continue;
            if (Higiene.Mac().IsMatch(valor) || Guid().IsMatch(valor)) continue;
            if (c is "sinal" or "signal") { if (int.TryParse(valor.TrimEnd('%', ' '), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pct) && pct is >= 0 and <= 100) sinal = pct; }
            else if (c is "banda" or "band") banda = BandaDoTexto(valor) ?? banda;
            else if (c is "canal" or "channel") { if (int.TryParse(valor, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ch) && ch is > 0 and < 300) canal = ch; }
        }
        banda ??= canal is int n ? (n <= 14 ? "2,4 GHz" : "5 ou 6 GHz") : null; // versões antigas do netsh só trazem o canal
        if (banda == null && sinal == null) return null;
        return new JsonObject { ["banda"] = banda, ["sinal_pct"] = sinal };
    }

    static string? BandaDoTexto(string v)
    {
        string t = v.Replace(',', '.').ToLowerInvariant();
        if (t.Contains("2.4")) return "2,4 GHz";
        if (t.Contains('6')) return "6 GHz";
        if (t.Contains('5')) return "5 GHz";
        return null;
    }

    /// <summary>Localização liberada no Windows inteiro e para apps da área de trabalho (Configurações > Privacidade > Localização).</summary>
    static bool LocalizacaoLiberada()
    {
        const string chave = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location";
        try
        {
            using var maquina = Hklm(chave);
            using var usuario = Registry.CurrentUser.OpenSubKey(chave + @"\NonPackaged");
            return maquina?.GetValue("Value") as string == "Allow" && usuario?.GetValue("Value") as string == "Allow";
        }
        catch { return false; }
    }

    /// <summary>
    /// Lista reduzida (spec 018, 5.3): só o que mexe em captura, overlay, rede virtual e áudio virtual. Saíram Steam, Epic, EA, Battle.net, iCUE, Razer e
    /// LGHub (quase sempre abertos, sinal fraco, e mostram hábito). Virtual Desktop, Sunshine e Moonlight entraram: aparecem como monitor ou placa virtual
    /// (LIV-148, 162 e 163).
    /// </summary>
    static readonly string[] Interferem =
    {
        "obs64", "obs32", "streamlabs obs", "xsplit.core", "bandicam", "action_x64", "nvsphelper64", "nvidia share", "rtss", "msiafterburner",
        "parsecd", "virtualdesktop.streamer", "sunshine", "moonlight", "medal", "overwolf", "outplayed", "wallpaper64", "wallpaper32", "gamebar",
        "radeonsoftware", "amdrsserv", "voicemeeter", "voicemeeterpro", "voicemeeter8", "voicemeeter8x64", "steelseriessonar", "nahimicsvc64",
        "rvrvpngui", "zerotier_desktop_ui", "hamachi-2-ui", "tailscale-ipn",
    };

    /// <summary>
    /// O que sobe só sob demanda (spec 018, 5.3): programas que interferem e antivírus, lidos agora, não no perfil de rotina. <b>Ainda sem quem chame:</b>
    /// a A6 decide os gatilhos (Marcar problema, falha grave de captura ou de rede, relatório do instalador) e os estados em que sobe (nunca por falha no
    /// Desligado). Lê só a lista de processos, sem WMI.
    /// </summary>
    public static JsonObject SobDemanda() => new()
    {
        ["programas_que_interferem"] = Tenta(Programas),
        ["antivirus"] = Tenta(Antivirus),
    };

    static JsonNode Programas()
    {
        var abertos = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try { string n = p.ProcessName.ToLowerInvariant(); if (Interferem.Contains(n)) abertos.Add(p.ProcessName); }
            catch { }
            finally { p.Dispose(); }
        }
        return new JsonArray(abertos.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
    }

    // ------------------------------------------------------------------ Win32

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(ref SYSTEM_POWER_STATUS status);
    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr rootPowerKey, out IntPtr activePolicyGuid);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr mem);

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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point p, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
