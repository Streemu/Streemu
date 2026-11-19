using System.Diagnostics;
using Streemu.Util;

namespace Streemu;

/// <summary>Convite para a pesquisa com os usuários (ver <see cref="ConvitePesquisa"/>): janela que não rouba o foco, só com a janela do Streemu visível.</summary>
public sealed partial class MainForm
{
    Form? _convitePesquisa;

    /// <summary>Chamado a cada segundo pelo timer da telemetria.</summary>
    void TalvezMostrarConvitePesquisa()
    {
        if (Automated) return;
        if (_state.PrimeiroUsoEm == default) { _state.PrimeiroUsoEm = DateTime.Now; _state.Save(); }
        if (_convitePesquisa != null || _faixaDiagnostico.Visible || !Visible || WindowState == FormWindowState.Minimized) return;
        if (!ConvitePesquisa.Deve(_state, DateTime.Now)) return;
        MostrarConvitePesquisa();
    }

    void MostrarConvitePesquisa()
    {
        var f = new Form
        {
            Text = ConvitePesquisa.Titulo, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16), BackColor = Theme.Surface, ForeColor = Theme.Text,
        };
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        layout.Controls.Add(new Label { Text = ConvitePesquisa.Texto, AutoSize = true, MaximumSize = new Size(UiScale.Dp(this, 420), 0), Margin = new Padding(0, 0, 0, 12) });
        var botoes = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = true };
        var responder = new Button { Text = "Responder", AutoSize = true };
        var agoraNao = new Button { Text = "Agora não", AutoSize = true };
        var nunca = new Button { Text = "Não perguntar mais", AutoSize = true };
        Theme.StyleButton(responder, true); Theme.StyleButton(agoraNao, false); Theme.StyleButton(nunca, false);
        botoes.Controls.AddRange(new Control[] { responder, agoraNao, nunca });
        layout.Controls.Add(botoes);
        f.Controls.Add(layout);
        f.AcceptButton = responder;
        bool decidiu = false;
        responder.Click += (_, _) =>
        {
            decidiu = true; _state.PesquisaEstado = ConvitePesquisa.Respondeu;
            try { Process.Start(new ProcessStartInfo(ConvitePesquisa.Link) { UseShellExecute = true }); } catch (Exception e) { Log.Info("Não abri o link da pesquisa: " + e.Message); }
            f.Close();
        };
        nunca.Click += (_, _) => { decidiu = true; _state.PesquisaEstado = ConvitePesquisa.NaoPerguntar; f.Close(); };
        agoraNao.Click += (_, _) => f.Close();
        // fechar no X ou em "Agora não" adia; só "Responder" e "Não perguntar mais" encerram o convite de vez
        f.FormClosed += (_, _) =>
        {
            if (!decidiu) _state.PesquisaAdiadaAte = DateTime.Now + ConvitePesquisa.Adiamento;
            _state.Save(); _convitePesquisa = null; f.Dispose();
        };
        _convitePesquisa = f;
        f.Show(this);
    }
}
