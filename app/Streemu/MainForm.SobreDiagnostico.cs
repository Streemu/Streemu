using Streemu.Telemetria;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// "Sobre o diagnóstico": o aviso que fica na tela até a pessoa fechar (primeira abertura e quando o texto muda) e a página completa em Ajustes.
/// O texto vem de <see cref="TextoDiagnostico"/>. O aviso não é TopMost nem pega o foco do jogo: só aparece com a janela do Streemu visível.
/// </summary>
public sealed partial class MainForm
{
    readonly Button _teleSobre = new() { Text = "Sobre o diagnóstico", AutoSize = true };
    bool _avisoDiagnosticoPendente;
    Form? _avisoDiagnostico;

    void IniciarSobreDiagnostico()
    {
        _teleSobre.Click += (_, _) => MostrarSobreDiagnostico();
        // quem nunca viu esta versão do texto: o aviso aparece quando a janela do Streemu estiver visível (não durante um teste automático)
        _avisoDiagnosticoPendente = !Automated && _state.TelemetriaAvisoVersao < TextoDiagnostico.VersaoDoAviso;
    }

    /// <summary>Chamado a cada segundo pelo timer da telemetria: mostra o aviso pendente quando a janela está à vista.</summary>
    void TalvezMostrarAvisoDiagnostico()
    {
        if (!_avisoDiagnosticoPendente || !Visible || WindowState == FormWindowState.Minimized || _avisoDiagnostico != null) return;
        _avisoDiagnosticoPendente = false;
        MostrarAvisoDiagnostico();
    }

    void MarcarAvisoDiagnosticoVisto()
    {
        _state.TelemetriaAvisoVisto = true;
        _state.TelemetriaAvisoVersao = TextoDiagnostico.VersaoDoAviso;
        if (_tele != null) { _tele.Aguardando = false; _tele.Agora(); }
        if (!Automated) _state.Save();
    }

    void MostrarAvisoDiagnostico()
    {
        bool tinhaDesligado = _state.TelemetriaDesligada;
        var f = new Form
        {
            Text = TextoDiagnostico.TituloDoAviso, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16), BackColor = Theme.Surface, ForeColor = Theme.Text,
        };
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        layout.Controls.Add(new Label { Text = TextoDiagnostico.Aviso(tinhaDesligado), AutoSize = true, MaximumSize = new Size(UiScale.Dp(this, 460), 0), Margin = new Padding(0, 0, 0, 12) });
        var botoes = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = true };
        var entendi = new Button { Text = "Entendi", AutoSize = true };
        var desligar = new Button { Text = "Desligar diagnóstico", AutoSize = true, Visible = !tinhaDesligado };
        var lerMais = new Button { Text = "Ler mais", AutoSize = true };
        Theme.StyleButton(entendi, true); Theme.StyleButton(desligar, false); Theme.StyleButton(lerMais, false);
        botoes.Controls.AddRange(new Control[] { entendi, desligar, lerMais });
        layout.Controls.Add(botoes);
        f.Controls.Add(layout);
        f.AcceptButton = entendi;
        entendi.Click += (_, _) => f.Close();
        desligar.Click += (_, _) => { _teleEnviar.Checked = false; f.Close(); }; // dispara o mesmo caminho do interruptor de Ajustes
        lerMais.Click += (_, _) => MostrarSobreDiagnostico(f);
        // fechar de qualquer jeito (inclusive no X) conta como lido: o aviso fica até a pessoa fechar, e só aparece de novo quando o texto mudar
        f.FormClosed += (_, _) => { MarcarAvisoDiagnosticoVisto(); _avisoDiagnostico = null; f.Dispose(); };
        _avisoDiagnostico = f;
        f.Show(this);
    }

    void MostrarSobreDiagnostico(Form? dono = null)
    {
        var f = new Form
        {
            Text = TextoDiagnostico.TituloDoAviso, FormBorderStyle = FormBorderStyle.Sizable, StartPosition = FormStartPosition.CenterParent,
            ShowInTaskbar = false, Padding = new Padding(12), BackColor = Theme.Surface, ForeColor = Theme.Text,
            ClientSize = new Size(UiScale.Dp(this, 560), UiScale.Dp(this, 520)), MinimumSize = new Size(UiScale.Dp(this, 380), UiScale.Dp(this, 300)),
        };
        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, WordWrap = true,
            Text = TextoDiagnostico.Completo.Replace("\r\n", "\n").Replace("\n", "\r\n"), BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.None,
            TabStop = false,
        };
        var fechar = new Button { Text = "Fechar", AutoSize = true, Dock = DockStyle.Bottom };
        Theme.StyleButton(fechar, true);
        f.Controls.Add(box);
        f.Controls.Add(fechar);
        f.AcceptButton = fechar; f.CancelButton = fechar;
        fechar.Click += (_, _) => f.Close();
        f.FormClosed += (_, _) => f.Dispose();
        f.Show(dono ?? this);
        box.SelectionStart = 0; box.SelectionLength = 0;
    }
}
