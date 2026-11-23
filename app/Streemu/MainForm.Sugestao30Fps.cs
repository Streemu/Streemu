using Streemu.Media;
using Streemu.Ui;
using Streemu.Util;
using System.Text.Json.Nodes;

namespace Streemu;

/// <summary>
/// Oferta de "usar 30 FPS" quando a live em 60 chega com poucos quadros (ver <see cref="SugestaoDe30Fps"/>). Aceitar troca só o preset da live em andamento,
/// sem gravar a escolha em <c>_state</c>: a próxima live volta a começar no que a pessoa configurou, e a oferta só reaparece se a captura de novo ficar abaixo.
/// </summary>
public sealed partial class MainForm
{
    readonly SugestaoDe30Fps _sugestaoFps = new();
    SugestaoFpsCard? _sugestaoCard;

    void LigarSugestao30Fps()
    {
        _sender.SecondMeasured += (novos, _, medida) => Ui(() => ObservarSugestao30Fps((int)Math.Clamp(novos, -1, 1000), medida));
        _sender.Stopped += () => Ui(() => { _sugestaoFps.Reiniciar(); AtualizarSugestaoCard(); });
        SizeChanged += (_, _) => PosicionarSugestaoCard();
    }

    void ObservarSugestao30Fps(int novos, string medida)
    {
        int fps = _sender.Running ? _sender.Current?.Preset.Fps ?? 0 : 0;
        if (_sugestaoFps.Observar(fps, novos, medida)) AtualizarSugestaoCard();
        else if (_sugestaoFps.Visivel) _sugestaoCard?.Mostrar(_sugestaoFps.Tipico);
    }

    void AtualizarSugestaoCard()
    {
        if (!_sugestaoFps.Visivel) { if (_sugestaoCard != null) _sugestaoCard.Visible = false; return; }
        GarantirSugestaoCard().Mostrar(_sugestaoFps.Tipico);
        PosicionarSugestaoCard();
        _sugestaoCard!.Visible = true;
        _sugestaoCard.BringToFront();
        _teleSessao?.Evento("sugestao_fps", new JsonObject { ["acao"] = "mostrou", ["quadros_s"] = _sugestaoFps.Tipico });
    }

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
        if (_sugestaoCard is not { Visible: true } card || WindowState == FormWindowState.Minimized) return;
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
