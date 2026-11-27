using Streemu.Util;

namespace Streemu;

static class Program
{
    public const string Version = "0.6.0";

    [STAThread]
    static void Main(string[] args)
    {
        Log.Init();
        // o argumento pode ser um link de convite (streemu://grupo/<código>), que é a chave do grupo: só a máscara vai para o log (spec 018, D25)
        Log.Info($"Streemu {Version} iniciando. Args: {Telemetria.Higiene.SemCaminhos(Telemetria.Higiene.SemConvite(string.Join(' ', args)))}");
        Log.Info($"Build: {Util.Updater.CurrentLabel}, canal {BuildInfo.Canal}, commit {(BuildInfo.Commit.Length >= 7 ? BuildInfo.Commit[..7] : "?")}.");
        Application.ThreadException += (_, e) => Log.Info("ERRO (UI): " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Info("ERRO (fatal): " + e.ExceptionObject);
        // Validação completa do relay do grupo sem abrir a tela (usa o servidor padrão ou o grupo.json; precisa de conta entrada): --relay-check
        if (args.Any(a => a.Equals("--relay-check", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = RelayCheck().GetAwaiter().GetResult();
            return;
        }
        // Mede a captura com o jogo aberto, sem rede nem player: --capture-test [trecho do nome da janela]
        int ci = Array.FindIndex(args, a => a.Equals("--capture-test", StringComparison.OrdinalIgnoreCase));
        if (ci >= 0)
        {
            string? match = args.Length > ci + 1 && !args[ci + 1].StartsWith("--") ? args[ci + 1] : null;
            // sem isso o Windows devolve tamanhos com a escala aplicada (2560x1440 a 125% virava 2048x1152)
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            // fora da thread STA: a captura de áudio por processo (WASAPI) completa a ativação em outra thread
            Environment.ExitCode = Task.Run(() => Media.CaptureTest.RunAsync(match)).GetAwaiter().GetResult();
            return;
        }
        // Confere as regras do vigia da captura (janela ↔ monitor) com números de campo, sem janela nem captura: --vigia-check
        if (args.Any(a => a.Equals("--vigia-check", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = Media.VigiaCheck.Run();
            return;
        }
        // Confere que MAC, GUID e SSID não saem na telemetria (netsh em UTF-8 e em 850, regra 9): --higiene-check
        if (args.Any(a => a.Equals("--higiene-check", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = Telemetria.HigieneCheck.Run();
            return;
        }
        // Grava o texto "Sobre o diagnóstico" num arquivo, para a página de privacidade do instalador (spec 018, D7): --exportar-privacidade <arquivo>
        int pi = Array.FindIndex(args, a => a.Equals("--exportar-privacidade", StringComparison.OrdinalIgnoreCase));
        if (pi >= 0)
        {
            Environment.ExitCode = Telemetria.TextoDiagnostico.Exportar(pi + 1 < args.Length ? args[pi + 1] : null);
            return;
        }
        // Confere se o Discord aceita o Streemu (handshake do Rich Presence), sem mostrar nada no perfil: --discord-check
        if (args.Any(a => a.Equals("--discord-check", StringComparison.OrdinalIgnoreCase)))
        {
            using var discord = new Discord.DiscordPresence(Discord.DiscordPresence.DefaultAppId);
            var until = DateTime.Now.AddSeconds(6);
            while (!discord.Connected && DateTime.Now < until) Thread.Sleep(100);
            string result = discord.Connected
                ? $"DISCORD OK: conectado como {discord.DiscordUser ?? "?"} (app {Discord.DiscordPresence.DefaultAppId})."
                : $"DISCORD FALHOU: não achei o Discord aberto ou ele recusou o {Util.UiBrand.Name} (veja o log).";
            // --presenca: mostra no perfil, por ~8 s, uma presença de teste com o "Participar" (grupo público) e apaga
            if (discord.Connected && args.Any(a => a.Equals("--presenca", StringComparison.OrdinalIgnoreCase)))
            {
                discord.Set(new Discord.PresenceInfo($"Teste do {Util.UiBrand.Name}", "conferindo o botão Participar", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    null, JoinSecret: "teste" + Environment.ProcessId, PartySize: 1));
                Thread.Sleep(8000);
                result += discord.PublicPartyRefused
                    ? " PRESENÇA: o Discord recusou o grupo público; ficou o \"Pedir para participar\"."
                    : discord.LastAccepted != null ? " PRESENÇA: aceita com grupo público. Resposta: " + discord.LastAccepted : " PRESENÇA: sem resposta do Discord.";
                discord.Set(null);
                Thread.Sleep(1000);
            }
            Log.Info(result);
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "discord-check.txt"), result + Environment.NewLine, new System.Text.UTF8Encoding(true)); } catch { }
            Environment.ExitCode = discord.Connected ? 0 : 1;
            return;
        }
        // Teste do cliente TURN sem abrir a tela: --turn-test host:porta usuario senha
        int ti = Array.FindIndex(args, a => a.Equals("--turn-test", StringComparison.OrdinalIgnoreCase));
        if (ti >= 0 && args.Length >= ti + 4)
        {
            Environment.ExitCode = TurnSelfTest(args[ti + 1], args[ti + 2], args[ti + 3]).GetAwaiter().GetResult();
            return;
        }
        // O instalador roda isto no fim para a pasta instalada já ficar na última versão da main (spec 004, seção 7): --atualizar
        if (args.Any(a => a.Equals("--atualizar", StringComparison.OrdinalIgnoreCase)))
        {
            // --so-ffmpeg: o exe antigo acabou de trocar o exe por este e pede para ele trocar o ffmpeg que ELE quer (spec 004, seção 8)
            bool onlyFfmpeg = args.Any(a => a.Equals("--so-ffmpeg", StringComparison.OrdinalIgnoreCase));
            Environment.ExitCode = Util.Updater.RunHeadlessAsync(onlyFfmpeg).GetAwaiter().GetResult();
            return;
        }
        ApplicationConfiguration.Initialize();
        bool localMode = args.Any(a => a.Equals("--local", StringComparison.OrdinalIgnoreCase));
        // Um Streemu por usuário: clicar em Assistir no Discord com o app aberto abre um segundo .exe com o link
        // streemu://; ele entrega o link ao primeiro e fecha. Testes (--local, STREEMU_TEST_DIR) abrem várias instâncias.
        // aberto pela atualização: espera a versão antiga fechar (senão a instância única entregaria o pedido a ela)
        int ai = Array.FindIndex(args, a => a.Equals("--pid", StringComparison.OrdinalIgnoreCase));
        if (args.Contains("--atualizado") && ai >= 0 && args.Length > ai + 1 && int.TryParse(args[ai + 1], out int oldPid))
        {
            MainForm.JustUpdated = true;
            try { using var old = System.Diagnostics.Process.GetProcessById(oldPid); if (!old.WaitForExit(20000)) Log.Info("A versão anterior ainda não fechou em 20 s; abrindo mesmo assim."); }
            catch (ArgumentException) { } // já fechou
        }
        if (!localMode && Environment.GetEnvironmentVariable("STREEMU_TEST_DIR") == null)
        {
            var instance = SingleInstance.TryAcquire();
            if (instance == null)
            {
                switch (SingleInstance.Forward(args))
                {
                    case SingleInstance.ForwardResult.Delivered:
                        Log.Info("Já tem este Streemu aberto: entreguei o pedido a ele.");
                        return;
                    case SingleInstance.ForwardResult.OldInstanceClosing:
                        Log.Info("Havia um Streemu de OUTRA pasta aberto (outra versão); ele está fechando e esta assume.");
                        instance = SingleInstance.WaitAcquire(TimeSpan.FromSeconds(15));
                        if (instance == null) Log.Info("O Streemu antigo não fechou em 15 s; abrindo mesmo assim.");
                        break;
                    case SingleInstance.ForwardResult.OldVersionNoReply:
                        Log.Info("Há um Streemu de versão anterior aberto; pedi para fechar à mão.");
                        MessageBox.Show($"Tem um {Util.UiBrand.Name} de versão anterior aberto (olhe perto do relógio, na bandeja).\n\n" +
                                        "Clique com o botão direito no ícone dele → Sair, e abra este de novo.",
                            Util.UiBrand.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    default:
                        Log.Info("Outro Streemu parece aberto, mas não respondeu; abrindo mesmo assim.");
                        break;
                }
            }
            MainForm.Instance = instance;
        }
        MainForm.LaunchArgs = args;
        Application.Run(new MainForm(localMode));
    }

    /// <summary>
    /// Valida o relay do grupo passo a passo e grava o resultado em relay-check.txt ao lado do .exe:
    /// grupo.json → Worker no ar → credencial → reserva na Cloudflare → pacote vai e volta.
    /// Códigos de saída: 0 ok, 1 sem configuração, 2 Worker, 3 credencial/reserva, 4 repasse.
    /// </summary>
    static async Task<int> RelayCheck()
    {
        var lines = new List<string>();
        void Step(string s) { lines.Add(s); Log.Info("RELAY-CHECK: " + s); }
        int code;
        try
        {
            var cfg = Net.RelayConfig.Load(new AppState());
            if (cfg == null) { Step("FALHOU: sem servidor (modo de teste sem STREEMU_ROOM_URL)."); return Finish(1); }
            Step($"configuração: {cfg.Source}");
            Step($"servidor do grupo: {cfg.Url}");
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
            {
                try
                {
                    var root = await http.GetStringAsync(cfg.Url.TrimEnd('/') + "/");
                    Step($"Worker no ar: \"{root.Trim()}\"");
                }
                catch (Exception e) { Step($"FALHOU: Worker não respondeu ({e.GetBaseException().Message}). Se acabou de publicar, espere alguns minutos o certificado do workers.dev."); return Finish(2); }
            }
            Net.RelayConfig.Credentials creds;
            try { creds = await Net.RelayConfig.FetchAsync(cfg); }
            catch (Exception e) { Step("FALHOU ao pegar credencial: " + e.Message); return Finish(3); }
            Step($"credencial temporária recebida (servidor TURN {creds.Server})");
            // porta aleatória de propósito: não usa a porta salva do app, então não prende a reserva dele (TURN 437)
            using var tunnel = new Net.Tunnel(false);
            var pub = await tunnel.DiscoverAsync();
            Step($"meu endereço público: {Net.Tunnel.Mask(pub)} (porta local {tunnel.LocalPort}, aleatória; o app usa outra)");
            System.Net.IPEndPoint relayed;
            try { relayed = await tunnel.EnableRelayAsync(creds.Server, creds.Username, creds.Password).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception e) { Step("FALHOU ao reservar o relay: " + e.Message); return Finish(3); }
            Step($"relay reservado: {Net.Tunnel.Mask(relayed)}");
            bool ok = await tunnel.SelfTestRelayAsync(TimeSpan.FromSeconds(5));
            Step(ok ? "pacote de teste foi e voltou pela Cloudflare: RELAY OK" : "FALHOU: o pacote de teste não voltou pelo relay");
            if (tunnel.Turn?.Release() == true)
            {
                Step("reserva liberada (Refresh com LIFETIME 0)");
                // prova que liberar resolve o 437: reservar de novo na MESMA porta logo em seguida tem que funcionar
                try
                {
                    var again = await Net.RelayConfig.FetchAsync(cfg);
                    await tunnel.EnableRelayAsync(again.Server, again.Username, again.Password).WaitAsync(TimeSpan.FromSeconds(10));
                    Step("reservou de novo na mesma porta logo depois de liberar (sem erro 437)");
                    tunnel.Turn?.Release();
                }
                catch (Exception e) { Step("AVISO: depois de liberar, reservar de novo falhou: " + e.Message); }
            }
            else Step("AVISO: o relay não confirmou a liberação da reserva (ela vence sozinha em até 10 min)");
            return Finish(ok ? 0 : 4);
        }
        catch (Exception e) { Step("FALHOU: " + e); return Finish(3); }

        int Finish(int c)
        {
            code = c;
            lines.Add(c == 0 ? "RESULTADO: tudo certo" : $"RESULTADO: falhou (código {c})");
            try { File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "relay-check.txt"), lines, new System.Text.UTF8Encoding(true)); } catch { }
            return c;
        }
    }

    /// <summary>Reserva um relay, autoriza o próprio IP público, manda um pacote para o relay e confere se ele volta pelo túnel.</summary>
    static async Task<int> TurnSelfTest(string hostPort, string user, string pass)
    {
        try
        {
            var parts = hostPort.Split(':');
            var ip = (await System.Net.Dns.GetHostAddressesAsync(parts[0])).First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            var server = new System.Net.IPEndPoint(ip, int.Parse(parts[1]));
            using var tunnel = new Net.Tunnel(false);
            var pub = await tunnel.DiscoverAsync();
            var relayed = await tunnel.EnableRelayAsync(server, user, pass);
            Log.Info($"TURN-TESTE: relay reservado em {relayed} (meu público {pub}).");
            // segundo socket faz o papel do amigo: manda para o relay; o servidor só repassa se o IP dele tiver permissão
            using var friend = new System.Net.Sockets.UdpClient(0);
            tunnel.Turn!.Permit(pub.Address);
            await Task.Delay(800);
            long before = (long)tunnel.Counters["pacotes_com_codigo_errado"]!.GetValue<long>();
            var probe = new byte[] { (byte)'L', (byte)'X', 1, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
            for (int i = 0; i < 5; i++) { await friend.SendAsync(probe, probe.Length, relayed); await Task.Delay(200); }
            await Task.Delay(800);
            long viaRelay = (long)tunnel.Counters["pacotes_pelo_meu_relay"]!.GetValue<long>();
            Log.Info($"TURN-TESTE: {viaRelay} pacote(s) chegaram pelo relay.");
            return viaRelay > 0 ? 0 : 2;
        }
        catch (Exception e) { Log.Info("TURN-TESTE falhou: " + e); return 1; }
    }
}
