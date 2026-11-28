using System.Text.Json;
using System.Text.Json.Nodes;
using Streemu.Util;

namespace Streemu.Filme;

/// <summary>
/// A janela de quem assiste o filme pela transmissão do anfitrião (spec 011, T3 e T5): o mpv dentro (--wid) e os controles de um player de filme. Como o vídeo chega
/// como fluxo, o que muda o ponto do filme (tocar, pausar, pular) é um PEDIDO ao anfitrião (<see cref="Comando"/>); o que é só desta tela (volume, tela cheia, estatísticas,
/// captura de tela) vai direto para o mpv.
/// <para>
/// <b>Buffer e relógio comum.</b> O anfitrião manda o filme adiantado (<see cref="EnvioStream.AdiantadoS"/>) com o carimbo de tempo do próprio filme; o mpv guarda o que
/// chega (para frente e <see cref="EnvioStream.AtrasS"/> para trás) e esta tela o põe na linha do tempo da Sessão do anfitrião (<see cref="Estado"/>, o mesmo filme_estado
/// que quem tem o arquivo recebe): pausa e volta na hora marcada, no ponto exato, e corrige a velocidade como a <see cref="Sessao"/>. Caso de 11/11: antes cada continuar
/// reabria o fluxo no ponto do anfitrião e quem assistia, 1 a 3 s atrás, voltava adiantado. Pular para um ponto guardado só procura no cache; fora dele, o anfitrião abre
/// uma "época" nova (<see cref="Epoca"/>) e o fluxo é reaberto no mpv (loadfile replace, 0,5 a 1,5 s no spike C1).
/// </para>
/// Teclas: espaço pausa, ← e → pulam 10 s, ↑ e ↓ o volume, M muda, L abre a fila, F ou F11 tela cheia, Esc sai da tela cheia, S captura a tela, I mostra as estatísticas.
/// </summary>
public sealed class StreamTela : Form
{
    readonly string _url;
    double _duracao;
    string _titulo;
    readonly string _anfitriao;
    /// <summary>O meu apelido na lista "Na sessão" do player.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string MeuNome { get; init; } = "Você";
    /// <summary>O id da minha conta: é por ele que acho "Você" na lista do anfitrião (o nome pode repetir).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string MeuId { get; init; } = "";
    readonly Func<long?> _relogio;
    readonly Font _bodyFont = new(UiTypography.Family, 9.5f);
    readonly Font _captionFont = new(UiTypography.Family, 8.5f);
    readonly Panel _tela = new() { Dock = DockStyle.Fill, BackColor = Theme.VideoMargin };
    readonly Panel _barra = new() { Dock = DockStyle.Bottom, Height = 52, BackColor = Theme.SurfaceRaised };
    readonly FlowLayoutPanel _fallbackFlow = new() { WrapContents = true };
    readonly Button _tocar = Botao("▶"), _voltar = Botao("−10 s"), _avancar = Botao("+10 s"), _mudo = Botao("🔊"), _cheia = Botao("⛶");
    readonly TrackBar _linha = new() { Minimum = 0, TickStyle = TickStyle.None, SmallChange = 5, LargeChange = 30, AutoSize = false, Height = 30 };
    readonly TrackBar _volume = new() { Minimum = 0, Maximum = 100, Value = 80, TickStyle = TickStyle.None, AutoSize = false, Height = 30, Width = 90 };
    readonly Label _tempo = new() { AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(6, 0, 6, 0) };
    readonly Label _info = new() { AutoSize = true, ForeColor = Theme.TextMuted };
    readonly System.Windows.Forms.Timer _relogioTela = new() { Interval = 200 };
    Mpv? _mpv;
    bool _tocando, _arrastando, _cheiaLigada;
    bool _podeControlar, _atualizando, _encerrada;
    string? _aviso;
    DateTime _avisoAte;
    FormWindowState _estadoAntes;
    FormBorderStyle _bordaAntes;

    /// <summary>Pedido para o anfitrião: "tocar", "pausar" ou "ir" (com a posição em s). Quem não tem permissão recebe o motivo por <see cref="Aviso"/>.</summary>
    public event Action<string, double?>? Comando;
    /// <summary>A janela foi fechada (o MainForm encerra a ligação com o anfitrião).</summary>
    public event Action? Fechou;
    /// <summary>Depois de um pulo (D4), o ponto novo já está guardado e o mpv está nele: (seq do estado, id do filme), para o anfitrião não seguir sem esta pessoa.</summary>
    public event Action<int, string>? Carregado;
    /// <summary>O ponto do filme não está no que o mpv guardou (e não vai chegar): o anfitrião abre uma época nova nele.</summary>
    public event Action? Recarregar;
    public event Action? TelaCheiaPedida;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool EmTelaCheia { get => _cheiaLigada; set => _cheiaLigada = value; }

    /// <param name="relogio">Hora do anfitrião em µs (nulo enquanto o relógio comum não foi medido).</param>
    public StreamTela(string titulo, string url, double duracaoS, string perfil, Func<long?> relogio, string anfitriao = "o anfitrião")
    {
        _url = url; _duracao = duracaoS; _titulo = titulo; _relogio = relogio; _anfitriao = anfitriao;
        Text = $"Streemu — {titulo}"; BackColor = Theme.VideoMargin; KeyPreview = true;
        ClientSize = new Size(1100, 660); StartPosition = FormStartPosition.CenterScreen;
        _linha.Maximum = Math.Max(1, (int)duracaoS);
        _tempo.Font = _bodyFont; _info.Font = _captionFont;
        _info.Text = $"Com {anfitriao}";
        _fallbackFlow.Controls.AddRange(new Control[] { _tocar, _voltar, _avancar, _tempo, _mudo, _volume, _cheia });
        foreach (var button in new[] { _tocar, _voltar, _avancar, _mudo, _cheia }) { button.Font = _bodyFont; Theme.StyleButton(button, false); }
        _barra.Controls.AddRange(new Control[] { _linha, _fallbackFlow, _info });
        _info.TextChanged += (_, _) => PerformLayout();
        _tempo.TextChanged += (_, _) => PerformLayout();
        // a fila fica entre o vídeo e a barra: encolhe o vídeo só enquanto está aberta (quem assiste a pediu)
        _filaView.Fechar += () => AlternarFila(false);
        Controls.Add(_tela); Controls.Add(_filaView); Controls.Add(_barra);
        _barra.Visible = !MpvUi.Ligada;
        Ligar();
        AtualizarPermissoes();
    }

    bool _layoutFallback;
    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (!_layoutFallback && _fallbackFlow != null && !MpvUi.Ligada && !_encerrada)
        {
            _layoutFallback = true;
            try { LayoutFallback(); }
            finally { _layoutFallback = false; }
        }
        base.OnLayout(levent);
    }

    void LayoutFallback()
    {
        int pad = UiScale.Dp(this, 8), width = Math.Max(1, ClientSize.Width - pad * 2);
        _linha.SetBounds(pad, pad, width, UiScale.Dp(this, 30));
        _fallbackFlow.Width = width;
        _fallbackFlow.SetBounds(pad, _linha.Bottom + pad, width, _fallbackFlow.GetPreferredSize(new Size(width, 0)).Height);
        _info.MaximumSize = new Size(width, 0);
        _info.Location = new Point(pad, _fallbackFlow.Bottom + pad);
        _barra.Height = _info.Bottom + pad;
    }

    static Button Botao(string t) => new() { Text = t, AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Theme.Text, BackColor = Theme.Surface, Margin = new Padding(0, 0, 6, 0), TabStop = false };

    void Ligar()
    {
        _tocar.Click += (_, _) => PedirTocarOuPausar();
        _voltar.Click += (_, _) => Pedir("ir", Math.Max(0, Posicao() - 10));
        _avancar.Click += (_, _) => Pedir("ir", Posicao() + 10);
        _mudo.Click += (_, _) => AlternarMudo();
        _cheia.Click += (_, _) => AlternarTelaCheia();
        _volume.ValueChanged += (_, _) => _ = _mpv?.Set("volume", _volume.Value);
        _linha.MouseDown += (_, _) => _arrastando = true;
        _linha.MouseUp += (_, _) => { _arrastando = false; Pedir("ir", _linha.Value); };
        _relogioTela.Tick += async (_, _) => await AtualizarTempoAsync();
        KeyDown += OnTecla;
        FormClosing += (_, e) => MotivoFechou ??= e.CloseReason == CloseReason.UserClosing ? "a pessoa fechou a janela" : $"fechada pelo app ({e.CloseReason})";
        FormClosed += (_, _) => EncerrarPlayer();
    }

    void EncerrarPlayer()
    {
        if (_encerrada) return;
        _encerrada = true;
        MotivoFechou ??= "a apresentação foi descartada";
        _relogioTela.Stop();
        Interlocked.Increment(ref _gen); // para a correção
        FecharEpoca();
        if (_contada) Interlocked.Decrement(ref _abertas);
        Log.Info($"[filme] a janela do filme pela transmissão fechou: {MotivoFechou}; {Math.Round(_aberta.Elapsed.TotalMinutes, 1)} min, {EpocasRecebidas} época(s), "
            + $"{EpocasSemTocar} sem tocar, {Travadas} travada(s) por falta de dados, {QuadrosPerdidos} quadro(s) perdido(s), erro p95 {ErroP95Ms:0} ms.");
        var mpv = _mpv; _mpv = null;
        if (mpv != null) _ = Task.Run(mpv.Dispose); // o encerramento do processo pode esperar 1,5 s; não prender a navegação
        Fechou?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { EncerrarPlayer(); _relogioTela.Dispose(); }
        base.Dispose(disposing);
        if (disposing) { _bodyFont.Dispose(); _captionFont.Dispose(); }
    }

    public void FocusVideo()
    {
        Control root = this;
        while (root.Parent != null) root = root.Parent;
        if (Visible && !_encerrada && root.ContainsFocus) MpvUi.Focar(_tela.Handle);
    }

    // ------------------------------------------------------------------ telemetria (spec 009): o que quem assiste viu, sem repetir o teste com ninguém
    // Casos de 11/11: player parado e sem vídeo, época que não tocou (o ffmpeg do SRT do anfitrião morreu ao abrir e o mpv fez um único loadfile), dois players abertos.

    /// <summary>Quantas janelas do filme pela transmissão estão abertas neste app: mais de uma é o caso dos dois players para a mesma pessoa.</summary>
    static int _abertas;
    public static int Abertas => Volatile.Read(ref _abertas);
    bool _contada;
    readonly System.Diagnostics.Stopwatch _aberta = System.Diagnostics.Stopwatch.StartNew();
    readonly System.Diagnostics.Stopwatch _epocaRelogio = new();
    int _epocaNumero = -1, _ticks;
    bool _epocaAberta, _epocaTocou, _epocaAvisou, _paradoAntes;
    volatile bool _epocaCarregada;
    double _dropsBase, _dropsUltimo;
    /// <summary>Sem imagem nesse tempo depois do loadfile, o fluxo é aberto de novo (uma época normal mostra o primeiro quadro em 0,5 a 1,5 s; spike C1).</summary>
    const int ReabrirMs = 4000;
    readonly System.Diagnostics.Stopwatch _tentativa = new();
    int _reabriuNaEpoca;
    /// <summary>Quantas vezes o fluxo foi aberto de novo porque a época não mostrou imagem (vai no filme_fim).</summary>
    public int Reaberturas { get; private set; }

    /// <summary>Por que a janela fechou (a pessoa, o mpv saiu, o app); vai no log e no filme_fim.</summary>
    public string? MotivoFechou { get; private set; }
    /// <summary>Épocas em que nenhuma imagem chegou até a próxima época ou o fechamento.</summary>
    public int EpocasSemTocar { get; private set; }
    /// <summary>Quantas vezes o mpv parou para esperar dados (paused-for-cache) com o filme tocando.</summary>
    public int Travadas { get; private set; }
    /// <summary>Quadros perdidos no mpv somados entre as épocas (o loadfile zera o contador dele).</summary>
    public long QuadrosPerdidos => (long)(_dropsBase + _dropsUltimo);
    /// <summary>Segundos de vídeo guardados à frente no mpv (demuxer-cache-duration) e se ele está parado esperando dados, lidos uma vez por segundo.</summary>
    public double CacheS { get; private set; } = double.NaN;
    public bool ParadoCache { get; private set; }
    public int EpocaAtual => _epocaNumero;
    /// <summary>Tempo até o primeiro quadro de cada época (ms).</summary>
    public List<long> PrimeiroQuadroMs { get; } = new();
    /// <summary>Quanto o mpv está fora da linha do tempo do anfitrião agora (ms; NaN sem leitura), para o filme.csv.</summary>
    public double UltimoErroMs { get; private set; } = double.NaN;
    readonly List<double> _erros = new();
    int _pulosCorrecao;
    /// <summary>O p95 do erro contra a linha do tempo depois de alinhar (ms; -1 sem amostra). A meta da spec 011 é 100 ms.</summary>
    public double ErroP95Ms { get { lock (_erros) { if (_erros.Count == 0) return -1; var e = _erros.OrderBy(v => v).ToArray(); return Math.Round(e[Math.Min(e.Length - 1, (int)(e.Length * 0.95))], 1); } } }
    public int AmostrasErro { get { lock (_erros) return _erros.Count; } }
    /// <summary>Velocidade que a correção aplicou no mpv agora (1 = normal).</summary>
    public double Velocidade => _vel;

    /// <summary>A época anterior acabou (veio outra ou a janela fechou): se nada apareceu, conta como época que não tocou.</summary>
    void FecharEpoca()
    {
        if (_epocaAberta && !_epocaTocou && _epocaNumero >= 0)
        {
            EpocasSemTocar++;
            Log.Info($"[filme] [aviso] a época {_epocaNumero} terminou sem nenhuma imagem do anfitrião ({_epocaRelogio.ElapsedMilliseconds} ms esperando).");
        }
        _epocaAberta = false;
    }

    /// <summary>Uma vez por segundo (no relógio da tela): cache, espera por dados e quadros perdidos. Só leituras do mpv pelo pipe, nada pesado.</summary>
    async Task AmostrarAsync(Mpv mpv)
    {
        CacheS = await mpv.NumAsync("demuxer-cache-duration");
        bool parado = await mpv.BoolAsync("paused-for-cache");
        if (parado && !_paradoAntes && _tocando) { Travadas++; Log.Info($"[filme] o player parou esperando dados do anfitrião (época {_epocaNumero}, cache {CacheS:0.#} s)."); }
        ParadoCache = _paradoAntes = parado;
        double d = await mpv.NumAsync("frame-drop-count");
        if (!double.IsNaN(d)) { if (d < _dropsUltimo) _dropsBase += _dropsUltimo; _dropsUltimo = d; }
        _faixas = await FaixasAsync(mpv);
    }

    /// <summary>Resumo para o evento filme_fim de quem assiste.</summary>
    public JsonObject Resumo() => new()
    {
        ["minutos"] = Math.Round(_aberta.Elapsed.TotalMinutes, 1), ["epocas"] = EpocasRecebidas, ["epocas_sem_tocar"] = EpocasSemTocar,
        ["primeiro_quadro_ms"] = new JsonArray(PrimeiroQuadroMs.Select(v => (JsonNode?)v).ToArray()),
        ["travadas"] = Travadas, ["quadros_perdidos"] = QuadrosPerdidos, ["motivo_fechou"] = MotivoFechou, ["reaberturas"] = Reaberturas,
        ["erro_p95_ms"] = ErroP95Ms, ["amostras_erro"] = AmostrasErro, ["pulos_correcao"] = _pulosCorrecao,
    };

    /// <summary>Abre o mpv dentro da janela, já tentando o fluxo (o anfitrião abre o SRT quando a época dele começa).</summary>
    public async Task AbrirAsync(string mpvExe)
    {
        // sonda curta: por padrão o mpv lê 5 MB antes de tocar, e a 6 Mbps em tempo real isso são ~6,7 s a cada época (medido no teste local, 2026-11-11).
        // Buffer: --rebase-start-time=no deixa o time-pos ser o tempo do filme (o anfitrião manda o carimbo dele); --demuxer-seekable-cache=yes deixa procurar no que já
        // chegou num fluxo que não dá para procurar; 256 MiB à frente e 192 MiB atrás cabem os 30 s adiantados e ~45 s de volta até ~34 Mbps (cópia direta de um 4K).
        // Na memória, não no disco: some quando a janela fecha, sem arquivo para limpar.
        var aberto = await Mpv.OpenAsync(mpvExe, _url, _tela.Handle, _volume.Value, new[]
        {
            // Só o MPEG-TS do anfitrião, pelo SRT: --demuxer=lavf impede o mpv de adivinhar outro formato (sem ele um .mkv passava pelo
            // demuxer próprio do mpv); format_whitelist e protocol_whitelist recusam o resto. Sem codec_whitelist: o anfitrião copia o codec do filme.
            "--demuxer=lavf", "--demuxer-lavf-format=mpegts", "--demuxer-lavf-o-append=format_whitelist=mpegts", "--stream-lavf-o-append=protocol_whitelist=srt",
            "--demuxer-lavf-probesize=250000", "--demuxer-lavf-analyzeduration=0.3", "--cache=yes", "--rebase-start-time=no", "--demuxer-seekable-cache=yes",
            "--demuxer-max-bytes=256MiB", "--demuxer-max-back-bytes=192MiB", "--cache-pause-wait=0.5",
        });
        if (_encerrada) { _ = Task.Run(aberto.Dispose); return; }
        _mpv = aberto;
        _mpv.Saiu += () => { try { BeginInvoke(new Action(() => { if (_encerrada) return; MotivoFechou ??= "o mpv saiu sozinho"; Close(); })); } catch (InvalidOperationException) { } };
        if (MpvUi.Ligada)
        {
            // interface dentro do mpv (o padrão): ela tem todos os botões desta barra (volume e mudo ela muda direto no mpv)
            _barra.Visible = false;
            _mpv.Mensagem += a => { try { BeginInvoke(new Action(() => AcaoUi(a))); } catch (InvalidOperationException) { } };
        }
        _contada = true;
        int abertas = Interlocked.Increment(ref _abertas);
        if (abertas > 1) Log.Info($"[filme] [aviso] {abertas} players do filme pela transmissão abertos ao mesmo tempo neste PC.");
        _relogioTela.Start(); // sincronização/amostras continuam ao navegar; apresentação é suspensa separadamente
        FocusVideo();
    }

    // ------------------------------------------------------------------ épocas (o fluxo reaberto) e a linha do tempo do anfitrião

    /// <summary>O anfitrião abriu o envio num ponto novo (época): o mpv abre o fluxo de novo, pausado, e a linha do tempo o põe no lugar quando os dados chegam.</summary>
    public async void Epoca(int numero, double pos)
    {
        if (_mpv is not { Exited: false } mpv || numero <= _epocaNumero) return;
        FecharEpoca();
        EpocasRecebidas++;
        _epocaNumero = numero; _epocaAberta = true; _epocaTocou = _epocaAvisou = _epocaCarregada = false; _epocaRelogio.Restart();
        _reabriuNaEpoca = 0; _tentativa.Restart(); _mpvTempo = null; _faixas = new();
        Log.Info($"[filme] época {numero} recebida: o anfitrião manda a partir de {pos:0.#} s.");
        try
        {
            await mpv.Set("pause", true);
            await mpv.Cmd("loadfile", _url, "replace");
            if (_epocaNumero == numero) _epocaCarregada = true;
        }
        catch (Exception e) { Log.Info($"[filme] não deu para abrir a época {numero} no mpv: {e.Message}"); }
        Aplicar();
    }

    // a linha do tempo da Sessão do anfitrião (filme_estado): tocando ou não, da posição P a partir do instante T (µs no relógio dele)
    int _seq = -1;
    volatile bool _tlTocando;
    double _tlPos;
    long _tlT;
    bool _tlPulo;
    string _filme = "";
    int _gen;
    double _vel = 1;
    bool _alinhado;

    /// <summary>O estado da Sessão do anfitrião (o filme_estado que vai para todos): pausa, volta e pula na hora marcada, dentro do que o mpv guardou.</summary>
    public void Estado(JsonObject m)
    {
        int seq = m["seq"]?.GetValue<int>() ?? 0;
        // o mesmo seq é a repetição periódica do anfitrião (Sessao.ReenviarEstado): não reaplica nada. Um seq maior depois de uma pausa longa é o comando que se perdeu.
        if (_anfitriaoCaiu || !EstadoDoFilme.EhNovo(_seq, seq)) return;
        _seq = seq;
        Permissoes(m);
        _filme = m["filme"]?.GetValue<string>() ?? _filme;
        _tlPos = m["pos"]?.GetValue<double>() ?? 0; _tlT = m["t"]?.GetValue<long>() ?? 0; _tlPulo = m["pulo"]?.GetValue<bool>() ?? false;
        _tlTocando = _tocando = m["tocando"]?.GetValue<bool>() ?? false;
        Log.Info($"[filme] linha do tempo do anfitrião: {(_tocando ? "tocando" : "pausado")} em {_tlPos:0.#} s{(_tlPulo ? " (pulo)" : "")}.");
        AtualizarBotao();
        Aplicar();
    }

    void Aplicar() { int gen = Interlocked.Increment(ref _gen); _ = Task.Run(() => AplicarAsync(gen)); }

    /// <summary>Onde o filme do grupo está no instante <paramref name="agoraUs"/> do relógio do anfitrião.</summary>
    double Alvo(long agoraUs) => _tlTocando ? _tlPos + Math.Max(0, agoraUs - _tlT) / 1e6 : _tlPos;

    /// <summary>A mesma ideia da <see cref="Sessao"/> (hora marcada e correção 7.4), com o cache do mpv no lugar do arquivo: só procura onde há dados guardados.</summary>
    async Task AplicarAsync(int gen)
    {
        try
        {
            if (_mpv is not { } mpv || _seq < 0) return;
            long now;
            while (_relogio() == null) { if (gen != _gen) return; await Task.Delay(100); }
            now = _relogio() ?? 0;
            await mpv.Set("speed", 1.0); _vel = 1;
            int seq = _seq;
            if (!_tlTocando)
            {
                if (_tlT > now) await EsperarAte(_tlT);
                if (gen != _gen) return;
                await mpv.Set("pause", true);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Lugar lugar;
                while ((lugar = await PosicionarAsync(mpv, _tlPos, gen)) != Lugar.Pronto && gen == _gen && sw.ElapsedMilliseconds < 8000)
                {
                    if (lugar == Lugar.Antes && sw.ElapsedMilliseconds > 2000) PedirRecarregar("o ponto da pausa ficou antes do que chegou");
                    await Task.Delay(150);
                }
                if (gen == _gen && lugar == Lugar.Pronto) _falhasAplicar = 0;
                if (gen == _gen && _tlPulo && lugar == Lugar.Pronto) Carregado?.Invoke(seq, _filme);
                return;
            }
            // tocando: espera os dados do ponto (época nova, ou o pulo foi além do que chegou) e começa na hora marcada, como quem tem o arquivo
            long inicio;
            await mpv.Set("pause", true);
            var espera = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                if (gen != _gen) return;
                now = _relogio() ?? now;
                inicio = _tlT - now >= 150_000 ? _tlT : now + 400_000;
                var r = await PosicionarAsync(mpv, Alvo(inicio), gen);
                if (r == Lugar.Pronto) break;
                // 2 s de folga: num pulo para fora do que chegou, a época nova do anfitrião já vem (ele junta os pulos de uma rajada por até ~1 s)
                if ((r == Lugar.Antes && espera.ElapsedMilliseconds > 2000) || espera.ElapsedMilliseconds > 12000)
                    PedirRecarregar(r == Lugar.Antes ? "o ponto ficou antes do que chegou" : "os dados do ponto não chegaram em 12 s");
                await Task.Delay(150);
            }
            await EsperarAte(inicio);
            if (gen != _gen) return;
            await mpv.Set("pause", false);
            _falhasAplicar = 0;
            _alinhado = false;
            await CorrigirAsync(gen, mpv);
        }
        catch (Exception e)
        {
            Log.Info($"[filme] falha ao seguir a linha do tempo do anfitrião: {e.Message}");
            if (++_falhasAplicar >= 2 && gen == _gen)
            {
                // roda numa thread de fundo: o texto da janela só se muda na da janela
                try { BeginInvoke(new Action(() => Aviso("Não estou conseguindo seguir o filme do anfitrião (o player não responde). Se continuar assim, feche e entre de novo."))); }
                catch (InvalidOperationException) { }
            }
        }
    }

    /// <summary>Falhas seguidas ao seguir a linha do tempo do anfitrião (zera quando o mpv chega a tocar na hora marcada ou a parar no ponto da pausa).</summary>
    int _falhasAplicar;

    enum Lugar { Pronto, Esperar, Antes }

    /// <summary>
    /// Põe o mpv em <paramref name="alvo"/> se esse ponto está guardado. O mpv só procura entre quadros-chave guardados (no teste de 11/11, procurar fora disso era
    /// ignorado e o vídeo seguia de onde estava): fora, <see cref="Lugar.Esperar"/> (ainda vai chegar) ou <see cref="Lugar.Antes"/> (ficou para trás do que chegou).
    /// </summary>
    /// <remarks>
    /// Só diz <see cref="Lugar.Pronto"/> depois de conferir o time-pos no fim da procura, e só procura pela geração atual (<paramref name="gen"/>). Teste de 11/11
    /// (filme-stream, cópia direta): o anfitrião pausou em 47 s e quem assistia ficou parado em 23,3 s. A espera da procura acabava em 3 s com o mpv ainda procurando
    /// e o ponto era dado por pronto sem olhar onde o mpv estava; e uma geração antiga (do estado anterior) podia mandar a procura dela DEPOIS da nova.
    /// </remarks>
    async Task<Lugar> PosicionarAsync(Mpv mpv, double alvo, int gen)
    {
        // uma procura por vez: a de uma geração antiga não pode chegar ao mpv depois da atual
        await _procura.WaitAsync();
        try
        {
            if (gen != _gen || !_epocaCarregada) return Lugar.Esperar;
            double cur = await mpv.NumAsync("time-pos");
            if (!double.IsNaN(cur) && Math.Abs(cur - alvo) < 0.06) return Lugar.Pronto;
            var faixas = _faixas = await FaixasAsync(mpv);
            foreach (var (a, b) in faixas)
                if (alvo >= a - 0.05 && alvo <= b)
                {
                    if (gen != _gen) return Lugar.Esperar;
                    await mpv.Seek(alvo);
                    bool terminou = await mpv.WaitSeekAsync(3000, () => gen != _gen);
                    if (!terminou || gen != _gen) return Lugar.Esperar;
                    double depois = await mpv.NumAsync("time-pos");
                    if (!double.IsNaN(depois) && Math.Abs(depois - alvo) < 0.3) return Lugar.Pronto;
                    Log.Info($"[filme] procurei {alvo:0.##} s no que chegou, mas o mpv ficou em {(double.IsNaN(depois) ? "?" : depois.ToString("0.##"))} s; tento de novo.");
                    return Lugar.Esperar;
                }
            // perto o bastante: a correção (velocidade) acerta sem procurar
            if (!double.IsNaN(cur) && Math.Abs(cur - alvo) < 0.3) return Lugar.Pronto;
            double comeco = faixas.Count > 0 ? faixas.Min(f => f.A) : cur;
            if (!double.IsNaN(comeco) && alvo < comeco - 0.5 && _epocaRelogio.ElapsedMilliseconds > 2500) return Lugar.Antes;
            return Lugar.Esperar;
        }
        finally { _procura.Release(); }
    }

    readonly SemaphoreSlim _procura = new(1, 1);

    List<(double A, double B)> _faixas = new();

    /// <summary>O que o mpv tem guardado e dá para procurar (demuxer-cache-state/seekable-ranges), em s do filme.</summary>
    static async Task<List<(double A, double B)>> FaixasAsync(Mpv mpv)
    {
        var l = new List<(double, double)>();
        string? json = await mpv.TextoAsync("demuxer-cache-state");
        if (json == null) return l;
        try
        {
            using var d = JsonDocument.Parse(json);
            if (d.RootElement.TryGetProperty("seekable-ranges", out var r) && r.ValueKind == JsonValueKind.Array)
                foreach (var f in r.EnumerateArray())
                    if (f.TryGetProperty("start", out var a) && f.TryGetProperty("end", out var b) && a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number)
                        l.Add((a.GetDouble(), b.GetDouble()));
        }
        catch (JsonException) { }
        return l;
    }

    readonly System.Diagnostics.Stopwatch _pediuRecarregar = new();

    void PedirRecarregar(string motivo)
    {
        if (_pediuRecarregar.IsRunning && _pediuRecarregar.ElapsedMilliseconds < 4000) return;
        _pediuRecarregar.Restart();
        Log.Info($"[filme] pedindo ao anfitrião para reabrir o envio no ponto do filme: {motivo} (época {_epocaNumero}).");
        Recarregar?.Invoke();
    }

    /// <summary>Até o instante (µs no relógio do anfitrião): dorme até perto e gira o último milissegundo (como a Sessao).</summary>
    async Task EsperarAte(long alvoUs)
    {
        while (_relogio() is long n && alvoUs - n > 20_000) await Task.Delay((int)Math.Min(200, (alvoUs - n - 15_000) / 1000));
        while (_relogio() is long n && alvoUs - n > 2_000) Thread.Sleep(1);
        while (_relogio() is long n && n < alvoUs) Thread.SpinWait(50);
    }

    /// <summary>
    /// A correção contínua da Sessao (7.4): até 15 ms nada; até 1 s muda a velocidade (±5%); mais de 1 s procura no cache. Sem os dados do ponto, espera (o mpv para
    /// sozinho até chegarem); com o ponto para trás do que chegou, pede uma época nova.
    /// </summary>
    async Task CorrigirAsync(int gen, Mpv mpv)
    {
        await Task.Delay(300);
        while (gen == _gen && _tlTocando && !mpv.Exited)
        {
            long? a0 = _relogio();
            double p = await mpv.NumAsync("audio-pts");
            long? a1 = _relogio();
            if (double.IsNaN(p)) p = await mpv.NumAsync("time-pos");
            if (gen != _gen) break;
            if (!double.IsNaN(p) && a0 is long x && a1 is long y)
            {
                double esperado = Alvo((x + y) / 2);
                double err = p - esperado;
                UltimoErroMs = err * 1000;
                if (Math.Abs(err) < 0.015) _alinhado = true;
                if (_alinhado) lock (_erros) { _erros.Add(Math.Abs(err) * 1000); if (_erros.Count > 20000) _erros.RemoveRange(0, 10000); }
                if (Math.Abs(err) > 1)
                {
                    _alinhado = false;
                    if (_vel != 1) { await mpv.Set("speed", 1.0); _vel = 1; }
                    var r = await PosicionarAsync(mpv, esperado + 0.3, gen);
                    if (r == Lugar.Pronto) { _pulosCorrecao++; Log.Info($"[filme] fora da linha do tempo {err * 1000:N0} ms: pulo dentro do que chegou."); }
                    else if (r == Lugar.Antes) PedirRecarregar("ficou para trás do que chegou");
                }
                else
                {
                    double v = Math.Abs(err) > 0.015 ? Math.Clamp(1 - err / 2, 0.95, 1.05) : Math.Abs(err) < 0.005 ? 1 : _vel;
                    if (Math.Abs(v - _vel) >= 0.001) { _vel = v; await mpv.Set("speed", v); }
                }
            }
            await Task.Delay(250);
        }
    }

    // ------------------------------------------------------------------ fila de episódios e lista "Na sessão" (mensagens novas do anfitrião; app antigo não as manda)

    readonly Ui.FilaFilmeView _filaView = new() { Dock = DockStyle.Right, Width = 300, Visible = false };
    string[] _filaNomes = [];
    int _filaAtual = -1;
    bool _filaAberta;
    List<MpvUi.Pessoa>? _pessoasHost;
    int _errosNaTroca;
    /// <summary>Quantas vezes o anfitrião passou para o episódio seguinte com esta janela aberta (a janela é a mesma; o teste confere).</summary>
    public int Trocas { get; private set; }
    public string TituloAtual => _titulo;
    public string FilmeAtual => _filme;
    public IReadOnlyList<string> FilaNomes => _filaNomes;
    /// <summary>Os nomes da lista "Na sessão" que o player de quem assiste mostra (teste); sem a lista do anfitrião, só ele e eu.</summary>
    public IReadOnlyList<string> PessoasVistas => _pessoasHost is { Count: > 0 } l ? l.Where(p => !p.Sou(MeuId, MeuNome)).Select(p => p.Nome).Append(MeuNome).ToList() : [_anfitriao, MeuNome];

    /// <summary>
    /// Fila: o anfitrião passou para o próximo arquivo (filme_trocar, vem ANTES da época nova). A janela, o mpv e a ligação são os mesmos: só o título, a duração, o id do
    /// filme e a linha do tempo recomeçam (o seq do estado volta a 0 na Sessão nova do anfitrião, e o filme_estado dele seria descartado como velho).
    /// O ponto do mpv fica "não carregado" até a época nova chegar: o fluxo velho ainda guarda o começo do filme anterior no cache, e achá-lo ali daria o episódio novo por
    /// pronto antes da hora.
    /// </summary>
    public void Trocar(JsonObject m)
    {
        if (_encerrada) return;
        string? para = m["para"]?.GetValue<string>();
        if (para == null) return; // sem comparar com o id de agora: dois arquivos iguais na fila têm o mesmo id e a troca vale do mesmo jeito (o Tunnel já descarta mensagem repetida)
        _titulo = m["nome"]?.GetValue<string>() is { Length: > 0 } nome ? nome : _titulo;
        Text = $"Streemu — {_titulo}";
        double dur = m["duracao"]?.GetValue<double>() ?? 0;
        if (dur > 0) { _duracao = dur; _linha.Maximum = Math.Max(1, (int)dur); }
        _seq = -1; _filme = para;
        _tlTocando = _tocando = false; _tlPos = 0; _tlT = 0; _tlPulo = false;
        _epocaCarregada = false; _mpvTempo = null; _alinhado = false; UltimoErroMs = double.NaN;
        Interlocked.Increment(ref _gen); // para a correção do episódio velho
        lock (_erros) _errosNaTroca = _erros.Count;
        Trocas++;
        AtualizarBotao();
        Log.Info($"[filme] o anfitrião passou para o próximo da fila ({m["indice"]} de {m["total"]}); a janela continua a mesma.");
    }

    /// <summary>Erro p95 contra a linha do tempo só depois da última troca de episódio (-1 sem amostra), para o teste da fila.</summary>
    public double ErroP95PosTrocaMs { get { lock (_erros) { var e = _erros.Skip(Math.Min(_errosNaTroca, _erros.Count)).OrderBy(v => v).ToArray(); return e.Length == 0 ? -1 : Math.Round(e[Math.Min(e.Length - 1, (int)(e.Length * 0.95))], 1); } } }
    public int AmostrasPosTroca { get { lock (_erros) return Math.Max(0, _erros.Count - _errosNaTroca); } }

    /// <summary>A fila do anfitrião (filme_fila): só leitura aqui.</summary>
    public void Fila(JsonObject m)
    {
        var itens = m["itens"] is JsonArray a ? a.Select(x => x?.GetValue<string>() ?? "").ToArray() : [];
        _filaNomes = itens; _filaAtual = m["atual"]?.GetValue<int>() ?? -1;
        _filaView.Mostrar(_filaNomes, _filaAtual, false, _anfitriao);
        if (_filaNomes.Length == 0 && _filaAberta) AlternarFila(false);
    }

    /// <summary>A lista "Na sessão" inteira, como o anfitrião a vê (filme_pessoas): quem tem o arquivo e quem assiste pela transmissão.</summary>
    public void Pessoas(JsonObject m)
    {
        if (m["pessoas"] is not JsonArray a) return;
        var lista = new List<MpvUi.Pessoa>();
        foreach (var x in a)
            if (x is JsonObject o && o["nome"]?.GetValue<string>() is { Length: > 0 } nome)
                lista.Add(new MpvUi.Pessoa(nome, o["papel"]?.GetValue<string>(), o["estado"]?.GetValue<string>(), o["tom"]?.GetValue<string>() ?? "", Id: o["id"]?.GetValue<string>() ?? ""));
        _pessoasHost = lista;
    }

    void AlternarFila(bool? abrir = null)
    {
        bool quer = abrir ?? !_filaAberta;
        if (quer && _filaNomes.Length == 0) { Aviso("Não há fila nesta sessão."); return; }
        _filaAberta = quer;
        _filaView.Width = UiScale.Dp(this, 300);
        _filaView.Visible = quer;
    }

    public void Aviso(string texto) { _aviso = texto; _avisoAte = DateTime.Now.AddSeconds(6); _info.Text = texto; }

    /// <summary>Aviso que fica na tela até alguém tirá-lo (o <see cref="Aviso"/> some em 6 s): o sinal do anfitrião (MainForm.FilmeSinal.cs). Nulo tira.</summary>
    public void AvisoFixo(string? texto)
    {
        _avisoFixo = texto;
        if (texto != null) _info.Text = texto;
    }
    string? _avisoFixo;
    bool _anfitriaoCaiu;

    /// <summary>
    /// O anfitrião ficou 30 s sem sinal e a ligação foi encerrada: o vídeo para aqui, o aviso fica e a janela continua aberta para a pessoa ler e sair
    /// (o mpv segue aberto por causa do --keep-open, então o fim do fluxo não a fecha).
    /// </summary>
    public void AnfitriaoCaiu(string texto)
    {
        if (_encerrada) return;
        _anfitriaoCaiu = true;
        MotivoFechou ??= "o anfitrião caiu";
        Interlocked.Increment(ref _gen); // para a correção e qualquer aplicação em curso
        _tlTocando = _tocando = false; _podeControlar = false;
        AtualizarPermissoes(); AtualizarBotao();
        AvisoFixo(texto);
        if (_mpv is { } mpv) _ = Task.Run(async () => { await mpv.Set("pause", true); await mpv.Set("speed", 1.0); });
    }

    public void Permissoes(JsonObject m)
    {
        _podeControlar = m["todos"]?.GetValue<bool>() == true;
        AtualizarPermissoes();
    }

    void AtualizarPermissoes() => _tocar.Enabled = _voltar.Enabled = _avancar.Enabled = _linha.Enabled = _podeControlar;

    void Pedir(string comando, double? pos = null)
    {
        if (_encerrada) return;
        if (!_podeControlar) { Aviso($"Só {_anfitriao} controla o filme agora."); return; }
        Comando?.Invoke(comando, pos);
    }

    /// <summary>O app fecha a janela (o anfitrião fechou o filme, a ligação caiu): o motivo vai no log e no filme_fim.</summary>
    public void FecharPor(string motivo)
    {
        MotivoFechou ??= motivo;
        if (!IsDisposed) Close();
    }

    /// <summary>Teste automático: a janela abre fora da tela e sem som (regra 2 do AGENTS.md), e o teste lê a posição do mpv e faz os pedidos de quem assiste.</summary>
    public void ModoTeste() { StartPosition = FormStartPosition.Manual; Location = new Point(-32000, -32000); ShowInTaskbar = false; _volume.Value = 0; }
    public double? PosicaoMpv => _mpvTempo;
    public bool Tocando => _tocando;
    public int EpocasRecebidas { get; private set; }
    public void PedirParaTeste(string cmd, double? pos) => Comando?.Invoke(cmd, pos);

    /// <summary>Onde o filme está para quem assiste: o time-pos do mpv (o carimbo é o tempo do filme); sem imagem ainda, o ponto da linha do tempo do anfitrião.</summary>
    public double PosicaoFilme => Posicao();
    double Posicao() => _mpvTempo ?? (_relogio() is long n ? Alvo(n) : _tlPos);
    double? _mpvTempo;

    async Task AtualizarTempoAsync()
    {
        if (_atualizando || _encerrada || _mpv is not { Exited: false } mpv) return;
        _atualizando = true;
        try { await AtualizarTempoAsync(mpv); }
        finally { _atualizando = false; }
    }

    async Task AtualizarTempoAsync(Mpv mpv)
    {
        // o mpv devolve NaN até a primeira imagem chegar; antes do loadfile voltar, o time-pos ainda é o do fluxo da época anterior
        try { double t = _epocaCarregada ? await mpv.NumAsync("time-pos") : double.NaN; _mpvTempo = double.IsNaN(t) || double.IsInfinity(t) ? null : t; } catch { _mpvTempo = null; }
        if (_epocaAberta && !_epocaTocou && _epocaCarregada && _mpvTempo != null)
        {
            _epocaTocou = true; PrimeiroQuadroMs.Add(_epocaRelogio.ElapsedMilliseconds);
            Log.Info($"[filme] época {_epocaNumero}: primeiro quadro em {_epocaRelogio.ElapsedMilliseconds} ms.");
        }
        else if (_epocaAberta && !_epocaTocou && _epocaCarregada && _tentativa.ElapsedMilliseconds > ReabrirMs && _reabriuNaEpoca < 3)
        {
            // caso de 11/11: o ffmpeg do SRT do anfitrião morria ao abrir, o laço dele abria outro listener 100 ms depois, mas o mpv tinha feito um único loadfile e
            // ficava parado até o próximo comando ("pausou e bugou"). Abrir de novo conecta ao listener novo, que começa do último quadro-chave.
            _reabriuNaEpoca++; Reaberturas++; _tentativa.Restart(); _epocaCarregada = false;
            int numero = _epocaNumero;
            Log.Info($"[filme] a época {numero} não mostrou imagem em {_epocaRelogio.ElapsedMilliseconds} ms: abrindo o fluxo de novo (tentativa {_reabriuNaEpoca}).");
            try { await mpv.Set("pause", true); await mpv.Cmd("loadfile", _url, "replace"); } catch { }
            if (_epocaNumero == numero) _epocaCarregada = true;
            Aplicar();
        }
        else if (_epocaAberta && !_epocaTocou && !_epocaAvisou && _epocaRelogio.ElapsedMilliseconds > 5000)
        {
            _epocaAvisou = true;
            Log.Info($"[filme] [aviso] a época {_epocaNumero} não mostrou imagem em 5 s: nada chegou do anfitrião (cache {CacheS:0.#} s). "
                + "Provável: o envio dele não abriu para esta época; o log dele mostra o motivo.");
        }
        if (++_ticks % 5 == 0) { try { await AmostrarAsync(mpv); } catch { } }
        if (_encerrada) return;
        double p = Posicao();
        if (!_arrastando) _linha.Value = Math.Clamp((int)p, 0, _linha.Maximum);
        _tempo.Text = $"{Sessao.Tempo(p)} / {Sessao.Tempo(_duracao)}";
        if (!MpvUi.Ligada)
            _info.Text = _avisoFixo ?? (_aviso != null && DateTime.Now < _avisoAte ? _aviso : $"Com {_anfitriao} · {(_podeControlar ? "Todos podem controlar" : "Só o anfitrião controla")}");
        // a duração vai do app: o fluxo do anfitrião não diz quanto o filme tem; "buffer" desenha na linha do tempo o que já chegou
        if (MpvUi.Ligada)
        {
            var buffer = new JsonArray(_faixas.Select(f => (JsonNode?)new JsonArray(Math.Round(f.A, 1), Math.Round(f.B, 1))).ToArray());
            var (eu, meuTom) = _mpvTempo == null ? ("Recebendo o filme", "espera") : ParadoCache ? ("Esperando dados", "espera") : _tocando ? ("Assistindo", "ok") : ("Pausado", "");
            var (estado, tom) = _avisoFixo != null ? (_avisoFixo, "aviso") : _aviso != null && DateTime.Now < _avisoAte ? (_aviso, "aviso") : (_podeControlar ? "Todos podem controlar" : $"Só {_anfitriao} controla", "");
            // com a lista do anfitrião (filme_pessoas) todo mundo vê quem está na sessão; eu me marco como "Você" com o meu estado daqui. Sem ela (anfitrião em versão antiga), só ele e eu
            var pessoas = new List<MpvUi.Pessoa>();
            if (_pessoasHost is { Count: > 0 })
            {
                foreach (var x in _pessoasHost) pessoas.Add(x.Sou(MeuId, MeuNome) ? new(MeuNome, "Você", eu, meuTom, Id: MeuId) : x);
                if (!pessoas.Any(x => x.Sou(MeuId, MeuNome))) pessoas.Add(new(MeuNome, "Você", eu, meuTom, Id: MeuId));
            }
            else { pessoas.Add(new(_anfitriao, "Anfitrião", "Transmitindo", "ok")); pessoas.Add(new(MeuNome, "Você", eu, meuTom)); }
            string linha = _filaNomes.Length > 1 && _filaAtual >= 0 ? $"Episódio {_filaAtual + 1} de {_filaNomes.Length} · Com {_anfitriao}" : $"Com {_anfitriao}";
            MpvUi.Mandar(mpv, new MpvUi.EstadoTela(p, _duracao, _tocando, _podeControlar, _titulo, linha, estado, tom, _cheiaLigada, pessoas),
                new JsonObject { ["buffer"] = buffer, ["fila"] = _filaNomes.Length > 0 ? new JsonObject { ["total"] = _filaNomes.Length, ["indice"] = _filaAtual + 1, ["aberta"] = _filaAberta } : null });
            MpvUi.LigarMouse(_tela.Handle);
        }
    }

    /// <summary>Botão da interface do mpv (MpvUi): vira o mesmo pedido ao anfitrião dos botões da barra.</summary>
    void AcaoUi(string[] args)
    {
        if (MpvUi.Ler(args) is not var (acao, v)) return;
        switch (acao)
        {
            case "tocar": PedirTocarOuPausar(); break;
            case "pular" when v is double d: Pedir("ir", Math.Max(0, Posicao() + d)); break;
            case "ir" when v is double p: Pedir("ir", Math.Max(0, p)); break;
            case "tocar-negado": Pedir("pausar"); break;
            case "cheia": AlternarTelaCheia(); break;
            case "fila": AlternarFila(); break;
            case "sair": Close(); break;
        }
    }

    void AtualizarBotao() => _tocar.Text = _tocando ? "⏸" : "▶";

    void PedirTocarOuPausar() => Pedir(_tocando ? "pausar" : "tocar");

    void AlternarMudo() { bool mudo = _mudo.Text == "🔇"; _mudo.Text = mudo ? "🔊" : "🔇"; _ = _mpv?.Set("mute", !mudo); }

    void AlternarTelaCheia()
    {
        if (!TopLevel) { TelaCheiaPedida?.Invoke(); return; }
        _cheiaLigada = !_cheiaLigada;
        if (_cheiaLigada) { _estadoAntes = WindowState; _bordaAntes = FormBorderStyle; FormBorderStyle = FormBorderStyle.None; WindowState = FormWindowState.Maximized; _barra.Visible = false; }
        else { FormBorderStyle = _bordaAntes; WindowState = _estadoAntes; _barra.Visible = !MpvUi.Ligada; }
    }

    void OnTecla(object? s, KeyEventArgs e)
    {
        e.Handled = true;
        switch (e.KeyCode)
        {
            case Keys.Space: PedirTocarOuPausar(); break;
            // 5 s com a interface do mpv (−5/+5, pedido do 2º teste)
            case Keys.Left: Pedir("ir", Math.Max(0, Posicao() - (MpvUi.Ligada ? 5 : 10))); break;
            case Keys.Right: Pedir("ir", Posicao() + (MpvUi.Ligada ? 5 : 10)); break;
            case Keys.Up: _volume.Value = Math.Min(100, _volume.Value + 5); break;
            case Keys.Down: _volume.Value = Math.Max(0, _volume.Value - 5); break;
            case Keys.M: AlternarMudo(); break;
            case Keys.L: AlternarFila(); break; // F já é a tela cheia
            case Keys.F: case Keys.F11: AlternarTelaCheia(); break;
            case Keys.Escape: if (_cheiaLigada) AlternarTelaCheia(); break;
            case Keys.S: _ = _mpv?.Cmd("screenshot"); break;
            case Keys.I: _ = _mpv?.Cmd("script-binding", "stats/display-stats-toggle"); break;
            default: e.Handled = false; break;
        }
    }
}
