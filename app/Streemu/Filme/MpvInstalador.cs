using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Streemu.Net;
using Streemu.Util;

namespace Streemu.Filme;

/// <summary>
/// Baixa o mpv do grupo quando alguém abre o modo filme pela primeira vez (spec 010, 9.2). Como o ffmpeg (spec 004, 8): o exe sabe QUAL
/// pacote quer (mpv-desejado.json embutido, com o SHA-256), o Worker dá só o link da pré-release fixa, e nada vai para o git.
/// Diferente do ffmpeg, ninguém tem o mpv ainda: não há troca de exe aberto, só "baixar, conferir, abrir na pasta mpv". Cada arquivo do zip é conferido
/// contra o pin (Binarios) antes de ir para a pasta, e o <see cref="Mpv.Find"/> confere de novo antes de cada uso. Também é o reparo do mpv que estava
/// diferente do esperado (vira mpv.exe.invalido e o filme baixa de novo).
/// </summary>
public static class MpvInstalador
{
    const string LogTag = "[mpv-instalacao]";
    public static readonly PinBinario? Desejado = Binarios.Pin(Binario.Mpv);

    static string Dir => Path.Combine(AppContext.BaseDirectory, "mpv");
    static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Streemu", "atualizacao");

    /// <summary>Deixa o mpv em mpv\mpv.exe. <paramref name="progresso"/> recebe frases curtas para a tela. Falha = exceção com o motivo para o usuário.</summary>
    public static async Task<string> InstalarAsync(RelayConfig.Settings? cfg, IProgress<string> progresso, CancellationToken ct)
    {
        try
        {
            string exe = await InstalarSemRegistroAsync(cfg, progresso, ct);
            Binarios.ReparoDoMpvTerminou(true, null);
            return exe;
        }
        catch (Exception e) { Binarios.ReparoDoMpvTerminou(false, e.Message); throw; }
    }

    static async Task<string> InstalarSemRegistroAsync(RelayConfig.Settings? cfg, IProgress<string> progresso, CancellationToken ct)
    {
        if (Mpv.Find() is string ja) return ja;
        var pin = Desejado ?? throw new InvalidOperationException("este build não sabe qual mpv usar (falta o mpv-desejado.json)");
        if (cfg == null) throw new InvalidOperationException("sem o servidor do grupo configurado, não sei de onde baixar o player");
        Directory.CreateDirectory(Folder);
        string zip = Path.Combine(Folder, pin.Tag + ".zip");
        if (!File.Exists(zip) || Sha256(zip) != pin.ZipSha256) await BaixarAsync(cfg, pin, zip, progresso, ct);
        progresso.Report("Abrindo o player...");
        string tmp = Dir + ".novo";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        ZipFile.ExtractToDirectory(zip, tmp);
        // cada arquivo do zip tem que ser o do pin: um zip com a soma certa mas um arquivo a mais ou a menos não entra na pasta
        foreach (var (nome, esperado) in pin.Arquivos)
        {
            string f = Path.Combine(tmp, nome);
            if (!File.Exists(f) || ConferidorDeBinario.Sha256(f) != esperado.Sha256)
            {
                Directory.Delete(tmp, true);
                throw new InvalidOperationException($"o {nome} do pacote do player não é o esperado; tente de novo");
            }
        }
        if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        Directory.Move(tmp, Dir);
        try { File.Delete(zip); } catch { }
        Log.Info($"{LogTag} mpv {pin.Versao} instalado em mpv\\.");
        return Mpv.Find() ?? throw new InvalidOperationException("o pacote não tinha o mpv.exe");
    }

    static async Task BaixarAsync(RelayConfig.Settings cfg, PinBinario pin, string zip, IProgress<string> progresso, CancellationToken ct)
    {
        string url = (Environment.GetEnvironmentVariable("STREEMU_ATUALIZACAO_URL") ?? cfg.Url).TrimEnd('/') + "/mpv?tag=" + Uri.EscapeDataString(pin.Tag);
        string link;
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
        {
            using var resp = await RelayConfig.EnviarAsync(http, () => new HttpRequestMessage(HttpMethod.Get, url), ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("o servidor do grupo ainda não entrega o player (falta publicar o Worker novo)");
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"o servidor do grupo respondeu {(int)resp.StatusCode}");
            link = (JsonNode.Parse(body) as JsonObject)?["url"]?.GetValue<string>() is { Length: > 0 } l ? l
                : throw new InvalidOperationException("o pacote do player ainda não foi publicado no grupo");
        }
        string tmp = zip + ".baixando";
        long have = File.Exists(tmp) ? new FileInfo(tmp).Length : 0;
        using var dl = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var r = new HttpRequestMessage(HttpMethod.Get, link);
        if (have > 0) r.Headers.Range = new RangeHeaderValue(have, null);
        using var res = await dl.SendAsync(r, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"o download respondeu {(int)res.StatusCode}");
        bool append = have > 0 && res.StatusCode == HttpStatusCode.PartialContent;
        if (!append) have = 0;
        await using (var src = await res.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(tmp, append ? FileMode.Append : FileMode.Create, FileAccess.Write))
        {
            var buf = new byte[256 * 1024];
            int n, ultimo = -1;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                have += n;
                int pct = pin.ZipTamanho > 0 ? (int)(have * 100 / pin.ZipTamanho) : 0;
                if (pct != ultimo) { ultimo = pct; progresso.Report($"Baixando o player do filme ({pin.ZipTamanho / 1048576} MB, só na primeira vez): {pct}%"); }
            }
        }
        if (Sha256(tmp) != pin.ZipSha256) { File.Delete(tmp); throw new InvalidOperationException("o pacote do player veio diferente do esperado; tente de novo"); }
        File.Move(tmp, zip, overwrite: true);
    }

    static string Sha256(string path) { using var f = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant(); }
}
