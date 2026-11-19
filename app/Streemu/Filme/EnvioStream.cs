using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Streemu.Media;
using Streemu.Net;
using Streemu.Util;

namespace Streemu.Filme;

/// <summary>
/// O anfitrião manda o próprio arquivo como vídeo (spec 011, T2): um ffmpeg lê o arquivo e, para cada espectador, outro ffmpeg copia o mpegts para SRT (listener)
/// ligado ao <see cref="LocalRelay"/> da ligação do filme dele. É o mesmo caminho da live de jogo; muda a fonte e o jeito de repartir.
/// <para>
/// <b>Envio adiantado (buffer de quem assiste, spec 011 6 e 010 5.2).</b> Caso de 11/11 (um amigo assistindo o JoJo de outro): cada pausa parava o ffmpeg e o
/// "continuar" abria uma época nova no ponto do ANFITRIÃO; quem assiste estava 1 a 3 s atrás (o tempo de cada época começar a tocar), então voltava adiantado e perdia esse
    /// trecho a cada pausa. Agora o envio lê o arquivo mais rápido que o filme anda (<see cref="RitmoAgora"/>), até <see cref="AdiantadoS"/> à frente da posição da Sessão, e
/// segura a leitura aí (o ffmpeg fica parado no pipe). O mpv de quem assiste guarda o que chega e segue o relógio comum da Sessão (<see cref="StreamTela"/>): pausar e
/// continuar não mexem no envio, e pular para um ponto que todos já têm não reabre nada.
/// </para>
/// Cada "época" é um ponto de partida do ffmpeg: abre quando alguém começa a assistir, quando a Sessão pula para fora do que já foi mandado (ou para antes do que todos
/// guardam), ao trocar o perfil ou a faixa de áudio. Os espectadores abrem a conexão de novo (spike C1: o mpv recarregado volta a tocar em 0,5 a 1,5 s).
/// Por que não o <c>TsSplitter</c>/<c>TsPipe</c> da live: eles descartam a fila quando uma escrita fica parada mais de 400 ms e esperam o próximo quadro-chave, que num
/// filme vem a cada 2 a 10 s. Aqui o distribuidor guarda o trecho desde o último quadro-chave e cada espectador começa dele (<see cref="MaxGopBytes"/>).
/// Quem manda no tempo é a <see cref="Sessao"/> do anfitrião; este objeto só segue o que ela diz (<see cref="Seguir"/>).
/// Uso: <c>var e = new EnvioStream(info, perfil); e.Adicionar(link); e.Seguir(sessao.Posicao, sessao.Tocando);</c>
/// </summary>
internal sealed partial class EnvioStream : IDisposable
{
    /// <summary>Latência do SRT: o filme não precisa de pouco atraso, mas cada época paga essa latência para voltar a tocar (teste local: 150 ms deu 0,5 s por época, 600 ms deu 1,0 s).</summary>
    public const int LatenciaMs = 150;
    /// <summary>Quanto o envio corre à frente da posição da Sessão: é o que quem assiste tem guardado para pular para frente sem recarregar (e para a rede engasgar sem travar).</summary>
    public const double AdiantadoS = 30;
    /// <summary>
    /// Quanto atrás da posição o mpv de quem assiste ainda tem (o <c>--demuxer-max-back-bytes</c> da <see cref="StreamTela"/> segura ~45 s até ~34 Mbps): pular para trás
    /// dentro disso não recarrega.
    /// </summary>
    public const double AtrasS = 40;
    /// <summary>Folga da leitura logo depois de abrir a época (s de filme mandados de uma vez), como o 0,5 s do -re de antes, um pouco maior para o primeiro quadro chegar logo.</summary>
    const double RajadaS = 1.0;
    const int Ts = 188, VideoPid = 0x100, PatPid = 0, PmtPid = 0x1000; // PIDs padrão do muxer mpegts do ffmpeg (o vídeo é o 1º stream), como no TsPipe
    /// <summary>Teto do trecho guardado para quem conecta depois do início da época (~3,5 s a 6 Mbps); acima disso quem entra começa no próximo quadro-chave.</summary>
    const int MaxGopBytes = 2_500_000;
    const int FilaMaxChunks = 400, Chunk = Ts * 256;

    sealed class Saida
    {
        public required PeerLink Link;
        public volatile bool Removida;
        public Process? Srt;
        public BlockingCollection<byte[]>? Fila;
        /// <summary>Carimbo (s do filme) do primeiro quadro-chave que este espectador recebeu na época: antes disso ele não tem nada guardado. NaN até saber.</summary>
        public double Inicio = double.NaN;
    }

    readonly object _gate = new();
    readonly object _dist = new();
    readonly Dictionary<string, Saida> _saidas = new();
    /// <summary>As saídas com fila aberta; só mexe sob <c>_dist</c> (nunca pega <c>_gate</c> lá dentro: a ordem das travas é sempre _gate e depois _dist).</summary>
    readonly List<Saida> _ativas = new();
    // cache da época (sempre sob _dist): últimas tabelas PAT/PMT e os pacotes desde o último quadro-chave do vídeo
    byte[]? _pat, _pmt;
    readonly List<byte[]> _gop = new();
    int _gopBytes;
    bool _gopValido;
    double _gopPts = double.NaN;
    // até onde a época já mandou (s do filme, pelo PTS do vídeo; sob _dist): o maior carimbo e o do último quadro-chave (só se pula até um quadro-chave guardado)
    double _ptsMax = double.NaN, _ptsChave = double.NaN;
    Process? _fonte;
    // a posição da Sessão (o que o envio segue) e quando ela foi lida, para andar entre uma leitura e outra
    double _pos;
    bool _tocando;
    readonly Stopwatch _posLida = Stopwatch.StartNew();
    double _epocaInicio;
    double _maiorPos;
    readonly Stopwatch _epocaRelogio = new();
    bool _fimDoArquivo, _fonteParou;
    volatile int _epoca;
    volatile bool _fim;

    public FonteInfo Info { get; private set; }
    public PerfilStream Perfil { get; private set; }
    /// <summary>Upload que o envio pode usar (Mbps), dividido entre os espectadores: decide o ritmo do envio adiantado.</summary>
    public double UploadMbps { get; set; } = 30;

    /// <summary>(época, ponto em que ela começa em s, tocando): chamado a cada época nova, para quem assiste reabrir o fluxo.</summary>
    public event Action<int, double, bool>? Mudou;

    public EnvioStream(FonteInfo info, PerfilStream perfil) { Info = info; Perfil = perfil; }

    public int Epoca => _epoca;
    public bool Tocando { get { lock (_gate) return _tocando; } }

    /// <summary>A posição da Sessão que o envio segue (anda enquanto toca).</summary>
    public double Posicao { get { lock (_gate) return PosicaoLocked(); } }

    double PosicaoLocked() => _tocando ? _pos + _posLida.Elapsed.TotalSeconds : _pos;

    /// <summary>Quantos s do filme o envio já mandou à frente da posição (NaN sem época ou antes do primeiro quadro).</summary>
    public double Adiantado { get { double p = Posicao; lock (_dist) return _ptsMax - p; } }

    // ------------------------------------------------------------------ seguir a Sessão

    double _posAntes = double.NaN;
    bool _tocandoAntes, _saltoPendente, _emRajada;
    readonly Stopwatch _passada = new(), _ultimoSalto = new();
    int _saltosJuntos;

    /// <summary>
    /// A Sessão do anfitrião está em <paramref name="pos"/> (tocando ou não): chamado a cada mudança dela e a cada 400 ms. Abre uma época nova só quando quem assiste não tem
    /// esse ponto guardado; pausar e continuar só mudam até onde a leitura vai.
    /// </summary>
    public void Seguir(double pos, bool tocando)
    {
        pos = Math.Max(0, pos);
        lock (_gate)
        {
            if (_fim) return;
            double passou = _passada.IsRunning ? _passada.Elapsed.TotalSeconds : 0;
            bool salto = !double.IsNaN(_posAntes) && Math.Abs(pos - (_posAntes + (_tocandoAntes ? passou : 0))) > 1.0;
            _posAntes = pos; _tocandoAntes = tocando; _passada.Restart();
            _pos = pos; _tocando = tocando; _posLida.Restart();
            _maiorPos = Math.Max(_maiorPos, pos);
            if (salto)
            {
                // rajada = outro pulo há menos de 1,5 s (seta segurada, cliques seguidos na linha do tempo)
                _emRajada = _ultimoSalto.IsRunning && _ultimoSalto.ElapsedMilliseconds < 1500;
                _ultimoSalto.Restart();
                _saltoPendente = true;
            }
            if (_saidas.Count == 0) return; // ninguém assistindo: nada a ler (a primeira época abre quando alguém entra)
            string? motivo = null;
            if (_fonte == null) motivo = "alguém começou a assistir";
            else if (_fonteParou && _epocaRelogio.Elapsed.TotalSeconds > 3) motivo = "o ffmpeg do envio parou";
            else if (!TemGuardadoLocked(pos, _saltoPendente, out string porque)) motivo = porque;
            else { if (_saltoPendente && _saltosJuntos == 0) Log.Info($"[filme] pulo para {pos:0.#} s sem reabrir o envio: quem assiste já tem esse trecho."); _saltoPendente = false; _saltosJuntos = 0; return; }
            // caso de 11/11: 13 pulos de +10 s em 2,5 s (seta segurada) reabriam o envio 5 vezes. O primeiro pulo vale na hora; numa rajada, o ponto vale só depois de
            // 600 ms sem pulo novo (este método roda a cada mudança da Sessão, inclusive entre um pulo e outro, e a cada 400 ms).
            if (_saltoPendente && _emRajada && _ultimoSalto.ElapsedMilliseconds < 600) { if (salto) _saltosJuntos++; return; }
            if (_saltosJuntos > 0) Log.Info($"[filme] {_saltosJuntos + 1} pulos seguidos juntados numa época só.");
            _saltosJuntos = 0; _saltoPendente = false;
            IniciarEpocaLocked(pos, motivo);
        }
    }

    /// <summary>
    /// Quem assiste tem o ponto guardado? Para trás, até o primeiro quadro-chave que o espectador que entrou por último recebeu (e <see cref="AtrasS"/>). Para frente, até
    /// o que já foi mandado; num pulo, só até o último quadro-chave mandado (o mpv só procura entre quadros-chave guardados; medido no teste de 11/11).
    /// </summary>
    bool TemGuardadoLocked(double pos, bool salto, out string porque)
    {
        porque = "";
        lock (_dist)
        {
            if (double.IsNaN(_ptsMax))
            {
                // a época acabou de abrir: os dados do ponto dela estão vindo
                if (Math.Abs(pos - _epocaInicio) < 2) return true;
                porque = $"pulo para {pos:0.#} s antes de a época anterior mandar algo"; return false;
            }
            // Voltar não devolve ao mpv os dados já descartados: o limite segue o ponto mais distante desta época.
            double atras = Math.Max(_ptsChaveInicio(), _maiorPos - AtrasS);
            if (pos < atras - 0.2) { porque = $"pulo para {pos:0.#} s, antes do que quem assiste tem guardado ({atras:0.#} s)"; return false; }
            double frente = _fimDoArquivo ? double.MaxValue : salto ? _ptsChave : _ptsMax + 2;
            if (pos > frente) { porque = $"pulo para {pos:0.#} s, além do que já foi mandado ({(salto ? _ptsChave : _ptsMax):0.#} s)"; return false; }
            return true;
        }
    }

    /// <summary>Sob <c>_dist</c>: o começo do que TODOS os espectadores conectados têm (o maior "primeiro quadro-chave" entre eles).</summary>
    double _ptsChaveInicio()
    {
        double inicio = double.NegativeInfinity;
        foreach (var s in _ativas) if (!double.IsNaN(s.Inicio) && s.Inicio > inicio) inicio = s.Inicio;
        return double.IsNegativeInfinity(inicio) ? _epocaInicio - 10 : inicio;
    }

    /// <summary>Quem assiste não tem o ponto (o cache dele começa depois, ou o fluxo não chegou): abre uma época nova no ponto da Sessão, no máximo uma a cada 3 s.</summary>
    public void Recarregar(string quem)
    {
        lock (_gate)
        {
            if (_fim || _saidas.Count == 0 || _epocaRelogio.Elapsed.TotalSeconds < 3 || _saltoPendente) return; // com um pulo pendente, a época dele já vem
            IniciarEpocaLocked(PosicaoLocked(), $"{quem} não tinha o trecho guardado");
        }
    }

    // ------------------------------------------------------------------ telemetria (spec 009)

    long _bytes;
    double _velocidade = double.NaN, _velocidadeMin = double.NaN;
    readonly Stopwatch _lento = new();
    bool _avisouLento;
    long _segurouEm = long.MinValue / 2;

    /// <summary>Bytes que já saíram para todos os espectadores (somados), para o Mbps do filme.csv.</summary>
    public long BytesEnviados => Interlocked.Read(ref _bytes);
    /// <summary>Velocidade do ffmpeg do envio contra o tempo real nos últimos ~6 s: abaixo de 1 o fluxo atrasa e quem assiste trava. NaN pausado ou sem leitura.</summary>
    public double Velocidade => Tocando ? Volatile.Read(ref _velocidade) : double.NaN;
    /// <summary>A menor velocidade vista (NaN se nunca leu), para o filme_fim.</summary>
    public double VelocidadeMin => Volatile.Read(ref _velocidadeMin);

    [System.Text.RegularExpressions.GeneratedRegex(@"time=\s*(\d+):(\d+):([\d.]+)")] private static partial System.Text.RegularExpressions.Regex TempoMidia();
    readonly Queue<(double Relogio, double Midia)> _amostrasFonte = new();

    /// <summary>
    /// A linha de estatística do ffmpeg do envio (a cada 2 s, -stats_period). Caso de 11/11: um i5-8250U recomprimindo um 4K HDR andava a ~0,3x e ninguém via.
    /// Não usa o "speed=" do ffmpeg: é a média desde que ele abriu, e abrir o arquivo ou pular o deixa baixo por muito tempo. Aqui é quanto o "time=" andou nos últimos
    /// ~6 s dividido pelo tempo real. Com o envio adiantado, o ffmpeg também fica parado de propósito (já mandou <see cref="AdiantadoS"/> à frente, ou pausado): essas
    /// janelas não contam, senão toda pausa viraria "lento".
    /// </summary>
    void LinhaFonte(string linha, int epoca, Stopwatch idade)
    {
        var m = TempoMidia().Match(linha);
        if (!m.Success || _epoca != epoca) return;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        double midia = int.Parse(m.Groups[1].Value, ci) * 3600 + int.Parse(m.Groups[2].Value, ci) * 60 + double.Parse(m.Groups[3].Value, ci);
        double agora = idade.Elapsed.TotalSeconds;
        double v;
        lock (_amostrasFonte)
        {
            if (Environment.TickCount64 - Interlocked.Read(ref _segurouEm) < 7000) { _amostrasFonte.Clear(); _lento.Reset(); return; }
            _amostrasFonte.Enqueue((agora, midia));
            while (_amostrasFonte.Count > 1 && agora - _amostrasFonte.Peek().Relogio > 6.5) _amostrasFonte.Dequeue();
            var (r0, m0) = _amostrasFonte.Peek();
            if (agora - r0 < 3.5) return; // pouco tempo de janela ainda (começo da época)
            v = (midia - m0) / (agora - r0);
        }
        Volatile.Write(ref _velocidade, v);
        if (double.IsNaN(_velocidadeMin) || v < _velocidadeMin) Volatile.Write(ref _velocidadeMin, v);
        if (v >= 0.95) { _lento.Reset(); return; }
        if (!_lento.IsRunning) _lento.Start();
        if (_lento.Elapsed.TotalSeconds >= 6 && !_avisouLento)
        {
            _avisouLento = true;
            Log.Info($"[filme] [aviso] o envio do filme anda a {v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}x do tempo real há {_lento.Elapsed.TotalSeconds:0} s "
                + $"(época {epoca}, perfil {Perfil}, {Info.Resumo}): quem assiste vai travar. Provável: este PC não dá conta de recomprimir o arquivo; o perfil leve ajuda.");
            Lento?.Invoke(v); // o MainForm cai para o Leve ou avisa o anfitrião
        }
    }

    /// <summary>O envio anda abaixo de 0,95x do tempo real há 6 s nesta época (velocidade medida); chamado da thread do ffmpeg, uma vez por época.</summary>
    public event Action<double>? Lento;

    /// <summary>Muda o perfil (o envio ficou lento, entrou mais gente): com uma época aberta, abre outra no ponto da Sessão.</summary>
    public void TrocarPerfil(PerfilStream perfil, string motivo)
    {
        lock (_gate)
        {
            if (_fim || Perfil == perfil) return;
            Log.Info($"[filme] envio do filme passa do perfil {Perfil} para {perfil} ({motivo}).");
            Perfil = perfil;
            if (_fonte != null) IniciarEpocaLocked(PosicaoLocked(), motivo);
        }
    }

    FaixaAudio? _audio;
    /// <summary>A faixa de áudio do envio (a que o mpv do anfitrião toca); nula = a primeira do arquivo.</summary>
    public FaixaAudio? Audio { get { lock (_gate) return _audio; } }

    /// <summary>O anfitrião trocou a faixa de áudio no mpv dele: com uma época aberta, abre outra no ponto da Sessão.</summary>
    public void TrocarAudio(FaixaAudio faixa)
    {
        lock (_gate)
        {
            if (_fim || _audio?.Map == faixa.Map) return;
            bool primeira = _audio == null;
            _audio = faixa;
            Log.Info($"[filme] envio com a faixa de áudio {faixa.Descricao}{(primeira ? "" : " (o anfitrião trocou)")}.");
            if (_fonte != null) IniciarEpocaLocked(PosicaoLocked(), "faixa de áudio");
        }
    }

    /// <summary>
    /// Fila de episódios: o anfitrião passou para outro arquivo. O envio continua (as saídas e os SRT de quem assiste ficam), mas lê o arquivo novo desde 0 numa época nova, como
    /// o <see cref="TrocarPerfil"/> faz. Tudo que seguia o arquivo velho é zerado (posição, quadros-chave, faixa de áudio): o <see cref="Seguir"/> seguinte, com a
    /// Sessão nova parada em 0, não pode ver isso como um pulo e reabrir o arquivo velho. Chamar no mesmo trecho em que a Sessão do anfitrião é trocada.
    /// </summary>
    /// <param name="audio">A faixa que o mpv do anfitrião escolheu no arquivo novo (nula = a primeira).</param>
    public void TrocarFonte(FonteInfo nova, PerfilStream perfil, FaixaAudio? audio)
    {
        lock (_gate)
        {
            if (_fim) return;
            Log.Info($"[filme] envio do filme passa para o próximo arquivo da fila: {nova.Resumo}; perfil {perfil}.");
            PararFonteLocked();
            Info = nova; Perfil = perfil; _audio = audio;
            _pos = _maiorPos = _epocaInicio = 0; _tocando = false; _posLida.Restart();
            _posAntes = double.NaN; _tocandoAntes = false; _saltoPendente = false; _emRajada = false; _saltosJuntos = 0;
            _fimDoArquivo = _fonteParou = false;
            // ninguém assistindo: nada a ler, a primeira época abre quando alguém entrar (Seguir)
            if (_saidas.Count > 0) IniciarEpocaLocked(0, "próximo da fila");
        }
    }

    public void Adicionar(PeerLink link)
    {
        var s = new Saida { Link = link };
        lock (_gate) { if (_fim || !_saidas.TryAdd(link.SessionId, s)) return; }
        new Thread(() => LacoSaida(s)) { IsBackground = true, Name = "filme-stream-" + link.SessionId[..Math.Min(6, link.SessionId.Length)] }.Start();
    }

    public void Remover(string sessionId)
    {
        Saida? s;
        lock (_gate)
        {
            if (!_saidas.Remove(sessionId, out s)) return;
            // Sem espectadores, não há buffer a alimentar nem motivo para manter o arquivo e o encoder abertos.
            if (_saidas.Count == 0) PararFonteLocked();
        }
        s.Removida = true;
        Tools.KillQuietly(s.Srt);
    }

    public int Espectadores { get { lock (_gate) return _saidas.Count; } }

    // ------------------------------------------------------------------ fonte (o ffmpeg que lê o arquivo)

    void IniciarEpocaLocked(double pos, string motivo)
    {
        PararFonteLocked();
        _epoca++;
        _epocaInicio = _maiorPos = pos; _epocaRelogio.Restart(); _fimDoArquivo = _fonteParou = false;
        Log.Info($"[filme] época {_epoca}: envio a partir de {pos:0.#} s ({motivo}), {_saidas.Count} espectador(es), {(_tocando ? "tocando" : "pausado")}.");
        // as saídas de quem já assiste são derrubadas: cada uma abre um listener novo para a conexão nova do espectador
        foreach (var s in _saidas.Values) Tools.KillQuietly(s.Srt);
        var args = Fonte.ArgsEnvio(Info, Perfil, pos, _audio);
        Process p;
        int ep = _epoca;
        var idade = Stopwatch.StartNew();
        Volatile.Write(ref _velocidade, double.NaN); _lento.Reset(); _avisouLento = false;
        lock (_amostrasFonte) _amostrasFonte.Clear(); // as amostras são do relógio de cada época
        try { p = Tools.Start(Tools.Ffmpeg, args, "ffmpeg-filme", redirectStdin: false, onStderr: l => LinhaFonte(l, ep, idade), binaryStdout: true); }
        catch (Exception e) { Log.Info($"[filme] o envio do filme não abriu o ffmpeg: {e.Message}"); _fonteParou = true; return; }
        _fonte = p;
        bool placa = args.Contains("-hwaccel");
        new Thread(() => LerFonte(p, ep, placa)) { IsBackground = true, Name = "filme-stream-fonte" }.Start();
        bool tocando = _tocando;
        Task.Run(() => Mudou?.Invoke(ep, pos, tocando));
    }

    void PararFonteLocked()
    {
        var p = _fonte; _fonte = null;
        if (p != null) Tools.KillQuietly(p);
        lock (_dist)
        {
            _gop.Clear(); _gopBytes = 0; _gopValido = false; _gopPts = double.NaN; _pat = _pmt = null;
            _ptsMax = _ptsChave = double.NaN;
            foreach (var s in _saidas.Values) s.Inicio = double.NaN;
        }
    }

    /// <summary>
    /// Lê o mpegts do ffmpeg em pacotes inteiros de 188 bytes, guarda o trecho desde o último quadro-chave e reparte para as filas dos espectadores, na mesma ordem.
    /// O ritmo é daqui: no máximo <see cref="RitmoAgora"/> s de filme por s (para caber no upload; uma rajada solta no SRT "live" vira pacote descartado por atraso) e
    /// nunca mais de <see cref="AdiantadoS"/> à frente da Sessão. Parar de ler segura o ffmpeg no pipe, sem matar a época.
    /// </summary>
    void LerFonte(Process p, int epoca, bool placa)
    {
        var buf = new byte[Chunk * 2];
        int resto = 0;
        long lidos = 0;
        double credito = RajadaS, ultimoPts = double.NaN;
        var relogio = Stopwatch.StartNew();
        try
        {
            var stdout = p.StandardOutput.BaseStream;
            int n;
            while (_epoca == epoca && !_fim)
            {
                // ritmo: o crédito (s de filme) enche a RitmoAgora por segundo, até a rajada
                double dt = relogio.Elapsed.TotalSeconds; relogio.Restart();
                credito = Math.Min(Math.Max(RajadaS, credito), credito + dt * RitmoAgora());
                double adiantado = Adiantado;
                if (adiantado > AdiantadoS) { Interlocked.Exchange(ref _segurouEm, Environment.TickCount64); Thread.Sleep(100); continue; }
                if (credito <= 0) { Thread.Sleep(15); continue; }
                if ((n = stdout.Read(buf, resto, buf.Length - resto)) <= 0) break;
                lidos += n;
                int total = resto + n, inteiros = total / Ts * Ts;
                if (inteiros > 0)
                {
                    var pedaco = buf.AsSpan(0, inteiros).ToArray();
                    double pts;
                    lock (_dist) { if (_epoca != epoca) return; Guardar(pedaco); foreach (var s in _ativas) s.Fila!.TryAdd(pedaco); pts = _ptsMax; }
                    if (!double.IsNaN(pts)) { if (!double.IsNaN(ultimoPts) && pts > ultimoPts) credito -= pts - ultimoPts; ultimoPts = double.IsNaN(ultimoPts) ? pts : Math.Max(ultimoPts, pts); }
                }
                resto = total - inteiros;
                if (resto > 0) Buffer.BlockCopy(buf, inteiros, buf, 0, resto);
            }
        }
        catch (Exception e) when (!_fim && _epoca == epoca) { Log.Info($"[filme] a leitura do filme parou: {e.Message}"); }
        if (_fim || _epoca != epoca) return;
        if (lidos > 0)
        {
            // fim do arquivo (o ffmpeg sai com 0): tudo já foi mandado e qualquer ponto até o fim está guardado. Outro motivo: a próxima passada abre outra época
            string saida = SaidaDe(p);
            lock (_gate)
            {
                if (_epoca != epoca || !ReferenceEquals(_fonte, p)) return;
                if (saida == "0") { _fimDoArquivo = true; Log.Info($"[filme] o envio leu o filme até o fim (época {epoca})."); }
                else { _fonteParou = true; Log.Info($"[filme] [aviso] o ffmpeg do envio parou no meio (saída {saida}, época {epoca}); abrindo de novo no ponto do filme."); }
            }
            return;
        }
        if (!placa) { lock (_gate) if (_epoca == epoca) _fonteParou = true; return; }
        // o ffmpeg saiu sem escrever nada com a decodificação na placa (driver ou placa sem suporte): tenta a mesma época na CPU, e as próximas já vão sem a placa
        lock (_gate)
        {
            if (_fim || _epoca != epoca || !ReferenceEquals(_fonte, p) || !Fonte.Placa) return;
            Fonte.Placa = false;
            Log.Info($"[filme] [aviso] o envio do filme não abriu decodificando na placa de vídeo (saída {SaidaDe(p)}); tentando na CPU.");
            IniciarEpocaLocked(_epocaInicio, "sem a placa de vídeo");
        }
    }

    /// <summary>
    /// S de filme por s de relógio: o upload (com 30% de folga) dividido pelo fluxo de cada espectador, entre 1,1 e 2. Com 1 espectador num 1080p recomprimido (6 Mbps) é 2:
    /// o buffer de quem assiste cresce 1 s por s tocando e 2 s por s pausado. Na cópia direta que mal cabe, fica perto de 1,1 (cresce devagar, mas não estoura o upload).
    /// </summary>
    double RitmoAgora()
    {
        int n; lock (_gate) n = Math.Max(1, _saidas.Count);
        double r = UploadMbps * 0.7 / (Fonte.MbpsDoFluxo(Info, Perfil) * n);
        return Math.Clamp(double.IsFinite(r) ? r : 1.1, 1.1, 2.0);
    }

    static string SaidaDe(Process p) { try { return p.WaitForExit(2000) ? p.ExitCode.ToString() : "?"; } catch { return "?"; } }

    /// <summary>Sob <c>_dist</c>: atualiza PAT/PMT, o trecho desde o último quadro-chave do vídeo e até onde a época já mandou (PTS).</summary>
    void Guardar(byte[] pedaco)
    {
        for (int i = 0; i + Ts <= pedaco.Length; i += Ts)
        {
            int pid = ((pedaco[i + 1] & 0x1F) << 8) | pedaco[i + 2];
            var pacote = pedaco.AsSpan(i, Ts).ToArray();
            if (pid == PatPid) { _pat = pacote; continue; }
            if (pid == PmtPid) { _pmt = pacote; continue; }
            if (pid == VideoPid && (pedaco[i + 1] & 0x40) != 0 && Pts(pedaco, i) is double pts)
            {
                if (double.IsNaN(_ptsMax) || pts > _ptsMax) _ptsMax = pts;
                if (IsKeyStart(pedaco, i))
                {
                    _ptsChave = pts; _gopPts = pts;
                    // quem conectou sem o trecho guardado começa a ter o que procurar daqui
                    foreach (var s in _ativas) if (double.IsNaN(s.Inicio)) s.Inicio = pts;
                }
            }
            if (pid == VideoPid && IsKeyStart(pedaco, i)) { _gop.Clear(); _gopBytes = 0; _gopValido = true; }
            if (!_gopValido) continue;
            _gop.Add(pacote); _gopBytes += Ts;
            if (_gopBytes > MaxGopBytes) { _gop.Clear(); _gopBytes = 0; _gopValido = false; } // GOP grande demais para guardar: quem entra agora começa no próximo
        }
    }

    /// <summary>Início de quadro-chave: payload_unit_start + campo de adaptação com random_access_indicator (o mesmo teste do TsPipe).</summary>
    static bool IsKeyStart(byte[] b, int i) => (b[i + 1] & 0x40) != 0 && (b[i + 3] & 0x20) != 0 && b[i + 4] > 0 && (b[i + 5] & 0x40) != 0;

    /// <summary>O PTS (s) do cabeçalho PES que começa neste pacote TS; nulo se o pacote não traz um.</summary>
    internal static double? Pts(byte[] b, int i)
    {
        int af = (b[i + 3] >> 4) & 3;
        if (af == 2) return null; // só campo de adaptação, sem dados
        int p = i + 4 + (af == 3 ? 1 + b[i + 4] : 0);
        if (p + 14 > i + Ts || b[p] != 0 || b[p + 1] != 0 || b[p + 2] != 1 || (b[p + 7] & 0x80) == 0) return null;
        long pts = ((long)((b[p + 9] >> 1) & 7) << 30) | ((long)b[p + 10] << 22) | ((long)(b[p + 11] >> 1) << 15) | ((long)b[p + 12] << 7) | ((long)b[p + 13] >> 1);
        return pts / 90000.0;
    }

    // ------------------------------------------------------------------ uma saída por espectador

    /// <summary>
    /// ffmpeg copiando o mpegts para SRT (listener) na porta local do relay da ligação dele. Quando o player dele reconecta (época nova) ou fecha, o ffmpeg sai e um novo
    /// fica esperando. A senha vem do túnel dele: sem ela (túnel ainda não conectou) espera.
    /// </summary>
    void LacoSaida(Saida s)
    {
        int rapidas = 0;
        while (!_fim && !s.Removida)
        {
            string pass = s.Link.Tunnel.Passphrase;
            if (string.IsNullOrEmpty(pass)) { Thread.Sleep(300); continue; }
            int epoca = _epoca;
            int porta = LocalRelay.FreeUdpPort();
            s.Link.Relay.FixedTarget = new IPEndPoint(IPAddress.Loopback, porta);
            var aberto = Stopwatch.StartNew();
            var fila = new BlockingCollection<byte[]>(FilaMaxChunks);
            try
            {
                // caso de 11/11: o ffmpeg do SRT morria ao abrir ("Could not write header") e a época não tocava para ninguém; o log dele não vira evento
                // (é de ffmpeg), então a primeira linha de erro de cada abertura vira "falhou" no log do app, que sobe como evento "falha"
                int avisou = 0;
                void Erro(string l)
                {
                    if ((l.Contains("Could not write header") || l.Contains("Error opening output")) && Interlocked.Exchange(ref avisou, 1) == 0)
                        Log.Info($"[filme] o envio do filme para {s.Link.PeerNick} falhou na época {epoca}: {Tools.ForLog(l.Length > 200 ? l[..200] : l)}");
                }
                var srt = Tools.Start(Tools.Ffmpeg, ArgsSrt(porta, pass), "ffmpeg-srt-filme", redirectStdin: true, onStderr: Erro);
                s.Srt = srt;
                var escritor = new Thread(() => Escrever(fila, srt)) { IsBackground = true, Name = "filme-stream-escrita" };
                escritor.Start();
                Anexar(s, fila);
                while (!_fim && !s.Removida && _epoca == epoca && !srt.WaitForExit(250)) { }
            }
            catch (Exception e) { Log.Info($"[filme] o envio do filme para {s.Link.PeerNick} falhou: {e.Message}"); }
            lock (_dist) { s.Fila = null; _ativas.Remove(s); s.Inicio = double.NaN; }
            fila.CompleteAdding();
            Tools.KillQuietly(s.Srt);
            rapidas = aberto.Elapsed.TotalSeconds < 3 ? rapidas + 1 : 0;
            Thread.Sleep(rapidas >= 5 ? 5000 : 100);
        }
    }

    /// <summary>Liga a fila do espectador ao distribuidor: primeiro o trecho desde o último quadro-chave (para ele começar no quadro-chave da época), depois o que vier ao vivo.</summary>
    void Anexar(Saida s, BlockingCollection<byte[]> fila)
    {
        lock (_dist)
        {
            s.Inicio = double.NaN;
            if (_gopValido && _pat != null && _pmt != null)
            {
                fila.TryAdd(_pat); fila.TryAdd(_pmt);
                for (int i = 0; i < _gop.Count; i += Chunk / Ts)
                {
                    int quantos = Math.Min(Chunk / Ts, _gop.Count - i);
                    var junto = new byte[quantos * Ts];
                    for (int k = 0; k < quantos; k++) Buffer.BlockCopy(_gop[i + k], 0, junto, k * Ts, Ts);
                    fila.TryAdd(junto);
                }
                s.Inicio = _gopPts;
            }
            s.Fila = fila; _ativas.Add(s);
        }
    }

    void Escrever(BlockingCollection<byte[]> fila, Process srt)
    {
        try
        {
            var stdin = srt.StandardInput.BaseStream;
            foreach (var pedaco in fila.GetConsumingEnumerable()) { stdin.Write(pedaco, 0, pedaco.Length); stdin.Flush(); Interlocked.Add(ref _bytes, pedaco.Length); }
        }
        catch { /* o ffmpeg do SRT saiu (o espectador fechou ou a época mudou): o laço abre outro */ }
    }

    /// <summary>
    /// O ffmpeg do SRT só REPASSA os bytes do mpegts (<c>-f data</c>), sem abrir o mpegts. Caso de 11/11: ele lia o mpegts com uma sonda de 100 KB / 0,1 s, e quando o
    /// primeiro pacote de áudio (E-AC-3 ou AAC) vinha depois disso morria com "Could not find codec parameters", "sample rate not set" e "Could not write header", e a época
    /// não tocava. Aumentar a sonda não serve: no mpegts o ffmpeg lê até o teto mesmo depois de achar tudo (faixas podem aparecer no meio), e com o <c>-re</c> da fonte
    /// 3 s de sonda viraram 3,4 s a mais por época no teste local. O <c>-fflags +nobuffer</c> de antes ainda DESCARTAVA os pacotes lidos na sonda, então a época perdia
    /// o começo do quadro-chave guardado ("non-existing PPS 0 referenced"). Repassando, o primeiro byte sai na hora e nada se perde; quem lê o mpegts é o mpv.
    /// Pacotes de 1316 bytes (7 de 188, o pkt_size do SRT): o que chega no pipe já começa num pacote TS inteiro, então cada mensagem do SRT leva pacotes TS inteiros.
    /// </summary>
    static List<string> ArgsSrt(int porta, string pass)
    {
        Tools.PassphraseHint = pass;
        return new List<string> { "-hide_banner", "-loglevel", "warning", "-nostats", "-f", "data", "-raw_packet_size", "1316", "-i", "pipe:0",
            "-map", "0", "-c", "copy", "-f", "data", "-flush_packets", "1",
            $"srt://127.0.0.1:{porta}?mode=listener&latency={LatenciaMs * 1000L}&passphrase={pass}&pbkeylen=16&transtype=live&pkt_size=1316" };
    }

    /// <summary>O endereço que o mpv de quem assiste abre: a porta local do relay da ligação dele (o túnel leva até o anfitrião).</summary>
    public static string UrlDoEspectador(PeerLink link) =>
        $"srt://127.0.0.1:{link.Relay.Port}?mode=caller&latency={LatenciaMs * 1000L}&passphrase={link.Tunnel.Passphrase}&pbkeylen=16&transtype=live&connect_timeout=10000";

    public void Dispose()
    {
        List<Saida> todas;
        lock (_gate) { _fim = true; todas = _saidas.Values.ToList(); _saidas.Clear(); _tocando = false; PararFonteLocked(); }
        foreach (var s in todas) { s.Removida = true; Tools.KillQuietly(s.Srt); }
    }
}
