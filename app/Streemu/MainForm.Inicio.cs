using Streemu.Util;

namespace Streemu;

/// <summary>
/// "Iniciar com o Windows" (caixa no rodapé e item na bandeja) e o ícone da janela.
/// Fica fora do MainForm.cs para dar menos conflito (ver AGENTS.md).
/// </summary>
public sealed partial class MainForm
{
    readonly CheckBox _startWithWindows = new Ui.ToggleSwitch { Text = "Iniciar com o Windows", AutoSize = true };

    /// <summary>Aberto pelo Windows na entrada (valor do Run): começa escondido na bandeja.</summary>
    static bool OpenedAtLogon => LaunchArgs.Any(a => a.Equals(StartWithWindows.Arg, StringComparison.OrdinalIgnoreCase));

    /// <summary>Chamado no começo do InitGroup.</summary>
    void InitStartup()
    {
        // o ícone embutido tem todos os tamanhos; o ExtractAssociatedIcon do exe só devolve 32 px, que a barra de título reduz mal
        try { Icon = IconeJanela(AtividadeIcone.Ocioso); }
        catch { try { if (Environment.ProcessPath is { } exe) Icon = Icon.ExtractAssociatedIcon(exe); } catch { } }
        // teste automático nunca mexe no registro do usuário (AGENTS.md, regra 3)
        if (Automated) { _startWithWindows.Visible = false; return; }
        if (OpenedAtLogon) { _startHidden = true; Log.Info("Aberto pelo Windows na entrada: fica na bandeja."); }
        // build local (canal dev) não toma o lugar do Streemu instalado na inicialização do Windows
        string canal = BuildInfo.Canal; // variável: com a constante direto o compilador avisa "código inacessível" no build dev
        if (canal != "dev") StartWithWindows.Refresh();
        _startWithWindows.Checked = StartWithWindows.IsEnabled;
        _startWithWindows.CheckedChanged += (_, _) => SetStartWithWindows(_startWithWindows.Checked);
    }

    void SetStartWithWindows(bool on)
    {
        if (StartWithWindows.IsEnabled == on) return;
        if (!StartWithWindows.Set(on))
            Notify("Não consegui mudar o \"Iniciar com o Windows\". Veja o log (Abrir logs) e tente de novo.");
        bool now = StartWithWindows.IsEnabled;
        if (_startWithWindows.Checked != now) _startWithWindows.Checked = now;
    }

    void AddStartupMenuItem(ContextMenuStrip menu)
    {
        menu.Items.Add(new ToolStripMenuItem("Iniciar com o Windows", null, (_, _) => SetStartWithWindows(!StartWithWindows.IsEnabled))
        {
            Checked = StartWithWindows.IsEnabled,
        });
    }
}
