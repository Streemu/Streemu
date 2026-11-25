using Streemu.Util;

namespace Streemu.Media;

/// <summary>
/// Confere as regras do vigia da captura com os números de casos de campo, sem abrir janela, captura nem rede: --vigia-check.
/// Grava vigia-check.txt ao lado do .exe; código de saída 0 = todas as regras certas. Roda no scripts/testes-locais.ps1.
/// </summary>
static class VigiaCheck
{
    public static int Run()
    {
        var lines = new List<string>();
        int failures = 0;
        void Check(string name, bool got, bool want)
        {
            bool ok = got == want;
            if (!ok) failures++;
            lines.Add($"{(ok ? "OK   " : "FALHA")} {name}: {(got ? "sim" : "não")}" + (ok ? "" : $" (esperado {(want ? "sim" : "não")})"));
        }
        static Native.RECT R(int l, int t, int w, int h) => new() { Left = l, Top = t, Right = l + w, Bottom = t + h };
        bool Covers(Native.RECT client, Native.RECT monitor) => CaptureSource.CoveredFraction(client, monitor) >= CaptureSource.MinCoverage;
        var mon = R(0, 0, 2560, 1440);
        var second = R(2560, 0, 1920, 1080);

        // quanto do monitor a janela cobre
        Check("Spotify 1270x1030 em janela num monitor 2560x1440 cobre o monitor", Covers(R(400, 200, 1270, 1030), mon), false);
        Check("jogo em tela cheia 2560x1440 cobre o monitor", Covers(R(0, 0, 2560, 1440), mon), true);
        Check("jogo sem bordas com 1 pixel a menos (2560x1439) cobre o monitor", Covers(R(0, 0, 2560, 1439), mon), true);
        Check("jogo sem bordas com 1 pixel a mais (2560x1441) cobre o monitor", Covers(R(0, 0, 2560, 1441), mon), true);
        Check("janela maximizada com barra de título e barra de tarefas (2560x1361) cobre o monitor", Covers(R(0, 31, 2560, 1361), mon), false);
        Check("janela 1920x1080 no monitor 2560x1440 (do tamanho do OUTRO monitor) cobre o monitor", Covers(R(0, 0, 1920, 1080), mon), false);
        Check("jogo em tela cheia no segundo monitor cobre o segundo monitor", Covers(R(2560, 0, 1920, 1080), second), true);
        Check("janela 2560x1440 metade fora do monitor cobre o monitor", Covers(R(1280, 0, 2560, 1440), mon), false);

        // a troca do vigia (60 fps)
        Check("campo 2026-11-10: Spotify parado em janela (janela ~1/s, monitor ~54/s) troca para o monitor",
            StreamSender.ShouldSwitchMethod(60, 1, 54, altCapturesMonitor: true, windowCoversMonitor: false), false);
        Check("jogo em tela cheia com a captura da janela travada (janela ~5/s, monitor ~58/s) troca para o monitor",
            StreamSender.ShouldSwitchMethod(60, 5, 58, altCapturesMonitor: true, windowCoversMonitor: true), true);
        Check("captura do monitor travada (monitor ~2/s, janela ~55/s) troca para a janela",
            StreamSender.ShouldSwitchMethod(60, 2, 55, altCapturesMonitor: false, windowCoversMonitor: false), true);
        Check("jogo em tela cheia mandando pouco de verdade (janela ~20/s, monitor ~25/s) troca",
            StreamSender.ShouldSwitchMethod(60, 20, 25, altCapturesMonitor: true, windowCoversMonitor: true), false);
        Check("janela ~30/s e monitor ~40/s (o outro não pega 1,5x mais) troca",
            StreamSender.ShouldSwitchMethod(60, 30, 40, altCapturesMonitor: true, windowCoversMonitor: true), false);
        // monitor escolhido na aba Telas: as duas capturas são do monitor inteiro (mesma imagem), então sempre pode trocar
        Check("campo 2026-11-12: monitor pelo gfxcapture ~1/s com o RoR2 em 4K, ddagrab do mesmo monitor ~60/s troca",
            StreamSender.ShouldSwitchMethod(60, 1, 60, altCapturesMonitor: true, windowCoversMonitor: true), true);
        Check("monitor pelo ddagrab ~2/s (Hookshot), gfxcapture do mesmo monitor ~50/s troca",
            StreamSender.ShouldSwitchMethod(60, 2, 50, altCapturesMonitor: true, windowCoversMonitor: true), true);
        Check("monitor parado de verdade (área de trabalho: ~1/s pelos dois) troca",
            StreamSender.ShouldSwitchMethod(60, 1, 2, altCapturesMonitor: true, windowCoversMonitor: true), false);

        // quando passar pela sonda (primeira live: cada segundo aqui é slideshow para quem assiste)
        Check("quase parada (~1/s) há 2 s: passa pela sonda", StreamSender.ShouldProbe(2, 2), true);
        Check("quase parada há só 1 s: passa pela sonda", StreamSender.ShouldProbe(1, 1), false);
        Check("engasgada (~25 de 60, janela de jogo em tela cheia) há 6 s: passa pela sonda", StreamSender.ShouldProbe(0, 6), true);
        Check("engasgada há só 5 s: passa pela sonda", StreamSender.ShouldProbe(0, 5), false);

        // a live voltou sozinha durante a sonda? (mediana do que ela recebeu)
        Check("campo 2026-11-12: um segundo de 44/s entre vários de ~1/s (mediana 1): voltou sozinha", StreamSender.Recovered(60, 1), false);
        Check("mediana 30 de 60 durante a sonda: voltou sozinha", StreamSender.Recovered(60, 30), false);
        Check("mediana 52 de 60 durante a sonda: voltou sozinha", StreamSender.Recovered(60, 52), true);

        // a captura travou (uma nova do mesmo método pega bem mais)?
        Check("Hookshot: live ~3/s, captura nova da janela ~49/s: travou", StreamSender.IsStale(60, 3, 49), true);
        Check("live engasgada ~30/s, captura nova ~58/s: travou", StreamSender.IsStale(60, 30, 58), true);
        Check("live ~30/s, captura nova ~38/s (o jogo é que está nisso): travou", StreamSender.IsStale(60, 30, 38), false);
        Check("live ~1/s, captura nova ~2/s (load): travou", StreamSender.IsStale(60, 1, 2), false);

        // espera até a próxima sonda depois de "é o jogo"
        Check("campo 2026-11-12: quase parado em toda sonda, a espera não passa de 20 s",
            StreamSender.NextBackoff(TimeSpan.FromSeconds(20), 2, 60) <= TimeSpan.FromSeconds(20), true);
        Check("jogo estável a ~40 de 60 (GPU a 100%): a espera chega a 40 s",
            StreamSender.NextBackoff(TimeSpan.FromSeconds(20), 40, 60) == TimeSpan.FromSeconds(40), true);
        Check("jogo estável a ~40 de 60: a espera passa de 60 s",
            StreamSender.NextBackoff(TimeSpan.FromSeconds(60), 40, 60) > TimeSpan.FromSeconds(60), false);

        // encoder aos trancos com a captura em dia (áudio e vídeo da captura desalinhados): reabre a captura
        Check("campo 2026-11-12 13:24: captura 60/s, encoder \"0 0 0 132 12 82 148 47\": encoder aos trancos",
            StreamSender.EncoderStutters(new[] { (60, 0), (59, 0), (60, 0), (60, 132), (59, 12), (60, 82), (60, 148), (60, 47) }, 60), true);
        Check("captura e encoder a 60/s: encoder aos trancos",
            StreamSender.EncoderStutters(new[] { (60, 60), (59, 61), (60, 59), (60, 60), (61, 60), (60, 60), (60, 61), (59, 60) }, 60), false);
        Check("placa cheia: captura a 5–30/s e encoder junto (13:23:47–58): encoder aos trancos",
            StreamSender.EncoderStutters(new[] { (5, 10), (6, 9), (33, 9), (28, 49), (15, 28), (20, 33), (18, 33), (9, 14) }, 60), false);
        Check("um tranco só (0 e depois 120) com o resto em dia: encoder aos trancos",
            StreamSender.EncoderStutters(new[] { (60, 60), (60, 0), (60, 120), (60, 60), (60, 60), (60, 60), (60, 60), (60, 60) }, 60), false);

        // trava de foco: o monitor transmitido mostra o jogo?
        Check("jogo com o foco: mostra o jogo", StreamSender.ShowsGame(false, true, false, false), true);
        Check("campo 2026-11-12: foco no Streemu do OUTRO monitor, jogo cobrindo a TV: mostra o jogo", StreamSender.ShowsGame(false, false, true, true), true);
        Check("foco em outra janela no MESMO monitor do jogo (Alt+Tab): mostra o jogo", StreamSender.ShowsGame(false, false, false, true), false);
        Check("foco em outro monitor, mas o jogo em janela (não cobre o monitor): mostra o jogo", StreamSender.ShowsGame(false, false, true, false), false);
        Check("jogo minimizado (tela cheia exclusiva perdendo o foco): mostra o jogo", StreamSender.ShowsGame(true, false, true, true), false);

        // testa o AMF só com placa AMD
        Check("PC com Parsec + RTX 5070: testa o AMF", StreamSender.HasAmdGpu(new[] { "Parsec Virtual Display Adapter (driver 0.45)", "NVIDIA GeForce RTX 5070 (driver 621.14)" }), false);
        Check("PC com RX 6750 XT: testa o AMF", StreamSender.HasAmdGpu(new[] { "AMD Radeon RX 6750 XT (driver 32.0)" }), true);
        Check("sem lista de placas: testa o AMF", StreamSender.HasAmdGpu(Array.Empty<string>()), true);

        // que captura abre (sem STREEMU_CAPTURE)
        Check("monitor escolhido na aba Telas abre pelo ddagrab", Capture.WantsDda(null, isWindow: false, fullScreenNow: false), true);
        Check("jogo em tela cheia abre pelo ddagrab", Capture.WantsDda(null, isWindow: true, fullScreenNow: true), true);
        Check("app em janela comum abre pelo ddagrab", Capture.WantsDda(null, isWindow: true, fullScreenNow: false), false);
        Check("monitor com STREEMU_CAPTURE=wgc abre pelo ddagrab", Capture.WantsDda("WGC", isWindow: false, fullScreenNow: false), false);
        Check("app em janela com STREEMU_CAPTURE=dda abre pelo ddagrab", Capture.WantsDda("dda", isWindow: true, fullScreenNow: false), true);

        // a captura de uma janela deve ser pelo monitor?
        Check("app em janela, sem troca do vigia: captura pelo monitor", StreamSender.WantMonitor(null, fullScreenNow: false, coversMonitor: false), false);
        Check("jogo em tela cheia (PR #62): captura pelo monitor", StreamSender.WantMonitor(null, fullScreenNow: true, coversMonitor: true), true);
        Check("o vigia trocou para o monitor e o jogo segue em tela cheia: captura pelo monitor", StreamSender.WantMonitor(true, fullScreenNow: true, coversMonitor: true), true);
        Check("o vigia trocou para o monitor e o jogo segue sem bordas com 1 pixel a menos: captura pelo monitor", StreamSender.WantMonitor(true, fullScreenNow: false, coversMonitor: true), true);
        Check("o vigia trocou para o monitor e o jogo SAIU da tela cheia: captura pelo monitor", StreamSender.WantMonitor(true, fullScreenNow: false, coversMonitor: false), false);

        // reabrir direto, sem sonda (PC do grupo, Hookshot, 10/11: a captura da janela travou 3 vezes e reabrir sempre resolveu)
        // LIV-161 (2026-11-25): pausas do gfxcapture de 1–3 s voltam sozinhas; só 4 s ou mais parada reabre direto
        Check("captura já travou nesta live, caída há 4 s: reabre direto", StreamSender.CanFastReopen(true, 4, 60, 1e9), true);
        Check("captura já travou, caída há 2 s (volta sozinha): não reabre", StreamSender.CanFastReopen(true, 2, 60, 1e9), false);
        Check("captura já travou, caída há 3 s (volta sozinha): não reabre", StreamSender.CanFastReopen(true, 3, 60, 1e9), false);
        Check("captura nunca travou nesta live, caída há 4 s: reabre direto", StreamSender.CanFastReopen(false, 4, 60, 1e9), false);
        Check("captura já travou, caída há só 1 s: reabre direto", StreamSender.CanFastReopen(true, 1, 60, 1e9), false);
        Check("captura já travou, mas reabriu direto há 10 s (load longo?): reabre direto de novo", StreamSender.CanFastReopen(true, 4, 60, 10), false);
        Check("captura já travou e acabou de abrir (2 s): reabre direto", StreamSender.CanFastReopen(true, 4, 2, 1e9), false);
        // limite por janela (ReaberturaInutil): barra só a reabertura rápida; a sonda segue (o Watchdog só a bloqueia com Desistiu)
        var janela = new ReaberturaInutil();
        for (int i = 0; i < ReaberturaInutil.ReaberturasNaJanela; i++) janela.Reabriu(i * 37, 5, 60);
        double agoraJanela = (ReaberturaInutil.ReaberturasNaJanela - 1) * 37 + 30;
        Check("4 reaberturas em 10 min: janela cheia", janela.NoLimite(agoraJanela), true);
        Check("janela cheia: a reabertura rápida espera", StreamSender.CanFastReopen(true, 4, 60, 1e9, janela.NoLimite(agoraJanela)), false);
        Check("janela cheia não é desistência: a sonda continua liberada", janela.Desistiu, false);
        Check("janela vazia de novo (11 min depois): a reabertura rápida volta", StreamSender.CanFastReopen(true, 4, 60, 1e9, janela.NoLimite(agoraJanela + 700)), true);

        // memória do método por jogo
        var hoje = new DateTime(2026, 11, 10, 21, 0, 0);
        Check("Hookshot: o vigia trocou para a janela hoje, abre pela janela", StreamSender.RememberedWindowSince("janela|2026-11-10", hoje) != null, true);
        Check("trocou para a janela há 13 dias, abre pela janela", StreamSender.RememberedWindowSince("janela|2026-10-28", hoje) != null, true);
        Check("trocou para a janela há 20 dias (memória vencida), abre pela janela", StreamSender.RememberedWindowSince("janela|2026-10-21", hoje) != null, false);
        Check("o vigia depois trocou de volta para o monitor, abre pela janela", StreamSender.RememberedWindowSince("monitor", hoje) != null, false);
        Check("sem memória, abre pela janela", StreamSender.RememberedWindowSince(null, hoje) != null, false);
        Check("valor estragado, abre pela janela", StreamSender.RememberedWindowSince("janela|ontem", hoje) != null, false);

        lines.Add(failures == 0 ? "RESULTADO: todas as regras do vigia certas" : $"RESULTADO: {failures} regra(s) erradas");
        foreach (var l in lines) Log.Info("VIGIA-CHECK: " + l);
        try { File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "vigia-check.txt"), lines, new System.Text.UTF8Encoding(true)); } catch { }
        return failures == 0 ? 0 : 1;
    }
}
