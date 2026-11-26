using System.Diagnostics;
using System.Runtime.InteropServices;
using Streemu.Media;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
if (args.Contains("--media")) return await MediaCheck.Run(args);
if (args.Contains("--telemetria")) return await TelemetryCheck.Run();

var failures = 0;
void Check(string name, Action test)
{
    try { test(); Console.WriteLine("OK    " + name); }
    catch (Exception e) { failures++; Console.WriteLine("FALHA " + name + ": " + e.Message); }
}
void Require(bool value, string why) { if (!value) throw new Exception(why); }

Check("fila-saudavel-preserva-pcm", () =>
{
    var b = new LiveAudioBuffer(400, 4);
    byte[] input = Enumerable.Range(1, 200).Select(i => (byte)i).ToArray(), result = new byte[200];
    b.Push(input); b.Read(result, result.Length);
    Require(input.SequenceEqual(result) && b.Counts.Dropped == 0, "Alterou som sem sobrecarga.");
});
Check("sobrecarga-preserva-som-recente", () =>
{
    var b = new LiveAudioBuffer(40, 4);
    b.Push(Enumerable.Repeat((byte)1, 32).ToArray()); b.Push(Enumerable.Repeat((byte)2, 32).ToArray());
    var result = new byte[40]; b.Read(result, 40);
    Require(result.Take(8).All(x => x == 1) && result.Skip(8).All(x => x == 2) && b.Counts.Dropped == 24, "Preservou som velho ou perdeu duração.");
});
Check("retomada-buraco-antes-do-som", () =>
{
    var b = new LiveAudioBuffer(400, 4); b.Push(Enumerable.Repeat((byte)7, 200).ToArray());
    var result = new List<byte>();
    for (int behind = 800; behind > 0; behind -= 40) { var chunk = new byte[40]; b.Read(chunk, behind); result.AddRange(chunk); }
    Require(result.Take(600).All(x => x == 0) && result.Skip(600).All(x => x == 7), "Som recente foi encaixado no começo do atraso.");
});
Check("pacote-enorme-e-alinhamento", () =>
{
    var b = new LiveAudioBuffer(40, 4); b.Push(Enumerable.Range(0, 101).Select(i => (byte)i).ToArray());
    var result = new byte[40]; b.Read(result, 40);
    Require(result.SequenceEqual(Enumerable.Range(60, 40).Select(i => (byte)i)), "Não limitou/alinhar a fila.");
});
Check("falha-do-pipe-antigo-nao-desliga-o-novo", () =>
{
    using var old = new BlockingSink(); using var next = new CollectSink(); using var p = new LiveAudioPacer();
    p.Attach(old); Require(old.Entered.Wait(2000), "Não entrou na escrita antiga.");
    p.Attach(next); p.Push(new byte[AudioPump.BytesPerSecond / 10]); old.Release.Set();
    Require(SpinWait.SpinUntil(() => next.Count > 0, 2000), "A falha antiga desligou o destino novo.");
    Require(p.Snapshot()["pipe_ligado"]!.GetValue<bool>(), "O destino novo ficou desligado.");
});
Check("pacer-se-recupera-sem-reabrir-stream", () =>
{
    // Bloqueia a 2ª escrita: o relógio só começa depois da 1ª escrita aceita (um ffmpeg novo que ainda abre não gera dívida).
    using var sink = new BlockingSink(throws: false, blockAt: 2); using var p = new LiveAudioPacer();
    p.Attach(sink); Require(sink.Entered.Wait(2000), "Não bloqueou.");
    for (int i = 0; i < 40; i++) { p.Push(new byte[AudioPump.BytesPerSecond / 50]); Thread.Sleep(20); }
    sink.Release.Set();
    Require(SpinWait.SpinUntil(() => p.Snapshot()["retomadas_apos_bloqueio"]!.GetValue<long>() > 0, 3000), "Não recuperou o relógio.");
    var s = p.Snapshot();
    Require(s["epoca"]!.GetValue<int>() == 1 && s["escritas_bloqueadas"]!.GetValue<long>() > 0, "Recuperou trocando de stream.");
});
Check("fila-de-conteudo-persistente-se-realinha-sem-trocar-pipe", () =>
{
    using var sink = new CollectSink(); using var p = new LiveAudioPacer(); p.Attach(sink);
    var clock = Stopwatch.StartNew(); var packet = new byte[AudioPump.BytesPerSecond / 25];
    // Produção propositalmente duas vezes mais rápida: fila cheia com relógio do pipe em dia.
    while (clock.Elapsed.TotalSeconds < 6 && p.Snapshot()["correcoes_fila_persistente"]!.GetValue<long>() == 0) { p.Push(packet); Thread.Sleep(20); }
    var snapshot = p.Snapshot();
    Require(snapshot["correcoes_fila_persistente"]!.GetValue<long>() == 1 && snapshot["epoca"]!.GetValue<int>() == 1,
        "Não corrigiu a fila, corrigiu em repetição ou trocou o pipe.");
});
void RealTimeSinkCase(SlowOpenSink sink, bool expectReanchor)
{
    using var p = new LiveAudioPacer(); p.Attach(sink);
    var clock = Stopwatch.StartNew(); long pushed = 0; var packet = new byte[AudioPump.BytesPerSecond / 50]; MemoryMarshal.Cast<byte, float>(packet.AsSpan()).Fill(0.5f);
    long zeroMid = 0, silenceMid = 0, droppedMid = 0, realMid = 0; bool mid = false;
    while (clock.Elapsed.TotalSeconds < 8)
    {
        while (pushed * 1000 / AudioPump.BytesPerSecond < clock.ElapsedMilliseconds) { p.Push(packet); pushed += packet.Length; }
        if (!mid && clock.Elapsed.TotalSeconds >= 5)
        {
            mid = true; var m = p.Snapshot(); (zeroMid, realMid) = (sink.Zero, sink.Real);
            silenceMid = m["silencio_inserido_ms"]!.GetValue<long>(); droppedMid = m["descartado_ms"]!.GetValue<long>();
        }
        Thread.Sleep(5);
    }
    var e = p.Snapshot(); long zero = sink.Zero - zeroMid, real = sink.Real - realMid;
    long silence = e["silencio_inserido_ms"]!.GetValue<long>() - silenceMid, dropped = e["descartado_ms"]!.GetValue<long>() - droppedMid;
    Console.WriteLine($"      últimos 3 s: som real {real * 1000 / AudioPump.BytesPerSecond} ms, zeros {zero * 1000 / AudioPump.BytesPerSecond} ms, silêncio inserido {silence} ms, descartado {dropped} ms, relógio reancorado {e["relogio_reancorado"]}");
    Require(real > zero * 9, $"Depois da abertura o som virou silêncio (real {real}, zeros {zero}).");
    Require(silence < 300 && dropped < 300, $"Silêncio/descarte continuam subindo (silêncio {silence} ms, descartado {dropped} ms).");
    if (expectReanchor) Require(e["relogio_reancorado"]!.GetValue<long>() >= 1, "A dívida persistente não re-ancorou o relógio.");
}
// Alt+Tab real (2026-11-17): o ffmpeg novo só lê o stdin depois de ~1,3 s e então consome em tempo real.
Check("ffmpeg-novo-que-demora-a-ler-nao-vira-silencio-permanente", () =>
{
    using var sink = new SlowOpenSink(TimeSpan.FromMilliseconds(1300), periodicStallEvery: 50, periodicStall: TimeSpan.FromMilliseconds(150));
    RealTimeSinkCase(sink, expectReanchor: false);
});
// A 1ª escrita cabe no buffer do pipe (não bloqueia); a dívida nasce depois, de uma parada no meio, e o ffmpeg
// segue em tempo real com travadas de ~150 ms (~3 escritas bloqueadas/s no campo): exercita a re-ancoragem.
Check("divida-persistente-com-escritas-lentas-reancora-o-relogio", () =>
{
    using var sink = new SlowOpenSink(TimeSpan.Zero, stallAt: 10, stall: TimeSpan.FromMilliseconds(1500), periodicStallEvery: 50, periodicStall: TimeSpan.FromMilliseconds(150));
    RealTimeSinkCase(sink, expectReanchor: true);
});

Check("pts-wrap-e-pacotes-invalidos", () =>
{
    Require(TsAvMeter.Difference(90, (1L << 33) - 90) == 180, "Wrap virou atraso de 26 horas.");
    Require(TsAvMeter.Difference((1L << 33) - 90, 90) == -180, "Wrap negativo incorreto.");
    var packet = Packet(0x100, (1L << 33) - 225);
    Require(TsAvMeter.TryPts(packet, out int pid, out long pts) && pid == 0x100 && pts == (1L << 33) - 225, "Leu PTS errado.");
    packet[4] = 184; packet[3] = 0x30;
    Require(!TsAvMeter.TryPts(packet, out _, out _), "Aceitou adaptação fora dos limites.");
    packet = Packet(0x101, 100); packet[1] |= 0x80;
    Require(!TsAvMeter.TryPts(packet, out _, out _), "Aceitou pacote com erro de transporte.");
});
Check("ts-sem-audio-sem-avanco-e-transicao", () =>
{
    var meter = new TsAvMeter(); meter.BeginEpoch();
    for (int i = 0; i < 4; i++) meter.Observe(Packet(0x100, i * 1500));
    Require(meter.Snapshot()["audio_menos_video_ms"] == null, "Inventou medida de áudio inexistente.");
    for (int i = 0; i < 4; i++) { meter.Observe(Packet(0x100, i * 1500)); meter.Observe(Packet(0x101, i * 1500 + 90)); }
    Require(meter.Snapshot()["audio_menos_video_ms"]!.GetValue<int>() == 1, "Não mediu os dois fluxos.");
    meter.BeginEpoch();
    Require(meter.Snapshot()["audio_menos_video_ms"] == null && meter.Snapshot()["transicao"]!.GetValue<bool>(), "Misturou épocas.");
});
Check("player-parser-sem-audio-nan-cultura-e-sinais", () =>
{
    var saved = System.Globalization.CultureInfo.CurrentCulture;
    try
    {
        System.Globalization.CultureInfo.CurrentCulture = new("pt-BR");
        Require(ViewerSync.TryStatus(Status(10, -1.02), out _, out int d, out _, out _, out _) && d == -1020, "Sinal/unidade/cultura incorretos.");
        Require(ViewerSync.TryStatus(Status(10, 1), out _, out d, out _, out _, out _) && d == 1000, "Ignorou áudio adiantado.");
        Require(!ViewerSync.TryStatus(Status(10, 0).Replace("0.000", "nan"), out _, out _, out _, out _, out _), "NaN virou zero.");
        Require(!ViewerSync.TryStatus(Status(10, 0).Replace("A-V:", "M-V:"), out _, out _, out _, out _, out _), "Sem áudio virou sincronizado.");
    }
    finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
});
Check("protecao-persistencia-dois-sinais-limites-e-confirmacao", () =>
{
    foreach (int sign in new[] { -1, 1 })
    {
        var guard = new LiveSyncGuard(); long now = Stopwatch.GetTimestamp();
        bool Sample(int? d, bool eligible = true, int seconds = 1) { now += Stopwatch.Frequency * seconds; return guard.Observe(d, eligible, now); }
        Require(!Sample(1000 * sign), "Corrigiu na primeira amostra.");
        Sample(null); Sample(1000 * sign, false);
        for (int i = 0; i < 5; i++) Require(!Sample(1000 * sign), "Não esperou 5 s.");
        Require(Sample(1000 * sign), "Não protegeu descompasso persistente.");
        for (int i = 0; i < 3; i++) Require(!Sample(0), "Corrigiu stream saudável.");
        Require(guard.Snapshot()["recuperacoes_verificadas"]!.GetValue<int>() == 1, "Não verificou o resultado.");
        for (int i = 0; i < 50; i++) Require(!Sample(1000 * sign), "Repetiu antes do intervalo mínimo.");
        bool second = false;
        for (int i = 0; i < 10; i++) second |= Sample(1000 * sign);
        Require(second, "Não permitiu segunda recuperação após o intervalo.");
        for (int i = 0; i < 200; i++) Require(!Sample(1000 * sign), "Entrou em repetição sem limite.");
    }
});
Check("protecao-nao-confunde-jitter-perda-de-medida-e-wrap", () =>
{
    var guard = new LiveSyncGuard(); long now = Stopwatch.GetTimestamp();
    for (int i = 0; i < 100; i++)
    {
        now += Stopwatch.Frequency;
        Require(!guard.Observe(i % 2 == 0 ? 1000 : -1000, true, now), "Jitter alternado disparou.");
    }
    for (int i = 0; i < 20; i++)
    {
        now += Stopwatch.Frequency * 4;
        Require(!guard.Observe(1000, true, now), "Amostras esparsas dispararam.");
    }
    for (int i = 0; i < 20; i++) { now += Stopwatch.Frequency; Require(!guard.Observe(int.MaxValue, true, now), "Tempo inválido/wrap disparou."); }
});
Check("telemetria-grava-lote-isolado-e-mostra-falha-de-disco", () =>
{
    string testDir = Path.Combine(Path.GetTempPath(), "streemu-sincronia-tests-" + Guid.NewGuid().ToString("N"));
    string? before = Environment.GetEnvironmentVariable("STREEMU_TEST_DIR"); Environment.SetEnvironmentVariable("STREEMU_TEST_DIR", testDir);
    try
    {
        var session = Streemu.Telemetria.SessaoLocal.Criar("sessao-sincronia-teste", null, naoEnviar: true);
        using (var series = new Streemu.Telemetria.SerieSincronia(session))
            for (int i = 0; i < 5; i++) series.Add("live-teste", "ligacao-teste", "assiste", new() { ["delta_ms"] = i });
        Streemu.Telemetria.Gravador.Descarregar(TimeSpan.FromSeconds(10)); // a gravação é assíncrona (etapa 8)
        string file = Path.Combine(session.Dir, session.ArquivoSincronia); // uma série por live: sincronia-1.ndjson
        Require(File.ReadAllLines(file).Length == 5 && session.Arquivos().Any(x => x.Nome == Streemu.Telemetria.SessaoLocal.NomeSincronia(1)), "Perdeu lote final ou não incluiu no envio.");
        var broken = Streemu.Telemetria.SessaoLocal.Criar("sessao-disco-teste", null, naoEnviar: true);
        Directory.CreateDirectory(Path.Combine(broken.Dir, broken.ArquivoSincronia));
        using (var series = new Streemu.Telemetria.SerieSincronia(broken)) series.Add(null, "codigo", "assiste", new() { ["medida"] = "sem_medida_recente" });
        Streemu.Telemetria.Gravador.Descarregar(TimeSpan.FromSeconds(10));
        Require(!string.IsNullOrWhiteSpace(broken.FalhaLocal), "Falha de disco ficou invisível.");
    }
    finally { Environment.SetEnvironmentVariable("STREEMU_TEST_DIR", before); }
});
Check("player-persistencia-pausa-recuperacao-e-medida-velha", () =>
{
    long now = Stopwatch.GetTimestamp(); var monitor = new ViewerSync(() => now);
    void Sample(int sec, double delta, double? clock = null) { now += Stopwatch.Frequency; monitor.Observe(Status(clock ?? sec, delta)); }
    Sample(1, 0); Sample(2, 1); Sample(3, 0);
    Require(monitor.Snapshot()["incidentes_persistentes"]!.GetValue<int>() == 0, "Um pico virou incidente.");
    for (int i = 4; i <= 9; i++) Sample(i, -1);
    Require(monitor.Snapshot()["incidentes_persistentes"]!.GetValue<int>() == 1, "Não reconheceu atraso persistente.");
    Sample(10, 0); Sample(11, 0.25); Sample(12, 0); Sample(13, 0);
    Require(monitor.Snapshot()["recuperacoes_observadas"]!.GetValue<int>() == 0, "Somou medidas boas separadas por descompasso.");
    Sample(14, 0);
    Require(monitor.Snapshot()["recuperacoes_observadas"]!.GetValue<int>() == 1, "Não verificou a recuperação.");
    for (int i = 15; i <= 21; i++) Sample(i, 2, 14);
    Require(monitor.Snapshot()["incidentes_persistentes"]!.GetValue<int>() == 1 && monitor.Snapshot()["audio_menos_video_ms"] == null, "Pausa intencional gerou incidente.");
    now += Stopwatch.Frequency * 4;
    Require(monitor.Snapshot()["audio_menos_video_ms"] == null, "Reutilizou medida velha.");
});
Check("custo-medidor-ts-sem-alocacao-por-pacote", () =>
{
    var meter = new TsAvMeter(); meter.BeginEpoch();
    var batch = Enumerable.Range(0, 700).SelectMany(i => Packet(i % 2 == 0 ? 0x100 : 0x101, i * 900)).ToArray();
    meter.Observe(batch); long allocated = GC.GetAllocatedBytesForCurrentThread();
    using var proc = Process.GetCurrentProcess(); var cpu = proc.TotalProcessorTime; var sw = Stopwatch.StartNew();
    for (int i = 0; i < 2000; i++) meter.Observe(batch);
    long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
    Console.WriteLine($"      1,4 milhão de pacotes: {sw.Elapsed.TotalMilliseconds:0.0} ms, CPU {(proc.TotalProcessorTime - cpu).TotalMilliseconds:0.0} ms, alocação {bytes} bytes (inclui medição do processo).");
    Require(bytes < 100_000, "Alocou por pacote.");
});
return failures == 0 ? 0 : 1;

static string Status(double clock, double delta) => FormattableString.Invariant($"[info] {clock:0.00} A-V: {delta:0.000} fd= 3 aq= 8KB vq= 60KB sq= 0B");
static byte[] Packet(int pid, long pts)
{
    var p = new byte[188]; p[0] = 0x47; p[1] = (byte)(0x40 | (pid >> 8)); p[2] = (byte)pid; p[3] = 0x10;
    p[6] = 1; p[7] = 0xe0; p[10] = 0x80; p[11] = 0x80; p[12] = 5;
    p[13] = (byte)(0x21 | ((pts >> 30) & 7) << 1); p[14] = (byte)(pts >> 22); p[15] = (byte)(((pts >> 15) & 127) << 1 | 1);
    p[16] = (byte)(pts >> 7); p[17] = (byte)((pts & 127) << 1 | 1); return p;
}
sealed class BlockingSink(bool throws = true, int blockAt = 1) : MemoryStream
{
    public readonly ManualResetEventSlim Entered = new(), Release = new();
    int _first;
    public override void Write(ReadOnlySpan<byte> data)
    {
        if (Interlocked.Increment(ref _first) == blockAt) { Entered.Set(); if (!Release.Wait(5000)) throw new TimeoutException(); if (throws) throw new IOException("pipe antigo"); }
        base.Write(data);
    }
    protected override void Dispose(bool disposing) { Release.Set(); base.Dispose(disposing); }
}
sealed class CollectSink : Stream
{
    long _count;
    public long Count => Interlocked.Read(ref _count);
    public override void Write(ReadOnlySpan<byte> b) => Interlocked.Add(ref _count, b.Length);
    public override void Write(byte[] b, int o, int n) => Interlocked.Add(ref _count, n);
    public override void Flush() { }
    public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
    public override long Length => Count; public override long Position { get => Count; set => throw new NotSupportedException(); }
    public override int Read(byte[] b, int o, int n) => throw new NotSupportedException();
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long n) => throw new NotSupportedException();
}

/// <summary>ffmpeg que abre devagar (1ª escrita bloqueada) e depois lê só no ritmo do tempo real, como o ddagrab ao vivo; opcionalmente trava de vez em quando.</summary>
sealed class SlowOpenSink(TimeSpan openTime, int stallAt = 0, TimeSpan stall = default, int periodicStallEvery = 0, TimeSpan periodicStall = default) : Stream
{
    long _zero, _real; int _writes; Stopwatch? _clock; double _consumed;
    public long Zero => Interlocked.Read(ref _zero);
    public long Real => Interlocked.Read(ref _real);
    void Restart() { _clock = Stopwatch.StartNew(); _consumed = 0; }
    public override void Write(ReadOnlySpan<byte> b)
    {
        int n = Interlocked.Increment(ref _writes);
        if (n == 1) { Thread.Sleep(openTime); Restart(); }
        else
        {
            if (n == stallAt) { Thread.Sleep(stall); Restart(); }
            else if (periodicStallEvery > 0 && n % periodicStallEvery == 0) Thread.Sleep(periodicStall); // sem Restart: depois da travada o ffmpeg alcança a agenda
            // Agenda absoluta: Thread.Sleep(20) dorme ~31 ms no Windows e viraria um leitor mais lento que o tempo real.
            _consumed += b.Length * 1000.0 / AudioPump.BytesPerSecond;
            while (_clock!.Elapsed.TotalMilliseconds < _consumed) Thread.Sleep(1);
        }
        // 0,5f tem 3 de 4 bytes zerados: conta por amostra (float), não por byte.
        int zeros = 0; foreach (var f in MemoryMarshal.Cast<byte, float>(b)) if (f == 0f) zeros += 4;
        Interlocked.Add(ref _zero, zeros); Interlocked.Add(ref _real, b.Length - zeros);
    }
    public override void Write(byte[] b, int o, int n) => Write(b.AsSpan(o, n));
    public override void Flush() { }
    public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
    public override long Length => 0; public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override int Read(byte[] b, int o, int n) => throw new NotSupportedException();
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long n) => throw new NotSupportedException();
}
