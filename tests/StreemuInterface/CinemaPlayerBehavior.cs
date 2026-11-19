using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Streemu;
using Streemu.Filme;
using Streemu.Net;
using Streemu.Ui;

static class CinemaPlayerBehavior
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")] static extern int GetGuiResources(IntPtr process, int flags);

    public static void Run(string? output)
    {
        if (output != null) Directory.CreateDirectory(output);
        using var main = new MainForm(true);
        using var surface = new Panel { Size = new Size(1200, 760), Font = main.Font };
        var cinema = (FilmeTela)Call(main, "AbrirFilme")!;
        cinema.Parent!.Controls.Remove(cinema);
        surface.Controls.Add(cinema); surface.CreateControl(); cinema.CreateControl();
        var stream = new StreamTela("Filme escolhido", "srt://127.0.0.1:1", 5400, "perfil do ensaio", () => null, "Ana");
        int closed = 0, commands = 0, fullscreen = 0;
        stream.Fechou += () => closed++;
        stream.Comando += (_, _) => commands++;
        stream.TelaCheiaPedida += () => fullscreen++;
        cinema.MostrarTransmissao(stream);
        Set(main, "_streamTela", stream); Set(main, "_streamSid", "escolhida");
        var video = Field<Panel>(stream, "_tela");
        var handle = video.Handle;
        var host = Field<Panel>(cinema, "_transmissaoHost");
        var nav = Field<FlowLayoutPanel>(cinema, "_playerNav");
        var view = Field<CinemaView>(cinema, "_cinema");
        Check(!stream.TopLevel && stream.FormBorderStyle == FormBorderStyle.None, "Transmissão continuou em janela separada");
        Check(cinema.Ocupado && stream.Visible && !view.Visible, "Player não ocupou a página do Cinema");
        Check(!Field<System.Windows.Forms.Timer>(stream, "_relogioTela").Enabled, "Apresentação sem mpv iniciou sondagem");
        Check(stream.Text.Contains("Filme escolhido"), "Título foi substituído pelo anfitrião");

        foreach (var size in new[] { new Size(1200, 760), new Size(736, 568), new Size(416, 400), new Size(3440, 1440) })
        {
            surface.Size = size; cinema.PerformLayout();
            // com a interface do mpv não há faixa acima do vídeo (mockups de 2026-11-15): o vídeo começa no topo da página
            Check(nav.Visible == !MpvUi.Ligada && (!nav.Visible || host.Top >= nav.Bottom) && host.Bottom <= cinema.Height, "Vídeo cobriu a navegação ou saiu da página");
            Check(stream.ClientSize == host.ClientSize && video.Right <= stream.ClientSize.Width, $"Player não acompanhou o espaço útil em {size}: player {stream.ClientSize}, área {host.ClientSize}, limite do Windows {SystemInformation.MaxWindowTrackSize}");
            if (!MpvUi.Ligada)
            {
                var flow = Field<FlowLayoutPanel>(stream, "_fallbackFlow");
                foreach (Control control in flow.Controls)
                    Check(control.Right <= flow.ClientSize.Width && control.Bottom <= flow.ClientSize.Height, "Controles de contingência ficaram cortados");
                Check(Field<Button>(cinema, "_sairTransmissao").Visible, "Contingência ficou sem saída da sessão");
                var bar = Field<Panel>(stream, "_barra");
                Check(bar.Bottom <= stream.ClientSize.Height, $"Barra saiu do player: {bar.Bounds} / {stream.ClientSize}");
                Check(!bar.Bounds.IntersectsWith(video.Bounds), "Barra cobriu o vídeo");
                foreach (Control control in bar.Controls)
                    Check(control.Bottom <= bar.ClientSize.Height, $"Barra cortou {control.GetType().Name}: {control.Bounds} / {bar.Bounds}");
            }
            if (output != null)
            {
                using var image = new Bitmap(surface.Width, surface.Height);
                surface.DrawToBitmap(image, surface.ClientRectangle);
                image.Save(Path.Combine(output, $"cinema-integrado-{size.Width}x{size.Height}.png"));
            }
            // com a interface do mpv a volta é o Cinema da barra lateral (AbrirFilme chama MostrarCatalogo); sem ela, o botão da faixa
            if (nav.Visible) Field<Button>(cinema, "_voltarCinema").PerformClick(); else cinema.MostrarCatalogo();
            Check(view.Visible && !host.Visible && closed == 0 && cinema.Ocupado, "Voltar encerrou a participação");
            Field<Button>(view, "_create").PerformClick();
            Check(closed == 0 && Field<StreamTela>(cinema, "_transmissao") == stream, "Criar substituiu a sessão em curso");
            Field<Button>(view, "_resume").PerformClick();
            Check(host.Visible && !view.Visible && video.Handle == handle, "Retorno recriou o HWND ou perdeu o player");
            surface.Visible = false; surface.Visible = true;
            Check(video.Handle == handle && closed == 0, "Ocultar a página encerrou/recriou a apresentação");
            cinema.EmTelaCheia = stream.EmTelaCheia = true;
            Check(!nav.Visible, "Navegação nativa ocupou a tela cheia");
            Call(stream, "AcaoUi", (object)new[] { "streemu", "cheia" });
            Check(fullscreen > 0 && !stream.TopLevel, "Tela cheia não foi encaminhada à janela principal");
            cinema.EmTelaCheia = stream.EmTelaCheia = false;
        }

        Call(stream, "AcaoUi", (object)new[] { "streemu", "tocar" });
        Check(commands == 0, "Comando liberado antes de receber permissão");
        using var selected = Link("escolhida", "Ana");
        using var other = Link("outra", "Bruno");
        Call(main, "FilmeStreamControle", other, new JsonObject { ["type"] = "filme_estado", ["seq"] = 99, ["pos"] = 80.0, ["todos"] = true });
        Check(stream.PosicaoFilme == 0, "Relógio recebeu estado de outra ligação");
        Call(main, "FilmeStreamControle", selected, new JsonObject { ["type"] = "filme_estado", ["seq"] = 1, ["pos"] = 25.0, ["todos"] = true });
        Check(stream.PosicaoFilme == 25, "Estado escolhido não chegou ao player");
        Call(stream, "AcaoUi", (object)new[] { "streemu", "pular", "5" });
        Check(commands == 1, "Permissão existente não liberou os comandos");
        Call(main, "FilmeStreamControle", other, new JsonObject { ["type"] = "filme_permissoes", ["todos"] = false });
        Check(Field<Button>(stream, "_tocar").Enabled, "Outra ligação revogou a permissão");
        Call(main, "FilmeStreamControle", selected, new JsonObject { ["type"] = "filme_permissoes", ["todos"] = false });
        Call(stream, "AcaoUi", (object)new[] { "streemu", "ir", "50" });
        Check(commands == 1 && !Field<Button>(stream, "_tocar").Enabled, "Revogação não bloqueou comandos");
        Call(main, "FilmeStreamControle", other, new JsonObject { ["type"] = "filme_stream_epoca", ["epoca"] = "não deve ser lido" });
        Check(stream.EpocasRecebidas == 0, "Época de outra ligação chegou ao player");
        var info = new TaskCompletionSource<JsonObject>();
        Set(main, "_streamInfo", info); Set(main, "_streamPedidoSid", "escolhida");
        Call(main, "FilmeStreamControle", other, new JsonObject { ["type"] = "filme_stream_info", ["titulo"] = "Outro filme" });
        Check(!info.Task.IsCompleted, "Resposta de outra ligação concluiu a entrada");
        Call(main, "FilmeStreamControle", selected, new JsonObject { ["type"] = "filme_stream_info", ["titulo"] = "Filme escolhido" });
        Check((string?)info.Task.Result["titulo"] == "Filme escolhido", "Resposta escolhida não concluiu a entrada");
        Set(main, "_streamInfo", null!); Set(main, "_streamPedidoSid", null!);
        Call(stream, "AcaoUi", (object)new[] { "streemu", "sair" });
        stream.Dispose();
        Check(closed == 1 && !cinema.Ocupado && view.Visible, "Saída não limpou a participação uma única vez");
        Set(main, "_streamTela", null!);
        using (var next = new StreamTela("Próximo", "srt://127.0.0.1:1", 10, "", () => null))
        {
            cinema.MostrarTransmissao(next); cinema.Fechar();
            Check(next.IsDisposed && !cinema.Ocupado, "Fechar o app deixou a apresentação órfã");
        }
        Console.WriteLine($"OK: Cinema integrado, 4 tamanhos, navegação/retorno com HWND estável, tela cheia encaminhada, permissões e estado filtrados por ligação, saída/descarte únicos. DPI {surface.DeviceDpi}. Sem iniciar mpv, mídia, captura ou conexão externa.");
    }

    public static void Cost(string? output, int rounds = 3)
    {
        using var surface = new Panel { Size = new Size(1200, 760) };
        var lig = new FilmeLigacao("Rafa", () => null, () => null, _ => { }, _ => Task.FromResult<string?>(null), () => { }, _ => { }, () => null, () => 0, () => null, () => null);
        using var cinema = new FilmeTela(lig, true);
        surface.Controls.Add(cinema); surface.CreateControl();
        WeakReference<StreamTela>? retired = null;
        void Cycle(int i)
        {
            using var stream = new StreamTela("Ensaio", "srt://127.0.0.1:1", 90, "", () => null);
            retired = new(stream);
            cinema.MostrarTransmissao(stream);
            surface.Size = new Size(i % 2 == 0 ? 1200 : 416, 760);
            cinema.MostrarCatalogo(); cinema.RetomarPlayer();
            Check(!Field<System.Windows.Forms.Timer>(stream, "_relogioTela").Enabled, "Apresentação iniciou timer sem mídia");
        }
        for (int i = 0; i < 10; i++) Cycle(i);
        Pump(500);
        object Resources()
        {
            var snapshot = Snapshot();
            Check(retired?.TryGetTarget(out _) == false, "Apresentação descartada continuou retida após GC");
            Check(Field<Panel>(cinema, "_transmissaoHost").Controls.Count == 0, "Hospedeiro reteve apresentações descartadas");
            return snapshot;
        }
        var samples = new List<object> { Resources() };
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime; Pump(5000);
        double idleCpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        for (int round = 0; round < Math.Clamp(rounds, 1, 20); round++)
        {
            var watch = Stopwatch.StartNew(); cpu = process.TotalProcessorTime;
            for (int i = 0; i < 60; i++) Cycle(i);
            Pump(200);
            samples.Add(new { round, wallMs = watch.Elapsed.TotalMilliseconds, cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds, resources = Resources() });
        }
        string json = JsonSerializer.Serialize(new { dpi = surface.DeviceDpi, idleCpuMs, samples }, new JsonSerializerOptions { WriteIndented = true });
        if (output != null) File.WriteAllText(output, json);
        Console.WriteLine(json);
    }
    static object Snapshot()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var p = Process.GetCurrentProcess();
        return new { privateMb = p.PrivateMemorySize64 / 1048576.0, managedMb = GC.GetTotalMemory(false) / 1048576.0, gdi = GetGuiResources(p.Handle, 0), user = GetGuiResources(p.Handle, 1), handles = p.HandleCount };
    }
    static PeerLink Link(string sid, string nick) => (PeerLink)Activator.CreateInstance(typeof(PeerLink), Hidden, null, [sid, nick, nick, false, true], null)!;
    static void Pump(int ms) { var w = Stopwatch.StartNew(); while (w.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(5); } }
    static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Hidden)!.GetValue(o)!;
    static void Set(object o, string name, object value) => o.GetType().GetField(name, Hidden)!.SetValue(o, value);
    static object? Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, Hidden)!.Invoke(o, args);
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
