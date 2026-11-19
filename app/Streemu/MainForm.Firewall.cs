using Streemu.Util;

namespace Streemu;

/// <summary>
/// Firewall do Windows: conferência na abertura, botão "Firewall do Windows..." e o "Liberar agora" (UAC).
/// Fica fora do MainForm.cs para dar menos conflito (ver AGENTS.md).
/// </summary>
public sealed partial class MainForm
{
    bool _firewallAsked;

    /// <summary>
    /// Na abertura. O Windows cria as regras de BLOQUEIO assim que mostra o aviso "bloqueou alguns recursos" (exe novo ou
    /// pasta nova, como a do instalador), e só troca por "permite" quando a pessoa clica em Permitir. A conferência rodava
    /// ~1 s depois de abrir, com o aviso ainda na tela, e acusava bloqueio à toa (07/11 23:57, primeira abertura instalada).
    /// Por isso confere de novo depois de 30 s antes de avisar.
    /// </summary>
    void CheckFirewallAtStartup()
    {
        _fw = FirewallCheck.Inspect();
        Log.Info("Firewall: " + _fw.Summary);
        if (!_fw.BlockingNow) return;
        Log.Info("Firewall: há regra de bloqueio agora; confiro de novo em 30 s (o aviso do Windows pode estar aberto).");
        _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => Ui(() =>
        {
            _fw = FirewallCheck.Inspect();
            Log.Info("Firewall (30 s depois): " + _fw.Summary);
            if (!_fw.BlockingNow) return;
            ShowFirewallBlocked();
            // escondido na bandeja (ex.: abriu com o Windows) não abre pergunta por cima do jogo: fica o balão
            if (Visible && WindowState != FormWindowState.Minimized && !_firewallAsked)
            {
                _firewallAsked = true;
                if (MessageBox.Show(this,
                        $"O Firewall do Windows está bloqueando o {UiBrand.Name} nesta rede, e os amigos podem não conseguir chegar até você.\n\n" +
                        "Liberar agora? O Windows vai pedir permissão de administrador uma vez.",
                        "Firewall do Windows", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    _ = AllowFirewallAsync();
            }
        }));
    }

    void ShowFirewallBlocked()
    {
        string texto = $"O Firewall do Windows está BLOQUEANDO o {UiBrand.Name} nesta rede (o aviso do Windows foi cancelado ou fechado). " +
                       "Abra Configurações > Conexão, clique em \"Firewall do Windows...\" e em \"Sim\" para liberar, senão o amigo pode não chegar até você.";
        Log.Info(texto);
        Notify(texto);
    }

    /// <summary>Botão "Firewall do Windows...".</summary>
    void OnFirewallButton()
    {
        _fw = FirewallCheck.Inspect();
        Log.Info("Firewall: " + _fw.Summary);
        if (_fw.AllowedNow && !_fw.BlockingNow)
        {
            if (MessageBox.Show(this, _fw.Summary + "\n\nEstá liberado: os amigos conseguem chegar até você.\n\nAbrir as configurações do Firewall mesmo assim?",
                    "Firewall do Windows", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                FirewallCheck.OpenAllowedAppsSettings();
            return;
        }
        var answer = MessageBox.Show(this,
            _fw.Summary + $"\n\nLiberar o {UiBrand.Name} no Firewall agora? O Windows vai pedir permissão de administrador uma vez.\n\n" +
            "Sim = liberar sozinho.  Não = abrir as configurações do Firewall para fazer à mão.",
            "Firewall do Windows", MessageBoxButtons.YesNoCancel, _fw.BlockingNow ? MessageBoxIcon.Warning : MessageBoxIcon.Question);
        if (answer == DialogResult.Yes) _ = AllowFirewallAsync();
        else if (answer == DialogResult.No) OpenFirewallSettingsByHand();
    }

    async Task AllowFirewallAsync()
    {
        string error = "";
        bool ok = await Task.Run(() => FirewallCheck.AllowThisApp(out error));
        _fw = FirewallCheck.Inspect();
        Log.Info("Firewall: " + _fw.Summary);
        if (ok && !_fw.BlockingNow)
        {
            Notify("Firewall do Windows liberado: os amigos conseguem chegar até você.");
            return;
        }
        Log.Info("Firewall: não liberou (" + (ok ? "ainda há regra de bloqueio" : error) + ").");
        if (MessageBox.Show(this,
                $"Não consegui liberar sozinho: {(ok ? "ainda há uma regra de bloqueio (talvez de outro programa de segurança)" : error)}.\n\n" +
                "Abrir as configurações do Firewall para liberar à mão?",
                "Firewall do Windows", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            OpenFirewallSettingsByHand();
    }

    void OpenFirewallSettingsByHand()
    {
        MessageBox.Show(this,
            $"Na janela que vai abrir, clique em \"Alterar configurações\", ache o {UiBrand.Name}" +
            "(ou \"Permitir outro aplicativo\" e escolha o Streemu.exe) e marque Privada e Pública.",
            "Firewall do Windows", MessageBoxButtons.OK, MessageBoxIcon.Information);
        FirewallCheck.OpenAllowedAppsSettings();
    }
}
