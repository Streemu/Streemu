using Streemu.Net;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>Uma linha da lista de grupos. <see cref="Papel"/> nulo = ainda não carregou os detalhes ("dono" ou "membro").</summary>
public sealed record GrupoLinha(string Id, string Nome, string? Papel, IReadOnlyList<MembroInfo>? Membros);

/// <summary>
/// Conteúdo da tela "Grupos" da barra lateral (plano 019, etapa 8; ela era uma categoria das Configurações): lista dos grupos (nome, membros, seu papel), "Criar grupo", "Entrar com código" e,
/// por grupo, "Copiar convite", "Sair" e, para o dono, "Renomear", "Trocar convite" e "Remover" por membro. SÓ TELA: não fala com o servidor; quem decide é
/// <c>MainForm.Grupos.cs</c>, que liga os eventos. Visual provisório no estilo das outras categorias (a estilização final fica para depois: pode trocar o
/// layout à vontade, desde que os eventos e os métodos públicos continuem).
/// </summary>
public sealed class GruposView : FlowLayoutPanel
{
    public const string TextoVazio = "Você ainda não está em nenhum grupo. Crie um ou entre com o convite de um amigo.";
    /// <summary>Confirmação de "Sair" (texto do plano 019).</summary>
    public const string ConfirmaSair = "Você deixa de ver as pessoas deste grupo. Se for o último, o grupo é apagado.";

    readonly Label _aviso = new() { AutoSize = true, MaximumSize = new Size(620, 0), Margin = new Padding(3, 4, 3, 8), Visible = false };
    readonly FlowLayoutPanel _lista = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
    readonly TextBox _nomeNovo = new() { Width = 220, MaxLength = 40, PlaceholderText = "Nome do grupo" };
    readonly Button _criar = new() { Text = "Criar grupo", AutoSize = true };
    readonly TextBox _codigo = new() { Width = 260, MaxLength = 80, PlaceholderText = "Código ou link do convite" };
    readonly Button _entrar = new() { Text = "Entrar com código", AutoSize = true };
    bool _ocupado;

    public event Action<string>? PedirCriar;
    public event Action<string>? PedirEntrar;
    public event Action<string>? PedirCopiarConvite;
    public event Action<string>? PedirSair;
    public event Action<string>? PedirRenomear;
    public event Action<string>? PedirTrocarConvite;
    /// <summary>(grupo, conta do membro, apelido dele).</summary>
    public event Action<string, string, string>? PedirRemover;

    public GruposView()
    {
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = Padding.Empty;
        var criar = AjusteBlocos.Cartao(AjusteBlocos.Legenda("Criar um grupo"), AjusteBlocos.Botoes(new CampoArredondado(_nomeNovo, 260), _criar));
        var entrar = AjusteBlocos.Cartao(AjusteBlocos.Legenda("Entrar com um convite"), AjusteBlocos.Botoes(new CampoArredondado(_codigo, 300), _entrar));
        criar.Margin = entrar.Margin = new Padding(0, 0, 0, 14);
        Controls.AddRange(new Control[] { _lista, _aviso, criar, entrar });
        Theme.Paint(this, new HashSet<Button> { _criar, _entrar }); // esta tela nasce depois do Theme.Apply da janela: sem isto os botões ficariam sem estilo
        _criar.Click += (_, _) => { var n = _nomeNovo.Text.Trim(); if (n.Length > 0) PedirCriar?.Invoke(n); else Aviso("Dê um nome ao grupo (até 40 letras).", true); };
        _entrar.Click += (_, _) => { var c = _codigo.Text.Trim(); if (c.Length > 0) PedirEntrar?.Invoke(c); else Aviso("Cole o código ou o link do convite.", true); };
        _nomeNovo.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _criar.PerformClick(); } };
        _codigo.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _entrar.PerformClick(); } };
        Mostrar(Array.Empty<GrupoLinha>());
    }

    public void LimparCampos() { _nomeNovo.Clear(); _codigo.Clear(); }

    /// <summary>Refaz a lista. Quem chama já ordena (a ordem de entrada).</summary>
    public void Mostrar(IReadOnlyList<GrupoLinha> grupos)
    {
        _lista.SuspendLayout();
        foreach (Control c in _lista.Controls.Cast<Control>().ToList()) { _lista.Controls.Remove(c); c.Dispose(); }
        if (grupos.Count == 0)
            _lista.Controls.Add(AjusteBlocos.Nota(TextoVazio, alinhadaAoInterruptor: false));
        foreach (var g in grupos) _lista.Controls.Add(Cartao(g));
        _lista.ResumeLayout(true);
        Theme.Paint(_lista, new HashSet<Button>()); // os cartões e botões novos pegam o tema de agora
        Travar(_ocupado);
    }

    Control Cartao(GrupoLinha g)
    {
        string papel = g.Papel == "dono" ? "você é o dono" : g.Papel == "membro" ? "você é membro" : "carregando...";
        string membros = g.Membros == null ? "" : g.Membros.Count == 1 ? "1 pessoa · " : $"{g.Membros.Count} pessoas · ";
        var botoes = new List<Control> { Botao("Copiar convite", () => PedirCopiarConvite?.Invoke(g.Id)), Botao("Sair", () => PedirSair?.Invoke(g.Id)) };
        if (g.Papel == "dono")
        {
            botoes.Add(Botao("Renomear", () => PedirRenomear?.Invoke(g.Id)));
            botoes.Add(Botao("Trocar convite", () => PedirTrocarConvite?.Invoke(g.Id)));
        }
        var linhas = new List<Control>
        {
            AjusteBlocos.Legenda(g.Nome.Length > 0 ? g.Nome : "(sem nome)"),
            AjusteBlocos.Nota(membros + papel, alinhadaAoInterruptor: false),
            AjusteBlocos.Botoes(botoes.ToArray()),
        };
        if (g.Papel == "dono" && g.Membros != null)
            foreach (var m in g.Membros.Where(m => m.Papel != "dono")) linhas.Add(LinhaDeMembro(g, m));
        var cartao = AjusteBlocos.Cartao(linhas.ToArray());
        cartao.Margin = new Padding(0, 0, 0, 14);
        return cartao;
    }

    /// <summary>Um membro (que não é o dono) com o botão de removê-lo do grupo.</summary>
    Control LinhaDeMembro(GrupoLinha g, MembroInfo m)
    {
        var nome = new Label { Text = m.Apelido.Length > 0 ? m.Apelido : "(sem apelido)", AutoSize = true, Margin = new Padding(0, 13, 12, 0) };
        return AjusteBlocos.Linha(nome, Botao("Remover", () => PedirRemover?.Invoke(g.Id, m.Id, m.Apelido)));
    }

    static Button Botao(string texto, Action aoClicar)
    {
        var botao = new Button { Text = texto, AutoSize = true, Tag = "acao" };
        botao.Click += (_, _) => aoClicar();
        return botao;
    }

    /// <summary>Texto abaixo da lista: erro (vermelho) ou confirmação.</summary>
    public void Aviso(string? texto, bool erro = false)
    {
        _aviso.Text = texto ?? ""; _aviso.ForeColor = erro ? Theme.Error : Theme.Ok; _aviso.Visible = !string.IsNullOrEmpty(texto);
    }

    /// <summary>Trava os botões enquanto uma chamada ao servidor está em andamento.</summary>
    public void Travar(bool sim)
    {
        _ocupado = sim;
        _criar.Enabled = _entrar.Enabled = _nomeNovo.Enabled = _codigo.Enabled = !sim;
        foreach (var b in _lista.Controls.Cast<Control>().SelectMany(Botoes)) b.Enabled = !sim;
    }

    static IEnumerable<Button> Botoes(Control c) => c.Controls.Cast<Control>().SelectMany(x => x is Button b ? new[] { b } : Botoes(x));
}

/// <summary>Janelinha com uma linha de texto (renomear grupo). Fica aqui, separada da lógica, para a estilização final.</summary>
public static class DialogoTexto
{
    /// <summary>Devolve o texto digitado, ou null se a pessoa cancelou.</summary>
    public static string? Pedir(IWin32Window dono, string titulo, string rotulo, string inicial, int maximo = 40)
    {
        using var f = new Form { Text = titulo, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, ClientSize = new Size(380, 110) };
        var l = new Label { Text = rotulo, AutoSize = true, Left = 12, Top = 12 };
        var t = new TextBox { Text = inicial, MaxLength = maximo };
        var campo = new CampoArredondado(t, 356) { Left = 12, Top = 36 };
        var ok = new Button { Text = "Salvar", DialogResult = DialogResult.OK, Left = 212, Top = 72, Width = 75 };
        var cancelar = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Left = 293, Top = 72, Width = 75 };
        f.Controls.AddRange(new Control[] { l, campo, ok, cancelar });
        f.AcceptButton = ok; f.CancelButton = cancelar;
        return f.ShowDialog(dono) == DialogResult.OK && t.Text.Trim().Length > 0 ? t.Text.Trim() : null;
    }
}
