using System.Diagnostics;
using System.Text;
using Streemu.Telemetria;
using Streemu.Ui;
using Streemu.Util;
using B = Streemu.Ui.AjusteBlocos;

namespace Streemu;

/// <summary>
/// O aviso visível do gravador local (spec 018, seção 6.1, D37): em Ajustes > Diagnóstico, "Ver o que é enviado" (o que sobe no próximo envio e o que fica só no
/// PC, nos três estados), o histórico dos últimos 30 dias do que o gravador mandou, com "Ver o que foi" e "Apagar", e um ponto no botão Diagnóstico quando há
/// envio novo. Nada de pop-up, notificação ou som: a pessoa só vê quando abre a página. Depois do primeiro Apagar, uma pergunta única (nunca desliga o diagnóstico).
/// A lógica de quando a série sobe fica em <see cref="GravadorLocal"/>; aqui só a tela.
/// </summary>
public sealed partial class MainForm
{
    readonly FlowLayoutPanel _histLista = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
    readonly Label _histVazio = B.Nota("Nada da série da live foi enviado nos últimos 30 dias.", alinhadaAoInterruptor: false);
    readonly CheckBox _teleSoMarcar = new Ui.ToggleSwitch { Text = "Só enviar a série da live quando eu marcar", AutoSize = true };
    bool _histMudando;
    int _histTick;
    string _histAssinatura = "\0";

    /// <summary>O cartão da página Diagnóstico com o histórico do que o gravador enviou.</summary>
    Control CartaoHistoricoEnvios() => B.Cartao(
        B.Legenda("Séries da live enviadas"),
        B.Nota("A série segundo a segundo da live (quadros, sincronia) fica 7 dias só no seu PC. Ela só sobe se você marcar um problema, se o app tiver uma falha grave, se a live tiver um problema que dura vários minutos ou se um amigo da mesma live marcar um problema. O que subiu aparece aqui por 30 dias; você pode ver o que foi e apagar do servidor.", alinhadaAoInterruptor: false),
        _histVazio, _histLista, _teleSoMarcar,
        B.Nota("Com isto ligado, só a sua marcação sobe a série da live. O diagnóstico em si não desliga."));

    void IniciarHistoricoEnvios()
    {
        _teleVer.Click += (_, _) => MostrarPrevia();
        _histMudando = true;
        _teleSoMarcar.Checked = _state.TelemetriaSoQuandoMarcar;
        _histMudando = false;
        _teleSoMarcar.CheckedChanged += (_, _) =>
        {
            if (_histMudando) return;
            _state.TelemetriaSoQuandoMarcar = _teleSoMarcar.Checked;
            if (!Automated) _state.Save();
            Log.Info(_teleSoMarcar.Checked ? "A série da live só sobe quando a pessoa marca o problema." : "A série da live volta a subir também nas falhas graves, nos problemas que duram e nos pedidos de amigos.");
        };
        if (_settingsPage != null) _settingsPage.CategoryChanged += nome => { if (nome == "Diagnóstico") { AtualizarHistorico(marcarVistos: true); _settingsPage?.SetBadge("Diagnóstico", false); } };
        AtualizarHistorico(marcarVistos: false);
    }

    /// <summary>Roda a cada segundo com os Ajustes à vista: a cada 3 s atualiza o ponto do botão (ou a lista, se a pessoa já está na página).</summary>
    void TickHistoricoEnvios()
    {
        if (_histTick++ % 3 != 0 || _settingsPage == null) return;
        if (_settingsPage.SelectedCategory == "Diagnóstico") { AtualizarHistorico(marcarVistos: true); _settingsPage.SetBadge("Diagnóstico", false); }
        else _settingsPage.SetBadge("Diagnóstico", HistoricoEnvios.TemNovo());
    }

    static string Tamanho(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.#} MB" : bytes >= 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes} B";

    static string MotivoDoEnvio(string motivo) => motivo switch
    {
        nameof(Disparo.Marcacao) => "você marcou um problema",
        nameof(Disparo.FalhaGrave) => "falha grave do app",
        nameof(Disparo.ResumoComProblema) => "problema que durou na live",
        nameof(Disparo.Pedido) => "um amigo da live marcou um problema",
        _ => "envio da série da live",
    };

    void AtualizarHistorico(bool marcarVistos)
    {
        var lista = HistoricoEnvios.Listar();
        string assinatura = string.Join("|", lista.Select(e => $"{e.Pacote}:{e.Bytes}"));
        bool temNovo = lista.Any(e => !e.Visto);
        if (assinatura != _histAssinatura)
        {
            _histAssinatura = assinatura;
            _histLista.SuspendLayout();
            foreach (Control c in _histLista.Controls.Cast<Control>().ToList()) { _histLista.Controls.Remove(c); c.Dispose(); }
            foreach (var e in lista)
            {
                var envio = e;
                string quando = DateTime.TryParse(e.Quando, null, System.Globalization.DateTimeStyles.RoundtripKind, out var q) ? q.ToLocalTime().ToString("dd/MM HH:mm") : e.Quando;
                string anonimo = e.Modo == nameof(ModoEnvio.Anonimo) ? " (anônimo)" : "";
                var texto = new Label
                {
                    Text = $"{(e.Visto ? "" : "Novo · ")}{quando} · {MotivoDoEnvio(e.Motivo)}{anonimo} · {e.Arquivos.Count} arquivo(s), {Tamanho(e.Bytes)}",
                    AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 6, 0, 0),
                };
                var ver = new Button { Text = "Ver o que foi", AutoSize = true };
                var apagar = new Button { Text = "Apagar", AutoSize = true };
                Theme.StyleButton(ver, false); Theme.StyleButton(apagar, false);
                ver.Click += (_, _) => MostrarEnvio(envio);
                apagar.Click += (_, _) => ApagarEnvio(envio);
                _histLista.Controls.Add(texto);
                _histLista.Controls.Add(B.Botoes(ver, apagar));
            }
            _histLista.ResumeLayout();
            _histVazio.Visible = lista.Count == 0;
            _settingsPage?.RelayoutPage();
        }
        if (marcarVistos && temNovo) HistoricoEnvios.MarcarVistos();
    }

    /// <summary>"Ver o que foi": o que o pacote levou, sem abrir o conteúdo (ele está nos arquivos da sessão, na pasta local, por 7 dias).</summary>
    void MostrarEnvio(EntradaEnvio e)
    {
        string quando = DateTime.TryParse(e.Quando, null, System.Globalization.DateTimeStyles.RoundtripKind, out var q) ? q.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : e.Quando;
        var t = new StringBuilder();
        t.AppendLine($"Enviado em {quando}.");
        t.AppendLine($"Motivo: {MotivoDoEnvio(e.Motivo)}.");
        t.AppendLine($"Estado do diagnóstico: {(e.Modo == nameof(ModoEnvio.Anonimo) ? "anônimo (sem o seu nome nem o dos amigos)" : "identificado")}.");
        t.AppendLine($"Arquivos: {string.Join(", ", e.Arquivos)} ({Tamanho(e.Bytes)} no total).");
        t.AppendLine();
        t.AppendLine("É a série segundo a segundo da live: quadros por segundo, atraso, sincronia de áudio e vídeo e o uso da placa. Não tem imagem, nem áudio, nem o texto do que você escreveu.");
        t.AppendLine("O conteúdo exato está nos arquivos da sessão, na pasta local do diagnóstico, até completarem 7 dias.");
        t.AppendLine();
        t.AppendLine("Apagar tira este envio do servidor; os outros envios e o resto do diagnóstico não mudam.");
        MostrarJanelaDeTexto("O que foi enviado", t.ToString(), ("Abrir a pasta local", AbrirPastaDiagnostico));
    }

    /// <summary>"Ver o que é enviado": o que sobe no próximo envio e o que fica só no PC, nos três estados.</summary>
    void MostrarPrevia()
    {
        var t = new StringBuilder();
        var modo = _tele?.Modo ?? Envio.ModoDosAjustes(_state);
        t.AppendLine(modo switch
        {
            ModoEnvio.Completo => "Diagnóstico identificado: o que sobe leva o seu id de PC e de instalação, o seu nome no app e os amigos como amigo-N.",
            ModoEnvio.Anonimo => "Diagnóstico anônimo: o que sobe não leva o seu nome, o seu Discord nem o nome dos amigos (trocados por pessoa-N).",
            _ => "Diagnóstico desligado: nada abaixo sobe. Fica só no PC, para você ver. Se você marcar um problema, uma janela mostra o que iria e só envia se você apertar \"Enviar só isto\".",
        });
        t.AppendLine();
        if (_tele == null) t.AppendLine("O diagnóstico não ligou nesta abertura: nada sobe agora.");
        else
        {
            var itens = _tele.Previa();
            var sobe = itens.Where(i => i.Sobe).ToList(); var fica = itens.Where(i => !i.Sobe).ToList();
            t.AppendLine("VAI SUBIR NO PRÓXIMO ENVIO");
            if (sobe.Count == 0) t.AppendLine("- nada além do que já subiu");
            foreach (var i in sobe) t.AppendLine($"- {i.Arquivo} ({Tamanho(i.Bytes)}): {i.Motivo}");
            t.AppendLine();
            t.AppendLine("FICA SÓ NO SEU PC");
            if (fica.Count == 0) t.AppendLine("- nada");
            foreach (var i in fica) t.AppendLine($"- {i.Arquivo} ({Tamanho(i.Bytes)}): {i.Motivo}");
        }
        t.AppendLine();
        var enviados = HistoricoEnvios.Listar();
        t.AppendLine($"JÁ ENVIADO (série da live, 30 dias): {enviados.Count} envio(s). A lista está em Ajustes > Diagnóstico, com \"Ver o que foi\" e \"Apagar\".");
        t.AppendLine();
        t.AppendLine("Só a camada essencial sobe sempre: contagem de uso e de falhas graves, sem guardar quem foi.");
        MostrarJanelaDeTexto("O que é enviado", t.ToString(), ("Abrir a pasta local", AbrirPastaDiagnostico));
    }

    // ---------------------------------------------------------------- marcação com o diagnóstico Desligado (spec 018, seção 6)

    /// <summary>Janela que aparece sem tomar o foco (quem está jogando não perde o jogo); os botões funcionam ao clicar.</summary>
    sealed class JanelaSemFoco : Form { protected override bool ShowWithoutActivation => true; }

    Form? _janelaMarcacao;

    static string NomeDoArquivoParaAPessoa(string arquivo) => arquivo switch
    {
        SessaoLocal.Log => "o log do Streemu desta abertura",
        SessaoLocal.Eventos => "os eventos desta abertura (lives, falhas e a sua marcação)",
        _ when arquivo.StartsWith("quadros", StringComparison.Ordinal) => "a série por segundo da sua transmissão (quadros, espectadores e uso da placa)",
        _ when arquivo.StartsWith("sincronia", StringComparison.Ordinal) => "a série por segundo da live (atraso e sincronia de áudio e vídeo)",
        _ => arquivo,
    };

    /// <summary>
    /// Marcou um problema com o diagnóstico Desligado (botão, atalho ou bandeja): nada sobe sozinho. Esta janela diz o que iria, com os tamanhos, e só envia no
    /// "Enviar só isto"; "Não enviar", o X e o Esc não mandam nada. Sem som, sem tomar o foco, não modal. Os dois botões têm o mesmo tamanho para nenhum puxar o clique.
    /// O envio é uma vez só e não liga o diagnóstico (<see cref="GravadorLocal.EnviarMarcacaoDesligada"/>).
    /// </summary>
    void PerguntarEnvioDesligado(DateTime quando)
    {
        if (_tele == null || _gravador == null || IsDisposed) return;
        _janelaMarcacao?.Close(); // marcou de novo sem responder: vale a janela mais nova, com os tamanhos de agora
        var tele = _tele; var gravador = _gravador;
        var itens = tele.TamanhosDoEnvioUnico(gravador.ArquivosDaMarcacaoDesligada());
        var t = new StringBuilder();
        t.AppendLine($"Você marcou um problema às {quando:HH:mm:ss}. O diagnóstico está desligado: nada saiu do seu PC.");
        t.AppendLine();
        if (itens.Count == 0) t.AppendLine("Não há nada novo para enviar desta abertura.");
        else
        {
            t.AppendLine("Se quiser mandar só este problema, vai isto:");
            foreach (var (arquivo, bytes) in itens) t.AppendLine($"- {NomeDoArquivoParaAPessoa(arquivo)}: {Tamanho(bytes)}");
            t.AppendLine();
            t.AppendLine("Sem o seu nome nem o dos amigos que o Streemu conhece (trocados por pessoa-N). Sobe uma vez e o diagnóstico continua desligado.");
        }
        int largura = UiScale.Dp(this, 420);
        var f = new JanelaSemFoco
        {
            Text = "Problema marcado", FormBorderStyle = FormBorderStyle.FixedToolWindow, StartPosition = FormStartPosition.CenterScreen,
            ShowInTaskbar = false, TopMost = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12),
            BackColor = Theme.Surface, ForeColor = Theme.Text,
        };
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        var texto = new Label { Text = t.ToString().TrimEnd(), AutoSize = true, MaximumSize = new Size(largura, 0) };
        var estado = new Label { Text = "", AutoSize = true, MaximumSize = new Size(largura, 0), Margin = new Padding(3, 8, 3, 0), ForeColor = SystemColors.GrayText };
        var enviar = new Button { Text = "Enviar só isto", Dock = DockStyle.Fill, Enabled = itens.Count > 0 };
        var naoEnviar = new Button { Text = "Não enviar", Dock = DockStyle.Fill };
        Theme.StyleButton(enviar, false); Theme.StyleButton(naoEnviar, false);
        var botoes = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Width = largura, Height = UiScale.Dp(this, 36), Margin = new Padding(3, 10, 3, 0) };
        botoes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); botoes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        botoes.Controls.Add(enviar, 0, 0); botoes.Controls.Add(naoEnviar, 1, 0);
        layout.Controls.Add(texto); layout.Controls.Add(estado); layout.Controls.Add(botoes);
        f.Controls.Add(layout);
        f.CancelButton = naoEnviar; // Esc fecha sem enviar; o Enter não manda nada (nenhum botão é o padrão)
        naoEnviar.Click += (_, _) => f.Close();
        enviar.Click += (_, _) =>
        {
            enviar.Enabled = false; naoEnviar.Enabled = false;
            estado.Text = "Enviando...";
            // no Desligado o app não ensinava os nomes (a sala, o apelido, o Discord) ao troca-nomes: ensina agora, com a mesma regra do anônimo (só fica pronto depois de a sala trazer os amigos ou de 60 s)
            EnsinarAmigosAosPseudonimos(forcar: true);
            EnsinarNomesAoAnonimizador(forcar: true);
            Task.Run(() => gravador.EnviarMarcacaoDesligada()).ContinueWith(r => Ui(() =>
            {
                var (ok, mensagem) = r.IsFaulted ? (false, "Não consegui enviar (" + r.Exception?.GetBaseException().Message + "). Nada saiu do PC; tente de novo mais tarde.") : r.Result;
                if (f.IsDisposed) { if (ok) { AtualizarHistorico(marcarVistos: false); Notify(mensagem); } return; }
                estado.Text = mensagem;
                naoEnviar.Enabled = true;
                if (ok) { enviar.Text = "Enviado"; naoEnviar.Text = "Fechar"; AtualizarHistorico(marcarVistos: false); }
                else enviar.Enabled = true; // falhou: dá para tentar de novo, e o que não subiu continua só no PC
            }));
        };
        f.FormClosed += (_, _) => { if (ReferenceEquals(_janelaMarcacao, f)) _janelaMarcacao = null; f.Dispose(); };
        _janelaMarcacao = f;
        f.Show(); // sem dono: o app pode estar na bandeja, e a janela não tem que puxar a principal
    }

    static void AbrirPastaDiagnostico()
    {
        try { Directory.CreateDirectory(Pasta.Raiz); Process.Start(new ProcessStartInfo("explorer.exe", Pasta.Raiz) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Janela simples de texto, não modal, com um botão de ação opcional e o Fechar.</summary>
    void MostrarJanelaDeTexto(string titulo, string texto, (string Rotulo, Action Acao)? acao = null)
    {
        var f = new Form
        {
            Text = titulo, FormBorderStyle = FormBorderStyle.Sizable, StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, Padding = new Padding(12),
            BackColor = Theme.Surface, ForeColor = Theme.Text,
            ClientSize = new Size(UiScale.Dp(this, 560), UiScale.Dp(this, 420)), MinimumSize = new Size(UiScale.Dp(this, 380), UiScale.Dp(this, 260)),
        };
        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, WordWrap = true, TabStop = false,
            Text = texto.Replace("\r\n", "\n").Replace("\n", "\r\n"), BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.None,
        };
        var fechar = new Button { Text = "Fechar", AutoSize = true, Dock = DockStyle.Bottom };
        Theme.StyleButton(fechar, true);
        f.Controls.Add(box);
        if (acao is { } a)
        {
            var botao = new Button { Text = a.Rotulo, AutoSize = true, Dock = DockStyle.Bottom };
            Theme.StyleButton(botao, false);
            botao.Click += (_, _) => a.Acao();
            f.Controls.Add(botao);
        }
        f.Controls.Add(fechar);
        f.AcceptButton = fechar; f.CancelButton = fechar;
        fechar.Click += (_, _) => f.Close();
        f.FormClosed += (_, _) => f.Dispose();
        f.Show(this);
        box.SelectionStart = 0; box.SelectionLength = 0;
    }

    /// <summary>"Apagar": pede ao servidor para apagar só este pacote; na primeira vez, depois de apagar, a pergunta única.</summary>
    void ApagarEnvio(EntradaEnvio e)
    {
        if (_tele == null) { Notify("O diagnóstico não está ligado nesta abertura: não consigo falar com o servidor para apagar. Abra o Streemu de novo e tente outra vez."); return; }
        var r = MessageBox.Show(this, "Apagar este envio do servidor? Só este; os outros e o resto do diagnóstico não mudam. Não dá para desfazer.", "Apagar envio", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;
        var envio = _tele;
        Task.Run(() => envio.ApagarPacote(e.Pacote, e.Modo)).ContinueWith(t => Ui(() =>
        {
            var (ok, mensagem) = t.IsFaulted ? (false, "Não consegui apagar: " + t.Exception?.GetBaseException().Message) : t.Result;
            if (!ok) { Notify(mensagem); return; }
            HistoricoEnvios.Remover(e.Pacote);
            AtualizarHistorico(marcarVistos: false);
            Notify("Envio apagado do servidor.");
            PerguntarDepoisDoPrimeiroApagar();
        }));
    }

    /// <summary>
    /// A pergunta única depois do primeiro Apagar (spec 018, D37): "só quando eu marcar" é uma escolha da pessoa, nunca um desligamento automático do
    /// diagnóstico. Seja qual for a resposta, não pergunta de novo.
    /// </summary>
    void PerguntarDepoisDoPrimeiroApagar()
    {
        if (_state.TelemetriaPerguntouApagar) return;
        _state.TelemetriaPerguntouApagar = true;
        var r = MessageBox.Show(this, "Quer que a série da live só suba quando você mesmo marcar um problema? O diagnóstico continua ligado; muda só quando a série sobe. Dá para trocar depois em Ajustes > Diagnóstico.",
            "Série da live", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r == DialogResult.Yes) { _state.TelemetriaSoQuandoMarcar = true; _histMudando = true; _teleSoMarcar.Checked = true; _histMudando = false; }
        if (!Automated) _state.Save();
    }
}
