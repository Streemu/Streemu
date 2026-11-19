using System.Text.Json.Nodes;
using Streemu.Filme;
using Streemu.Net;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// Modo filme (spec 010, F1), modo A para quem JÁ tem o arquivo. Pela sala do grupo: o anfitrião avisa a sala, quem quer assistir
/// abre uma ligação só para o filme (como o "quero_assistir" da live, mas fora das ligações da live). A ligação por código acabou
/// na spec 022: só a sala. Fora do MainForm.cs para dar menos conflito. Quem não tem o arquivo baixa do anfitrião
/// pela mesma ligação (4.2, Filme/Download.cs).
/// </summary>
public partial class MainForm
{
    static readonly string? TesteFilme = Environment.GetEnvironmentVariable("STREEMU_TEST_FILME") is { Length: > 0 } f ? f : null;
    /// <summary>Aberto para todos como beta (selo na barra lateral); STREEMU_FILME=0 esconde.</summary>
    static readonly bool FilmeLigado = Environment.GetEnvironmentVariable("STREEMU_FILME") != "0" || TesteFilme != null;

    FilmeTela? _filmeForm;
    /// <summary>Onde a tela do filme fica na janela principal (no lugar do Início, 11.1).</summary>
    readonly Panel _filmeHost = new() { Dock = DockStyle.Fill, Visible = false, BackColor = Theme.VideoMargin };
    bool _filmeView;
    Form? _filmeJanelaTeste;
    /// <summary>Filmes abertos na sala: id do anfitrião → anúncio (o anfitrião repete a cada 5 s; some depois de 20 s sem repetir).</summary>
    readonly Dictionary<string, (JsonObject Msg, DateTime Visto)> _filmeAbertos = new();
    /// <summary>O meu filme, quando sou o anfitrião.</summary>
    JsonObject? _filmeMeu;
    /// <summary>Ligações do filme (sessão → ligação), separadas das da live: nada de player da live nem contagem de espectadores.</summary>
    readonly Dictionary<string, PeerLink> _filmeLinks = new();
    string? _filmeErro;
    readonly System.Windows.Forms.Timer _filmeTimer = new() { Interval = 5000 };
    string? _filmeVisto;
    string? _filmeSessaoSid;

    static string Tipo(JsonObject m, string chave = "type") => m[chave]?.GetValue<string>() ?? "";

    void InitFilme()
    {
        _side.FilmeVisivel = FilmeLigado;
        if (!FilmeLigado) return;
        _side.FilmeClicked += () => AbrirFilme();
        AtualizarFilmeLateral();
        // teste do layout: STREEMU_TEST_VIEW=filme abre a tela do filme; filme:<arquivo> já abre o arquivo como anfitrião (sem som)
        if (Environment.GetEnvironmentVariable("STREEMU_TEST_VIEW") is { } v && v.StartsWith("filme"))
            Shown += (_, _) => BeginInvoke(async () =>
            {
                var tela = AbrirFilme();
                if (v.Length <= 6) return;
                for (int i = 0; i < 50 && _room is not { Connected: true }; i++) await Task.Delay(100);
                await tela.EscolherAsync(v[6..]);
            });
        _filmeTimer.Tick += (_, _) => FilmeTick();
        _ = SerieFilmeAsync();
        _filmeTimer.Start();
    }

    // ------------------------------------------------------------------ janela

    /// <summary>
    /// A tela do filme no lugar do Início (11.1). Se já existe, só volta para ela. No teste automático ela vai numa janela fora da tela, porque a janela principal do teste fica minimizada.
    /// </summary>
    FilmeTela AbrirFilme()
    {
        if (_filmeForm is { IsDisposed: false } aberto)
        { MostrarFilme(); aberto.MostrarCatalogo(); return aberto; }
        if (_filmeForm is { } velha)
        {
            _filmeForm = null;
            velha.Parent?.Controls.Remove(velha);
            velha.Dispose();
            _filmeJanelaTeste?.Close(); _filmeJanelaTeste = null;
        }
        var lig = new FilmeLigacao(Nick,
                () => _room is { Connected: true } ? null : "Sem a sala do grupo agora: confira a internet. Ela volta sozinha.",
                () => _filmeAbertos.Values.OrderByDescending(v => v.Visto).Select(v => v.Msg).FirstOrDefault(),
                AnunciarSala, EntrarSalaAsync, SairSala, EnviarSala,
                () => LigacaoDoArquivo()?.Tunnel.PeerClockOffsetUs,
                () => _filmeLinks.Values.Select(l => Math.Max(0, l.Tunnel.RttMs)).DefaultIfEmpty(0).Max(),
                () => RelayConfig.Load(_state),
                () => LigacaoDoArquivo() is { Connected: true } link ? link.Tunnel : null,
                () => _filmeAbertos.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value.Msg).ToArray(),
                () => _streamTela is { IsDisposed: false } || _streamAbrindo,
                RetomarStreamFilme, AvatarPorNome, CapaDe);
        var tela = _filmeForm = new FilmeTela(lig, teste: TestDir != null || Environment.GetEnvironmentVariable("STREEMU_TEST_VIEW")?.StartsWith("filme") == true);
        tela.Telemetria += (tipo, campos) =>
        {
            if (tipo == "filme_fim" && campos != null) ComplementarFimFilme(campos); // MainForm.FilmeStream.cs: quem assistiu pela transmissão
            _teleSessao?.Evento(tipo, campos);
            if (tipo == "filme_fim") GravarSerieFilme();
        };
        tela.TelaCheiaPedida += AlternarTelaCheiaCinema;
        tela.Saiu += () => { if (_fullscreenStage) { ToggleFullscreen(); tela.EmTelaCheia = false; } ShowView(stage: false); };
        ConectarStreamFilme(tela); // MainForm.FilmeStream.cs: filme pela transmissão (spec 011)
        if (TestDir != null)
        {
            var j = _filmeJanelaTeste = new JanelaFilmeTeste();
            j.Controls.Add(tela);
            j.Show();
        }
        else
        {
            _filmeHost.Controls.Add(tela);
            MostrarFilme();
        }
        return tela;
    }

    /// <summary>A foto de quem está numa sessão, pelo apelido (o anúncio do filme leva apelidos, não ids).</summary>
    Ui.Avatars.AvatarImage? AvatarPorNome(string nick) =>
        nick == Nick ? MyAvatarImage() : _room?.Members.FirstOrDefault(m => m.Nick == nick) is { } m ? AvatarOfMember(m.Id) : null;

    void MostrarFilme()
    {
        HideFlyout();
        if (_preparationBusy) return;
        if (ContaBloqueia()) return;
        _preparationView = false;
        _filmeView = true;
        _arquivosView = false; // MainForm.ArquivosTela.cs
        _stageView = false;
        _settingsView = false;
        ApplyView();
    }

    void AtualizarFilmeLateral()
    {
        // só "Cinema", como no mockup (o selo BETA já ocupa o fim da linha); quem abriu um filme aparece no aviso e na lista do Cinema
        const string texto = "Cinema";
        _side.FilmeTexto = texto;
        if (_flyout != null) _flyout.Panel.FilmeTexto = texto;
        _filmeForm?.AtualizarAnuncios();
    }

    /// <summary>O card de quem ainda não entrou (11.1) mostra se o filme está no aguardo, rolando ou pausado, e em que ponto.</summary>
    JsonObject ComEstado(JsonObject m)
    {
        if (_filmeForm?.Sessao is { Anfitriao: true } s)
        {
            m["estado"] = s.Tocando ? "rolando" : s.Comecou ? "pausado" : "aguardo";
            m["pos"] = Math.Round(s.Posicao, 1);
            // quem assiste pela transmissão também está na sessão (caso de 2026-11-18: só aparecia quem tinha o arquivo), sem contar duas vezes
            var quem = s.Amigos.Keys.Concat(s.FluxoNomes?.Invoke() ?? []).Distinct().ToList();
            m["pessoas"] = quem.Count;
            // quem já entrou, para as bolinhas do cartão no Cinema de quem ainda não entrou (campo novo: versão antiga ignora)
            m["quem"] = new JsonArray(quem.Select(k => (JsonNode?)k).ToArray());
        }
        return m;
    }

    // ------------------------------------------------------------------ sala do grupo

    /// <summary>Mensagens da sala com tipo filme_* (chamado pelo OnRoomMessage do MainForm.Grupo.cs).</summary>
    void OnFilmeSala(string from, string nick, JsonObject msg)
    {
        if (!FilmeLigado) return;
        string sid = msg["sessao"]?.GetValue<string>() ?? "";
        _filmeLinks.TryGetValue(sid, out var link);
        // o "sessao" vem do app do amigo (só o "from" é carimbado pela sala): só o outro lado da ligação pode mexer nela
        if (link != null && link.PeerId != from)
        {
            Log.Info("[filme] Sala: mensagem de sessão de outra pessoa; ignorei.");
            link = null;
        }
        switch (Tipo(msg, "tipo"))
        {
            case "filme_aberto":
                bool novo = !_filmeAbertos.ContainsKey(from);
                msg["de"] = from;
                msg["recebido"] = Environment.TickCount64; // o card anda a posição de quem está rolando a partir daqui
                _filmeAbertos[from] = (msg, DateTime.Now);
                if (novo) { Log.Info($"[filme] {nick} abriu \"{msg["nome"]}\"."); AvisoFilme(from, nick, msg["nome"]?.GetValue<string>() ?? "filme"); } // MainForm.Avisos.cs
                PedirCapaSeFaltar(from, msg); // MainForm.FilmeCapa.cs
                AtualizarFilmeLateral();
                break;
            case CapaDoFilme.TipoPedido:
                OnCapaPedida(from, msg);
                break;
            case CapaDoFilme.TipoImagem:
                OnCapaRecebida(from, nick, msg);
                break;
            case "filme_fechado":
                if (_filmeAbertos.Remove(from)) AtualizarFilmeLateral();
                StreamAnfitriaoFechou(from); // MainForm.FilmeStream.cs: fecha o player de quem assiste pela transmissão
                if (_filmeForm?.AnfitriaoSelecionado == from)
                    _filmeForm.Receber(new JsonObject { ["type"] = "filme_fim", ["filme"] = msg["filme"]?.DeepClone() });
                break;
            case "filme_quero":
                _ = AceitarFilmeAsync(from, nick, sid, msg["codigo"]?.GetValue<string>() ?? "", msg["filme"]?.GetValue<string>() ?? "");
                break;
            case "filme_aceito" when link is { IAmSender: false }:
                try { link.Connect(msg["codigo"]?.GetValue<string>() ?? ""); }
                catch (FormatException e) { _filmeErro = $"{nick} mandou um código inválido ({e.Message}). Vocês estão em versões diferentes?"; FimLigacaoFilme(sid); }
                break;
            case "filme_recusado" when link != null:
                _filmeErro = msg["motivo"]?.GetValue<string>() ?? $"{nick} recusou.";
                FimLigacaoFilme(sid);
                break;
            case "filme_parar" when link != null:
                FimLigacaoFilme(sid);
                _filmeForm?.AmigoSaiu(nick);
                break;
        }
    }

    /// <summary>A cada 5 s: o anfitrião repete o anúncio (quem entrou na sala depois também fica sabendo); anúncio sem repetir some.</summary>
    void FilmeTick()
    {
        if (_filmeMeu != null && _room is { Connected: true } room)
            foreach (var f in room.Friends) _ = room.SendTo(f.Id, AnuncioSala(_filmeMeu));
        int antes = _filmeAbertos.Count;
        foreach (var k in _filmeAbertos.Where(kv => DateTime.Now - kv.Value.Visto > TimeSpan.FromSeconds(20)).Select(kv => kv.Key).ToList()) _filmeAbertos.Remove(k);
        if (_filmeAbertos.Count != antes) AtualizarFilmeLateral();
        // filme fechado: para de servir o arquivo
        if (_filmeForm?.ArquivoAnfitriao == null)
            foreach (var t in _filmeEnvios.Keys.ToList()) { var e = _filmeEnvios[t]; _filmeEnvios.Remove(t); Task.Run(e.Dispose); }
    }

    // ------------------------------------------------------------------ telemetria (spec 009)

    readonly System.Text.StringBuilder _filmeCsv = new();
    int _filmeCsvLinhas;
    long _filmeEnvioAntes;
    bool _filmeSerieEncerrada;

    /// <summary>
    /// filme.csv na pasta da sessão (sobe como o log, igual ao quadros.csv da live): por segundo, onde o filme está, quanto o player
    /// está fora do relógio comum e a velocidade da correção (7.4), o relógio comum, quadros perdidos no mpv, o envio do arquivo no
    /// anfitrião e o download em quem baixa. Cruzando o dos dois PCs pela hora, dá para achar quando e por que dessincronizou sem
    /// repetir o teste com ninguém. Junta 10 linhas e grava de uma vez; sem filme, não grava nada.
    /// </summary>
    async Task SerieFilmeAsync()
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        string N(double v, string f = "0.#") => double.IsNaN(v) ? "" : v.ToString(f, ci);
        while (!IsDisposed && !_filmeSerieEncerrada)
        {
            await Task.Delay(1000);
            if (IsDisposed || _filmeSerieEncerrada) break;
            try
            {
                var tela = _filmeForm; var s = tela?.Sessao; var d = tela?.UltimoDownload;
                bool baixando = tela is { Ocupado: true } && s == null && d != null;
                var st = s == null && !baixando ? _streamTela : null; // quem assiste pela transmissão (spec 011) não tem Sessao: papel "stream"
                long envio = _filmeEnvios.Values.Sum(e => e.Emissor?.BytesEnviados ?? 0);
                long envioDelta = Math.Max(0, envio - _filmeEnvioAntes);
                _filmeEnvioAntes = envio;
                if (_teleSessao == null || (s == null && !baixando && st == null && _filmeEnvios.Count == 0)) { GravarSerieFilme(); continue; }
                double perdidos = double.NaN, perdidosDec = double.NaN;
                if (tela?.Player is { } mpv && s != null) { perdidos = await mpv.NumAsync("frame-drop-count"); perdidosDec = await mpv.NumAsync("decoder-frame-drop-count"); }
                if (st != null) perdidos = st.QuadrosPerdidos;
                // A leitura do player pode terminar depois de o filme ou o app fecharem: o último lote já foi salvo.
                if (_filmeSerieEncerrada || !ReferenceEquals(tela?.Sessao, s)) continue;
                var t = s is { Anfitriao: true } ? null : _filmeLinks.Values.FirstOrDefault(l => !l.IAmSender)?.Tunnel;
                var fluxo = SerieStream(); // MainForm.FilmeStream.cs
                if (_filmeCsvLinhas == 0 && _filmeCsv.Length == 0 && !File.Exists(Path.Combine(_teleSessao.Dir, "filme.csv")))
                    _filmeCsv.Append("hora;papel;tocando;posicao_s;erro_ms;velocidade;relogio_offset_us;relogio_ida_volta_ms;caminho;quadros_perdidos;quadros_perdidos_decoder;envio_mb_s;envios;download_fase;download_pct;download_mb_s;amigos;"
                        + "stream_epoca;stream_cache_s;stream_parado_cache;stream_envio_velocidade;stream_espectadores;stream_envio_mbps\n");
                _filmeCsv.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(';')
                    .Append(st != null ? "stream" : s == null ? "baixando" : s.Anfitriao ? "anfitriao" : "espectador").Append(';')
                    .Append(s is { Tocando: true } || st is { Tocando: true } ? 1 : 0).Append(';')
                    .Append(st != null ? N(st.PosicaoFilme, "0.###") : s == null ? "" : N(s.Posicao, "0.###")).Append(';')
                    .Append(st != null ? N(st.UltimoErroMs) : s == null ? "" : N(s.UltimoErroMs)).Append(';')
                    .Append(st != null ? N(st.Velocidade, "0.###") : s == null ? "" : N(s.Velocidade, "0.###")).Append(';')
                    .Append(t?.PeerClockOffsetUs?.ToString(ci) ?? "").Append(';')
                    .Append(t == null ? "" : N(t.PeerClockRttMs, "0.##")).Append(';')
                    .Append(t?.Target is { } c ? (c.Kind == CandKind.Direct ? "direto" : "relay") : "").Append(';')
                    .Append(N(perdidos, "0")).Append(';').Append(N(perdidosDec, "0")).Append(';')
                    .Append(N(envioDelta / 1e6, "0.##")).Append(';').Append(_filmeEnvios.Count).Append(';')
                    .Append(baixando ? d!.Atual.Fase : "").Append(';')
                    .Append(baixando ? d!.Atual.Pct.ToString(ci) : "").Append(';')
                    .Append(baixando ? N(d!.Atual.BytesPorSegundo / 1e6, "0.##") : "").Append(';')
                    .Append(s is { Anfitriao: true } ? string.Join("|", s.Amigos.Select(kv => $"{kv.Key}={kv.Value}")).Replace(";", ",") : "").Append(';')
                    .Append(fluxo.Epoca).Append(';').Append(fluxo.CacheS).Append(';').Append(fluxo.ParadoCache).Append(';')
                    .Append(fluxo.Velocidade).Append(';').Append(fluxo.Espectadores).Append(';').Append(fluxo.Mbps).Append('\n');
                if (++_filmeCsvLinhas >= 10) GravarSerieFilme();
            }
            catch (Exception e) { Log.Info($"[filme] série por segundo: {e.Message}"); }
        }
    }

    void GravarSerieFilme()
    {
        if (_filmeCsv.Length == 0 || _teleSessao == null) return;
        _teleSessao.Acrescentar("filme.csv", _filmeCsv.ToString());
        _filmeCsv.Clear();
        _filmeCsvLinhas = 0;
    }

    /// <summary>O filme na marcação de problema (Ctrl+Alt+B): o que estava acontecendo nele naquela hora.</summary>
    JsonObject? FilmeTele()
    {
        var tela = _filmeForm;
        if (tela?.Sessao is { } s)
            return new JsonObject
            {
                ["papel"] = s.Anfitriao ? "anfitriao" : "espectador", ["filme"] = s.Filme, ["tocando"] = s.Tocando, ["posicao_s"] = Math.Round(s.Posicao, 1),
                ["erro_ms"] = double.IsNaN(s.UltimoErroMs) ? null : Math.Round(s.UltimoErroMs, 1), ["velocidade"] = s.Velocidade,
                ["amigos"] = s.Anfitriao ? string.Join(",", s.Amigos.Select(kv => $"{kv.Key}={kv.Value}")) : null, ["envios"] = _filmeEnvios.Count,
            };
        if (tela?.UltimoDownload is { } d && tela.Ocupado) return new JsonObject { ["papel"] = "baixando", ["fase"] = d.Atual.Fase, ["pct"] = d.Atual.Pct };
        return _filmeEnvios.Count > 0 ? new JsonObject { ["envios"] = _filmeEnvios.Count } : null;
    }

    // ------------------------------------------------------------------ transferência do arquivo (4.2)

    /// <summary>Túnel → envio do arquivo para quem está do outro lado.</summary>
    readonly Dictionary<Tunnel, Envio> _filmeEnvios = new();

    /// <summary>Anfitrião: um amigo pediu o arquivo (filme_baixar, repetido até chegar a resposta com o manifesto).</summary>
    void ServirArquivo(Tunnel t, JsonObject m)
    {
        if (_filmeForm is not { Sessao: { Anfitriao: true } s, ArquivoAnfitriao: string arq } || m["filme"]?.GetValue<string>() != s.Filme) return;
        if (!_filmeEnvios.TryGetValue(t, out var e))
        {
            // 4.2: com live saindo deste PC, a transferência fica só com a sobra do upload
            _filmeEnvios[t] = e = new Envio(t, arq, s.Filme, s.Nome, () => _sender.Running || _links.Count > 0);
            Log.Info("[filme] um amigo pediu o arquivo; preparando para mandar.");
        }
        e.Responder();
    }

    JsonObject AnuncioSala(JsonObject a) => ComCapa(ComEstado(new() { ["tipo"] = "filme_aberto", ["filme"] = a["filme"]?.DeepClone(), ["nome"] = a["nome"]?.DeepClone(), ["anfitriao"] = a["anfitriao"]?.DeepClone(), ["stream"] = _filmeStream != null }));

    void AnunciarSala(JsonObject? a)
    {
        var antes = _filmeMeu;
        _filmeMeu = a;
        if (a == null) { FimFilmeDoAnfitriao(); _capaMinha = null; } // MainForm.FilmeStream.cs: desliga o envio pela transmissão
        else TirarCapaSeFaltar(a); // MainForm.FilmeCapa.cs
        AtualizarFilmeLateral();
        if (_room is not { Connected: true } room) return;
        if (a != null)
        {
            foreach (var f in room.Friends) _ = room.SendTo(f.Id, AnuncioSala(a));
            if (antes?["filme"]?.ToJsonString() != a["filme"]?.ToJsonString() && a["fila"] == null) ContarQueAbriUmFilme(); // MainForm.Atividades.cs: só quando o filme é outro, sem o nome
            return;
        }
        foreach (var f in room.Friends) _ = room.SendTo(f.Id, new JsonObject { ["tipo"] = "filme_fechado", ["filme"] = antes?["filme"]?.DeepClone() });
        // o filme_fim já saiu pelas ligações (3 vezes em 300 ms): fecha depois dele
        var links = _filmeLinks.Keys.ToList();
        Task.Delay(1000).ContinueWith(_ => Ui(() => { foreach (var sid in links) FimLigacaoFilme(sid); }));
    }

    // tetos de ligações de envio do modo filme: o upload do anfitrião é o que acaba (ver MaxEspectadores na live)
    const int MaxLigacoesFilmePorPessoa = 4, MaxLigacoesFilme = 24;

    /// <summary>Anfitrião: um amigo quer entrar. Ligação nova só para o filme, como o AcceptViewerAsync da live.</summary>
    async Task AceitarFilmeAsync(string from, string nick, string sid, string code, string filme)
    {
        if (_room == null || sid == "" || _filmeLinks.ContainsKey(sid)) return;
        if (_filmeMeu == null || _filmeMeu["filme"]?.GetValue<string>() != filme)
        {
            await _room.SendTo(from, new JsonObject { ["tipo"] = "filme_recusado", ["sessao"] = sid, ["motivo"] = $"O filme de {Nick} já fechou." });
            return;
        }
        // Aqui NÃO vale "uma ligação por pessoa" como na live: a mesma pessoa abre legitimamente mais de uma (player pela transmissão e download do arquivo,
        // ou dois players, caso de 11/11). Então só há tetos folgados, para um app adulterado não abrir ligações sem fim (socket, threads e relay cada).
        // Os pedidos em andamento também contam (o CreateAsync leva segundos e o link só entra em _filmeLinks depois dele: uma rajada passaria toda).
        // OnFilmeSala roda na thread da tela (Ui) e o await volta para ela: sem lock.
        var doAmigo = _filmeLinks.Values.Count(l => l.IAmSender && l.PeerId == from) + _filmeAceitando.GetValueOrDefault(from);
        if (doAmigo >= MaxLigacoesFilmePorPessoa || _filmeLinks.Values.Count(l => l.IAmSender) + _filmeAceitando.Values.Sum() >= MaxLigacoesFilme)
        {
            Log.Info($"[filme] {nick} pediu para entrar, mas já há ligações demais ({doAmigo} dele); recusei.");
            await _room.SendTo(from, new JsonObject { ["tipo"] = "filme_recusado", ["sessao"] = sid, ["motivo"] = $"{Nick} já tem gente demais ligada neste filme. Feche o filme aberto aí e tente de novo." });
            return;
        }
        _filmeAceitando[from] = _filmeAceitando.GetValueOrDefault(from) + 1;
        try
        {
            PeerLink link;
            PeerLink? criado = null;
            try
            {
                criado = await PeerLink.CreateAsync(sid, from, nick, iAmSender: true, _localMode, Nick, CarrierIp, LinkRelay, "filme");
                criado.Connect(code);
                link = criado;
            }
            catch (Exception e)
            {
                criado?.Dispose(); // código recusado depois de a ligação existir: fecha (mesmo motivo do AcceptViewerAsync)
                Log.Info($"[filme] {nick} quis entrar, mas não deu para preparar a conexão: {e.Message}");
                await _room.SendTo(from, new JsonObject { ["tipo"] = "filme_recusado", ["sessao"] = sid, ["motivo"] = $"{Nick} não conseguiu preparar a conexão ({e.Message})." });
                return;
            }
            AdicionarLinkFilme(link);
            Log.Info($"[filme] {nick} está entrando no filme.");
            await _room.SendTo(from, new JsonObject { ["tipo"] = "filme_aceito", ["sessao"] = sid, ["codigo"] = link.MyCode });
        }
        finally
        {
            if (_filmeAceitando.GetValueOrDefault(from) <= 1) _filmeAceitando.Remove(from); else _filmeAceitando[from]--;
        }
    }

    /// <summary>Pedidos de entrada sendo preparados agora, por pessoa (ver AceitarFilmeAsync). Só a thread da tela mexe.</summary>
    readonly Dictionary<string, int> _filmeAceitando = new();

    /// <summary>Quem assiste: liga ao anfitrião e espera a ligação e o relógio comum (o primeiro pong novo).</summary>
    async Task<string?> EntrarSalaAsync(JsonObject a)
    {
        var result = await LigarAoAnfitriaoAsync(a);
        if (result.Erro == null) _filmeSessaoSid = result.Link?.SessionId;
        return result.Erro;
    }

    PeerLink? LigacaoDoArquivo() => _filmeSessaoSid != null && _filmeLinks.TryGetValue(_filmeSessaoSid, out var link) ? link : null;

    /// <summary>Como o <see cref="EntrarSalaAsync"/>, mas devolve a ligação criada (o "Assistir agora" usa a dele, não a última da lista).</summary>
    async Task<(string? Erro, PeerLink? Link)> LigarAoAnfitriaoAsync(JsonObject a)
    {
        string host = a["de"]?.GetValue<string>() ?? "";
        if (_room == null || _room.Find(host) is not { } m) return ("O anfitrião saiu da sala do grupo.", null);
        string sid = RandomId(12);
        _filmeErro = null;
        PeerLink link;
        try { link = await PeerLink.CreateAsync(sid, host, m.Nick, iAmSender: false, _localMode, Nick, CarrierIp, LinkRelay, "filme"); }
        catch (Exception e) { return ($"Não deu para preparar a conexão com {m.Nick}: {e.Message}", null); }
        AdicionarLinkFilme(link);
        await _room.SendTo(host, new JsonObject { ["tipo"] = "filme_quero", ["sessao"] = sid, ["codigo"] = link.MyCode, ["filme"] = a["filme"]?.DeepClone() });
        var ate = DateTime.Now.AddSeconds(25);
        while (DateTime.Now < ate)
        {
            if (!_filmeLinks.ContainsKey(sid)) return (_filmeErro ?? $"{m.Nick} não aceitou.", null);
            if (link.Connected && link.Tunnel.PeerClockOffsetUs != null)
            {
                Log.Info($"[filme] ligado a {m.Nick} em {link.Tunnel.ConnectMs} ms; relógio com ida e volta de {link.Tunnel.PeerClockRttMs:N1} ms.");
                return (null, link);
            }
            await Task.Delay(100);
        }
        bool ligou = link.Connected;
        SairLigacao(sid); // só esta: as outras ligações de quem assiste (um player pela transmissão aberto) continuam
        return (ligou ? $"{m.Nick} tem uma versão do app sem o modo filme (o relógio não respondeu). Ele precisa atualizar."
                      : $"Não consegui conectar com {m.Nick} em 25 s. Tente de novo; se repetir, marque um problema.", null);
    }

    void SairSala()
    {
        if (_filmeSessaoSid is { } sid) { _filmeSessaoSid = null; SairLigacao(sid); }
    }

    /// <summary>Quem assiste sai de uma ligação só: avisa o anfitrião e a encerra.</summary>
    void SairLigacao(string sid)
    {
        if (!_filmeLinks.TryGetValue(sid, out var link) || link.IAmSender) return;
        _ = _room?.SendTo(link.PeerId, new JsonObject { ["tipo"] = "filme_parar", ["sessao"] = sid });
        FimLigacaoFilme(sid);
    }

    void EnviarSala(JsonObject m)
    {
        // anfitrião manda para todos que entraram; quem assiste, para o anfitrião. O SendControl carimba um id: cópia por ligação.
        bool anfitriao = _filmeMeu != null;
        foreach (var link in _filmeLinks.Values.Where(l => anfitriao ? l.IAmSender : l.SessionId == _filmeSessaoSid).ToList())
            if (m.DeepClone() is JsonObject copia) link.Tunnel.SendControl(copia);
    }

    void AdicionarLinkFilme(PeerLink link)
    {
        _filmeLinks[link.SessionId] = link; // quem assiste e quem serve: o anfitrião troca de episódio sem fechar nenhuma (FilmeTela.Fila.cs)
        link.Control += m =>
        {
            if (!Tipo(m).StartsWith("filme_")) return;
            Ui(() =>
            {
                if (Tipo(m) == "filme_fim" && link.SessionId == _streamSid) StreamAnfitriaoFechou(link.PeerId);
                if (Tipo(m).StartsWith("filme_stream") || (Tipo(m) is "filme_estado" or "filme_permissoes" or "filme_pessoas" or "filme_fila" or "filme_trocar" && link.SessionId == _streamSid)) FilmeStreamControle(link, m);
                else if (Tipo(m) == "filme_baixar") ServirArquivo(link.Tunnel, m);
                else if (_filmeMeu != null && link.IAmSender || link.SessionId == _filmeSessaoSid) _filmeForm?.Receber(m);
            });
        };
    }

    void FimLigacaoFilme(string sid)
    {
        if (!_filmeLinks.Remove(sid, out var link)) return;
        if (_filmeEnvios.Remove(link.Tunnel, out var envio)) Task.Run(envio.Dispose);
        FimStreamDaLigacao(sid);
        Log.Info($"[filme] ligação com {link.PeerNick} encerrada.");
        Task.Run(link.Dispose);
    }

    // ------------------------------------------------------------------ teste automático

    /// <summary>
    /// STREEMU_TEST_FILME=&lt;arquivo&gt;: A abre como anfitrião, os outros entram com o mesmo arquivo; A começa, pausa, pula e continua.
    /// Só pela sala (STREEMU_TEST_ROOM=1). Cada um grava &lt;papel&gt;-filme.json com o erro contra o relógio comum.
    /// Janelas fora da tela e sem som (regra 2). STREEMU_TEST_FILME_AMIGOS = quantos A espera entrar (padrão 1).
    /// </summary>
    async Task TesteFilmeAsync(string arquivo)
    {
        if (Environment.GetEnvironmentVariable("STREEMU_TEST_FILME_STREAM") == "1") { await TesteFilmeStreamAsync(arquivo); return; } // MainForm.FilmeStream.cs
        await Task.Delay(1000);
        var form = AbrirFilme();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Tunnel? t = null;
        // STREEMU_TEST_FILME_FECHAR_EM=<s>: fecha o app N s depois de o filme começar a tocar, com ele ainda aberto (o fechamento de verdade, não o fim do roteiro).
        // Não dá para usar STREEMU_TEST_SECONDS: pela sala o roteiro só começa depois do login e da entrada na sala, e ele já é o teto de outros cenários do filme.
        if (int.TryParse(Environment.GetEnvironmentVariable("STREEMU_TEST_FILME_FECHAR_EM"), out int fecharEm) && fecharEm > 0)
            _ = Task.Run(async () =>
            {
                var ate = DateTime.Now.AddSeconds(90);
                while (form.Sessao is not { Tocando: true } && DateTime.Now < ate) await Task.Delay(50);
                await Task.Delay(fecharEm * 1000);
                Log.Info($"teste-filme: fechando o app {fecharEm} s depois de o filme começar.");
                BeginInvoke(ExitApp);
            });
        if (TestRole == "A")
        {
            int esperados = int.TryParse(Environment.GetEnvironmentVariable("STREEMU_TEST_FILME_AMIGOS"), out var n) ? n : 1;
            int limite = int.TryParse(Environment.GetEnvironmentVariable("STREEMU_TEST_FILME_ESPERA"), out var lim) ? lim : 35;
            // STREEMU_TEST_FILME_TOCAR_JA=1: A aperta ▶ assim que os N amigos entram (baixando ou conferindo); com "começar quando todos
            // estiverem prontos" (4.3, o padrão) o filme só começa quando o último fica pronto
            bool tocarJa = Environment.GetEnvironmentVariable("STREEMU_TEST_FILME_TOCAR_JA") == "1";
            await form.EscolherAsync(arquivo);
            int maisBaixando = 0;
            while (form.Sessao is { } espera && sw.Elapsed.TotalSeconds < limite)
            {
                if (tocarJa ? espera.Tocando : espera.Amigos.Count(kv => kv.Value == "pronto") >= esperados) break;
                if (tocarJa && espera.Amigos.Count >= esperados && !espera.Aguardando) espera.Tocar(10);
                // o "baixando" de quem baixa (4.3) vai para o relatório: o retrato com mais gente baixando ao mesmo tempo
                int baixando = espera.Amigos.Count(kv => kv.Value.StartsWith("baixando"));
                if (baixando > maisBaixando) { maisBaixando = baixando; _filmeVisto = string.Join(",", espera.Amigos.Select(kv => $"{kv.Key}={kv.Value}")); }
                await Task.Delay(100);
            }
            if (form.Sessao is { } s)
            {
                Log.Info($"teste-filme: {s.Amigos.Count} amigo(s) prontos; começo.");
                t = _filmeLinks.Values.FirstOrDefault()?.Tunnel;
                if (!s.Tocando) s.Tocar(10);
                await Task.Delay(5000);
                s.Ir(60); // D4: todos pausam no ponto novo, carregam e voltam juntos
                var pulo = System.Diagnostics.Stopwatch.StartNew();
                await Task.Delay(200);
                while (!s.Tocando && pulo.Elapsed.TotalSeconds < 7) await Task.Delay(50);
                await Task.Delay(5000);
                s.Pausar();
                await Task.Delay(1500);
                s.Tocar();
                await Task.Delay(5000);
            }
        }
        else
        {
            while (_filmeAbertos.Count == 0 && sw.Elapsed.TotalSeconds < 25) await Task.Delay(100);
            // STREEMU_TEST_FILME_BAIXAR=<pasta>: não tem o arquivo e baixa do anfitrião; STREEMU_TEST_FILME_FECHAR=<%>: o app "fecha" no meio
            // (sai na hora, sem salvar nada além do que o download já salvou) para a próxima abertura continuar de onde parou
            if (Environment.GetEnvironmentVariable("STREEMU_TEST_FILME_BAIXAR") is { Length: > 0 } pasta)
            {
                if (int.TryParse(Environment.GetEnvironmentVariable("STREEMU_TEST_FILME_FECHAR"), out int fechar) && fechar > 0)
                    _ = Task.Run(async () =>
                    {
                        while (true)
                        {
                            if (form.UltimoDownload?.Atual is { Fase: "baixando" } p && p.Pct >= fechar)
                            {
                                Log.Info($"teste-filme: fechando o app no meio do download ({p.Pct}%).");
                                Environment.Exit(0);
                            }
                            await Task.Delay(10);
                        }
                    });
                await form.BaixarAsync(pasta);
            }
            else await form.EscolherAsync(arquivo);
            t = _filmeLinks.Values.FirstOrDefault()?.Tunnel; // o fim do filme fecha a ligação: guarda antes
            sw.Restart();
            while (form.Sessao != null && sw.Elapsed.TotalSeconds < 60) await Task.Delay(200);
        }
        var r = form.UltimaSessao?.Relatorio() ?? new JsonObject { ["erro"] = "sem sessão" };
        r["pela_sala"] = true; // o campo fica: relatórios e scripts antigos o leem
        r["relogio_offset_us"] = t?.PeerClockOffsetUs;
        r["relogio_ida_volta_ms"] = t == null ? -1 : Math.Round(t.PeerClockRttMs, 2);
        if (form.UltimaSessao is { Anfitriao: true } h) r["amigos"] = string.Join(",", h.Amigos.Select(kv => $"{kv.Key}={kv.Value}"));
        if (_filmeVisto != null) r["amigos_baixando"] = _filmeVisto;
        RelatorioCapa(r); // MainForm.FilmeCapa.cs
        if (form.UltimoDownload is { } d) { r["download"] = d.Relatorio(); r["download"]!["fase"] = d.Atual.Fase; }
        File.WriteAllText(Path.Combine(TestDir!, TestRole + "-filme.json"), r.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Log.Info("teste-filme: " + r.ToJsonString());
        form.Fechar();
        _filmeJanelaTeste?.Close();
    }

}
