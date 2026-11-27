using System.Text.Json;
using System.Text.Json.Nodes;
using Streemu.Net;
using Streemu.Telemetria;
using Streemu.Ui;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// Conta Streemu na janela principal (spec 019, etapa 5; spec 022). A conta é sempre obrigatória e não há sala antiga: com a conta guardada o app abre
/// DIRETO a sala da conta; sem conta mostra a tela de entrada e não abre sala nenhuma. Servidor que não responde nunca vira um modo sem conta:
/// o app avisa o motivo provável e pergunta de novo sozinho.
/// </summary>
public sealed partial class MainForm
{
    Conta? _conta;
    EstadoServidor _contaServidor = EstadoServidor.Obrigatoria;
    bool _salaDeConta;
    bool _contaSemGrupoNoTeste;
    string? _contaGrupoId;
    bool _testeEntradaIniciada, _contaServidorForaLogado, _entradaComAvisoDoServidor;
    EntradaView? _entrada;
    const string UrlPrivacidade = "https://www.streemu.com.br/privacidade";
    ContaAcoesView? _contaAcoes;
    bool _contaAjustesAdicionados;
    CancellationTokenSource? _acaoCts;
    bool _contaExcluindo;
    readonly System.Windows.Forms.Timer _contaTimer = new() { Interval = 10 * 60 * 1000 };
    /// <summary>Pergunta de novo, rápido, quando o servidor não respondeu: o ciclo de 10 minutos deixaria a pessoa olhando um aviso por tempo demais.</summary>
    readonly System.Windows.Forms.Timer _contaRetry = new() { Interval = 15 * 1000 };
    const string AvisoServidorFora = "O servidor do grupo não respondeu. Confira a internet; o app tenta de novo sozinho.";

    /// <summary>STREEMU_TEST_CONTA=1: o teste faz o login sem navegador, por arquivos em STREEMU_TEST_DIR (ver docs/VARIAVEIS-DE-TESTE.md).</summary>
    static bool TestConta => Environment.GetEnvironmentVariable("STREEMU_TEST_CONTA") == "1" && TestDir != null;
    /// <summary>STREEMU_TEST_CONTA_SEM_LOGIN=1: não inicia o login sozinho (teste da conta obrigatória). STREEMU_TEST_CONTA_SAIR=1: depois de entrar, sai do PC.</summary>
    static bool TesteSemLogin => Environment.GetEnvironmentVariable("STREEMU_TEST_CONTA_SEM_LOGIN") == "1";
    static bool TesteSair => Environment.GetEnvironmentVariable("STREEMU_TEST_CONTA_SAIR") == "1";
    async Task SairParaTesteAsync() { if (_conta == null) return; _testeEntradaIniciada = true; await _conta.SairAsync(); await AplicarEstadoServidorAsync(_contaServidor, null); }
    string TestArq(string sufixo) => Path.Combine(TestDir!, TestRole + "-" + sufixo);

    /// <summary>STREEMU_TEST_CONTA_ACAO=vincular|sessoes|excluir: depois de entrar, o teste faz a ação (o navegador é trocado por arquivos, como no login) e grava o relatório.
    /// "esperar": só espera a conta cair (o outro PC usou "sair de todos"). STREEMU_TEST_CONTA_ACAO_ESPERA=B: antes de agir, espera o papel B terminar de entrar.</summary>
    static string? TesteAcaoConta => TestConta ? Environment.GetEnvironmentVariable("STREEMU_TEST_CONTA_ACAO") : null;

    async Task TesteContaAcaoAsync(string acao)
    {
        if (acao == "esperar")
        {
            var limite = DateTime.Now.AddSeconds(90);
            while (_conta is { TemConta: true } && DateTime.Now < limite) await Task.Delay(200);
            await Task.Delay(1500);
        }
        else
        {
            if (Environment.GetEnvironmentVariable("STREEMU_TEST_CONTA_ACAO_ESPERA") is { Length: > 0 } outro)
            {
                var limite = DateTime.Now.AddSeconds(90);
                while (!File.Exists(Path.Combine(TestDir!, outro + "-conta.json")) && DateTime.Now < limite) await Task.Delay(200);
                await Task.Delay(4000); // o outro PC precisa estar na sala para ser derrubado
            }
            await RodarAcaoAsync(acao switch { "vincular" => AcaoConta.Vincular, "sessoes" => AcaoConta.SairDeTodos, _ => AcaoConta.Excluir });
            await Task.Delay(3000); // dá tempo de o outro PC reagir antes de o teste fechar
        }
        File.WriteAllText(Path.Combine(TestDir!, TestRole + "-relatorio.json"), BuildReport());
    }

    /// <summary>Conta obrigatória e a pessoa ainda não entrou: o início vira a tela de entrada e transmitir, assistir e conectar ficam travados.</summary>
    bool EntradaObrigatoria => _contaServidor == EstadoServidor.Obrigatoria && _conta is { TemConta: false };

    /// <summary>Usado nos pontos de entrada (transmitir, assistir, filme, código). Devolve verdadeiro se travou.</summary>
    bool ContaBloqueia()
    {
        if (!EntradaObrigatoria) return false;
        _stageView = _settingsView = _filmeView = _gruposView = _novidadesView = _mumuView = _apoiarView = _preparationView = false;
        ApplyView();
        Notify("Entre na sua conta para usar.");
        return true;
    }

    /// <summary>Fim do StartRoom: pergunta ao servidor sobre contas. Sem conta guardada a tela de entrada já aparece (travada, sem sala) enquanto a resposta não vem.</summary>
    void IniciarConta(string serverUrl)
    {
        if (Automated && !TestConta) return;
        if (TestConta) CarregarContaDeTeste();
        _conta = new Conta(serverUrl, _state, SalvarConta);
        _conta.Changed += () => Ui(() => { AtualizarContaUi(); AplicarNomeDaConta(); }); // MainForm.Nome.cs: o nome é da conta
        _conta.ApelidoAtual = () => _apelidoAntigo ?? ""; // só vai ao servidor enquanto a conta não tem nome (migração)
        _conta.IdsDoAparelho = IdsConta.Atuais; // spec 029, 8.2: pc e hw no login e no renovar, com ou sem diagnóstico; o hw chega quando o WMI acabar
        if (!Automated) IdsConta.IniciarHardware(); // em teste automático não há WMI (a telemetria também não sobe)
        _conta.NomeAdotadoNaMigracao += nome => Ui(() => AvisarNomeAdotado(nome));
        var c = _conta;
        RelayConfig.Token = async () => c.TemConta ? await c.AcessoAsync() : null; // relay, ffmpeg e mpv só atendem quem tem conta
        _contaTimer.Tick += (_, _) => _ = VerificarContaAsync();
        _contaTimer.Start();
        _contaRetry.Tick += (_, _) => { _contaRetry.Stop(); _ = VerificarContaAsync(); };
        if (!c.TemConta) MostrarEntrada(true);
        _ = VerificarContaAsync();
    }

    void TeleConta(string tipo, JsonObject dados) { try { _teleSessao?.Evento(tipo, dados); } catch { } }

    /// <summary>Evento do login: vai para o arquivo do diagnóstico como sempre e, sem conta, também para a fila do envio sem credencial (só passo, ok e tipo do erro).</summary>
    void TeleLogin(string passo, bool ok, string? erro = null)
    {
        var d = new JsonObject { ["passo"] = passo, ["ok"] = ok };
        if (erro != null) d["erro"] = erro;
        TeleConta("login", d);
        if (_conta is not { TemConta: true }) LoginSemConta.Enfileirar(passo, ok, erro);
    }

    /// <summary>Token para o envio da telemetria (roda na thread "telemetria", que pode esperar a rede). Nulo = sem conta: só sobem os eventos do login (Telemetria/LoginSemConta.cs).</summary>
    string? TokenDaConta(bool renovar)
    {
        var c = _conta;
        if (c is not { TemConta: true }) return null;
        try { return c.AcessoAsync(renovar).GetAwaiter().GetResult(); } catch { return null; }
    }

    // ---- estado de teste (arquivo no lugar do estado.json)
    void CarregarContaDeTeste()
    {
        try
        {
            string p = TestArq("conta.json");
            if (!File.Exists(p)) return;
            var s = JsonSerializer.Deserialize<AppState>(File.ReadAllText(p));
            if (s == null) return;
            _state.ContaId = s.ContaId; _state.DispositivoId = s.DispositivoId; _state.ContaSegredoProtegido = s.ContaSegredoProtegido;
            _state.ContaMetodo = s.ContaMetodo; _state.ContaGoogleVinculado = s.ContaGoogleVinculado; _state.Grupos = s.Grupos;
            _state.ContaNome = s.ContaNome; _state.EscolhaDeNomeMostrada = s.EscolhaDeNomeMostrada;
        }
        catch (Exception e) { Log.Info("Conta de teste: não li o arquivo (" + e.GetType().Name + ")."); }
    }

    void SalvarConta()
    {
        if (TestConta)
        {
            try { File.WriteAllText(TestArq("conta.json"), JsonSerializer.Serialize(_state)); } catch { }
            return;
        }
        if (!Automated) _state.Save();
    }

    async Task VerificarContaAsync()
    {
        var conta = _conta;
        if (conta == null) return;
        var (est, _) = await conta.ConfigAsync();
        if (IsDisposed) return;
        Ui(() => _ = AplicarEstadoServidorAsync(est, null));
    }

    /// <summary>Decide o que a janela mostra a partir do que o servidor diz e do que o app guarda.</summary>
    async Task AplicarEstadoServidorAsync(EstadoServidor est, string? aviso)
    {
        var conta = _conta;
        if (conta == null) return;
        _contaServidor = EstadoServidor.Obrigatoria; // não há outro modo (spec 022)
        if (est == EstadoServidor.Indisponivel)
        {
            // sem rede, servidor fora ou 503: nunca vira um modo sem conta. Avisa o motivo provável e pergunta de novo em instantes.
            if (!_contaServidorForaLogado) { _contaServidorForaLogado = true; Log.Info("Conta: o servidor do grupo não respondeu sobre contas. Pergunto de novo sozinho."); }
            _contaRetry.Stop(); _contaRetry.Start();
            if (!conta.TemConta)
            {
                if (_salaDeConta) VoltarParaSalaVazia();
                MostrarEntrada(true);
                _entradaComAvisoDoServidor = true;
                _entrada!.MostrarErro(aviso ?? AvisoServidorFora);
                return;
            }
            // com a conta guardada segue abaixo: renovar a sessão e abrir a sala não dependem de a pergunta sobre contas ter voltado
        }
        else
        {
            _contaServidorForaLogado = false;
            if (_entradaComAvisoDoServidor) { _entradaComAvisoDoServidor = false; _entrada?.Passo(1); } // tira o aviso de servidor fora
        }
        AdicionarAjustesConta();
        TeleConta("sessao_conta", new JsonObject { ["servidor"] = est.ToString(), ["tem_conta"] = conta.TemConta, ["metodo"] = conta.Metodo }); // sem e-mail, UID nem pedido
        if (conta.TemConta)
        {
            var s = await conta.GarantirAsync();
            if (IsDisposed) return;
            if (s == EstadoSessao.Ok) { MostrarEntrada(false); TrocarParaSalaDeConta(); AtualizarContaUi(); AplicarNomeDaConta(); ConferirEscolhaDeNome(); if (_convitePendente != null) _ = ProcessarConvitePendenteAsync(); return; }
            if (s == EstadoSessao.SemRede) { AtualizarContaUi(); _contaRetry.Stop(); _contaRetry.Start(); return; } // mantém o que está e pergunta de novo em instantes
            aviso ??= _contaExcluindo ? "Sua conta foi excluída." : Conta.FraseSessaoEncerrada; // Encerrada: a conta local já foi apagada
        }
        if (_salaDeConta) VoltarParaSalaVazia();
        AtualizarContaUi();
        if (TestConta && !TesteSemLogin && !_testeEntradaIniciada) { _testeEntradaIniciada = true; _ = IniciarEntradaUiAsync(); } // no teste ninguém clica em "Entrar"
        MostrarEntrada(true);
        if (aviso != null) _entrada!.MostrarErro(aviso);
    }

    // ---- salas
    // Com conta, o app fica em TODOS os grupos da pessoa: uma sala por grupo, vistas como uma só (Net/SalaUnida.cs). O que decide quais salas existem é a lista de
    // grupos que o servidor deu à conta (Conta.Grupos); criar, entrar e sair (MainForm.Grupos.cs) só mudam essa lista e chamam SincronizarSalas.
    SalaUnida? _salaUnida;

    void TrocarParaSalaDeConta()
    {
        if (_conta is not { TemConta: true } conta || _roomServerUrl == null || conta.ContaId == null) return;
        if (conta.Grupos.Count == 0 && !_salaDeConta)
        {
            _groupStatus.Text = "Você ainda não está em nenhum grupo. Crie um ou entre com o convite de um amigo em Grupos, na barra lateral.";
            _contaSemGrupoNoTeste = true; // RunRoomTestAsync: o cenário falha logo em vez de esperar a sala que não vem
            AtualizarFiltroGrupos();
            AbrirGruposSemGrupo();
            return;
        }
        if (!_salaDeConta)
        {
            var unida = new SalaUnida(conta.ContaId, Nick);
            unida.GrupoEncerrado += (gid, motivo) => Ui(() => AoGrupoEncerrado(gid, motivo));
            unida.Encerrada += motivo => Ui(() => AoSessaoEncerrada(motivo));
            _salaUnida = unida;
            TrocarSala(unida);
            _salaDeConta = true;
        }
        SincronizarSalas();
    }

    bool _gruposAbertosSemGrupo;

    /// <summary>
    /// Sem grupo não há nada para fazer no Início: a pessoa caía nele lendo "vá em Configurações > Grupos". Abre a tela Grupos uma vez por sessão;
    /// a checagem da conta repete a cada 10 min e não pode tirar a pessoa de onde ela foi depois.
    /// </summary>
    void AbrirGruposSemGrupo()
    {
        if (_gruposAbertosSemGrupo || _gruposTela == null) return;
        _gruposAbertosSemGrupo = true;
        MostrarGrupos();
    }

    /// <summary>Faz as salas abertas ficarem iguais à lista de grupos da conta: abre as que faltam, fecha as que sobram, renomeia. As outras não caem.</summary>
    void SincronizarSalas()
    {
        if (_salaUnida == null) { TrocarParaSalaDeConta(); return; } // o primeiro grupo da pessoa (criou ou entrou): a sala nasce aqui
        if (_salaUnida is not { } unida || _conta is not { TemConta: true } conta || _roomServerUrl == null || conta.ContaId == null) return;
        var devem = conta.Grupos.ToList();
        foreach (var (id, _) in unida.Grupos) if (devem.All(g => g.Id != id)) unida.Remover(id);
        int abertas = 0;
        foreach (var g in devem)
        {
            if (unida.Tem(g.Id))
            {
                if (unida.Grupos.Any(x => x.Id == g.Id && x.Nome != g.Nome)) unida.Renomear(g.Id, g.Nome);
                continue;
            }
            unida.Adicionar(g.Id, g.Nome, new GroupRoom(GroupRoom.RoomUrl(_roomServerUrl, g.Id), conta.ContaId, Nick, forcar => conta.AcessoAsync(forcar)));
            abertas++;
        }
        _contaGrupoId = devem.FirstOrDefault()?.Id;
        if (abertas > 0) TeleConta("sala_conta", new JsonObject { ["grupo"] = _contaGrupoId, ["grupos"] = devem.Count });
        AtualizarFiltroGrupos();
        AtualizarGruposUi();
        OnRoomChanged();
    }

    /// <summary>A conta acabou (saiu, foi excluída, sessão encerrada): fecha as salas e fica a lista vazia, que não conecta a nada.</summary>
    void VoltarParaSalaVazia()
    {
        _salaDeConta = false; _contaGrupoId = null; _salaUnida = null;
        TrocarSala(new SalaUnida("", Nick));
        AtualizarFiltroGrupos();
        AtualizarGruposUi();
    }

    /// <summary>Troca a sala em uso: a nova assume os eventos e a live atual é anunciada de novo (a sala nova não sabe dela).</summary>
    void TrocarSala(ISala nova)
    {
        var velha = _room;
        _room = nova;
        RecomecarAvataresDaSala(); // MainForm.AvatarRede.cs
        LigarSala(nova);
        (velha as IDisposable)?.Dispose();
        UpdateLive(force: true);
        OnRoomChanged();
    }

    void AoSessaoEncerrada(string motivo)
    {
        if (!_salaDeConta) return;
        if (motivo == "sessao") _ = AplicarEstadoServidorAsync(_contaServidor, Conta.FraseSessaoEncerrada);
    }

    /// <summary>Removido de um grupo (ou saiu por outro PC): a sala dele já foi tirada; avisa e confere a lista no servidor.</summary>
    void AoGrupoEncerrado(string gid, string motivo)
    {
        if (!_salaDeConta) return;
        string nome = _conta?.Grupos.FirstOrDefault(g => g.Id == gid)?.Nome ?? "";
        string quem = string.IsNullOrWhiteSpace(nome) ? "do grupo" : $"do grupo {nome}";
        Notify(motivo == "removido" ? $"Você foi removido {quem}." : $"Você saiu {quem}.");
        _ = AplicarEstadoServidorAsync(_contaServidor, null); // renova a sessão e a lista de grupos; SincronizarSalas fecha o resto
    }

    // ---- tela de entrada e faixa
    /// <summary>A tela de entrada cobre a janela inteira (barra lateral incluída), por isso é filha direta do formulário e sem encaixe: o encaixe da janela depende da ordem dos painéis.</summary>
    void GarantirEntrada()
    {
        if (_entrada != null) return;
        _entrada = new EntradaView { Visible = false, Bounds = ClientRectangle };
        _entrada.PedirEntrada += () => _ = IniciarEntradaUiAsync();
        _entrada.CodigoConfirmado += c => _ = ConcluirEntradaUiAsync(c);
        _entrada.Cancelado += () => { if (!EntradaObrigatoria) MostrarEntrada(false); };
        _entrada.AbrirPrivacidade += () => Conta.AbrirNavegador(UrlPrivacidade);
        Controls.Add(_entrada);
        SizeChanged += (_, _) => AjustarEntrada();
    }

    void AjustarEntrada()
    {
        if (_entrada is { Visible: true } && WindowState != FormWindowState.Minimized) _entrada.Bounds = ClientRectangle;
    }

    void MostrarEntrada(bool mostrar)
    {
        if (!mostrar && _entrada == null) return;
        GarantirEntrada();
        _entrada!.PodeFechar = false; // a conta é obrigatória: não dá para fechar a tela de entrada sem entrar
        if (mostrar) { AumentarParaEntrada(); _entrada.Bounds = ClientRectangle; _entrada.BringToFront(); }
        _entrada.Visible = mostrar;
    }

    bool _entradaAumentada;

    /// <summary>
    /// No tamanho de sempre (990 × 660) a tela de entrada abria só com o cartão: a Mumu precisa de janela mais larga (EntradaMedidas.LarguraComMascote),
    /// e ela é a primeira impressão do app. Cresce uma vez para 1280 × 800 na escala do monitor, sem passar da área útil, e não encolhe depois do login.
    /// </summary>
    void AumentarParaEntrada()
    {
        if (_entradaAumentada || Automated || WindowState != FormWindowState.Normal) return; // teste automático escolhe o tamanho (STREEMU_TEST_SIZE)
        _entradaAumentada = true;
        var area = Screen.FromRectangle(Bounds).WorkingArea; // FromControl criaria o handle: isto roda no construtor, antes de a janela aparecer
        double k = DeviceDpi / 96.0;
        int w = Math.Max(Width, Math.Min((int)(1280 * k), area.Width - 40)), h = Math.Max(Height, Math.Min((int)(800 * k), area.Height - 40));
        if (w == Width && h == Height) return;
        Bounds = new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
    }

    async Task IniciarEntradaUiAsync()
    {
        var conta = _conta; if (conta == null) return;
        GarantirEntrada();
        _entrada!.Ocupado(true);
        try
        {
            var r = await conta.IniciarEntradaAsync();
            TeleLogin("inicio", r.Ok);
            if (!r.Ok || r.Url == null) { _entrada.MostrarErro(r.Texto ?? "Não consegui começar a entrada. Tente de novo."); return; }
            if (TestConta)
            {
                File.WriteAllText(TestArq("pedido.txt"), r.Url);
                _ = EsperarCodigoDeTesteAsync();
            }
            else if (!Conta.AbrirNavegador(r.Url)) { _entrada.MostrarEndereco(r.Url); _entrada.MostrarErro("Não consegui abrir o navegador. Copie o endereço abaixo e cole no navegador."); }
            _entrada.Passo(2);
        }
        finally { _entrada.Ocupado(false); }
    }

    async Task EsperarCodigoDeTesteAsync()
    {
        string p = TestArq("codigo.txt");
        for (int i = 0; i < 240; i++)
        {
            await Task.Delay(500);
            if (!File.Exists(p)) continue;
            string c = "";
            try { c = File.ReadAllText(p).Trim(); File.Delete(p); } catch { continue; }
            Ui(() => _ = ConcluirEntradaUiAsync(c));
            return;
        }
    }

    async Task ConcluirEntradaUiAsync(string codigo)
    {
        var conta = _conta; if (conta == null || _entrada == null) return;
        _entrada.Ocupado(true);
        try
        {
            var r = await conta.ConcluirEntradaAsync(codigo, _apelidoAntigo ?? "");
            TeleLogin("codigo", r.Ok, r.Erro); // só o tipo do erro, nunca o código
            if (!r.Ok) { _entrada.MostrarErro(r.Texto ?? "O servidor não aceitou o código. Tente de novo."); return; }
            _entrada.Passo(1);
            MostrarEntrada(false);
            await AplicarEstadoServidorAsync(_contaServidor, null);
        }
        finally { _entrada.Ocupado(false); }
    }

    // ---- Ajustes > Perfil > conta (só aparece quando o servidor tem contas). A tela é a ContaAcoesView; aqui só se liga cada botão ao que ele faz.
    void AdicionarAjustesConta()
    {
        if (_contaAjustesAdicionados) return;
        _contaAjustesAdicionados = true;
        _contaAcoes = new ContaAcoesView();
        _contaAcoes.PedirEntrar += () => { _settingsView = false; MostrarEntrada(true); ApplyView(); };
        _contaAcoes.PedirSairDestePc += async () => await SairDestePcAsync();
        _contaAcoes.PedirVincular += () => _ = RodarAcaoAsync(AcaoConta.Vincular);
        _contaAcoes.PedirSairDeTodos += () => { if (ConfirmarConta(ContaAcoesView.ConfirmaSairDeTodos, "Sair de todos os dispositivos", MessageBoxIcon.Question)) _ = RodarAcaoAsync(AcaoConta.SairDeTodos); };
        _contaAcoes.PedirExcluir += () => { if (ConfirmarConta(ContaAcoesView.ConfirmaExcluir, "Excluir minha conta", MessageBoxIcon.Warning)) _ = RodarAcaoAsync(AcaoConta.Excluir); };
        _contaAcoes.Cancelado += () => { _acaoCts?.Cancel(); _conta?.CancelarAcao(); };
        // a conta mora no Perfil (quem sou eu): uma linha a mais na página, com a mesma margem e âncora das outras (SettingsSection)
        if (_paginaPerfil != null)
        {
            _contaAcoes.Margin = new Padding(0, 0, 0, 14);
            _contaAcoes.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            _paginaPerfil.Controls.Add(_contaAcoes, 0, _paginaPerfil.Controls.Count);
            _settingsPage?.RelayoutPage();
        }
        AtualizarContaUi();
        AdicionarAjustesGrupos(); // MainForm.Grupos.cs
    }

    void AtualizarContaUi()
    {
        if (_contaAcoes == null || _conta == null) return;
        _contaAcoes.Atualizar(_conta.TemConta, _conta.Metodo, _conta.GoogleVinculado);
    }

    bool ConfirmarConta(string texto, string titulo, MessageBoxIcon icone) =>
        MessageBox.Show(this, texto, titulo, MessageBoxButtons.YesNo, icone, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    // Textos da espera e do fim de cada ação (frases curtas: o motivo e o próximo passo)
    static string TextoEspera(AcaoConta a) => a switch
    {
        AcaoConta.Vincular => "Termine no navegador: entre com e-mail e senha e escolha o Google a vincular. Quando acabar, esta tela avisa.",
        AcaoConta.SairDeTodos => "Termine no navegador: entre na conta para confirmar. Quando acabar, esta tela avisa.",
        _ => "Termine no navegador: entre na conta e digite EXCLUIR. Só depois disso a conta é apagada.",
    };
    static string TextoFim(AcaoConta a) => a switch
    {
        AcaoConta.Vincular => "Google vinculado. Agora você entra por ele ou por e-mail e senha, na mesma conta.",
        AcaoConta.SairDeTodos => "Pronto: os outros computadores foram desconectados. Este continua entrado.",
        _ => "Conta excluída.",
    };

    /// <summary>Vincular, sair de todos ou excluir: pede o pedido, abre o navegador e espera a página terminar. Sem código a digitar.</summary>
    async Task RodarAcaoAsync(AcaoConta acao)
    {
        var conta = _conta; var tela = _contaAcoes;
        if (conta == null || tela == null || _acaoCts != null) return;
        _acaoCts = new CancellationTokenSource();
        var cts = _acaoCts;
        string nome = acao.ToString();
        try
        {
            tela.Esperando("Preparando…");
            var r = await conta.IniciarAcaoAsync(acao);
            TeleConta("conta_acao", new JsonObject { ["acao"] = nome, ["passo"] = r.Ok ? "iniciou" : "falhou" }); // sem e-mail, UID nem pedido
            if (!r.Ok || r.Url == null)
            {
                tela.Livre(r.Texto ?? "Não consegui começar. Tente de novo.", erro: true);
                if (!conta.TemConta) await AplicarEstadoServidorAsync(_contaServidor, r.Texto);
                return;
            }
            if (TestConta) File.WriteAllText(TestArq("acao.txt"), nome.ToLowerInvariant() + "\n" + r.Url); // o ajudante dos testes faz o papel da página
            else if (!Conta.AbrirNavegador(r.Url)) { tela.Livre("Não consegui abrir o navegador. Copie este endereço e cole nele: " + r.Url, erro: true); return; }
            tela.Esperando(TextoEspera(acao));
            if (acao == AcaoConta.Excluir) { _contaExcluindo = true; _testeEntradaIniciada = true; } // no teste, depois de excluir ninguém entra de novo sozinho
            var fim = await conta.AguardarAcaoAsync(cts.Token);
            TeleConta("conta_acao", new JsonObject { ["acao"] = nome, ["passo"] = fim.Ok ? "concluiu" : fim.Erro == "cancelado" ? "cancelou" : "falhou" });
            if (!fim.Ok)
            {
                _contaExcluindo = false;
                tela.Livre(fim.Erro == "cancelado" ? null : fim.Texto, erro: fim.Erro != "cancelado");
                return;
            }
            tela.Livre(acao == AcaoConta.Excluir ? null : TextoFim(acao));
            if (acao == AcaoConta.Excluir)
            {
                // as credenciais locais já foram apagadas pelo Conta; a sala cai e a tela de entrada volta
                await AplicarEstadoServidorAsync(_contaServidor, TextoFim(acao));
                _contaExcluindo = false;
            }
            else AtualizarContaUi();
        }
        finally { _acaoCts = null; cts.Dispose(); }
    }

    async Task SairDestePcAsync()
    {
        if (_conta == null) return;
        if (MessageBox.Show(this, "Sair da conta neste PC? Você precisará entrar de novo para transmitir e assistir.", "Sair deste PC", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        await _conta.SairAsync();
        await AplicarEstadoServidorAsync(_contaServidor, null);
    }
}
