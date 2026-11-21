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
/// "momento" é o retrato da hora (bateria, Wi-Fi, programas abertos). Roda em segundo plano: o WMI leva até alguns segundos.
/// Nada de IP, MAC, nome do PC, nome do usuário nem SSID.
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
        var momento = new JsonObject
        {
            ["hora_utc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["fuso"] = TimeZoneInfo.Local.BaseUtcOffset.ToString(),
            ["ligado_ha_h"] = Math.Round(Environment.TickCount64 / 3600000.0, 1),
            ["energia"] = Tenta(Energia),
            ["wifi"] = Tenta(Wifi),
            ["programas_que_interferem"] = Tenta(Programas),
            ["antivirus"] = Tenta(Antivirus),
            ["ram_livre_mb"] = Tenta(() => JsonValue.Create(RamLivreMb())),
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
            ["pasta"] = Higiene.Limpar(pasta),
            // pasta do instalador: Programs\Streemu
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
        long instalado = Convert.ToInt64(k?.GetValue("InstallDate") ?? 0L);
        using var gd = Hklm(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
        using var gameBar = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar");
        using var dvr = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore");
        using var captura = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\GameDVR");
        return new JsonObject
        {
            // DisplayVersion ("24H2") só existe a partir do 20H2; antes disso o nome era ReleaseId ("2004")
            ["produto"] = produto, ["edicao"] = k?.GetValue("EditionID")?.ToString(),
            ["versao"] = k?.GetValue("DisplayVersion")?.ToString() ?? k?.GetValue("ReleaseId")?.ToString(),
            ["build"] = build, ["ubr"] = k?.GetValue("UBR") is int ubr ? ubr : null,
            ["instalado_em"] = instalado > 0 ? DateTimeOffset.FromUnixTimeSeconds(instalado).ToString("yyyy-MM-dd") : null,
            ["idioma"] = CultureInfo.InstalledUICulture.Name,
            ["ultima_atualizacao"] = UltimaAtualizacao(),
            // 2 = ligado, 1 = desligado, ausente = o padrão do driver
            ["agendamento_gpu_hardware"] = gd?.GetValue("HwSchMode") is int h ? (h == 2 ? "ligado" : "desligado") : "padrão",
            ["modo_de_jogo"] = gameBar?.GetValue("AutoGameModeEnabled") is int gm ? gm == 1 : null,
            ["game_dvr"] = dvr?.GetValue("GameDVR_Enabled") is int gd2 ? gd2 == 1 : null,
            ["captura_xbox"] = captura?.GetValue("AppCaptureEnabled") is int ac ? ac == 1 : null,
            ["processo_64bits"] = Environment.Is64BitProcess,
        };
    }

    static JsonNode? UltimaAtualizacao()
    {
        // Win32_QuickFixEngineering é lento (1–3 s): por isso o perfil todo roda fora da tela
        var kbs = Wmi("SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering")
            .Select(o => (Id: o["HotFixID"]?.ToString() ?? "", Em: DateTime.TryParse(o["InstalledOn"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.MinValue))
            .OrderByDescending(x => x.Em).ToList();
        if (kbs.Count == 0) return null;
        return new JsonObject { ["kb"] = kbs[0].Id, ["em"] = kbs[0].Em == DateTime.MinValue ? null : kbs[0].Em.ToString("yyyy-MM-dd"), ["total_kbs"] = kbs.Count };
    }

    static JsonNode Maquina()
    {
        var cs = WmiObrigatorio("SELECT Manufacturer, Model, PCSystemType, TotalPhysicalMemory FROM Win32_ComputerSystem", "sistema").First();
        var chassi = Wmi("SELECT ChassisTypes FROM Win32_SystemEnclosure").FirstOrDefault()?["ChassisTypes"] as ushort[];
        // notebook: PCSystemType 2 (mobile) ou chassi 8–10, 14, 31, 32 (portátil, laptop, notebook, sub-notebook, conversível, destacável)
        bool notebook = Convert.ToInt32(cs?["PCSystemType"] ?? 0) == 2 || (chassi?.Any(c => c is 8 or 9 or 10 or 14 or 31 or 32) ?? false);
        return new JsonObject
        {
            ["fabricante"] = cs?["Manufacturer"]?.ToString(), ["modelo"] = cs?["Model"]?.ToString(),
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
        var pentes = WmiObrigatorio("SELECT Capacity, Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory", "pente de memória");
        return new JsonObject
        {
            ["total_gb"] = Math.Round(pentes.Sum(x => Convert.ToDouble(x["Capacity"])) / (1L << 30), 1),
            ["pentes"] = new JsonArray(pentes.Select(x => (JsonNode)new JsonObject
            {
                ["gb"] = Math.Round(Convert.ToDouble(x["Capacity"]) / (1L << 30), 1),
                ["mhz"] = Convert.ToInt32(x["ConfiguredClockSpeed"] ?? x["Speed"] ?? 0),
            }).ToArray()),
        };
    }

    static int RamLivreMb()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref m) ? (int)(m.ullAvailPhys >> 20) : -1;
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
                o[Higiene.Limpar(nome)] = k!.GetValue(nome)?.ToString();
        }
        return o;
    }

    static JsonNode Audio()
    {
        using var e = new MMDeviceEnumerator();
        JsonNode? Disp(DataFlow f, Role r)
        {
            try
            {
                using var d = e.GetDefaultAudioEndpoint(f, r);
                using var cliente = d.CreateAudioClient();
                var fmt = cliente.MixFormat;
                return new JsonObject { ["nome"] = d.FriendlyName, ["hz"] = fmt.SampleRate, ["bits"] = fmt.BitsPerSample, ["canais"] = fmt.Channels, ["formato"] = fmt.Encoding.ToString() };
            }
            catch (Exception ex) { return new JsonObject { ["erro"] = ex.Message }; }
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
            bool tem4 = false, temGateway = false;
            try
            {
                var ip = n.GetIPProperties();
                tem4 = ip.UnicastAddresses.Any(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                temGateway = ip.GatewayAddresses.Count > 0;
            }
            catch { }
            arr.Add(new JsonObject
            {
                ["tipo"] = n.NetworkInterfaceType.ToString(), ["descricao"] = n.Description, ["mbps"] = n.Speed > 0 ? n.Speed / 1_000_000 : null,
                ["vpn"] = Vpn.Any(d.Contains), ["ipv4"] = tem4, ["tem_gateway"] = temGateway,
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
            ["na_tomada"] = ok ? p.ACLineStatus == 1 : null, ["bateria_pct"] = ok && p.BatteryLifePercent <= 100 ? p.BatteryLifePercent : null,
            ["economia_de_bateria"] = ok ? p.SystemStatusFlag == 1 : null, ["plano"] = PlanoDeEnergia(),
        };
    }

    /// <summary>Nome do plano de energia ativo pela função do Windows (antes abria o powercfg.exe: outro programa a menos para o antivírus estranhar).</summary>
    static string? PlanoDeEnergia()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var guid) != 0) return null;
        try
        {
            uint tam = 0;
            PowerReadFriendlyName(IntPtr.Zero, guid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref tam);
            if (tam == 0 || tam > 4096) return null;
            var buf = Marshal.AllocHGlobal((int)tam);
            try { return PowerReadFriendlyName(IntPtr.Zero, guid, IntPtr.Zero, IntPtr.Zero, buf, ref tam) == 0 ? Marshal.PtrToStringUni(buf) : null; }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { LocalFree(guid); }
    }

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
        var o = new JsonObject();
        foreach (var linha in DecodificarConsole(bytes).Split('\n'))
        {
            int i = linha.IndexOf(':');
            if (i <= 0) continue;
            string chave = linha[..i].Trim(), valor = linha[(i + 1)..].Trim();
            string c = chave.ToLowerInvariant();
            if (c.Contains("ssid") || c.Contains("físico") || c.Contains("fisico") || c.Contains("physical") || c.Contains("perfil") || c.Contains("profile")
                || c.Contains("guid") || c == "nome" || c == "name" || chave.Length > 40) continue;
            if (Higiene.Mac().IsMatch(valor) || Guid().IsMatch(valor)) continue;
            o[chave] = valor;
        }
        return o.Count == 0 ? null : o;
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

    static readonly string[] Interferem =
    {
        "obs64", "obs32", "streamlabs obs", "xsplit.core", "bandicam", "action_x64", "nvsphelper64", "nvidia share", "rtss", "msiafterburner",
        "parsecd", "medal", "overwolf", "outplayed", "wallpaper64", "wallpaper32", "gamebar", "radeonsoftware", "amdrsserv", "lghub",
        "icue", "razercentralservice", "voicemeeter", "voicemeeterpro", "voicemeeter8", "voicemeeter8x64", "steelseriessonar", "nahimicsvc64",
        "rvrvpngui", "zerotier_desktop_ui", "hamachi-2-ui", "tailscale-ipn", "steamwebhelper", "epicgameslauncher", "battle.net", "ea desktop",
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
    [DllImport("powrprof.dll")] static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupGuid, IntPtr settingGuid, IntPtr buffer, ref uint bufferSize);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr mem);

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

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
