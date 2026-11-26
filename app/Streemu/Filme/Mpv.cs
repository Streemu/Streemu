using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Streemu.Util;

namespace Streemu.Filme;

/// <summary>
/// Um mpv desenhando dentro de uma janela do app (--wid) e controlado pelo pipe (--input-ipc-server), spec 010, seção 9.
/// O ffplay não serve aqui: não aceita comando enquanto toca nem entra numa janela.
/// </summary>
public sealed class Mpv : IDisposable
{
    readonly Process _p;
    readonly NamedPipeClientStream _pipe;
    readonly StreamWriter _w;
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pend = new();
    int _id;
    static int _seq;

    public event Action? Saiu;
    /// <summary>"script-message" de um script do mpv (a interface do <see cref="MpvUi"/>), na thread do pipe.</summary>
    public event Action<string[]>? Mensagem;

    /// <summary>
    /// Só o mpv\mpv.exe ao lado do exe (onde o <see cref="MpvInstalador"/> põe) e só se ele e a DLL dele baterem com o SHA-256 do pin; nulo se não (aí o
    /// filme baixa o player). Nada de PATH. STREEMU_MPV vale só em teste e Debug (ver <see cref="Binarios.SubstituicaoPermitida(bool,bool)"/>).
    /// Chame fora da thread da janela quando a conferência ainda puder estar rodando (a da abertura a adianta).
    /// </summary>
    public static string? Find() => Binarios.Caminho(Binario.Mpv);

    Mpv(Process p, NamedPipeClientStream pipe)
    {
        _p = p; _pipe = pipe;
        _w = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var r = new StreamReader(pipe, Encoding.UTF8);
        new Thread(() =>
        {
            try
            {
                string? l;
                while ((l = r.ReadLine()) != null)
                {
                    using var d = JsonDocument.Parse(l);
                    if (d.RootElement.TryGetProperty("request_id", out var q) && q.ValueKind == JsonValueKind.Number && _pend.TryRemove(q.GetInt32(), out var t))
                        t.TrySetResult(d.RootElement.Clone());
                    else if (d.RootElement.TryGetProperty("event", out var evento) && (evento.ValueEquals("file-loaded") || evento.ValueEquals("end-file")))
                    {
                        // o loadfile responde antes de o arquivo abrir: quem espera o arquivo novo (CarregarAsync) fica sabendo por aqui
                        var espera = _carregando;
                        if (espera != null && evento.ValueEquals("file-loaded")) espera.TrySetResult();
                        else if (espera != null && d.RootElement.TryGetProperty("reason", out var motivo) && motivo.ValueEquals("error"))
                            espera.TrySetException(new IOException("o player não conseguiu abrir o arquivo"));
                    }
                    else if (Mensagem != null && d.RootElement.TryGetProperty("event", out var ev) && ev.ValueEquals("client-message")
                        && d.RootElement.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array)
                    {
                        string[] args = a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).ToArray();
                        try { Mensagem(args); } catch (Exception e) { Log.Info($"[filme] mensagem do mpv ({string.Join(' ', args)}) deu erro: {e.Message}"); }
                    }
                }
            }
            catch { }
            foreach (var k in _pend.Keys) if (_pend.TryRemove(k, out var t)) t.TrySetException(new IOException("o mpv fechou"));
            Saiu?.Invoke();
        }) { IsBackground = true, Name = "mpv-pipe" }.Start();
    }

    /// <summary>
    /// Abre o arquivo pausado. Sempre com --no-config: um mpv.conf antigo no PC já quebrou o --version (spike S6).
    /// A posição se lê pelo audio-pts: o time-pos anda de quadro em quadro (42 ms a 24 FPS; spike S1).
    /// </summary>
    /// <param name="extra">Opções a mais (o modo "pela transmissão" passa <c>--rebase-start-time=no</c>, para o time-pos ser a posição do filme e não zerar a cada conexão nova).</param>
    public static async Task<Mpv> OpenAsync(string exe, string file, IntPtr wid, int volume, IEnumerable<string>? extra = null)
    {
        string pipe = $"streemu-filme-{Environment.ProcessId}-{Interlocked.Increment(ref _seq)}";
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        if (MpvUi.Ligada) extra = (extra ?? Array.Empty<string>()).Concat(MpvUi.Opcoes());
        foreach (var a in new[]
        {
            "--no-config", $"--wid={wid.ToInt64()}", $@"--input-ipc-server=\\.\pipe\{pipe}", "--pause", "--keep-open=yes", "--hr-seek=yes",
            // --ytdl=no: o mpv nunca chama youtube-dl/yt-dlp por causa de uma URL que veio de um amigo. --load-scripts=no só desliga os
            // scripts automáticos das pastas do mpv; o streemu-osc.lua entra por --script= (conferido: continua rodando).
            "--ytdl=no", "--load-scripts=no",
            "--osc=no", "--input-default-bindings=no", "--input-vo-keyboard=no", "--input-cursor=no", "--cursor-autohide=no",
            "--vo=gpu-next", "--hwdec=auto-safe", "--video-sync=display-resample", "--audio-pitch-correction=yes",
            $"--volume={volume}", "--msg-level=all=warn",
        }.Concat(extra ?? Array.Empty<string>()).Concat(new[] { "--", file })) psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("o mpv não abriu");
        var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try { await client.ConnectAsync(200); break; }
            catch (TimeoutException) when (sw.ElapsedMilliseconds < 8000 && !p.HasExited) { }
            catch (TimeoutException) { try { p.Kill(); } catch { } throw new InvalidOperationException("o mpv não respondeu (antivírus bloqueando?)"); }
        }
        Log.Info($"[filme] mpv aberto em {sw.ElapsedMilliseconds} ms: {(file.StartsWith("srt://", StringComparison.Ordinal) ? "(fluxo do anfitrião)" : "(arquivo local)")}"); // o nome do arquivo é da pessoa e não vai ao log (spec 018); a URL do fluxo leva a senha do túnel: nunca no log
        return new Mpv(p, client);
    }

    public Task<JsonElement> Cmd(params object[] args)
    {
        int i = Interlocked.Increment(ref _id);
        var t = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pend[i] = t;
        try { lock (_w) _w.WriteLine(JsonSerializer.Serialize(new { command = args, request_id = i })); }
        catch (Exception e) { _pend.TryRemove(i, out _); t.TrySetException(e); }
        return t.Task;
    }

    /// <summary>Propriedade numérica; NaN se o mpv não tem (ex.: audio-pts num arquivo sem som) ou fechou.</summary>
    public async Task<double> NumAsync(string prop)
    {
        try
        {
            var r = await Cmd("get_property", prop);
            return r.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : double.NaN;
        }
        catch { return double.NaN; }
    }

    /// <summary>Propriedade como texto (string direto; número ou objeto em JSON); nulo se não tem ou fechou.</summary>
    public async Task<string?> TextoAsync(string prop)
    {
        try
        {
            var r = await Cmd("get_property", prop);
            if (!r.TryGetProperty("data", out var d)) return null;
            return d.ValueKind switch { JsonValueKind.String => d.GetString(), JsonValueKind.Null or JsonValueKind.Undefined => null, _ => d.GetRawText() };
        }
        catch { return null; }
    }

    /// <summary>
    /// O que o player está usando, para a telemetria (spec 009): decodificador na GPU ou na CPU, codec, resolução, cor (HDR), som.
    /// Sem caminho de arquivo (regra 9 do AGENTS: o caminho tem o nome do usuário).
    /// </summary>
    public async Task<System.Text.Json.Nodes.JsonObject> InfoAsync()
    {
        var o = new System.Text.Json.Nodes.JsonObject();
        foreach (var p in new[] { "hwdec-current", "video-codec", "width", "height", "container-fps", "video-params/pixelformat", "video-params/primaries",
                     "video-params/gamma", "audio-codec-name", "audio-params/channel-count", "audio-params/samplerate", "current-ao", "current-vo", "duration", "file-format" })
            o[p] = await TextoAsync(p);
        return o;
    }

    public async Task<bool> BoolAsync(string prop)
    {
        try { var r = await Cmd("get_property", prop); return r.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.True; }
        catch { return false; }
    }

    volatile TaskCompletionSource? _carregando;

    /// <summary>
    /// Troca o arquivo neste mesmo player (loadfile replace), sem fechar o mpv nem a janela: é o que a fila de episódios usa. Já abre pausado (a opção --pause vale para o
    /// processo e o loadfile não a tira). Espera o evento file-loaded, e não a resposta do comando, que sai antes de o arquivo abrir.
    /// </summary>
    public async Task CarregarAsync(string arquivo, int maxMs = 15000)
    {
        var espera = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _carregando = espera;
        try
        {
            await Set("pause", true);
            var r = await Cmd("loadfile", arquivo, "replace");
            if (r.TryGetProperty("error", out var erro) && erro.ValueKind == JsonValueKind.String && erro.GetString() != "success")
                throw new IOException($"o player recusou o arquivo ({erro.GetString()})");
            if (await Task.WhenAny(espera.Task, Task.Delay(maxMs)) != espera.Task) throw new TimeoutException("o player demorou mais de 15 s para abrir o arquivo");
            await espera.Task;
        }
        finally { if (ReferenceEquals(_carregando, espera)) _carregando = null; }
    }

    public Task Set(string prop, object value) => Safe(Cmd("set_property", prop, value));
    public Task Seek(double pos) => Safe(Cmd("seek", pos, "absolute+exact"));
    static async Task Safe(Task t) { try { await t; } catch { } }

    /// <summary>
    /// Espera o mpv terminar de ir para o ponto novo (até <paramref name="maxMs"/>, ou até <paramref name="desistir"/> dizer que não vale mais).
    /// Devolve falso se o prazo acabou com o mpv ainda procurando: quem chama não pode tratar isso como "chegou" (teste de 11/11: o prazo
    /// acabava com seeking=true e a tela do filme pela transmissão dava o ponto por pronto parada 24 s antes do anfitrião).
    /// </summary>
    public async Task<bool> WaitSeekAsync(int maxMs = 5000, Func<bool>? desistir = null)
    {
        var sw = Stopwatch.StartNew();
        await Task.Delay(30);
        while (true)
        {
            if (!await BoolAsync("seeking")) return true;
            if (sw.ElapsedMilliseconds >= maxMs || desistir?.Invoke() == true) return false;
            await Task.Delay(20);
        }
    }

    public bool Exited { get { try { return _p.HasExited; } catch { return true; } } }

    public void Dispose()
    {
        try { _ = Cmd("quit"); } catch { }
        try { if (!_p.WaitForExit(1500)) _p.Kill(); } catch { }
        try { _pipe.Dispose(); } catch { }
    }
}
