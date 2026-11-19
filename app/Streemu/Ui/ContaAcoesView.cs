using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// Cartões da conta na página Perfil das Configurações (plano 019, etapas 5 e 6): estado da conta e os botões Entrar, Sair deste PC, Vincular o Google,
/// Sair de todos os dispositivos e Excluir minha conta. SÓ TELA: não fala com o servidor nem sabe o que cada botão faz; quem decide é
/// <c>MainForm.Conta.cs</c>, que liga os eventos. Visual provisório, no estilo das outras categorias (a estilização final fica para depois: pode trocar
/// o layout à vontade, desde que os eventos e os métodos públicos continuem).
/// </summary>
public sealed class ContaAcoesView : FlowLayoutPanel
{
    // Os textos ficam aqui, juntos, para quem restiliza mexer num lugar só.
    public const string TextoSairDestePc = "Sair deste PC encerra só a sessão deste computador.";
    public const string TextoTrocaDeSenha = "Trocar a senha da conta não encerra as sessões que já estão abertas: para isso, use Sair de todos os dispositivos.";
    public const string TextoVincular = "Ligue um Google à sua conta de e-mail e senha. Depois você entra por qualquer um dos dois, na mesma conta e com os mesmos grupos.";
    public const string TextoSairDeTodos = "Encerra a sessão do Streemu em todos os seus outros computadores. Este continua entrado.";
    public const string TextoExcluirResumo = "Apaga a sua conta, tira você de todos os grupos e não tem volta.";

    /// <summary>Pergunta de confirmação de "Sair de todos os dispositivos".</summary>
    public const string ConfirmaSairDeTodos = "Encerrar a sessão do Streemu em todos os seus outros computadores? Este PC continua entrado.";

    /// <summary>Texto da confirmação de "Excluir minha conta": o que apaga, o que NÃO apaga e que não tem volta (spec 019, 4.3).</summary>
    public const string ConfirmaExcluir =
        "Excluir sua conta Streemu?\n\n" +
        "O que acontece: você sai de todos os grupos (se for a última pessoa, o grupo acaba), as suas atividades somem, todos os seus computadores deixam de entrar e a conta de login (Google ou e-mail e senha) é apagada.\n\n" +
        "O que NÃO é apagado aqui: o diagnóstico que o Streemu já enviou. Ele tem o prazo e o pedido de exclusão dele.\n\n" +
        "Não tem volta. Para continuar, o navegador vai abrir e pedir que você entre de novo e digite EXCLUIR.";

    readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(620, 0), Margin = new Padding(0, 0, 0, 12) };
    readonly Button _entrar = new() { Text = "Entrar ou criar conta", AutoSize = true };
    readonly Button _sair = new() { Text = "Sair deste PC", AutoSize = true };
    readonly Button _vincular = new() { Text = "Vincular o Google", AutoSize = true };
    readonly Button _sairDeTodos = new() { Text = "Sair de todos os dispositivos", AutoSize = true };
    readonly Button _excluir = new() { Text = "Excluir minha conta", AutoSize = true };
    readonly Button _cancelar = new() { Text = "Cancelar", AutoSize = true, Visible = false };
    readonly Label _espera = new() { AutoSize = true, MaximumSize = new Size(620, 0), Margin = new Padding(0, 6, 0, 6), Visible = false };
    readonly Label _aviso = new() { AutoSize = true, MaximumSize = new Size(620, 0), Margin = new Padding(0, 6, 0, 6), Visible = false };
    // um cartão por assunto: quem sou (e entrar), as sessões abertas e a zona de perigo; o Atualizar mostra só os que valem
    readonly Label _notaVincular = AjusteBlocos.Nota(TextoVincular, alinhadaAoInterruptor: false);
    readonly CartaoDeAjuste _cartaoEntrada, _cartaoConta, _cartaoPerigo, _cartaoEspera;

    public event Action? PedirEntrar;
    public event Action? PedirSairDestePc;
    public event Action? PedirVincular;
    public event Action? PedirSairDeTodos;
    public event Action? PedirExcluir;
    /// <summary>A pessoa desistiu de esperar o navegador.</summary>
    public event Action? Cancelado;

    public ContaAcoesView()
    {
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = Padding.Empty;
        Theme.StyleButton(_entrar, true);
        foreach (var b in new[] { _sair, _vincular, _sairDeTodos, _cancelar }) Theme.StyleButton(b, false);
        EstilizarPerigo();
        _cartaoEntrada = AjusteBlocos.Cartao(AjusteBlocos.Legenda("Sua conta"), _status, AjusteBlocos.Botoes(_entrar));
        _cartaoConta = AjusteBlocos.Cartao(AjusteBlocos.Legenda("Sessões"),
            AjusteBlocos.Botoes(_sair), AjusteBlocos.Nota(TextoSairDestePc + " " + TextoTrocaDeSenha, alinhadaAoInterruptor: false),
            AjusteBlocos.Botoes(_vincular), _notaVincular,
            AjusteBlocos.Botoes(_sairDeTodos), AjusteBlocos.Nota(TextoSairDeTodos, alinhadaAoInterruptor: false));
        _cartaoPerigo = AjusteBlocos.Cartao(AjusteBlocos.Legenda("Zona de perigo"), AjusteBlocos.Botoes(_excluir), AjusteBlocos.Nota(TextoExcluirResumo, alinhadaAoInterruptor: false));
        _cartaoEspera = AjusteBlocos.Cartao(_espera, AjusteBlocos.Botoes(_cancelar), _aviso);
        foreach (var cartao in new[] { _cartaoEntrada, _cartaoConta, _cartaoPerigo, _cartaoEspera }) cartao.Margin = new Padding(0, 0, 0, 14);
        Controls.AddRange(new Control[] { _cartaoEntrada, _cartaoConta, _cartaoPerigo, _cartaoEspera });
        _entrar.Click += (_, _) => PedirEntrar?.Invoke();
        _sair.Click += (_, _) => PedirSairDestePc?.Invoke();
        _vincular.Click += (_, _) => PedirVincular?.Invoke();
        _sairDeTodos.Click += (_, _) => PedirSairDeTodos?.Invoke();
        _excluir.Click += (_, _) => PedirExcluir?.Invoke();
        _cancelar.Click += (_, _) => Cancelado?.Invoke();
        _cartaoEspera.Visible = false;
        Theme.Paint(this, new HashSet<Button> { _entrar }); // esta tela nasce depois do Theme.Apply da janela: sem isto os botões ficariam sem estilo
        EstilizarPerigo();
        Theme.Changed += EstilizarPerigo;
        // o Theme.Apply da janela devolve todo botão ao estilo neutro: o vermelho do Excluir volta toda vez
        _excluir.BackColorChanged += (_, _) => { if (_excluir.BackColor != Theme.Error) EstilizarPerigo(); };
        _excluir.ForeColorChanged += (_, _) => { if (_excluir.ForeColor != Color.White) EstilizarPerigo(); };
        Atualizar(false, null, false);
    }

    /// <summary>O botão de excluir é vermelho (não tem volta); refeito quando o tema muda.</summary>
    void EstilizarPerigo()
    {
        if (_estilizandoPerigo) return; // o estilo neutro do meio do caminho dispara os eventos de cor: sem a trava, recursão infinita
        _estilizandoPerigo = true;
        try
        {
            Theme.StyleButton(_excluir, false);
            _excluir.BackColor = Theme.Error;
            _excluir.FlatAppearance.MouseOverBackColor = Theme.Error;
            _excluir.ForeColor = Color.White;
        }
        finally { _estilizandoPerigo = false; }
    }

    bool _estilizandoPerigo;

    protected override void Dispose(bool disposing) { if (disposing) Theme.Changed -= EstilizarPerigo; base.Dispose(disposing); }

    /// <summary>Mostra o que a conta tem: sem conta só o botão de entrar; com conta, as ações (vincular só para quem entrou por e-mail e senha e ainda não ligou o Google).</summary>
    public void Atualizar(bool temConta, string? metodo, bool googleVinculado)
    {
        _status.Text = temConta
            ? "Você está com a conta Streemu entrada neste PC" + (metodo == "google.com" ? " (Google)." : metodo == "password" ? (googleVinculado ? " (e-mail e senha, com o Google vinculado)." : " (e-mail e senha).") : ".")
            : "Você ainda não entrou na sua conta Streemu.";
        _cartaoEntrada.Visible = true; // o status ("Você ainda não entrou" / "Você está com a conta...") aparece nos dois casos
        _entrar.Visible = _entrar.Parent!.Visible = !temConta; // a linha do botão some junto, sem deixar vão
        _cartaoConta.Visible = _cartaoPerigo.Visible = temConta;
        bool mostrarVincular = temConta && metodo == "password" && !googleVinculado; // (Visible de um filho diz falso com o pai escondido: não dá para ler de volta)
        _vincular.Visible = _notaVincular.Visible = _vincular.Parent!.Visible = mostrarVincular; // a linha do botão some junto, sem deixar vão
        foreach (var cartao in new[] { _cartaoEntrada, _cartaoConta, _cartaoPerigo }) { cartao.Controls[0].PerformLayout(); cartao.PerformLayout(); } // linhas que apareceram ou sumiram mudam a altura
    }

    /// <summary>Enquanto o navegador está aberto numa ação: trava os botões, mostra o que fazer e um Cancelar.</summary>
    public void Esperando(string texto)
    {
        _aviso.Visible = false;
        _espera.Text = texto; _espera.Visible = true; _cartaoEspera.Visible = true;
        _cancelar.Visible = true;
        Travar(true);
    }

    /// <summary>Fim da espera. <paramref name="aviso"/> aparece no lugar (erro em vermelho, ou confirmação).</summary>
    public void Livre(string? aviso = null, bool erro = false)
    {
        _espera.Visible = false; _cancelar.Visible = false;
        _aviso.Text = aviso ?? ""; _aviso.ForeColor = erro ? Theme.Error : Theme.Ok; _aviso.Visible = !string.IsNullOrEmpty(aviso);
        _cartaoEspera.Visible = _aviso.Visible;
        Travar(false);
    }

    void Travar(bool sim) { foreach (var b in new[] { _entrar, _sair, _vincular, _sairDeTodos, _excluir }) b.Enabled = !sim; }
}
