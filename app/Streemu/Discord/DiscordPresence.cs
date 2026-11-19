using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Streemu.Util;

namespace Streemu.Discord;

/// <summary>O que aparece no perfil do Discord enquanto a pessoa transmite.</summary>
/// <param name="JoinSecret">
/// Com valor: em vez do botão-link (que abre o navegador), usa o "Participar" nativo do Discord (party + secrets.join).
/// Quem clica recebe o segredo direto no Streemu dele pelo IPC (ACTIVITY_JOIN), sem navegador. Experimental: o
/// Discord pode não mostrar o botão para apps sem liberação, e botões-link e segredos não podem ir juntos.
/// </param>
public sealed record PresenceInfo(string Details, string State, long StartUnixMs, string? WatchUrl,
    string? JoinSecret = null, int PartySize = 1, int PartyMax = 8);

/// <summary>
/// Rich Presence pelo IPC local do Discord (pipe \\.\pipe\discord-ipc-N), o mesmo caminho que qualquer jogo usa:
/// não é bot, nem selfbot, nem mexe no cliente. Precisa do Discord desktop aberto e de "Compartilhar minha atividade"
/// ligado. Se o Discord não estiver aberto, tenta de novo a cada 15 s; quando ele abrir, a presença aparece sozinha.
///
/// Protocolo: cada quadro = opcode (int32 LE) + tamanho (int32 LE) + JSON. 0 HANDSHAKE, 1 FRAME, 2 CLOSE, 3 PING, 4 PONG.
/// Os botões do Rich Presence só abrem link https no navegador, e quem transmite não vê o próprio botão (só os outros).
/// </summary>
public sealed class DiscordPresence : IDisposable
{
    /// <summary>Aplicativo "Streemu" no Discord Developer Portal (criado em 2026-11-06). O ID não é segredo.</summary>
    public const string DefaultAppId = "1568336028157218906";

    readonly string _appId;
    readonly CancellationTokenSource _cts = new();
    readonly AutoResetEvent _wake = new(false);
    readonly object _gate = new();
    PresenceInfo? _desired;
    bool _dirty = true;
    volatile bool _publicParty = true, _lastHadPrivacy;
    NamedPipeClientStream? _pipe;
    bool _loggedMissing;

    public bool Connected { get; private set; }
    /// <summary>Usuário do Discord conectado (só para o log e a tela).</summary>
    public string? DiscordUser { get; private set; }
    /// <summary>Id e código do avatar da conta (do READY), para o app baixar a imagem pública. Nunca vão para o log.</summary>
    public string? DiscordUserId { get; private set; }
    public string? DiscordAvatarCode { get; private set; }
    public event Action? Changed;
    /// <summary>Última presença que o Discord confirmou (JSON), para o --discord-check --presenca.</summary>
    public string? LastAccepted { get; private set; }
    /// <summary>O Discord recusou o grupo público e o app voltou ao "Pedir para participar".</summary>
    public bool PublicPartyRefused => !_publicParty;
    /// <summary>Alguém clicou em "Participar" no perfil de quem transmite: o Discord entregou o segredo (id da transmissão).</summary>
    public event Action<string>? JoinRequested;

    public DiscordPresence(string appId)
    {
        _appId = appId;
        new Thread(Loop) { IsBackground = true, Name = "discord-ipc" }.Start();
    }

    /// <summary>Mostra (ou, com null, apaga) a presença. Pode chamar de qualquer thread; só manda quando muda.</summary>
    public void Set(PresenceInfo? info)
    {
        lock (_gate)
        {
            if (Equals(info, _desired)) return;
            _desired = info;
            _dirty = true;
        }
        _wake.Set();
    }

    void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            if (_pipe == null)
            {
                if (!TryConnect()) { _wake.WaitOne(TimeSpan.FromSeconds(15)); continue; }
            }
            try
            {
                PresenceInfo? info; bool dirty;
                lock (_gate) { info = _desired; dirty = _dirty; _dirty = false; }
                if (dirty) SendActivity(info);
            }
            catch (Exception e)
            {
                Log.Info("Discord: conexão caiu (" + e.Message + "). Tento de novo em 15 s.");
                Disconnect();
                lock (_gate) _dirty = true;
                continue;
            }
            _wake.WaitOne(TimeSpan.FromSeconds(5));
        }
    }

    bool TryConnect()
    {
        for (int i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try { pipe.Connect(200); }
            catch { pipe.Dispose(); continue; }
            try
            {
                Write(pipe, 0, new JsonObject { ["v"] = 1, ["client_id"] = _appId });
                var (op, ready) = Read(pipe);
                if (op != 1 || ready?["evt"]?.GetValue<string>() != "READY")
                {
                    Log.Info($"Discord recusou o streemu: {ready?["message"] ?? ready?["data"]?["message"] ?? ready?.ToJsonString()}");
                    pipe.Dispose();
                    return false;
                }
                _pipe = pipe;
                DiscordUser = ready["data"]?["user"]?["global_name"]?.GetValue<string>() ?? ready["data"]?["user"]?["username"]?.GetValue<string>();
                DiscordUserId = ready["data"]?["user"]?["id"]?.GetValue<string>();
                DiscordAvatarCode = ready["data"]?["user"]?["avatar"]?.GetValue<string>();
                Connected = true; // só depois da conta: quem vê Connected já lê nome e avatar preenchidos
                _loggedMissing = false;
                Log.Info($"Discord conectado ({DiscordUser ?? "usuário desconhecido"}): o perfil mostra quando você estiver transmitindo.");
                // "Participar" sem navegador: quem assiste recebe ACTIVITY_JOIN; quem transmite aceita sozinho os pedidos
                // (ACTIVITY_JOIN_REQUEST). Aceitar qualquer um é seguro: sem a senha da sala do grupo o segredo não abre nada.
                foreach (var evt in new[] { "ACTIVITY_JOIN", "ACTIVITY_JOIN_REQUEST" })
                    Write(pipe, 1, new JsonObject { ["cmd"] = "SUBSCRIBE", ["evt"] = evt, ["args"] = new JsonObject(), ["nonce"] = Guid.NewGuid().ToString("N") });
                new Thread(() => ReadLoop(pipe)) { IsBackground = true, Name = "discord-ipc-read" }.Start();
                Changed?.Invoke();
                return true;
            }
            catch (Exception e)
            {
                Log.Info("Discord: handshake falhou: " + e.Message);
                pipe.Dispose();
            }
        }
        if (!_loggedMissing)
        {
            _loggedMissing = true;
            Log.Info("Discord não encontrado (o app do Discord está fechado?). O card de \"transmitindo\" aparece sozinho quando ele abrir.");
        }
        return false;
    }

    void ReadLoop(NamedPipeClientStream pipe)
    {
        try
        {
            while (!_cts.IsCancellationRequested && pipe.IsConnected)
            {
                var (op, msg) = Read(pipe);
                switch (op)
                {
                    case 3: lock (pipe) Write(pipe, 4, msg ?? new JsonObject()); break;
                    case 2: throw new IOException("o Discord fechou a conexão: " + msg?["message"]);
                    case 1 when IsError(msg):
                        Log.Info($"Discord recusou {msg!["cmd"]} {msg["evt"]}: {msg["data"]?["message"]}");
                        // o IPC não aceitou o grupo público: manda de novo sem ele (volta ao "Pedir para participar")
                        if ((msg["cmd"] as JsonValue)?.GetValue<string>() == "SET_ACTIVITY" && _publicParty && _lastHadPrivacy)
                        {
                            _publicParty = false;
                            Log.Info("Discord: sem grupo público; o botão volta a ser \"Pedir para participar\" (o Streemu aceita sozinho).");
                            lock (_gate) _dirty = true;
                            _wake.Set();
                        }
                        break;
                    case 1 when (msg!["cmd"] as JsonValue)?.GetValue<string>() == "SET_ACTIVITY" && msg["data"] is JsonObject accepted:
                        // resposta de sucesso: o Discord devolve a presença como ficou (serve para conferir o grupo público)
                        LastAccepted = accepted.ToJsonString();
                        if (_lastHadPrivacy) Log.Info($"Discord aceitou a presença com grupo público (party: {accepted["party"]?.ToJsonString() ?? "-"}).");
                        break;
                    case 1 when Evt(msg) == "ACTIVITY_JOIN":
                        if (msg!["data"]?["secret"]?.GetValue<string>() is string secret)
                        {
                            Log.Info("Discord: clicaram em Participar; abrindo a transmissão.");
                            JoinRequested?.Invoke(secret);
                        }
                        break;
                    case 1 when Evt(msg) == "ACTIVITY_JOIN_REQUEST":
                        if (msg!["data"]?["user"]?["id"]?.GetValue<string>() is string userId)
                        {
                            Log.Info($"Discord: {msg["data"]?["user"]?["username"]} pediu para participar; aceitando.");
                            lock (pipe) Write(pipe, 1, new JsonObject
                            {
                                ["cmd"] = "SEND_ACTIVITY_JOIN_INVITE", ["args"] = new JsonObject { ["user_id"] = userId },
                                ["nonce"] = Guid.NewGuid().ToString("N")
                            });
                        }
                        break;
                }
            }
        }
        catch (Exception e) when (!_cts.IsCancellationRequested)
        {
            if (_pipe == pipe)
            {
                Log.Info("Discord desconectou (" + e.Message + ").");
                Disconnect();
                lock (_gate) _dirty = true;
                _wake.Set();
            }
        }
        catch { }
    }

    static string? Evt(JsonObject? msg) => (msg?["evt"] as JsonValue)?.GetValue<string>();
    static bool IsError(JsonObject? msg) => Evt(msg) == "ERROR";

    void SendActivity(PresenceInfo? info)
    {
        var pipe = _pipe ?? throw new IOException("sem conexão");
        JsonObject? activity = null;
        if (info != null)
        {
            activity = new JsonObject
            {
                ["type"] = 0,
                ["details"] = Clip(info.Details, 128),
                ["state"] = Clip(info.State, 128),
                ["timestamps"] = new JsonObject { ["start"] = info.StartUnixMs },
                ["assets"] = new JsonObject { ["large_image"] = "logo", ["large_text"] = $"{UiBrand.Name}: stream entre amigos" },
                ["instance"] = false
            };
            if (info.JoinSecret != null)
            {
                activity["party"] = new JsonObject { ["id"] = "streemu-" + info.JoinSecret, ["size"] = new JsonArray(info.PartySize, info.PartyMax) };
                // Grupo PÚBLICO (privacy 1): amigos e quem está na mesma call podem entrar sem pedir. No privado (padrão)
                // o botão era "Pedir para participar" e o Discord mandava um pedido por mensagem marcando quem transmite
                // ("Stream Request ... asked you to stream", teste de 2026-11-06), mesmo com o Streemu aceitando sozinho.
                // O Discord diz que isso "depende das configurações de privacidade" de cada conta; sem o campo, volta ao pedido.
                if (_publicParty) activity["party"]!["privacy"] = 1;
                activity["secrets"] = new JsonObject { ["join"] = info.JoinSecret };
            }
            else if (info.WatchUrl != null)
                activity["buttons"] = new JsonArray(new JsonObject { ["label"] = "Assistir", ["url"] = info.WatchUrl });
        }
        _lastHadPrivacy = activity?["party"]?["privacy"] != null;
        var frame = new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["args"] = new JsonObject { ["pid"] = Environment.ProcessId, ["activity"] = activity },
            ["nonce"] = Guid.NewGuid().ToString("N")
        };
        lock (pipe) Write(pipe, 1, frame);
        Log.Info(info == null ? "Discord: presença apagada." : $"Discord: presença \"{info.Details} · {info.State}\".");
    }

    static string Clip(string s, int max) => s.Length <= max ? (s.Length < 2 ? s + "  " : s) : s[..max];

    static void Write(Stream s, int op, JsonObject payload)
    {
        var body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var buf = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buf, op);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), body.Length);
        body.CopyTo(buf, 8);
        s.Write(buf);
        s.Flush();
    }

    static (int Op, JsonObject? Msg) Read(Stream s)
    {
        var head = new byte[8];
        s.ReadExactly(head);
        int op = BinaryPrimitives.ReadInt32LittleEndian(head);
        int len = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4));
        if (len < 0 || len > 1 << 20) throw new IOException("quadro inválido do Discord");
        var body = new byte[len];
        s.ReadExactly(body);
        return (op, JsonNode.Parse(body) as JsonObject);
    }

    void Disconnect()
    {
        var p = _pipe;
        _pipe = null;
        Connected = false;
        try { p?.Dispose(); } catch { }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        // apaga a presença ao fechar o app (o Discord também apaga sozinho quando o pipe fecha)
        try { if (_pipe != null) SendActivity(null); } catch { }
        _cts.Cancel();
        _wake.Set();
        Disconnect();
    }
}
