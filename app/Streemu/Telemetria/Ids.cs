using System.Security.Cryptography;
using System.Text;
using Streemu.Util;
using Microsoft.Win32;

namespace Streemu.Telemetria;

/// <summary>
/// Quem é este PC para a telemetria (spec 009, seção 4). Nada que a pessoa escolhe (apelido, avatar, nome do Discord) entra aqui:
/// o InstallId já mudou sozinho quando o estado.json voltou zerado (uma pessoa virou três na sala, DESCOBERTAS 2026-11-08).
/// </summary>
public static class Ids
{
    static string? _pc;
    static (string Texto, bool EhMachineGuid)? _fontePc;
    static readonly object _gateFonte = new();
    static IReadOnlyList<string>? _partesHw;
    static bool _partesHwLidas;
    static readonly object _gateHw = new();

    /// <summary>
    /// SHA-256 do MachineGuid do Windows (16 caracteres). Não muda reinstalando o app nem apagando o estado; só com o Windows reinstalado
    /// (aí o servidor liga o PC novo ao antigo pelo <see cref="Hardware"/>). Sem MachineGuid, um id aleatório guardado na pasta da telemetria.
    /// </summary>
    public static string Pc => _pc ??= HashPc(FontePc());

    /// <summary>
    /// De onde vem o id deste PC: o MachineGuid (minúsculo) ou, sem ele, o id aleatório guardado em <c>pc.txt</c> (que já é um hash de 16 caracteres).
    /// A telemetria e a conta (spec 029, seção 8.2) partem da MESMA fonte, cada uma com o seu prefixo (<see cref="HashPc"/> e <see cref="HashPcConta"/>):
    /// assim o id da conta não cruza com o da telemetria, mas também não muda a cada abertura. Lida uma vez só.
    /// </summary>
    public static (string Texto, bool EhMachineGuid) FontePc() { lock (_gateFonte) return _fontePc ??= LerFontePc(); }

    /// <summary>O id do PC da telemetria a partir da fonte. Não mude a fórmula: o servidor liga os relatórios antigos por ele.</summary>
    public static string HashPc((string Texto, bool EhMachineGuid) fonte) => fonte.EhMachineGuid ? Hash("streemu-pc:" + fonte.Texto) : fonte.Texto;

    /// <summary>O id do PC que vai no login da conta: mesma fonte, prefixo próprio.</summary>
    public static string HashPcConta((string Texto, bool EhMachineGuid) fonte) => Hash("streemu-conta-pc:" + fonte.Texto);

    static (string Texto, bool EhMachineGuid) LerFontePc()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (k?.GetValue("MachineGuid") is string g && g.Length >= 16) return (g.Trim().ToLowerInvariant(), true);
        }
        catch (Exception e) { Log.Info("[telemetria] não li o MachineGuid: " + e.Message); }
        string arquivo = Path.Combine(Pasta.Raiz, "pc.txt");
        try
        {
            if (File.Exists(arquivo) && File.ReadAllText(arquivo).Trim() is { Length: 16 } salvo) return (salvo, false);
            string novo = Hash("streemu-pc-aleatorio:" + Guid.NewGuid());
            Directory.CreateDirectory(Pasta.Raiz);
            File.WriteAllText(arquivo, novo);
            return (novo, false);
        }
        catch { return (Hash("streemu-pc-aleatorio:" + Guid.NewGuid()), false); }
    }

    /// <summary>
    /// Impressão do hardware (UUID da placa-mãe, serial da placa e da BIOS), para reconhecer o mesmo PC depois de reinstalar o Windows.
    /// Seriais genéricos de fábrica ("To be filled by O.E.M.") não contam; com menos de dois bons, fica nulo (melhor não juntar do que juntar errado).
    /// Sem o serial do disco: ler o disco é o que programa de "HWID" e ladrão de dados fazem, e a heurística do antivírus olha isso.
    /// WMI é lento (até segundos): só em segundo plano, com tempo limite.
    /// </summary>
    public static string? Hardware() => HashHw(PartesHardware());

    /// <summary>Pode demorar (WMI). A consulta roda uma vez só por execução e é dividida entre a telemetria e a conta: quem chegar depois espera a mesma. Nulo = menos de duas partes boas.</summary>
    public static IReadOnlyList<string>? PartesHardware()
    {
        lock (_gateHw)
        {
            if (!_partesHwLidas) { _partesHw = LerPartesHardware(); _partesHwLidas = true; }
            return _partesHw;
        }
    }

    /// <summary>O hardware da telemetria a partir das partes. Não mude a fórmula.</summary>
    public static string? HashHw(IReadOnlyList<string>? partes) => partes is { Count: >= 2 } ? Hash("streemu-hw:" + string.Join("|", partes)) : null;

    /// <summary>O hardware que vai no login da conta: mesmas partes, prefixo próprio.</summary>
    public static string? HashHwConta(IReadOnlyList<string>? partes) => partes is { Count: >= 2 } ? Hash("streemu-conta-hw:" + string.Join("|", partes)) : null;

    static IReadOnlyList<string>? LerPartesHardware()
    {
        var partes = new List<string>();
        void Pega(string tabela, string campo)
        {
            try
            {
                foreach (var o in Perfil.Wmi($"SELECT {campo} FROM {tabela}"))
                {
                    string v = o[campo]?.ToString()?.Trim() ?? "";
                    if (!Generico(v)) { partes.Add(tabela + "=" + v); return; }
                }
            }
            catch { }
        }
        Pega("Win32_ComputerSystemProduct", "UUID");
        Pega("Win32_BaseBoard", "SerialNumber");
        Pega("Win32_BIOS", "SerialNumber");
        return partes.Count >= 2 ? partes : null;
    }

    static bool Generico(string v)
    {
        if (v.Length < 4 || v.Distinct().Count() <= 2) return true; // "0000000", "FFFFFFFF-FFFF-..."
        string n = v.ToLowerInvariant();
        return n.Contains("to be filled") || n.Contains("default string") || n.Contains("system serial") || n.Contains("not specified")
            || n.Contains("not applicable") || n == "none" || n.Contains("o.e.m") || n == "03000200-0400-0500-0006-000700080009";
    }

    public static string Hash(string texto) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(texto)))[..16].ToLowerInvariant();
}

/// <summary>
/// Pastas da telemetria: %LOCALAPPDATA%\Streemu\telemetria. Teste nunca mistura com o uso real: com STREEMU_TEST_DIR fica dentro dela, e com
/// STREEMU_TELEMETRIA_URL (servidor de teste) fica numa pasta própria daquele servidor. Sem isso, uma sessão de teste ficava na pasta real e a
/// próxima abertura do app de verdade a mandava para o servidor do grupo (visto em 2026-11-10, antes de acontecer).
/// </summary>
public static class Pasta
{
    public static string Raiz { get; } = Environment.GetEnvironmentVariable("STREEMU_TEST_DIR") is string t
        ? Path.Combine(t, "telemetria")
        : Environment.GetEnvironmentVariable("STREEMU_TELEMETRIA_URL") is string url
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Streemu", "telemetria-teste-" + Ids.Hash(url.TrimEnd('/'))[..8])
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Streemu", "telemetria");
}
