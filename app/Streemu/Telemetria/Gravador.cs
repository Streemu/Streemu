using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Streemu.Telemetria;

/// <summary>
/// Quem de fato escreve no disco o que a telemetria registra (eventos, séries, perfil, relatórios). Antes, <c>SessaoLocal.Evento</c> e
/// <c>Acrescentar</c> faziam o <c>File.AppendAllText</c> na thread de quem chamava: a da interface (marcação, relatório, fim de live), a do
/// laço de captura ou do player (séries) e qualquer uma que escrevesse uma linha de erro no log (o evento "falha"). Disco lento, antivírus
/// examinando a pasta ou disco cheio travavam essa thread. Agora quem chama só põe o item numa fila (sem disco, sem espera) e uma thread própria
/// de prioridade baixa grava em lotes: um <c>AppendAllText</c> por arquivo e por lote, no máximo a cada ~150 ms.
///
/// Garantias: a ordem dos itens de um mesmo arquivo é a da chamada; <see cref="Descarregar"/> espera tudo chegar ao disco (usado ao fechar o app,
/// antes de cada rodada de envio e nos testes); eventos urgentes (<see cref="SessaoLocal.EventoUrgente">marcação, falha, fim de sessão, suspensão</see>) pulam o agrupamento; fila cheia nunca bloqueia: o item é descartado e contado como perda local
/// (<c>perda_local</c>, motivo <c>FilaCheiaException</c>). Se o app morrer, perde-se no máximo o que estava na fila (cerca de 150 ms).
/// </summary>
public static class Gravador
{
    /// <summary>Teto de itens (e de bytes) esperando disco. Passou: descarta e conta como perda. Os testes encolhem.</summary>
    public static int MaxItens { get; set; } = 20_000;
    public static long MaxBytes { get; set; } = 32L * 1024 * 1024;
    /// <summary>Só os testes: com isso ligado a thread não grava, e dá para provar que a chamada não esperou o disco.</summary>
    public static volatile bool PausarParaTeste;

    sealed class Item
    {
        public SessaoLocal S = null!;
        public string Nome = "", Texto = "";
        public bool Retrato, Evento;
        public int Bytes;
    }

    static readonly ConcurrentQueue<Item> _fila = new();
    static readonly AutoResetEvent _sinal = new(false);
    static readonly object _inicio = new();
    static Thread? _thread;
    static int _pendentes;
    static long _bytesPendentes;
    static volatile bool _pressa;
    /// <summary>Acorda a thread de disco antes dos ~150 ms de agrupamento (item urgente ou <see cref="Descarregar">descarga</see>).</summary>
    static readonly ManualResetEventSlim _acelerar = new(false);

    /// <summary>Quantos itens ainda esperam disco (testes e custo).</summary>
    public static int Pendentes => Volatile.Read(ref _pendentes);

    /// <summary>Põe um item na fila. Falso = fila cheia (quem chama conta a perda). Nunca grava, nunca espera.</summary>
    internal static bool Enfileirar(SessaoLocal s, string nome, string texto, bool retrato, bool evento, bool urgente = false)
    {
        int bytes = texto.Length * 2; // estimativa barata (UTF-16 em memória); o teto é de memória, não de disco
        if (Volatile.Read(ref _pendentes) >= MaxItens || Interlocked.Read(ref _bytesPendentes) + bytes > MaxBytes)
        {
            Custo.Descartou();
            return false;
        }
        Interlocked.Increment(ref _pendentes);
        Interlocked.Add(ref _bytesPendentes, bytes);
        _fila.Enqueue(new Item { S = s, Nome = nome, Texto = texto, Retrato = retrato, Evento = evento, Bytes = bytes });
        Custo.FilaViu(Volatile.Read(ref _pendentes));
        Garantir();
        if (urgente) { _pressa = true; _acelerar.Set(); } // sem os ~150 ms de agrupamento: só para os poucos eventos que um travamento deixaria sem rastro
        _sinal.Set();
        return true;
    }

    static void Garantir()
    {
        if (_thread != null) return;
        lock (_inicio)
        {
            if (_thread != null) return;
            _thread = new Thread(Laco) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "telemetria-disco" };
            _thread.Start();
        }
    }

    /// <summary>Espera o que está na fila chegar ao disco. Devolve falso se passou do prazo. Só chame de thread que pode esperar (nunca a da interface).</summary>
    public static bool Descarregar(TimeSpan prazo)
    {
        if (Volatile.Read(ref _pendentes) == 0) return true;
        var fim = DateTime.UtcNow + prazo;
        _pressa = true;
        _acelerar.Set();
        _sinal.Set();
        while (Volatile.Read(ref _pendentes) > 0 && DateTime.UtcNow < fim) Thread.Sleep(5);
        return Volatile.Read(ref _pendentes) == 0;
    }

    static void Laco()
    {
        while (true)
        {
            _sinal.WaitOne();
            // junta o que chegar junto num lote só (uma marcação costuma vir com relatório, perfil e linhas de série); com pressa não espera
            if (!_pressa) _acelerar.Wait(150);
            _acelerar.Reset();
            while (PausarParaTeste) Thread.Sleep(10);
            _pressa = false;
            var lote = new List<Item>();
            while (_fila.TryDequeue(out var it)) lote.Add(it);
            if (lote.Count == 0) continue;
            var cronometro = Stopwatch.StartNew();
            long bytesGravados = 0;
            int lotes = 0;
            try
            {
                // por arquivo, na ordem da chamada; retrato (arquivo inteiro de uma vez) e linhas não se misturam porque são arquivos diferentes
                var grupos = new List<((SessaoLocal S, string Nome) Chave, StringBuilder Texto, int N, bool Evento)>();
                var indice = new Dictionary<(SessaoLocal, string), int>();
                foreach (var it in lote)
                {
                    if (it.Retrato) { it.S.GravarRetratoAgora(it.Nome, it.Texto); bytesGravados += it.Texto.Length; lotes++; continue; }
                    if (!indice.TryGetValue((it.S, it.Nome), out int g)) { g = grupos.Count; indice[(it.S, it.Nome)] = g; grupos.Add(((it.S, it.Nome), new StringBuilder(), 0, it.Evento)); }
                    var gr = grupos[g];
                    gr.Texto.Append(it.Texto).Append('\n');
                    grupos[g] = (gr.Chave, gr.Texto, gr.N + 1, gr.Evento);
                }
                foreach (var g in grupos)
                {
                    string texto = g.Texto.ToString();
                    g.Chave.S.GravarLinhasAgora(g.Chave.Nome, texto, g.N, g.Evento);
                    bytesGravados += texto.Length; lotes++;
                }
            }
            catch { /* GravarLinhasAgora e GravarRetratoAgora já contam a perda; nada daqui pode matar a thread */ }
            finally
            {
                cronometro.Stop();
                Custo.Gravou(lote.Count, bytesGravados, lotes, cronometro.ElapsedTicks);
                foreach (var it in lote) { Interlocked.Add(ref _bytesPendentes, -it.Bytes); Interlocked.Decrement(ref _pendentes); }
            }
        }
    }
}
