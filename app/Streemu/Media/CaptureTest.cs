using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Streemu.Net;
using Streemu.Util;

namespace Streemu.Media;

/// <summary>
/// <c>Streemu.exe --capture-test [trecho do nome do jogo]</c>: mede a captura sem rede, sem player e sem abrir janela,
/// com o jogo aberto. Cada variante roda ~8 s e conta, pelo relógio, os quadros que chegam de verdade.
/// Grava capture-test.txt ao lado do .exe.
///
/// Por quê (teste de campo 0.1.8): a stream ficou em câmera lenta nos dois sentidos com rede perfeita;
/// precisamos separar captura (gfxcapture janela/monitor, ddagrab), encoder, pipe de áudio e SRT/LocalRelay.
/// </summary>
public static class CaptureTest
{
    sealed record Result(string Id, string Label, JsonObject Stats, int? GpuPct, string Notes);

    public static async Task<int> RunAsync(string? match)
    {
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("STREEMU_CAPTURE_SECONDS"), out var sec) && sec >= 3 ? sec : 8;
        var lines = new List<string>();
        void W(string s) { lines.Add(s); Log.Info("CAPTURE-TEST: " + s); }
        int code = 0;
        try
        {
            if (!Tools.Available(out var msg)) { W("FALHOU: " + msg); return Finish(1); }
            W($"Streemu {Program.Version}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}, {seconds} s por variante");
            W($"Windows: {SystemInfo.Windows}");
            W($"GPU: {string.Join(" | ", SystemInfo.Gpus())}");
            foreach (var o in Display.Outputs())
                W($"saída de vídeo: placa {o.Adapter} \"{o.AdapterName}\" saída {o.OutputIdx} ({o.DeviceName}) {o.Rect.Width}x{o.Rect.Height} em {o.Rect.Left},{o.Rect.Top}");

            var sources = Sources.List();
            var game = match != null
                ? sources.FirstOrDefault(x => x.IsWindow && x.Label.Contains(match, StringComparison.OrdinalIgnoreCase))
                : sources.FirstOrDefault(x => x.IsWindow && x.IsFullScreen);
            IntPtr monitor;
            if (game == null)
            {
                W(match != null ? $"AVISO: não achei janela com \"{match}\"." : "AVISO: nenhum jogo em tela cheia aberto.");
                W("Medindo só o monitor principal: o resultado NÃO diz nada sobre jogo. Abra o jogo e rode de novo.");
                monitor = sources.First(x => !x.IsWindow && x.IsPrimary).Handle;
            }
            else
            {
                if (game.IsMinimized) W(game.RestoreIfMinimized() ? "a janela do jogo estava minimizada: restaurada" : "AVISO: a janela do jogo está minimizada e não voltou");
                monitor = Display.MonitorOf(game.Handle);
                W($"jogo: {game.Label}");
                W($"modo do jogo: {Display.GameMode(game.Handle, game.IsFullScreen)}");
            }
            int? gpuIdle = Display.NvidiaUtilization();
            if (gpuIdle != null) W($"uso da GPU antes de medir: {gpuIdle}%");

            var caps = new List<CaptureSpec>();
            if (game != null) caps.Add(Capture.Window(game, 60));
            caps.Add(Capture.Monitor(monitor, 60, "monitor do jogo"));
            var dda = Capture.Dda(monitor, 60, "monitor do jogo");
            if (dda != null) caps.Add(dda); else W("ddagrab: o DXGI não achou a saída desse monitor (pulando a variante c).");

            var results = new List<Result>();
            char letter = 'a';
            var pipelines = new Dictionary<string, (StreamSender.Pipeline P, Preset Pr)>();
            // STREEMU_CAPTURE_ONLY=h: só a variante do reinício (para quem está mexendo no vigia)
            if (Environment.GetEnvironmentVariable("STREEMU_CAPTURE_ONLY") == "h") caps.Clear();
            foreach (var cap in caps)
            {
                string id = (letter++).ToString();
                W($"--- {id}) {cap}");
                results.Add(await RunVariant(id + "1", $"{cap.Name}, só captura (passthrough, quadros reais)", cap, null, null, false, false, seconds, W));
                var preset = PresetFor(cap.Width, cap.Height);
                StreamSender.Pipeline pipe;
                try { pipe = StreamSender.ChoosePipeline(EncoderChoice.Auto, preset, cap, (cap.Width, cap.Height), (cap.Width, cap.Height)); }
                catch (Exception e) { W($"{id}2) nenhum encoder aceitou: {e.Message}"); continue; }
                pipelines[cap.Name] = (pipe, preset);
                results.Add(await RunVariant(id + "2", $"{cap.Name} + {pipe} cfr {preset.Fps} ({preset.Kbps} kbps)", cap, pipe, preset, false, false, seconds, W));
            }

            // a melhor captura é a que mais entrega quadros novos já com o encoder (é o que vai para o amigo)
            var best = results.Where(r => r.Id.EndsWith('2')).OrderByDescending(r => (double)r.Stats["quadros_novos_s_mediana"]!).FirstOrDefault();
            if (best != null)
            {
                var cap = caps.First(c => best.Label.StartsWith(c.Name + " "));
                var (pipe, preset) = pipelines[cap.Name];
                W($"--- melhor até aqui: {best.Id} ({cap.Name}). Agora com áudio e SRT como na transmissão real.");
                results.Add(await RunVariant("d", $"{cap.Name} + encoder + áudio pelo pipe (mpegts)", cap, pipe, preset, true, false, seconds, W, game));
                results.Add(await RunVariant("e", $"{cap.Name} + encoder + áudio + SRT pelo LocalRelay até um ouvinte descartável", cap, pipe, preset, true, true, seconds, W, game));
                results.Add(await RunVariant("f", $"igual a e, com prioridade ALTA na GPU e na CPU para o ffmpeg", cap, pipe, preset, true, true, seconds, W, game, highPriority: true));
                // teste 0.1.8: um player do grupo estava sem VRAM; um player lento segura o ffmpeg de quem transmite?
                results.Add(await RunVariant("g", $"igual a e, com o ouvinte lendo devagar (player travando)", cap, pipe, preset, true, true, seconds, W, game, slowListener: true));
            }
            var src = game ?? sources.First(x => !x.IsWindow && x.IsPrimary);
            results.Add(await RunRestartVariant(src, seconds, W));

            W("");
            W("RESUMO (quadros novos por segundo: mín / mediana / média / máx; rajada = segundo com mais de 70 saindo)");
            foreach (var r in results)
            {
                var s = r.Stats;
                W($"{r.Id,-3} {s["quadros_novos_s_min"],3} / {s["quadros_novos_s_mediana"],3} / {s["quadros_novos_s_media"],5} / {s["quadros_novos_s_max"],3}" +
                  $"  rajadas {s["rajadas"]}, parado {s["segundos_parado"]} s, saindo máx {s["quadros_saindo_s_max"]}/s" +
                  (s["tempo_midia_por_relogio"] is JsonNode t ? $", mídia/relógio {t}" : "") +
                  (r.GpuPct != null ? $", GPU {r.GpuPct}%" : "") + $"  {r.Label}" + (r.Notes != "" ? $"  [{r.Notes}]" : ""));
            }
            foreach (var v in Verdict(results)) W("CONCLUSÃO: " + v);
            return Finish(0);
        }
        catch (Exception e) { W("FALHOU: " + e); return Finish(2); }

        int Finish(int c)
        {
            code = c;
            try { File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "capture-test.txt"), lines, new System.Text.UTF8Encoding(true)); } catch { }
            return c;
        }
    }

    static Preset PresetFor(int w, int h)
    {
        bool wide = w > h * 2.2;
        var p = Preset.All.FirstOrDefault(x => x.Fps == 60 && x.Height == h && x.Name.Contains("ultrawide") == wide)
                ?? Preset.All.FirstOrDefault(x => x.Fps == 60 && x.Height == h);
        return p ?? new Preset($"{h}p60", h, 60, 14000);
    }

    static async Task<Result> RunVariant(string id, string label, CaptureSpec cap, StreamSender.Pipeline? pipe, Preset? preset,
        bool audio, bool srt, int seconds, Action<string> W, CaptureSource? game = null, bool highPriority = false, bool slowListener = false)
    {
        var stats = new FrameStats(60);
        var notes = new List<string>();
        var errors = new List<string>();
        AudioPump? pump = null;
        LocalRelay? senderRelay = null, viewerRelay = null;
        Process? ff = null, listener = null;
        int? gpu = null;
        string pass = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        try
        {
            if (audio)
            {
                try
                {
                    pump = game != null
                        ? await AudioPump.CreateAsync(game.ProcessId, include: true, $"só o app {game.ProcessName}")
                        : await AudioPump.CreateAsync(Environment.ProcessId, include: false, "PC todo");
                }
                catch (Exception e) { notes.Add("sem áudio: " + e.Message); }
            }
            var a = new List<string> { "-hide_banner", "-loglevel", "warning", "-stats", "-stats_period", "1" };
            var outputs = new List<string>();
            string? vf = pipe != null ? StreamSender.VideoFilter(pipe, (cap.Width, cap.Height), (cap.Width, cap.Height)) : null;
            int audioInput = cap.ForCounting().Args(vf, a, outputs); // o diagnóstico conta quadros novos: sem o ddagrab repetir
            if (pump != null) a.AddRange(StreamSender.AudioInputArgs());
            a.AddRange(outputs);
            if (pump != null) a.AddRange(new[] { "-map", $"{audioInput}:a:0" });
            if (pipe == null) a.AddRange(new[] { "-fps_mode", "passthrough", "-c:v", "wrapped_avframe" });
            else a.AddRange(StreamSender.EncoderArgs(pipe, preset!));
            if (pump != null) a.AddRange(StreamSender.AudioEncoderArgs());
            int port = 0;
            if (srt)
            {
                senderRelay = new LocalRelay("teste-envio");
                viewerRelay = new LocalRelay("teste-player");
                port = LocalRelay.FreeUdpPort();
                senderRelay.FixedTarget = new IPEndPoint(IPAddress.Loopback, port);
                senderRelay.Outgoing = d => viewerRelay.Deliver(d);
                viewerRelay.Outgoing = d => senderRelay.Deliver(d);
                Tools.PassphraseHint = pass;
                a.AddRange(new[] { "-f", "mpegts", "-flush_packets", "1", "-muxdelay", "0", "-muxpreload", "0",
                    $"srt://127.0.0.1:{port}?mode=listener&latency=80000&passphrase={pass}&pbkeylen=16&transtype=live&pkt_size=1316" });
            }
            else if (pump != null) a.AddRange(new[] { "-f", "mpegts", "-flush_packets", "1", "-muxdelay", "0", "-muxpreload", "0", "-y", "NUL" });
            else a.AddRange(new[] { "-f", "null", "-" });

            ff = Tools.Start(Tools.Ffmpeg, a, "teste-captura", redirectStdin: pump != null, onStderr: l =>
            {
                if (stats.Feed(l) == null && !l.StartsWith("frame=") && !string.IsNullOrWhiteSpace(l)
                    && !l.Contains("non monotonically increasing dts") && !l.Contains("Guessed Channel Layout"))
                    lock (errors) if (errors.Count < 3) errors.Add(l.Trim());
            });
            if (pump != null) pump.Attach(ff.StandardInput.BaseStream);
            if (highPriority)
            {
                var (st, level) = Display.RaiseGpuPriority(ff);
                try { ff.PriorityClass = ProcessPriorityClass.High; } catch { }
                notes.Add(st == 0 ? $"prioridade da GPU: {level}" : $"prioridade da GPU recusada (status 0x{st:X8})");
            }
            if (srt)
            {
                await Task.Delay(500);
                var la = new List<string> { "-hide_banner", "-loglevel", "error" };
                if (slowListener) la.AddRange(new[] { "-readrate", "0.25" });
                listener = Tools.Start(Tools.Ffmpeg, la.Concat(new[] {
                    "-i", $"srt://127.0.0.1:{viewerRelay!.Port}?mode=caller&latency=80000&passphrase={pass}&pbkeylen=16&transtype=live&connect_timeout=10000",
                    "-c", "copy", "-f", "null", "-" }), "teste-ouvinte", redirectStdin: false);
            }
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds && !ff.HasExited)
            {
                await Task.Delay(250);
                if (gpu == null && sw.Elapsed.TotalSeconds > seconds / 2.0) gpu = Display.NvidiaUtilization();
            }
            if (ff.HasExited) notes.Add($"ffmpeg saiu sozinho (código {ff.ExitCode})");
        }
        catch (Exception e) { notes.Add("erro: " + e.Message); }
        finally
        {
            pump?.Attach(null);
            Tools.KillQuietly(ff);
            Tools.KillQuietly(listener);
            pump?.Dispose();
            senderRelay?.Dispose();
            viewerRelay?.Dispose();
            await Task.Delay(400);
        }
        var summary = stats.Summary();
        lock (errors) if (errors.Count > 0) notes.Add("ffmpeg disse: " + string.Join(" | ", errors));
        var r = new Result(id, label, summary, gpu, string.Join("; ", notes));
        W($"{id}) {label}: {stats.OneLine()}" + (gpu != null ? $", GPU {gpu}%" : "") + (r.Notes != "" ? $" [{r.Notes}]" : ""));
        return r;
    }

    /// <summary>
    /// h) A transmissão de verdade (<see cref="StreamSender"/>), fingindo que a captura travou depois de 8 s como no load
    /// do Cyberpunk (teste 0.1.9). Um ffmpeg faz o papel do player e conta quantos quadros recebe por segundo:
    /// o vigia tem que perceber, reabrir só a captura, e o "player" continuar conectado.
    /// </summary>
    static async Task<Result> RunRestartVariant(CaptureSource src, int seconds, Action<string> W)
    {
        string label = "transmissão real; captura finge travar aos 8 s; o vigia tem que reabrir sem derrubar quem assiste";
        var received = new FrameStats(60);
        var perSecond = new List<int>();
        string pass = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        using var senderRelay = new LocalRelay("teste-envio");
        using var viewerRelay = new LocalRelay("teste-player");
        senderRelay.Outgoing = d => viewerRelay.Deliver(d);
        viewerRelay.Outgoing = d => senderRelay.Deliver(d);
        var sender = new StreamSender() { TestStuckAfter = 8, TestKillSensorAfter = 12 };
        sender.AddOutput("teste", "teste", senderRelay, () => pass);
        // o que a telemetria grava por segundo: de onde veio o segundo (sensor/mudo/pausa) e o uso da placa
        var medidas = new Dictionary<string, int>();
        var gpuSeq = new List<string>();
        using var gpu = new Telemetria.UsoGpu(() => src.IsWindow ? src.ProcessId : null);
        sender.SecondMeasured += (_, _, medida) =>
        {
            lock (medidas)
            {
                medidas[medida] = medidas.GetValueOrDefault(medida) + 1;
                gpuSeq.Add(gpu.Agora is { } g ? $"{g.Total3d}/{g.App3d}/{g.Dwm3d}/{g.Encoder}/cpu{g.AppCpu}" : "-");
            }
        };
        Process? listener = null;
        bool survived = false;
        string notes = "";
        int decodeErrors = 0;
        string stats2 = "não testado";
        try
        {
            var cap = Capture.For(src, 60);
            var preset = PresetFor(cap.Width, cap.Height);
            await sender.StartAsync(new StreamSender.Settings(src, preset, src.IsWindow ? AudioMode.OnlyApp : AudioMode.None, EncoderChoice.Auto, 80));
            await Task.Delay(800);
            Tools.PassphraseHint = pass;
            // o "player" DECODIFICA (na placa, como o ffplay): se a emenda perder o quadro-chave, aparecem erros de decodificação
            listener = Tools.Start(Tools.Ffmpeg, new[] { "-hide_banner", "-loglevel", "warning", "-stats", "-stats_period", "1", "-hwaccel", "auto",
                "-i", $"srt://127.0.0.1:{viewerRelay.Port}?mode=caller&latency=80000&passphrase={pass}&pbkeylen=16&transtype=live&connect_timeout=10000",
                "-map", "0:v:0", "-fps_mode", "passthrough", "-f", "null", "-" }, "teste-ouvinte", redirectStdin: false,
                onStderr: l =>
                {
                    if (received.Feed(l) is { } r) lock (perSecond) perSecond.Add((int)Math.Round(r.Out));
                    else if (!l.StartsWith("frame=") && (l.Contains("rror") || l.Contains("non-existing") || l.Contains("no frame") || l.Contains("concealing")))
                        Interlocked.Increment(ref decodeErrors);
                });
            var sw = Stopwatch.StartNew();
            int total = Math.Max(30, seconds * 3);
            bool forced = false;
            while (sw.Elapsed.TotalSeconds < total && !listener.HasExited)
            {
                await Task.Delay(250);
                // sem jogo a tela fica parada, a captura de teste também pega pouco e o vigia (certo) não reabre;
                // aí forçamos um reinício para provar que reabrir não derruba quem assiste
                if (!forced && sw.Elapsed.TotalSeconds > 18 && sender.SessionSummary()?["reinicios_da_captura"]?.GetValue<int>() == 0)
                {
                    forced = true;
                    sender.RequestRestart("teste: reinício forçado");
                }
            }
            survived = !listener.HasExited;
            // o amigo fecha o player e abre de novo: o envio tem que se refazer e o segundo "player" receber imagem
            // (o app do amigo avisa "watching: false", que vira ViewerLeft)
            Tools.KillQuietly(listener);
            sender.ViewerLeft("teste");
            await Task.Delay(1500);
            var second = new FrameStats(60);
            using (var again = Tools.Start(Tools.Ffmpeg, new[] { "-hide_banner", "-loglevel", "warning", "-stats", "-stats_period", "1",
                       "-i", $"srt://127.0.0.1:{viewerRelay.Port}?mode=caller&latency=80000&passphrase={pass}&pbkeylen=16&transtype=live&connect_timeout=25000",
                       "-map", "0:v:0", "-c", "copy", "-f", "null", "-" }, "teste-ouvinte", redirectStdin: false, onStderr: l => second.Feed(l)))
            {
                // o envio só percebe que o player sumiu pelo tempo-limite do SRT (~5 s), igual era antes
                var sw2 = Stopwatch.StartNew();
                double firstAt = -1;
                while (sw2.Elapsed.TotalSeconds < 25 && second.Intervals < 3)
                {
                    await Task.Delay(250);
                    if (firstAt < 0 && second.Intervals > 0) firstAt = sw2.Elapsed.TotalSeconds;
                }
                stats2 = second.Intervals >= 3 ? $"sim, imagem depois de {firstAt:0} s, mediana {second.NewFrames().Median:0} quadros/s" : "NÃO";
                Tools.KillQuietly(again);
            }
            var summary = sender.SessionSummary();
            notes = $"reinícios da captura: {summary?["reinicios_da_captura"]}; " + string.Join(" | ", (summary?["diagnosticos"]?.AsArray() ?? new JsonArray()).Select(x => x?.ToString()));
        }
        catch (Exception e) { notes = "erro: " + e.Message; }
        finally
        {
            sender.Stop();
            Tools.KillQuietly(listener);
            await Task.Delay(400);
        }
        var stats = received.Summary();
        stats["ouvinte_continuou_conectado"] = survived;
        stats["erros_de_decodificacao"] = decodeErrors;
        notes += $"; erros de decodificação no \"player\": {decodeErrors}; player fechado e aberto de novo recebeu imagem: {stats2}";
        lock (medidas)
        {
            notes += "; segundos gravados: " + string.Join(", ", medidas.Select(x => $"{x.Key} {x.Value}"));
            notes += "; placa (3D total/3D do streemu/3D do dwm/encoder %): " + string.Join(" ", gpuSeq);
            stats["segundos_mudos"] = medidas.GetValueOrDefault("mudo");
            stats["placa_lida"] = gpuSeq.Any(x => x != "-");
        }
        stats["reconectou"] = stats2.StartsWith("sim");
        string seq;
        lock (perSecond) seq = string.Join(" ", perSecond);
        W($"h) {label}: quadros recebidos por segundo pelo \"player\": {seq}; continuou conectado: {(survived ? "sim" : "NÃO")} [{notes}]");
        return new Result("h", label, stats, null, notes);
    }

    /// <summary>Lê os números e diz qual parte segura os quadros.</summary>
    static IEnumerable<string> Verdict(List<Result> rs)
    {
        double Med(string id) => rs.FirstOrDefault(r => r.Id == id) is { } r ? (double)r.Stats["quadros_novos_s_mediana"]! : -1;
        int Bursts(string id) => rs.FirstOrDefault(r => r.Id == id) is { } r ? (int)r.Stats["rajadas"]! : 0;
        if (rs.FirstOrDefault(r => r.Id == "h") is { } h)
        {
            bool alive = h.Stats["ouvinte_continuou_conectado"]?.GetValue<bool>() == true;
            bool restarted = h.Notes.Contains("reinícios da captura: ") && !h.Notes.Contains("reinícios da captura: 0");
            yield return !restarted ? "a captura não foi reaberta (veja o log)."
                : alive ? $"reabrir a captura não derruba quem assiste (ficou parado {h.Stats["segundos_parado"]} s, mediana {h.Stats["quadros_novos_s_mediana"]} quadros/s, {h.Stats["erros_de_decodificacao"]} erros de decodificação)."
                : "reabrir a captura DERRUBOU quem assiste.";
            yield return h.Stats["reconectou"]?.GetValue<bool>() == true ? "fechar e abrir o player de novo funciona." : "fechar e abrir o player de novo NÃO funcionou.";
        }
        var caps = rs.Where(r => r.Id.EndsWith('1')).ToList();
        if (caps.Count == 0) yield break;
        foreach (var c in caps)
        {
            double raw = Med(c.Id), enc = Med(c.Id[..1] + "2");
            string name = c.Label.Split(',')[0];
            if (raw < 45) yield return $"{name}: a CAPTURA sozinha já entrega pouco ({raw:0}/s).";
            else if (enc >= 0 && enc < raw * 0.8) yield return $"{name}: captura ok ({raw:0}/s), mas com o encoder cai para {enc:0}/s (encoder ou GPU no limite).";
            else yield return $"{name}: ok ({raw:0}/s só captura, {enc:0}/s com encoder).";
        }
        double best = rs.Where(r => r.Id.EndsWith('2')).Select(r => (double)r.Stats["quadros_novos_s_mediana"]!).DefaultIfEmpty(-1).Max();
        double d = Med("d"), e = Med("e"), f = Med("f"), g = Med("g");
        if (g >= 0 && e > 0)
            yield return g < e * 0.8 || Bursts("g") > Bursts("e") + 1
                ? $"um PLAYER LENTO do outro lado segura o ffmpeg de quem transmite ({e:0} → {g:0}/s, rajadas {Bursts("e")} → {Bursts("g")})."
                : $"player lento não segura quem transmite ({g:0}/s).";
        if (d >= 0 && best > 0)
            yield return d < best * 0.8 ? $"o ÁUDIO pelo pipe derruba de {best:0} para {d:0}/s." : $"áudio não atrapalha ({d:0}/s).";
        if (e >= 0 && d > 0)
            yield return e < d * 0.8 || Bursts("e") > Bursts("d") + 1 ? $"o SRT/LocalRelay atrapalha ({d:0} → {e:0}/s, rajadas {Bursts("d")} → {Bursts("e")})." : $"SRT/LocalRelay não atrapalha ({e:0}/s).";
        if (f >= 0 && e >= 0)
            yield return f > e * 1.2 ? $"prioridade alta na GPU AJUDA ({e:0} → {f:0}/s)." : $"prioridade da GPU não muda nada ({e:0} → {f:0}/s).";
    }
}
