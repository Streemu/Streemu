using Streemu.Avisos;
using Streemu.Ui;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// Sino de silenciar as notificações (spec 012, parte B). Todo balão da bandeja, som e a faixa de atualização passam por
/// <see cref="MostrarBalao"/>, <see cref="TocarSom"/> e <see cref="FaixaDeAtualizacaoVisivel"/>: um aviso novo que use esses três já respeita o silêncio.
/// O que fica na tela (contadores, cards de convite, o sino) nunca é calado. A atualização automática não depende disto.
/// </summary>
public sealed partial class MainForm
{
    Silencio? _silencio;
    string _ultimoSino = "";

    // Criado sob demanda com o que o estado.json guardou (ver SilencioMudou).
    Silencio ModoSilencio => _silencio ??= CriarSilencio();

    Silencio CriarSilencio()
    {
        DateTime? ate = _state.SilencioAteUnixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(_state.SilencioAteUnixMs).LocalDateTime : null;
        var silencio = new Silencio(_state.SilencioIndefinido, ate);
        silencio.Mudou += SilencioMudou;
        return silencio;
    }

    void SilencioMudou()
    {
        _state.SilencioIndefinido = ModoSilencio.Indefinido;
        _state.SilencioAteUnixMs = ModoSilencio.Ate is { } ate ? new DateTimeOffset(ate).ToUnixTimeMilliseconds() : 0;
        if (!Automated) _state.Save();
        Log.Info(ModoSilencio.Ativo ? $"Notificações silenciadas {ModoSilencio.Descricao()}." : "Notificações ligadas de novo.");
        SinalSilencio();
    }

    /// <summary>O balão da bandeja, se as notificações estão ligadas.</summary>
    void MostrarBalao(int milissegundos, string titulo, string texto, ToolTipIcon icone)
    {
        if (_tray == null) return;
        if (ModoSilencio.Ativo) { Log.Info($"[silêncio] balão calado: {texto}"); return; }
        _tray.ShowBalloonTip(milissegundos, titulo, texto, icone);
    }

    /// <summary>Os sons de aviso (live abriu, alguém entrou...): calados no teste automático e no silêncio.</summary>
    void TocarSom(Sons.Som som) => Sons.Tocar(som, mudo: Automated || ModoSilencio.Ativo);

    /// <summary>A faixa azul "Nova versão" é um alerta: some enquanto silenciado (o botão ↻ da barra continua).</summary>
    bool FaixaDeAtualizacaoVisivel => _update != null && !ModoSilencio.Ativo;

    /// <summary>Uma atualização marcada como obrigatória acende um ponto no sino enquanto o alerta está calado.</summary>
    bool PontoNoSino => ModoSilencio.Ativo && _update is { Obrigatoria: true };

    /// <summary>A cada segundo e a cada mudança: põe o sino e a faixa de atualização de acordo com o estado (inclusive quando o prazo acaba sozinho).</summary>
    void SinalSilencio(bool forcar = false)
    {
        bool silenciado = ModoSilencio.Ativo;
        string dica = DicaDoSino(silenciado);
        string assinatura = $"{silenciado}|{dica}|{PontoNoSino}|{FaixaDeAtualizacaoVisivel}";
        if (!forcar && assinatura == _ultimoSino) return;
        _ultimoSino = assinatura;
        string texto = silenciado ? "Silenciadas " + ModoSilencio.Descricao().Replace("até você ligar de novo", "").Trim() : "Notificações";
        _side.SetSino(silenciado, texto.Trim(), dica, PontoNoSino);
        _flyout?.Panel.SetSino(silenciado, texto.Trim(), dica, PontoNoSino);
        if (!_updating) _updateBar.Visible = FaixaDeAtualizacaoVisivel;
    }

    string DicaDoSino(bool silenciado)
    {
        if (!silenciado) return "Notificações ligadas. Clique para silenciar por um tempo.";
        string dica = $"Notificações silenciadas {ModoSilencio.Descricao()}. Clique para ligar de novo ou mudar o prazo.";
        return PontoNoSino ? dica + " Tem uma versão nova obrigatória: clique em Atualizar agora." : dica;
    }

    /// <summary>O menu do sino, aberto no canto do item da barra: ligar de novo (se silenciado) e os prazos.</summary>
    void MostrarMenuSino(Rectangle itemNaTela)
    {
        var menu = new ContextMenuStrip();
        TrayVisuals.Style(menu);
        if (ModoSilencio.Ativo)
        {
            menu.Items.Add("Ligar as notificações", null, (_, _) => ModoSilencio.Ligar());
            menu.Items.Add(new ToolStripSeparator());
        }
        menu.Items.Add(new ToolStripLabel("Silenciar por") { ForeColor = Theme.TextMuted });
        foreach (var prazo in Silencio.Prazos)
            menu.Items.Add(prazo.Texto, null, (_, _) => ModoSilencio.Silenciar(prazo.Duracao));
        menu.Closed += (_, _) => menu.BeginInvoke(menu.Dispose);
        SegurarBarraFlutuante(menu); // MainForm.Janela.cs: a barra flutuante não abre por cima do menu
        menu.Show(new Point(itemNaTela.Right, itemNaTela.Top));
    }
}
