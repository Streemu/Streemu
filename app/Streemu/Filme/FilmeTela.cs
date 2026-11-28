using System.Text.Json.Nodes;
using Streemu.Util;

namespace Streemu.Filme;

/// <summary>
/// Com quem o filme acontece: a sala do grupo, com uma ligação por amigo. O MainForm monta a ligação (a direta por código acabou na spec 022).
/// </summary>
/// <param name="SemRede">Por que não dá para assistir junto agora (nulo = dá).</param>
/// <param name="Anuncio">Filme que alguém abriu (filme, nome, anfitriao), ou nulo.</param>
/// <param name="Anunciar">Anfitrião: conta ao grupo (nulo = fechou).</param>
/// <param name="Entrar">Quem assiste: liga ao anfitrião do anúncio; devolve o erro para a tela ou nulo.</param>
/// <param name="Enviar">Anfitrião: para todos que entraram; quem assiste: para o anfitrião.</param>
/// <param name="OffsetAnfitriaoUs">Hora do anfitrião menos a minha (relógio comum, 7.2); nulo enquanto não mediu.</param>
/// <param name="IdaVolta">Maior ida e volta entre os que estão juntos (folga da hora marcada).</param>
/// <param name="Avatar">A foto de uma pessoa: o id da conta (nulo no anúncio de app antigo, que só leva o nome) e o nome (só para o caso sem id).</param>
/// <param name="MeuId">O id da minha conta; vai junto do nome nas mensagens da sessão.</param>
/// <param name="NomeDe">O nome atual de uma pessoa pelo id da conta (texto de tela; nulo = o último nome que ela mandou).</param>
/// <param name="Capa">A capa de um filme pelo id e o hash do anúncio (spec 017); nulo = o degradê.</param>
public sealed record FilmeLigacao(string MeuNome, Func<string?> SemRede, Func<JsonObject?> Anuncio, Action<JsonObject?> Anunciar,
    Func<JsonObject, Task<string?>> Entrar, Action Sair, Action<JsonObject> Enviar, Func<long?> OffsetAnfitriaoUs, Func<double> IdaVolta,
    Func<Net.RelayConfig.Settings?> Grupo, Func<Net.Tunnel?> TunelAnfitriao,
    Func<IReadOnlyList<JsonObject>>? Anuncios = null, Func<bool>? OutraParticipacao = null, Action? Retomar = null,
    Func<string?, string, Ui.Avatars.AvatarImage?>? Avatar = null, Func<string, string?, Image?>? Capa = null,
    Func<string>? MeuId = null, Func<string, string?>? NomeDe = null);

/// <summary>
/// A tela do filme (spec 010, 11.1 e 11.2), dentro da janela principal, no lugar do Início: palco preto com o mpv dentro (--wid), a
/// sala de espera à direita (quem está pronto, baixando ou conferindo) e a barra de baixo. Tela cheia é a janela principal sem borda
/// (como o palco das lives): o mpv nunca troca de janela, então não pisca nem reabre. Os controles que somem e os avatares ficam
/// para o desenho combinado no grupo.
/// </summary>
public sealed partial class FilmeTela : UserControl
{
    readonly FilmeLigacao _lig;
    string MeuId => _lig.MeuId?.Invoke() ?? "";
    readonly bool _teste;
    readonly Panel _tela = new() { Dock = DockStyle.Fill, BackColor = Theme.VideoMargin };
    // a entrada numa sessão (11.1, mockup "Cinema Formas de entrada"): assistir sem baixar, baixar e assistir, ou o próprio arquivo (FilmeTela.Entrada.cs)
    readonly Ui.CinemaEntryView _card;
    readonly Button _principal = BotaoCard(primario: true), _tenho = BotaoCard(), _stream = BotaoCard();
    long? _parcial; string? _parcialDe; DateTime _parcialEm;
    readonly Label _sala = new() { Dock = DockStyle.Right, Width = 260, ForeColor = Theme.Text, BackColor = Theme.SurfaceRaised, Padding = new Padding(14, 14, 10, 10), Font = UiTypography.Create(UiTypography.Action), Visible = false };
    readonly Label _status = new() { Dock = DockStyle.Top, Height = 30, ForeColor = Theme.Text, BackColor = Theme.SurfaceRaised, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 10, 0) };
    readonly Panel _barra = new() { Dock = DockStyle.Bottom, Height = 44, BackColor = Theme.SurfaceRaised };
    readonly FlowLayoutPanel _barraEsq = new() { Dock = DockStyle.Fill, Padding = new Padding(8, 6, 0, 0), WrapContents = false };
    readonly FlowLayoutPanel _barraDir = new() { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 6, 2, 0), WrapContents = false };
    readonly Button _tocar = Botao("▶"), _voltar = Botao("−10 s"), _avancar = Botao("+10 s"),
        _telaCheia = Botao("Tela cheia"), _sair = Botao("Sair do filme");
    readonly Label _tempo = new() { AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(10, 8, 10, 0) };
    readonly CheckBox _todos = new() { Text = "Todos controlam", AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(10, 7, 0, 0), Visible = false };
    /// <summary>Anfitrião: manda o filme como vídeo ao vivo para quem não tem o arquivo (spec 011), com qualidade menor e sem download.</summary>
    readonly CheckBox _transmitir = new() { Text = "Transmitir para quem não tem o arquivo", AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(10, 7, 0, 0), Visible = false };
    /// <summary>O anfitrião ligou (true) ou desligou a transmissão do filme sem download.</summary>
    public event Action<bool>? TransmitirPedido;
    /// <summary>Quem assiste clicou em "Assistir agora, sem baixar" no filme anunciado.</summary>
    public event Action<JsonObject?>? AssistirStreamPedido;
    /// <summary>A transmissão não pôde ligar: desmarca o interruptor sem disparar de novo.</summary>
    /// <summary>"Assistir agora" está conectando (o botão fica travado até o player abrir ou falhar).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool StreamAbrindo { get => _streamAbrindoTela; set { if (_streamAbrindoTela == value) return; _streamAbrindoTela = value; Atualizar(); } }
    bool _streamAbrindoTela;
    bool _disposing;
    public void StreamRecusado() { _transmitir.CheckedChanged -= TransmitirMudou; _transmitir.Checked = false; _transmitir.CheckedChanged += TransmitirMudou; }
    void TransmitirMudou(object? s, EventArgs e) => TransmitirPedido?.Invoke(_transmitir.Checked);
    readonly CheckBox _esperar = new() { Text = "Esperar todos", Checked = true, AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(10, 7, 0, 0), Visible = false };
    readonly System.Windows.Forms.Timer _relogioTela = new() { Interval = 250 };
    readonly ToolTip _dica = new();
    string? _aviso; DateTime _avisoAte;

    public Sessao? Sessao { get; private set; }
    /// <summary>A última sessão, mesmo depois de fechada (relatório do teste).</summary>
    public Sessao? UltimaSessao { get; private set; }
    /// <summary>Anfitrião: o arquivo que ele serve a quem baixa (4.2).</summary>
    public string? ArquivoAnfitriao { get; private set; }
    /// <summary>O download em curso (ou o último, para o relatório do teste).</summary>
    public Download? UltimoDownload { get; private set; }
    CancellationTokenSource? _baixando, _conferindo;
    DateTime _progressoEnviado;

    /// <summary>O botão de tela cheia (ou Esc em tela cheia): a janela principal alterna.</summary>
    public event Action? TelaCheiaPedida;
    /// <summary>Eventos para a telemetria (spec 009): "filme", "filme_fim", "filme_download_fim". O MainForm grava na sessão.</summary>
    public event Action<string, JsonObject>? Telemetria;
    /// <summary>O player da sessão aberta (a série por segundo do MainForm lê quadros perdidos dele).</summary>
    public Mpv? Player { get; private set; }
    DateTime _inicioSessao;
    string _como = "";
    /// <summary>"Sair do filme": a janela principal volta para o Início.</summary>
    public event Action? Saiu;
    /// <summary>A janela principal diz se está em tela cheia (muda o texto do botão; o Esc sai).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool EmTelaCheia { get => _emTelaCheia; set { _emTelaCheia = value; Atualizar(); } }
    bool _emTelaCheia;
    /// <summary>Há filme aberto ou download em curso (a barra lateral mostra o "Filme" marcado e o app não se atualiza).</summary>
    public bool Ocupado => Sessao != null || _transmissao != null || _baixando != null || _abrindo || StreamAbrindo;

    public FilmeTela(FilmeLigacao lig, bool teste)
    {
        _lig = lig; _teste = teste;
        Dock = DockStyle.Fill;
        BackColor = Theme.VideoMargin;
        _card = new Ui.CinemaEntryView([_stream, _principal, _tenho]);
        InitCinema();
        _stream.Enabled = false;
        _dica.SetToolTip(_stream, "O anfitrião ainda não ligou \"Transmitir para quem não tem o arquivo\".");
        // caso de 11/11: um clique duplo abria dois players e duas ligações (a janela só aparece depois de conectar, ~1 s); o MainForm desliga o StreamAbrindo no fim
        _stream.Click += (_, _) =>
        {
            if (!PodeEntrar()) return;
            var anuncio = AnuncioAtual();
            if (anuncio?["stream"]?.GetValue<bool>() != true) return;
            StreamAbrindo = true; _stream.Enabled = false; AssistirStreamPedido?.Invoke(anuncio);
        };
        _transmitir.CheckedChanged += TransmitirMudou;
        _dica.SetToolTip(_transmitir, "Quem não tem o arquivo assiste agora, sem baixar, em qualidade menor. O som e a imagem saem do seu arquivo.");
        _barraEsq.Controls.AddRange(new Control[] { _tocar, _voltar, _avancar, _tempo, _todos, _transmitir, _esperar });
        _barraDir.Controls.AddRange(new Control[] { _telaCheia, _sair });
        _barra.Controls.Add(_barraEsq); _barra.Controls.Add(_barraDir);
        _dica.SetToolTip(_esperar, "O primeiro ▶ espera todos terminarem de baixar e conferir; o filme começa sozinho.");
        Controls.Add(_tela); Controls.Add(_cinema); Controls.Add(_sala); Controls.Add(_filaView); Controls.Add(_barra); Controls.Add(_status); Controls.Add(_playerNav);
        InitFila(); // FilmeTela.Fila.cs
        _principal.Click += async (_, _) =>
        {
            if (_baixando != null) _baixando.Cancel();
            else if (AnuncioAtual() != null) await BaixarAsync(null);
            else await EscolherAsync(null);
        };
        _tenho.Click += async (_, _) => await EscolherAsync(null);
        _tocar.Click += (_, _) => TocarOuPausar();
        // com a interface dentro do mpv (MpvUi, o padrão): ela tem tudo desta barra, até as escolhas do anfitrião e o "Sair do filme" (a barra some no Atualizar)
        _voltar.Click += (_, _) => Sessao?.Ir(Sessao.Posicao - 10);
        _avancar.Click += (_, _) => Sessao?.Ir(Sessao.Posicao + 10);
        _todos.CheckedChanged += (_, _) => Sessao?.MudarPermissao(_todos.Checked);
        _esperar.CheckedChanged += (_, _) => { if (Sessao is { } s) { s.EsperarTodos = _esperar.Checked; if (!_esperar.Checked) s.ComecarAgora(); } };
        _telaCheia.Click += (_, _) => TelaCheiaPedida?.Invoke();
        _sair.Click += (_, _) => SairDoFilme();
        _relogioTela.Tick += (_, _) => Atualizar();
        VisibleChanged += (_, _) => Atualizar();
        Atualizar();
    }

    /// <summary>Espaço, setas e Esc valem com o foco em qualquer controle da tela (o mpv não recebe teclado: --input-vo-keyboard=no).</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && EmTelaCheia) { TelaCheiaPedida?.Invoke(); return true; }
        // direto na sessão e não PerformClick: com a interface do mpv os botões ficam escondidos, e PerformClick num botão escondido não faz nada
        if (keyData == Keys.L && Sessao != null) { AlternarFila(); return true; } // F já é a tela cheia
        if (Sessao is { } s && keyData is Keys.Space or Keys.Left or Keys.Right)
        {
            if (!s.PodeControlar) return true; // como antes: o botão desligado engolia a tecla
            if (keyData == Keys.Space) TocarOuPausar();
            else s.Ir(s.Posicao + (keyData == Keys.Left ? -Passo : Passo));
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Quanto as setas pulam: 5 s com a interface do mpv (−5/+5, pedido do 2º teste), 10 s com os botões de antes.</summary>
    static int Passo => MpvUi.Ligada ? 5 : 10;

    void TocarOuPausar() { if (Sessao is { } s) { if (s.Tocando) s.Pausar(); else s.Tocar(); } }

    void SairDoFilme() { _baixando?.Cancel(); Encerrar(); Saiu?.Invoke(); }

    /// <summary>Botão da interface do mpv (MpvUi): o mesmo caminho dos botões da tela.</summary>
    void AcaoUi(string[] args)
    {
        if (MpvUi.Ler(args) is not var (acao, v) || Sessao is not { } s) return;
        switch (acao)
        {
            case "cheia": TelaCheiaPedida?.Invoke(); return;
            case "sair": SairDoFilme(); return;
            // as escolhas do anfitrião passam pelas caixas escondidas da barra: os mesmos eventos de antes (permissão, transmitir, esperar)
            case "todos" when s.Anfitriao: _todos.Checked = !_todos.Checked; return;
            case "transmitir" when s.Anfitriao: _transmitir.Checked = !_transmitir.Checked; return;
            case "esperar" when s.Anfitriao: _esperar.Checked = !_esperar.Checked; return;
            case "fila": AlternarFila(); return;
            case "anterior" when s.Anfitriao: _ = AnteriorAsync(); return;
            case "proximo" when s.Anfitriao: _ = ProximoAsync(); return;
        }
        if (!s.PodeControlar) { Avisar($"Só {s.NomeAnfitriao} controla o filme agora."); return; }
        switch (acao)
        {
            case "tocar": TocarOuPausar(); break;
            case "pular" when v is double d: s.Ir(s.Posicao + d); break;
            case "ir" when v is double p: s.Ir(p); break;
        }
    }

    static Button BotaoCard(bool primario = false) => new()
    {
        AutoSize = false, FlatStyle = FlatStyle.Flat, Font = UiTypography.Create(UiTypography.Body),
        BackColor = primario ? Theme.Accent : Theme.Neutral, ForeColor = primario ? Theme.AccentText : Theme.Text, Margin = new Padding(0, 0, 0, 8),
        FlatAppearance = { BorderSize = 0 },
    };

    static Button Botao(string t) => new()
    {
        Text = t, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Theme.Neutral, ForeColor = Theme.Text, Margin = new Padding(0, 0, 6, 0),
        FlatAppearance = { BorderSize = 0 },
    };

    /// <summary>Escolhe o arquivo (diálogo, ou <paramref name="caminho"/> no teste) e abre o mpv. Vira anfitrião, ou entra no filme aberto se for o mesmo arquivo.</summary>
    public async Task EscolherAsync(string? caminho)
    {
        if (!PrepararEntrada() || !PodeEntrar()) return;
        _abrindo = true;
        try
        {
            if (_lig.SemRede() is string sem) { Avisar(sem); return; }
            List<string>? resto = null;
            if (caminho == null)
            {
                // vários arquivos viram a fila (episódios): em ordem natural, o primeiro toca e o resto espera. Entrando no filme de um amigo, é um arquivo só
                using var dlg = new OpenFileDialog { Title = "Escolha o filme", Filter = FiltroVideos, Multiselect = AnuncioAtual() == null };
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                var escolhidos = Fila.Ordenar(dlg.FileNames);
                caminho = escolhidos[0];
                if (escolhidos.Count > 1) resto = escolhidos.Skip(1).ToList();
            }
            _principal.Enabled = _tenho.Enabled = false;
            try
            {
                await AbrirAsync(caminho);
                if (resto != null && Sessao is { Anfitriao: true }) AdicionarNaFila(resto);
            }
            finally { _principal.Enabled = _tenho.Enabled = true; }
        }
        finally { _abrindo = false; Atualizar(); }
    }

    /// <summary>
    /// Quem não tem o arquivo (ou tem outra versão) baixa o do anfitrião pela ligação do filme (4.2). Um download parado na última
    /// pasta continua sozinho; senão pergunta a pasta (<paramref name="pasta"/> no teste). Terminou e conferiu: entra no filme.
    /// </summary>
    public async Task BaixarAsync(string? pasta)
    {
        if (!PrepararEntrada() || !PodeEntrar()) return;
        if (_lig.SemRede() is string sem) { Avisar(sem); return; }
        if (AnuncioAtual() is not { } anuncio) { Avisar("Escolha uma sessão no Cinema para baixar."); return; }
        _abrindo = true;
        try
        {
            string filme = anuncio["filme"]?.GetValue<string>() ?? "", dono = anuncio["anfitriao"]?.GetValue<string>() ?? "o amigo";
            long tam = Download.TamanhoDe(filme);
            if (pasta == null)
            {
                var saved = await Task.Run(() => { string path = Download.PastaPadrao; return (Path: path, Partial: Download.JaBaixado(path, filme)); });
                string padrao = saved.Path;
                if (saved.Partial != null) pasta = padrao;
                else
                {
                    using var dlg = new FolderBrowserDialog { Description = $"Onde guardar o filme de {dono}?", SelectedPath = padrao, UseDescriptionForTitle = true };
                    if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                    pasta = dlg.SelectedPath;
                }
            }
            if (await Task.Run(() => Download.FaltaEspaco(pasta, tam - (Download.JaBaixado(pasta, filme) ?? 0))) is string semEspaco) { Avisar(semEspaco); return; }
            await Task.Run(() => Download.PastaPadrao = pasta);
            var cts = _baixando = new CancellationTokenSource();
            _tenho.Enabled = false;
            Atualizar();
            try
            {
                Avisar($"Conectando com {dono}...");
                if (await _lig.Entrar(anuncio) is string erro) { Avisar(erro); return; }
                if (_lig.TunelAnfitriao() is not { } t) { _lig.Sair(); Avisar($"A ligação com {dono} caiu. Tente de novo."); return; }
                if (t.Target is { Kind: not Net.CandKind.Direct })
                {
                    // D1 (spec 010, 8): liberado no beta para testar com arquivos leves (2026-11-11); o relay é pago por GB acima
                    // de 1.000 GB/mês, então as regras de gasto da 8.3 entram antes de abrir para filmes grandes
                    Avisar($"A conexão com {dono} passa pelo relay de reserva: o download usa {Download.TamanhoDe(filme) / 1e9:N1} GB da cota do grupo e pode ser mais lento.");
                    Log.Info($"[filme] download pelo relay ({t.Target}): {Download.TamanhoDe(filme) / 1e6:N0} MB da cota do grupo.");
                }
                _aviso = null;
                var d = UltimoDownload = new Download(t, filme, pasta);
                string final;
                try
                {
                    final = await Task.Run(() => d.RodarAsync(p => Ui(() => Progredir(filme, p)), cts.Token));
                }
                catch (OperationCanceledException) { TeleDownload(d, t, "parado"); _lig.Sair(); Avisar("Download parado. O que já veio fica guardado: clique em Baixar para continuar."); return; }
                catch (Exception e) { TeleDownload(d, t, "erro: " + e.Message); _lig.Sair(); Avisar($"O download parou: {e.Message}"); Log.Info($"[filme] download parou: {e.Message}"); return; }
                TeleDownload(d, t, "ok");
                // O arquivo veio do PC de um amigo, que pode estar infectado: confere pelo conteúdo que é um vídeo antes de o mpv abrir. Uma
                // lista (.m3u8, EDL...) com nome de .mkv faria o mpv buscar URLs ou abrir arquivos daqui. Só este caminho: o arquivo que a
                // pessoa escolhe (EscolherAsync) é dela e não passa pela conferência.
                string? ruim = await Task.Run(() => ConferirFormato.Motivo(Media.Tools.FfmpegOuNulo, final));
                if (ruim != null)
                {
                    Log.Info($"[filme] arquivo baixado recusado, não abri no player: {ruim}");
                    _lig.Sair();
                    // apaga: um arquivo recusado largado na pasta seria aceito depois pelo "já tenho o arquivo" (a identidade bate) e iria ao mpv
                    try { File.Delete(final); } catch (Exception e) { Log.Info($"[filme] não consegui apagar o arquivo recusado: {e.GetType().Name}"); }
                    Avisar($"O arquivo que veio de {dono} não é um vídeo que o {UiBrand.Name} reconhece; não abri. Peça para ele conferir o arquivo.");
                    return;
                }
                await AbrirAsync(final, jaLigado: true);
            }
            finally
            {
                _baixando = null;
                cts.Dispose();
                _tenho.Enabled = true;
                Atualizar();
            }
        }
        finally { _abrindo = false; Atualizar(); }
    }

    /// <summary>
    /// 4.1: confere o arquivo inteiro contra o manifesto do anfitrião sem segurar o filme (o mpv já segue o anfitrião). Enquanto isso o
    /// anfitrião vê "conferindo N%"; no fim, "pronto" ou o aviso de defeito. Anfitrião sem o download (versão antiga): segue como pronto,
    /// a identidade já bateu.
    /// </summary>
    async Task ConferirAsync(string caminho, string filme)
    {
        JsonObject Msg(JsonObject extra)
        {
            var m = new JsonObject { ["type"] = "filme_pronto", ["filme"] = filme, ["pronto"] = false, ["nome"] = _lig.MeuNome, ["id"] = MeuId };
            foreach (var kv in extra) m[kv.Key] = kv.Value?.DeepClone();
            return m;
        }
        _lig.Enviar(Msg(new JsonObject { ["conferindo"] = new JsonObject { ["pct"] = 0 }, ["primeiro"] = true }));
        if (_lig.TunelAnfitriao() is not { } t) return;
        var cts = _conferindo = new CancellationTokenSource();
        var d = new Download(t, filme, "");
        DateTime enviado = DateTime.Now;
        try
        {
            int ruins = await Task.Run(() => d.ConferirAsync(caminho, p =>
            {
                if (p.Fase != "conferindo" || DateTime.Now - enviado < TimeSpan.FromSeconds(1)) return;
                enviado = DateTime.Now;
                Ui(() => { if (Sessao?.Filme == filme) _lig.Enviar(Msg(new JsonObject { ["conferindo"] = new JsonObject { ["pct"] = p.Pct } })); });
            }, cts.Token));
            if (Sessao?.Filme != filme) return;
            if (ruins == 0) _lig.Enviar(Msg(new JsonObject { ["pronto"] = true }));
            else
            {
                _lig.Enviar(Msg(new JsonObject { ["defeito"] = ruins }));
                Avisar($"Seu arquivo tem {ruins} pedaço(s) diferentes do de {Sessao.NomeAnfitriao} (arquivo corrompido?). Feche o filme e clique em Baixar para trazer o arquivo certo.");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Log.Info($"[filme] não deu para conferir o arquivo ({e.Message}); sigo como pronto.");
            if (Sessao?.Filme == filme) _lig.Enviar(Msg(new JsonObject { ["pronto"] = true }));
        }
        finally { if (_conferindo == cts) _conferindo = null; cts.Dispose(); }
    }

    /// <summary>Progresso do download: na tela e, a cada segundo, para o anfitrião (o "baixando" com % e tempo da 4.3).</summary>
    void Progredir(string filme, Progresso p)
    {
        Atualizar();
        if (p.Fase != "baixando" || DateTime.Now - _progressoEnviado < TimeSpan.FromSeconds(1)) return;
        _progressoEnviado = DateTime.Now;
        _lig.Enviar(new JsonObject
        {
            ["type"] = "filme_pronto", ["filme"] = filme, ["pronto"] = false, ["nome"] = _lig.MeuNome, ["id"] = MeuId,
            ["baixando"] = new JsonObject { ["pct"] = p.Pct, ["eta_s"] = Math.Round(p.EtaS) },
        });
    }

    async Task AbrirAsync(string caminho, bool jaLigado = false)
    {
        string? exe = await Task.Run(Mpv.Find); // fora da janela: a primeira conferência do SHA-256 do mpv leva uns instantes
        if (exe == null)
        {
            try { exe = await Task.Run(() => MpvInstalador.InstalarAsync(_lig.Grupo(), new Progress<string>(Avisar), CancellationToken.None)); }
            catch (Exception e) { Avisar($"Não consegui baixar o player do filme: {e.Message}."); Log.Info($"[filme] mpv: {e.Message}"); return; }
        }
        Avisar("Conferindo o arquivo...");
        string id;
        try { id = await Task.Run(() => Identidade.De(caminho)); }
        catch (Exception e) { Avisar($"Não consegui ler o arquivo ({e.Message})."); return; }
        string nome = Path.GetFileNameWithoutExtension(caminho);
        var anuncio = AnuncioAtual();
        bool anfitriao = anuncio == null;
        string nomeAnfitriao = _lig.MeuNome;
        if (!anfitriao)
        {
            string dono = anuncio!["anfitriao"]?.GetValue<string>() ?? "o amigo";
            if (anuncio["filme"]?.GetValue<string>() != id)
            {
                Avisar($"Esse arquivo é outra versão do filme de {dono} (tamanho ou conteúdo diferente). Clique em \"Baixar e assistir\" para trazer o arquivo de {dono}.");
                return;
            }
            if (!jaLigado)
            {
                Avisar($"Conectando com {dono}...");
                if (await _lig.Entrar(anuncio) is string erro) { Avisar(erro); return; }
            }
            nome = anuncio["nome"]?.GetValue<string>() ?? nome;
            nomeAnfitriao = dono;
        }
        Mpv mpv;
        try { mpv = await Mpv.OpenAsync(exe, caminho, _tela.Handle, _teste ? 0 : 100); }
        catch (Exception e) { if (!anfitriao) _lig.Sair(); Avisar($"O player não abriu: {e.Message}."); return; }
        Func<long?> relogio = anfitriao ? () => Net.Tunnel.NowUs : () => _lig.OffsetAnfitriaoUs() is long o ? Net.Tunnel.NowUs + o : null;
        var s = new Sessao(anfitriao, id, nome, nomeAnfitriao, _lig.MeuNome, mpv, relogio, _lig.IdaVolta, _lig.Enviar, MeuId) { NomeNaSala = _lig.NomeDe };
        Sessao = UltimaSessao = s;
        _catalogo = false;
        LigarSessao(s); // FilmeTela.Fila.cs
        _caminhoArquivo = caminho;
        mpv.Saiu += () => Ui(() => { if (!IsDisposed) Avisar("O player fechou."); });
        Log.Info($"[filme] {(anfitriao ? "anfitrião" : "espectador")}: (nome do filme fora do log) ({id})"); // o nome do filme é o do arquivo da pessoa: fica na tela, não na telemetria (spec 018)
        Player = mpv;
        if (MpvUi.Ligada) mpv.Mensagem += a => Ui(() => AcaoUi(a));
        _inicioSessao = DateTime.Now;
        _como = anfitriao ? "anfitriao" : jaLigado ? "baixou" : "mesmo_arquivo";
        var tun = anfitriao ? null : _lig.TunelAnfitriao();
        string como = _como;
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500); // o mpv termina de abrir o arquivo
            var info = await mpv.InfoAsync();
            Log.Info("[filme] player: " + info.ToJsonString());
            Ui(() =>
            {
                if (!ReferenceEquals(Sessao, s)) return; // não anunciar um player que já fechou ou foi trocado
                Telemetria?.Invoke("filme", new JsonObject
                {
                    ["papel"] = anfitriao ? "anfitriao" : "espectador", ["filme"] = id, ["tamanho_mb"] = Math.Round(Download.TamanhoDe(id) / 1e6),
                    ["como"] = como, ["caminho"] = Caminho(tun), ["relogio_ida_volta_ms"] = tun == null ? null : Math.Round(tun.PeerClockRttMs, 2), ["player"] = info,
                });
            });
        });
        if (anfitriao) ArquivoAnfitriao = caminho;
        if (anfitriao)
        {
            // "Transmitir para quem não tem o arquivo" começa ligado (pedido de 11/11): o envio só lê o arquivo quando alguém clica em "Assistir agora", então
            // ligado e sem ninguém custa só a leitura do cabeçalho do arquivo. O anfitrião desmarca se não quiser.
            if (!_transmitir.Checked) _transmitir.Checked = true;
            _fila.Iniciar([caminho]);
            AnunciarMeu(s, new JsonObject { ["filme"] = id, ["nome"] = nome, ["anfitriao"] = _lig.MeuNome }); // FilmeTela.Fila.cs
            _difusao.Start();
            _ = VigiarFimAsync(s, mpv);
            // a lista de pedaços (manifesto) é calculada só quando alguém pede o download (Envio.Responder): caso de 11/11, num arquivo de 30 GB ela levou 391 s lendo o
            // disco a ~77 MB/s ao mesmo tempo que o mpv e o envio pela transmissão, sem ninguém baixando
        }
        // baixado agora: já conferido no download; com o próprio arquivo: confere em segundo plano (4.1) antes de ficar "pronto"
        else if (jaLigado) _lig.Enviar(new JsonObject { ["type"] = "filme_pronto", ["filme"] = id, ["pronto"] = true, ["nome"] = _lig.MeuNome, ["id"] = MeuId, ["primeiro"] = true });
        else _ = ConferirAsync(caminho, id);
        _aviso = null;
    }

    /// <summary>Mensagem filme_* de quem está junto (já na thread da janela).</summary>
    public void Receber(JsonObject m)
    {
        if (ReceberDaTroca(m)) return; // FilmeTela.Fila.cs: antes do filtro por id, que jogaria fora o filme_trocar e o que chega para o episódio novo
        if (m["filme"] is { } filme && (string?)filme != (Sessao?.Filme ?? (string?)AnuncioAtual()?["filme"])) return;
        if (m["type"]?.GetValue<string>() == "filme_fim" && _baixando != null && UltimoDownload != null)
        {
            _baixando.Cancel();
            return;
        }
        if (m["type"]?.GetValue<string>() == "filme_fim" && Sessao is { Anfitriao: false } s && m["filme"]?.GetValue<string>() == s.Filme)
        {
            Encerrar();
            Avisar($"{s.NomeAnfitriao} fechou o filme.");
            return;
        }
        if (ReceberDaFila(m)) return;
        Sessao?.Receber(m);
    }

    /// <summary>Anfitrião: alguém saiu do filme.</summary>
    public void AmigoSaiu(string id) => Sessao?.AmigoSaiu(id);

    void Encerrar()
    {
        _baixando?.Cancel();
        _conferindo?.Cancel();
        var s = Sessao;
        if (s == null) return;
        // 7.4 + 4.3 da sessão inteira: erro contra o relógio comum, pulos, espera do pulo, com quem começou, quanto tempo durou
        var rel = s.Relatorio();
        rel["filme"] = s.Filme;
        rel["como"] = _como;
        rel["minutos"] = Math.Round((DateTime.Now - _inicioSessao).TotalMinutes, 1);
        if (s.Anfitriao) rel["amigos"] = string.Join(",", s.Amigos.Select(kv => $"{s.NomeDe(kv.Key)}={kv.Value}"));
        Telemetria?.Invoke("filme_fim", rel);
        Player = null;
        Sessao = null;
        _avisoFixo = null;
        ArquivoAnfitriao = null;
        LimparFila();
        if (s.Anfitriao) { _lig.Enviar(new JsonObject { ["type"] = "filme_fim", ["filme"] = s.Filme }); _lig.Anunciar(null); }
        else _lig.Sair();
        s.Dispose();
        _principal.Enabled = _tenho.Enabled = true;
        Atualizar();
    }

    public void Avisar(string texto)
    {
        if (InvokeRequired) { Ui(() => Avisar(texto)); return; }
        _aviso = texto; _avisoAte = DateTime.Now.AddSeconds(6); Atualizar();
    }

    /// <summary>
    /// Aviso que fica na tela até alguém tirá-lo (o "Avisar" some em 6 s): o sinal do anfitrião (MainForm.FilmeSinal.cs). Nulo tira.
    /// Vem da thread da janela principal, que é a mesma desta tela.
    /// </summary>
    public void AvisoFixo(string? texto)
    {
        if (InvokeRequired) { Ui(() => AvisoFixo(texto)); return; }
        _avisoFixo = texto; Atualizar();
    }
    string? _avisoFixo;

    /// <summary>O anfitrião ficou 30 s sem sinal: o filme pausa aqui e o aviso fica na tela até a pessoa sair (a janela não fecha sozinha, para dar tempo de ler).</summary>
    public void AnfitriaoCaiu(string texto)
    {
        if (InvokeRequired) { Ui(() => AnfitriaoCaiu(texto)); return; }
        if (Sessao is { Anfitriao: false } s) s.PararPorQueda();
        AvisoFixo(texto);
    }

    void Ui(Action a) { if (IsDisposed || !IsHandleCreated) return; try { BeginInvoke(a); } catch { } }

    void Atualizar()
    {
        if (IsDisposed || _disposing) return;
        if (!Visible) { _relogioTela.Stop(); return; }
        // Descoberta muda pelos anúncios, não por sondagem a cada 250 ms. Detalhes em reprodução precisam do relógio.
        _relogioTela.Interval = Ocupado || _aviso != null && DateTime.Now < _avisoAte ? 250 : 1000;
        _relogioTela.Enabled = Visible && (_transmissao == null && (Ocupado || _selecao.Detail && !_listaCinema) || _aviso != null && DateTime.Now < _avisoAte);
        var s = Sessao;
        _tocar.Enabled = _voltar.Enabled = _avancar.Enabled = s is { PodeControlar: true };
        _tocar.Text = s is { Tocando: true } ? "⏸" : s is { Aguardando: true } ? "▶ Começar agora" : "▶";
        _todos.Visible = s is { Anfitriao: true };
        _transmitir.Visible = s is { Anfitriao: true };
        _esperar.Visible = s is { Anfitriao: true, Tocando: false } && s.UltimaEsperaPuloMs < 0 && s.ComecouCom == null;
        AtualizarCinema();
        var anuncio = s == null ? AnuncioAtual() : null;
        _tempo.Text = s == null ? "" : Sessao.Tempo(s.Posicao) + (double.IsNaN(s.UltimoErroMs) || !s.Tocando ? "" : $"   ·   {s.UltimoErroMs:+0;-0} ms");
        if (s is { PodeControlar: false }) _dica.SetToolTip(_tocar, $"Só {s.NomeAnfitriao} controla"); else _dica.SetToolTip(_tocar, null);
        string linha;
        if (_avisoFixo != null && s != null) linha = _avisoFixo;
        else if (_aviso != null && DateTime.Now < _avisoAte) linha = _aviso;
        else if (_baixando != null && UltimoDownload?.Atual is { } p)
            linha = p.Fase switch
            {
                "preparando" => $"O PC de {anuncio?["anfitriao"]} está preparando o arquivo ({p.Feito}%)...",
                "manifesto" => "Recebendo a lista de pedaços...",
                "conferindo" => $"Conferindo o arquivo baixado ({p.Pct}%)...",
                "baixando" => $"Baixando \"{anuncio?["nome"]}\": {p.Pct}% · {p.BytesPorSegundo / 1e6:N1} MB/s" + (p.EtaS >= 0 ? " · " + Progresso.Faltam(p.EtaS) : ""),
                _ => "Conectando...",
            };
        else if (s == null)
        {
            var a = AnuncioAtual();
            linha = _lig.SemRede() ?? (a != null
                ? $"{a["anfitriao"]} abriu \"{a["nome"]}\". Escolha o seu arquivo do mesmo filme para assistir junto."
                : "Escolha um filme para ser o anfitrião. Quem quiser assistir junto precisa ter o mesmo arquivo.");
        }
        else if (s.Anfitriao)
            linha = $"Você é o anfitrião de \"{s.Nome}\" · " + (s.Amigos.Count == 0 ? "ninguém entrou ainda" : string.Join(" · ", s.Amigos.Select(kv => $"{s.NomeDe(kv.Key)}: {s.EstadoVisto(kv.Key, kv.Value)}")))
                + (s.Aguardando ? " · começa quando todos estiverem prontos" : "");
        else linha = $"\"{s.Nome}\" com {s.NomeAnfitriao}" + (_lig.OffsetAnfitriaoUs() == null ? " · acertando o relógio..." : "");
        Texto(_status, linha);
        _sair.Visible = Ocupado;
        _telaCheia.Text = EmTelaCheia ? "Sair da tela cheia" : "Tela cheia";

        // sala de espera (11.2): antes de começar, pausado ou baixando, quem está junto e como está
        string eu = _conferindo != null ? "conferindo o arquivo" : s is { Tocando: true } ? "assistindo" : "pronto";
        string sala = s switch
        {
            { Anfitriao: true } => "Na sala\n\nVocê (anfitrião): pronto\n" + string.Join("\n", s.Amigos.Select(kv => $"{s.NomeDe(kv.Key)}: {s.EstadoVisto(kv.Key, kv.Value)}"))
                + (!AlguemEntrou(s) ? "\nNinguém entrou ainda. Os amigos veem o filme na barra lateral." : "")
                + (s.Aguardando ? "\n\nComeça sozinho quando todos estiverem prontos." : ""),
            { } => $"Na sala\n\n{s.NomeAnfitriao} (anfitrião)\nVocê: {eu}" + (s.Tocando ? "" : $"\n\nEsperando {s.NomeAnfitriao} dar o play."),
            null when _baixando != null => $"Na sala\n\n{anuncio?["anfitriao"]} (anfitrião)\nVocê: baixando {UltimoDownload?.Atual.Pct ?? 0}%",
            _ => "",
        };
        // com a interface do mpv, quem está na sala vai para dentro do vídeo: o painel ao lado encolhia o vídeo toda vez que pausava (1º teste, 11/11)
        _sala.Visible = !_catalogo && sala.Length > 0 && (s is not { Tocando: true }) && !(MpvUi.Ligada && s != null);
        Texto(_sala, sala);
        AtualizarCard(s, anuncio);
        // com o card na tela, a linha de cima só aparece para avisos e a barra de baixo (controles do filme) some
        // com a interface do mpv, a linha de cima vai para dentro do vídeo durante o filme
        _status.Visible = (s != null && !MpvUi.Ligada && !_catalogo) || (_cinema.Visible && (_avisoFixo != null || _aviso != null && DateTime.Now < _avisoAte));
        if (_status.Visible && _cinema.Visible)
            _status.Height = Math.Max(UiScale.Dp(this, 30), TextRenderer.MeasureText(_status.Text, _status.Font,
                new Size(Math.Max(1, ClientSize.Width - _status.Padding.Horizontal), int.MaxValue), TextFormatFlags.WordBreak).Height + UiScale.Dp(this, 12));
        // com a interface do mpv a barra some: em tela cheia ela ficava embaixo e tirava altura do vídeo, que aparecia com faixas pretas dos lados
        _barra.Visible = s != null && !MpvUi.Ligada && !_catalogo;
        AtualizarFila(s); // FilmeTela.Fila.cs
        if (MpvUi.Ligada && s != null && Player is { } mpv) MandarInterface(s, mpv); // FilmeTela.Interface.cs
    }

    /// <summary>Posição do anfitrião agora: a do anúncio, andando desde que ele chegou se o filme está rolando.</summary>
    static double PosicaoDe(System.Text.Json.Nodes.JsonObject a)
    {
        double pos = a["pos"]?.GetValue<double>() ?? 0;
        if (a["estado"]?.GetValue<string>() == "rolando" && a["recebido"]?.GetValue<long>() is long em) pos += (Environment.TickCount64 - em) / 1000.0;
        return pos;
    }

    static string Tamanho(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):N1} GB" : $"{b / (double)(1 << 20):N0} MB";

    static string? Caminho(Net.Tunnel? t) => t?.Target is { } c ? (c.Kind == Net.CandKind.Direct ? "direto" : "relay") : null;

    void TeleDownload(Download d, Net.Tunnel t, string resultado)
    {
        var r = d.Relatorio();
        r["resultado"] = resultado.Length > 300 ? resultado[..300] : resultado;
        r["fase"] = d.Atual.Fase;
        r["caminho"] = Caminho(t);
        Telemetria?.Invoke("filme_download_fim", r);
    }

    static void Texto(Control c, string t) { if (c.Text != t) c.Text = t; }

    /// <summary>Fecha o filme e o que estiver em curso (o app vai fechar, ou o teste terminou).</summary>
    public void Fechar() { _baixando?.Cancel(); _transmissao?.FecharPor("o app encerrou o filme"); Encerrar(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _disposing = true; _relogioTela.Stop(); _relogioTela.Dispose(); Fechar(); _difusao.Dispose(); _dica.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>Teste: a tela do filme numa janela fora da tela e sem foco (a janela principal do teste fica minimizada; regra 2 do AGENTS).</summary>
sealed class JanelaFilmeTeste : Form
{
    public JanelaFilmeTeste()
    {
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-4000, 0);
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        ClientSize = new Size(1024, 650);
    }

    protected override bool ShowWithoutActivation => true;
}
