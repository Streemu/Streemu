using System.Text.Json.Nodes;
using Streemu.Ui;
using Streemu.Util;

namespace Streemu.Filme;

public sealed partial class FilmeTela
{
    readonly CinemaSelection _selecao = new();
    CinemaView _cinema = null!;
    StreamTela? _transmissao;
    readonly Panel _transmissaoHost = new() { Dock = DockStyle.Fill, Visible = false, BackColor = Theme.VideoMargin };
    readonly FlowLayoutPanel _playerNav = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, BackColor = Theme.SurfaceRaised };
    readonly Button _voltarCinema = new() { Text = "Voltar ao Cinema", AutoSize = true, AccessibleName = "Voltar ao Cinema sem sair da sessão" };
    readonly Button _sairTransmissao = new() { Text = "Sair da sessão", AutoSize = true };
    bool _catalogo = true, _listaCinema = true, _abrindo, _parcialPendente;

    IReadOnlyList<JsonObject> Anuncios() => _lig.Anuncios?.Invoke() ?? (_lig.Anuncio() is { } a ? [a] : []);
    JsonObject? AnuncioAtual() => _selecao.Selected;
    public string? AnfitriaoSelecionado => (string?)_selecao.Selected?["de"];
    bool Participando => Ocupado || _lig.OutraParticipacao?.Invoke() == true;
    /// <summary>O player (filme com o arquivo ou pela transmissão) está à vista, e não a lista ou a entrada do Cinema: a barra flutuante não abre por cima dele.</summary>
    public bool VideoNaTela => (Sessao != null || _transmissao != null) && !_catalogo;

    void InitCinema()
    {
        Controls.Add(_transmissaoHost);
        _transmissaoHost.BringToFront();
        _playerNav.Padding = new Padding(UiScale.Dp(this, 8), UiScale.Dp(this, 4), 0, UiScale.Dp(this, 4));
        Theme.StyleButton(_voltarCinema, false);
        _voltarCinema.Click += (_, _) => MostrarCatalogo();
        _playerNav.Controls.Add(_voltarCinema);
        Theme.StyleButton(_sairTransmissao, false);
        _sairTransmissao.Click += (_, _) => _transmissao?.FecharPor("a pessoa saiu da sessão");
        _playerNav.Controls.Add(_sairTransmissao);
        _cinema = new CinemaView(_card);
        if (_lig.Avatar is { } avatar) _cinema.AvatarOf = avatar;
        _cinema.CoverOf = CapaDe;
        // "Assistir agora" é o roxo cheio do mockup; baixar e escolher o arquivo, o roxo apagado (FilmeTela.Entrada.cs troca no criar e no baixar)
        foreach (var button in new[] { _principal, _stream, _tenho }) { Theme.StyleButton(button, button == _stream); if (button != _stream) Estilo(button, primario: false); }
        _cinema.SessionPicked += SelecionarCinema;
        _cinema.CreatePicked += () => { if (PodeMudar()) { _selecao.Create(); _listaCinema = false; _tenho.Enabled = true; Atualizar(); } };
        _cinema.BackPicked += () => { if (!Ocupado) _selecao.Back(); _listaCinema = true; _catalogo = true; Atualizar(); };
        _cinema.ResumePicked += () =>
        {
            if (Sessao != null || Ocupado || _transmissao != null) { RetomarPlayer(); }
            else _lig.Retomar?.Invoke();
        };
    }

    // O formulário existente vira filho antes de abrir o mpv. Navegar nunca troca o HWND do vídeo.
    public void MostrarTransmissao(StreamTela tela)
    {
        if (_transmissao != null && _transmissao != tela) throw new InvalidOperationException("Saia da sessão atual antes de entrar em outra.");
        _transmissao = tela;
        tela.TopLevel = false;
        tela.FormBorderStyle = FormBorderStyle.None;
        tela.Dock = DockStyle.Fill;
        _transmissaoHost.Controls.Add(tela);
        tela.Fechou += () =>
        {
            if (_transmissao != tela) return;
            _transmissao = null;
            _catalogo = _listaCinema = true;
            Atualizar();
        };
        tela.Show();
        RetomarPlayer();
    }

    public void RetomarPlayer()
    {
        _listaCinema = _catalogo = false;
        Atualizar();
        if (_transmissao is { IsDisposed: false } tela) tela.FocusVideo();
        else _tela.Focus();
    }

    void SelecionarCinema(JsonObject a)
    {
        if (!PodeMudar()) return;
        _selecao.Select(a); _listaCinema = false; _parcial = null; _tenho.Enabled = true; _catalogo = true; Atualizar();
    }

    public void MostrarCatalogo() { _catalogo = _listaCinema = true; Atualizar(); }
    public void AtualizarAnuncios() => Atualizar();

    /// <summary>Chegou uma capa (spec 017): o anúncio não mudou, então os cartões só redesenham; a entrada pega a imagem no Atualizar.</summary>
    public void AtualizarCapas() { _cinema.RefreshCovers(); Atualizar(); }

    /// <summary>A capa do anúncio (pelo id do filme e o hash), ou nulo para o degradê.</summary>
    Image? CapaDe(JsonObject? anuncio) =>
        anuncio == null || _lig.Capa == null ? null : _lig.Capa((string?)anuncio["filme"] ?? "", (string?)anuncio["capa"]);

    bool PodeMudar()
    {
        if (!Participando) return true;
        Avisar("Você já participa de uma sessão. Volte à sessão e saia dela antes de criar ou entrar em outra.");
        return false;
    }

    bool PodeEntrar()
    {
        if (!PodeMudar()) return false;
        _selecao.Refresh(Anuncios(), false);
        if (_selecao.Missing) { Avisar("Esta sessão encerrou. Volte ao Cinema e escolha outra."); return false; }
        if (!_selecao.Detail) { Avisar("Escolha uma sessão ou crie a sua no Cinema."); return false; }
        return true;
    }

    // Os ensaios existentes chamam Escolher/Baixar sem clique; só inferir quando não existe ambiguidade.
    bool PrepararEntrada()
    {
        if (!Participando) _listaCinema = false;
        if (_selecao.Detail) return true;
        var anuncios = Anuncios();
        if (anuncios.Count == 0) _selecao.Create();
        else if (anuncios.Count == 1) _selecao.Select(anuncios[0]);
        else { Avisar("Escolha a sessão no Cinema antes de abrir o arquivo."); return false; }
        return true;
    }

    void AtualizarCinema()
    {
        bool browser = Sessao == null && _transmissao == null || _catalogo;
        _cinema.Visible = browser; _tela.Visible = !browser && _transmissao == null;
        _transmissaoHost.Visible = !browser && _transmissao != null;
        // com a interface do mpv o vídeo ocupa a página toda, como nos mockups: "Sair da sessão" fica na barra do player e o Cinema da
        // barra lateral volta às sessões sem sair (o "Voltar à sessão" traz o player de volta)
        _playerNav.Visible = !browser && !EmTelaCheia && !MpvUi.Ligada;
        _sairTransmissao.Visible = _transmissao != null && !MpvUi.Ligada;
        if (!browser) return;
        var anuncios = Anuncios();
        _selecao.Refresh(anuncios, Ocupado);
        // O HWND do mpv permanece no mesmo painel. Ocultar a apresentação não encerra a sessão.
        _cinema.SetSessions(anuncios, !_listaCinema && _selecao.Detail && Sessao == null, Participando, _lig.SemRede());
    }

    void ConsultarParcial(string filme)
    {
        if (_parcialPendente || (_parcialDe == filme && DateTime.Now - _parcialEm <= TimeSpan.FromSeconds(2))) return;
        _parcialPendente = true; _parcialDe = filme; _parcialEm = DateTime.Now;
        _ = Task.Run(() => Download.JaBaixado(Download.PastaPadrao, filme)).ContinueWith(result => Ui(() =>
        {
            _parcialPendente = false;
            if (result.IsCompletedSuccessfully && (string?)_selecao.Selected?["filme"] == filme) _parcial = result.Result;
        }));
    }
}
