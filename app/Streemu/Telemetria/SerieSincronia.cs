using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

namespace Streemu.Telemetria;

/// <summary>Fila limitada, números somente. Disco em lotes numa thread própria; nunca na captura, no player ou na tela.</summary>
sealed class SerieSincronia : IDisposable
{
    public const string Arquivo = "sincronia.ndjson";
    readonly SessaoLocal _session;
    readonly BlockingCollection<string> _queue = new(128);
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
        try { if (!_queue.TryAdd(line)) Interlocked.Increment(ref _discarded); }
        catch (InvalidOperationException) { } // o app já está fechando
    }

    void Write()
    {
        var batch = new StringBuilder();
        long flushed = Environment.TickCount64;
        while (!_queue.IsCompleted)
        {
            if (_queue.TryTake(out string? line, 500)) batch.AppendLine(line);
            if (batch.Length > 0 && (Environment.TickCount64 - flushed >= 10_000 || batch.Length >= 64_000 || _queue.IsCompleted))
            {
                _session.Acrescentar(Arquivo, batch.ToString()); batch.Clear(); flushed = Environment.TickCount64;
            }
        }
        if (batch.Length > 0) _session.Acrescentar(Arquivo, batch.ToString());
    }

    public void Dispose() { _queue.CompleteAdding(); _writer.Join(1000); }
}
