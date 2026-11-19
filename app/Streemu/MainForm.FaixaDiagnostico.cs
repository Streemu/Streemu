using Streemu.Telemetria;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// A faixa do diagnóstico, no lugar da janela que abria na primeira abertura, antes de a pessoa sequer entrar (assustava e tomava o lugar da Mumu).
/// Aparece no topo só depois de a pessoa entrar e estar num grupo, com os mesmos três caminhos da spec 018 (D6): Entendi, Desligar e Saiba mais.
/// Fica até ser fechada por um botão; até lá nada do diagnóstico sobe (<see cref="Envio.Aguardando"/>).
/// </summary>
public sealed partial class MainForm
{
    readonly FlowLayoutPanel _faixaDiagnostico = new() { Dock = DockStyle.Top, AutoSize = true, Visible = false, WrapContents = true, Padding = new Padding(12, 6, 12, 6) };
    readonly Label _faixaDiagnosticoTexto = new() { AutoSize = true, Padding = new Padding(0, 7, 10, 0) };
    readonly Button _faixaDiagnosticoDesligar = new() { Text = "Desligar", AutoSize = true };
    readonly ToolTip _faixaDiagnosticoDica = new() { AutoPopDelay = 30000 };

    /// <summary>Sem conta ou sem grupo a pessoa ainda está chegando: o aviso espera ela ter onde usar o app.</summary>
    bool PodeMostrarFaixaDiagnostico => _conta is { TemConta: true } conta && conta.Grupos.Count > 0 && _entrada is not { Visible: true };

    /// <summary>Monta a faixa escondida. Fica no topo como a de atualização (a ordem do encaixe é refeita em MainForm.Janela.cs).</summary>
    void CriarFaixaDiagnostico()
    {
        var entendi = new Button { Text = "Entendi", AutoSize = true };
        var saibaMais = new Button { Text = "Saiba mais", AutoSize = true };
        Theme.StyleButton(entendi, true); Theme.StyleButton(_faixaDiagnosticoDesligar, false); Theme.StyleButton(saibaMais, false);
        entendi.Click += (_, _) => FecharFaixaDiagnostico();
        _faixaDiagnosticoDesligar.Click += (_, _) => { _teleEnviar.Checked = false; FecharFaixaDiagnostico(); }; // o mesmo caminho do interruptor de Ajustes
        saibaMais.Click += (_, _) => MostrarSobreDiagnostico();
        _faixaDiagnostico.BackColor = Theme.SurfaceRaised;
        _faixaDiagnosticoTexto.ForeColor = Theme.Text;
        _faixaDiagnostico.Controls.AddRange(new Control[] { _faixaDiagnosticoTexto, entendi, _faixaDiagnosticoDesligar, saibaMais });
        Controls.Add(_faixaDiagnostico);
        _faixaDiagnostico.SendToBack();
    }

    /// <summary>O texto curto na faixa; o aviso inteiro de antes fica na dica do texto, e a página completa no "Saiba mais".</summary>
    void MostrarFaixaDiagnostico()
    {
        bool tinhaDesligado = _state.TelemetriaDesligada;
        _faixaDiagnosticoTexto.Text = TextoDiagnostico.Faixa(tinhaDesligado);
        _faixaDiagnosticoDica.SetToolTip(_faixaDiagnosticoTexto, TextoDiagnostico.Aviso(tinhaDesligado));
        _faixaDiagnosticoDesligar.Visible = !tinhaDesligado;
        _faixaDiagnostico.Visible = true;
        CorrigirOrdemDoEncaixe();
        AjustarAlturaMinima();
    }

    void FecharFaixaDiagnostico()
    {
        _faixaDiagnostico.Visible = false;
        MarcarAvisoDiagnosticoVisto();
        AjustarAlturaMinima();
    }
}
