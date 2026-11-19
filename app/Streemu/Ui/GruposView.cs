using Streemu.Net;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>Uma linha da lista de grupos. <see cref="Papel"/> nulo = ainda não carregou os detalhes ("dono" ou "membro").</summary>
public sealed record GrupoLinha(string Id, string Nome, string? Papel, IReadOnlyList<MembroInfo>? Membros);

/// <summary>
/// Conteúdo da tela "Grupos" da barra lateral (plano 019, etapa 8; ela era uma categoria das Configurações): os grupos da pessoa (um cartão por grupo, com
/// "Copiar convite" à vista e "Renomear", "Trocar convite" e "Sair" no menu "Mais"; para o dono, também "Remover" por membro) e, abaixo, "Criar um grupo" e
/// "Entrar com um convite" lado a lado. Sem grupo nenhum a página abre com uma explicação curta e os dois caminhos no lugar de destaque.
/// SÓ TELA: não fala com o servidor; quem decide é <c>MainForm.Grupos.cs</c>, que liga os eventos.
/// Redesenhada em 2026-11-19 (teste de primeiro uso: cartão cortado e metade da página vazia). O layout é feito à mão em <see cref="Organizar"/>, pela largura
/// que a página dá: tudo cabe ou desce de linha, nada é cortado. Os eventos e os métodos públicos não mudaram.
/// </summary>
public sealed class GruposView : Panel
{
    public const string TextoVazio = "Um grupo é o seu círculo no Streemu: as pessoas dele veem quando você está online e ao vivo, e você vê as delas. Crie o seu ou entre com o convite de um amigo.";
    /// <summary>Confirmação de "Sair" (texto do plano 019).</summary>
    public const string ConfirmaSair = "Você deixa de ver as pessoas deste grupo. Se for o último, o grupo é apagado.";

    readonly Label _aviso = new() { AutoSize = true, Visible = false };
    readonly Label _intro = new() { Text = TextoVazio, AutoSize = true, ForeColor = SystemColors.GrayText };
    readonly Label _secao = AjusteBlocos.Legenda("Seus grupos");
    readonly List<Control> _cartoes = new();
    readonly TextBox _nomeNovo = new() { Width = 220, MaxLength = 40, PlaceholderText = "Nome do grupo" };
    readonly Button _criar = new() { Text = "Criar grupo", AutoSize = false };
    readonly TextBox _codigo = new() { Width = 260, MaxLength = 80, PlaceholderText = "Código ou link do convite" };
    readonly Button _entrar = new() { Text = "Entrar com código", AutoSize = false };
    readonly CartaoDeAjuste _cartaoCriar = new(), _cartaoEntrar = new();
    readonly ConteudoAcao _conteudoCriar, _conteudoEntrar;
    bool _ocupado, _temAviso, _organizando;

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
        BackColor = Theme.Surface;
        Margin = Padding.Empty;
        _conteudoCriar = new ConteudoAcao("Criar um grupo", "Dê um nome e mande o link do convite a quem você quer por perto.", new CampoArredondado(_nomeNovo, 260), _criar);
        _conteudoEntrar = new ConteudoAcao("Entrar com um convite", "Cole o código ou o link que um amigo mandou.", new CampoArredondado(_codigo, 300), _entrar);
        _cartaoCriar.Controls.Add(_conteudoCriar);
        _cartaoEntrar.Controls.Add(_conteudoEntrar);
        Controls.AddRange(new Control[] { _aviso, _intro, _secao, _cartaoCriar, _cartaoEntrar });
        _criar.Click += (_, _) => { var n = _nomeNovo.Text.Trim(); if (n.Length > 0) PedirCriar?.Invoke(n); else Aviso("Dê um nome ao grupo (até 40 letras).", true); };
        _entrar.Click += (_, _) => { var c = _codigo.Text.Trim(); if (c.Length > 0) PedirEntrar?.Invoke(c); else Aviso("Cole o código ou o link do convite.", true); };
        _nomeNovo.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _criar.PerformClick(); } };
        _codigo.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _entrar.PerformClick(); } };
        Theme.Paint(this, new HashSet<Button>()); // esta tela nasce depois do Theme.Apply da janela: sem isto os botões ficariam sem estilo. Só uma vez: repintar troca o cinza dos textos de apoio
        Mostrar(Array.Empty<GrupoLinha>());
    }

    public void LimparCampos() { _nomeNovo.Clear(); _codigo.Clear(); }

    /// <summary>Refaz a lista. Quem chama já ordena (a ordem de entrada).</summary>
    public void Mostrar(IReadOnlyList<GrupoLinha> grupos)
    {
        SuspendLayout();
        foreach (var c in _cartoes) { Controls.Remove(c); c.Dispose(); }
        _cartoes.Clear();
        var principais = new HashSet<Button>();
        foreach (var g in grupos)
        {
            var cartao = Cartao(g, principais);
            // pinta só o cartão novo (por um painel de passagem): o Theme.Paint repinta o texto de tudo que recebe e tiraria o cinza dos textos de apoio já pintados
            using (var passagem = new Panel()) { passagem.Controls.Add(cartao); Theme.Paint(passagem, principais); passagem.Controls.Remove(cartao); }
            _cartoes.Add(cartao); Controls.Add(cartao);
            cartao.SizeChanged += (_, _) => Organizar();
        }
        bool vazio = grupos.Count == 0;
        _intro.Visible = vazio; _secao.Visible = !vazio;
        // um botão de destaque por conjunto: sem grupo, são os dois caminhos; com grupos, o "Copiar convite" de cada um
        Theme.StyleButton(_criar, vazio); Theme.StyleButton(_entrar, vazio);
        _conteudoCriar.ComNota = _conteudoEntrar.ComNota = vazio;
        _conteudoCriar.Titulo = vazio ? "Criar um grupo" : "Criar outro grupo";
        ResumeLayout(false);
        PintarSair();
        Travar(_ocupado);
        Organizar();
    }

    Control Cartao(GrupoLinha g, HashSet<Button> principais)
    {
        string papel = g.Papel == "dono" ? "você é o dono" : g.Papel == "membro" ? "você é membro" : "carregando...";
        string membros = g.Membros == null ? "" : g.Membros.Count == 1 ? "1 pessoa · " : $"{g.Membros.Count} pessoas · ";
        string nome = g.Nome.Length > 0 ? g.Nome : "(sem nome)";
        var titulo = new Label { Text = nome, AutoSize = false, AutoEllipsis = true, Font = UiTypography.Create(UiTypography.Section * (float)UiScale.User, FontStyle.Bold) }; // o cartão nasce depois da escala do Ctrl +: a fonte própria já sai na escala
        titulo.Disposed += (_, _) => titulo.Font.Dispose();
        var detalhe = new Label { Text = membros + papel, AutoSize = false, AutoEllipsis = true, ForeColor = SystemColors.GrayText };
        var copiar = Botao("Copiar convite", () => PedirCopiarConvite?.Invoke(g.Id));
        principais.Add(copiar);
        Button segundo;
        if (g.Papel == "dono")
        {
            segundo = Botao("Mais ▾", null);
            segundo.AccessibleName = "Mais ações do grupo " + nome;
            segundo.Click += (_, _) => MostrarMenu(segundo, g);
        }
        else
        {
            segundo = Botao("Sair", () => PedirSair?.Invoke(g.Id));
            segundo.Tag = "sair";
        }
        var pessoas = new List<(Label, Button)>();
        if (g.Papel == "dono" && g.Membros != null)
            foreach (var m in g.Membros.Where(m => m.Papel != "dono"))
            {
                var rotulo = new Label { Text = m.Apelido.Length > 0 ? m.Apelido : "(sem apelido)", AutoSize = false, AutoEllipsis = true };
                pessoas.Add((rotulo, Botao("Remover", () => PedirRemover?.Invoke(g.Id, m.Id, m.Apelido))));
            }
        var cartao = new CartaoDeAjuste();
        cartao.Controls.Add(new ConteudoGrupo(titulo, detalhe, copiar, segundo, pessoas));
        return cartao;
    }

    /// <summary>Renomear, Trocar convite e Sair, num menu do estilo do app. "Sair" em vermelho: é a única ação que tira a pessoa do grupo.</summary>
    void MostrarMenu(Button ancora, GrupoLinha g)
    {
        var menu = new ContextMenuStrip();
        TrayVisuals.Style(menu);
        menu.Items.Add(new ToolStripMenuItem("Renomear", null, (_, _) => PedirRenomear?.Invoke(g.Id)));
        menu.Items.Add(new ToolStripMenuItem("Trocar convite", null, (_, _) => PedirTrocarConvite?.Invoke(g.Id)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Sair do grupo", null, (_, _) => PedirSair?.Invoke(g.Id)) { ForeColor = Theme.Error });
        menu.Closed += (_, _) => menu.BeginInvoke(menu.Dispose);
        menu.Show(ancora, new Point(ancora.Width, ancora.Height), ToolStripDropDownDirection.BelowLeft);
    }

    /// <summary>O "Sair" que fica à vista (membro) leva a cor de ação destrutiva; o Theme.Paint repinta o texto de todo botão, então isto vem depois.</summary>
    void PintarSair()
    {
        foreach (var b in Botoes(this).Where(b => b.Tag as string == "sair")) b.ForeColor = Theme.Error;
    }

    static Button Botao(string texto, Action? aoClicar)
    {
        var botao = new Button { Text = texto, AutoSize = false, Tag = "acao" };
        if (aoClicar != null) botao.Click += (_, _) => aoClicar();
        return botao;
    }

    /// <summary>Texto no alto da página: erro (vermelho) ou confirmação.</summary>
    public void Aviso(string? texto, bool erro = false)
    {
        _aviso.Text = texto ?? ""; _aviso.ForeColor = erro ? Theme.Error : Theme.Ok;
        _temAviso = !string.IsNullOrEmpty(texto); _aviso.Visible = _temAviso;
        Organizar();
    }

    /// <summary>Trava os botões enquanto uma chamada ao servidor está em andamento.</summary>
    public void Travar(bool sim)
    {
        _ocupado = sim;
        _criar.Enabled = _entrar.Enabled = _nomeNovo.Enabled = _codigo.Enabled = !sim;
        foreach (var b in _cartoes.SelectMany(Botoes)) b.Enabled = !sim;
    }

    static IEnumerable<Button> Botoes(Control c) => c.Controls.Cast<Control>().SelectMany(x => x is Button b ? new[] { b } : Botoes(x));

    // ------------------------------------------------------------------ layout

    /// <summary>Largura mínima de cada cartão para os dois ficarem lado a lado ("Criar" e "Entrar"); abaixo disso empilham.</summary>
    const int CartaoMinimoAt96 = 340, EspacoAt96 = 16;

    protected override void OnResize(EventArgs e) { base.OnResize(e); Organizar(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) Organizar(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Organizar(); }

    void Organizar()
    {
        if (_organizando || Width <= 0 || IsDisposed) return;
        _organizando = true;
        try
        {
            int largura = Width, gap = UiScale.Dp(this, EspacoAt96), y = 0;
            var faixa = new Size(largura, 0);
            if (_temAviso)
            {
                _aviso.MaximumSize = faixa; _aviso.Location = new Point(0, y);
                y += _aviso.GetPreferredSize(faixa).Height + UiScale.Dp(this, 10);
            }
            bool vazio = _cartoes.Count == 0;
            var cabeca = vazio ? _intro : _secao;
            cabeca.MaximumSize = faixa; cabeca.Location = new Point(0, y);
            y += cabeca.GetPreferredSize(faixa).Height + (vazio ? gap : UiScale.Dp(this, 8));
            int entre = UiScale.Dp(this, 12);
            foreach (var cartao in _cartoes)
            {
                cartao.SetBounds(0, y, largura, cartao.Height);
                cartao.PerformLayout();
                y += cartao.Height + entre;
            }
            if (!vazio) y += gap - entre;

            bool lado = largura >= UiScale.Dp(this, 2 * CartaoMinimoAt96 + EspacoAt96);
            if (lado)
            {
                int cada = (largura - gap) / 2;
                _conteudoCriar.AlturaMinima = _conteudoEntrar.AlturaMinima = 0;
                _conteudoCriar.AlturaNotaMinima = _conteudoEntrar.AlturaNotaMinima = 0;
                _cartaoCriar.SetBounds(0, y, cada, _cartaoCriar.Height);
                _cartaoEntrar.SetBounds(cada + gap, y, largura - cada - gap, _cartaoEntrar.Height);
                _cartaoCriar.PerformLayout(); _cartaoEntrar.PerformLayout();
                // a frase de apoio dos dois ocupa a mesma altura: campo e botão na mesma linha
                _conteudoCriar.AlturaNotaMinima = _conteudoEntrar.AlturaNotaMinima = Math.Max(_conteudoCriar.AlturaDaNota(_conteudoCriar.Width), _conteudoEntrar.AlturaDaNota(_conteudoEntrar.Width));
                _cartaoCriar.PerformLayout(); _cartaoEntrar.PerformLayout();
                int altura = Math.Max(_cartaoCriar.Height, _cartaoEntrar.Height);
                // os dois cartões da fileira têm a mesma altura, com os campos alinhados em cima
                _conteudoCriar.AlturaMinima = _conteudoEntrar.AlturaMinima = altura - _cartaoCriar.Padding.Vertical;
                _cartaoCriar.PerformLayout(); _cartaoEntrar.PerformLayout();
                y += Math.Max(_cartaoCriar.Height, _cartaoEntrar.Height);
            }
            else
            {
                _conteudoCriar.AlturaMinima = _conteudoEntrar.AlturaMinima = 0;
                _conteudoCriar.AlturaNotaMinima = _conteudoEntrar.AlturaNotaMinima = 0;
                _cartaoCriar.SetBounds(0, y, largura, _cartaoCriar.Height); _cartaoCriar.PerformLayout();
                y += _cartaoCriar.Height + gap;
                _cartaoEntrar.SetBounds(0, y, largura, _cartaoEntrar.Height); _cartaoEntrar.PerformLayout();
                y += _cartaoEntrar.Height;
            }
            if (Height != y) Height = y;
        }
        finally { _organizando = false; }
    }
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
