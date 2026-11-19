using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Streemu.Media;

/// <summary>
/// Tira o som do próprio Streemu (o player da live de um amigo, os avisos sonoros) de "PC todo menos Discord". Essa captura só sabe excluir UM programa
/// (o Discord), então o som da live que o anfitrião assiste entrava na live dele (diagnóstico de UX de 13/11, item 1).
/// Como funciona: o AudioPump abre uma segunda captura só do Streemu (incluir a árvore do app, onde o ffplay é filho) e cada pacote do "PC todo menos
/// Discord" é emparelhado com o pacote do app do mesmo período do motor de áudio do Windows. Medido em 13/11 com um ruído rosa tocado pelo ffplay: 100 % das
/// amostras idênticas nas duas capturas, então X − P deixa só o resto (jogo, música...). O QPC dos dois pacotes casados difere de 0,2 a 2,5 ms (e a fase de
/// cada captura é sorteada a cada abertura), então arredondar o QPC para o período às vezes casa o pacote vizinho (testado: o ruído sobrava com ganho 0,87).
/// Por isso o par é escolhido pelo conteúdo: entre os períodos vizinhos (−1, 0, +1) vale o que mais reduz a energia do pacote; se nenhum reduz, o pacote passa.
/// Pacote do app que não chega (o app está calado: o Windows não manda pacote no silêncio) deixa o do PC passar sem mexer. Custo: o PC todo espera
/// <see cref="HoldMs"/> ms pelo par antes de seguir (áudio ~20 ms mais tarde que o vídeo, abaixo do que se percebe).
/// Uso: <c>var s = new AppAudioSubtractor(pcm => buffer.AddSamples(pcm)); s.AddApp(qpc, pcmApp); s.AddAll(qpc, pcmTudo);</c>
/// </summary>
sealed class AppAudioSubtractor : IDisposable
{
    public const int HoldMs = 20;
    const int MaxAppPackets = 100; // 1 s de pacotes do app esperando par; mais que isso é streemu de um par que nunca vem

    sealed record Pending(long Key, byte[] Pcm, long ArrivedMs);

    readonly object _gate = new();
    readonly SortedDictionary<long, byte[]> _app = new();
    readonly Queue<Pending> _all = new();
    readonly Action<ReadOnlySpan<byte>> _emit;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Thread _thread;
    volatile bool _stopped;
    long _paired, _passed;
    readonly long[] _byOffset = new long[3];    // quantos pares vieram do período −1, 0 e +1
    readonly long[] _missDistance = new long[6]; // sem par: distância (em períodos) até o pacote do app mais próximo; 5 = 5 ou mais

    public AppAudioSubtractor(Action<ReadOnlySpan<byte>> emit)
    {
        _emit = emit;
        _thread = new Thread(Loop) { IsBackground = true, Name = "audio-subtrai", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Pacotes de "PC todo menos Discord" que tiveram o som do app tirado, e os que passaram sem par (o app estava calado).</summary>
    public (long Paired, long Passed) Counts { get { lock (_gate) return (_paired, _passed); } }

    /// <summary>Resumo para o log: de que período vizinho vieram os pares e, nos pacotes sem par, a que distância estava o pacote do app mais próximo.</summary>
    public string Detail { get { lock (_gate) return $"pares por deslocamento −1/0/+1: {_byOffset[0]}/{_byOffset[1]}/{_byOffset[2]}; sem par por distância 0..5+: {string.Join("/", _missDistance)}"; } }

    /// <summary>Número do período de áudio do pacote: o QPC (unidades de 100 ns) dividido pela duração do pacote, arredondado (os dois lados diferem ~0,2 ms).</summary>
    static long PeriodOf(long qpc, int bytes)
    {
        double ticks = bytes / (double)AudioPump.BlockAlign * 10_000_000.0 / AudioPump.SampleRate;
        return (long)Math.Round(qpc / ticks);
    }

    public void AddApp(long qpc, ReadOnlySpan<byte> pcm)
    {
        lock (_gate)
        {
            _app[PeriodOf(qpc, pcm.Length)] = pcm.ToArray();
            while (_app.Count > MaxAppPackets) _app.Remove(_app.Keys.First());
        }
    }

    public void AddAll(long qpc, ReadOnlySpan<byte> pcm)
    {
        var item = new Pending(PeriodOf(qpc, pcm.Length), pcm.ToArray(), _clock.ElapsedMilliseconds);
        lock (_gate) _all.Enqueue(item);
    }

    void Loop()
    {
        while (!_stopped)
        {
            Thread.Sleep(5);
            foreach (var ready in TakeReady()) _emit(ready);
        }
    }

    /// <summary>Os pacotes do PC que já esperaram o par, na ordem de chegada, com o som do app tirado quando o par existe.</summary>
    List<byte[]> TakeReady()
    {
        var ready = new List<byte[]>();
        lock (_gate)
        {
            while (_all.Count > 0 && _clock.ElapsedMilliseconds - _all.Peek().ArrivedMs >= HoldMs)
            {
                var all = _all.Dequeue();
                if (SubtractBestMatch(all)) _paired++; else _passed++;
                ready.Add(all.Pcm);
            }
        }
        return ready;
    }

    /// <summary>Tira do pacote o pacote do app que mais reduz a energia entre os períodos vizinhos (e o consome). Falso se nenhum reduz (o app estava calado).</summary>
    bool SubtractBestMatch(Pending all)
    {
        var x = MemoryMarshal.Cast<byte, float>(all.Pcm.AsSpan());
        double bestEnergy = Energy(x, null);
        long bestKey = long.MinValue;
        foreach (long key in new[] { all.Key, all.Key - 1, all.Key + 1 })
        {
            if (!_app.TryGetValue(key, out var app) || app.Length != all.Pcm.Length) continue;
            double energy = Energy(x, MemoryMarshal.Cast<byte, float>(app.AsSpan()));
            if (energy >= bestEnergy * 0.999) continue;
            bestEnergy = energy;
            bestKey = key;
        }
        if (bestKey == long.MinValue) { _missDistance[(int)Math.Min(5, NearestAppDistance(all.Key))]++; return false; }
        _byOffset[(int)(bestKey - all.Key + 1)]++;
        var p = MemoryMarshal.Cast<byte, float>(_app[bestKey].AsSpan());
        for (int i = 0; i < x.Length; i++) x[i] -= p[i];
        _app.Remove(bestKey);
        return true;
    }

    long NearestAppDistance(long key) => _app.Count == 0 ? 5 : _app.Keys.Min(k => Math.Abs(k - key));

    /// <summary>Energia de x − p (ou de x, sem p).</summary>
    static double Energy(ReadOnlySpan<float> x, ReadOnlySpan<float> p)
    {
        double sum = 0;
        for (int i = 0; i < x.Length; i++) { double v = p.IsEmpty ? x[i] : x[i] - p[i]; sum += v * v; }
        return sum;
    }

    public void Dispose()
    {
        _stopped = true;
        _thread.Join(1000);
    }
}
