using Streemu.Media;
using Streemu.Ui;
using Streemu.Util;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Streemu;

/// <summary>
/// Oferta de "usar 30 FPS" quando a live em 60 chega com poucos quadros (ver <see cref="SugestaoDe30Fps"/>). Aceitar troca só o preset da live em andamento,
/// sem gravar a escolha em <c>_state</c>: a próxima live volta a começar no que a pessoa configurou, e a oferta só reaparece se a captura de novo ficar abaixo.
/// Onde aparece: com a janela do Streemu na frente, o card dela; com o jogo na frente, o mesmo card por cima do jogo (<see cref="SugestaoFpsOverlay"/>),
/// que dá para aceitar com Ctrl+Alt+F3 (sai em 15 s; "Não mostrar no jogo" ou Ajustes > Aparência > Avançados desligam o aviso sobre o jogo); em tela cheia exclusiva, onde o Windows não deixa nada por cima, só o balão da bandeja.
/// </summary>
public sealed partial class MainForm
{
    const int TeclaDoAtalhoDaOferta = 0x72; // F3: Ctrl+Alt+F3 (Ctrl+Alt+3 é AltGr+3 no ABNT2 e digitaria "³" no chat do jogo)
    const string NomeDoAtalhoDaOferta = "Ctrl+Alt+F3";
    const int QunsRunningD3dFullScreen = 3;
    readonly SugestaoDe30Fps _sugestaoFps = new();
    SugestaoFpsCard? _sugestaoCard;
    SugestaoFpsOverlay? _ofertaSobreOJogo;
    AtalhoGlobal? _atalhoDaOferta;
    bool _mudandoOfertaSobreOJogo;
    ToggleSwitch? _ofertaNoJogoTransmitir; // o mesmo interruptor dentro de Avançados na tela Transmitir (refeito junto com ela)
    const string FraseAvisoDesligado = "Aviso desligado. Para religar: Configurações > Aparência > Avançados.";

    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

    void LigarSugestao30Fps()
    {
        _sender.SecondMeasured += (novos, _, medida) => Ui(() => ObservarSugestao30Fps((int)Math.Clamp(novos, -1, 1000), medida));
        _sender.Stopped += () => Ui(() => { _sugestaoFps.Reiniciar(); AtualizarSugestaoCard(); });
        SizeChanged += (_, _) => PosicionarSugestaoCard();
        Activated += (_, _) => FecharOfertaSobreOJogo(true); // a janela veio para a frente: o card dela já está à vista
        FormClosed += (_, _) => { FecharOfertaSobreOJogo(true); _ofertaSobreOJogo?.Dispose(); };
    }

    void ObservarSugestao30Fps(int novos, string medida)
    {
        int fps = _sender.Running ? _sender.Current?.Preset.Fps ?? 0 : 0;
        if (_sugestaoFps.Observar(fps, novos, medida)) AtualizarSugestaoCard();
        else if (_sugestaoFps.Visivel) _sugestaoCard?.Mostrar(_sugestaoFps.Tipico);
    }

    void AtualizarSugestaoCard()
    {
        if (!_sugestaoFps.Visivel)
        {
            if (_sugestaoCard != null) _sugestaoCard.Visible = false;
            FecharOfertaSobreOJogo();
            return;
        }
        GarantirSugestaoCard().Mostrar(_sugestaoFps.Tipico);
        PosicionarSugestaoCard();
        _sugestaoCard!.Visible = true;
        _sugestaoCard.BringToFront();
        Log.Info($"[qualidade] oferta de 30 FPS: a captura chega a ~{_sugestaoFps.Tipico} quadros/s com a live em 60 FPS.");
        string onde = AvisarOfertaForaDaJanela();
        _teleSessao?.Evento("sugestao_fps", new JsonObject { ["acao"] = "mostrou", ["quadros_s"] = _sugestaoFps.Tipico, ["onde"] = onde });
    }

    /// <summary>
    /// A janela do Streemu não está na frente (a pessoa está jogando): o card dela não seria visto (campo 2026-11-23: o evento "mostrou" saiu e ninguém viu nada).
    /// Mostra o mesmo card por cima do jogo; se o Windows não deixa (tela cheia exclusiva) ou a janela não pode ficar fora da captura, cai no balão da bandeja,
    /// cujo clique abre a janela do Streemu, onde o card espera.
    /// </summary>
    string AvisarOfertaForaDaJanela()
    {
        // o card da janela só é visto com a janela principal ativa; com o player, o Transmitir ou um diálogo na frente ele fica escondido
        if (ReferenceEquals(Form.ActiveForm, this) && WindowState != FormWindowState.Minimized) return "janela";
        bool teste = Environment.GetEnvironmentVariable("STREEMU_TEST_SELO") is "1" or "visivel";
        if (Automated && !teste) return "teste";
        if (_state.OfertaFpsSobreOJogoDesligada) return "desligado";
        if (ModoSilencio.Ativo) { Log.Info("[qualidade] oferta de 30 FPS só no card da janela: os avisos estão silenciados."); return "silencio"; }
        if (JogoEmTelaCheiaExclusiva()) Log.Info("[qualidade] o jogo está em tela cheia exclusiva: o Windows não deixa a oferta aparecer por cima dele; aviso pela bandeja.");
        else if (MostrarOfertaSobreOJogo()) return "jogo";
        MostrarBalao(8000, UiBrand.Name, "Sua live está abaixo de 60 FPS. Clique aqui e escolha 30 FPS para ficar mais estável.", ToolTipIcon.Info);
        return "bandeja";
    }

    bool MostrarOfertaSobreOJogo()
    {
        if (_sender.Current is not { } atual) return false;
        if (_ofertaSobreOJogo == null)
        {
            _ofertaSobreOJogo = new SugestaoFpsOverlay();
            _ofertaSobreOJogo.Aceitou += AceitarSugestao30Fps;
            _ofertaSobreOJogo.Recusou += RecusarSugestao30Fps;
            _ofertaSobreOJogo.Expirou += () => { FecharOfertaSobreOJogo(); _teleSessao?.Evento("sugestao_fps", new JsonObject { ["acao"] = "expirou", ["quadros_s"] = _sugestaoFps.Tipico }); }; // sumiu sozinha: não é recusa, o card da janela continua esperando
            _ofertaSobreOJogo.Desligou += DesligarOfertaSobreOJogo;
        }
        _atalhoDaOferta ??= new AtalhoGlobal(TeclaDoAtalhoDaOferta, AceitarSugestao30Fps);
        string? dica = _atalhoDaOferta.Registrado ? $"{NomeDoAtalhoDaOferta} troca para 30." : null;
        if (_ofertaSobreOJogo.Mostrar(MonitorBounds(atual.Source), _sugestaoFps.Tipico, dica)) return true;
        FecharOfertaSobreOJogo();
        return false;
    }

    /// <summary>"Não mostrar no jogo": o aviso diz por uns segundos como religar (a pessoa está jogando e não vê outra confirmação) e a opção é gravada como a de Aparência. Não é recusa: o card da janela continua.</summary>
    void DesligarOfertaSobreOJogo()
    {
        _ofertaSobreOJogo?.MostrarConfirmacao(FraseAvisoDesligado, TimeSpan.FromSeconds(3));
        MudarOfertaSobreOJogo(false, "aviso");
        Notify(FraseAvisoDesligado);
    }

    /// <summary>
    /// Único caminho que liga ou desliga o aviso sobre o jogo, chamado pelo aviso, por Aparência e pela tela Transmitir: grava, deixa os dois
    /// interruptores iguais (sem laço), tira o aviso da tela e conta na telemetria de onde veio a mudança ("aviso", "aparencia" ou "transmitir").
    /// </summary>
    void MudarOfertaSobreOJogo(bool ligado, string origem)
    {
        if (_mudandoOfertaSobreOJogo || _state.OfertaFpsSobreOJogoDesligada == !ligado) return;
        _mudandoOfertaSobreOJogo = true;
        try
        {
            _state.OfertaFpsSobreOJogoDesligada = !ligado;
            if (!Automated) _state.Save();
            _ofertaNoJogoOn.Checked = ligado;
            if (_ofertaNoJogoTransmitir is { IsDisposed: false } outro) outro.Checked = ligado;
            if (!ligado) FecharOfertaSobreOJogo();
            Log.Info($"[qualidade] a pessoa {(ligado ? "religou" : "desligou")} o aviso de 30 FPS por cima do jogo (origem: {origem}).");
            _teleSessao?.Evento("sugestao_fps", new JsonObject { ["acao"] = ligado ? "religou" : "desligou", ["origem"] = origem });
        }
        finally { _mudandoOfertaSobreOJogo = false; }
    }

    /// <summary>O mesmo interruptor de Aparência, para a seção Avançados da tela Transmitir (cada preparação cria o seu).</summary>
    ToggleSwitch NovoInterruptorDaOfertaNaTransmissao()
    {
        var interruptor = new ToggleSwitch { Text = _ofertaNoJogoOn.Text, AutoSize = true, Checked = !_state.OfertaFpsSobreOJogoDesligada };
        interruptor.CheckedChanged += (_, _) => MudarOfertaSobreOJogo(interruptor.Checked, "transmitir");
        _ofertaNoJogoTransmitir = interruptor;
        return interruptor;
    }

    /// <summary>
    /// Esconde a oferta sobre o jogo e solta o atalho (a combinação só fica presa enquanto o aviso está na tela). Uma frase de retorno que ainda
    /// está aparecendo termina sozinha, a não ser que <paramref name="forcar"/>.
    /// </summary>
    void FecharOfertaSobreOJogo(bool forcar = false)
    {
        if (forcar || _ofertaSobreOJogo is not { EmMensagem: true }) _ofertaSobreOJogo?.Esconder();
        _atalhoDaOferta?.Dispose();
        _atalhoDaOferta = null;
    }

    static bool JogoEmTelaCheiaExclusiva() => SHQueryUserNotificationState(out int estado) == 0 && estado == QunsRunningD3dFullScreen;

    /// <summary>Filho direto do formulário e sem encaixe (como a tela de entrada): o encaixe da janela depende da ordem dos painéis.</summary>
    SugestaoFpsCard GarantirSugestaoCard()
    {
        if (_sugestaoCard != null) return _sugestaoCard;
        _sugestaoCard = new SugestaoFpsCard { Visible = false };
        _sugestaoCard.Aceitou += AceitarSugestao30Fps;
        _sugestaoCard.Recusou += RecusarSugestao30Fps;
        Controls.Add(_sugestaoCard);
        return _sugestaoCard;
    }

    void PosicionarSugestaoCard()
    {
        // sem exigir Visible: a primeira vez posiciona ANTES de mostrar, e com a exigência o card caía em (0,0), sobre a barra lateral
        if (_sugestaoCard is not { } card || WindowState == FormWindowState.Minimized) return;
        card.Location = new Point(Math.Max(0, ClientSize.Width - card.Width - UiScale.Dp(this, 16)), UiScale.Dp(this, 12));
    }

    void AceitarSugestao30Fps()
    {
        int quadros = _sugestaoFps.Tipico;
        if (_sender.Running && _sender.Current is { Preset.Fps: 60 }) _ofertaSobreOJogo?.MostrarConfirmacao("Live em 30 FPS.", TimeSpan.FromSeconds(2)); // quem aceitou pelo atalho, jogando, não vê mais nada mudar
        _sugestaoFps.Esconder();
        AtualizarSugestaoCard();
        if (!_sender.Running || _sender.Current is not { Preset.Fps: 60 } atual) return;
        var em30 = atual.Preset.WithFps(30);
        Log.Info($"Qualidade: {atual.Preset.Short} → {em30.Short} (você aceitou a oferta; a captura chegava a ~{quadros} quadros/s).");
        _teleSessao?.Evento("sugestao_fps", new JsonObject { ["acao"] = "aceitou", ["quadros_s"] = quadros });
        _sender.Reconfigure(atual with { Preset = em30 });
    }

    void RecusarSugestao30Fps()
    {
        _sugestaoFps.Recusar();
        AtualizarSugestaoCard();
        _teleSessao?.Evento("sugestao_fps", new JsonObject { ["acao"] = "recusou", ["quadros_s"] = _sugestaoFps.Tipico });
    }
}
