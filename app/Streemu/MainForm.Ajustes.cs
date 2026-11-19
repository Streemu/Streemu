using Streemu.Ui;
using Streemu.Util;

namespace Streemu;

public sealed partial class MainForm
{
    SettingsView? _settingsPage;
    /// <summary>A página Perfil: o cartão da conta entra nela depois da montagem (MainForm.Conta.cs), quando o servidor tem contas.</summary>
    TableLayoutPanel? _paginaPerfil;

    /// <summary>Reutiliza os controles e seus eventos; apenas a categoria escolhida fica na área rolável.</summary>
    Control BuildSettingsPage(Control transmitDetails)
    {
        _settingsPage = new SettingsView();
        _chooseAvatar.Text = "Escolher imagem";
        var avatarLigado = BuildAvatarOptions(); // liga o avatar e seus eventos; o PerfilLayout remonta os mesmos controles no cartão novo
        _settingsPage.AddCategory("Perfil", _paginaPerfil = SettingsSection("Perfil", PerfilLayout.Montar(_nick, _myAvatarBadge!, _chooseAvatar, _removeAvatar, _useDiscordAvatar, DiscordJoinOption())));
        avatarLigado.Dispose(); // já sem filhos: os controles foram para o cartão
        _settingsPage.AddCategory("Aparência", PaginaAparencia());
        _settingsPage.AddCategory("Assistir", PaginaAssistir());
        _settingsPage.AddCategory("Notificações", PaginaNotificacoes());
        _settingsPage.AddCategory("Conexão", PaginaConexao());
        _settingsPage.AddCategory("Sistema", PaginaSistema());
        _settingsPage.AddCategory("Diagnóstico", PaginaDiagnostico());

        // As tabelas antigas só serviram para montar os controles; os controles funcionais já foram transferidos.
        transmitDetails.Dispose();
        return _settingsPage;
    }

    void ShowSettingsAt(string section)
    {
        ShowSettings();
        _settingsPage?.SelectCategory(section);
        TickTelemetria();
    }

    static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));

    // A home e Configurações têm colunas diferentes; cada uma mede os próprios textos.
    void WrapLabelsTo(Control root)
    {
        if (root is SettingsView) { root.PerformLayout(); return; }
        int width = Math.Max(UiScale.Dp(this, 240), root.ClientSize.Width - root.Padding.Horizontal - UiScale.Dp(this, 80));
        // Campos e menus de Configurações (itens 4 e 8 do diagnóstico de UX, #138) encolhem em SettingsLayout.Constrain, que limita cada campo à largura
        // da categoria e deixa o botão ao lado descer de linha; não há uma segunda conta de largura aqui para não disputar com ela.
        foreach (var label in _wrapLabels.Where(l => !l.IsDisposed)) label.MaximumSize = new Size(Math.Max(1, width), 0);
    }

    static TableLayoutPanel SettingsSection(string title, params Control[] rows)
    {
        var table = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.Controls.Add(new Label { Text = title, AutoSize = true, Font = UiTypography.Create(UiTypography.PageTitle, FontStyle.Bold), Margin = new Padding(0, 0, 0, 26) }, 0, 0);
        foreach (var row in rows)
        {
            row.Margin = new Padding(0, 0, 0, 14);
            row.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            table.Controls.Add(row, 0, table.Controls.Count);
        }
        return table;
    }


    static Control Disclosure(string title, params Control[] controls)
    {
        return new SettingsDisclosure(title, controls);
    }

    Control LogToggle()
    {
        _log.Dock = DockStyle.None;
        _log.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _log.Height = UiScale.Dp(this, 170);
        return Disclosure("Log ao vivo", _log);
    }
}
