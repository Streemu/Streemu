using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using Streemu.Net;
using Streemu.Util;

namespace Streemu.Filme;

/// <summary>
/// Transporte confiável do arquivo do modo filme sobre o túnel (spec 010, 4.2), no tipo de pacote <see cref="Tunnel.TArquivo"/>.
/// O túnel é UDP e o SRT de hoje é de vídeo ao vivo (descarta o que atrasa), por isso um transporte próprio:
/// quem baixa pede uma janela de pedaços e conta, a cada 20 ms, o que já tem de cada um (confirmação seletiva por mapa de bits);
/// quem manda reenvia o que falta depois de um tempo sem resposta. A taxa sobe enquanto não há perda e cai quando há (como o TCP),
/// para só usar a sobra do upload. Cada pacote vai cifrado e autenticado (AES-GCM) com a chave da ligação.
/// </summary>
static class Pacote
{
    public const int Pedaco = 1 << 20;
    /// <summary>Dados por pacote: com os cabeçalhos (túnel 7 + nosso 9 + pedaço 6 + tag 16) fica abaixo de 1280 (o mínimo do IPv6).</summary>
    public const int Frag = 1200;
    public const byte KDados = 1, KEstado = 2;
    /// <summary>Pedaços com este bit são do manifesto (a lista de hashes), não do arquivo.</summary>
    public const uint Manifesto = 0x8000_0000;
    const int Cab = 9, Tag = 16;
    public const int Max = Cab + 6 + Frag + Tag;

    public static int Frags(int tamanho) => (tamanho + Frag - 1) / Frag;

    // [tipo 1][sal 4][seq 4][cifrado][tag 16]. Nonce = os 9 bytes do cabeçalho + 3 zeros: o tipo separa os dois sentidos,
    // o sal (sorteado por transferência) separa transferências na mesma ligação e o seq nunca repete dentro dela.
    public static int Selar(AesGcm aes, byte tipo, uint sal, uint seq, ReadOnlySpan<byte> claro, Span<byte> saida)
    {
        saida[0] = tipo;
        BinaryPrimitives.WriteUInt32LittleEndian(saida[1..], sal);
        BinaryPrimitives.WriteUInt32LittleEndian(saida[5..], seq);
        Span<byte> nonce = stackalloc byte[12];
        nonce.Clear();
        saida[..Cab].CopyTo(nonce);
        aes.Encrypt(nonce, claro, saida.Slice(Cab, claro.Length), saida.Slice(Cab + claro.Length, Tag), saida[..Cab]);
        return Cab + claro.Length + Tag;
    }

    public static bool Abrir(AesGcm aes, ReadOnlySpan<byte> pkt, Span<byte> claro, out byte tipo, out uint seq, out int n)
    {
        tipo = 0; seq = 0; n = pkt.Length - Cab - Tag;
        if (n < 0 || n > claro.Length) return false;
        tipo = pkt[0];
        seq = BinaryPrimitives.ReadUInt32LittleEndian(pkt[5..]);
        Span<byte> nonce = stackalloc byte[12];
        nonce.Clear();
        pkt[..Cab].CopyTo(nonce);
        try { aes.Decrypt(nonce, pkt.Slice(Cab, n), pkt.Slice(Cab + n, Tag), claro[..n], pkt[..Cab]); return true; }
        catch (CryptographicException) { return false; }
    }

    public static uint Sal() => (uint)RandomNumberGenerator.GetInt32(int.MaxValue) ^ ((uint)RandomNumberGenerator.GetInt32(2) << 31);

    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
}

/// <summary>
/// Teste: imita uma rede ruim na chegada dos pacotes do arquivo (o loopback não perde nada e responde em 0,1 ms, e assim o reenvio
/// e o recuo da taxa nunca rodam). STREEMU_TEST_FILME_PERDA=% descarta ao acaso; STREEMU_TEST_FILME_ATRASO=ms segura cada pacote esse
/// tempo (vale nos dois sentidos: a ida e volta fica 2x). Só os pacotes do arquivo; o resto do túnel não muda.
/// </summary>
static class Simulador
{
    public static readonly double Perda = double.TryParse(Environment.GetEnvironmentVariable("STREEMU_TEST_FILME_PERDA"), System.Globalization.CultureInfo.InvariantCulture, out var p) ? p / 100 : 0;
    public static readonly int AtrasoMs = int.TryParse(Environment.GetEnvironmentVariable("STREEMU_TEST_FILME_ATRASO"), out var a) ? a : 0;
    public static bool Ligado => Perda > 0 || AtrasoMs > 0;
    /// <summary>A ida e volta que o túnel não vê (o ping não passa pelo simulador).</summary>
    public static double IdaVoltaMs => 2 * AtrasoMs;

    /// <summary>Embrulha o tratador: descarta e/ou atrasa. Com atraso, tudo chega por uma thread só (o tratador não precisa de trava nova).</summary>
    public static Action<ReadOnlySpan<byte>> Embrulhar(Action<ReadOnlySpan<byte>> tratar, Func<bool> fim)
    {
        if (!Ligado) return tratar;
        if (AtrasoMs <= 0) return pkt => { if (Random.Shared.NextDouble() >= Perda) tratar(pkt); };
        var fila = new System.Collections.Concurrent.BlockingCollection<(long Ate, byte[] Pkt)>();
        new Thread(() =>
        {
            Pacote.timeBeginPeriod(1);
            try
            {
                while (!fim())
                {
                    if (!fila.TryTake(out var it, 100)) continue;
                    while (Stopwatch.GetTimestamp() < it.Ate) Thread.Sleep(1);
                    tratar(it.Pkt);
                }
            }
            catch (Exception e) when (!fim()) { Log.Info($"[filme] simulador parou: {e.Message}"); }
            finally { Pacote.timeEndPeriod(1); }
        }) { IsBackground = true, Name = "filme-simulador" }.Start();
        return pkt => { if (Random.Shared.NextDouble() >= Perda) fila.Add((Stopwatch.GetTimestamp() + AtrasoMs * Stopwatch.Frequency / 1000, pkt.ToArray())); };
    }
}

/// <summary>Quem manda (o anfitrião): responde ao estado de quem baixa mandando o que falta, no ritmo da taxa.</summary>
public sealed class Emissor : IDisposable
{
    const double TaxaMin = 128 * 1024;
    /// <summary>Teto para teste (STREEMU_FILME_TETO em bytes/s); sem ele, só a perda limita.</summary>
    static readonly double TaxaMax = double.TryParse(Environment.GetEnvironmentVariable("STREEMU_FILME_TETO"), out var t) && t > 0 ? t : 1e9;

    readonly Tunnel _t;
    readonly AesGcm _sela, _abre;
    readonly uint _sal = Pacote.Sal();
    uint _seq;
    readonly Func<uint, int> _tamanho;
    readonly Action<uint, byte[], int> _ler;
    readonly Func<bool> _recuar;
    readonly Thread _thread;
    readonly Action<ReadOnlySpan<byte>> _entrada;
    volatile bool _fim;
    readonly Stopwatch _sw = Stopwatch.StartNew();
    readonly object _gate = new();

    volatile (uint Peca, byte[]? Tem)[] _pedidos = Array.Empty<(uint, byte[]?)>();
    long _estadoMs = -1;
    /// <summary>Hora (ticks) em que cada seq saiu, para medir a ida e volta pelo eco de quem baixa.</summary>
    readonly long[] _saiuEm = new long[1 << 17];
    // controle de taxa (só na thread que recebe o estado, dentro do _gate)
    double _taxa = 1 << 20, _melhor, _perdaBase, _srtt = -1, _rttJanela = double.MaxValue;
    bool _inicio = true;
    long _jMs = -1, _quedaMs = -100_000, _subidaMs = -100_000, _jBytes;
    uint _jRx, _jMax;
    long _bytes, _reenvios;
    /// <summary>Menor ida e volta de cada bloco de 10 s do último minuto: a ida e volta sem fila (a base do LEDBAT).</summary>
    readonly double[] _base = Enumerable.Repeat(double.MaxValue, 6).ToArray();
    long _baseBloco = -1;
    // log a cada 5 s (o S3 entre dois PCs sai daqui, pela telemetria)
    long _lMs = -1, _lBytes, _lReenvios;
    uint _lEnviados, _lChegaram;
    int _lQuedas;
    double _lFila;

    /// <summary>Ida e volta para o reenvio: a medida pelos pacotes do arquivo; antes da primeira, a do ping do túnel.</summary>
    double Rtt => _srtt > 0 ? _srtt : Math.Max(1, _t.RttMs) + Simulador.IdaVoltaMs;
    double Base => _base.Min();
    public double Taxa => _taxa;
    public long BytesEnviados => Interlocked.Read(ref _bytes);
    public long Reenvios => Interlocked.Read(ref _reenvios);

    /// <param name="ler">Lê o pedaço inteiro no buffer (tamanho de <paramref name="tamanho"/>).</param>
    /// <param name="recuar">Há live saindo deste PC: a transferência fica só com a sobra do upload.</param>
    public Emissor(Tunnel t, Func<uint, int> tamanho, Action<uint, byte[], int> ler, Func<bool> recuar)
    {
        _t = t; _tamanho = tamanho; _ler = ler; _recuar = recuar;
        _sela = new AesGcm(t.ChaveArquivo, 16);
        _abre = new AesGcm(t.ChaveArquivo, 16);
        t.Arquivo = _entrada = Simulador.Embrulhar(Receber, () => _fim);
        _thread = new Thread(Laco) { IsBackground = true, Name = "filme-envio" };
        _thread.Start();
    }

    void Receber(ReadOnlySpan<byte> pkt)
    {
        long chegou = Stopwatch.GetTimestamp();
        Span<byte> claro = stackalloc byte[Pacote.Max];
        if (!Pacote.Abrir(_abre, pkt, claro, out byte tipo, out _, out int n) || tipo != Pacote.KEstado || n < 17) return;
        uint rx = BinaryPrimitives.ReadUInt32LittleEndian(claro), max = BinaryPrimitives.ReadUInt32LittleEndian(claro[4..]);
        uint eco = BinaryPrimitives.ReadUInt32LittleEndian(claro[8..]);
        long esperaUs = BinaryPrimitives.ReadUInt32LittleEndian(claro[12..]);
        int qtd = claro[16], o = 17;
        var lista = new (uint, byte[]?)[qtd];
        for (int i = 0; i < qtd; i++)
        {
            if (o + 6 > n) return;
            uint p = BinaryPrimitives.ReadUInt32LittleEndian(claro[o..]);
            int len = BinaryPrimitives.ReadUInt16LittleEndian(claro[(o + 4)..]);
            o += 6;
            if (o + len > n) return;
            lista[i] = (p, len == 0 ? null : claro.Slice(o, len).ToArray());
            o += len;
        }
        long agora = _sw.ElapsedMilliseconds;
        lock (_gate)
        {
            _pedidos = lista;
            Volatile.Write(ref _estadoMs, agora);
            // ida e volta = agora - quando o seq do eco saiu - quanto ele esperou do outro lado até este estado sair
            if (eco != 0 && _seq - eco < _saiuEm.Length && Volatile.Read(ref _saiuEm[eco & (_saiuEm.Length - 1)]) is long saiu && saiu > 0)
            {
                double rtt = (chegou - saiu) * 1000.0 / Stopwatch.Frequency - esperaUs / 1000.0;
                if (rtt > 0 && rtt < 5000)
                {
                    _srtt = _srtt < 0 ? rtt : _srtt * 0.875 + rtt * 0.125;
                    _rttJanela = Math.Min(_rttJanela, rtt);
                    long bloco = agora / 10_000;
                    if (bloco != _baseBloco) { _baseBloco = bloco; _base[bloco % _base.Length] = double.MaxValue; }
                    _base[bloco % _base.Length] = Math.Min(_base[bloco % _base.Length], rtt);
                }
            }
            AjustarTaxa(agora, rx, max);
        }
    }

    /// <summary>
    /// Controle de taxa por atraso, como o LEDBAT (feito para download em segundo plano que cede a vez): a fila no caminho é a ida e
    /// volta medida menos a menor do último minuto. Fila acima do alvo (25 ms; 10 ms com live saindo) = o upload encheu: cai 20%.
    /// É também o que protege o ping do jogo, porque a fila do roteador sobe antes de ele começar a descartar.
    /// Perda só conta como congestionamento acima da perda de fundo + 5 pontos: perda ao acaso (wi-fi, operadora) não é fila e só
    /// pede reenvio; tratar como fila derrubava a taxa para o mínimo (teste com 3% de perda, 2026-11-11).
    /// Sem fila: dobra até a primeira queda, depois sobe 10% a cada ida e volta. Avalia a cada 100 ms com pelo menos 30 pacotes
    /// (em taxa baixa, a janela espera juntar pacotes; sem isso a taxa ficava presa no mínimo).
    /// </summary>
    void AjustarTaxa(long agora, uint rx, uint max)
    {
        long bytes = Interlocked.Read(ref _bytes);
        if (_jMs < 0) { _jMs = agora; _jRx = rx; _jMax = max; _jBytes = bytes; return; }
        long dt = agora - _jMs;
        uint enviados = max - _jMax, chegaram = rx - _jRx;
        if (dt < 100 || (enviados < 30 && dt < 1000)) return;
        long usados = bytes - _jBytes;
        double rttJanela = _rttJanela;
        _jMs = agora; _jRx = rx; _jMax = max; _jBytes = bytes; _rttJanela = double.MaxValue;
        _lEnviados += enviados; _lChegaram += chegaram;
        bool recuar = _recuar();
        double perda = enviados == 0 ? 0 : 1 - Math.Min(1, chegaram / (double)enviados);
        double fila = rttJanela < double.MaxValue && Base < double.MaxValue ? Math.Max(0, rttJanela - Base) : 0;
        _lFila = Math.Max(_lFila, fila);
        bool cheia = fila > (recuar ? 10 : 25);
        bool perdaAlta = enviados >= 30 && perda > _perdaBase + 0.05;
        if (cheia || perdaAlta)
        {
            if (agora - _quedaMs > Rtt + 100) { _taxa = Math.Max(TaxaMin, _taxa * (perda > 0.2 ? 0.5 : 0.8)); _quedaMs = agora; _inicio = false; _lQuedas++; }
        }
        else
        {
            _perdaBase = Math.Min(0.2, _perdaBase * 0.95 + perda * 0.05);
            double medida = usados * 1000.0 / dt;
            // a melhor taxa SEM live: com live a taxa já está presa ao teto e a medida não diz quanto o upload aguenta (ver o teto abaixo)
            if (!recuar && perda < _perdaBase + 0.01) _melhor = Math.Max(_melhor, medida);
            // só sobe se está usando a taxa (sem nada para mandar, subir só prepara uma rajada) e uma vez por ida e volta
            if (medida > _taxa * 0.6 && agora - _quedaMs > 300 && agora - _subidaMs >= Math.Max(100, Rtt))
            {
                _taxa = _inicio ? _taxa * 2 : _taxa * 1.1 + 64 * 1024;
                _subidaMs = agora;
            }
        }
        // Com live saindo, quem cede a vez é a fila (alvo de 10 ms, acima); o teto de 40% só vale com uma medida tirada SEM live.
        // Antes o teto era 40% da melhor medida, que com a live já no ar era tirada debaixo do próprio teto: 1 MB/s, depois 0,4, e preso
        // em ~0,3 MB/s com o upload sobrando (fila 1-2 ms, perda 0) — envio de 38 GB entre dois PCs do grupo em 2026-11-17.
        if (recuar)
        {
            _inicio = false; // sem dobrar: sobe 10% por ida e volta, para a fila avisar antes de a live sentir
            if (_melhor > 0) _taxa = Math.Min(_taxa, Math.Max(256 * 1024, 0.4 * _melhor));
        }
        _taxa = Math.Min(_taxa, TaxaMax);
        if (_lMs < 0) _lMs = agora;
        else if (agora - _lMs >= 5000)
        {
            long reenvios = Interlocked.Read(ref _reenvios);
            Log.Info($"[filme] envio: {(bytes - _lBytes) / 1e3 / (agora - _lMs):N1} MB/s, taxa {_taxa / 1e6:N1} MB/s, " +
                     $"perda {(_lEnviados == 0 ? 0 : 100.0 * (1 - Math.Min(1, _lChegaram / (double)_lEnviados))):N1}% (fundo {_perdaBase * 100:N1}%), " +
                     $"reenvios {reenvios - _lReenvios}, quedas {_lQuedas}, ida e volta {Rtt:N0} ms (base {(Base < double.MaxValue ? Base : -1):N0}, fila máx {_lFila:N0})" +
                     (recuar ? ", recuando (live saindo)" : "") + ".");
            _lMs = agora; _lBytes = bytes; _lReenvios = reenvios; _lEnviados = _lChegaram = 0; _lQuedas = 0; _lFila = 0;
        }
    }

    /// <summary>
    /// Quanto da taxa o balde guarda: 4 ms normalmente, mas se o sistema demorou para acordar o laço, o balde cobre esse atraso (até 20 ms) em vez de jogar fora o
    /// que a taxa já deu direito. Sem isso a vazão virava "balde ÷ intervalo do relógio" (~1 MB/s com a janela minimizada), qualquer que fosse a taxa.
    /// </summary>
    static double BucketSeconds(double gapSeconds) => Math.Clamp(gapSeconds * 1.5, 0.004, 0.02);

    void Laco()
    {
        Pacote.timeBeginPeriod(1);
        using var boost = TransferBoost.Request(); // com a janela minimizada o Windows acordaria este laço só a cada ~15,6 ms (ver TransferBoost)
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        var envioEm = new Dictionary<uint, long[]>();
        var cache = new Dictionary<uint, byte[]>();
        try
        {
            var claro = new byte[6 + Pacote.Frag];
            var pkt = new byte[Pacote.Max];
            double fichas = 0;
            long antes = _sw.ElapsedTicks;
            (uint, byte[]?)[]? visto = null;
            while (!_fim)
            {
                long ticks = _sw.ElapsedTicks, agora = _sw.ElapsedMilliseconds;
                double taxa = _taxa;
                fichas = Math.Min(fichas + (ticks - antes) * taxa / Stopwatch.Frequency, Math.Max(16 * 1024, taxa * BucketSeconds((ticks - antes) / (double)Stopwatch.Frequency)));
                antes = ticks;
                var pedidos = _pedidos;
                long estado = Volatile.Read(ref _estadoMs);
                // sem notícia de quem baixa há 2 s (fechou o app, a ligação caiu): para de mandar
                if (estado < 0 || agora - estado > 2000 || pedidos.Length == 0 || _t.State != TunnelState.Connected) { fichas = 0; Thread.Sleep(5); continue; }
                if (!ReferenceEquals(pedidos, visto))
                {
                    foreach (var k in envioEm.Keys.Where(k => !pedidos.Any(p => p.Item1 == k)).ToList()) envioEm.Remove(k);
                    foreach (var k in cache.Keys.Where(k => !pedidos.Any(p => p.Item1 == k)).ToList()) { ArrayPool<byte>.Shared.Return(cache[k]); cache.Remove(k); }
                    visto = pedidos;
                }
                long rto = (long)(Rtt * 1.5) + 40;
                bool mandou = false;
                foreach (var (peca, tem) in pedidos)
                {
                    if (fichas < Pacote.Frag) break;
                    int tam = _tamanho(peca);
                    if (tam <= 0) continue;
                    int nf = Pacote.Frags(tam);
                    if (!envioEm.TryGetValue(peca, out var env)) envioEm[peca] = env = new long[nf];
                    byte[]? dados = null;
                    for (int f = 0; f < nf && fichas >= Pacote.Frag; f++)
                    {
                        if (tem != null && (f >> 3) < tem.Length && (tem[f >> 3] & (1 << (f & 7))) != 0) continue;
                        if (env[f] != 0 && agora + 1 - env[f] < rto) continue;
                        if (env[f] != 0) Interlocked.Increment(ref _reenvios);
                        if (dados == null && !cache.TryGetValue(peca, out dados))
                        {
                            dados = ArrayPool<byte>.Shared.Rent(Pacote.Pedaco);
                            _ler(peca, dados, tam);
                            cache[peca] = dados;
                        }
                        int len = Math.Min(Pacote.Frag, tam - f * Pacote.Frag);
                        BinaryPrimitives.WriteUInt32LittleEndian(claro, peca);
                        BinaryPrimitives.WriteUInt16LittleEndian(claro.AsSpan(4), (ushort)f);
                        Buffer.BlockCopy(dados, f * Pacote.Frag, claro, 6, len);
                        uint seq = ++_seq;
                        if (seq == 0) seq = ++_seq; // 0 é "sem eco"
                        int n = Pacote.Selar(_sela, Pacote.KDados, _sal, seq, claro.AsSpan(0, 6 + len), pkt);
                        Volatile.Write(ref _saiuEm[seq & (_saiuEm.Length - 1)], Stopwatch.GetTimestamp());
                        _t.SendArquivo(pkt.AsSpan(0, n));
                        env[f] = agora + 1;
                        fichas -= n;
                        mandou = true;
                        Interlocked.Add(ref _bytes, n);
                    }
                }
                if (!mandou || fichas < Pacote.Frag) Thread.Sleep(1);
            }
        }
        catch (Exception e) when (!_fim) { Log.Info($"[filme] o envio do arquivo parou: {e.Message}"); }
        finally
        {
            Pacote.timeEndPeriod(1);
            foreach (var b in cache.Values) ArrayPool<byte>.Shared.Return(b);
        }
    }

    public void Dispose()
    {
        _fim = true;
        if (_t.Arquivo == _entrada) _t.Arquivo = null;
        _thread.Join(500);
        _sela.Dispose(); _abre.Dispose();
    }
}

/// <summary>Quem baixa: pede os pedaços numa janela, junta os pacotes e entrega cada pedaço inteiro em <see cref="Prontos"/>.</summary>
public sealed class Receptor : IDisposable
{
    /// <summary>Pedaços em curso (10 MB): os mapas de bits de todos cabem num pacote de estado.</summary>
    const int Janela = 10;

    sealed class Peca { public uint Id; public byte[] Buf = null!; public byte[] Tem = null!; public int Nf, Faltam, Tam; }

    readonly Tunnel _t;
    readonly AesGcm _sela, _abre;
    readonly uint _sal = Pacote.Sal();
    uint _seq;
    readonly Func<uint, int> _tamanho;
    readonly object _gate = new();
    readonly SortedDictionary<uint, Peca> _emCurso = new();
    readonly SortedSet<uint> _fila = new();
    int _entregues;
    uint _rx, _max, _eco;
    long _ecoEm;
    bool _temMax;
    readonly byte[] _claro = new byte[Pacote.Max];
    readonly Channel<(uint Peca, byte[] Buf, int Tam)> _prontos = Channel.CreateUnbounded<(uint, byte[], int)>();
    readonly Thread _thread;
    readonly Action<ReadOnlySpan<byte>> _entrada;
    volatile bool _fim;
    long _bytes;

    public ChannelReader<(uint Peca, byte[] Buf, int Tam)> Prontos => _prontos.Reader;
    public long BytesRecebidos => Interlocked.Read(ref _bytes);

    public Receptor(Tunnel t, Func<uint, int> tamanho)
    {
        _t = t; _tamanho = tamanho;
        _sela = new AesGcm(t.ChaveArquivo, 16);
        _abre = new AesGcm(t.ChaveArquivo, 16);
        t.Arquivo = _entrada = Simulador.Embrulhar(Receber, () => _fim);
        _thread = new Thread(Laco) { IsBackground = true, Name = "filme-recebe" };
        _thread.Start();
    }

    public void Pedir(IEnumerable<uint> pecas) { lock (_gate) foreach (var p in pecas) _fila.Add(p); }

    /// <summary>O consumidor terminou com o pedaço: devolve o buffer e abre espaço na janela.</summary>
    public void Liberar(byte[] buf) { ArrayPool<byte>.Shared.Return(buf); lock (_gate) _entregues--; }

    void Receber(ReadOnlySpan<byte> pkt) // thread do túnel
    {
        if (!Pacote.Abrir(_abre, pkt, _claro, out byte tipo, out uint seq, out int n) || tipo != Pacote.KDados || n < 6) return;
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(_claro);
        int f = BinaryPrimitives.ReadUInt16LittleEndian(_claro.AsSpan(4)), len = n - 6;
        Peca? pronta = null;
        lock (_gate)
        {
            _rx++;
            if (!_temMax || (int)(seq - _max) > 0) { _max = seq; _temMax = true; }
            _eco = seq; _ecoEm = Stopwatch.GetTimestamp();
            if (!_emCurso.TryGetValue(id, out var p) || f >= p.Nf || (p.Tem[f >> 3] & (1 << (f & 7))) != 0) return;
            if (len != Math.Min(Pacote.Frag, p.Tam - f * Pacote.Frag)) return;
            Buffer.BlockCopy(_claro, 6, p.Buf, f * Pacote.Frag, len);
            p.Tem[f >> 3] |= (byte)(1 << (f & 7));
            _bytes += len;
            if (--p.Faltam == 0) { _emCurso.Remove(id); _entregues++; pronta = p; }
        }
        if (pronta != null) _prontos.Writer.TryWrite((pronta.Id, pronta.Buf, pronta.Tam));
    }

    void Laco()
    {
        Pacote.timeBeginPeriod(1); // o pedido de pedaços dorme 20 ms: com o relógio em 15,6 ms (janela minimizada) viraria ~31 ms e o envio ficaria sem notícia
        using var boost = TransferBoost.Request();
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        var claro = new byte[Pacote.Max - 25];
        var pkt = new byte[Pacote.Max];
        try
        {
            while (!_fim)
            {
                int n;
                lock (_gate)
                {
                    while (_emCurso.Count + _entregues < Janela && _fila.Count > 0)
                    {
                        uint id = _fila.Min;
                        _fila.Remove(id);
                        if (_emCurso.ContainsKey(id)) continue;
                        int tam = _tamanho(id), nf = Pacote.Frags(tam);
                        _emCurso[id] = new Peca { Id = id, Tam = tam, Nf = nf, Faltam = nf, Tem = new byte[(nf + 7) / 8], Buf = ArrayPool<byte>.Shared.Rent(Pacote.Pedaco) };
                    }
                    BinaryPrimitives.WriteUInt32LittleEndian(claro, _rx);
                    BinaryPrimitives.WriteUInt32LittleEndian(claro.AsSpan(4), _max);
                    // eco: o último seq que chegou e há quanto tempo (µs), para quem manda medir a ida e volta sem a espera daqui
                    BinaryPrimitives.WriteUInt32LittleEndian(claro.AsSpan(8), _eco);
                    BinaryPrimitives.WriteUInt32LittleEndian(claro.AsSpan(12), _eco == 0 ? 0u : (uint)Math.Min(uint.MaxValue, (Stopwatch.GetTimestamp() - _ecoEm) * 1_000_000 / Stopwatch.Frequency));
                    int o = 17, q = 0;
                    foreach (var p in _emCurso.Values)
                    {
                        int len = p.Faltam < p.Nf ? p.Tem.Length : 0;
                        if (o + 6 + len > claro.Length || q == 255) break;
                        BinaryPrimitives.WriteUInt32LittleEndian(claro.AsSpan(o), p.Id);
                        BinaryPrimitives.WriteUInt16LittleEndian(claro.AsSpan(o + 4), (ushort)len);
                        o += 6;
                        if (len > 0) { p.Tem.CopyTo(claro, o); o += len; }
                        q++;
                    }
                    claro[16] = (byte)q;
                    n = o;
                }
                int m = Pacote.Selar(_sela, Pacote.KEstado, _sal, ++_seq, claro.AsSpan(0, n), pkt);
                _t.SendArquivo(pkt.AsSpan(0, m));
                Thread.Sleep(20);
            }
        }
        catch (Exception e) when (!_fim) { Log.Info($"[filme] o pedido de pedaços parou: {e.Message}"); }
        finally { Pacote.timeEndPeriod(1); }
    }

    public void Dispose()
    {
        _fim = true;
        if (_t.Arquivo == _entrada) _t.Arquivo = null;
        _thread.Join(500);
        lock (_gate) { foreach (var p in _emCurso.Values) ArrayPool<byte>.Shared.Return(p.Buf); _emCurso.Clear(); }
        _sela.Dispose(); _abre.Dispose();
    }
}
