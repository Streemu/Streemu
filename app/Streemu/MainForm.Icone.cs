using Streemu.Ui;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// O ícone acompanha o que o app está fazendo (ocioso, assistindo, transmitindo, Cinema), na janela (barra de tarefas) e na bandeja ao mesmo tempo.
/// Fora do MainForm.cs para dar menos conflito.
/// </summary>
public partial class MainForm
{
    // Cada .ico é carregado uma vez e reaproveitado: trocar de ícone a cada tick criaria um handle GDI novo por vez.
    static readonly Dictionary<AtividadeIcone, Icon> _iconesJanela = new(), _iconesBandeja = new();

    static Icon IconeJanela(AtividadeIcone atividade)
    {
        if (!_iconesJanela.TryGetValue(atividade, out var icone)) _iconesJanela[atividade] = icone = UiBrand.CreateIcon(atividade);
        return icone;
    }

    static Icon IconeBandeja(AtividadeIcone atividade)
    {
        if (!_iconesBandeja.TryGetValue(atividade, out var icone)) _iconesBandeja[atividade] = icone = TrayVisuals.CreateIcon(atividade);
        return icone;
    }

    AtividadeIcone? _iconeAplicado;
    string? _iconeTexto;

    /// <summary>Minha live no ar: o mesmo critério da live que a sala, o Discord e a barra lateral usam (<see cref="UpdateLive"/>).</summary>
    bool MinhaLiveNoAr => _streamId != null && (_sender.Running || _fakeLive);

    /// <summary>
    /// Assistindo a live de outra pessoa: algum player de amigo aberto (uma ou várias). A prévia da minha própria live (<c>_myLive</c>) não conta;
    /// quem a vê está transmitindo, e isso já tem ícone.
    /// </summary>
    bool AssistindoLiveDeAmigo => _linkViewers.Values.Any(v => v.Running);

    /// <summary>
    /// No Cinema: anfitrião ou participante de uma sessão, baixando o filme do anfitrião, ou assistindo pela transmissão do anfitrião
    /// (<c>_streamTela</c>/<c>_streamAbrindo</c>, spec 011). É o mesmo critério do <c>Busy</c> da atualização automática.
    /// </summary>
    bool NoCinema => _filmeForm is { IsDisposed: false, Ocupado: true } || _streamTela is { IsDisposed: false } || _streamAbrindo;

    /// <summary>
    /// Recalcula a atividade e aplica o ícone na janela e na bandeja, só quando mudou. Chamado pelos pontos em que cada estado começa ou termina
    /// (<c>UpdateLive</c>, <c>StartLinkViewer</c>, <c>EndLink</c>) e, como rede de segurança, pelo <c>GroupTick</c> de 1 s: sessão do Cinema que
    /// acaba, player que fecha sozinho e falhas não passam por um ponto só. Sempre na thread da tela.
    /// </summary>
    void AtualizarIconeAtividade()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { Ui(AtualizarIconeAtividade); return; }
        var atividade = IconeAtividade.Escolher(NoCinema, MinhaLiveNoAr, AssistindoLiveDeAmigo);
        string texto = atividade switch
        {
            AtividadeIcone.Transmitindo => $"{UiBrand.Name}: transmitindo {_liveGame}",
            AtividadeIcone.Cinema => $"{UiBrand.Name}: no Cinema",
            AtividadeIcone.Assistindo => $"{UiBrand.Name}: assistindo",
            _ => UiBrand.Name
        };
        if (texto.Length > 63) texto = texto[..63]; // limite do NotifyIcon.Text
        if (atividade == _iconeAplicado && texto == _iconeTexto) return;
        if (atividade != _iconeAplicado)
        {
            _iconeAplicado = atividade;
            try { Icon = IconeJanela(atividade); } catch (Exception ex) { Log.Info($"Ícone da janela não trocou: {ex.Message}"); }
            if (_tray != null) try { _tray.Icon = IconeBandeja(atividade); } catch (Exception ex) { Log.Info($"Ícone da bandeja não trocou: {ex.Message}"); }
        }
        _iconeTexto = texto;
        if (_tray != null) _tray.Text = texto;
    }
}
