using System.Text.Json.Nodes;
using Streemu.Filme;
using Streemu.Net;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// Filme pela transmissão (spec 011): o anfitrião manda o arquivo dele como vídeo ao vivo e quem não tem o arquivo assiste sem baixar. Fica fora do
/// MainForm.Filme.cs para dar menos conflito; lá entram só os ganchos <see cref="ConectarStreamFilme"/>, o anúncio com "stream" e o fim da ligação.
/// Mensagens pela ligação do filme (type): filme_stream_quero (quem assiste pede), filme_stream_info (resposta), filme_stream_epoca (tocar, pausar e pular),
/// filme_stream_cmd (pedido de quem assiste: tocar, pausar, ir) e filme_stream_aviso. Quem manda no tempo é a Sessao do anfitrião; o EnvioStream só a segue.
/// </summary>
public sealed partial class MainForm
{
    /// <summary>Upload que o "Automático" supõe até o T6 medir o de verdade (Mbps).</summary>
    const double UploadSupostoMbps = 30;

    EnvioStream? _filmeStream;
    readonly HashSet<string> _streamSids = new();
    System.Windows.Forms.Timer? _streamSegue;
    StreamTela? _streamTela;
    /// <summary>A ligação do player aberto aqui (quem assiste) e o nome do anfitrião dela.</summary>
    string? _streamSid, _streamAnfitriao;
    TaskCompletionSource<JsonObject>? _streamInfo;
    bool _streamLigando;
    int _streamGeracao;
    bool? _streamTodosAntes;
    /// <summary>Quem assistiu pela transmissão neste filme, id da conta → nome (para os "amigos" do filme_fim do anfitrião; quem assiste assim não entra na Sessao).</summary>
    readonly Dictionary<string, string> _streamVistos = new();
    long _streamBytesAntes;

    /// <summary>
    /// O filme_fim do anfitrião (MainForm.Filme.cs) ganha quem assistiu pela transmissão e como o envio andou. Os "amigos" da Sessao só têm quem tem o arquivo:
    /// em 11/11 o filme_fim saiu com amigos vazio e não dava para saber quem estava assistindo.
    /// </summary>
    void ComplementarFimFilme(JsonObject campos)
    {
        if (_streamVistos.Count == 0 && _filmeStream == null) return;
        string pelaTransmissao = string.Join(",", _streamVistos.Values.Select(n => $"{n}=transmissão"));
        string antes = campos["amigos"]?.GetValue<string>() ?? "";
        if (pelaTransmissao.Length > 0) campos["amigos"] = antes.Length == 0 ? pelaTransmissao : antes + "," + pelaTransmissao;
        if (_filmeStream is { } e)
        {
            campos["stream_perfil"] = e.Perfil.ToString();
            campos["stream_epocas"] = e.Epoca;
            campos["stream_mb"] = Math.Round(e.BytesEnviados / 1e6, 1);
            campos["stream_velocidade_min"] = double.IsNaN(e.VelocidadeMin) ? null : Math.Round(e.VelocidadeMin, 2);
        }
        _streamVistos.Clear();
    }

    /// <summary>Os campos do filme fluxo no filme.csv (MainForm.Filme.cs): época, cache do mpv de quem assiste, velocidade e Mbps do envio do anfitrião.</summary>
    (string Epoca, string CacheS, string ParadoCache, string Velocidade, string Espectadores, string Mbps) SerieStream()
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        if (_streamTela is { } t)
            return (t.EpocaAtual.ToString(ci), double.IsNaN(t.CacheS) ? "" : t.CacheS.ToString("0.##", ci), t.ParadoCache ? "1" : "0", "", "", "");
        if (_filmeStream is not { } e) { _streamBytesAntes = 0; return ("", "", "", "", "", ""); }
        long b = e.BytesEnviados, delta = Math.Max(0, b - _streamBytesAntes);
        _streamBytesAntes = b;
        double v = e.Velocidade, adiantado = e.Adiantado;
        // no anfitrião, stream_cache_s é quanto o envio já mandou à frente da posição do filme (o buffer que quem assiste deveria ter)
        return (e.Epoca.ToString(ci), double.IsNaN(adiantado) ? "" : adiantado.ToString("0.##", ci), "", double.IsNaN(v) ? "" : v.ToString("0.##", ci), _streamSids.Count.ToString(ci), (delta * 8 / 1e6).ToString("0.##", ci));
    }

    /// <summary>Chamado ao criar a tela do filme: liga o interruptor do anfitrião e o botão "Assistir agora" de quem assiste.</summary>
    void ConectarStreamFilme(FilmeTela tela)
    {
        tela.TransmitirPedido += ligar => _ = LigarStreamFilmeAsync(ligar);
        tela.AnalisarParaTransmissao = AnalisarProximoParaStreamAsync;
        tela.AnfitriaoTrocouArquivo = AnfitriaoTrocouArquivo;
        tela.AssistirStreamPedido += anuncio => _ = AssistirStreamAsync(anuncio);
    }

    // ------------------------------------------------------------------ anfitrião

    /// <summary>O anfitrião liga ou desliga "Transmitir para quem não tem o arquivo": lê o arquivo, escolhe o perfil e passa a seguir a Sessao.</summary>
    async Task LigarStreamFilmeAsync(bool ligar)
    {
        if (!ligar) { DesligarStreamFilme(); return; }
        // já ligando (o filme abre com a transmissão ligada): espera terminar, para quem chamou ver o envio pronto
        if (_streamLigando)
        {
            int esperando = _streamGeracao;
            while (_streamLigando && esperando == _streamGeracao && !IsDisposed) await Task.Delay(50);
            return;
        }
        if (_filmeStream != null) return;
        if (_filmeForm is not { ArquivoAnfitriao: string arquivo }) { Notify("Abra um filme primeiro para transmiti-lo."); _filmeForm?.StreamRecusado(); return; }
        _streamLigando = true;
        int geracao = ++_streamGeracao;
        try
        {
            if (!Media.Tools.Available(out var falta)) throw new InvalidOperationException(falta);
            var info = await Task.Run(() => Fonte.Analisar(Media.Tools.Ffmpeg, arquivo));
            // o anfitrião fechou (ou trocou) o filme enquanto o arquivo era lido: não liga um envio para um arquivo que não está mais aberto
            if (geracao != _streamGeracao || IsDisposed) return;
            if (_filmeForm?.ArquivoAnfitriao != arquivo) { _filmeForm?.StreamRecusado(); return; }
            var perfil = Fonte.Escolher(info, Math.Max(1, _streamSids.Count + 1), UploadSupostoMbps, nucleos: Environment.ProcessorCount);
            _streamPerfilForcado = Enum.TryParse(Environment.GetEnvironmentVariable("STREEMU_TEST_FILME_PERFIL"), true, out PerfilStream forcado);
            if (_streamPerfilForcado) perfil = forcado; // só em teste
            Log.Info($"[filme] transmitindo o filme sem download: {info.Resumo}; perfil {perfil} ({Environment.ProcessorCount} threads, placa {(perfil == PerfilStream.Copia ? "sem uso (cópia direta)" : ConversaoPlaca.Ligada ? "convertendo (Vulkan + AMF)" : Fonte.Placa ? "decodificando" : "desligada")}).");
            var e = _filmeStream = new EnvioStream(info, perfil) { UploadMbps = UploadSupostoMbps };
            e.Mudou += (epoca, pos, tocando) => Ui(() => AvisarEspectadores(new JsonObject { ["type"] = "filme_stream_epoca", ["epoca"] = epoca, ["pos"] = pos, ["tocando"] = tocando }));
            e.Lento += v => Ui(() => EnvioLento(e, v));
            await SeguirFaixaAudioAsync(e); // antes da primeira época: o fluxo já sai com a faixa que o anfitrião ouve
            if (!ReferenceEquals(_filmeStream, e)) return; // fechou o filme ou desligou enquanto lia
            _streamSegue = new System.Windows.Forms.Timer { Interval = 400 };
            _streamSegue.Tick += (_, _) => SeguirSessaoNoStream();
            _streamSegue.Start();
            if (_filmeMeu != null) AnunciarSala(_filmeMeu);
        }
        catch (Exception e)
        {
            if (geracao != _streamGeracao || IsDisposed) return;
            Log.Info($"[filme] não deu para transmitir o filme sem download: {e.Message}");
            Notify($"Não deu para transmitir o filme: {e.Message}");
            _filmeForm?.StreamRecusado();
        }
        finally { if (geracao == _streamGeracao) _streamLigando = false; }
    }

    void DesligarStreamFilme()
    {
        // Desmarcar enquanto o cabeçalho é analisado também cancela a abertura; a resposta antiga não pode ligar o envio de novo.
        _streamGeracao++;
        _streamLigando = false;
        _streamSegue?.Stop(); _streamSegue?.Dispose(); _streamSegue = null;
        var e = _filmeStream; _filmeStream = null;
        _streamSids.Clear();
        _streamTodosAntes = null;
        _streamAvisouLento = false;
        if (_streamSessao is { } s) { s.Mudou -= SessaoMudouParaStream; s.FluxoIds = null; }
        _streamSessao = null;
        if (e != null) { Log.Info($"[filme] transmissão do filme desligada (época {e.Epoca})."); Task.Run(e.Dispose); }
        if (_filmeMeu != null) AnunciarSala(_filmeMeu);
    }

    /// <summary>
    /// O anfitrião fechou o filme (AnunciarSala(null)). Caso de 11/11: o envio continuava ligado e o filme seguinte saía do arquivo ANTERIOR (o LigarStreamFilmeAsync
    /// via o envio velho e saía cedo, e o SeguirSessaoNoStream aplicava o filme novo a ele); o anúncio seguia com "stream" ligado.
    /// </summary>
    void FimFilmeDoAnfitriao()
    {
        if (_filmeStream == null && !_streamLigando) return;
        DesligarStreamFilme();
        _filmeForm?.StreamRecusado(); // desmarca "Transmitir para quem não tem o arquivo" sem pedir para desligar de novo
    }

    Sessao? _streamSessao;

    /// <summary>A Sessao do anfitrião mudou (tocar, pausar, pular): o envio segue na hora, sem esperar a passada de 400 ms (um pulo para fora do buffer abre a época logo).</summary>
    void SessaoMudouParaStream() => Ui(SeguirSessaoNoStream);

    /// <summary>
    /// O envio segue a Sessao do anfitrião: é ela que tem as permissões, o relógio comum e os botões. Quem assiste recebe o mesmo filme_estado que quem tem o arquivo e
    /// segue a linha do tempo pelo buffer do mpv; o envio só abre uma época nova quando o ponto não está guardado (EnvioStream.Seguir).
    /// </summary>
    void SeguirSessaoNoStream()
    {
        if (_filmeStream is not { } e || _filmeForm?.Sessao is not { Anfitriao: true } s) return;
        if (!ReferenceEquals(_streamSessao, s))
        {
            if (_streamSessao is { } velha) { velha.Mudou -= SessaoMudouParaStream; velha.FluxoIds = null; }
            _streamSessao = s;
            s.Mudou += SessaoMudouParaStream;
            // quem assiste pela transmissão conta para a hora marcada e para a espera do pulo (D4), pelo apelido da ligação
            s.FluxoIds = () => _streamSids.Select(sid => _filmeLinks.TryGetValue(sid, out var l) ? l.PeerId : null).OfType<string>().Distinct().ToList();
        }
        if (_streamTodosAntes != s.TodosControlam)
        {
            if (_streamTodosAntes != null) Log.Info($"[filme] \"Todos controlam\" {(s.TodosControlam ? "ligado" : "desligado")}: quem assiste pela transmissão {(s.TodosControlam ? "pode" : "não pode mais")} tocar, pausar e pular.");
            _streamTodosAntes = s.TodosControlam;
        }
        if (++_streamPassadas % 5 == 0) _ = SeguirFaixaAudioAsync(e); // a cada ~2 s: o anfitrião pode ter trocado a faixa de áudio no mpv dele
        e.Seguir(s.Posicao, s.Tocando);
    }

    int _streamPassadas;
    bool _streamLendoFaixa, _streamPerfilForcado, _streamAvisouLento, _streamRebaixou;

    // ------------------------------------------------------------------ fila de episódios: o anfitrião passou para o arquivo seguinte (FilmeTela.Fila.cs)

    /// <summary>Antes da troca: com a transmissão ligada, lê o arquivo novo (ffmpeg, fora da janela). Nulo = transmissão desligada ou arquivo ilegível.</summary>
    async Task<FonteInfo?> AnalisarProximoParaStreamAsync(string caminho)
    {
        if (_filmeStream == null) return null;
        try
        {
            if (!Media.Tools.Available(out var falta)) throw new InvalidOperationException(falta);
            return await Task.Run(() => Fonte.Analisar(Media.Tools.Ffmpeg, caminho));
        }
        catch (Exception e) { Log.Info($"[filme] fila: não deu para ler o próximo arquivo para a transmissão: {e.Message}"); return null; }
    }

    /// <summary>
    /// A Sessão do anfitrião já é a do arquivo novo (mesmo trecho, sem espera): o envio passa a ler o arquivo novo numa época em 0 e quem baixa o arquivo velho é cortado
    /// (o que o <see cref="Envio"/> serve é o arquivo e o id velhos). Caso de 11/11 do FimFilmeDoAnfitriao: o envio não pode continuar mandando o arquivo anterior.
    /// </summary>
    void AnfitriaoTrocouArquivo(FonteInfo? info, FaixaAudio? faixa)
    {
        foreach (var t in _filmeEnvios.Keys.ToList()) { var envio = _filmeEnvios[t]; _filmeEnvios.Remove(t); Task.Run(envio.Dispose); }
        if (_filmeStream is not { } e) return;
        if (info == null)
        {
            // a transmissão estava ligada e o arquivo novo não pôde ser lido: parar é melhor que seguir mandando o arquivo velho para quem assiste
            AvisarEspectadores(new JsonObject { ["type"] = "filme_stream_aviso", ["texto"] = $"{Nick} passou para o próximo episódio e a transmissão não conseguiu abri-lo. Baixe o arquivo para continuar." });
            DesligarStreamFilme();
            _filmeForm?.StreamRecusado();
            Notify("Não deu para transmitir o próximo episódio: a transmissão foi desligada. Quem assiste sem baixar precisa do arquivo.");
            return;
        }
        var perfil = Fonte.Escolher(info, Math.Max(1, _streamSids.Count), UploadSupostoMbps, nucleos: Environment.ProcessorCount);
        if (_streamPerfilForcado && Enum.TryParse(Environment.GetEnvironmentVariable("STREEMU_TEST_FILME_PERFIL"), true, out PerfilStream forcado)) perfil = forcado; // só em teste
        else if (_streamRebaixou && perfil < e.Perfil) perfil = e.Perfil; // o Leve por lentidão ou excesso de gente continua no episódio seguinte
        e.TrocarFonte(info, perfil, faixa);
        SeguirSessaoNoStream(); // já segue a Sessão nova (parada em 0): sem isso a passada de 400 ms ainda a veria como a velha
    }

    /// <summary>
    /// O envio anda abaixo do tempo real (caso de 11/11: ~0,3x num notebook recomprimindo um 4K HDR, e ninguém percebia). No Padrão cai para o Leve; no Leve, ou na
    /// cópia direta (que não recomprime: lento ali é disco, e o Leve só pioraria), avisa o anfitrião uma vez, porque quem assiste vai travar.
    /// </summary>
    void EnvioLento(EnvioStream e, double v)
    {
        if (!ReferenceEquals(_filmeStream, e)) return;
        string x = v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        if (e.Perfil == PerfilStream.Padrao && !_streamPerfilForcado)
        {
            _streamRebaixou = true; // o próximo episódio não volta a subir sozinho
            e.TrocarPerfil(PerfilStream.Leve, $"o envio andava a {x}x do tempo real");
            AvisarEspectadores(new JsonObject { ["type"] = "filme_stream_aviso", ["texto"] = $"O PC de {Nick} não estava dando conta: a imagem passou para a qualidade leve." });
            return;
        }
        if (_streamAvisouLento) return;
        _streamAvisouLento = true;
        Notify($"Seu PC não está dando conta de transmitir o filme ({x}x do tempo real): quem assiste vai travar. Feche programas pesados ou peça para baixarem o arquivo.");
    }

    /// <summary>O envio manda a faixa de áudio que o mpv do anfitrião está tocando (Fonte.FaixaDoMpvAsync); se ela mudou, o EnvioStream abre uma época nova.</summary>
    async Task SeguirFaixaAudioAsync(EnvioStream e)
    {
        if (_streamLendoFaixa || _filmeForm?.Player is not { Exited: false } mpv) return;
        _streamLendoFaixa = true;
        try { if (await Fonte.FaixaDoMpvAsync(mpv) is { } faixa && ReferenceEquals(_filmeStream, e)) e.TrocarAudio(faixa); }
        catch (Exception x) { Log.Info($"[filme] não deu para ler a faixa de áudio do mpv: {x.Message}"); }
        finally { _streamLendoFaixa = false; }
    }

    void AvisarEspectadores(JsonObject m)
    {
        foreach (var sid in _streamSids.ToList())
            if (_filmeLinks.TryGetValue(sid, out var link) && m.DeepClone() is JsonObject copia) link.Tunnel.SendControl(copia);
    }

    /// <summary>Mensagens da ligação do filme sobre o fluxo (anfitrião e quem assiste); chamado pelo AdicionarLinkFilme.</summary>
    void FilmeStreamControle(PeerLink link, JsonObject m)
    {
        switch (m["type"]?.GetValue<string>())
        {
            case "filme_stream_quero" when link.IAmSender && _filmeStream is { } e:
                // caso de 11/11: dois players abertos para a mesma pessoa; aqui aparece como o mesmo amigo pedindo por duas ligações
                int outras = _streamSids.Count(sid => sid != link.SessionId && _filmeLinks.TryGetValue(sid, out var l) && l.PeerId == link.PeerId);
                if (outras > 0) Log.Info($"[filme] [aviso] {link.PeerNick} pediu o filme pela transmissão por outra ligação e já recebe por {outras}: são dois players abertos para ele.");
                e.Adicionar(link); _streamSids.Add(link.SessionId); _streamVistos[link.PeerId] = link.PeerNick;
                _filmeForm?.PessoasMudaram(); // "Na sessão" de todos passa a ter esta pessoa (FilmeTela.Fila.cs)
                // o perfil foi escolhido para quem já assistia: com mais gente, a cópia direta pode não caber no upload (só desce de qualidade, nunca sobe no meio do filme)
                var cabe = Fonte.Escolher(e.Info, _streamSids.Count, UploadSupostoMbps, nucleos: Environment.ProcessorCount);
                if (!_streamPerfilForcado && cabe > e.Perfil)
                {
                    e.TrocarPerfil(cabe, $"{_streamSids.Count} assistindo");
                    AvisarEspectadores(new JsonObject { ["type"] = "filme_stream_aviso", ["texto"] = $"Mais gente entrou: a imagem passou para a qualidade {(cabe == PerfilStream.Leve ? "leve" : "padrão")}." });
                }
                // o envio abre (ou reabre) a época já, para a resposta levar o número dela; o estado da Sessao vai junto para quem entra no meio do filme
                SeguirSessaoNoStream();
                link.Tunnel.SendControl(new JsonObject
                {
                    ["type"] = "filme_stream_info", ["titulo"] = _filmeMeu?["nome"]?.DeepClone() ?? "filme", ["duracao"] = e.Info.DuracaoS,
                    ["perfil"] = $"{e.Perfil} · {e.Info.Resumo}", ["epoca"] = e.Epoca, ["pos"] = e.Posicao, ["tocando"] = e.Tocando,
                    ["estado"] = _filmeForm?.Sessao is { Anfitriao: true } sessao ? sessao.EstadoAtual() : null,
                });
                Log.Info($"[filme] {link.PeerNick} assiste pela transmissão ({e.Perfil}).");
                break;
            case "filme_stream_recarregar" when link.IAmSender && _filmeStream is { } e:
                Log.Info($"[filme] {link.PeerNick} não tem o ponto do filme guardado: reabrindo o envio.");
                e.Recarregar(link.PeerNick);
                break;
            case "filme_stream_carregado" when link.IAmSender:
                // D4: quem assiste pela transmissão já está no ponto do pulo; vira o filme_carregado da Sessao com o id da ligação (o mesmo do FluxoIds)
                _filmeForm?.Receber(new JsonObject { ["type"] = "filme_carregado", ["filme"] = m["filme"]?.DeepClone(), ["seq"] = m["seq"]?.DeepClone(), ["nome"] = link.PeerNick, ["id"] = link.PeerId });
                break;
            case "filme_estado" when !link.IAmSender && link.SessionId == _streamSid: _streamTela?.Estado(m); break;
            case "filme_permissoes" when !link.IAmSender && link.SessionId == _streamSid: _streamTela?.Permissoes(m); break;
            // fila de episódios e "Na sessão" inteira: o anfitrião manda a todos; app antigo não manda e a janela fica como era
            case "filme_pessoas" when !link.IAmSender && link.SessionId == _streamSid: _streamTela?.Pessoas(m); break;
            case "filme_fila" when !link.IAmSender && link.SessionId == _streamSid: _streamTela?.Fila(m); break;
            case "filme_trocar" when !link.IAmSender && link.SessionId == _streamSid: _streamTela?.Trocar(m); break;
            case "filme_stream_quero" when link.IAmSender:
                link.Tunnel.SendControl(new JsonObject { ["type"] = "filme_stream_aviso", ["texto"] = $"{Nick} desligou a transmissão do filme." });
                break;
            case "filme_stream_cmd" when link.IAmSender: ComandoDeEspectador(link, m); break;
            case "filme_stream_info" when !link.IAmSender && link.SessionId == _streamPedidoSid: _streamInfo?.TrySetResult(m); break;
            case "filme_stream_epoca" when !link.IAmSender && link.SessionId == _streamSid:
                _streamTela?.Epoca(m["epoca"]?.GetValue<int>() ?? 0, m["pos"]?.GetValue<double>() ?? 0);
                break;
            case "filme_stream_aviso" when !link.IAmSender && link.SessionId == _streamSid: _streamTela?.Aviso(m["texto"]?.GetValue<string>() ?? ""); break;
        }
    }

    /// <summary>Quem assiste pediu tocar, pausar ou pular: vale se o anfitrião deixou todos controlarem (as permissões da spec 010); senão, o motivo volta.</summary>
    void ComandoDeEspectador(PeerLink link, JsonObject m)
    {
        double? pos = m["pos"]?.GetValue<double>();
        string pedido = m["cmd"]?.GetValue<string>() + (pos is double p ? $" {p:0.#} s" : "");
        if (_filmeForm?.Sessao is not { Anfitriao: true } s) { Log.Info($"[filme] {link.PeerNick} pediu {pedido}: recusado (não há filme aberto como anfitrião)."); return; }
        if (!s.TodosControlam)
        {
            Log.Info($"[filme] {link.PeerNick} pediu {pedido} pela transmissão: recusado (só o anfitrião controla).");
            link.Tunnel.SendControl(new JsonObject { ["type"] = "filme_stream_aviso", ["texto"] = $"Só {Nick} controla o filme." });
            return;
        }
        Log.Info($"[filme] {link.PeerNick} pediu {pedido} pela transmissão: aceito (em {s.Posicao:0.#} s).");
        switch (m["cmd"]?.GetValue<string>())
        {
            case "tocar": s.Tocar(); break;
            case "pausar": s.Pausar(); break;
            case "ir" when pos != null: s.Ir(pos.Value); break;
        }
    }

    // ------------------------------------------------------------------ quem assiste

    /// <summary>"Assistir agora, sem baixar": entra na sala do filme (ligação própria), pede o fluxo e abre a janela com o mpv.</summary>
    async Task AssistirStreamAsync(JsonObject? anuncio, bool teste = false)
    {
        if (_streamTela is { IsDisposed: false }) { if (!teste) RetomarStreamFilme(); if (_filmeForm != null) _filmeForm.StreamAbrindo = false; return; }
        // caso de 11/11: um clique duplo abria dois players e duas ligações, porque a _streamTela só existe depois de conectar (~1 s)
        if (anuncio == null || _streamAbrindo) return;
        _streamAbrindo = true;
        try { await AssistirStreamLigarAsync(anuncio, teste); }
        finally { _streamAbrindo = false; _streamPedidoSid = null; _streamInfo = null; if (_filmeForm != null) _filmeForm.StreamAbrindo = false; }
    }

    bool _streamAbrindo;
    string? _streamPedidoSid;

    void RetomarStreamFilme()
    {
        if (_streamTela is not { IsDisposed: false } tela) return;
        if (tela.TopLevel) { if (!Automated) tela.Activate(); return; }
        MostrarFilme();
        _filmeForm?.RetomarPlayer();
    }

    void AlternarTelaCheiaCinema()
    {
        ToggleFullscreen();
        if (_filmeForm != null) _filmeForm.EmTelaCheia = _fullscreenStage;
        if (_streamTela != null) _streamTela.EmTelaCheia = _fullscreenStage;
    }

    async Task AssistirStreamLigarAsync(JsonObject anuncio, bool teste)
    {
        // caso de 11/11: sem o mpv, "Assistir agora" só mandava abrir o filme pelo botão normal; agora instala como ele (MpvInstalador), com o progresso no card do filme
        string? mpvExe = await Task.Run(Mpv.Find); // fora da janela: a primeira conferência do SHA-256 do mpv leva uns instantes
        if (mpvExe == null)
        {
            var tela = _filmeForm;
            try { mpvExe = await Task.Run(() => MpvInstalador.InstalarAsync(RelayConfig.Load(_state), new Progress<string>(t => tela?.Avisar(t)), CancellationToken.None)); }
            catch (Exception e)
            {
                Log.Info($"[filme] mpv: {e.Message}");
                Notify($"Não consegui baixar o player do filme: {e.Message}.");
                tela?.Avisar($"Não consegui baixar o player do filme: {e.Message}.");
                return;
            }
        }
        var (erro, link) = await LigarAoAnfitriaoAsync(anuncio);
        if (erro != null || link == null) { Notify(erro ?? "Não consegui conectar com o anfitrião."); return; }
        _streamInfo = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _streamPedidoSid = link.SessionId;
        link.Tunnel.SendControl(new JsonObject { ["type"] = "filme_stream_quero" });
        var pronto = await Task.WhenAny(_streamInfo.Task, Task.Delay(15000));
        if (pronto != _streamInfo.Task) { Notify($"{link.PeerNick} não respondeu ao pedido para assistir. Ele precisa estar com a transmissão do filme ligada."); SairLigacao(link.SessionId); return; }
        var info = _streamInfo.Task.Result;
        if (IsDisposed || Disposing || !_filmeLinks.TryGetValue(link.SessionId, out var atual) || atual != link) return;
        try
        {
            string perfil = info["perfil"]?.GetValue<string>() ?? "";
            // a hora do anfitrião (o relógio comum da ligação, 7.2): a janela segue a linha do tempo da Sessao dele
            var tela = new StreamTela(info["titulo"]?.GetValue<string>() ?? (string?)anuncio["nome"] ?? "Filme", EnvioStream.UrlDoEspectador(link), info["duracao"]?.GetValue<double>() ?? 0, perfil,
                () => link.Tunnel.PeerClockOffsetUs is long o ? Tunnel.NowUs + o : null, link.PeerNick) { MeuNome = Nick, MeuId = _room?.MyId ?? "" };
            tela.Comando += (cmd, pos) =>
            {
                Log.Info($"[filme] pedi {cmd}{(pos is double p ? $" {p:0.#} s" : "")} ao anfitrião pela transmissão.");
                link.Tunnel.SendControl(new JsonObject { ["type"] = "filme_stream_cmd", ["cmd"] = cmd, ["pos"] = pos });
            };
            tela.Carregado += (seq, filme) => link.Tunnel.SendControl(new JsonObject { ["type"] = "filme_stream_carregado", ["seq"] = seq, ["filme"] = filme });
            tela.Recarregar += () => link.Tunnel.SendControl(new JsonObject { ["type"] = "filme_stream_recarregar" });
            // quem assiste pela transmissão não tem Sessao: os eventos filme e filme_fim (papel "stream") saem daqui, para a análise não ficar cega deste lado
            string? Caminho() => link.Tunnel.Target is { } c ? (c.Kind == CandKind.Direct ? "direto" : "relay") : null;
            var filmeId = anuncio["filme"]?.DeepClone();
            tela.Fechou += () =>
            {
                var fim = tela.Resumo();
                fim["papel"] = "stream"; fim["filme"] = filmeId?.DeepClone(); fim["perfil"] = perfil; fim["caminho"] = Caminho(); fim["anfitriao"] = link.PeerNick;
                _teleSessao?.Evento("filme_fim", fim);
                GravarSerieFilme();
                // só a ligação desta janela: o SairSala() derrubava todas e deixava outra janela órfã (caso de 11/11)
                if (_streamTela == tela) { _streamTela = null; _streamSid = null; }
                SairLigacao(link.SessionId);
                if (!teste && !IsDisposed && !Disposing && _fullscreenStage) AlternarTelaCheiaCinema();
            };
            _streamTela = tela; _streamSid = link.SessionId; _streamAnfitriao = link.PeerNick;
            if (teste) { tela.ModoTeste(); tela.Show(); }
            else
            {
                tela.TelaCheiaPedida += AlternarTelaCheiaCinema;
                _filmeForm!.MostrarTransmissao(tela);
                MostrarFilme();
            }
            await tela.AbrirAsync(mpvExe);
            if (tela.IsDisposed || _streamTela != tela) return;
            Log.Info($"[filme] assistindo {link.PeerNick} pela transmissão ({perfil}), caminho {Caminho() ?? "?"}.");
            _teleSessao?.Evento("filme", new JsonObject
            {
                ["papel"] = "stream", ["filme"] = filmeId?.DeepClone(), ["perfil"] = perfil, ["duracao_s"] = info["duracao"]?.DeepClone(), ["caminho"] = Caminho(),
                ["relogio_ida_volta_ms"] = Math.Round(link.Tunnel.PeerClockRttMs, 2), ["anfitriao"] = link.PeerNick,
            });
            if (info["estado"] is JsonObject estado) tela.Estado(estado);
            tela.Epoca(info["epoca"]?.GetValue<int>() ?? 0, info["pos"]?.GetValue<double>() ?? 0);
        }
        catch (Exception e)
        {
            Log.Info($"[filme] não deu para abrir o filme pela transmissão: {e.Message}");
            Notify($"Não deu para abrir o filme: {e.Message}");
            if (_streamTela != null) _streamTela.Close(); else SairLigacao(link.SessionId);
        }
    }

    /// <summary>A ligação com um amigo do filme acabou: ele deixa de receber o fluxo; se era a ligação do player aberto aqui, o player fecha.</summary>
    void FimStreamDaLigacao(string sid)
    {
        if (_streamSids.Remove(sid)) _filmeForm?.PessoasMudaram();
        _filmeStream?.Remover(sid);
        if (sid == _streamSid && _streamTela is { } t)
            FecharStreamTela(t, $"A conexão com {_streamAnfitriao} caiu. Clique em \"Assistir agora\" de novo.", "a ligação com o anfitrião acabou");
    }

    /// <summary>
    /// Quem assiste: o anfitrião fechou o filme (filme_fim pela ligação ou filme_fechado pela sala). Caso de 11/11: quem assiste não tem Sessao, o filme_fim não fazia
    /// nada e o player ficava aberto e congelado até a pessoa fechar o app.
    /// </summary>
    void StreamAnfitriaoFechou(string peerId)
    {
        if (_streamTela is not { } t || _streamSid == null || !_filmeLinks.TryGetValue(_streamSid, out var l) || l.PeerId != peerId) return;
        FecharStreamTela(t, $"{l.PeerNick} fechou o filme.", "o anfitrião fechou o filme");
    }

    void FecharStreamTela(StreamTela t, string aviso, string motivo)
    {
        Log.Info($"[filme] fechando o player do filme pela transmissão: {motivo}.");
        Notify(aviso);
        _filmeForm?.Avisar(aviso);
        t.FecharPor(motivo);
    }

    // ------------------------------------------------------------------ teste automático

    /// <summary>
    /// STREEMU_TEST_FILME_STREAM=1 (com STREEMU_TEST_FILME=&lt;arquivo&gt;, par A/B pela sala): A abre o filme e liga a transmissão; B não tem o arquivo e assiste pelo botão "Assistir agora".
    /// Roteiro de A: tocar em 10 s, pular para 40 s, pausar, continuar. B pede "ir 15" no fim (A libera todos controlarem). Cada um grava &lt;papel&gt;-filme-stream.json com os
    /// horários, para o teste conferir que a posição de B acompanha a de A. Janelas fora da tela e sem som.
    /// </summary>
    async Task TesteFilmeStreamAsync(string arquivo)
    {
        if (Environment.GetEnvironmentVariable("STREEMU_TEST_FILME_FILA") == "1") { await TesteFilmeFilaAsync(arquivo); return; }
        await Task.Delay(1000);
        var form = AbrirFilme();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = new JsonObject();
        var marcas = new JsonArray();
        void Marca(string o, double? v = null) => marcas.Add(new JsonObject { ["o"] = o, ["utc_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["v"] = v });
        if (TestRole == "A")
        {
            await form.EscolherAsync(arquivo);
            // A abertura deve ligar sozinha: chamar LigarStreamFilmeAsync aqui esconderia uma regressão do padrão.
            while (_streamLigando && sw.Elapsed.TotalSeconds < 20) await Task.Delay(50);
            r["stream_ligado"] = _filmeStream != null;
            while (_streamSids.Count == 0 && sw.Elapsed.TotalSeconds < 40) await Task.Delay(100);
            r["espectadores"] = _streamSids.Count;
            if (form.Sessao is { } s && _filmeStream is { } e)
            {
                s.MudarPermissao(true);
                s.EsperarTodos = false;
                Marca("tocar", 10); s.Tocar(10); await Task.Delay(8000);
                Marca("ir", 40); s.Ir(40); await Task.Delay(8000);
                int epocaPausa = e.Epoca;
                Marca("pausar"); s.Pausar(); await Task.Delay(4000);
                Marca("tocar"); s.Tocar(); await Task.Delay(2000);
                r["epocas_na_pausa"] = e.Epoca - epocaPausa;
                await Task.Delay(6000); // B pede o pulo depois: não contar essa reabertura como se fosse da pausa
                r["perfil"] = e.Perfil.ToString(); r["info"] = e.Info.Resumo; r["epocas"] = e.Epoca;
                await Task.Delay(14000); // B ainda pede "ir 15" e confere
                // seta segurada (caso de 11/11): 8 pulos em 1,6 s têm que virar no máximo 2 épocas (o primeiro na hora e o ponto final). Para trás (−5 s, de ~69 s
                // a ~30 s): para frente o vídeo de teste (90 s) acaba no meio da rajada, e parado cada pulo só muda o ponto (conta época sem reabrir nada)
                int antes = e.Epoca;
                Marca("rajada");
                for (int i = 0; i < 8; i++) { s.Ir(Math.Max(0, s.Posicao - 5)); await Task.Delay(200); }
                await Task.Delay(2500);
                r["rajada_epocas"] = e.Epoca - antes;
            }
            else await Task.Delay(14000);
            // fechar o filme desliga o envio (caso de 11/11: o filme seguinte saía do arquivo anterior) e fecha o player de B
            Marca("fechar");
            form.Fechar();
            r["stream_desligado"] = _filmeStream == null;
            await Task.Delay(1500);
            _filmeJanelaTeste?.Close();
            string segundo = Path.Combine(TestDir!, "segundo-" + Path.GetFileName(arquivo));
            File.Copy(arquivo, segundo, overwrite: true);
            form = AbrirFilme();
            await form.EscolherAsync(segundo);
            await LigarStreamFilmeAsync(true);
            r["segundo_arquivo_certo"] = _filmeStream?.Info.Caminho == segundo;
        }
        else
        {
            while (!_filmeAbertos.Values.Any(v => v.Msg["stream"]?.GetValue<bool>() == true) && sw.Elapsed.TotalSeconds < 40) await Task.Delay(100);
            var anuncio = _filmeAbertos.Values.Select(v => v.Msg).FirstOrDefault(m => m["stream"]?.GetValue<bool>() == true);
            var ate = System.Diagnostics.Stopwatch.StartNew();
            var task = AssistirStreamAsync(anuncio, teste: true);
            var duplo = AssistirStreamAsync(anuncio, teste: true); // clique duplo (caso de 11/11): tem que abrir um player e uma ligação só
            while (_streamTela?.PosicaoMpv is not > 0.5 && ate.Elapsed.TotalSeconds < 40) await Task.Delay(100);
            r["primeiro_quadro_ms"] = ate.ElapsedMilliseconds;
            await task; await duplo;
            await AssistirStreamAsync(anuncio, teste: true); // com o player aberto, o botão só o traz para a frente
            r["players_abertos"] = StreamTela.Abertas;
            r["ligacoes"] = _filmeLinks.Values.Count(l => !l.IAmSender);
            var tela = _streamTela;
            var amostras = new JsonArray();
            bool pediu = false;
            sw.Restart();
            while (sw.Elapsed.TotalSeconds < 40 && _streamTela != null)
            {
                amostras.Add(new JsonObject { ["utc_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["pos"] = _streamTela.PosicaoMpv is null ? null : _streamTela.PosicaoFilme, ["tocando"] = _streamTela.Tocando });
                if (!pediu && sw.Elapsed.TotalSeconds > 24) { pediu = true; Marca("pedir_ir", 15); _streamTela.PedirParaTeste("ir", 15); }
                await Task.Delay(150);
            }
            r["amostras"] = amostras; r["epocas_recebidas"] = _streamTela?.EpocasRecebidas;
            // contra a linha do tempo do anfitrião, depois de alinhar (a meta da spec 011 é 100 ms)
            r["erro_p95_ms"] = _streamTela?.ErroP95Ms; r["amostras_erro"] = _streamTela?.AmostrasErro;
            // A fecha o filme: o player de B tem que fechar sozinho (caso de 11/11: ficava aberto e congelado)
            sw.Restart();
            while (tela is { IsDisposed: false } && sw.Elapsed.TotalSeconds < 40) await Task.Delay(100);
            r["fechou_sozinho"] = tela?.IsDisposed == true;
            r["motivo_fechou"] = tela?.MotivoFechou;
            Marca("fechou");
            _streamTela?.Close();
        }
        r["marcas"] = marcas;
        File.WriteAllText(Path.Combine(TestDir!, TestRole + "-filme-stream.json"), r.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Log.Info("teste-filme-stream: " + TestRole + " gravou o relatório.");
        form.Fechar();
        _filmeJanelaTeste?.Close();
    }

    /// <summary>
    /// STREEMU_TEST_FILME_FILA=1 (junto com STREEMU_TEST_FILME_STREAM=1 e STREEMU_TEST_FILME=&lt;arquivo&gt;, par A/B pela sala): a fila de episódios. A abre o arquivo e uma cópia dele (com 1 byte
    /// a mais, para ser outro arquivo de verdade: outra identidade) como fila de 2; B NÃO tem o arquivo e assiste pela transmissão. A toca, pede Próximo, depois Anterior, e por último
    /// pula para perto do fim do episódio 1 para o fim sozinho chamar o próximo. B grava, a cada 150 ms, o título, a posição e se toca (para o teste medir quanto levou a voltar a tocar
    /// depois de cada troca) e, no fim, se a janela continuou a mesma (a troca não pode fechá-la nem abrir outra), o erro p95 depois da troca e as ligações. Cada um grava
    /// &lt;papel&gt;-filme-fila.json. Janelas fora da tela e sem som (regra 2 do AGENTS.md).
    /// </summary>
    async Task TesteFilmeFilaAsync(string arquivo)
    {
        await Task.Delay(1000);
        var form = AbrirFilme();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = new JsonObject();
        var marcas = new JsonArray();
        void Marca(string o, double? v = null) => marcas.Add(new JsonObject { ["o"] = o, ["utc_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["v"] = v });
        if (TestRole == "A")
        {
            string segundo = Path.Combine(TestDir!, "episodio2-" + Path.GetFileName(arquivo));
            File.Copy(arquivo, segundo, overwrite: true);
            using (var fs = new FileStream(segundo, FileMode.Append)) fs.WriteByte(0);
            await form.EscolherAsync(arquivo);
            while (_streamLigando && sw.Elapsed.TotalSeconds < 20) await Task.Delay(50);
            form.AdicionarNaFila([segundo]);
            r["nome1"] = Path.GetFileNameWithoutExtension(arquivo); r["nome2"] = Path.GetFileNameWithoutExtension(segundo);
            r["itens_na_fila"] = form.FilaItens.Count;
            r["stream_ligado"] = _filmeStream != null;
            while (_streamSids.Count == 0 && sw.Elapsed.TotalSeconds < 40) await Task.Delay(100);
            r["espectadores"] = _streamSids.Count;
            if (form.Sessao is { } s && _filmeStream is { } e)
            {
                string filme1 = s.Filme;
                s.EsperarTodos = false;
                Marca("tocar", 10); s.Tocar(10); await Task.Delay(9000);
                Marca("proximo"); await form.ProximoAsync();
                r["indice_apos_proximo"] = form.FilaIndice;
                r["anuncio_mudou"] = _filmeMeu?["filme"]?.GetValue<string>() != filme1;
                r["ligacoes_apos_proximo"] = _filmeLinks.Values.Count(l => l.IAmSender);
                r["envio_no_arquivo_novo"] = _filmeStream?.Info.Caminho == segundo;
                r["pessoas_a"] = new JsonArray(form.NomesNaSessao.Select(n => (JsonNode?)n).ToArray()); // quem assiste pela transmissão tem que aparecer aqui (caso de 2026-11-18)
                await Task.Delay(9000);
                Marca("anterior"); await form.AnteriorAsync();
                r["indice_apos_anterior"] = form.FilaIndice;
                r["envio_no_arquivo_velho"] = _filmeStream?.Info.Caminho == arquivo;
                await Task.Delay(9000);
                // o fim sozinho: perto do fim do episódio 1 (o filme de teste tem ~90 s); o mpv para no fim e a fila passa para o episódio 2 sem ninguém clicar
                double dur = e.Info.DuracaoS;
                Marca("ir_fim", Math.Max(0, dur - 6)); form.Sessao?.Ir(Math.Max(0, dur - 6));
                var fim = System.Diagnostics.Stopwatch.StartNew();
                while (form.FilaIndice != 1 && fim.Elapsed.TotalSeconds < 25) await Task.Delay(100);
                r["indice_apos_fim"] = form.FilaIndice;
                r["fim_sozinho_s"] = Math.Round(fim.Elapsed.TotalSeconds, 1);
                await Task.Delay(12000);
                r["trocas"] = form.TrocasDaFila;
                r["ligacoes_no_fim"] = _filmeLinks.Values.Count(l => l.IAmSender);
                r["espectadores_no_fim"] = _streamSids.Count;
            }
            Marca("fechar");
            form.Fechar();
            await Task.Delay(1500);
        }
        else
        {
            while (!_filmeAbertos.Values.Any(v => v.Msg["stream"]?.GetValue<bool>() == true) && sw.Elapsed.TotalSeconds < 40) await Task.Delay(100);
            var anuncio = _filmeAbertos.Values.Select(v => v.Msg).FirstOrDefault(m => m["stream"]?.GetValue<bool>() == true);
            var ate = System.Diagnostics.Stopwatch.StartNew();
            await AssistirStreamAsync(anuncio, teste: true);
            while (_streamTela?.PosicaoMpv is not > 0.5 && ate.Elapsed.TotalSeconds < 40) await Task.Delay(100);
            r["primeiro_quadro_ms"] = ate.ElapsedMilliseconds;
            var tela = _streamTela;
            r["titulo_inicial"] = tela?.TituloAtual;
            var amostras = new JsonArray();
            sw.Restart();
            bool mesmaJanela = true;
            int maxAbertas = 0;
            while (sw.Elapsed.TotalSeconds < 110 && tela is { IsDisposed: false })
            {
                if (!ReferenceEquals(_streamTela, tela)) mesmaJanela = false; // a troca de episódio não pode fechar nem trocar a janela
                maxAbertas = Math.Max(maxAbertas, StreamTela.Abertas);
                amostras.Add(new JsonObject
                {
                    ["utc_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["pos"] = tela.PosicaoMpv is null ? null : tela.PosicaoFilme,
                    ["tocando"] = tela.Tocando, ["titulo"] = tela.TituloAtual, ["trocas"] = tela.Trocas, ["lig"] = _filmeLinks.Values.Count(l => !l.IAmSender),
                });
                await Task.Delay(150);
            }
            r["amostras"] = amostras;
            r["janela_igual"] = mesmaJanela; r["max_players_abertos"] = maxAbertas;
            r["ligacoes"] = _filmeLinks.Values.Count(l => !l.IAmSender);
            r["trocas"] = tela?.Trocas;
            r["titulo_final"] = tela?.TituloAtual;
            r["fila_vista"] = tela == null ? 0 : tela.FilaNomes.Count;
            r["pessoas_vistas"] = new JsonArray((tela?.PessoasVistas ?? []).Select(n => (JsonNode?)n).ToArray());
            r["erro_p95_pos_troca_ms"] = tela?.ErroP95PosTrocaMs; r["amostras_pos_troca"] = tela?.AmostrasPosTroca;
            r["epocas_recebidas"] = tela?.EpocasRecebidas; r["epocas_sem_tocar"] = tela?.EpocasSemTocar;
            r["motivo_fechou"] = tela?.MotivoFechou;
            Marca("fechou");
            _streamTela?.Close();
        }
        r["marcas"] = marcas;
        File.WriteAllText(Path.Combine(TestDir!, TestRole + "-filme-fila.json"), r.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Log.Info("teste-filme-fila: " + TestRole + " gravou o relatório.");
        form.Fechar();
        _filmeJanelaTeste?.Close();
    }
}
