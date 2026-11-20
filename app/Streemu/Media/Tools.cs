using System.Diagnostics;
using System.Text.RegularExpressions;
using Streemu.Util;

namespace Streemu.Media;

/// <summary>
/// O ffmpeg e o ffplay que vêm com o Streemu (ffmpeg\ ao lado do exe) e inicia processos com log. O caminho só sai daqui depois de o SHA-256 bater com o
/// pin (<see cref="Binarios"/>): nunca o PATH, nem a raiz do app, nem um nome solto que o Windows resolveria sozinho (até 2026-11-18 era assim e qualquer
/// ffmpeg rodava). Sem arquivo válido lança <see cref="BinarioIndisponivelException"/> com o motivo e o que fazer. Chame fora da thread da janela.
/// </summary>
public static class Tools
{
    public static string Ffmpeg => Binarios.Exigir(Binario.Ffmpeg);
    public static string Ffplay => Binarios.Exigir(Binario.Ffplay);

    /// <summary>O caminho conferido, ou nulo (para quem só descreve o estado e não deve lançar).</summary>
    public static string? FfmpegOuNulo => Binarios.Caminho(Binario.Ffmpeg);

    /// <summary>
    /// "-thread_queue_size 64" para pôr antes de uma entrada, só se este ffmpeg aceitar. O ffmpeg master de novembro de 2026 (gyan.dev
    /// 2026-11-09-git-16a345b022) tirou a opção das entradas (agora ela é só do muxer) e recusa o comando inteiro ("cannot be applied to
    /// input url"): sem esta checagem, nenhuma live abriria com ele. O zip de atualização não leva o ffmpeg, então cada amigo pode estar com
    /// uma versão; no build 2025-12-14 a opção continua indo.
    /// </summary>
    public static string[] InputQueueArgs => InputQueueOk.Value ? new[] { "-thread_queue_size", "64" } : Array.Empty<string>();

    static Lazy<bool> InputQueueOk = new(ProbeInputQueue);

    static bool ProbeInputQueue()
    {
        var r = Ask("-hide_banner", "-loglevel", "error", "-thread_queue_size", "64", "-f", "lavfi", "-i", "nullsrc=s=16x16:d=0.1", "-frames:v", "1", "-f", "null", "-");
        bool ok = r == null || r.Value.Exit == 0 || !r.Value.Output.Contains("thread_queue_size");
        if (!ok) Log.Info("[ffmpeg] este ffmpeg não aceita -thread_queue_size na entrada (versão nova): a captura e o áudio vão sem ele");
        return ok;
    }

    /// <summary>
    /// O gfxcapture deste ffmpeg repete o último quadro quando a janela não manda nenhum (opção dup_frames, ffmpeg master de 24/10/2026, ligada por padrão)?
    /// Com ela a janela parada não segura mais o áudio (o agendador do ffmpeg 8 não deixa um stream passar 100 ms à frente do outro), mas o "dup=" do ffmpeg
    /// principal deixa de contar repetidos: quem conta os quadros novos passa a ser o <see cref="FrameSensor"/>, como no ddagrab.
    /// </summary>
    public static bool GfxCaptureRepeats => GfxDup.Value;

    static Lazy<bool> GfxDup = new(ProbeGfxDup);

    static bool ProbeGfxDup()
    {
        bool dup = Ask("-hide_banner", "-h", "filter=gfxcapture") is { } r && r.Output.Contains("dup_frames");
        if (dup) Log.Info("[ffmpeg] o gfxcapture deste ffmpeg repete o quadro com a janela parada (dup_frames): o sensor conta os quadros novos");
        return dup;
    }

    /// <summary>
    /// O ffmpeg da pasta foi trocado (spec 004, seção 8): as respostas acima eram do anterior e a próxima live pergunta de novo. Sem isso a primeira
    /// live depois de trocar o 2025-12-14 pelo 2026-11-09 mandaria o -thread_queue_size na entrada e o ffmpeg novo recusaria o comando.
    /// </summary>
    public static void ResetProbes()
    {
        InputQueueOk = new(ProbeInputQueue);
        GfxDup = new(ProbeGfxDup);
    }

    /// <summary>Roda o ffmpeg só para perguntar algo (opção aceita, ajuda de um filtro). Nulo se não rodou ou passou de 10 s.</summary>
    static (int Exit, string Output)? Ask(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(10000)) { KillQuietly(p); return null; }
            return (p.ExitCode, output + err.Result);
        }
        catch { return null; }
    }

    public static bool Available(out string message)
    {
        bool ok = Binarios.Caminho(Binario.Ffmpeg) != null && Binarios.Caminho(Binario.Ffplay) != null;
        message = ok ? "ffmpeg e ffplay conferidos."
            : Binarios.Caminho(Binario.Ffmpeg) == null ? Binarios.Motivo(Binario.Ffmpeg) : Binarios.Motivo(Binario.Ffplay);
        return ok;
    }

    /// <param name="binaryStdout">true: o stdout fica para quem chamou ler como bytes (ex.: mpegts da captura).</param>
    public static Process Start(string exe, IEnumerable<string> args, string tag, bool redirectStdin, Action<string>? onStderr = null, bool hideWindow = true, bool binaryStdout = false, Action<string>? onStdout = null)
    {
        // só o caminho completo que o Binarios deu: um nome solto faria o Windows procurar o exe no PATH
        if (!Path.IsPathFullyQualified(exe)) throw new BinarioIndisponivelException($"caminho de programa sem a pasta ({Path.GetFileName(exe)}): o Streemu só roda o que vem com ele");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = hideWindow,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = redirectStdin,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // com vários espectadores há uma senha por túnel: esconde qualquer passphrase=..., não só a última
        string shown = System.Text.RegularExpressions.Regex.Replace(string.Join(' ', psi.ArgumentList.Select(Quote)), "passphrase=[^&\"\\s]+", "passphrase=***");
        Log.Info($"[{tag}] {Path.GetFileName(exe)} {(string.IsNullOrEmpty(PassphraseHint) ? shown : shown.Replace(PassphraseHint, "***"))}");
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var filter = new ChildLogFilter(tag);
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) { filter.Flush(); return; }
            onStderr?.Invoke(e.Data);
            if (!e.Data.StartsWith("frame=") && !e.Data.Contains("A-V:") && !string.IsNullOrWhiteSpace(e.Data) && !IsFfplayChatter(e.Data)) filter.Write(ForLog(e.Data));
        };
        p.OutputDataReceived += (_, e) => { if (e.Data is { } line) onStdout?.Invoke(line); };
        p.Start();
        Native.AddToKillOnCloseJob(p);
        p.BeginErrorReadLine();
        if (!binaryStdout) p.BeginOutputReadLine();
        return p;
    }

    /// <summary>
    /// O que o ffplay em "-loglevel level+info" escreve e não interessa ao log: tudo marcado "[info]" (a descrição da entrada, que traz a URL do
    /// SRT com a senha; ~15 linhas do Vulkan/libplacebo a cada abertura; a linha de estado, ~30 por segundo) e o código de limpar a linha do
    /// terminal. Avisos e erros continuam indo. O Viewer lê o tamanho do vídeo da linha "Stream #0:0 ... Video:" antes de ela ser descartada.
    /// </summary>
    internal static bool IsFfplayChatter(string l) =>
        l.Contains("[info] ", StringComparison.Ordinal) || l.Contains(" aq=", StringComparison.Ordinal) || l.StartsWith('\u001b')
        // "[warning]" que o Vulkan do ffplay só escreve fora do "-loglevel warning": 5 por abertura, extensões do Linux que o Windows não tem
        || l.Contains("not found, excluding.", StringComparison.Ordinal);

    /// <summary>
    /// A linha como vai para o log: sem a marca de nível ("[warning] ", "[error] ") do "-loglevel level+...", para o filtro (dump do Vulkan) a ver
    /// como antes, e com a senha do SRT escondida. O ffplay escreve a URL inteira quando não conecta ("Connection to srt://...passphrase=...
    /// failed: I/O error"): a senha ia em texto puro para o log e para a telemetria (achado em 2026-11-11 nos logs deste PC).
    /// </summary>
    internal static string ForLog(string l)
    {
        var timeout = TimeSpan.FromMilliseconds(50);
        l = System.Text.RegularExpressions.Regex.Replace(l, @"\[(?:warning|error|fatal|panic)\] ", "", System.Text.RegularExpressions.RegexOptions.None, timeout);
        return System.Text.RegularExpressions.Regex.Replace(l, "passphrase=[^&\"'\\s]+", "passphrase=***", System.Text.RegularExpressions.RegexOptions.None, timeout);
    }

    /// <summary>Senha atual do SRT, escondida nos logs.</summary>
    public static string? PassphraseHint { get; set; }

    static string Quote(string a) => a.Contains(' ') ? $"\"{a}\"" : a;

    public static void KillQuietly(Process? p)
    {
        try { if (p != null && !p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }
}

/// <summary>
/// Filtra o que um ffmpeg/ffplay grava no log do app. Motivo (teste 0.1.8 num PC do grupo): o ffplay sem memória de vídeo
/// repetiu em loop os blocos "Memory heaps / Memory pool / Backtrace" do Vulkan e o log do relatório ficou só com isso.
///   - descarta esses blocos (linhas recuadas depois do "[Nome @ 0x...]") e conta;
///   - linha igual (ignorando números e endereços) em menos de 60 s não entra de novo: sai "(repetiu N vezes)";
///   - no máximo <see cref="MaxPerMinute"/> linhas por minuto por processo.
/// </summary>
sealed partial class ChildLogFilter
{
    const int MaxPerMinute = 30;
    readonly string _tag;
    readonly object _gate = new();
    readonly Dictionary<string, (DateTime First, int Repeats, string Sample)> _seen = new();
    DateTime _windowStart = DateTime.Now;
    int _inWindow, _droppedRate, _droppedDump;

    public ChildLogFilter(string tag) => _tag = tag;

    [GeneratedRegex(@"^\[[^\]]+ @ [0-9a-fA-Fx]+\]\s?")] private static partial Regex PrefixRx();
    [GeneratedRegex(@"0x[0-9a-fA-F]+|[0-9a-fA-F]{8,}|\d+(\.\d+)?")] private static partial Regex NumbersRx();

    static bool IsVulkanDump(string body) =>
        body.StartsWith("  ", StringComparison.Ordinal) // continuação recuada (heaps, pools, backtrace, "for malloc:")
        || body.StartsWith("Memory heaps supported", StringComparison.Ordinal)
        || body.StartsWith("Memory pool", StringComparison.Ordinal)
        || body.StartsWith("Backtrace", StringComparison.Ordinal);

    public void Write(string line)
    {
        var batch = new List<string>();
        lock (_gate)
        {
            var now = DateTime.Now;
            string body = PrefixRx().Replace(line, "");
            if (IsVulkanDump(body)) { _droppedDump++; return; }
            string key = NumbersRx().Replace(body, "#");
            if (_seen.TryGetValue(key, out var s) && now - s.First < TimeSpan.FromSeconds(60))
            {
                _seen[key] = (s.First, s.Repeats + 1, s.Sample);
                return;
            }
            if (s.Repeats > 0) batch.Add($"[{_tag}] (repetiu {s.Repeats} vezes desde {s.First:HH:mm:ss}) {s.Sample}");
            _seen[key] = (now, 0, body);
            if (_seen.Count > 200) Expire(now, batch, force: true);

            if (now - _windowStart >= TimeSpan.FromMinutes(1))
            {
                Summaries(batch);
                Expire(now, batch, force: false);
                _windowStart = now; _inWindow = 0;
            }
            if (_inWindow >= MaxPerMinute) { _droppedRate++; }
            else { _inWindow++; batch.Add($"[{_tag}] {line}"); }
        }
        foreach (var l in batch) Log.Info(l);
    }

    /// <summary>Fim do processo: grava as contagens que ficaram pendentes.</summary>
    public void Flush()
    {
        var batch = new List<string>();
        lock (_gate)
        {
            foreach (var (_, s) in _seen) if (s.Repeats > 0) batch.Add($"[{_tag}] (repetiu {s.Repeats} vezes desde {s.First:HH:mm:ss}) {s.Sample}");
            _seen.Clear();
            Summaries(batch);
        }
        foreach (var l in batch) Log.Info(l);
    }

    void Summaries(List<string> batch)
    {
        if (_droppedDump > 0) batch.Add($"[{_tag}] ({_droppedDump} linhas de diagnóstico do Vulkan descartadas)");
        if (_droppedRate > 0) batch.Add($"[{_tag}] ({_droppedRate} linhas descartadas: mais de {MaxPerMinute} por minuto)");
        _droppedDump = 0; _droppedRate = 0;
    }

    void Expire(DateTime now, List<string> batch, bool force)
    {
        foreach (var k in _seen.Where(kv => force || now - kv.Value.First >= TimeSpan.FromSeconds(60)).Select(kv => kv.Key).ToList())
        {
            var s = _seen[k];
            if (s.Repeats > 0) batch.Add($"[{_tag}] (repetiu {s.Repeats} vezes desde {s.First:HH:mm:ss}) {s.Sample}");
            _seen.Remove(k);
        }
    }
}
