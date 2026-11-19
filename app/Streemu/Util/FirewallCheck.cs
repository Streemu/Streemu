using System.Diagnostics;

namespace Streemu.Util;

/// <summary>
/// Lê (sem precisar de administrador) as regras do Firewall do Windows para este .exe.
/// Quando alguém clica em "Cancelar" no aviso do Firewall, o Windows cria regras de BLOQUEIO de entrada,
/// e aí o amigo não consegue mandar nada para cá.
/// </summary>
public static class FirewallCheck
{
    public sealed record Result(string Summary, bool BlockingNow, bool AllowedNow, bool NoRules);

    public static Result Inspect()
    {
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type == null) return new("não consegui abrir o Firewall do Windows", false, false, false);
            dynamic policy = Activator.CreateInstance(type)!;
            int current = policy.CurrentProfileTypes;
            bool enabledHere = true;
            try
            {
                enabledHere = false;
                foreach (int bit in new[] { 1, 2, 4 })
                    if ((current & bit) != 0 && (bool)policy.get_FirewallEnabled(bit)) enabledHere = true;
            }
            catch { enabledHere = true; }
            string exe = Environment.ProcessPath ?? "";
            var lines = new List<string>();
            bool blocking = false, allowing = false;
            foreach (dynamic rule in policy.Rules)
            {
                string? app = rule.ApplicationName;
                if (app == null || !string.Equals(Path.GetFullPath(app), exe, StringComparison.OrdinalIgnoreCase)) continue;
                int direction = rule.Direction, action = rule.Action, profiles = rule.Profiles, protocol = rule.Protocol;
                bool enabled = rule.Enabled;
                lines.Add($"{(direction == 1 ? "entrada" : "saída")} {(action == 1 ? "permite" : "BLOQUEIA")} " +
                          $"{(protocol == 17 ? "UDP" : protocol == 6 ? "TCP" : protocol == 256 ? "tudo" : protocol.ToString())} " +
                          $"[{Profiles(profiles)}]{(enabled ? "" : " (desativada)")}");
                bool appliesNow = enabled && direction == 1 && (profiles & current) != 0 && (protocol == 17 || protocol == 256);
                if (appliesNow && action == 0) blocking = true;
                if (appliesNow && action == 1) allowing = true;
            }
            string summary = $"Firewall {(enabledHere ? "ligado" : "desligado")} na rede atual ({Profiles(current)}). " +
                             (lines.Count == 0 ? "Nenhuma regra para o Streemu ainda (o Windows deve perguntar)." : "Regras do streemu: " + string.Join("; ", lines));
            return new(summary, enabledHere && blocking, !enabledHere || allowing, lines.Count == 0);
        }
        catch (Exception e)
        {
            return new("não consegui ler as regras do Firewall: " + e.Message, false, false, false);
        }
    }

    static string Profiles(int mask)
    {
        var p = new List<string>();
        if ((mask & 1) != 0) p.Add("domínio");
        if ((mask & 2) != 0) p.Add("privada");
        if ((mask & 4) != 0) p.Add("pública");
        return p.Count == 0 ? "nenhuma" : string.Join("/", p);
    }

    /// <summary>
    /// Libera a entrada do Streemu.exe no Firewall em todas as redes. Apaga antes TODAS as regras deste .exe porque no
    /// Firewall do Windows bloqueio vence permissão: só acrescentar "permite" não adiantava depois de um "Cancelar".
    /// Pede administrador (UAC) uma vez. O instalador roda o mesmo comando (instalador/streemu.iss).
    /// </summary>
    public static bool AllowThisApp(out string error)
    {
        error = "";
        string exe = Environment.ProcessPath ?? "";
        if (exe == "" || exe.Contains('"')) { error = "não sei onde está o Streemu.exe"; return false; }
        // /s: o cmd tira só as aspas de fora e mantém as do caminho (pasta com espaço)
        string args = $"/s /c \"netsh advfirewall firewall delete rule name=all program=\"{exe}\" & " +
                      $"netsh advfirewall firewall add rule name=\"streemu\" dir=in action=allow program=\"{exe}\" enable=yes profile=any\"";
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), args)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p == null) { error = "o Windows não abriu o comando"; return false; }
            if (!p.WaitForExit(30_000)) { error = "o comando do Firewall não terminou em 30 s"; return false; }
            if (p.ExitCode != 0) { error = $"o netsh devolveu o código {p.ExitCode}"; return false; }
            Log.Info("Firewall: Streemu.exe liberado (regras antigas apagadas, regra \"streemu\" de entrada criada).");
            return true;
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            error = "a permissão de administrador foi recusada";
            return false;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    public static void OpenAllowedAppsSettings()
    {
        try { Process.Start(new ProcessStartInfo("control.exe", "/name Microsoft.WindowsFirewall /page pageConfigureApps") { UseShellExecute = true }); }
        catch (Exception e) { Log.Info("Não consegui abrir as configurações do Firewall: " + e.Message); }
    }
}
