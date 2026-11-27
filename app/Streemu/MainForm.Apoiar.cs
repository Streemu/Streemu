using Streemu.Ui;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// A tela "Apoiar" na janela principal (plano 035): o item da barra lateral (sempre presente, logo abaixo de Novidades), a vista <c>_apoiarView</c> com o seu host
/// e o clique que a abre. A tela em si é <c>Ui/ApoiarTela.cs</c>: o app só leva ao site (<see cref="UiBrand.UrlApoio"/>), sem valor, Pix nem cartão (regra 11 do AGENTS.md).
/// </summary>
public sealed partial class MainForm
{
    readonly BufferedPanel _apoiarHost = new() { Dock = DockStyle.Fill, Visible = false };
    /// <summary>A tela Apoiar está aberta (vista da janela, como _novidadesView).</summary>
    bool _apoiarView;
    ApoiarTela? _apoiarTela;

    /// <summary>Chamado no fim do BuildShell, depois da Novidades: o host nasce depois do Theme.Apply da janela, como o dela.</summary>
    void AdicionarApoiar()
    {
        if (_apoiarTela != null) return;
        _apoiarTela = new ApoiarTela { Dock = DockStyle.Fill };
        _apoiarHost.BackColor = Theme.Surface;
        _apoiarHost.Controls.Add(_apoiarTela);
        Controls.Add(_apoiarHost);
        CorrigirOrdemDoEncaixe(); // o host entrou depois da barra lateral, e o encaixe do WinForms vai do último para o primeiro
        _side.ApoiarClicked += MostrarApoiar;
        AjustarAlturaMinima(); // um item a mais na navegação: a janela não pode ficar mais baixa que a barra
    }

    /// <summary>O item "Apoiar" da barra lateral: abre a tela no lugar do Início (mesmo jeito do Grupos e do Novidades).</summary>
    void MostrarApoiar()
    {
        HideFlyout();
        if (_preparationBusy) return; // Apoiar não pede conta: o apoio é anônimo, como o do site (plano 035, D1)
        _preparationView = _stageView = _settingsView = _filmeView = _arquivosView = _gruposView = _novidadesView = _mumuView = false;
        _apoiarView = true;
        ApplyView();
    }
}
