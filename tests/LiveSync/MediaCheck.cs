using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Streemu.Media;
using Streemu.Net;

static class MediaCheck
{
    // Só fontes artificiais. SDL dummy não cria janela ou sessão de áudio; nenhum jogo/dispositivo do usuário é capturado.
    public static async Task<int> Run(string[] args)
    {
        Environment.SetEnvironmentVariable("SDL_VIDEODRIVER", "dummy");
        Environment.SetEnvironmentVariable("SDL_AUDIODRIVER", "dummy");
        string encoder = args.Contains("--x264") ? "libx264" : "h264_nvenc";
        int skew = args.Contains("--ahead") ? 1000 : -1000;
        bool healthy = args.Contains("--healthy");
        string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("_local", "sincronia", encoder + (skew > 0 ? "-adiantado" : "-atrasado")
            + (args.Contains("--legacy-retiming") ? "-controle-antigo" : "") + (healthy ? "-saudavel" : "")));
        Directory.CreateDirectory(dir);
        string image = System.IO.Path.Combine(dir, "pausa.png");
        using (var bmp = new Bitmap(320, 180)) bmp.Save(image);
        const string pass = "sincronia-apenas-teste";
        var settings = new StreamSender.Settings(new CaptureSource { Label = "fonte artificial", IsWindow = false, Handle = IntPtr.Zero },
            new Preset("teste", 180, 60, 1500), AudioMode.OnlyApp, EncoderChoice.Auto, 80);
        var pipeline = new StreamSender.Pipeline(encoder, StreamSender.Path.Gpu);
        var cap = new CaptureSpec("sintetica", "sem tela", false, "testsrc2=size=320x180:rate=60", 0, 320, 180);
        using var sender = new StreamSender(); using var splitter = new TsSplitter(); using var pacer = new LiveAudioPacer();
        using var producing = new CancellationTokenSource();
        var producer = Task.Run(async () =>
        {
            long frame = 0; var pcm = new byte[AudioPump.BytesPerSecond / 100]; var clock = Stopwatch.StartNew();
            while (!producing.IsCancellationRequested)
            {
                while (frame < clock.Elapsed.TotalSeconds * AudioPump.SampleRate)
                {
                    Tone(pcm, frame); pacer.Push(pcm); frame += 480;
                }
                await Task.Delay(5);
            }
        });
        var processes = new List<Process>(); var pipes = new List<TsPipe>(); var viewers = new List<Viewer>();
        var statuses = new JsonArray(); Process? capture = null;
        int exits = 0, restarts = 0; var startedAt = new List<DateTime?>();
        void Viewer()
        {
            int port = LocalRelay.FreeUdpPort();
            var sendArgs = StreamSender.SrtArgs(settings, port, pass);
            if (args.Contains("--legacy-retiming"))
            {
                sendArgs.Remove("-copyts"); sendArgs.Remove("-start_at_zero"); sendArgs.InsertRange(0, new[] { "-dts_delta_threshold", "1" });
            }
            var output = Tools.Start(Tools.Ffmpeg, sendArgs, "teste-envio", redirectStdin: true);
            processes.Add(output); var pipe = new TsPipe(output.StandardInput.BaseStream, true); pipes.Add(pipe); splitter.Add(pipe);
            var viewer = new Viewer { UseHardware = false, ExtraArgs = new[] { "-volume", "0", "-stats" } };
            viewer.Exited += () => Interlocked.Increment(ref exits); viewer.Restarting += () => Interlocked.Increment(ref restarts);
            viewer.Start(port, pass, 80, "teste de sincronia sem janela"); viewers.Add(viewer); startedAt.Add(viewer.StartedAt);
        }
        void Source(bool pause = false, bool bad = false)
        {
            pacer.Attach(null); Tools.KillQuietly(capture);
            var a = pause ? StreamSender.PauseArgs(settings, pipeline, image, true) : sender.CaptureArgs(settings, cap, pipeline, true);
            sender.ContinueMediaClock(a);
            capture = Tools.Start(Tools.Ffmpeg, a, pause ? "teste-pausa" : "teste-captura", true, binaryStdout: true);
            // Depois do mux: um asetpts antes dele é intercalado pelo próprio ffmpeg e não produz desalinhamento dos frontiers TS.
            splitter.Feed(bad ? new SkewStream(capture.StandardOutput.BaseStream, skew) : capture.StandardOutput.BaseStream);
            AudioPump.BeginPipe(capture.StandardInput.BaseStream);   // o cabeçalho WAV que o AudioPump.Attach escreve no app
            pacer.Attach(capture.StandardInput.BaseStream);
        }
        try
        {
            Viewer(); Source(); await Task.Delay(3000); Viewer(); await Task.Delay(2500);
            Source(pause: true); await Task.Delay(1600); Source(); await Task.Delay(2500);
            Source(bad: !healthy); var guard = new LiveSyncGuard(); var epoch = Stopwatch.StartNew(); bool corrected = false;
            for (int i = 0; i < (healthy ? 10 : 25); i++)
            {
                await Task.Delay(1000); var ts = splitter.SyncSnapshot();
                bool request = guard.Observe(ts["audio_menos_video_ms"]?.GetValue<int>(), epoch.Elapsed.TotalSeconds >= 6, Stopwatch.GetTimestamp());
                statuses.Add(new JsonObject { ["s"] = i, ["origem"] = ts, ["audio"] = pacer.Snapshot(),
                    ["filas"] = new JsonArray(pipes.Select(p => (JsonNode)p.SyncSnapshot()).ToArray()),
                    ["players"] = new JsonArray(viewers.Select(v => (JsonNode)v.Sync.Snapshot()).ToArray()), ["protecao"] = guard.Snapshot() });
                if (request) { Source(); epoch.Restart(); corrected = true; Console.WriteLine("      proteção pediu recuperação da origem"); }
                if (processes.Any(p => p.HasExited) || viewers.Any(v => !v.Running)) throw new Exception("Um envio ou player encerrou durante a recuperação.");
            }
            if (!healthy && (!corrected || guard.Snapshot()["pedidos_recuperacao"]!.GetValue<int>() != 1 || guard.Snapshot()["recuperacoes_verificadas"]!.GetValue<int>() != 1))
                throw new Exception("A proteção não recuperou/verificou o descompasso artificial.");
            if (healthy && guard.Snapshot()["pedidos_recuperacao"]!.GetValue<int>() != 0) throw new Exception("Recuperou sem necessidade.");
            if (exits != 0 || restarts != 0 || viewers.Where((v, i) => v.StartedAt != startedAt[i]).Any()) throw new Exception("Reabriu algum player.");
            foreach (var v in viewers)
            {
                int? delta = v.Sync.Snapshot()["audio_menos_video_ms"]?.GetValue<int>();
                if (delta == null || Math.Abs(delta.Value) > 150) throw new Exception("O player não terminou com relógios próximos: " + v.Sync.Snapshot());
            }
            Console.WriteLine($"OK    mídia {encoder}, {(healthy ? "saudável sem recuperação" : $"desvio {skew} ms recuperado")}, 2 envios SRT e 2 players preservados");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("FALHA mídia: " + e.Message); return 1; }
        finally
        {
            producing.Cancel(); await producer; pacer.Attach(null); Tools.KillQuietly(capture);
            foreach (var v in viewers) v.Stop(); foreach (var p in processes) Tools.KillQuietly(p); foreach (var p in pipes) p.Dispose();
            File.WriteAllText(System.IO.Path.Combine(dir, "medidas.json"), statuses.ToJsonString(new() { WriteIndented = true }));
            File.WriteAllLines(System.IO.Path.Combine(dir, "log.txt"), Streemu.Util.Log.Tail(1500));
        }
    }

    static void Tone(byte[] bytes, long first)
    {
        var samples = MemoryMarshal.Cast<byte, float>(bytes.AsSpan());
        for (int i = 0; i < samples.Length / 2; i++) samples[2 * i] = samples[2 * i + 1] = (float)(0.05 * Math.Sin(2 * Math.PI * 440 * (first + i) / 48000));
    }

    sealed class SkewStream(Stream source, int milliseconds) : Stream
    {
        readonly byte[] _packet = new byte[188];
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count < 188) throw new InvalidOperationException("O leitor do teste precisa pedir um pacote TS.");
            source.ReadExactly(_packet);
            if (TsAvMeter.TryPts(_packet, out int pid, out long pts) && pid == 0x101)
            {
                pts = (pts + milliseconds * 90L) & ((1L << 33) - 1);
                int p = 4 + (((_packet[3] >> 4) & 3) == 3 ? 1 + _packet[4] : 0);
                _packet[p + 9] = (byte)((_packet[p + 9] & 0xf1) + ((pts >> 30 & 7) << 1));
                _packet[p + 10] = (byte)(pts >> 22); _packet[p + 11] = (byte)(((pts >> 15 & 127) << 1) | 1);
                _packet[p + 12] = (byte)(pts >> 7); _packet[p + 13] = (byte)(((pts & 127) << 1) | 1);
            }
            _packet.CopyTo(buffer, offset); return 188;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long o, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long n) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
    }
}
