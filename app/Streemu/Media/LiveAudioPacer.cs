using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Streemu.Media;

/// <summary>Relógio do PCM separado do dispositivo, para exercitar bloqueios e trocas reais de pipe sem capturar áudio do usuário.</summary>
sealed class LiveAudioPacer : IDisposable
{
    sealed record Destination(Stream Stream, int Epoch);
    readonly LiveAudioBuffer _buffer = new(AudioPump.BytesPerSecond / 2, AudioPump.BlockAlign);
    readonly Thread _thread;
    volatile bool _stopped;
    Destination? _destination;
    int _epoch;
    long _debtMs, _maxDebtMs, _maxWriteMs, _blocked, _failures, _catchups;
    long _queueTrims, _reanchors;

    public LiveAudioPacer()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "audio-pacer", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Push(ReadOnlySpan<byte> pcm) => _buffer.Push(pcm);

    public void Attach(Stream? stream)
    {
        _buffer.Clear();
        int epoch = Interlocked.Increment(ref _epoch);
        Interlocked.Exchange(ref _destination, stream == null ? null : new Destination(stream, epoch));
    }

    public JsonObject Snapshot()
    {
        var c = _buffer.Counts;
        return new JsonObject
        {
            ["epoca"] = Volatile.Read(ref _epoch), ["pipe_ligado"] = Volatile.Read(ref _destination) != null,
            ["fila_ms"] = c.Queued * 1000L / AudioPump.BytesPerSecond,
            ["descartado_ms"] = c.Dropped * 1000L / AudioPump.BytesPerSecond,
            ["silencio_inserido_ms"] = c.Silence * 1000L / AudioPump.BytesPerSecond,
            ["atraso_pacer_ms"] = Interlocked.Read(ref _debtMs), ["pior_atraso_pacer_ms"] = Interlocked.Read(ref _maxDebtMs),
            ["pior_escrita_ms"] = Interlocked.Read(ref _maxWriteMs), ["escritas_bloqueadas"] = Interlocked.Read(ref _blocked),
            ["falhas_escrita"] = Interlocked.Read(ref _failures), ["retomadas_apos_bloqueio"] = Interlocked.Read(ref _catchups),
            ["correcoes_fila_persistente"] = Interlocked.Read(ref _queueTrims),
            ["relogio_reancorado"] = Interlocked.Read(ref _reanchors),
        };
    }

    void Run()
    {
        var chunk = new byte[AudioPump.BytesPerSecond / 50];
        Destination? active = null;
        long written = 0;
        bool behind = false;
        long queueHighAt = 0, lastTrim = 0, debtHighAt = 0;
        // Início do relógio deste destino: o fim da PRIMEIRA escrita aceita (0 = ainda não aceitou nada). Um ffmpeg novo
        // só lê o stdin depois de abrir ddagrab/nvenc (~1,5 s) ou a imagem da pausa (~7 s) e conta o tempo do áudio a
        // partir do 1º byte que lê; contar desde o Attach virava dívida que o ffmpeg, preso ao tempo real, nunca deixava pagar.
        long origin = 0;
        while (!_stopped)
        {
            Thread.Sleep(5);
            var target = Volatile.Read(ref _destination);
            if (target == null) { active = null; continue; }
            if (!ReferenceEquals(target, active)) { active = target; written = 0; behind = false; queueHighAt = 0; debtHighAt = 0; origin = 0; }
            // Lotes limitados: uma parada longa não monopoliza um núcleo nem perde segundos do relógio do áudio.
            for (int batch = 0; batch < 50 && !_stopped && ReferenceEquals(target, Volatile.Read(ref _destination)); batch++)
            {
                long debt, ms = 0;
                if (origin == 0) debt = chunk.Length; // primeira escrita: sem relógio ainda, só entrega o som mais recente
                else
                {
                    long due = (long)(Stopwatch.GetElapsedTime(origin).TotalSeconds * AudioPump.BytesPerSecond) & ~(long)(AudioPump.BlockAlign - 1);
                    debt = Math.Max(0, due - written);
                    ms = debt * 1000 / AudioPump.BytesPerSecond;
                    Interlocked.Exchange(ref _debtMs, ms); Max(ref _maxDebtMs, ms);
                    if (ms >= 100) behind = true;
                    else if (behind) { Interlocked.Increment(ref _catchups); behind = false; }
                    // Rede de segurança: o ffmpeg dita o ritmo e a dívida não baixa. Um sink que só aceita em tempo real nunca
                    // paga dívida; sem isto o buffer devolve silêncio para sempre. Critério: dívida ≥ 500 ms contínua por 1,5 s.
                    // Não zera em escrita lenta: no campo (2026-11-17) o ffmpeg preso bloqueava ~3 escritas/s e a re-ancoragem
                    // nunca dispararia. Uma parada pontual seguida de um sink que volta a aceitar rápido paga a dívida em ms,
                    // a dívida cai abaixo de 500 e a contagem zera sozinha.
                    if (ms < 500) debtHighAt = 0;
                    else if (debtHighAt == 0) debtHighAt = Stopwatch.GetTimestamp();
                    else if (Stopwatch.GetElapsedTime(debtHighAt).TotalSeconds >= 1.5)
                    {
                        Interlocked.Increment(ref _reanchors);
                        origin = Stopwatch.GetTimestamp(); written = 0; debtHighAt = 0; behind = false;
                        Interlocked.Exchange(ref _debtMs, 0);
                        break;
                    }
                }
                if (debt < chunk.Length) break;
                // Fila persistentemente cheia pode atrasar o CONTEÚDO sem afastar os PTS. Não usar QPC de driver para decidir.
                // Só age com o pacer em dia, após 3 s acima de 250 ms; nenhuma espera ou mudança no PCM saudável.
                long now = Stopwatch.GetTimestamp();
                bool queued = ms < 100 && _buffer.Counts.Queued >= AudioPump.BytesPerSecond / 4;
                if (!queued) queueHighAt = 0;
                else if (queueHighAt == 0) queueHighAt = now;
                else if (Stopwatch.GetElapsedTime(queueHighAt, now).TotalSeconds >= 3
                    && (lastTrim == 0 || Stopwatch.GetElapsedTime(lastTrim, now).TotalSeconds >= 30))
                {
                    if (_buffer.KeepNewest(AudioPump.BytesPerSecond * 80 / 1000) > 0) Interlocked.Increment(ref _queueTrims);
                    lastTrim = now; queueHighAt = 0;
                }
                _buffer.Read(chunk, origin == 0 ? 0 : debt);
                if (!ReferenceEquals(target, Volatile.Read(ref _destination))) break;
                long started = Stopwatch.GetTimestamp();
                try { target.Stream.Write(chunk); target.Stream.Flush(); }
                catch
                {
                    Interlocked.Increment(ref _failures);
                    // A escrita do processo antigo pode falhar DEPOIS de Attach(novo). Não desligar o destino novo.
                    Interlocked.CompareExchange(ref _destination, null, target);
                    break;
                }
                long writeMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Max(ref _maxWriteMs, writeMs);
                if (writeMs >= 100) Interlocked.Increment(ref _blocked);
                if (origin == 0)
                {
                    origin = Stopwatch.GetTimestamp(); written = chunk.Length;
                    // Durante a espera a fila encheu com som que o ffmpeg ainda não podia ler; ficar com 0,5 s de som velho
                    // só atrasaria o áudio em relação ao vídeo ao vivo. Guarda o recente (a mesma poda da fila persistente).
                    if (writeMs >= 100) _buffer.KeepNewest(AudioPump.BytesPerSecond * 80 / 1000);
                    continue;
                }
                written += chunk.Length;
            }
        }
    }

    static void Max(ref long location, long value)
    {
        long before;
        do { before = Interlocked.Read(ref location); if (before >= value) return; }
        while (Interlocked.CompareExchange(ref location, value, before) != before);
    }

    public void Dispose()
    {
        _stopped = true; Interlocked.Exchange(ref _destination, null);
        if (Thread.CurrentThread != _thread) _thread.Join(1000);
    }
}
