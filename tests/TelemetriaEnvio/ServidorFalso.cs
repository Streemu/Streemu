using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>Como o servidor falso trata o próximo pedido (a fila <see cref="ServidorFalso.Fila"/> vale um pedido por item; vazia = Normal).</summary>
enum Modo
{
    Normal,
    /// <summary>Grava e fecha a conexão sem responder (o app não sabe que o servidor gravou).</summary>
    PerderResposta,
    /// <summary>Responde 500 sem gravar nada.</summary>
    Erro500,
    /// <summary>Não responde por mais de 20 s (o app desiste sozinho); não grava.</summary>
    Demorar,
    /// <summary>Grava e confirma só a primeira metade de cada pedaço (cursor velho / confirmado menor que o enviado).</summary>
    ConfirmadoMenor,
}

/// <summary>
/// Servidor HTTP em 127.0.0.1 que fala o protocolo de POST /telemetria/enviar do Worker (relay/telemetria.js, receber): grava um pedaço só se
/// ele começa no byte já confirmado; senão devolve o confirmado. TCP cru, sem HttpListener: não depende de permissão de URL e deixa fechar a
/// conexão no meio da resposta. Também roda sozinho num processo filho (<c>--servidor-falso</c>) para a carga não medir o servidor junto.
/// </summary>
sealed class ServidorFalso : IDisposable
{
    /// <summary>Contas (spec 019, 022): o servidor aceita Bearer igual a <see cref="TokenAceito"/> e o bilhete <see cref="BilheteAceito"/>; a senha do grupo acabou.
    /// <see cref="Credenciais"/> guarda como cada pedido se identificou ("token:...", "bilhete:...", "" = sem credencial).
    /// <c>POST /telemetria/login</c> é a rota aberta dos eventos do login (<see cref="Logins"/>): sem credencial, só o cabeçalho x-streemu-pc.</summary>
    public const string TokenPadrao = "tok-ok";
    public volatile string TokenAceito = TokenPadrao;
    readonly List<(string Pc, bool TemCredencial, string Corpo)> _logins = new();
    public (string Pc, bool TemCredencial, string Corpo)[] Logins() { lock (_g) return _logins.ToArray(); }
    public const string BilheteAceito = "st1.1.bilhete-ok";
    readonly List<string> _credenciais = new();
    public string[] Credenciais() { lock (_g) return _credenciais.ToArray(); }
    readonly TcpListener _l = new(IPAddress.Loopback, 0);
    readonly object _g = new();
    readonly Dictionary<string, MemoryStream> _arq = new();
    readonly HashSet<string> _eids = new();
    readonly List<(long N, long Ms)> _eventosEm = new();
    readonly List<string> _tipos = new();
    readonly List<JsonObject> _eventosObj = new();
    readonly List<string> _anomalias = new();
    readonly List<(string Arq, string? H0, string? H1)> _horas = new();
    volatile bool _fim;
    int _pedidos, _gravouEPerdeu;
    long _bytesRecebidos;

    public ConcurrentQueue<Modo> Fila { get; } = new();
    /// <summary>Controle do próprio teste: confirma o que recebeu SEM gravar. O verificador de cursor tem que pegar isso.</summary>
    public volatile bool Mentir;
    /// <summary>Nomes de arquivo (ex.: "log.txt") que o servidor põe em "recusados".</summary>
    readonly HashSet<string> _recusar = new();
    public void RecusarArquivo(string nome) { lock (_g) _recusar.Add(nome); }
    public void LiberarRecusas() { lock (_g) _recusar.Clear(); }
    /// <summary>Nome de arquivo → o que responder em "confirmados" no lugar da verdade (null = omitir a chave).</summary>
    public ConcurrentDictionary<string, JsonNode?> Forcar { get; } = new();
    public int DemoraMs { get; set; } = 22_000;
    public int Porta => ((IPEndPoint)_l.LocalEndpoint).Port;
    public int Pedidos => Volatile.Read(ref _pedidos);
    public int GravouEPerdeu => Volatile.Read(ref _gravouEPerdeu);
    public long BytesRecebidos => Interlocked.Read(ref _bytesRecebidos);

    public ServidorFalso()
    {
        _l.Start();
        new Thread(Aceitar) { IsBackground = true, Name = "servidor-falso" }.Start();
    }

    public long Tamanho(string chave) { lock (_g) return _arq.TryGetValue(chave, out var m) ? m.Length : 0; }
    public byte[] Conteudo(string chave) { lock (_g) return _arq.TryGetValue(chave, out var m) ? m.ToArray() : Array.Empty<byte>(); }
    public bool VeioEvento(string tipo) { lock (_g) return _tipos.Contains(tipo); }
    public int ContarEventos(string tipo) { lock (_g) return _tipos.Count(x => x == tipo); }
    readonly List<JsonObject> _cabecalhos = new();
    /// <summary>Os cabeçalhos (identidade, perfil, peças) dos pedidos recebidos, para o teste conferir o que o app disse sobre quem é.</summary>
    public JsonObject[] Cabecalhos() { lock (_g) return _cabecalhos.ToArray(); }
    public JsonObject? UltimoEvento(string tipo) { lock (_g) return _eventosObj.LastOrDefault(e => e["tipo"]?.GetValue<string>() == tipo); }
    /// <summary>h0/h1 (hora do conteúdo) de cada pedaço gravado do arquivo, na ordem.</summary>
    public List<(string? H0, string? H1)> Horas(string arq) { lock (_g) return _horas.Where(x => x.Arq == arq).Select(x => (x.H0, x.H1)).ToList(); }
    public string[] Anomalias() { lock (_g) return _anomalias.ToArray(); }

    void Aceitar()
    {
        while (!_fim)
        {
            TcpClient c;
            try { c = _l.AcceptTcpClient(); } catch { return; }
            // um por conexão: um pedido "demorando" não segura a nova tentativa
            ThreadPool.QueueUserWorkItem(_ => { try { Atender(c); } catch { } });
        }
    }

    void Atender(TcpClient c)
    {
        using (c)
        {
            var ns = c.GetStream();
            ns.ReadTimeout = 30_000;
            var (linha, cab, corpo) = LerRequisicao(ns);
            if (linha == null) return;
            if (linha.StartsWith("GET /_ctl/resumo")) { Responder(ns, 200, Resumo().ToJsonString()); return; }
            Interlocked.Increment(ref _pedidos);
            Interlocked.Add(ref _bytesRecebidos, corpo.Length);
            string cred;
            if (cab.TryGetValue("authorization", out var auth) && auth.StartsWith("Bearer ")) cred = "token:" + auth[7..];
            else if (cab.TryGetValue("x-streemu-bilhete", out var bil)) cred = "bilhete:" + bil;
            else cred = "";
            if (linha.StartsWith("POST /telemetria/login"))
            {
                // não conta em Credenciais: essa lista é só do envio de arquivos
                lock (_g) _logins.Add((cab.GetValueOrDefault("x-streemu-pc", ""), cred != "" || cab.ContainsKey("x-streemu-senha"), Encoding.UTF8.GetString(corpo)));
                Responder(ns, 200, "{\"ok\":true}");
                return;
            }
            lock (_g) _credenciais.Add(cred);
            bool aceita = cred == "token:" + TokenAceito || cred == "bilhete:" + BilheteAceito;
            if (!aceita) { Responder(ns, 401, "credencial recusada"); return; }
            if (!linha.StartsWith("POST /telemetria/enviar")) { Responder(ns, 404, "não encontrado"); return; }

            Modo modo = Fila.TryDequeue(out var m) ? m : Modo.Normal;
            switch (modo)
            {
                case Modo.Erro500: Responder(ns, 500, "erro de teste"); return;
                case Modo.Demorar: Thread.Sleep(DemoraMs); return; // o app já desistiu; fechar a conexão não importa
            }
            var resp = Processar(corpo, modo);
            if (modo == Modo.PerderResposta) { Interlocked.Increment(ref _gravouEPerdeu); return; } // fecha sem responder
            Responder(ns, 200, resp.ToJsonString());
        }
    }

    JsonObject Processar(byte[] corpo, Modo modo)
    {
        int len = (corpo[0] << 24) | (corpo[1] << 16) | (corpo[2] << 8) | corpo[3];
        var cab = (JsonObject)JsonNode.Parse(Encoding.UTF8.GetString(corpo, 4, len))!;
        int off = 4 + len;
        lock (_g) _cabecalhos.Add((JsonObject)cab.DeepClone());
        var conf = new JsonObject();
        var rec = new JsonArray();
        lock (_g)
        {
            foreach (var p in cab["pecas"]!.AsArray())
            {
                string sessao = p!["sessao"]!.GetValue<string>(), arq = p["arquivo"]!.GetValue<string>();
                long ini = p["inicio"]!.GetValue<long>(), fim = p["fim"]!.GetValue<long>();
                int tam = p["tam"]!.GetValue<int>();
                byte[] gz = corpo.AsSpan(off, tam).ToArray();
                off += tam;
                string chave = $"{sessao}|{arq}";
                if (_recusar.Contains(arq)) { rec.Add(chave); continue; }
                // resposta imposta pelo teste para este arquivo, sem gravar (número absurdo, negativo, texto) ou ausente (null = não diz nada)
                if (Forcar.TryGetValue(arq, out var forcado)) { if (forcado != null) conf[chave] = forcado.DeepClone(); continue; }
                if (!_arq.TryGetValue(chave, out var ms)) _arq[chave] = ms = new MemoryStream();
                long atual = ms.Length;
                // regra do Worker: só grava o que começa exatamente no byte já confirmado
                if (ini != atual || fim < ini) { conf[chave] = atual; continue; }
                byte[] dados = Gunzip(gz);
                if (dados.Length != fim - ini) _anomalias.Add($"{chave}: o pedaço diz {ini}..{fim} mas descomprimido tem {dados.Length} bytes");
                _horas.Add((arq, p["h0"]?.GetValue<string>(), p["h1"]?.GetValue<string>()));
                if (Mentir) { conf[chave] = fim; continue; }
                int guarda = modo == Modo.ConfirmadoMenor ? dados.Length / 2 : dados.Length;
                ms.Write(dados, 0, guarda);
                conf[chave] = ms.Length;
            }
            long agora = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var ev in cab["eventos"]!.AsArray())
            {
                if (ev is not JsonObject o) continue;
                string eid = o["eid"]?.GetValue<string>() ?? "";
                if (!_eids.Add(eid)) continue; // o servidor ignora o mesmo evento reenviado
                _tipos.Add(o["tipo"]?.GetValue<string>() ?? "");
                _eventosObj.Add((JsonObject)o.DeepClone());
                if (o["n"] is JsonNode n) _eventosEm.Add((n.GetValue<long>(), agora));
            }
        }
        return new JsonObject
        {
            ["ok"] = true, ["servidor"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["confirmados"] = conf, ["recusados"] = rec,
            ["ritmo"] = new JsonObject { ["ativo_s"] = 2, ["parado_s"] = 10 },
        };
    }

    JsonObject Resumo()
    {
        lock (_g)
            return new JsonObject
            {
                ["pedidos"] = Pedidos, ["bytes_recebidos"] = BytesRecebidos, ["anomalias"] = _anomalias.Count,
                ["eventos"] = new JsonArray(_eventosEm.Select(e => (JsonNode)new JsonArray(e.N, e.Ms)).ToArray()),
            };
    }

    static byte[] Gunzip(byte[] gz)
    {
        using var g = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress);
        var ms = new MemoryStream();
        g.CopyTo(ms);
        return ms.ToArray();
    }

    static (string? Linha, Dictionary<string, string> Cab, byte[] Corpo) LerRequisicao(NetworkStream ns)
    {
        var ms = new MemoryStream();
        var buf = new byte[16384];
        int fimCab;
        while (true)
        {
            int n = ns.Read(buf, 0, buf.Length);
            if (n <= 0) return (null, new(), Array.Empty<byte>());
            ms.Write(buf, 0, n);
            fimCab = ms.GetBuffer().AsSpan(0, (int)ms.Length).IndexOf("\r\n\r\n"u8);
            if (fimCab >= 0) break;
        }
        var linhas = Encoding.UTF8.GetString(ms.GetBuffer(), 0, fimCab).Split("\r\n");
        var cab = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in linhas.Skip(1)) { int i = l.IndexOf(':'); if (i > 0) cab[l[..i].Trim()] = l[(i + 1)..].Trim(); }
        int cl = cab.TryGetValue("content-length", out var s) ? int.Parse(s) : 0;
        int inicio = fimCab + 4;
        while (ms.Length - inicio < cl)
        {
            int n = ns.Read(buf, 0, buf.Length);
            if (n <= 0) return (null, cab, Array.Empty<byte>());
            ms.Write(buf, 0, n);
        }
        return (linhas[0], cab, ms.GetBuffer().AsSpan(inicio, cl).ToArray());
    }

    static void Responder(NetworkStream ns, int status, string texto)
    {
        byte[] b = Encoding.UTF8.GetBytes(texto);
        byte[] h = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {b.Length}\r\nConnection: close\r\n\r\n");
        ns.Write(h); ns.Write(b); ns.Flush();
    }

    public void Dispose() { _fim = true; try { _l.Stop(); } catch { } }

    /// <summary>Modo processo filho: imprime PORTA=n e roda até o pai fechar a entrada padrão.</summary>
    public static int Principal()
    {
        using var srv = new ServidorFalso();
        Console.Out.WriteLine("PORTA=" + srv.Porta);
        Console.Out.Flush();
        while (Console.In.ReadLine() != null) { }
        return 0;
    }
}
