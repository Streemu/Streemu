using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Streemu.Net;

namespace Streemu.Util;

/// <summary>Build mais novo da main, como o Worker do grupo devolve (/atualizacao).</summary>
public sealed record UpdateInfo(string Versao, string Commit, string Arvore, long Hora, string Sha256, long Tamanho, string Notas, string Url, bool Obrigatoria = false)
{
    public string Quando => DateTimeOffset.FromUnixTimeSeconds(Hora).ToLocalTime().ToString("dd/MM HH:mm");
}

/// <summary>
/// Atualização automática a partir da main (docs/specs/004-atualizacao-automatica.md).
/// Só há aviso quando o carimbo do código (hash da árvore app/Streemu no git) é OUTRO e o commit é mais NOVO:
/// push só de documentação nem gera build (o GitHub Actions filtra por pasta), e o mesmo código saído da release
/// manual e do build da main tem o mesmo carimbo. Builds locais (canal "dev") nunca se atualizam.
/// </summary>
public static class Updater
{
    const string ExeName = "Streemu.exe", OldName = "Streemu.old.exe";

    /// <summary>STREEMU_ATUALIZAR_DEV=1 liga o atualizador num build local (só para testar o próprio atualizador).</summary>
    public static bool Enabled =>
        (BuildInfo.Canal != "dev" || Environment.GetEnvironmentVariable("STREEMU_ATUALIZAR_DEV") == "1")
        && BuildInfo.Tree.Length > 0 && BuildInfo.CommitTime > 0;

    /// <summary>"0.2.2 · build de 07/11 10:54" (para o log e a tela).</summary>
    public static string CurrentLabel =>
        $"{Program.Version}" + (BuildInfo.CommitTime > 0 ? $" · build de {DateTimeOffset.FromUnixTimeSeconds(BuildInfo.CommitTime).ToLocalTime():dd/MM HH:mm}" : "")
        + (BuildInfo.Canal == "dev" ? " (desenvolvimento)" : "");

    static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Streemu", "atualizacao");

    /// <summary>
    /// Carrega na abertura as DLLs do zip, que só a troca do exe (e a do ffmpeg) usa. O exe é um arquivo único que o .NET lê sob demanda:
    /// em 2026-11-10, num PC do grupo, a atualização das 11:33 falhou 3 vezes com <c>FileNotFoundException: System.IO.Compression.ZipFile</c>
    /// (e a mensagem do erro veio vazia) num app aberto havia 10 min, enquanto o MESMO build atualizou normal nos outros dois PCs.
    /// A causa não foi confirmada; carregando aqui, o que falhar aparece na abertura, e não na hora de atualizar.
    /// </summary>
    public static void Preload()
    {
        try { PreloadZip(); }
        catch (Exception e) { Log.Info("Atualização: não consegui carregar a parte de zip do Streemu na abertura (" + Describe(e) + "). A atualização automática pode falhar; se falhar, rode o instalador de novo."); }
    }

    // separado para a falha de carregar a DLL (que acontece ao compilar ESTE método) cair no try de quem chama
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void PreloadZip()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ZipFile).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ZipArchive).TypeHandle);
    }

    /// <summary>Faltou uma DLL do próprio Streemu (não um arquivo baixado): o FileName é o nome da DLL ("X, Version=...").</summary>
    public static string? MissingPart(Exception e)
    {
        for (Exception? x = e; x != null; x = x.InnerException)
        {
            string? f = (x as FileNotFoundException)?.FileName ?? (x as FileLoadException)?.FileName;
            if (f != null && f.Contains(", Version=")) return f[..f.IndexOf(',')];
        }
        return null;
    }

    /// <summary>O motivo para a tela: nunca vazio (o FileNotFoundException daquele PC veio com Message vazia e a tela mostrou "Não consegui atualizar: .").</summary>
    public static string Describe(Exception e)
    {
        if (MissingPart(e) is { } part) return $"o {UiBrand.Name} não conseguiu carregar uma parte dele mesmo ({part})";
        if (!string.IsNullOrWhiteSpace(e.Message)) return e.Message.TrimEnd('.');
        var b = e.GetBaseException();
        return !string.IsNullOrWhiteSpace(b.Message) ? b.Message.TrimEnd('.') : b.GetType().Name;
    }

    /// <summary>
    /// Canal da atualização (Configurações > Sistema, spec 025): stable vem do repositório público (compilado e atestado lá, qualquer um confere),
    /// nightly vem do repositório de trabalho. Por enquanto os dois recebem toda atualização; o nightly existe para, quando houver mais gente,
    /// testar com quem quiser antes de soltar no stable. Qualquer valor desconhecido é nightly, que é o padrão.
    /// </summary>
    public const string CanalStable = "stable", CanalNightly = "nightly";
    public static string NormalizarCanal(string? canal) => canal == CanalStable ? CanalStable : CanalNightly;

    /// <summary>Pergunta ao Worker do grupo. Nulo = nada mais novo (ou sem servidor/rede: só vai para o log).</summary>
    public static async Task<UpdateInfo?> CheckAsync(RelayConfig.Settings cfg, string? canal, CancellationToken ct)
    {
        // Trocar de canal não volta versão por conta própria: só há aviso com árvore OUTRA e hora mais NOVA (ver abaixo). Para o mesmo código, o público e o
        // repositório de trabalho têm a mesma árvore, então trocar de canal com os dois em dia não baixa nada.
        string url = (Environment.GetEnvironmentVariable("STREEMU_ATUALIZACAO_URL") ?? cfg.Url).TrimEnd('/') + "/atualizacao?canal=" + NormalizarCanal(canal);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // /atualizacao é aberta (spec 022): o app sem conta também se atualiza, então não há credencial aqui
        using var resp = await http.GetAsync(url, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("o servidor do grupo ainda não tem /atualizacao (publique o Worker novo)");
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"o servidor do grupo respondeu {(int)resp.StatusCode}: {body}");
        var j = JsonNode.Parse(body) as JsonObject ?? throw new InvalidOperationException("resposta vazia");
        if (j["nenhuma"]?.GetValue<bool>() == true)
        {
            // com motivo (ex.: Worker sem GITHUB_TOKEN) é um problema do servidor, não "você está em dia"
            if (j["motivo"]?.GetValue<string>() is string motivo) throw new InvalidOperationException("o servidor do grupo não consegue ler as atualizações (" + motivo + ")");
            return null;
        }
        var u = new UpdateInfo(
            j["versao"]?.GetValue<string>() ?? "?", j["commit"]?.GetValue<string>() ?? "", j["arvore"]?.GetValue<string>() ?? "",
            j["hora"]?.GetValue<long>() ?? 0, j["sha256"]?.GetValue<string>() ?? "", j["tamanho"]?.GetValue<long>() ?? 0,
            j["notas"]?.GetValue<string>() ?? "", j["url"]?.GetValue<string>() ?? "",
            Obrigatoria: j["obrigatoria"]?.GetValue<bool>() == true);
        bool newer = u.Arvore.Length > 0 && u.Arvore != BuildInfo.Tree && u.Hora > BuildInfo.CommitTime && u.Url.Length > 0;
        return newer ? u : null;
    }

    /// <summary>Baixa o zip (reaproveita se já baixou) e confere tamanho e SHA-256.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo u, IProgress<int> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        string file = Path.Combine(Folder, $"streemu-{u.Commit[..Math.Min(12, u.Commit.Length)]}.zip");
        if (File.Exists(file) && Sha256(file) == u.Sha256.ToLowerInvariant()) { progress.Report(100); return file; }
        // sem a senha do grupo: o link é assinado pelo GitHub e não precisa de nada
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var resp = await http.GetAsync(u.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"o download respondeu {(int)resp.StatusCode} (o link vence em minutos: tente de novo)");
        long total = resp.Content.Headers.ContentLength ?? u.Tamanho;
        string tmp = file + ".baixando";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[81920];
            long done = 0; int last = -1;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                int pct = total > 0 ? (int)(done * 100 / total) : 0;
                if (pct != last) { last = pct; progress.Report(pct); }
            }
        }
        if (u.Sha256.Length > 0 && Sha256(tmp) != u.Sha256.ToLowerInvariant())
        {
            File.Delete(tmp);
            throw new InvalidOperationException("o arquivo baixado veio diferente do esperado (download incompleto?); tente de novo");
        }
        File.Move(tmp, file, overwrite: true);
        return file;
    }

    static string Sha256(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }

    /// <summary>
    /// Troca o exe e abre a versão nova. O Windows não deixa sobrescrever um exe aberto, mas deixa RENOMEAR:
    /// o exe atual vira Streemu.old.exe, o novo entra no lugar, e o novo é aberto com --atualizado --pid.
    /// Não mexe em ffmpeg\ (o ffmpeg tem a troca dele, <see cref="BinarioUpdater"/>), grupo.json nem no estado. Qualquer erro antes de abrir o novo desfaz a troca.
    /// Quem chama fecha o app logo depois.
    /// <paramref name="restart"/> falso (o instalador): só troca os arquivos, sem abrir o app novo.
    /// <paramref name="hidden"/>: o app novo abre escondido na bandeja (a janela estava escondida; atualizar sozinho não pode "pular" uma janela na cara de quem joga).
    /// </summary>
    public static void ApplyAndRestart(string zip, string commit, bool restart = true, bool hidden = false)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("não sei onde está o Streemu.exe");
        string dir = Path.GetDirectoryName(exe)!;
        string probe = Path.Combine(dir, ".streemu-teste-escrita");
        try { File.WriteAllText(probe, "ok"); File.Delete(probe); }
        catch { throw new InvalidOperationException($"não tenho permissão para escrever em {dir}; mova a pasta do {UiBrand.Name} para Documentos ou C:\\{UiBrand.Name}"); }

        string staging = Path.Combine(Folder, "novo-" + commit[..Math.Min(12, commit.Length)]);
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        ZipFile.ExtractToDirectory(zip, staging);
        string newExe = Directory.GetFiles(staging, ExeName, SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidOperationException("o pacote não tem o Streemu.exe");
        string newRoot = Path.GetDirectoryName(newExe)!;

        string old = Path.Combine(dir, OldName);
        try { if (File.Exists(old)) File.Delete(old); }
        catch { old = Path.Combine(dir, $"Streemu.old-{DateTime.Now:yyyyMMddHHmmss}.exe"); } // o .old anterior ainda aberto

        File.Move(exe, old);
        try
        {
            File.Copy(newExe, exe);
            // LEIA-ME e diagnóstico acompanham a versão; ffmpeg, grupo.json e estado ficam como estão
            foreach (var f in Directory.GetFiles(newRoot, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(newRoot, f);
                if (rel.Equals(ExeName, StringComparison.OrdinalIgnoreCase) || rel.Equals("grupo.json", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("ffmpeg" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                string target = Path.Combine(dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f, target, overwrite: true);
            }
            if (restart)
            {
                string args = $"--atualizado --pid {Environment.ProcessId}" + (hidden ? " " + StartWithWindows.Arg : "");
                var started = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = dir });
                if (started == null) throw new InvalidOperationException($"o Windows não abriu o {UiBrand.Name} novo");
            }
        }
        catch
        {
            try { if (File.Exists(exe)) File.Delete(exe); File.Move(old, exe); } catch { }
            throw;
        }
        Log.Info($"Atualização instalada ({commit[..Math.Min(7, commit.Length)]}); " + (restart ? "abrindo a versão nova. " : "") + $"Se ela não abrir, o {Path.GetFileName(old)} na pasta é a versão anterior.");
    }

    /// <summary>
    /// <c>Streemu.exe --atualizar</c> (o instalador roda no fim): confere, baixa e troca o exe da pasta SEM abrir o app, para quem instala (mesmo com um
    /// instalador antigo) já começar na última versão da main. Códigos: 0 em dia ou atualizado, 1 sem servidor (só em modo de teste sem STREEMU_ATUALIZACAO_URL),
    /// 2 falhou (sem rede, servidor, download). Falhar não atrapalha: o app se atualiza sozinho na primeira abertura.
    /// Depois o ffmpeg (spec 004, seção 8): trocado o exe, quem sabe o ffmpeg desejado é o exe NOVO, então ele roda com <c>--so-ffmpeg</c>;
    /// sem exe novo, este mesmo confere. O ffmpeg não muda o código de saída: se falhar, o app tenta de novo ao abrir.
    /// </summary>
    public static async Task<int> RunHeadlessAsync(bool onlyFfmpeg = false)
    {
        if (!Enabled) { Log.Info("--atualizar: este build não se atualiza (desenvolvimento)."); return 0; }
        var state = AppState.Load();
        var cfg = RelayConfig.Load(state);
        if (cfg == null) { Log.Info("--atualizar: modo de teste sem STREEMU_ATUALIZACAO_URL; não sei a quem perguntar pela versão nova."); return 1; }
        if (!onlyFfmpeg)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            try
            {
                var u = await CheckAsync(cfg, state.CanalAtualizacao, cts.Token);
                if (u == null) Log.Info($"--atualizar: já está na mais nova ({CurrentLabel}).");
                else
                {
                    Log.Info($"--atualizar: baixando {u.Versao} de {u.Quando}.");
                    string zip = await DownloadAsync(u, new Progress<int>(), cts.Token);
                    ApplyAndRestart(zip, u.Commit, restart: false);
                    Log.Info($"--atualizar: pronto, a pasta agora tem a versão de {u.Quando}.");
                    await FfmpegByNewExeAsync();
                    return 0;
                }
            }
            catch (Exception e)
            {
                Log.Info("--atualizar: não consegui (" + e.Message + "). O app se atualiza sozinho na primeira abertura.");
                return 2;
            }
        }
        foreach (var u in BinarioUpdater.Todos)
        {
            try { await u.RunHeadlessAsync(cfg); }
            catch (Exception e) { Log.Info("--atualizar: " + u.Nome + ": " + e.Message); }
        }
        return 0;
    }

    /// <summary>O exe novo (já no lugar deste) troca o ffmpeg que ELE quer. Espera até 9 min (~80 MB); o instalador espera junto.</summary>
    static async Task FfmpegByNewExeAsync()
    {
        try
        {
            string exe = Environment.ProcessPath!;
            using var p = Process.Start(new ProcessStartInfo(exe, "--atualizar --so-ffmpeg") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
            if (p == null) return;
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(9));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { Log.Info("--atualizar: o exe novo ainda trocava o ffmpeg depois de 9 min; segue sozinho."); }
        }
        catch (Exception e) { Log.Info("--atualizar: não consegui pedir ao exe novo para conferir o ffmpeg (" + e.Message + "); o app confere ao abrir."); }
    }

    /// <summary>Depois de atualizar: apaga o exe antigo e os pacotes baixados (o antigo já fechou).</summary>
    public static void CleanupOld()
    {
        string? dir = Path.GetDirectoryName(Environment.ProcessPath ?? "");
        if (dir != null)
            foreach (var f in Directory.GetFiles(dir, "Streemu.old*.exe"))
                try { File.Delete(f); Log.Info($"Atualização: apaguei {Path.GetFileName(f)} (versão anterior)."); } catch { }
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (var d in Directory.GetDirectories(Folder)) try { Directory.Delete(d, true); } catch { }
            // só os pacotes do app: o do ffmpeg (e a parte dele já baixada) é da limpeza do BinarioUpdater
            foreach (var z in Directory.GetFiles(Folder, "streemu-*.zip").OrderByDescending(File.GetLastWriteTime).Skip(1)) try { File.Delete(z); } catch { }
        }
        catch { }
        foreach (var u in BinarioUpdater.Todos) u.Cleanup();
    }
}
