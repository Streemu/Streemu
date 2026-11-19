using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Streemu.Util;

namespace Streemu.Telemetria;

/// <summary>
/// Diagnóstico de quem ainda não tem conta (spec 022, decisão Q2-B). Sem conta o app não manda arquivo nenhum, mas o defeito que impede o login é
/// justamente o que mais importa ver. Então só os eventos do login sobem, para <c>POST /telemetria/login</c>: sem credencial, sem identidade (só o id
/// do PC no cabeçalho <c>x-streemu-pc</c>, o mesmo do envio normal), no máximo <see cref="Max"/> eventos e <see cref="TamanhoMax"/> bytes por envio.
/// Cada evento leva só <c>passo</c>, <c>ok</c> e o TIPO do erro: nunca o código digitado, e-mail, token nem texto livre (por isso passo e erro passam
/// por <see cref="Limpo"/>). A fila é limitada: quando enche, os mais velhos saem. Quem esvazia é a thread "telemetria" (<see cref="Envio"/>).
/// </summary>
public static class LoginSemConta
{
    public const int Max = 20, TamanhoMax = 2048;

    public sealed record Evento(DateTime Hora, string Passo, bool Ok, string? Erro);

    static readonly object _gate = new();
    static readonly List<Evento> _fila = new();

    /// <summary>Guarda um evento do login. Seguro entre threads; nunca lança.</summary>
    public static void Enfileirar(string passo, bool ok, string? erro) => Enfileirar(passo, ok, erro, DateTime.UtcNow);

    public static void Enfileirar(string passo, bool ok, string? erro, DateTime horaUtc)
    {
        try
        {
            var e = new Evento(horaUtc, Limpo(passo) ?? "outro", ok, Limpo(erro));
            lock (_gate)
            {
                _fila.Add(e);
                while (_fila.Count > Max) _fila.RemoveAt(0); // descarta os mais velhos
            }
        }
        catch { }
    }

    /// <summary>Só letras minúsculas, dígitos e "_", até 32 caracteres: um tipo de erro ("codigo_errado"), nunca uma frase nem um valor digitado. Vazio vira nulo.</summary>
    public static string? Limpo(string? t)
    {
        if (string.IsNullOrWhiteSpace(t)) return null;
        var s = new string(t.Trim().ToLowerInvariant().Where(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_').Take(32).ToArray());
        return s.Length == 0 ? "outro" : s;
    }

    public static int Pendentes { get { lock (_gate) return _fila.Count; } }

    /// <summary>Esvazia a fila (testes).</summary>
    public static void Limpar() { lock (_gate) _fila.Clear(); }

    /// <summary>Monta o corpo do envio com os eventos mais antigos que cabem em <see cref="TamanhoMax"/> bytes. <paramref name="usados"/> diz quantos foram.</summary>
    public static string? Montar(IReadOnlyList<Evento> eventos, string versao, string commit, out int usados)
    {
        usados = 0;
        string? ultimo = null;
        for (int n = 1; n <= Math.Min(Max, eventos.Count); n++)
        {
            var corpo = new JsonObject
            {
                ["versao"] = versao, ["commit"] = commit,
                ["eventos"] = new JsonArray(eventos.Take(n).Select(e => (JsonNode)new JsonObject
                {
                    ["hora"] = e.Hora.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), ["passo"] = e.Passo, ["ok"] = e.Ok, ["erro"] = e.Erro,
                }).ToArray()),
            }.ToJsonString();
            if (Encoding.UTF8.GetByteCount(corpo) > TamanhoMax) break;
            ultimo = corpo; usados = n;
        }
        return ultimo;
    }

    /// <summary>Manda um lote, se houver. Chamado só pela thread "telemetria". Falha é só para o log (uma vez): o lote fica na fila para a próxima rodada.</summary>
    public static void EnviarPendentes(HttpClient http, Uri servidor, string pc, string versao, string commit, TimeSpan limite)
    {
        List<Evento> lote;
        lock (_gate) lote = _fila.ToList();
        if (lote.Count == 0) return;
        var corpo = Montar(lote, versao, commit, out int usados);
        if (corpo == null) { lock (_gate) _fila.RemoveAt(0); return; } // um evento sozinho já passa de 2 KB: não deveria existir; não trava a fila
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(servidor, "/telemetria/login")) { Content = new StringContent(corpo, Encoding.UTF8, "application/json") };
            req.Headers.Add("x-streemu-pc", pc);
            using var cts = new CancellationTokenSource(limite < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : limite);
            using var resp = http.Send(req, cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                lock (_gate) _fila.RemoveRange(0, Math.Min(usados, _fila.Count)); // saem os que foram; o que chegou durante o envio fica
                return;
            }
            // 4xx que não passa de novo (corpo recusado, rota que o Worker ainda não tem) não pode travar a fila nem repetir para sempre
            if ((int)resp.StatusCode is >= 400 and < 500 and not 429) lock (_gate) _fila.RemoveRange(0, Math.Min(usados, _fila.Count));
            Aviso($"o servidor respondeu {(int)resp.StatusCode}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException) { Aviso("sem resposta do servidor"); }
    }

    static bool _avisou;
    static void Aviso(string motivo)
    {
        if (_avisou) return;
        _avisou = true; // uma vez por abertura do app: sem conta isto se repetiria a cada rodada
        Log.Info($"[telemetria] não consegui mandar os eventos do login ({motivo}). Tento de novo sozinho.");
    }
}
