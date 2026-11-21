using Streemu.Net;
using Streemu.Ui;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// O nome da conta no app (spec 027, seção 6.1). O nome é da CONTA e igual em todos os aparelhos: quem manda é o servidor (<see cref="Conta.Nome"/>); o campo
/// "Nome da conta" do Perfil só o mostra e pede a troca (<see cref="Conta.MudarNomeAsync"/>) ao sair do campo ou no Enter. O nome do Discord não vira nome
/// (só o avatar dele continua). O apelido salvo de antes (<see cref="AppState.Nick"/>) só serve de nome até o servidor dar um e de sugestão à conta sem nome:
/// ele vai ao servidor no renovar enquanto a conta não tem nome, e o servidor o adota.
/// </summary>
public sealed partial class MainForm
{
    string? _apelidoAntigo;      // o apelido do estado.json de antes da spec 027 (migração); nunca é gravado de novo
    string? _nomeDeTeste;        // só nos cenários automáticos sem conta (MainForm.Grupo.cs, RunRoomTestAsync)
    string? _nomeAplicado;       // o último nome que a tela e a sala receberam
    bool _nomeSalvando, _escolhendoNome;

    /// <summary>Chamado no fim do construtor (MainForm.cs).</summary>
    void InitNick()
    {
        _apelidoAntigo = ApelidoDeAntes(_state);
        _nomeAplicado = Nick;
        _nick.Text = _nomeAplicado;
        _nick.Leave += (_, _) => _ = SalvarNomeDaTelaAsync();
        _nick.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true; // sem o "plim" do Windows
            _ = SalvarNomeDaTelaAsync();
        };
    }

    /// <summary>
    /// O apelido que a pessoa usava antes de o nome ser da conta: o digitado; sem digitado, o nome do Discord (era o que o app usava). Nunca o usuário do Windows
    /// (o estado antigo o gravava quando não havia Discord, e ele não é um nome que a pessoa escolheu).
    /// </summary>
    static string? ApelidoDeAntes(AppState s)
    {
        string? salvo = ParaNome(s.Nick);
        bool digitado = salvo != null && (s.NickManual ?? !string.Equals(salvo, Environment.UserName, StringComparison.OrdinalIgnoreCase));
        return digitado ? salvo : ParaNome(s.DiscordNome);
    }

    static string? ParaNome(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length <= 24 ? s.Trim() : s.Trim()[..24];

    /// <summary>Só os cenários automáticos usam: dá nome a quem não tem conta (com conta o nome é o dela).</summary>
    void DefinirNomeDeTeste(string nome) { _nomeDeTeste = nome; AplicarNomeDaConta(); }

    /// <summary>O nome mudou (resposta do servidor, minha troca, ou a sala difundindo outro nome para minha conta): atualiza a barra, o Perfil, a sala e a telemetria.</summary>
    void AplicarNomeDaConta()
    {
        string nome = Nick;
        if (nome == _nomeAplicado) return;
        _nomeAplicado = nome;
        if (!_nick.Focused) _nick.Text = nome; // quem está digitando não perde o texto
        _myAvatarBadge?.Invalidate(); // sem imagem, o avatar mostra a inicial do nome
        _room?.SetNick(nome);
        AtualizarQuem(); // MainForm.Telemetria.cs
        RefreshSide();
        RefreshMembers();
    }

    /// <summary>A sala difundiu os membros: se o meu nome ali é outro e a conta já tem nome, a troca foi feita em outro aparelho.</summary>
    void AdotarNomeDaSala()
    {
        if (_conta is not { TemConta: true } conta || _room is not { Connected: true } room || room.Find(room.MyId)?.Nick is not { Length: > 0 } nome) return;
        conta.AdotarNomeDaSala(nome); // dispara o Changed da conta, que chama AplicarNomeDaConta
    }

    /// <summary>Sair do campo ou Enter: troca o nome da conta no servidor. Erro ou sem rede: o texto volta ao nome de agora e o motivo vai para o aviso da tela.</summary>
    async Task SalvarNomeDaTelaAsync()
    {
        if (_nomeSalvando) return;
        string novo = _nick.Text.Trim();
        var conta = _conta;
        if (novo == Nick) { _nick.Text = Nick; return; }
        if (conta is not { TemConta: true }) { _nick.Text = Nick; Notify("Entre na sua conta para mudar o nome."); return; }
        if (novo.Length == 0) { _nick.Text = Nick; return; }
        _nomeSalvando = true;
        try
        {
            var r = await conta.MudarNomeAsync(novo);
            if (IsDisposed) return;
            Notify(r.Ok ? $"Seu nome agora é {conta.Nome} em todos os aparelhos." : r.Texto ?? "Não consegui mudar o nome. Tente de novo.");
            _nick.Text = Nick; // o servidor pode ter ajeitado espaços; no erro, volta ao nome de antes
            AplicarNomeDaConta();
        }
        finally { _nomeSalvando = false; }
    }

    /// <summary>O renovar trouxe um nome para uma conta que não tinha: o servidor adotou o apelido de antes (spec 027, seção 5). Avisa uma vez (não há link "Mudar": o Perfil é onde se muda).</summary>
    void AvisarNomeAdotado(string nome) => Notify($"Seu nome no Streemu agora é {nome} em todos os aparelhos. Para mudar, vá em Configurações > Perfil.");

    /// <summary>Conta que continua sem nome depois do renovar (só entrou pelo celular, por exemplo): uma caixinha "Escolha seu nome", uma vez só.</summary>
    void ConferirEscolhaDeNome()
    {
        if (_conta is not { TemConta: true, NomeConhecido: true, Nome: null } conta || _state.EscolhaDeNomeMostrada || _escolhendoNome || Automated) return;
        if (!Visible || WindowState == FormWindowState.Minimized) return; // aberto na bandeja: espera a janela aparecer (o renovar repete a cada 10 min)
        _escolhendoNome = true;
        _state.EscolhaDeNomeMostrada = true;
        SalvarConta();
        BeginInvoke(async () =>
        {
            try { await PedirNomeDaContaAsync(conta); }
            finally { _escolhendoNome = false; }
        });
    }

    async Task PedirNomeDaContaAsync(Conta conta)
    {
        using var f = new Form
        {
            Text = "Escolha seu nome", FormBorderStyle = FormBorderStyle.FixedToolWindow, StartPosition = FormStartPosition.CenterParent,
            ShowInTaskbar = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12),
            BackColor = Theme.Surface, ForeColor = Theme.Text,
        };
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        layout.Controls.Add(new Label { Text = "Como seus amigos devem ver você?\nÉ o nome da sua conta, igual em todos os aparelhos.", AutoSize = true });
        var box = new TextBox { Width = UiScale.Dp(this, 300), MaxLength = ApelidosLocais.Limite, Text = _apelidoAntigo ?? "" };
        var ok = new Button { Text = "Salvar", AutoSize = true, DialogResult = DialogResult.OK };
        Theme.StyleButton(ok, true);
        layout.Controls.Add(new CampoArredondado(box, 300));
        layout.Controls.Add(ok);
        f.Controls.Add(layout);
        f.AcceptButton = ok;
        f.Shown += (_, _) => { box.Focus(); box.SelectAll(); };
        if (f.ShowDialog(this) != DialogResult.OK) return;
        var r = await conta.MudarNomeAsync(box.Text);
        if (IsDisposed) return;
        if (r.Ok) { Notify($"Seu nome agora é {conta.Nome} em todos os aparelhos."); AplicarNomeDaConta(); return; }
        if (r.Erro == "sem_rede") _state.EscolhaDeNomeMostrada = false; // não chegou ao servidor: pergunta de novo no próximo renovar
        Notify(r.Texto ?? "Não consegui salvar o nome. Tente de novo em Configurações > Perfil.");
    }
}
