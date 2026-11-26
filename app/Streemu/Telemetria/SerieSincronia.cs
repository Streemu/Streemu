using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

namespace Streemu.Telemetria;

/// <summary>Fila limitada, números somente. Disco em lotes numa thread própria; nunca na captura, no player ou na tela.</summary>
sealed class SerieSincronia : IDisposable
{
    readonly SessaoLocal _session;
    /// <summary>Cada linha leva o arquivo da live em que foi tirada (<see cref="SessaoLocal.ArquivoSincronia"/>): a thread de gravação pode escrever depois de a live seguinte abrir.</summary>
    readonly BlockingCollection<(string Arquivo, string Linha)> _queue = new(128);
    readonly Thread _writer;
    long _discarded;

    public SerieSincronia(SessaoLocal session)
    {
        _session = session;
        _writer = new Thread(Write) { IsBackground = true, Name = "telemetria-sincronia", Priority = ThreadPriority.BelowNormal };
        _writer.Start();
    }

    public void Add(string? live, string link, string role, JsonObject data)
    {
        var line = new JsonObject
        {
            ["hora_utc"] = DateTime.UtcNow.ToString("O"), ["transmissao"] = live, ["ligacao"] = link, ["papel"] = role,
            ["amostras_descartadas"] = Interlocked.Read(ref _discarded), ["dados"] = data,
        }.ToJsonString();
        try { if (!_queue.TryAdd((_session.ArquivoSincronia, line))) Interlocked.Increment(ref _discarded); }
        catch (InvalidOperationException) { } // o app já está fechando
    }

    void Write()
    {
        var batch = new StringBuilder();
        string arquivo = "";
        long flushed = Environment.TickCount64;
        while (!_queue.IsCompleted)
        {
            if (_queue.TryTake(out var item, 500))
            {
                if (item.Arquivo != arquivo && batch.Length > 0) { _session.Acrescentar(arquivo, batch.ToString()); batch.Clear(); flushed = Environment.TickCount64; } // live nova: o lote da anterior fecha no arquivo dela
                arquivo = item.Arquivo;
                batch.AppendLine(item.Linha);
            }
            if (batch.Length > 0 && (Environment.TickCount64 - flushed >= 10_000 || batch.Length >= 64_000 || _queue.IsCompleted))
            {
                _session.Acrescentar(arquivo, batch.ToString()); batch.Clear(); flushed = Environment.TickCount64;
            }
        }
        if (batch.Length > 0) _session.Acrescentar(arquivo, batch.ToString());
    }

    public void Dispose() { _queue.CompleteAdding(); _writer.Join(1000); }
}
