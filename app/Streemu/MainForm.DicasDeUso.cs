using Streemu.Util;

namespace Streemu;

/// <summary>
/// Dicas de uso discretas da interface (relato de 2026-11-24: ninguém entendia o que a seta da barra lateral fazia). Cada dica aparece até a pessoa
/// usar o recurso uma vez, e "Mostrar dicas de uso" em Ajustes > Aparência desliga todas.
/// </summary>
public sealed partial class MainForm
{
    readonly CheckBox _dicasDeUso = new Ui.ToggleSwitch { Text = "Mostrar dicas de uso", AutoSize = true };

    void IniciarDicasDeUso()
    {
        _dicasDeUso.Checked = _state.DicasDeUso;
        _dicasDeUso.CheckedChanged += (_, _) => SetDicasDeUso(_dicasDeUso.Checked);
        AtualizarDicaDaBarra();
    }

    void SetDicasDeUso(bool ligadas)
    {
        _state.DicasDeUso = ligadas;
        if (!Automated) _state.Save();
        AtualizarDicaDaBarra();
    }

    /// <summary>A dica do botão de recolher vale até o primeiro uso e só com as dicas ligadas. Ex.: <c>AtualizarDicaDaBarra()</c> depois de mudar o estado.</summary>
    void AtualizarDicaDaBarra()
    {
        bool dica = _state.DicasDeUso && !_state.DicaBarraVista;
        _side.DicaToggle = dica;
        if (_flyout != null) _flyout.Panel.DicaToggle = dica;
    }

    /// <summary>A pessoa usou o botão de recolher: a dica some para sempre.</summary>
    void DispensarDicaDaBarra()
    {
        if (_state.DicaBarraVista) return;
        _state.DicaBarraVista = true;
        if (!Automated) _state.Save();
        AtualizarDicaDaBarra();
    }
}
