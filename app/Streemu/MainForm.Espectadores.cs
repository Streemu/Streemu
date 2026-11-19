using System.Text.Json.Nodes;
using Streemu.Net;
using Streemu.Ui;
using Streemu.Ui.Avatars;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// Quem de fato está assistindo, para a faixa do player mostrar só o transmissor e os espectadores (e não o grupo todo).
/// A sala só leva a CONTAGEM de quem assiste, e mudar isso exigiria publicar o Worker para todo mundo; por isso o transmissor
/// manda a lista direto a cada espectador pelas mensagens `para`/`de` que já existem (tipo "espectadores").
/// Fica fora do MainForm.Grupo.cs para dar menos conflito.
/// </summary>
public sealed partial class MainForm
{
    const int MaxWatcherIdLength = 64;
    static readonly TimeSpan WatchersListGrace = TimeSpan.FromSeconds(8);

    /// <summary>Do lado de quem ASSISTE: os ids que o transmissor disse estarem assistindo, por sessão.</summary>
    readonly Dictionary<string, HashSet<string>> _watchersBySession = new();
    string _lastWatchersSent = "";

    /// <summary>Do lado de quem TRANSMITE: avisa cada espectador da lista atual, só quando ela mudou.</summary>
    void TellViewersWhoWatches()
    {
        if (_room == null) return;
        var senderLinks = _links.Values.Where(l => l.IAmSender).ToList();
        var ids = senderLinks.Select(l => l.PeerId).OrderBy(id => id, StringComparer.Ordinal).ToList();
        string key = string.Join(",", ids);
        if (key == _lastWatchersSent) return;
        _lastWatchersSent = key;
        Log.Info($"Espectadores mudaram: {ids.Count} assistindo; avisando {senderLinks.Count} pessoa(s).");
        foreach (var link in senderLinks)
            _ = _room.SendTo(link.PeerId, new JsonObject
            {
                ["tipo"] = "espectadores", ["sessao"] = link.SessionId,
                ["ids"] = new JsonArray(ids.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray())
            });
    }

    /// <summary>
    /// A lista só chega se o TRANSMISSOR estiver numa build com ela (todas se anunciam como "0.2.4", então a versão não serve de teste).
    /// Se ele não mandou nada pouco depois de conectar, avisa o motivo e o que fazer, em vez de mostrar uma faixa incompleta sem explicar.
    /// </summary>
    void WarnIfStreamerSendsNoList(PeerLink link)
    {
        _ = Task.Delay(WatchersListGrace).ContinueWith(_ => Ui(() =>
        {
            if (!_links.ContainsKey(link.SessionId) || _watchersBySession.ContainsKey(link.SessionId)) return;
            Notify($"{link.PeerNick} está com uma versão do {UiBrand.Name} sem a lista de quem assiste: a faixa mostra só ele e você. Peça para ele clicar em \"Procurar atualização\".");
        }));
    }

    /// <summary>Do lado de quem assiste: guarda a lista recebida (ignora o que não é uma lista de textos curtos).</summary>
    void SaveWatchers(string sid, JsonObject msg)
    {
        if (msg["ids"] is not JsonArray array) return;
        var ids = new HashSet<string>();
        foreach (var node in array.Take(RosterEntry.MaxEntries))
            if (node is JsonValue v && v.TryGetValue<string>(out var id) && id.Length is > 0 and <= MaxWatcherIdLength) ids.Add(id);
        _watchersBySession[sid] = ids;
        Log.Info($"O transmissor avisou: {ids.Count} assistindo (a faixa mostra {ids.Count + 1} ou menos pessoas).");
    }

    /// <summary>A faixa de quem assiste esta transmissão: o transmissor, quem a lista diz que assiste, e eu.</summary>
    /// <summary>A faixa de quem assiste. Na tela leva o apelido local; <paramref name="paraRelatorio"/> usa só o nome real (o relatório vai à telemetria).</summary>
    IReadOnlyList<RosterEntry> WatchersRoster(PeerLink link, bool paraRelatorio = false) =>
        RosterEntry.FromWatchers(_room, link.PeerId, _watchersBySession.GetValueOrDefault(link.SessionId) ?? new HashSet<string>(), AvatarOfMember, paraRelatorio ? null : NomeNaTela);

    AvatarImage? AvatarOfMember(string memberId) => memberId == _room?.MyId ? MyAvatarImage() : _friendAvatars?.ImageOf(memberId);

    /// <summary>Teste da sala: espera N segundos (variável de ambiente) para entrar ou sair no meio da transmissão. Falso se não foi pedida.</summary>
    static async Task<bool> TestWaitAsync(string variable)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(variable), out var seconds) || seconds <= 0) return false;
        await Task.Delay(seconds * 1000);
        return true;
    }

    /// <summary>Teste da sala: fecha o que estou assistindo (como clicar em fechar o player), avisando o transmissor.</summary>
    void TestLeaveStream()
    {
        foreach (var link in _links.Values.Where(l => !l.IAmSender).ToList()) EndLink(link.SessionId, tellPeer: true, why: null);
    }

    // ------------------------------------------------------------------ teste da sala com vários transmissores (spec 006, MS20)

    /// <summary>STREEMU_TEST_LIVE="B,C": outros papéis que também ficam ao vivo (de mentira), além do A. Prepara o teste do multistream.</summary>
    static bool TestIsStreamer() =>
        TestRole == "A" || (Environment.GetEnvironmentVariable("STREEMU_TEST_LIVE") ?? "").Split(',', StringSplitOptions.TrimEntries).Contains(TestRole);

    /// <summary>Quem assiste só grava o relatório depois que TODOS os transmissores gravaram o deles (senão sair derruba as ligações antes).</summary>
    static bool TestStreamersReported() =>
        new[] { "A" }.Concat((Environment.GetEnvironmentVariable("STREEMU_TEST_LIVE") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .All(role => File.Exists(Path.Combine(TestDir!, role + "-relatorio.json")));

    /// <summary>Quantos relatórios o transmissor espera antes de fechar (padrão 3; com vários transmissores e espectadores, mais).</summary>
    static int TestExpectedReports() => int.TryParse(Environment.GetEnvironmentVariable("STREEMU_TEST_REPORTS"), out var n) ? n : 3;

    /// <summary>STREEMU_TEST_WATCH_ALL=N: este papel assiste TODOS os que estão ao vivo (espera N aparecerem). Nulo se não pedido.</summary>
    static int? TestWatchAll() => int.TryParse(Environment.GetEnvironmentVariable("STREEMU_TEST_WATCH_ALL"), out var n) && n > 0 ? n : null;

    async Task TestWatchEveryoneAsync(int expected)
    {
        var until = DateTime.Now.AddSeconds(25);
        while (_room != null && _room.Friends.Count(f => f.Live != null) < expected && DateTime.Now < until) await Task.Delay(200);
        foreach (var friend in _room?.Friends.Where(f => f.Live != null).ToList() ?? new List<Member>()) await WatchAsync(friend, "teste (todos)");
    }
}
