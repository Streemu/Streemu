using Streemu.Ui;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// A tela "Novidades" na janela principal (plano 027, etapa 4): o item da barra lateral (com o selo "NOVO" enquanto há novidade não vista), a vista
/// <c>_novidadesView</c> com o seu host, o botão "Ver novidades" das Configurações e o registro do que a pessoa já viu (<see cref="AppState.NovidadesVistas"/>).
/// A tela em si é <c>Ui/Novidades*.cs</c> (<see cref="NovidadesTela"/>): quem restilizar a página não precisa mexer neste arquivo. O que se vê é marcado
/// como visto ao SAIR da tela, não ao entrar: a pessoa precisa das bolinhas de "novo" enquanto lê. Escala (Ctrl + / -) e tema a página acompanha sozinha.
/// </summary>
public sealed partial class MainForm
{
    readonly BufferedPanel _novidadesHost = new() { Dock = DockStyle.Fill, Visible = false };
    /// <summary>Em Configurações > Sistema, ao lado de "Procurar atualização".</summary>
    readonly Button _verNovidades = new() { Text = "Ver novidades", AutoSize = true };
    NovidadesTela? _novidadesTela;
    IReadOnlyList<VersaoNovidade> _novidadesVersoes = [];
    /// <summary>A tela Novidades está aberta (vista da janela, como _gruposView).</summary>
    bool _novidadesView;
    /// <summary>Como _novidadesView estava no último ApplyView: a diferença é a saída da tela (<see cref="SincronizarNovidades"/>).</summary>
    bool _novidadesAberta;

    /// <summary>Chamado no fim do BuildShell: a tela nasce depois do Theme.Apply da janela, como a do Grupos.</summary>
    void AdicionarNovidades()
    {
        if (_novidadesTela != null) return;
        _novidadesVersoes = Novidades.Embutidas();
        var tela = _novidadesTela = new NovidadesTela();
        tela.ProcurarAtualizacao += async () => await CheckUpdateNowAsync(); // o mesmo método do botão das Configurações
        _novidadesHost.BackColor = Theme.Surface;
        _novidadesHost.Controls.Add(tela);
        Controls.Add(_novidadesHost);
        CorrigirOrdemDoEncaixe(); // o host entrou depois da barra lateral, e o encaixe do WinForms vai do último para o primeiro
        _verNovidades.Click += (_, _) => MostrarNovidades();
        // primeira vez com Novidades: tudo o que já existe conta como visto (sem selo). Teste automático não grava o estado (regra 3 do AGENTS.md)
        if (_state.NovidadesVistas == null)
        {
            _state.NovidadesVistas = Novidades.MarcarVistas(_novidadesVersoes, null);
            if (!Automated) _state.Save();
        }
        _side.NovidadesClicked += MostrarNovidades;
        _side.NovidadesVisivel = true;
        if (_flyout != null) _flyout.Panel.NovidadesVisivel = true; // a flutuante já ligou o clique ao nascer (EnsureFlyout)
        tela.Mostrar(_novidadesVersoes, Program.Version, _state.NovidadesVistas);
        AtualizarSeloNovidades();
        AjustarAlturaMinima(); // um item a mais na navegação: a janela não pode ficar mais baixa que a barra
    }

    /// <summary>O item "Novidades" da barra lateral e o botão das Configurações: abre a tela no lugar do Início (mesmo jeito do Grupos).</summary>
    void MostrarNovidades()
    {
        if (_novidadesTela == null) return;
        HideFlyout();
        if (_preparationBusy) return;
        if (ContaBloqueia()) return;
        _preparationView = _stageView = _settingsView = _filmeView = _arquivosView = _gruposView = false;
        _novidadesView = true;
        // com o que a pessoa viu ATÉ AGORA: as bolinhas de "novo" aparecem enquanto ela lê; o que ela viu só entra na saída (SincronizarNovidades)
        _novidadesTela.Mostrar(_novidadesVersoes, Program.Version, _state.NovidadesVistas);
        ApplyView();
    }

    /// <summary>
    /// Chamado pelo ApplyView, o único ponto por onde toda troca de tela passa: quando a tela Novidades deixa de ser a vista, o que ela mostrou vira "visto" e o selo some.
    /// A preparação da live passa por cima sem zerar a vista, então não conta como saída.
    /// </summary>
    void SincronizarNovidades()
    {
        if (_novidadesView == _novidadesAberta) return;
        _novidadesAberta = _novidadesView;
        if (_novidadesView) return;
        _state.NovidadesVistas = Novidades.MarcarVistas(_novidadesVersoes, _state.NovidadesVistas);
        if (!Automated) _state.Save();
        AtualizarSeloNovidades();
    }

    /// <summary>O selo "NOVO" (barra aberta) ou a bolinha (trilho) nas duas barras, pelo que a pessoa ainda não viu.</summary>
    void AtualizarSeloNovidades()
    {
        bool novas = Novidades.NaoLidas(_novidadesVersoes, _state.NovidadesVistas) > 0;
        _side.NovidadesNovas = novas;
        if (_flyout != null) _flyout.Panel.NovidadesNovas = novas;
    }
}
