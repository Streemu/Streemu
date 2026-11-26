using Streemu.Net;
using Streemu.Telemetria;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// "Sobre o diagnóstico": quando mostrar o aviso (a faixa de MainForm.FaixaDiagnostico.cs, na primeira vez e quando o texto muda) e a página completa em Ajustes.
/// O texto vem de <see cref="TextoDiagnostico"/>. O aviso não pega o foco do jogo: só aparece com a janela do Streemu visível.
/// </summary>
public sealed partial class MainForm
{
    readonly Button _teleSobre = new() { Text = "Sobre o diagnóstico", AutoSize = true };
    bool _avisoDiagnosticoPendente;
    /// <summary>Teste (STREEMU_TEST_AVISO_DIAGNOSTICO=1): a faixa aparece mesmo sem conta nem grupo, para a foto da inspeção (spec 018, N1).</summary>
    bool _avisoDeTeste;
    /// <summary>A linha da IA em Ajustes > Diagnóstico (spec 018, D31): o "Saiba mais" abre a política, na parte que explica o uso de IA.</summary>
    readonly LinkLabel _teleIaLinha = new()
    {
        Text = TextoDiagnostico.LinhaDaIa + " Saiba mais", AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 0, 0, 14),
        LinkArea = new LinkArea((TextoDiagnostico.LinhaDaIa + " ").Length, "Saiba mais".Length), LinkBehavior = LinkBehavior.HoverUnderline,
        UseCompatibleTextRendering = false, ForeColor = SystemColors.GrayText,
    };

    void IniciarSobreDiagnostico()
    {
        _teleSobre.Click += (_, _) => MostrarSobreDiagnostico();
        _teleIaLinha.LinkClicked += (_, _) => Conta.AbrirNavegador(TextoDiagnostico.UrlPolitica);
        CriarFaixaDiagnostico();
        // quem nunca viu esta versão do texto: o aviso aparece quando a janela do Streemu estiver visível (não durante um teste automático)
        // teste: STREEMU_TEST_AVISO_DIAGNOSTICO=1 mostra a faixa mesmo em teste automático, para fotografar (MainForm.BarraTeste.cs)
        _avisoDeTeste = Automated && Environment.GetEnvironmentVariable("STREEMU_TEST_AVISO_DIAGNOSTICO") == "1";
        _avisoDiagnosticoPendente = (!Automated || _avisoDeTeste) && TextoDiagnostico.AvisoPendente(_state);
    }

    /// <summary>Chamado a cada segundo pelo timer da telemetria: mostra o aviso pendente quando a janela está à vista e a pessoa já está num grupo.</summary>
    void TalvezMostrarAvisoDiagnostico()
    {
        if (!_avisoDiagnosticoPendente || !Visible || WindowState == FormWindowState.Minimized || (!PodeMostrarFaixaDiagnostico && !_avisoDeTeste)) return;
        _avisoDiagnosticoPendente = false;
        MostrarFaixaDiagnostico();
    }

    void MarcarAvisoDiagnosticoVisto()
    {
        TextoDiagnostico.MarcarVisto(_state);
        if (_tele != null) { _tele.Aguardando = false; _tele.AguardandoEssencial = false; _tele.Agora(); }
        if (!Automated) _state.Save();
    }

    void MostrarSobreDiagnostico()
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
        f.Show(this);
        box.SelectionStart = 0; box.SelectionLength = 0;
    }
}
