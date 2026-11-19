using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Streemu.Telemetria;

/// <summary>
/// O que a própria telemetria custa neste PC, medido por ela mesma: critério de aceitação da spec 009 ("não pesar") com número de campo, não só
/// de bancada. Contadores baratos (um <c>Interlocked</c> por lote ou por rodada; nada de WMI, nada de disco). A cada 5 min o <see cref="Envio"/>
/// grava o evento <c>telemetria_custo</c> com a diferença desde o anterior: quanto tempo a thread de disco e a de envio ficaram trabalhando
/// (relógio de parede; a rede é contada à parte, porque esperar a resposta do servidor não gasta CPU), quantos bytes, o maior tamanho da fila,
/// quantos itens foram descartados e, para comparar, a CPU e a memória alocada pelo app inteiro no mesmo intervalo.
/// </summary>
public static class Custo
{
    static long _gravItens, _gravBytes, _gravLotes, _gravTicks, _filaMax, _descartes;
    static long _montarTicks, _redeTicks, _rodadas, _falhas, _bytesEnviados;
    static readonly object _g = new();
    static DateTime _desde = DateTime.UtcNow;
    static TimeSpan _cpuAntes = TimeSpan.Zero;
    static long _alocAntes;
    static bool _iniciado;

    internal static void Gravou(int itens, long bytes, int lotes, long ticks)
    {
        Interlocked.Add(ref _gravItens, itens); Interlocked.Add(ref _gravBytes, bytes); Interlocked.Add(ref _gravLotes, lotes); Interlocked.Add(ref _gravTicks, ticks);
    }
    internal static void FilaViu(int tamanho)
    {
        long atual;
        while (tamanho > (atual = Interlocked.Read(ref _filaMax))) if (Interlocked.CompareExchange(ref _filaMax, tamanho, atual) == atual) break;
    }
    internal static void Descartou() => Interlocked.Increment(ref _descartes);
    internal static void Montou(long ticks) => Interlocked.Add(ref _montarTicks, ticks);
    internal static void Rede(long ticks, long bytes, bool ok) { Interlocked.Add(ref _redeTicks, ticks); Interlocked.Add(ref _bytesEnviados, bytes); Interlocked.Increment(ref _rodadas); if (!ok) Interlocked.Increment(ref _falhas); }

    static double Ms(long ticks) => Math.Round(ticks * 1000.0 / Stopwatch.Frequency, 1);

    /// <summary>
    /// O custo desde a última chamada (e zera os contadores), no formato do evento <c>telemetria_custo</c>. Só números: nada de caminho, nome ou
    /// conteúdo. A primeira chamada mede desde que o app abriu.
    /// </summary>
    public static JsonObject Delta()
    {
        lock (_g)
        {
            var agora = DateTime.UtcNow;
            double periodoMs = Math.Max(1, (agora - _desde).TotalMilliseconds);
            long gravTicks = Interlocked.Exchange(ref _gravTicks, 0), montarTicks = Interlocked.Exchange(ref _montarTicks, 0), redeTicks = Interlocked.Exchange(ref _redeTicks, 0);
            TimeSpan cpu = TimeSpan.Zero;
            try { using var p = Process.GetCurrentProcess(); cpu = p.TotalProcessorTime; } catch { }
            long aloc = GC.GetTotalAllocatedBytes(false);
            double cpuMs = _iniciado ? (cpu - _cpuAntes).TotalMilliseconds : cpu.TotalMilliseconds;
            double alocMb = (_iniciado ? aloc - _alocAntes : aloc) / 1048576.0;
            _cpuAntes = cpu; _alocAntes = aloc; _iniciado = true; _desde = agora;
            double ocupadoMs = Ms(gravTicks) + Ms(montarTicks);
            return new JsonObject
            {
                ["periodo_s"] = Math.Round(periodoMs / 1000.0, 1),
                ["gravacao"] = new JsonObject
                {
                    ["itens"] = Interlocked.Exchange(ref _gravItens, 0), ["lotes"] = Interlocked.Exchange(ref _gravLotes, 0), ["bytes"] = Interlocked.Exchange(ref _gravBytes, 0),
                    ["ms"] = Ms(gravTicks), ["fila_max"] = Interlocked.Exchange(ref _filaMax, 0), ["descartados"] = Interlocked.Exchange(ref _descartes, 0),
                },
                ["envio"] = new JsonObject
                {
                    ["rodadas"] = Interlocked.Exchange(ref _rodadas, 0), ["falhas"] = Interlocked.Exchange(ref _falhas, 0), ["bytes_enviados"] = Interlocked.Exchange(ref _bytesEnviados, 0),
                    ["montar_ms"] = Ms(montarTicks), ["rede_ms"] = Ms(redeTicks),
                },
                // trabalho da telemetria que gasta CPU e disco (gravar + montar/comprimir), em % do intervalo, para comparar com o limite de aceitação
                ["ocupada_pct"] = Math.Round(ocupadoMs * 100.0 / periodoMs, 3),
                ["app"] = new JsonObject { ["cpu_ms"] = Math.Round(cpuMs, 0), ["alocado_mb"] = Math.Round(alocMb, 1) },
            };
        }
    }
}
