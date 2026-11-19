using Microsoft.Win32;

namespace Streemu.Util;

/// <summary>
/// "Iniciar com o Windows": um valor "Streemu" em HKCU\...\Run (sem administrador) que abre o app escondido na bandeja
/// (<see cref="Arg"/>). O instalador grava o mesmo valor quando a caixa dele fica marcada, então app e instalador
/// enxergam a mesma escolha.
/// </summary>
public static class StartWithWindows
{
    /// <summary>Argumento da abertura pelo Windows: abre direto na bandeja, sem janela por cima do que a pessoa estiver fazendo.</summary>
    public const string Arg = "--inicio";

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // onde o Gerenciador de Tarefas guarda "Desabilitado" na aba Aplicativos de inicialização (o valor do Run continua lá)
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string ValueName = "Streemu";

    static string Exe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Streemu.exe");
    static string Command => $"\"{Exe}\" {Arg}";

    /// <summary>Ligado = o valor existe no Run e não foi desabilitado no Gerenciador de Tarefas.</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey);
                if (run?.GetValue(ValueName) is not string) return false;
                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
                // primeiro byte ímpar (03) = desabilitado pela pessoa no Gerenciador de Tarefas
                return approved?.GetValue(ValueName) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
            }
            catch { return false; }
        }
    }

    public static bool Set(bool enabled)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) run.SetValue(ValueName, Command);
            else run.DeleteValue(ValueName, throwOnMissingValue: false);
            // sem isso, quem desabilitou no Gerenciador de Tarefas marcava a caixa e nada acontecia
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
            Log.Info(enabled ? $"Iniciar com o Windows: ligado ({Command})." : "Iniciar com o Windows: desligado.");
            return true;
        }
        catch (Exception e)
        {
            Log.Info("Não consegui mudar o \"Iniciar com o Windows\": " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// Ligado mas apontando para outro .exe (a pessoa mudou a pasta, ou abriu o zip de outra versão): aponta para este.
    /// Mesma ideia do link streemu://: o Windows deve abrir o Streemu que a pessoa usou por último.
    /// </summary>
    public static void Refresh()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue(ValueName) is not string cur || cur == Command) return;
            run.SetValue(ValueName, Command);
            Log.Info($"Iniciar com o Windows: atualizado para este Streemu.exe (antes: {cur}).");
        }
        catch (Exception e) { Log.Info("Não consegui conferir o \"Iniciar com o Windows\": " + e.Message); }
    }
}
