using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Streemu.Telemetria;

/// <summary>
/// Gerador de carga: grava eventos e linhas de log em ritmo normal e 10x maior e mede o custo do envio. O servidor falso roda em OUTRO processo,
/// então a CPU e a memória medidas são do app (gerador + envio). A rodada do envio é a mesma <c>Envio.Rodada</c> (chamada por reflexão, a cada
/// 5 s como no ritmo ativo, numa thread de prioridade baixa como a "telemetria"); só o agendamento é do teste, para poder cronometrar cada rodada.
/// </summary>
static class Carga
{
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] static extern bool GetThreadTimes(IntPtr h, out long criada, out long saiu, out long kernel, out long usuario);
    [DllImport("kernel32.dll")] static extern bool GetProcessIoCounters(IntPtr h, out IoCounters c);
    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters { public ulong Reads, Writes, Other, ReadBytes, WriteBytes, OtherBytes; }

    sealed record Fase(string Nome, int EventosPorSeg, int LinhasPorSeg);

    public static void Rodar(string tmp, int segundos, string? saida)
    {
        var fases = new[] { new Fase("normal", 5, 20), new Fase("x10", 50, 200) };
        var resultados = new JsonArray();
        foreach (var f in fases)
        {
            using var filho = Servidor.Iniciar(); // servidor novo por fase: os números de evento recomeçam e o resumo não mistura
            var r = Medir(f, segundos, filho, tmp);
            resultados.Add(r);
            Console.WriteLine(Resumo(f, r));
        }
        var doc = new JsonObject
        {
            ["quando"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["maquina"] = new JsonObject { ["cpu"] = NomeCpu(), ["nucleos_logicos"] = Environment.ProcessorCount, ["so"] = RuntimeInformation.OSDescription, ["dotnet"] = RuntimeInformation.FrameworkDescription },
            ["segundos_por_fase"] = segundos,
            ["fases"] = resultados,
        };
        string json = doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        string arq = Path.Combine(tmp, "carga-resultado.json");
        File.WriteAllText(arq, json, new UTF8Encoding(false));
        Console.WriteLine("resultado da carga: " + arq);
        if (saida != null) { File.WriteAllText(saida, json, new UTF8Encoding(false)); Console.WriteLine("copiado para: " + saida); }
    }

    static JsonObject Medir(Fase fase, int segundos, Servidor srv, string tmp)
    {
        string id = $"20261116-{DateTime.Now:HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        string pastaLog = Path.Combine(tmp, "logs");
        Directory.CreateDirectory(pastaLog);
        string log = Path.Combine(pastaLog, $"sessao-{id}.log");
        File.WriteAllText(log, "");
        var s = SessaoLocal.Criar(id, log, naoEnviar: false);
        using var envio = new Envio($"http://127.0.0.1:{srv.Porta}", s, () => new Identidade(null, null, "carga", "instalacao-de-teste")) { Token = _ => ServidorFalso.TokenPadrao };
        var rodada = typeof(Envio).GetMethod("Rodada", BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new Exception("Envio.Rodada não existe mais: ajuste a carga.");

        var carimbos = new ConcurrentDictionary<long, long>();
        var duracoes = new List<double>();
        int rodadasFalhas = 0;
        long cpuEnvio = 0, alocEnvio = 0, cpuGerador = 0, alocGerador = 0;
        long eventosGerados = 0, linhasGeradas = 0;
        var parada = new CancellationTokenSource();

        var proc = Process.GetCurrentProcess();
        GetProcessIoCounters(proc.Handle, out var io0);
        var cpu0 = proc.TotalProcessorTime;
        long gc0 = GC.GetTotalAllocatedBytes(false);
        var relogio = Stopwatch.StartNew();

        var gerador = new Thread(() =>
        {
            long aloc0 = GC.GetAllocatedBytesForCurrentThread();
            using var w = new StreamWriter(new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };
            double devEv = 0, devLin = 0;
            var tick = Stopwatch.StartNew();
            long ultimo = 0, seq = 0, n = 0;
            while (!parada.IsCancellationRequested)
            {
                long agora = tick.ElapsedMilliseconds;
                double dt = (agora - ultimo) / 1000.0;
                ultimo = agora;
                devEv += fase.EventosPorSeg * dt; devLin += fase.LinhasPorSeg * dt;
                for (; devLin >= 1; devLin--) { w.Write($"12:00:00.000 [carga] linha {++n} texto neutro de duração média para o log do teste de carga do envio\n"); linhasGeradas++; }
                for (; devEv >= 1; devEv--)
                {
                    seq++;
                    carimbos[seq] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    s.Evento("carga", new JsonObject { ["n"] = seq, ["dados"] = "x1234567890abcdefghij-valor-de-exemplo-do-evento" });
                    eventosGerados++;
                }
                Thread.Sleep(100);
            }
            cpuGerador = CpuAtual(); alocGerador = GC.GetAllocatedBytesForCurrentThread() - aloc0;
        }) { Name = "carga-gerador" };

        var remetente = new Thread(() =>
        {
            long aloc0 = GC.GetAllocatedBytesForCurrentThread();
            var prox = Stopwatch.StartNew();
            int volta = 0;
            while (true)
            {
                bool fim = parada.IsCancellationRequested;
                var t = Stopwatch.StartNew();
                bool ok;
                try { ok = (bool)rodada.Invoke(envio, new object[] { TimeSpan.FromSeconds(20) })!; }
                catch (Exception) { ok = false; }
                lock (duracoes) duracoes.Add(t.Elapsed.TotalMilliseconds);
                if (!ok) rodadasFalhas++;
                if (fim && Pronto(s)) break;
                if (fim && ++volta > 20) break;
                if (!fim) { int resta = 5000 - (int)(prox.ElapsedMilliseconds % 5000); Thread.Sleep(resta); }
            }
            cpuEnvio = CpuAtual(); alocEnvio = GC.GetAllocatedBytesForCurrentThread() - aloc0;
        }) { Name = "carga-remetente", Priority = ThreadPriority.BelowNormal };

        gerador.Start(); remetente.Start();
        Thread.Sleep(segundos * 1000);
        parada.Cancel();
        gerador.Join(); remetente.Join();
        relogio.Stop();
        proc.Refresh();
        var cpuProc = proc.TotalProcessorTime - cpu0;
        GetProcessIoCounters(proc.Handle, out var io1);
        long alocProc = GC.GetTotalAllocatedBytes(false) - gc0;
        envio.Fechar(TimeSpan.FromSeconds(2));

        var resumo = srv.Resumo();
        var latencias = new List<double>();
        foreach (var (n, ms) in resumo.Eventos) if (carimbos.TryGetValue(n, out var c0)) latencias.Add(ms - c0);
        latencias.Sort();
        double Perc(double p) => latencias.Count == 0 ? 0 : latencias[Math.Min(latencias.Count - 1, (int)(p * latencias.Count))];
        Gravador.Descarregar(TimeSpan.FromSeconds(10)); // o que ainda estava na fila de gravação chega ao disco antes de medir o tamanho
        long tamLog = new FileInfo(log).Length, tamEv = new FileInfo(Path.Combine(s.Dir, SessaoLocal.Eventos)).Length;
        double[] dur; lock (duracoes) dur = duracoes.ToArray();
        double dur_s = relogio.Elapsed.TotalSeconds;

        return new JsonObject
        {
            ["fase"] = fase.Nome, ["eventos_por_s"] = fase.EventosPorSeg, ["linhas_log_por_s"] = fase.LinhasPorSeg, ["duracao_s"] = Math.Round(dur_s, 1),
            ["eventos_gerados"] = eventosGerados, ["linhas_geradas"] = linhasGeradas,
            ["eventos_vistos_pelo_servidor"] = resumo.Eventos.Count, ["eventos_sem_confirmacao"] = eventosGerados - resumo.Eventos.Count,
            ["arquivos_gravados_bytes"] = tamLog + tamEv, ["io_escrito_processo_bytes"] = (long)(io1.WriteBytes - io0.WriteBytes),
            ["bytes_enviados_envio"] = envio.BytesEnviados, ["bytes_recebidos_servidor"] = resumo.BytesRecebidos,
            ["pedidos"] = resumo.Pedidos, ["anomalias_servidor"] = resumo.Anomalias,
            ["rodadas"] = dur.Length, ["rodadas_com_falha"] = rodadasFalhas,
            ["rodada_media_ms"] = Math.Round(dur.Length == 0 ? 0 : dur.Average(), 1), ["rodada_maxima_ms"] = Math.Round(dur.Length == 0 ? 0 : dur.Max(), 1),
            ["evento_ate_confirmar_media_ms"] = Math.Round(latencias.Count == 0 ? 0 : latencias.Average(), 0),
            ["evento_ate_confirmar_p95_ms"] = Math.Round(Perc(0.95), 0), ["evento_ate_confirmar_max_ms"] = Math.Round(latencias.Count == 0 ? 0 : latencias.Max(), 0),
            ["cpu_processo_ms"] = Math.Round(cpuProc.TotalMilliseconds), ["cpu_processo_pct_de_um_nucleo"] = Math.Round(cpuProc.TotalMilliseconds / 10.0 / dur_s, 2),
            ["cpu_thread_envio_ms"] = Math.Round(cpuEnvio / 10_000.0), ["cpu_thread_envio_pct_de_um_nucleo"] = Math.Round(cpuEnvio / 10_000.0 / 10.0 / dur_s, 2),
            ["cpu_thread_gerador_ms"] = Math.Round(cpuGerador / 10_000.0),
            ["alocado_processo_bytes"] = alocProc, ["alocado_thread_envio_bytes"] = alocEnvio, ["alocado_thread_gerador_bytes"] = alocGerador,
            ["cursores_em_dia"] = Pronto(s),
        };
    }

    static bool Pronto(SessaoLocal s) =>
        s.Arquivos().All(a => s.Cursor(a.Nome) >= new FileInfo(a.Caminho).Length) && s.E.Indexados >= new FileInfo(Path.Combine(s.Dir, SessaoLocal.Eventos)).Length;

    /// <summary>CPU (kernel + usuário) da thread que chama, em unidades de 100 ns.</summary>
    static long CpuAtual() => GetThreadTimes(GetCurrentThread(), out _, out _, out long k, out long u) ? k + u : 0;

    static string Resumo(Fase f, JsonObject r) =>
        $"carga {f.Nome} ({f.EventosPorSeg} ev/s, {f.LinhasPorSeg} linhas/s, {r["duracao_s"]} s): enviado {Kb(r["bytes_enviados_envio"])} em {r["pedidos"]} pedidos, " +
        $"rodada média {r["rodada_media_ms"]} ms / máx {r["rodada_maxima_ms"]} ms, evento→confirmação média {r["evento_ate_confirmar_media_ms"]} ms / p95 {r["evento_ate_confirmar_p95_ms"]} ms / máx {r["evento_ate_confirmar_max_ms"]} ms, " +
        $"CPU do envio {r["cpu_thread_envio_pct_de_um_nucleo"]}% de um núcleo (processo todo {r["cpu_processo_pct_de_um_nucleo"]}%), alocou {Kb(r["alocado_thread_envio_bytes"])} no envio, " +
        $"gravou {Kb(r["arquivos_gravados_bytes"])} (E/S do processo {Kb(r["io_escrito_processo_bytes"])}), sem confirmação: {r["eventos_sem_confirmacao"]} evento(s), em dia: {r["cursores_em_dia"]}";

    static string Kb(JsonNode? b) => $"{(b?.GetValue<long>() ?? 0) / 1024.0:0.#} KB";

    static string NomeCpu()
    {
        try { using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"); return (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "?"; }
        catch { return "?"; }
    }

    /// <summary>Processo filho com o servidor falso; o resumo vem pelo GET /_ctl/resumo.</summary>
    sealed class Servidor : IDisposable
    {
        readonly Process _p;
        public int Porta { get; }
        public sealed record ResumoServidor(int Pedidos, long BytesRecebidos, int Anomalias, List<(long N, long Ms)> Eventos);

        Servidor(Process p, int porta) { _p = p; Porta = porta; }

        public static Servidor Iniciar()
        {
            var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--servidor-falso")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            }) ?? throw new Exception("não consegui iniciar o servidor falso");
            string? l = p.StandardOutput.ReadLine();
            if (l == null || !l.StartsWith("PORTA=")) { try { p.Kill(); } catch { } throw new Exception("servidor falso não informou a porta: " + l); }
            return new Servidor(p, int.Parse(l[6..]));
        }

        public ResumoServidor Resumo()
        {
            using var h = new HttpClient();
            var j = JsonNode.Parse(h.GetStringAsync($"http://127.0.0.1:{Porta}/_ctl/resumo").GetAwaiter().GetResult())!;
            var ev = j["eventos"]!.AsArray().Select(x => (x![0]!.GetValue<long>(), x[1]!.GetValue<long>())).ToList();
            return new ResumoServidor(j["pedidos"]!.GetValue<int>(), j["bytes_recebidos"]!.GetValue<long>(), j["anomalias"]!.GetValue<int>(), ev);
        }

        public void Dispose()
        {
            try { _p.StandardInput.Close(); if (!_p.WaitForExit(3000)) _p.Kill(); } catch { }
            _p.Dispose();
        }
    }
}
