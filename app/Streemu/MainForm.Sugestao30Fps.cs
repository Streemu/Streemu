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
/// que dá para aceitar com Ctrl+Alt+3; em tela cheia exclusiva, onde o Windows não deixa nada por cima, só o balão da bandeja.
/// </summary>
public sealed partial class MainForm
{
    const int TeclaDoAtalhoDaOferta = 0x33; // "3": Ctrl+Alt+3
    const string NomeDoAtalhoDaOferta = "Ctrl+Alt+3";
    const int QunsRunningD3dFullScreen = 3;
    readonly SugestaoDe30Fps _sugestaoFps = new();
    SugestaoFpsCard? _sugestaoCard;
    SugestaoFpsOverlay? _ofertaSobreOJogo;
    AtalhoGlobal? _atalhoDaOferta;

    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

    void LigarSugestao30Fps()
    {
        _sender.SecondMeasured += (novos, _, medida) => Ui(() => ObservarSugestao30Fps((int)Math.Clamp(novos, -1, 1000), medida));
        _sender.Stopped += () => Ui(() => { _sugestaoFps.Reiniciar(); AtualizarSugestaoCard(); });
        SizeChanged += (_, _) => PosicionarSugestaoCard();
        FormClosed += (_, _) => { FecharOfertaSobreOJogo(); _ofertaSobreOJogo?.Dispose(); };
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
        _teleSessao?.Evento("sugestao_fps", new JsonObject { ["acao"] = "mostrou", ["quadros_s"] = _sugestaoFps.Tipico });
        AvisarOfertaForaDaJanela();
    }

    /// <summary>
    /// A janela do Streemu não está na frente (a pessoa está jogando): o card dela não seria visto (campo 2026-11-23: o evento "mostrou" saiu e ninguém viu nada).
    /// Mostra o mesmo card por cima do jogo; se o Windows não deixa (tela cheia exclusiva) ou a janela não pode ficar fora da captura, cai no balão da bandeja,
    /// cujo clique abre a janela do Streemu, onde o card espera.
    /// </summary>
    void AvisarOfertaForaDaJanela()
    {
        if (Form.ActiveForm != null) return;
        if (JogoEmTelaCheiaExclusiva()) Log.Info("[qualidade] o jogo está em tela cheia exclusiva: o Windows não deixa a oferta aparecer por cima dele; aviso pela bandeja.");
        else if (MostrarOfertaSobreOJogo()) return;
        MostrarBalao(8000, UiBrand.Name, "Sua live está abaixo de 60 FPS. Clique aqui e escolha 30 FPS para ficar mais estável.", ToolTipIcon.Info);
    }

    bool MostrarOfertaSobreOJogo()
    {
        if (_sender.Current is not { } atual) return false;
        if (_ofertaSobreOJogo == null)
        {
            _ofertaSobreOJogo = new SugestaoFpsOverlay();
            _ofertaSobreOJogo.Aceitou += AceitarSugestao30Fps;
            _ofertaSobreOJogo.Recusou += RecusarSugestao30Fps;
            _ofertaSobreOJogo.Expirou += FecharOfertaSobreOJogo; // sumiu sozinha: não é recusa, o card da janela continua esperando
        }
        _atalhoDaOferta ??= new AtalhoGlobal(TeclaDoAtalhoDaOferta, AceitarSugestao30Fps);
        string? dica = _atalhoDaOferta.Registrado ? $"Ou aperte {NomeDoAtalhoDaOferta}." : null;
        if (_ofertaSobreOJogo.Mostrar(MonitorBounds(atual.Source), _sugestaoFps.Tipico, dica)) return true;
        FecharOfertaSobreOJogo();
        return false;
    }

    /// <summary>Esconde a oferta sobre o jogo e solta o atalho (a combinação só fica presa enquanto o aviso está na tela).</summary>
    void FecharOfertaSobreOJogo()
    {
        _ofertaSobreOJogo?.Esconder();
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
