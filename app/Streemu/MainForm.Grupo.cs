using System.Net;
using System.Text.Json.Nodes;
using Streemu.Discord;
using Streemu.Media;
using Streemu.Net;
using Streemu.Ui;
using Streemu.Ui.Avatars;
using Streemu.Ui.Multistream;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// Spec 003: sala do grupo, assistir sem código (uma <see cref="PeerLink"/> por espectador/transmissão), Rich Presence
/// com botão Assistir, link streemu://, bandeja e janela Transmitir. Fica num arquivo à parte do MainForm.cs de
/// propósito: aquele arquivo concentra conflitos entre os três; aqui ele só chama os ganchos (Init/Tick/OnLive...).
///
/// Mensagens entre apps pela sala ("msg" do {t:"para"}), todas com "sessao" (id da ligação):
///   quero_assistir {sala, codigo}  quem assiste -> quem transmite (sala = id da transmissão do link do Discord)
///   aceito {codigo}                quem transmite -> quem assiste; os dois furam o NAT com os códigos trocados
///   recusado {motivo}              a transmissão já acabou
///   parar {}                       qualquer lado encerra a ligação (fechou o player / parou de transmitir)
/// </summary>
public sealed partial class MainForm
{
    /// <summary>Instância única e argumentos de abertura (o Program preenche antes de criar a tela).</summary>
    public static SingleInstance? Instance { get; set; }
    public static string[] LaunchArgs { get; set; } = Array.Empty<string>();

    static readonly bool TestRoom = Environment.GetEnvironmentVariable("STREEMU_TEST_ROOM") == "1";

    ISala? _room;
    DiscordPresence? _discord;
    NotifyIcon? _tray;
    string? _roomServerUrl;
    string? _streamId;
    long _liveSinceMs;
    string _liveGame = "";
    bool _fakeLive, _exiting, _startHidden;
    /// <summary>A live está fechando: os espectadores caem juntos e só toca o som de "live fechou", não um "saiu" por pessoa.</summary>
    bool _closingLive;
    string? _pendingWatch;
    DateTime _pendingWatchAt;
    LiveInfo? _lastLive;
    int _groupTick;
    readonly Dictionary<string, PeerLink> _links = new();
    readonly Dictionary<string, Viewer> _linkViewers = new();
    readonly Dictionary<string, DateTime> _linkLastGood = new();
    readonly HashSet<string> _linkEverConnected = new();
    /// <summary>Ligações cujo amigo sumiu da sala com o túnel ainda de pé (para registrar no log uma vez só).</summary>
    readonly HashSet<string> _linkPeerAway = new();

    readonly Label _groupStatus = new() { AutoSize = true, MaximumSize = new Size(640, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 10, 0, 0) };
    readonly Label _liveSummaryText = new() { AutoSize = true, Font = UiTypography.TitleFont(), Text = "Ao vivo agora", Margin = new Padding(0, 4, 12, 4) };
    readonly Label _emptyLive = new() { AutoSize = true, MaximumSize = new Size(640, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 8, 0, 12), Text = "Ninguém está ao vivo agora. Quando um amigo começar a transmitir, o card dele aparece aqui. Você também pode transmitir." };
    readonly Button _watchAllLive = new() { Text = "Assistir todos juntos", AutoSize = true, Visible = false };
    readonly Button _stopLive = new() { Text = "Parar transmissão", AutoSize = true, Visible = false };

    bool Automated => _localMode || TestDir != null;

    // ------------------------------------------------------------------ montagem

    /// <summary>
    /// A home (modelo escolhido em 2026-11-09): o título "Ao vivo agora" com os botões à direita, a fileira de avatares, os cartões grandes e, se ninguém
    /// está ao vivo, um convite. Embaixo, quem está só online e o estado da sala. Sem caixa em volta: é a própria página.
    /// </summary>
    Control BuildHomeColumn()
    {
        _homePage = new HomeView(_liveSummaryText, _emptyLive, _groupStatus, _watchAllLive, _conviteCards, _liveCards);
        Theme.StyleButton(_watchAllLive, false);
        _watchAllLive.Click += (_, _) => WatchAllLive();
        LigarEventosPrivada();
        _stopLive.Click += (_, _) => EncerrarLive("Sua transmissão"); // regra única do #137 (MainForm.MinhaLive.cs)
        return _homePage;
    }

    /// <summary>Chamado no construtor, depois do WireEvents.</summary>
    void InitGroup()
    {
        InitStartup(); // "Iniciar com o Windows" e --inicio (MainForm.Inicio.cs)
        _convitePendente = LaunchArgs.Select(AppLink.InviteFrom).FirstOrDefault(c => c != null); // streemu://grupo/...: pergunta depois que a conta estiver de pé (MainForm.Grupos.cs)
        string? link = LaunchArgs.Select(AppLink.StreamIdFrom).FirstOrDefault(id => id != null);
        if (link != null) { _pendingWatch = link; _pendingWatchAt = DateTime.Now; _startHidden = true; }
        if (LaunchArgs.Any(AppLink.IsDiscordLaunch)) _startHidden = true; // o pedido chega pelo IPC logo depois
        if (Instance != null)
        {
            Instance.ArgsReceived += args => Ui(() => OnArgs(args));
            Instance.ReplacedBy += exe => Ui(() =>
            {
                Log.Info($"Abriram outro Streemu ({System.IO.Path.GetDirectoryName(exe)}); esta versão fecha para ele assumir.");
                ExitApp();
            });
        }
        InitThumbnails();
        LigarSugestao30Fps();
        _sender.Reconfigured += () => Ui(() =>
        {
            if (_sender.Current is { } c)
            {
                _liveGame = GameName.Of(c.Source);
                var match = _preset.Items.Cast<Preset>().FirstOrDefault(p => p.Name == c.Preset.Name);
                if (match != null) _preset.SelectedItem = match;
            }
            UpdateLive(force: true);
        });
        _sender.FocusHeld += held => Ui(() =>
        {
            _groupStatus.Text = held
                ? "Transmissão pausada: o jogo não está na frente e os amigos veem a tela \"Transmissão pausada\". Volte para o jogo e a stream continua."
                : DefaultStatus();
            _groupStatus.ForeColor = held ? Theme.Warn : Theme.TextMuted;
        });

        if (!Automated)
        {
            AppLink.Register();
            InitTray();
        }
        if (!Automated || Environment.GetEnvironmentVariable("STREEMU_DISCORD") == "1")
        {
            if (!Automated) AppLink.RegisterDiscordLaunch(DiscordPresence.DefaultAppId);
            _discord = new DiscordPresence(DiscordPresence.DefaultAppId);
            _discord.JoinRequested += secret => Ui(() =>
            {
                _pendingWatch = secret;
                _pendingWatchAt = DateTime.Now;
                TryPendingWatch();
            });
            WatchDiscordAvatar();
        }

        if (_startHidden && _tray != null) WindowState = FormWindowState.Minimized;
        RefreshMembers();
        InitUpdater();
    }

    /// <summary>Chamado no Load: entra na sala (não espera o STUN; a sala não depende dele).</summary>
    void StartRoom()
    {
        string? url = Environment.GetEnvironmentVariable("STREEMU_ROOM_URL");
        if (url == null)
        {
            if (Automated) { _groupStatus.Text = "Sala do grupo desligada no modo de teste."; return; }
            url = RelayConfig.Load(_state)!.Url; // fora de teste nunca é nulo (Automated já saiu acima)
        }
        _roomServerUrl = url;
        if (!Automated) { _state.InstallId ??= Guid.NewGuid().ToString("N"); _state.Save(); } // o diagnóstico usa o InstallId
        // Nenhuma sala abre aqui: a sala é a da conta (MainForm.Conta.cs), aberta só depois de a conta estar de pé. Até lá a lista de amigos é uma
        // sala vazia. Abrir a sala antes fazia a pessoa aparecer um instante com outro id (PR #232).
        _room = new SalaUnida("", Nick);
        LigarSala(_room);
        IniciarConta(url); // MainForm.Conta.cs: com conta guardada abre direto a sala; sem conta mostra a tela de entrada
    }

    void LigarSala(ISala room)
    {
        room.Changed += () => Ui(OnRoomChanged);
        room.Message += (from, nick, msg) => Ui(() => OnRoomMessage(from, nick, msg));
        room.Undeliverable += (to, msg) => Ui(() => OnUndeliverable(to, msg));
        room.EncerrarLive += () => Ui(AoServidorEncerrarLive); // MainForm.MinhaLive.cs
        room.LiveEncerrada += conta => Ui(() => AoServidorLiveEncerrada(conta));
    }

    void InitTray()
    {
        var menu = new ContextMenuStrip();
        TrayVisuals.Style(menu);
        menu.Opening += (_, e) => { BuildTrayMenu(menu); e.Cancel = false; };
        _tray = new NotifyIcon { Icon = IconeBandeja(AtividadeIcone.Ocioso), Text = UiBrand.Name, Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => ShowMain();
        _tray.BalloonTipClicked += (_, _) => ShowMain();
    }

    void ShowMain()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        ShowInTaskbar = true;
        Activate();
    }

    void ExitApp()
    {
        _exiting = true;
        Close();
    }

    /// <summary>true = cancela o fechamento (vai para a bandeja).</summary>
    bool HideToTrayInsteadOfClosing(CloseReason reason)
    {
        if (_exiting || _tray == null || reason != CloseReason.UserClosing) return false;
        MostrarDicaDaBandejaUmaVez();
        Hide();
        return true;
    }

    /// <summary>
    /// Na primeira vez que a pessoa fecha a janela, diz na própria janela (antes de esconder) que o Streemu continua na bandeja. Era um balão por abertura do app,
    /// e o sino silenciado o engolia: quem silenciou nunca ficava sabendo (diagnóstico de UX, item 9). É a resposta direta a um clique, não uma notificação.
    /// </summary>
    void MostrarDicaDaBandejaUmaVez()
    {
        if (_state.DicaBandejaVista || Automated) return;
        _state.DicaBandejaVista = true;
        _state.Save(); // antes da caixa: se algo travar, não repete
        MessageBox.Show(this, $"O {UiBrand.Name} continua aberto aqui na bandeja (perto do relógio), para os amigos te verem e você receber os avisos.\n\nPara fechar de vez: botão direito no ícone da bandeja → Sair.",
            $"{UiBrand.Name} continua aberto", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void ShutdownGroup()
    {
        foreach (var sid in _links.Keys.ToList()) EndLink(sid, tellPeer: true, why: null, wait: true);
        FecharTelemetria(); // MainForm.Telemetria.cs: depois das ligações (os relatórios delas entram no último envio)
        _discord?.Dispose();
        _contaTimer.Stop();
        _contaRetry.Stop();
        _room?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Instance?.Dispose();
    }

    // ------------------------------------------------------------------ link streemu://

    void OnArgs(string[] args)
    {
        if (args.Select(AppLink.InviteFrom).FirstOrDefault(c => c != null) is { } convite) { ReceberConvite(convite); return; } // MainForm.Grupos.cs
        var id = args.Select(AppLink.StreamIdFrom).FirstOrDefault(i => i != null);
        if (id == null && args.Any(AppLink.IsDiscordLaunch)) return; // "Participar": o segredo chega pelo IPC
        if (id == null && args.Contains(StartWithWindows.Arg, StringComparer.OrdinalIgnoreCase)) return; // o Windows não abre janela por cima
        if (id == null) { ShowMain(); return; }
        _pendingWatch = id;
        _pendingWatchAt = DateTime.Now;
        TryPendingWatch();
    }

    void TryPendingWatch()
    {
        if (_pendingWatch == null) return;
        if (_room == null)
        {
            Notify($"O {UiBrand.Name} ainda não conectou ao servidor do grupo. Confira a internet e tente de novo.");
            _pendingWatch = null;
            return;
        }
        var m = _room.FindByStream(_pendingWatch);
        if (m != null)
        {
            _pendingWatch = null;
            if (m.Id == _room.MyId) { Notify("Essa é a sua transmissão."); return; }
            _ = WatchAsync(m, "link do Discord");
            return;
        }
        // logo depois de abrir, a lista do grupo ainda não chegou: espera um pouco antes de dizer que acabou
        if (_room.Connected && DateTime.Now - _pendingWatchAt > TimeSpan.FromSeconds(6) || DateTime.Now - _pendingWatchAt > TimeSpan.FromSeconds(25))
        {
            _pendingWatch = null;
            Notify(_room.Connected
                ? "Essa transmissão já acabou (ninguém do grupo está transmitindo com esse link)."
                : "Não consegui entrar na sala do grupo para abrir a transmissão. " + _room.Status);
        }
    }

    // ------------------------------------------------------------------ sala

    void OnRoomChanged()
    {
        AdotarNomeDaSala(); // MainForm.Nome.cs: troca de nome feita em outro aparelho
        RefreshMembers();
        AvisosDaSala(); // MainForm.Avisos.cs: "fulano começou uma live"
        OnRoomMembersForAvatars();
        ForgetEndedThumbnails();
        TryPendingWatch();
        EndLinksOfPeersGoneFromRoom();
    }

    /// <summary>
    /// Amigo saiu da sala: a ligação com ele acaba, mas só se o túnel também parou. Sumir da sala sozinho não quer dizer que
    /// o app fechou: a conexão com a sala cai sem aviso por ~2 s de vez em quando e volta sozinha. Em 2026-11-10 isso fechou
    /// duas vezes a live de um amigo para outro, com o túnel conectado e a live no ar (ver DESCOBERTAS). Quem sai pela
    /// bandeja já manda "parar"; app travado ou PC desligado faz o túnel ir para Lost em 8 s, e aí esta regra encerra.
    /// Roda a cada mudança da sala e a cada segundo (o túnel pode cair depois que a sala já mudou).
    /// </summary>
    void EndLinksOfPeersGoneFromRoom()
    {
        if (_room is not { Connected: true }) return;
        foreach (var (sid, link) in _links.ToList())
        {
            if (_room.IsPresent(link.PeerId)) // presença de agora, sem a folga da lista (GroupRoom.AwayGrace)
            {
                if (_linkPeerAway.Remove(sid)) Log.Info($"{link.PeerNick} voltou para a sala do grupo; a ligação seguiu o tempo todo.");
                continue;
            }
            if (DateTime.Now - link.CreatedAt <= TimeSpan.FromSeconds(5)) continue;
            if (link.Connected)
            {
                if (_linkPeerAway.Add(sid)) Log.Info($"{link.PeerNick} sumiu da sala do grupo, mas a ligação direta continua; sigo com ela.");
                continue;
            }
            EndLink(sid, tellPeer: false, why: link.IAmSender ? null : $"{link.PeerNick} fechou o {UiBrand.Name}.");
        }
    }

    void OnRoomMessage(string from, string nick, JsonObject msg)
    {
        string tipo = msg["tipo"]?.GetValue<string>() ?? "", sid = msg["sessao"]?.GetValue<string>() ?? "";
        _links.TryGetValue(sid, out var link);
        // A sala carimba o remetente (from), mas o "sessao" é escrito pelo app do amigo: um membro mal-intencionado poderia mandar "aceito"/"parar"
        // com a sessão de outra pessoa. Só quem é o outro lado da ligação pode mexer nela.
        if (link != null && link.PeerId != from)
        {
            Log.Info("Sala: mensagem de sessão de outra pessoa; ignorei.");
            link = null;
        }
        switch (tipo)
        {
            case "quero_assistir":
                _ = AcceptViewerAsync(from, nick, sid, msg["sala"]?.GetValue<string>() ?? "", msg["codigo"]?.GetValue<string>() ?? "", msg["codigo_convite"]?.GetValue<string>());
                break;
            case "aceito" when link is { IAmSender: false }:
                try
                {
                    link.Connect(msg["codigo"]?.GetValue<string>() ?? "");
                    Log.Info($"{nick} aceitou; furando o NAT até ele...");
                    _groupStatus.Text = $"Conectando com {nick}...";
                }
                catch (FormatException e) { EndLink(sid, tellPeer: true, why: $"{nick} mandou um código inválido ({e.Message}). Vocês estão em versões diferentes?"); }
                break;
            case "recusado" when link != null:
                EndLink(sid, tellPeer: false, why: msg["motivo"]?.GetValue<string>() ?? $"{nick} já parou de transmitir.");
                break;
            case "avatar":
                ApplyFriendAvatar(from, nick, msg);
                break;
            case AvatarMessage.AskType:
                OnAvatarAsked(from);
                break;
            case SalaPrivada.TipoConvite:
                OnConvitePrivada(from, nick, msg);
                break;
            case SomNaLive.TipoMensagem:
                OnSomNaLive(from, msg); // MainForm.SomNaLive.cs
                break;
            case SalaPrivada.TipoRemocao:
                OnRemocaoPrivada(from, msg);
                break;
            case SalaPrivada.TipoResposta:
                OnRespostaConvite(from, nick, msg);
                break;
            case ThumbnailMessage.AskType:
                OnThumbnailAsk(from, msg);
                break;
            case ThumbnailMessage.Type:
                OnThumbnailReceived(msg);
                break;
            case "espectadores" when link is { IAmSender: false }:
                SaveWatchers(sid, msg);
                break;
            case "reabrir_player" when link is { IAmSender: true }:
                Log.Info($"{nick} vai reabrir o player; refazendo o envio para ele.");
                _sender.ViewerLeft(sid);
                break;
            case var f when f.StartsWith("filme_"):
                OnFilmeSala(from, nick, msg); // MainForm.Filme.cs
                break;
            case var a when a.StartsWith("arquivo_"):
                OnArquivoSala(from, nick, msg); // MainForm.Arquivos.cs (spec 020)
                break;
            case "parar" when link != null:
                if (link.IAmSender) Log.Info($"{nick} fechou o player.");
                EndLink(sid, tellPeer: false, why: link.IAmSender ? null : $"{nick} parou de transmitir.");
                break;
        }
    }

    void OnUndeliverable(string to, JsonObject? msg)
    {
        string sid = msg?["sessao"]?.GetValue<string>() ?? "", tipo = msg?["tipo"]?.GetValue<string>() ?? "";
        if (!_links.TryGetValue(sid, out var link)) return;
        // mesma regra de EndLinksOfPeersGoneFromRoom: amigo fora da sala por um instante com o túnel de pé não fechou o app.
        // "reabrir_player" é a exceção: sem o transmissor refazer o envio, o player novo pode ficar sem imagem.
        if (link.Connected && tipo != "reabrir_player")
        {
            Log.Info($"A sala não entregou \"{tipo}\" para {link.PeerNick} (fora da sala agora), mas a ligação direta continua.");
            return;
        }
        EndLink(sid, tellPeer: false, why: link.IAmSender ? null : $"{link.PeerNick} fechou o {UiBrand.Name}.");
    }

    // ------------------------------------------------------------------ ligações (um túnel por espectador)

    // descoberto na abertura (MainForm.Abertura.cs). Em modo local o teste "sala-alt-inalcancavel" liga o alternativo de mentira (127.0.0.2, que não é de operadora
    // nem privado e por isso não passa pelo filtro): é a prova de que um alternativo que ninguém alcança não atrapalha a conexão pelo endereço normal.
    IPAddress? CarrierIp => _localMode ? (TestDir != null && Environment.GetEnvironmentVariable("STREEMU_TEST_ALT") == "1" ? _alt : null)
                                       : _alt != null && Upnp.IsCarrierOrPrivate(_alt) ? _alt : null;
    RelayConfig.Settings? LinkRelay => Automated ? null : RelayConfig.Load(_state);

    static string RandomId(int len)
    {
        const string abc = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return new string(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>(abc, len));
    }

    /// <summary>
    /// Quem assiste: prepara um túnel novo e pede pela sala. O resto acontece quando chega o "aceito".
    /// <paramref name="origem"/> vai para o log: em 2026-11-15 uma pessoa do grupo aceitou uma privada e abriram também as lives públicas, e o log não dizia qual botão abriu cada uma.
    /// </summary>
    async Task WatchAsync(Member m, string origem)
    {
        if (m.Live == null || _room == null) return;
        if (ContaBloqueia()) return;
        if (_links.Values.Any(l => !l.IAmSender && l.PeerId == m.Id))
        {
            Notify($"Você já está assistindo {NomeNaTela(m)}.");
            return;
        }
        string sid = RandomId(12);
        _groupStatus.Text = $"Preparando a conexão com {NomeNaTela(m)}...";
        PeerLink link;
        try { link = await PeerLink.CreateAsync(sid, m.Id, m.Nick, iAmSender: false, _localMode, Nick, CarrierIp, LinkRelay, m.Live.StreamId); }
        catch (Exception e) { Notify($"Não deu para preparar a conexão com {NomeNaTela(m)}: {e.Message}"); return; }
        AddLink(link);
        Log.Info($"Pedindo para assistir {m.Nick} ({m.Live.Game}, {m.Live.Quality}) pelo {origem}.");
        await _room.SendTo(m.Id, new JsonObject { ["tipo"] = "quero_assistir", ["sessao"] = sid, ["sala"] = m.Live.StreamId, ["codigo"] = link.MyCode, ["codigo_convite"] = CodigoConviteDe(m.Id, m.Live.StreamId) });
        RefreshMembers();
    }

    /// <summary>Quem transmite: um amigo clicou em Assistir. Túnel novo só para ele e um envio SRT só para ele.</summary>
    async Task AcceptViewerAsync(string from, string nick, string sid, string streamId, string code, string? codigoConvite)
    {
        if (_room == null || sid == "" || _links.ContainsKey(sid)) return;
        bool live = _sender.Running || _fakeLive;
        if (!live || streamId != _streamId)
        {
            await _room.SendTo(from, new JsonObject
            {
                ["tipo"] = "recusado", ["sessao"] = sid,
                ["motivo"] = live ? $"Essa transmissão do {Nick} acabou, mas tem outra no ar: clique em Assistir de novo." : $"{Nick} já parou de transmitir."
            });
            return;
        }
        if (PrivadaRecusa(from, codigoConvite))
        {
            await _room.SendTo(from, new JsonObject { ["tipo"] = "recusado", ["sessao"] = sid, ["motivo"] = $"{Nick} fez essa transmissão só para convidados." });
            return;
        }
        // Uma ligação de envio por pessoa e por transmissão: quem reabre o player ou reconecta manda um pedido novo, e o novo vale.
        // Sem isso, um app adulterado abriria centenas de ligações (socket, threads, reserva no relay e um envio SRT cada) e esgotaria o upload de quem joga.
        // Os pedidos EM ANDAMENTO também contam: o CreateAsync leva segundos (STUN + relay) e o link só entra em _links depois dele, então uma rajada de
        // pedidos passaria toda pela checagem com _links ainda vazio. (OnRoomMessage roda na thread da tela, via Ui; o await volta para ela: sem lock.)
        if (_aceitandoViewer.Contains(from))
        {
            Log.Info($"{nick} pediu para assistir de novo antes de eu terminar o pedido anterior; ignorei este.");
            return;
        }
        if (SenderLinks + _aceitandoViewer.Count >= MaxEspectadores &&
            !_links.Values.Any(l => l.IAmSender && l.PeerId == from && l.StreamId == streamId)) // quem já assiste e reabre troca a sua, não ocupa vaga nova
        {
            Log.Info($"{nick} pediu para assistir, mas já são {SenderLinks} espectadores (o máximo); recusei.");
            await _room.SendTo(from, new JsonObject { ["tipo"] = "recusado", ["sessao"] = sid, ["motivo"] = $"{Nick} já tem o máximo de {MaxEspectadores} pessoas assistindo. Tente quando alguém sair." });
            return;
        }
        _aceitandoViewer.Add(from);
        try
        {
            foreach (var (velhaSid, velha) in _links.ToList())
                if (velha.IAmSender && velha.PeerId == from && velha.StreamId == streamId)
                {
                    Log.Info($"{nick} pediu para assistir de novo; troco a ligação antiga pela nova.");
                    EndLink(velhaSid, tellPeer: false, why: null); // tellPeer false: foi o próprio amigo que pediu de novo
                }
            PeerLink link;
            PeerLink? criado = null;
            try
            {
                criado = await PeerLink.CreateAsync(sid, from, nick, iAmSender: true, _localMode, Nick, CarrierIp, LinkRelay, streamId);
                criado.Connect(code);
                link = criado;
            }
            catch (Exception e)
            {
                // código recusado (ex.: aponta para a rede local) depois de a ligação existir: fecha, senão socket, threads e reserva no relay
                // ficariam abertos, e um app adulterado repetiria o pedido à vontade
                criado?.Dispose();
                Log.Info($"{nick} pediu para assistir, mas não deu para preparar a conexão: {e.Message}");
                await _room.SendTo(from, new JsonObject { ["tipo"] = "recusado", ["sessao"] = sid, ["motivo"] = $"{Nick} não conseguiu preparar a conexão ({e.Message})." });
                return;
            }
            AddLink(link);
            if (_sender.Running) _sender.AddOutput(sid, nick, link.Relay, () => link.Tunnel.Passphrase);
            Log.Info($"{nick} pediu para assistir; conectando (agora são {SenderLinks} espectador(es) pela sala).");
            await _room.SendTo(from, new JsonObject { ["tipo"] = "aceito", ["sessao"] = sid, ["codigo"] = link.MyCode });
            UpdateLive();
        }
        finally { _aceitandoViewer.Remove(from); }
    }

    /// <summary>Quem está com um pedido de "quero_assistir" sendo preparado agora (ver AcceptViewerAsync). Só a thread da tela mexe.</summary>
    readonly HashSet<string> _aceitandoViewer = new();

    void AddLink(PeerLink link)
    {
        _links[link.SessionId] = link;
        _linkLastGood[link.SessionId] = DateTime.Now;
        link.Tunnel.StateChanged += (s, _) => Ui(() => OnLinkState(link, s));
        link.Tunnel.Control += msg => Ui(() => AprenderSeenAs(link, msg)); // MainForm.Abertura.cs
        // conexão só de ida (e a volta dela): era o único aviso do túnel que ia para a tela do código; agora chega como aviso da sala
        link.Tunnel.Hint += msg => Ui(() => { if (_links.ContainsKey(link.SessionId)) Notify($"Ligação com {link.PeerNick}: {msg}"); });
        link.Tunnel.OneWayChanged += ida => Ui(() => { if (_links.ContainsKey(link.SessionId)) TelemetriaAvisoLigacao(link, ida); }); // MainForm.Sincronia.cs
        TelemetriaLigacao(link, comecou: true);
    }

    /// <summary>
    /// Teto de pessoas assistindo a uma transmissão ao mesmo tempo. Cada espectador é uma cópia do vídeo saindo pelo upload de quem joga,
    /// então sem teto um app adulterado (ou só muita gente) esgotaria a internet de quem transmite. 16 é bem mais que o grupo costuma ter.
    /// </summary>
    const int MaxEspectadores = 16;

    int SenderLinks => _links.Values.Count(l => l.IAmSender);

    void OnLinkState(PeerLink link, TunnelState s)
    {
        if (!_links.ContainsKey(link.SessionId)) return;
        TelemetriaEstadoLigacao(link, s);
        if (s != TunnelState.Connected) return;
        _linkLastGood[link.SessionId] = DateTime.Now;
        bool first = _linkEverConnected.Add(link.SessionId);
        if (first) Log.Info($"Ligação com {link.PeerNick} conectada em {link.Tunnel.ConnectMs} ms ({(link.IAmSender ? "você transmite" : "você assiste")}).");
        if (first && link.IAmSender) { TocarSom(Sons.Som.EspectadorEntrou); AnunciarEntradaNaLive(link.PeerId); } // MainForm.SomNaLive.cs
        if (first && !link.IAmSender) { WarnIfStreamerSendsNoList(link); ContarQueComeceiAAssistir(link); } // MainForm.Atividades.cs: só live pública
        if (!link.IAmSender && !_linkViewers.ContainsKey(link.SessionId) && Environment.GetEnvironmentVariable("STREEMU_TEST_NOSTREAM") != "1")
            StartLinkViewer(link);
        RefreshMembers();
    }

    // Uma falha observada não deve atrasar a abertura de todas as lives.
    long _vulkanRetryAt, _direct3DRetryAt;
    readonly object _playerPreparationLock = new();
    Task<string?>? _preparandoPlayerDirect3D;

    /// <summary>A janela de streams (spec 006): organiza o painel "Ao vivo agora" e os vídeos de todas as streams que o usuário assiste.</summary>
    PlayerStage? _playerStage;

    void StartLinkViewer(PeerLink link)
    {
        var v = new Viewer
        {
            UseHardware = _hwDecode.Checked,
            // O Vulkan já deixou a imagem borrada na RTX série 50. Tentamos Direct3D 11 nesse caso.
            AvoidVulkan = SystemInfo.HasRtx50 || Environment.TickCount64 < Interlocked.Read(ref _vulkanRetryAt),
            PrepareDirect3D = _hwDecode.Checked && !Automated ? PrepararPlayerDirect3D : null,
        };
        if (TestDir != null) v.ExtraArgs = new[] { "-volume", "0" };
        v.Audio.Restore(_state.PlayerVolumePercent, _state.PlayerMuted);
        if (!Automated)
            v.Audio.Changed += () => { _state.PlayerVolumePercent = v.Audio.Percent; _state.PlayerMuted = v.Audio.Muted; _state.Save(); };
        string sid = link.SessionId;
        v.Exited += () => Ui(() => EndLink(sid, tellPeer: true, why: null));
        v.VulkanFailed += () => Interlocked.Exchange(ref _vulkanRetryAt, Environment.TickCount64 + 20 * 60_000);
        v.Direct3DRejected += () => Interlocked.Exchange(ref _direct3DRetryAt, Environment.TickCount64 + 2 * 60_000);
        v.Restarting += () =>
        {
            _ = _room?.SendTo(link.PeerId, new JsonObject { ["tipo"] = "reabrir_player", ["sessao"] = sid });
        };
        _linkViewers[sid] = v;
        var stage = _playerStage ??= new PlayerStage(this);
        stage.Add(v, () => StreamCaption(link.PeerId), () => AvatarOfMember(link.PeerId)); // antes do Start: o ffplay novo já abre no lugar que a janela de streams reservou
        v.Start(link.Relay.Port, link.Tunnel.Passphrase, LatenciaDoEspectador(link), $"{UiBrand.Name} — {link.PeerNick}");
        PlayerOverlay.ShowFor(v, () => _room?.Members.FirstOrDefault(m => m.Id == link.PeerId)?.Live?.Quality ?? "", () => WatchersRoster(link), new AppStateMiniStore(_state, !Automated), text => Ui(() => Notify(text)), stage, DenunciaDoPlayer(link)); // MainForm.Denuncia.cs
        _groupStatus.Text = DefaultStatus();
        AtualizarIconeAtividade(); // passou a assistir (o fim sai pelo EndLink, que chama o UpdateLive)
        AvisarSomDoAmigoNaLive(); // MainForm.AvisoSom.cs
    }

    Task<string?> PrepararPlayerDirect3D()
    {
        if (_closing.IsCancellationRequested || Environment.TickCount64 < Interlocked.Read(ref _direct3DRetryAt))
            return Task.FromResult<string?>(null);
        lock (_playerPreparationLock)
        {
            if (_preparandoPlayerDirect3D is { IsCompleted: false } pending) return pending;
            return _preparandoPlayerDirect3D = Task.Run<string?>(async () =>
            {
                try
                {
                    if (Filme.Mpv.Find() is { } exe) return exe;
                    using var prazo = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
                    prazo.CancelAfter(TimeSpan.FromMinutes(2));
                    Log.Info("Player: preparando o Direct3D 11.");
                    return await Filme.MpvInstalador.InstalarAsync(RelayConfig.Load(_state), progresso: null, prazo.Token);
                }
                catch (OperationCanceledException) when (_closing.IsCancellationRequested) { return null; }
                catch (Exception e)
                {
                    Log.Info($"Player: não consegui preparar o Direct3D 11 ({e.Message}).");
                    return null;
                }
            });
        }
    }

    /// <summary>Legenda da peça na janela de streams: "Amigo · Jogo · ao vivo há 12 min".</summary>
    string? StreamCaption(string peerId)
    {
        if (_room?.Find(peerId) is not { } member) return null;
        if (member.Live is not { } live) return NomeNaTela(member);
        var since = TimeSpan.FromMilliseconds(Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - live.SinceUnixMs));
        string time = since.TotalMinutes < 1 ? "agora" : since.TotalHours < 1 ? $"{(int)since.TotalMinutes} min" : $"{(int)since.TotalHours} h {since.Minutes:00} min";
        return $"{NomeNaTela(member)} · {live.Game} · ao vivo há {time}".Replace("há agora", "agora");
    }

    void EndLink(string sid, bool tellPeer, string? why, bool wait = false)
    {
        if (!_links.TryGetValue(sid, out var link)) return;
        // O relatório do fim da ligação sai ANTES de desmontar: gerado depois (como era), ele não achava mais a ligação nem
        // o player, e o "fim-assistir" de todo mundo saía vazio (telemetria de 10/11, live de um amigo no Hookshot).
        TelemetriaLigacao(link, comecou: false);
        _links.Remove(sid);
        _linkLastGood.Remove(sid);
        _linkPeerAway.Remove(sid);
        // "saiu" só para quem chegou a entrar (tocou o "entrou"); quem desistiu antes de conectar não faz som
        if (_linkEverConnected.Remove(sid) && link.IAmSender && !_closingLive) { TocarSom(Sons.Som.EspectadorSaiu); AnunciarSaidaDaLive(link.PeerId); }
        _watchersBySession.Remove(sid);
        _escaladaLatencia.Remove(sid);
        if (_linkViewers.Remove(sid, out var v)) v.Stop();
        if (link.IAmSender) _sender.RemoveOutput(sid);
        if (tellPeer) _ = _room?.SendTo(link.PeerId, new JsonObject { ["tipo"] = "parar", ["sessao"] = sid });
        if (why != null) Notify(why); else _groupStatus.Text = DefaultStatus();
        Log.Info($"Ligação com {link.PeerNick} encerrada.");
        // liberar o relay espera a resposta da Cloudflare: fora da thread da tela
        var t = Task.Run(link.Dispose);
        if (wait) t.Wait(1500);
        UpdateLive();
        RefreshMembers();
    }

    // ------------------------------------------------------------------ ao vivo (sala + Discord)

    /// <summary>Chamado quando a transmissão começa.</summary>
    void OnLiveStarted(string game)
    {
        _streamId = RandomId(10);
        PrivadaAoComecar(); // antes do primeiro UpdateLive: a privada nunca chega a ser anunciada
        _liveSinceMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _liveGame = game;
        _sender.PauseCaption = $"{Nick} saiu do jogo por um instante. A stream volta sozinha.";
        _stopLive.Visible = true;
        Log.Info($"Ao vivo: {game}. Os amigos veem na lista do grupo" + (_discord != null ? " e no seu perfil do Discord." : "."));
        TelemetriaLive(comecou: true);
        UpdateLive(force: true);
        TocarSom(Sons.Som.LiveAbriu);
        IniciarSomNaLive(); // MainForm.SomNaLive.cs
        if (_sender.HdrWarning is string hdr) Notify(hdr);
    }

    /// <summary>Chamado quando a transmissão para (por botão, bandeja ou erro).</summary>
    void OnLiveStopped()
    {
        bool wasLive = _streamId != null; // Stopped também chega quando a transmissão nem chegou a abrir: aí não toca nada
        TelemetriaLive(comecou: false); // antes de fechar as ligações: o relatório ainda vê a live inteira
        _closingLive = true;
        try { foreach (var (sid, link) in _links.ToList()) if (link.IAmSender) EndLink(sid, tellPeer: true, why: null); }
        finally { _closingLive = false; }
        _streamId = null;
        PrivadaAoParar(); // os convites e códigos morrem com a stream
        EncerrarSomNaLive();
        _fakeLive = false;
        _stopLive.Visible = false;
        UpdateLive(force: true);
        if (wasLive) TocarSom(Sons.Som.LiveFechou);
        if (_avisoAoParar is { } aviso) { _avisoAoParar = null; Notify(aviso); } // por último: o EndLink acima troca o texto da linha de status
    }

    string LiveQuality
    {
        get
        {
            var c = _sender.Current;
            if (c == null) return "teste";
            return $"{_sender.OutSize.H}p {c.Preset.Fps} FPS";
        }
    }

    int WatchingCount => SenderLinks;

    void UpdateLive(bool force = false)
    {
        TellViewersWhoWatches(); // a lista pode mudar sem a contagem mudar (um sai, outro entra)
        bool live = _streamId != null && (_sender.Running || _fakeLive);
        var info = live ? new LiveInfo(_streamId!, _liveGame, LiveQuality, _liveSinceMs, WatchingCount) : null;
        var publica = InfoPublica(info); // privada: a sala e o Discord nunca recebem a live
        if (!force && Equals(info, _lastLive)) return;
        bool wentLive = info != null && _lastLive == null, ended = info == null && _lastLive != null;
        if (wentLive && !_preparationBusy) { _preparation?.Dispose(); _preparation = null; }
        _lastLive = info;
        OnLiveChanged(wentLive, ended); // MainForm.MinhaLive.cs
        RefreshSide();
        PlacePreparation();
        _room?.SetLive(publica);
        AtualizarIconeAtividade(); // MainForm.Icone.cs: minha live entrou ou saiu do ar
        if (_discord == null) return;
        if (info == null || publica == null) { _discord.Set(null); return; }
        string watching = info.Watching switch { 0 => "esperando amigos", 1 => "1 assistindo", var n => $"{n} assistindo" };
        // o Discord só aceita link https no botão (a página do Worker abre o streemu://)
        string? url = _roomServerUrl != null && _roomServerUrl.StartsWith("https://") ? $"{_roomServerUrl.TrimEnd('/')}/assistir/{_streamId}" : null;
        _discord.Set(new PresenceInfo($"Transmitindo {_liveGame}", $"{info.Quality} · {watching}", _liveSinceMs, url,
            JoinSecret: _state.DiscordJoinNativo ? _streamId : null, PartySize: info.Watching + 1));
    }

    /// <summary>Opção experimental no card do Grupo: "Participar" do Discord sem abrir o navegador.</summary>
    CheckBox DiscordJoinOption()
    {
        var box = new Ui.ToggleSwitch
        {
            AutoSize = true, Checked = _state.DiscordJoinNativo,
            Text = "Botão Participar sem abrir o navegador" // o PerfilLayout põe a nota "experimental" embaixo
        };
        box.CheckedChanged += (_, _) =>
        {
            _state.DiscordJoinNativo = box.Checked;
            if (!Automated) _state.Save();
            Log.Info(box.Checked ? "Discord: usando o Participar nativo (sem navegador)." : "Discord: usando o botão Assistir (abre pelo navegador).");
            UpdateLive(force: true);
        };
        return box;
    }

    // ------------------------------------------------------------------ janela Transmitir e bandeja

    /// <param name="convidarId">Amigo a convidar: a janela abre já em "Só convidados" com ele marcado (spec 013).</param>
    void RememberGoLive(CaptureSource src, Preset preset, int audio)
    {
        _state.GoLiveHeight = preset.IsSource ? 0 : preset.Height;
        _state.GoLiveFps = preset.Fps;
        _state.GoLiveAudio = audio;
        if (src.IsWindow) _state.GoLiveProcess = src.ProcessName;
        if (!Automated) _state.Save();
    }

    /// <summary>Seleciona fonte, preset e áudio na tela e transmite (a tela de sempre continua sendo a fonte da verdade).</summary>
    async Task StartWithAsync(CaptureSource src, Preset preset, int audio)
    {
        if (_sender.Running) return;
        var items = _source.Items.Cast<CaptureSource>().ToList();
        int idx = items.FindIndex(s => s.Handle == src.Handle && s.IsWindow == src.IsWindow);
        if (idx < 0) { _source.Items.Add(src); idx = _source.Items.Count - 1; }
        _source.SelectedIndex = idx;
        _preset.SelectedItem = preset;
        _audio.SelectedIndex = audio;
        await ToggleTransmitAsync();
    }

    void ChangeQuality(int height, int fps)
    {
        _state.GoLiveHeight = height; _state.GoLiveFps = fps;
        if (!Automated) _state.Save();
        if (_sender.Running && _sender.Current is { } cur) _sender.Reconfigure(cur with { Preset = Preset.Pick(height, fps) });
    }

    void BuildTrayMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        AddUpdateMenuItem(menu);
        if (EntradaObrigatoria) menu.Items.Add(new ToolStripMenuItem("Entre na sua conta para usar.") { Enabled = false }); // sem transmitir enquanto não entrar
        if (_sender.Running && _sender.Current is { } cur)
        {
            menu.Items.Add(new ToolStripMenuItem($"● Ao vivo: {_liveGame} · {WatchingCount} assistindo") { Enabled = false });
            var quality = new ToolStripMenuItem("Qualidade");
            int curH = cur.Preset.IsSource ? 0 : cur.Preset.Height;
            quality.DropDownItems.Add(new ToolStripMenuItem("TAXA DE QUADROS") { Enabled = false });
            foreach (int f in Preset.PickerFps)
            {
                int ff = f;
                quality.DropDownItems.Add(new ToolStripMenuItem(Preset.IsBeta(f) ? $"{f} FPS  · beta" : $"{f} FPS", null, (_, _) => ChangeQuality(curH, ff)) { Checked = _sender.RequestedFps == f });
            }
            quality.DropDownItems.Add(new ToolStripSeparator());
            quality.DropDownItems.Add(new ToolStripMenuItem("RESOLUÇÃO") { Enabled = false });
            foreach (int h in Preset.PickerHeights)
            {
                int hh = h;
                quality.DropDownItems.Add(new ToolStripMenuItem(h == 0 ? "Fonte" : $"{h}p", null, (_, _) => ChangeQuality(hh, _sender.RequestedFps)) { Checked = curH == h });
            }
            if (quality.DropDown is ToolStripDropDownMenu qd) { qd.Renderer = menu.Renderer; qd.BackColor = menu.BackColor; qd.ForeColor = menu.ForeColor; qd.ShowImageMargin = false; qd.ShowCheckMargin = true; }
            menu.Items.Add(quality);
            menu.Items.Add("Trocar janela...", null, (_, _) => OpenGoLive(changing: true));
            menu.Items.Add("Parar transmissão", null, (_, _) => EncerrarLive("menu da bandeja"));
        }
        else if (!EntradaObrigatoria)
        {
            var game = Sources.List().FirstOrDefault(s => s.IsFullScreen);
            var last = Preset.Pick(_state.GoLiveHeight, _state.GoLiveFps);
            if (game != null)
            {
                var g = game;
                menu.Items.Add(new ToolStripMenuItem($"Transmitir {GameName.Of(g)} ({last.Short})", null,
                    (_, _) => { RememberGoLive(g, last, _state.GoLiveAudio); _ = StartWithAsync(g, last, _state.GoLiveAudio); }) { Font = new Font(menu.Font, FontStyle.Bold) });
            }
            menu.Items.Add("Transmitir...", null, (_, _) => OpenGoLive(changing: false));
        }
        var liveFriends = _room?.Friends.Where(f => f.Live != null).ToList() ?? new();
        if (liveFriends.Count > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
            foreach (var f in liveFriends)
            {
                bool watching = _links.Values.Any(l => !l.IAmSender && l.PeerId == f.Id);
                var m = f;
                menu.Items.Add(new ToolStripMenuItem($"Assistir {NomeNaTela(f)} — {f.Live!.Game}", null, (_, _) => _ = WatchAsync(m, "menu da bandeja")) { Checked = watching, Enabled = !watching });
            }
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add($"Marcar problema ({Telemetria.AtalhoProblema.Nome})", null, (_, _) => MarcarProblema(pedirNota: false)); // MainForm.Telemetria.cs
        AddStartupMenuItem(menu); // MainForm.Inicio.cs
        menu.Items.Add($"Abrir o {UiBrand.Name}", null, (_, _) => ShowMain());
        menu.Items.Add("Sair", null, (_, _) => ExitApp());
    }

    // ------------------------------------------------------------------ lista do grupo

    void RefreshMembers()
    {
        if (_room == null) return;
        var friends = _room.Friends.OrderByDescending(f => f.Live != null).ThenBy(f => NomeNaTela(f)).ToList();
        UpdateLiveSummary(friends);
        SetLiveCards();
        RefreshSide();
        _preparation?.RefreshFriends(ComNomesDaTela(friends)); // MainForm.Apelidos.cs
        if (_room.Connected && !_groupStatus.Text.StartsWith("Conectando") && !_groupStatus.Text.StartsWith("Preparando")
            && !_groupStatus.Text.StartsWith("Transmissão pausada"))
            _groupStatus.Text = DefaultStatus();
    }

    /// <summary>
    /// O título da home ("Ao vivo agora · 2") e o que depende de haver alguém ao vivo: o convite quando ninguém está, o "ONLINE AGORA" quando há quem
    /// está só online e o "Assistir todos juntos" (2 ou mais ao vivo e espaço na janela de streams, até 4).
    /// </summary>
    void UpdateLiveSummary(IReadOnlyList<Member> friends)
    {
        int live = friends.Count(f => f.Live != null);
        int notWatched = friends.Count(f => f.Live != null && !_links.Values.Any(l => !l.IAmSender && l.PeerId == f.Id));
        _liveSummaryText.Text = live > 0 ? $"Ao vivo agora · {live}" : "Ao vivo agora";
        _liveSummaryText.Visible = live > 0; // sem ninguém ao vivo a tela fica sem o título (pedido de 14/11)
        _emptyLive.Visible = live == 0;
        _watchAllLive.Visible = notWatched >= 2 && (_playerStage?.Count ?? 0) + 2 <= PlayerStage.MaxStreams;
    }

    /// <summary>
    /// "Assistindo X" só enquanto existe um player aberto de verdade (antes ficava preso depois que o amigo parava).
    /// </summary>
    string DefaultStatus()
    {
        if (SomDoAmigoVazaAgora) return AvisoSomDoAmigo; // fica na linha de status e na barra lateral enquanto durar
        var watching = _links.Values.Where(l => !l.IAmSender && _linkViewers.TryGetValue(l.SessionId, out var v) && v.Running).Select(l => l.PeerNick).ToList();
        if (watching.Count > 0) return $"Assistindo {string.Join(" e ", watching)}.";
        if (_sender.Running) return $"Transmitindo {_liveGame}.";
        return $"Na sala do grupo como {Nick}." + (_discord?.Connected == true ? " Discord conectado." : _discord != null ? " Discord não encontrado (o card do perfil aparece quando ele abrir)." : "");
    }

    void Notify(string msg)
    {
        Log.Info(msg);
        _groupStatus.Text = msg;
        if (!Visible) MostrarBalao(5000, UiBrand.Name, msg, ToolTipIcon.Info);
    }

    // ------------------------------------------------------------------ a cada segundo

    void GroupTick()
    {
        KeepLiveCards(); // MainForm.Janela.cs
        AtualizarIconeAtividade(); // rede de segurança de 1 s: Cinema, player que fecha sozinho e falhas não têm um gancho só
        var now = DateTime.Now;
        foreach (var (sid, link) in _links.ToList())
        {
            if (link.Connected) { _linkLastGood[sid] = now; continue; }
            bool ever = _linkEverConnected.Contains(sid);
            // sem conexão: 45 s para conectar a primeira vez (relay incluso), 30 s para voltar depois de cair
            if (now - _linkLastGood.GetValueOrDefault(sid, now) > TimeSpan.FromSeconds(ever ? 30 : 45))
                EndLink(sid, tellPeer: true, why: link.IAmSender ? null
                    : ever ? $"A conexão com {link.PeerNick} caiu." : $"Não consegui conectar com {link.PeerNick} em 45 s. Copie o relatório e mande no grupo.");
        }
        EndLinksOfPeersGoneFromRoom();
        EnviarSonsPendentes(); // MainForm.SomNaLive.cs
        if (_pendingWatch != null) TryPendingWatch();
        UpdateLive();
        if (++_groupTick % 20 == 0) RefreshMembers();
    }

    JsonObject GroupReport() => new()
    {
        ["conectada"] = _room?.Connected ?? false,
        ["sala_com_conta"] = _salaDeConta,
        ["tem_conta"] = _conta?.TemConta ?? false,
        ["google_vinculado"] = _conta?.GoogleVinculado ?? false,
        ["grupo_da_conta"] = _contaGrupoId,
        ["grupos_na_sala"] = _salaUnida?.Quantidade ?? 0,
        ["roteiro"] = _roteiroResultado?.DeepClone(),
        ["situacao"] = _room?.Status,
        ["amigos_online"] = _room?.Friends.Count() ?? 0,
        ["atividades"] = new JsonArray(FrasesDeAtividade.Montar(_room?.Atividades ?? [], (_, apelido) => apelido, DateTimeOffset.Now).Select(l => (JsonNode)JsonValue.Create(l.Texto)!).ToArray()), // as frases do Início, com nomes reais (etapa 9)
        ["ausentes"] = new JsonArray((_room?.Ausentes ?? []).Select(a => (JsonNode)JsonValue.Create(a.Nick)!).ToArray()), // membros com o app fechado (só sala com conta; etapa 7)
        ["discord"] = _discord == null ? "desligado" : _discord.Connected ? "conectado" : "não encontrado",
        ["ao_vivo"] = _streamId != null,
        ["avatares_recebidos"] = new JsonArray((_friendAvatars?.FriendsWithAvatar() ?? Array.Empty<string>()).Select(id => (JsonNode)JsonValue.Create(_room?.Find(id)?.Nick ?? "?")!).ToArray()),
        ["ligacoes"] = new JsonArray(_links.Values.Select(l => (JsonNode)new JsonObject
        {
            ["amigo"] = l.PeerNick,
            ["ligacao"] = l.SessionId, // o id da ligação (não identifica a pessoa): o SemQuemEstavaComQuem acha a ligação por ele, não pelo apelido
            ["papel"] = l.IAmSender ? "transmito" : "assisto",
            ["estado"] = l.Tunnel.State.ToString(),
            ["caminho"] = CaminhoDaLigacao(l), // MainForm.Sincronia.cs: o mesmo valor do evento ligacao_estado
            ["tempo_para_conectar_ms"] = l.Tunnel.ConnectMs,
            ["rtt_ms"] = Math.Round(l.Tunnel.RttMs, 1),
            ["perda_recebendo_pct"] = Math.Round(l.Tunnel.RxViewer.TotalLossPct, 2),
            ["player_aberto"] = _linkViewers.TryGetValue(l.SessionId, out var v) && v.Running,
            // o player de cada live que eu assisto fica aqui, dentro da ligação (o bloco "player" de cima do relatório acabou com o modo por código)
            ["player"] = v == null ? null : new JsonObject
            {
                ["aberto_desde"] = v.StartedAt?.ToString("HH:mm:ss"),
                ["decodificando_na_gpu"] = v.UsingHardware,
                ["decodificador"] = v.Decoder,
                ["caiu_para_cpu"] = v.FellBackToCpu,
                ["erros"] = v.ErrorCounts(),
                ["sincronia"] = v.PlaybackSnapshot(),
            },
            ["bytes_recebidos"] = l.Tunnel.RxBytes,
            ["faixa"] = l.IAmSender ? null : new JsonArray(WatchersRoster(l, paraRelatorio: true).Select(e => (JsonNode)JsonValue.Create(e.Nick)!).ToArray()),
            ["faixa_com_avatar"] = l.IAmSender ? null : new JsonArray(WatchersRoster(l, paraRelatorio: true).Where(e => e.Avatar != null).Select(e => (JsonNode)JsonValue.Create(e.Nick)!).ToArray()),
            ["contadores"] = l.Tunnel.Counters
        }).ToArray())
    };

    // ------------------------------------------------------------------ teste automático da sala (STREEMU_TEST_ROOM=1)

    /// <summary>
    /// A fica ao vivo (com STREEMU_TEST_NOSTREAM=1 só finge, sem capturar); os outros papéis esperam A aparecer ao vivo
    /// na sala e clicam em Assistir. Precisa de STREEMU_ROOM_URL (ex.: wrangler dev na pasta relay/).
    /// </summary>
    async Task RunRoomTestAsync()
    {
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("STREEMU_TEST_SECONDS"), out var s) ? s : 25;
        bool noStream = Environment.GetEnvironmentVariable("STREEMU_TEST_NOSTREAM") == "1";
        DefinirNomeDeTeste("teste-" + TestRole);
        if (TestConta && TesteSemLogin)
        {
            // conta obrigatória e sem conta: a entrada tem que ocupar o início e travar transmitir, assistir e conectar
            var fim = DateTime.Now.AddSeconds(60);
            while (!EntradaObrigatoria && DateTime.Now < fim) await Task.Delay(200);
            bool bloqueou = ContaBloqueia();
            File.WriteAllText(Path.Combine(TestDir!, TestRole + "-obrigatoria.json"), new JsonObject
            {
                ["entrada_obrigatoria"] = EntradaObrigatoria, ["entrada_visivel"] = _entrada?.Visible ?? false, ["inicio_visivel"] = _homePage?.Visible ?? false, ["bloqueou"] = bloqueou,
                // desde a tela nova (PR #220) a entrada cobre a janela inteira por cima do início, que continua visível por baixo
                ["entrada_cobre"] = _entrada is { Visible: true } e && e.Bounds.Contains(ClientRectangle) && Controls.GetChildIndex(e) == 0,
            }.ToJsonString());
            ExitApp();
            return;
        }
        // plano 019, etapa 8 (MainForm.Grupos.cs): o app de teste entra SEM a senha do grupo, então começa sem grupo nenhum; o roteiro cria e entra em grupos
        if (TesteGrupoRoteiro is string roteiroDeGrupos)
        {
            await EsperarAsync(() => _conta is { TemConta: true }, 150);
            await TesteGrupoAsync(roteiroDeGrupos);
            ExitApp();
            return;
        }
        var until = DateTime.Now.AddSeconds(TestConta ? 150 : 15);
        // com STREEMU_TEST_CONTA o teste só começa quando o login (feito por arquivos) terminou e a sala com conta está de pé
        // Conta de teste sem grupo nunca abre a sala: o preparo do teste (scripts/testes/conta.ps1) falhou. Esperar o limite de 150 s só esconde isso, então falha já.
        // As ações de conta e o roteiro de grupos (que criam o grupo no meio do teste) ficam de fora.
        bool semGrupoEhErro() => TestConta && _contaSemGrupoNoTeste && !_salaDeConta && TesteAcaoConta == null;
        while ((_room is not { Connected: true } || TestConta && !_salaDeConta) && DateTime.Now < until && !semGrupoEhErro()) await Task.Delay(200);
        if (semGrupoEhErro())
        {
            Log.Info("conta de teste sem grupo: confira o preparo do teste (a conta precisa estar em um grupo antes de o app abrir).");
            Environment.ExitCode = 3;
            ExitApp();
            return;
        }
        if (TestConta && TesteSair)
        {
            await SairParaTesteAsync();
            var fim = DateTime.Now.AddSeconds(30);
            while ((_salaDeConta || _room is not { Connected: true }) && DateTime.Now < fim) await Task.Delay(200);
            File.WriteAllText(Path.Combine(TestDir!, TestRole + "-relatorio.json"), BuildReport());
            ExitApp();
            return;
        }
        if (TesteAcaoConta is string acaoDeConta) { await TesteContaAcaoAsync(acaoDeConta); ExitApp(); return; } // MainForm.Conta.cs (plano 019, etapa 6)
        if (TesteFilme != null) { await TesteFilmeAsync(TesteFilme); ExitApp(); return; } // MainForm.Filme.cs
        if (TesteArquivoAtivo) { await TesteArquivoAsync(); ExitApp(); return; } // MainForm.ArquivosTeste.cs (spec 020)
        if (TestIsStreamer())
        {
            if (noStream) { _fakeLive = true; OnLiveStarted(TestRole == "A" ? "Jogo de teste" : "Jogo de teste " + TestRole); if (TestePrivadaAnfitriao) await TestePrivadaConvidarAsync(); }
            else
            {
                if (int.TryParse(Environment.GetEnvironmentVariable("STREEMU_TEST_PRESET"), out var p)) _preset.SelectedIndex = p;
                if (int.TryParse(Environment.GetEnvironmentVariable("STREEMU_TEST_AUDIO"), out var a)) _audio.SelectedIndex = a;
                if (Environment.GetEnvironmentVariable("STREEMU_TEST_SOURCE_MATCH") is string match)
                {
                    RefreshSources();
                    int i = _source.Items.Cast<object>().ToList().FindIndex(o => o.ToString()!.Contains(match, StringComparison.OrdinalIgnoreCase));
                    if (i >= 0) _source.SelectedIndex = i;
                }
                await ToggleTransmitAsync();
            }
            // seconds é o TETO: com STREEMU_TEST_PRONTO o cenário acaba assim que o resultado que o teste confere está pronto (MainForm.TesteAte.cs)
            if (TesteCondicaoPronta() is { } pronto) await TesteAteProntoAsync(seconds, pronto);
            else await Task.Delay(seconds * 1000);
            await TesteEsperarTelemetriaAsync(); // MainForm.Telemetria.cs
            File.WriteAllText(Path.Combine(TestDir!, TestRole + "-relatorio.json"), BuildReport());
            // fica no ar até os espectadores gravarem os deles (com A fechado, as ligações deles já teriam caído)
            until = DateTime.Now.AddSeconds(15);
            while (DateTime.Now < until && Directory.GetFiles(TestDir!, "*-relatorio.json").Length < TestExpectedReports()) await Task.Delay(200);
            ExitApp();
            return;
        }
        else
        {
            Member? live = null;
            if (TestePrivadaAtiva) { await TestePrivadaVisitanteAsync(); await TesteEsperarTelemetriaAsync(); File.WriteAllText(Path.Combine(TestDir!, TestRole + "-relatorio.json"), BuildReport()); ExitApp(); return; }
            await TestWaitAsync("STREEMU_TEST_JOIN_AFTER");
            until = DateTime.Now.AddSeconds(25);
            while ((live = _room?.Friends.FirstOrDefault(f => f.Live != null)) == null && DateTime.Now < until) await Task.Delay(200);
            // papel D: fica na sala SEM assistir (a faixa dos outros não pode mostrá-lo)
            if (live != null && TestWatchAll() is int all) await TestWatchEveryoneAsync(all);
            else if (live != null && TestRole != "D") await WatchAsync(live, "teste"); else Log.Info("teste: não assisto (ninguém ao vivo, ou papel D).");
            if (await TestWaitAsync("STREEMU_TEST_LEAVE_AFTER")) TestLeaveStream();
            // grava logo depois do relatório de A, enquanto A ainda está no ar
            until = DateTime.Now.AddSeconds(seconds + 20);
            while (DateTime.Now < until && !TestStreamersReported()) await Task.Delay(200);
        }
        await TesteEsperarTelemetriaAsync();
        File.WriteAllText(Path.Combine(TestDir!, TestRole + "-relatorio.json"), BuildReport());
        ExitApp();
    }
}
