using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Streemu.Net;

namespace Streemu.Util;

/// <summary>
/// Troca o ffmpeg\ffmpeg.exe e o ffmpeg\ffplay.exe pelos que este exe quer (spec 004, seção 8): uma instância para cada, a mesma lógica (baixar, conferir,
/// trocar parado, desfazer se não rodar, desistir depois de 3 falhas). Até 2026-11 a atualização só trocava o exe e um ffmpeg novo só chegava a quem
/// reinstalava. O ffplay é de OUTRO build que o ffmpeg (o de 2026-11-09 cai no Vulkan com NVIDIA, docs/DESCOBERTAS.md, 2026-11-10), por isso tem pin próprio.
/// O desejado vem do exe, não do servidor: código e binário testados juntos chegam juntos, e o servidor só dá o link do pacote.
/// "Em dia" é o SHA-256 do arquivo bater com o pin (<see cref="Binarios"/>), não a versão que ele diz ter.
/// </summary>
public sealed class BinarioUpdater
{
    public static readonly BinarioUpdater Ffmpeg = new(Binario.Ffmpeg), Ffplay = new(Binario.Ffplay);
    public static BinarioUpdater[] Todos => new[] { Ffmpeg, Ffplay };
    public static BinarioUpdater De(Binario b) => b switch { Binario.Ffmpeg => Ffmpeg, Binario.Ffplay => Ffplay, _ => throw new ArgumentException("o mpv tem o instalador dele (MpvInstalador)") };

    /// <summary>Depois de 3 falhas seguidas desiste até o app abrir de novo (um pacote ruim não pode ficar baixando 80 MB a cada 10 min).</summary>
    const int GiveUpAfter = 3;

    readonly Binario _b;
    readonly DefBinario _def;
    readonly string _logTag, _nome, _exe, _novo, _old;
    int _failures;
    /// <summary>Para onde a última troca mandou o arquivo anterior (o desfazer volta ele).</summary>
    string _lastOld;

    BinarioUpdater(Binario b)
    {
        _b = b; _def = Binarios.Def(b);
        _nome = _def.Chave; _exe = _def.Exe;
        _logTag = $"[{_nome}-atualizacao]";
        _novo = $"{_nome}.novo.exe"; _old = $"{_nome}.old.exe";
        _lastOld = _old;
    }

    public string Nome => _nome;

    public bool GaveUp => _failures >= GiveUpAfter;

    /// <summary>O motivo da última falha (a mensagem que foi para o log); nulo se a última tentativa deu certo.</summary>
    public string? UltimaFalha { get; set; }

    /// <summary>O que este exe quer (ver <see cref="Binarios.Pin"/>).</summary>
    public PinBinario? Desired => Binarios.Pin(_b);

    /// <summary>A pasta ffmpeg ao lado do exe, a mesma de onde <see cref="Binarios"/> lê.</summary>
    static string Dir => Path.Combine(AppContext.BaseDirectory, "ffmpeg");
    static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Streemu", "atualizacao");

    ArquivoPin Principal(PinBinario pin) => pin.Arquivos[_exe];

    (string Key, string? Version) _cached = ("", null);

    /// <summary>Versão do arquivo da pasta, pelo <c>-version</c> (só para o log); roda de novo só se o arquivo mudou (tamanho + data).</summary>
    string? CurrentVersion()
    {
        string cur = Path.Combine(Dir, _exe);
        if (!File.Exists(cur)) return null;
        var fi = new FileInfo(cur);
        string key = $"{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{fi.CreationTimeUtc.Ticks}";
        if (_cached.Key == key) return _cached.Version;
        string? v = VersionOf(cur);
        _cached = (key, v);
        return v;
    }

    /// <summary>
    /// O binário a baixar, ou nulo (nada desejado, substituto de teste em uso, já está em dia). Em dia = o SHA-256 bate com o pin; um arquivo diferente
    /// vira .invalido na conferência e conta como faltando.
    /// </summary>
    public PinBinario? Pending()
    {
        var pin = Desired;
        if (pin == null || Binarios.UsaSubstituto(_b)) return null;
        return Binarios.Conferir(_b).Ok ? null : pin;
    }

    /// <summary>
    /// Deixa o binário desejado no lugar. <paramref name="idle"/> é conferido durante o download (ficou ocupado: para e guarda o que baixou) e na troca;
    /// <paramref name="onUi"/> roda a troca na thread da janela, para nenhuma live começar entre conferir "parado" e os dois renomeios.
    /// Verdadeiro = trocou. Erros vão só para o log (e para <see cref="UltimaFalha"/>).
    /// </summary>
    public async Task<bool> RunAsync(RelayConfig.Settings cfg, PinBinario pin, Func<bool> idle, Func<Func<bool>, Task<bool>> onUi, CancellationToken ct)
    {
        if (GaveUp) { UltimaFalha = "falhou 3 vezes seguidas"; return false; }
        string antes = CurrentVersion() ?? "nenhum";
        UltimaFalha = null;
        try
        {
            Directory.CreateDirectory(Dir);
            string? novo = await Task.Run(() => ReadyOnDisk(pin), ct);
            if (novo == null)
            {
                if (!idle()) return false;
                // fora da thread da janela: 80 MB e o SHA-256 deles não podem travar a tela (o laço da atualização volta nela a cada await)
                string? zip = await Task.Run(() => DownloadAsync(cfg, pin, idle, ct), ct);
                if (zip == null) { Log.Info($"{_logTag} o app ficou ocupado: parei o download (a parte baixada fica guardada)."); return false; }
                novo = await Task.Run(() => Prepare(zip, pin), ct);
            }
            if (!await onUi(() => { if (!idle()) return false; Swap(novo); return true; }))
            {
                Log.Info($"{_logTag} o app ficou ocupado antes da troca; o arquivo novo fica pronto em ffmpeg\\{_novo} e entra na próxima vez que estiver parado.");
                return false;
            }
            string? falha = await Task.Run(() => SmokeTest(Path.Combine(Dir, _exe), pin.Versao, out var why) ? null : why, ct);
            if (falha != null)
            {
                await onUi(() => { Undo(); return true; });
                throw new InvalidOperationException($"o {_nome} novo não rodou depois da troca ({falha}); voltei o anterior");
            }
            Log.Info($"{_logTag} trocado: {antes} → {pin.Versao}. O anterior fica em ffmpeg\\{Path.GetFileName(_lastOld)} por 7 dias.");
            _failures = 0;
            if (Directory.Exists(Folder)) foreach (var z in Directory.GetFiles(Folder, $"{_nome}-*.zip")) try { File.Delete(z); } catch { }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch (Exception e)
        {
            _failures++;
            UltimaFalha = e.Message;
            Log.Info($"{_logTag} não consegui trocar {antes} → {pin.Versao}: {e.Message}." + (GaveUp ? " Desisto até o app abrir de novo." : " Tento de novo depois."));
            return false;
        }
    }

    /// <summary>Para o <c>--atualizar</c> do instalador: sem janela, nada roda, troca direto.</summary>
    public async Task RunHeadlessAsync(RelayConfig.Settings cfg)
    {
        var pin = Pending();
        if (pin == null) { Log.Info($"{_logTag} --atualizar: {_nome} em dia ({(Desired == null ? "nenhum desejado" : CurrentVersion() ?? "fora da pasta do app")})."); return; }
        // ~80 MB: mais folga que o exe
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        Log.Info($"{_logTag} --atualizar: trocando o {_nome} ({CurrentVersion() ?? "nenhum"} → {pin.Versao}).");
        await RunAsync(cfg, pin, () => true, f => Task.FromResult(f()), cts.Token);
    }

    /// <summary>
    /// Binário desejado que já está na pasta, sem baixar: o .novo.exe de uma troca adiada, ou o .old.exe (voltar atrás para o anterior não
    /// baixa nada). Devolve o .novo.exe conferido, ou nulo.
    /// </summary>
    string? ReadyOnDisk(PinBinario pin)
    {
        string novo = Path.Combine(Dir, _novo);
        if (File.Exists(novo))
        {
            if (Matches(novo, pin) && SmokeTest(novo, pin.Versao, out _)) return novo;
            File.Delete(novo);
        }
        foreach (var f in Directory.GetFiles(Dir, $"{_nome}.old*.exe").Concat(Directory.GetFiles(Dir, $"{_nome}.ruim*.exe")))
        {
            if (!Matches(f, pin)) continue;
            File.Move(f, novo);
            if (SmokeTest(novo, pin.Versao, out _)) { Log.Info($"{_logTag} o {_nome} desejado já estava na pasta ({Path.GetFileName(f)}): volto ele sem baixar."); return novo; }
            File.Delete(novo);
        }
        return null;
    }

    bool Matches(string file, PinBinario pin)
    {
        var p = Principal(pin);
        return (p.Tamanho <= 0 || new FileInfo(file).Length == p.Tamanho) && ConferidorDeBinario.Sha256(file) == p.Sha256;
    }

    /// <summary>O servidor do grupo dá o link assinado do zip da release fixa (o repositório é privado: o app não lê o GitHub sozinho).</summary>
    async Task<string> LinkAsync(RelayConfig.Settings cfg, PinBinario pin, CancellationToken ct)
    {
        string url = (Environment.GetEnvironmentVariable("STREEMU_ATUALIZACAO_URL") ?? cfg.Url).TrimEnd('/') + $"/{_def.Rota}?tag=" + Uri.EscapeDataString(pin.Tag);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var resp = await RelayConfig.EnviarAsync(http, () => new HttpRequestMessage(HttpMethod.Get, url), ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException($"o servidor do grupo ainda não tem /{_def.Rota} (publique o Worker novo)");
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"o servidor do grupo respondeu {(int)resp.StatusCode}: {body}");
        var j = JsonNode.Parse(body) as JsonObject ?? throw new InvalidOperationException("resposta vazia");
        if (j["url"]?.GetValue<string>() is { Length: > 0 } link) return link;
        throw new InvalidOperationException("o servidor do grupo não tem o pacote " + pin.Tag + (j["motivo"]?.GetValue<string>() is string m ? $" ({m})" : ""));
    }

    /// <summary>
    /// Baixa o zip (~80 MB) e confere o SHA-256. Continua de onde parou (Range) se já tinha uma parte. Para (nulo) assim que
    /// <paramref name="idle"/> fica falso: baixar 80 MB não pode disputar a rede com uma live.
    /// </summary>
    async Task<string?> DownloadAsync(RelayConfig.Settings cfg, PinBinario pin, Func<bool> idle, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        string file = Path.Combine(Folder, SafeName(pin.Tag) + ".zip");
        if (File.Exists(file) && ConferidorDeBinario.Sha256(file) == pin.ZipSha256) return file;
        string tmp = file + ".baixando";
        long have = File.Exists(tmp) ? new FileInfo(tmp).Length : 0;
        if (pin.ZipTamanho > 0 && have >= pin.ZipTamanho) { File.Delete(tmp); have = 0; }

        string link = await LinkAsync(cfg, pin, ct);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var req = new HttpRequestMessage(HttpMethod.Get, link);
        if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"o download respondeu {(int)resp.StatusCode}");
        bool append = have > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
        if (!append) have = 0;
        Log.Info($"{_logTag} baixando {pin.Tag} ({pin.ZipTamanho / 1048576.0:N0} MB)" + (append ? $", continuando de {have / 1048576.0:N0} MB" : "") + ".");
        var sw = Stopwatch.StartNew();
        long start = have;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(tmp, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buf = new byte[256 * 1024];
            long sinceCheck = 0;
            while (true)
            {
                // sem dado por 60 s = conexão morta (o HttpClient não tem tempo limite aqui: o arquivo é grande)
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stall.CancelAfter(TimeSpan.FromSeconds(60));
                int n;
                try { n = await src.ReadAsync(buf, stall.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new InvalidOperationException("o download parou de chegar (60 s sem dados)"); }
                if (n == 0) break;
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                have += n; sinceCheck += n;
                if (sinceCheck >= 4 * 1048576) { sinceCheck = 0; if (!idle()) return null; }
            }
        }
        Log.Info($"{_logTag} baixei {(have - start) / 1048576.0:N0} MB em {sw.Elapsed.TotalSeconds:N0} s.");
        if (ConferidorDeBinario.Sha256(tmp) != pin.ZipSha256)
        {
            File.Delete(tmp);
            throw new InvalidOperationException("o pacote baixado veio diferente do esperado (SHA-256); apaguei e baixo de novo depois");
        }
        File.Move(tmp, file, overwrite: true);
        return file;
    }

    /// <summary>Extrai só o exe do pacote para &lt;nome&gt;.novo.exe (mesmo disco: a troca vira um renomear) e confere. Não toca no exe atual.</summary>
    string Prepare(string zip, PinBinario pin)
    {
        string probe = Path.Combine(Dir, ".streemu-teste-escrita");
        try { File.WriteAllText(probe, "ok"); File.Delete(probe); }
        catch { throw new InvalidOperationException($"sem permissão para escrever na pasta do {_nome}"); }
        string novo = Path.Combine(Dir, _novo);
        using (var z = ZipFile.OpenRead(zip))
        {
            var e = z.Entries.FirstOrDefault(x => x.Name.Equals(_exe, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"o pacote não tem o {_exe}");
            e.ExtractToFile(novo, overwrite: true);
        }
        if (!Matches(novo, pin)) { File.Delete(novo); throw new InvalidOperationException($"o {_exe} do pacote não é o esperado (SHA-256)"); }
        // antivírus que bloqueia o exe novo para aqui, com o atual intacto
        if (!SmokeTest(novo, pin.Versao, out var why)) { try { File.Delete(novo); } catch { } throw new InvalidOperationException($"o {_nome} novo não rodou ({why}); antivírus?"); }
        return novo;
    }

    /// <summary>Dois renomeios (milissegundos). Um exe ainda rodando continua do arquivo renomeado: o Windows deixa renomear exe aberto.</summary>
    void Swap(string novo)
    {
        string cur = Path.Combine(Dir, _exe);
        string old = Path.Combine(Dir, _old);
        try { if (File.Exists(old)) File.Delete(old); }
        catch { old = Path.Combine(Dir, $"{_nome}.old-{DateTime.Now:yyyyMMddHHmmss}.exe"); } // o anterior ainda aberto
        bool had = File.Exists(cur);
        if (had) File.Move(cur, old);
        _lastOld = old;
        try { File.Move(novo, cur); }
        catch { if (had) try { File.Move(old, cur); } catch { } throw; }
        // a limpeza dos 7 dias conta a partir da troca (voltar o .old para o lugar manteria a data antiga)
        try { File.SetCreationTime(cur, DateTime.Now); } catch { }
        Forget();
    }

    /// <summary>O arquivo trocado não rodou: ele vira .ruim.exe e o anterior volta.</summary>
    void Undo()
    {
        string cur = Path.Combine(Dir, _exe);
        string old = Path.Combine(Dir, Path.GetFileName(_lastOld));
        if (!File.Exists(old)) return;
        string bad = Path.Combine(Dir, $"{_nome}.ruim-{DateTime.Now:yyyyMMddHHmmss}.exe");
        try { File.Move(cur, bad); } catch { }
        File.Move(old, cur);
        Forget();
    }

    /// <summary>As checagens do ffmpeg anterior não valem para o novo (o 2026-11-09 recusa o -thread_queue_size que o 2025-12-14 aceitava).</summary>
    void Forget()
    {
        _cached = ("", null);
        if (_b == Binario.Ffmpeg) Media.Tools.ResetProbes();
    }

    /// <summary>"ffmpeg version X ..." (ou "ffplay version X ...") → X. Nulo se não rodou em 10 s.</summary>
    string? VersionOf(string exe)
    {
        var r = Run(exe, "-hide_banner", "-version");
        if (r is not { Exit: 0 } ok) return null;
        string first = ok.Output.Split('\n').FirstOrDefault()?.Trim() ?? "";
        string prefixo = _nome + " version ";
        return first.StartsWith(prefixo) ? first[prefixo.Length..].Split(' ')[0] : null;
    }

    /// <summary>A versão é a desejada e, no ffmpeg, um quadro de teste codifica (o exe roda de verdade, com as DLLs dele). O ffplay só abre janela com vídeo: fica na versão.</summary>
    bool SmokeTest(string exe, string versao, out string why)
    {
        string? v = VersionOf(exe);
        if (v != versao) { why = v == null ? "não abriu" : $"diz ser {v}"; return false; }
        if (_b == Binario.Ffmpeg)
        {
            var r = Run(exe, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "nullsrc=s=64x64:d=0.1", "-frames:v", "1", "-f", "null", "-");
            if (r is not { Exit: 0 }) { why = r == null ? "travou no teste" : $"teste saiu com {r.Value.Exit}: {r.Value.Output.Trim()}"; return false; }
        }
        why = "";
        return true;
    }

    static (int Exit, string Output)? Run(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(10000)) { try { p.Kill(true); } catch { } return null; }
            return (p.ExitCode, output + err.Result);
        }
        catch { return null; }
    }

    static string SafeName(string tag) => string.Concat(tag.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_'));

    /// <summary>
    /// Apaga o arquivo anterior 7 dias depois da troca (até lá dá para voltar à mão) e o que sobrou de trocas: .novo.exe que não serve mais,
    /// .invalido, pacotes de outro binário. Chamado junto da limpeza do exe antigo.
    /// </summary>
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(Dir))
            {
                string cur = Path.Combine(Dir, _exe);
                bool settled = File.Exists(cur) && DateTime.Now - File.GetCreationTime(cur) > TimeSpan.FromDays(7);
                if (settled)
                    foreach (var f in Directory.GetFiles(Dir, $"{_nome}.old*.exe").Concat(Directory.GetFiles(Dir, $"{_nome}.ruim*.exe")))
                        try { File.Delete(f); Log.Info($"{_logTag} apaguei {Path.GetFileName(f)} (o {_nome} atual está em uso há mais de 7 dias)."); } catch { }
                foreach (var f in Directory.GetFiles(Dir, _exe + ".invalido"))
                    if (DateTime.Now - File.GetLastWriteTime(f) > TimeSpan.FromDays(7)) try { File.Delete(f); } catch { }
                string novo = Path.Combine(Dir, _novo);
                if (File.Exists(novo) && Pending() == null) try { File.Delete(novo); } catch { }
            }
            if (Directory.Exists(Folder))
            {
                string keep = Desired == null ? "" : SafeName(Desired.Tag);
                foreach (var z in Directory.GetFiles(Folder, $"{_nome}-*"))
                    if (keep.Length == 0 || !Path.GetFileName(z).StartsWith(keep + ".zip", StringComparison.OrdinalIgnoreCase) || Pending() == null)
                        try { File.Delete(z); } catch { }
            }
        }
        catch { }
    }
}
