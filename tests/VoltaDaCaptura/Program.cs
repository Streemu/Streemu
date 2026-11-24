using Streemu.Media;

// Regra da volta da captura do monitor depois de o ddagrab cair (Media/VoltaDaCaptura.cs), com os números do campo de 2026-11-23 (LIV-141):
// 887a0026 (ACCESS_LOST), o DXGI sem o monitor logo depois e o gfxcapture com fonte 0x0.
static class Program
{
    static int Main(string[] args)
    {
        var casos = new (string Nome, Action Corpo)[]
        {
            ("espera-o-monitor-depois-do-ddagrab-cair", EsperaDepoisDoDdagrab),
            ("para-de-esperar-quando-o-monitor-volta", ParaQuandoVolta),
            ("desiste-depois-de-1500-ms", DesisteNoLimite),
            ("passos-cabem-na-espera", PassosCabem),
            ("nao-espera-se-a-captura-era-gfxcapture", NaoEsperaGfxcapture),
            ("nao-espera-se-o-vigia-trocou-para-gfxcapture", NaoEsperaVigia),
            ("fonte-0x0-nao-testa-encoder", ZeroNaoTesta),
            ("fonte-so-sem-altura-nao-testa-encoder", MeioZeroNaoTesta),
            ("fonte-normal-e-640x480-testam-encoder", NormalTesta),
            ("acha-o-monitor-pelo-identificador", AchaPeloHandle),
            ("acha-pelo-nome-quando-o-identificador-muda", AchaPeloNome),
            ("nao-acha-sem-nome-ou-com-o-monitor-fora", NaoAcha),
            ("mantem-a-anterior-com-monitor-0x0", MantemCom0x0),
            ("mantem-a-anterior-com-janela-sumida", MantemComJanelaSumida),
            ("testa-encoder-com-fonte-nova-de-verdade", TestaComFonteNova),
            ("nada-mudou-nada-a-manter", NadaMudou),
            ("queda-com-tela-sem-voltar-nao-conta-ate-10-s", QuedaNaoContaAte10s),
            ("queda-normal-continua-contando", QuedaNormal),
            ("parar-pausar-ou-trocar-qualidade-interrompe-a-espera", Interrompe),
            ("acesso-perdido-3-quedas-em-120-s-troca", AcessoTresEm120),
            ("acesso-perdido-1-e-2-quedas-nao-trocam", AcessoUmeDoisNaoTrocam),
            ("acesso-perdido-espalhadas-alem-de-120-s-nao-trocam", AcessoEspalhadas),
            ("acesso-perdido-janela-deslizante", AcessoJanelaDeslizante),
            ("acesso-perdido-queda-sem-887a0026-nao-conta", AcessoSemCodigo),
            ("acesso-perdido-janela-e-gfxcapture-nao-contam", AcessoSoMonitorNoDdagrab),
            ("acesso-perdido-live-nova-reinicia", AcessoLiveNova),
            ("acesso-perdido-3-quedas-a-proxima-abertura-e-gfxcapture-monitor", ProximaAberturaEGfxcapture),
            ("acesso-perdido-escolha-da-captura", EscolhaDaCaptura),
            ("acesso-perdido-troca-espera-o-monitor-voltar", TrocaEsperaOMonitor),
            ("acesso-perdido-keepold-deixa-a-troca-pendente", TrocaPendente),
            ("acesso-perdido-gfxcapture-ruim-ddagrab-bom-volta-uma-vez", VoltaUmaVez),
            ("acesso-perdido-gfxcapture-ruim-ddagrab-tambem-ruim-fica", DdagrabTambemRuimFica),
            ("acesso-perdido-gfxcapture-acima-do-piso-fica", GfxcaptureAcimaDoPisoFica),
            ("acesso-perdido-so-volta-com-a-captura-da-troca", SoVoltaComACapturaDaTroca),
            ("acesso-perdido-piso-acompanha-a-meta", PisoAcompanhaAMeta),
            ("acesso-perdido-mesmo-criterio-do-vigia", MesmoCriterioDoVigia),
            ("acesso-perdido-linha-de-processo-antigo-nao-marca", LinhaDeProcessoAntigo),
            ("acesso-perdido-outro-monitor-reinicia", OutroMonitorReinicia),
            ("acesso-perdido-mesma-fonte-outra-qualidade-mantem", MesmaFonteMantem),
            ("acesso-perdido-queda-de-outro-monitor-nao-soma", QuedaDeOutroMonitorNaoSoma),
        };
        var linhas = new List<string>();
        foreach (var (nome, corpo) in casos)
        {
            try { corpo(); linhas.Add($"OK    volta-da-captura-{nome}"); }
            catch (Exception e) { linhas.Add($"FALHA volta-da-captura-{nome}: {e.Message}"); }
        }
        linhas.ForEach(Console.WriteLine);
        if (args.Length == 1) File.WriteAllLines(args[0], linhas);
        return linhas.Any(l => l.StartsWith("FALHA")) ? 1 : 0;
    }

    static void Exigir(bool condicao, string erro) { if (!condicao) throw new Exception(erro); }

    // LIV-141: a reabertura veio ~5 ms depois do 887a0026 e o DXGI ainda não listava o monitor
    static void EsperaDepoisDoDdagrab() =>
        Exigir(VoltaDaCaptura.EsperaMonitor(caiuNoDdagrab: true, aindaQuerDdagrab: true, monitorNoDxgi: false, esperouMs: 0),
            "com o ddagrab recém-caído e o monitor fora do DXGI, o app tem que esperar antes de cair para o gfxcapture");

    static void ParaQuandoVolta() =>
        Exigir(!VoltaDaCaptura.EsperaMonitor(true, true, monitorNoDxgi: true, esperouMs: 250),
            "o monitor voltou ao DXGI: reabre pelo ddagrab na hora");

    // a queda real (o ddagrab não volta) continua caindo para o gfxcapture, só 1,5 s depois
    static void DesisteNoLimite()
    {
        Exigir(VoltaDaCaptura.EsperaMonitor(true, true, false, VoltaDaCaptura.EsperaMaximaMs - 1), "antes do limite ainda espera");
        Exigir(!VoltaDaCaptura.EsperaMonitor(true, true, false, VoltaDaCaptura.EsperaMaximaMs), "no limite desiste e segue para o gfxcapture");
        Exigir(VoltaDaCaptura.EsperaMaximaMs <= 1500, "a espera não pode passar de 1,5 s: a volta boa (RTX 3070) leva ~2,5 s ao todo");
    }

    static void PassosCabem()
    {
        int passo = VoltaDaCaptura.PassoMs, limite = VoltaDaCaptura.EsperaMaximaMs;
        Exigir(passo >= 200 && passo <= 300 && limite / passo >= 4, "olhar o DXGI a cada 200–300 ms, com pelo menos 4 olhadas na espera");
    }

    static void NaoEsperaGfxcapture() =>
        Exigir(!VoltaDaCaptura.EsperaMonitor(caiuNoDdagrab: false, true, false, 0),
            "a captura que caiu era o gfxcapture: não há ddagrab para esperar");

    static void NaoEsperaVigia() =>
        Exigir(!VoltaDaCaptura.EsperaMonitor(true, aindaQuerDdagrab: false, false, 0),
            "o vigia mandou trocar para o gfxcapture do monitor: esperar o ddagrab só atrasaria a troca");

    // LIV-141: "A fonte mudou (1920x1080 → 0x0, gfxcapture-monitor); testando o encoder de novo" e 6 encoders falhando com 0x80070057
    static void ZeroNaoTesta()
    {
        Exigir(VoltaDaCaptura.FonteVazia(0, 0), "0x0 é fonte vazia");
        Exigir(!VoltaDaCaptura.TestaEncoder(0, 0), "fonte 0x0 nunca dispara o teste de encoder");
    }

    static void MeioZeroNaoTesta()
    {
        Exigir(!VoltaDaCaptura.TestaEncoder(1920, 0), "sem altura não é fonte");
        Exigir(!VoltaDaCaptura.TestaEncoder(0, 1080), "sem largura não é fonte");
    }

    // LIV-149: o jogo trocou o monitor para 640x480 e o ddagrab reabriu direto: esse tamanho é real e pede o teste do encoder
    static void NormalTesta()
    {
        Exigir(VoltaDaCaptura.TestaEncoder(1920, 1080), "1920x1080 testa o encoder quando muda");
        Exigir(VoltaDaCaptura.TestaEncoder(640, 480), "640x480 do jogo em modo antigo é fonte de verdade");
    }

    static readonly IntPtr Antigo = 65655, Novo = 65700, Outro = 131000;

    static void AchaPeloHandle()
    {
        var r = VoltaDaCaptura.Acha([(@"\\.\DISPLAY2", Outro), (@"\\.\DISPLAY1", Antigo)], Antigo, @"\\.\DISPLAY1");
        Exigir(r.Achou && r.Monitor == Antigo && !r.PorNome, "o identificador de sempre continua valendo");
    }

    // o Windows recriou o monitor com outro HMONITOR depois da troca de modo: só o nome do dispositivo reencontra
    static void AchaPeloNome()
    {
        var r = VoltaDaCaptura.Acha([(@"\\.\DISPLAY2", Outro), (@"\\.\DISPLAY1", Novo)], Antigo, @"\\.\display1");
        Exigir(r.Achou && r.Monitor == Novo && r.PorNome, "tem que achar o DISPLAY1 pelo nome, sem diferenciar maiúsculas, e dizer que o identificador mudou");
    }

    static void NaoAcha()
    {
        Exigir(!VoltaDaCaptura.Acha([(@"\\.\DISPLAY1", Novo)], Antigo, null).Achou, "sem nome lembrado, só o identificador vale");
        Exigir(!VoltaDaCaptura.Acha([(@"\\.\DISPLAY2", Outro)], Antigo, @"\\.\DISPLAY1").Achou, "o monitor fora do DXGI não pode virar outro monitor");
        Exigir(!VoltaDaCaptura.Acha([], Antigo, @"\\.\DISPLAY1").Achou, "DXGI vazio (driver reiniciando): não achou");
        Exigir(!VoltaDaCaptura.Acha([(@"\\.\DISPLAY1", IntPtr.Zero)], Antigo, @"\\.\DISPLAY1").Achou, "saída sem monitor ligado não serve");
    }

    // LIV-141: monitor 1920x1080 → gfxcapture 0x0
    static void MantemCom0x0() =>
        Exigir(VoltaDaCaptura.MantemAnterior(mudou: true, fonteJanela: false, janelaSumiu: false, 0, 0), "fonte 0x0: reabre a captura anterior em vez de testar encoder");

    // LIV-138: janela do Discord com 144x20
    static void MantemComJanelaSumida() =>
        Exigir(VoltaDaCaptura.MantemAnterior(true, fonteJanela: true, janelaSumiu: true, 144, 20), "janela sumida: reabre a anterior (regra da JanelaMinimizada)");

    // LIV-149: o jogo pôs o monitor em 640x480; e a troca janela ↔ monitor de sempre
    static void TestaComFonteNova()
    {
        Exigir(!VoltaDaCaptura.MantemAnterior(true, false, false, 640, 480), "monitor em 640x480 é fonte nova: testa o encoder");
        Exigir(!VoltaDaCaptura.MantemAnterior(true, true, false, 1920, 1080), "janela visível com tamanho novo: testa o encoder");
    }

    static void NadaMudou()
    {
        Exigir(!VoltaDaCaptura.MantemAnterior(false, false, false, 0, 0), "sem mudança não há o que manter (a captura é a mesma)");
        Exigir(!VoltaDaCaptura.MantemAnterior(false, true, true, 144, 20), "sem mudança não há o que manter");
    }

    // LIV-141 22:18: reset do driver. 5 reaberturas de ~1,7 s com o monitor fora encerravam a live
    static void QuedaNaoContaAte10s()
    {
        Exigir(!VoltaDaCaptura.ContaQueda(contaNormal: true, vaziaHaMs: 0), "logo depois de a tela sumir, a queda não conta");
        Exigir(!VoltaDaCaptura.ContaQueda(true, VoltaDaCaptura.EsperaTotalMs - 1), "até o limite não conta");
        Exigir(VoltaDaCaptura.ContaQueda(true, VoltaDaCaptura.EsperaTotalMs), "depois do limite volta a contar: a tela não vai voltar");
        Exigir(VoltaDaCaptura.EsperaTotalMs >= 5 * (VoltaDaCaptura.EsperaMaximaMs + 200), "o limite tem que cobrir mais que 5 reaberturas com espera");
    }

    static void QuedaNormal()
    {
        Exigir(VoltaDaCaptura.ContaQueda(true, null), "com a tela de volta, a queda conta como sempre");
        Exigir(!VoltaDaCaptura.ContaQueda(false, null), "o que a regra de sempre não conta (janela sumida, captura que durou) continua sem contar");
    }

    // LIV-151, 2026-11-24 (RTX 2070): 42 quedas por 887a0026 em 49 min, mediana de 19 s entre elas, a menor com 0,8 s
    static readonly DateTime T0 = new(2026, 11, 24, 21, 0, 0);
    static readonly string M1 = TrocaPorAcessoPerdido.Chave(false, @"\\.\DISPLAY1");
    static readonly string M2 = TrocaPorAcessoPerdido.Chave(false, @"\\.\DISPLAY2");

    static bool Queda(TrocaPorAcessoPerdido r, double segundos, bool monitorNoDdagrab = true, bool acessoPerdido = true, string? chave = null) =>
        r.Registra(T0.AddSeconds(segundos), monitorNoDdagrab, acessoPerdido, chave ?? M1);

    /// <summary>Três quedas seguidas: a regra decide pelo gfxcapture (estado NoGfxcapture).</summary>
    static TrocaPorAcessoPerdido NoGfxcapture(string? chave = null)
    {
        var r = new TrocaPorAcessoPerdido();
        Queda(r, 0, chave: chave); Queda(r, 1, chave: chave);
        Exigir(Queda(r, 2, chave: chave), "a 3ª queda decide");
        return r;
    }

    static void AcessoTresEm120()
    {
        var r = new TrocaPorAcessoPerdido();
        Exigir(r.PermiteDdagrab && r.Estado == EstadoDaTroca.Ddagrab, "começa no ddagrab");
        Exigir(!Queda(r, 0) && !Queda(r, 19), "a 1ª e a 2ª reabrem pelo ddagrab como hoje");
        Exigir(r.PermiteDdagrab && !r.NoGfxcapture, "ainda no ddagrab depois de 2 quedas");
        Exigir(Queda(r, 38), "a 3ª em 120 s troca");
        Exigir(r.Estado == EstadoDaTroca.NoGfxcapture && !r.PermiteDdagrab, "trocou: o ddagrab não é mais permitido (a não ser pela volta)");
        Exigir(r.UltimaTroca.Quedas == 3 && Math.Abs(r.UltimaTroca.Segundos - 38) < 0.001, "guarda quantas quedas e em quantos segundos (para o log)");
        // o limite: 3 quedas dentro de exatamente 120 s ainda trocam
        var limite = new TrocaPorAcessoPerdido();
        Exigir(!Queda(limite, 0) && !Queda(limite, 60) && Queda(limite, 120), "a 1ª e a 3ª com exatamente 120 s de diferença ainda trocam");
    }

    static void AcessoUmeDoisNaoTrocam()
    {
        var r = new TrocaPorAcessoPerdido();
        Exigir(!Queda(r, 0), "uma queda isolada não troca");
        Exigir(!Queda(r, 1), "duas quedas (a menor distância do campo, 0,8 s) não trocam");
        Exigir(r.Estado == EstadoDaTroca.Ddagrab, "nada trocado");
    }

    static void AcessoEspalhadas()
    {
        var r = new TrocaPorAcessoPerdido();
        // uma a cada 3 min (jogo que troca de modo de vez em quando): nunca 3 na mesma janela
        for (int i = 0; i < 10; i++) Exigir(!Queda(r, i * 180), $"queda {i + 1} espalhada não troca");
        Exigir(!r.NoGfxcapture, "nunca trocou");
        // 3 quedas, mas a 1ª e a 3ª com 121 s de diferença
        var r2 = new TrocaPorAcessoPerdido();
        Exigir(!Queda(r2, 0) && !Queda(r2, 60) && !Queda(r2, 121), "121 s entre a 1ª e a 3ª: a 1ª já saiu da janela");
    }

    static void AcessoJanelaDeslizante()
    {
        var r = new TrocaPorAcessoPerdido();
        Exigir(!Queda(r, 0) && !Queda(r, 100), "duas quedas");
        Exigir(!Queda(r, 150), "a 1ª saiu da janela (150 s): sobram 2 (100 s e 150 s)");
        Exigir(Queda(r, 200), "100 s, 150 s e 200 s cabem em 120 s: troca");
    }

    static void AcessoSemCodigo()
    {
        var r = new TrocaPorAcessoPerdido();
        for (int i = 0; i < 20; i++) Exigir(!Queda(r, i, acessoPerdido: false), "queda sem 887a0026 (encoder, ffmpeg morto por outro motivo) não conta");
        Exigir(!r.NoGfxcapture, "20 quedas sem 887a0026 não trocam");
        // e não zera nem soma para as que têm o código
        Exigir(!Queda(r, 30) && !Queda(r, 31, acessoPerdido: false) && !Queda(r, 32), "intercalada com queda sem o código: ainda só 2 com o código");
        Exigir(Queda(r, 33), "a 3ª com o código troca");
    }

    static void AcessoSoMonitorNoDdagrab()
    {
        var r = new TrocaPorAcessoPerdido();
        // fonte janela ou captura gfxcapture (o chamador diz que não é monitor no ddagrab), mesmo com 887a0026
        for (int i = 0; i < 20; i++) Exigir(!Queda(r, i, monitorNoDdagrab: false), "janela ou gfxcapture não contam");
        Exigir(r.Estado == EstadoDaTroca.Ddagrab, "nada trocado");
        Exigir(!Queda(r, 30) && !Queda(r, 31), "e não deixaram resto na contagem: ainda começa do zero");
    }

    static void AcessoLiveNova()
    {
        var r = NoGfxcapture();
        r.Reiniciar();
        Exigir(r.PermiteDdagrab && r.Estado == EstadoDaTroca.Ddagrab && !r.QuerGfxcapture(M1), "live nova: o ddagrab volta a ser o padrão");
        Exigir(!Queda(r, 3) && !Queda(r, 4), "e a contagem recomeça do zero");
        // também depois da volta ao ddagrab: live nova, regra nova
        var v = NoGfxcapture();
        v.Volta();
        v.Reiniciar();
        Exigir(!Queda(v, 0) && !Queda(v, 1) && Queda(v, 2), "depois da volta, uma live nova troca de novo");
    }

    // 3 quedas: a próxima abertura do monitor é o gfxcapture-monitor (e antes disso é o padrão)
    static void ProximaAberturaEGfxcapture()
    {
        var r = new TrocaPorAcessoPerdido();
        Queda(r, 0); Queda(r, 1);
        Exigir(TrocaPorAcessoPerdido.ProximaCaptura(null, false, false, r.QuerGfxcapture(M1)) == MetodoDaCaptura.Padrao, "com 2 quedas ainda abre o padrão (ddagrab)");
        Queda(r, 2);
        Exigir(r.QuerGfxcapture(M1), "depois da 3ª, este monitor quer o gfxcapture");
        Exigir(TrocaPorAcessoPerdido.ProximaCaptura(null, false, false, r.QuerGfxcapture(M1)) == MetodoDaCaptura.GfxcaptureMonitor,
            "a próxima abertura é o gfxcapture-monitor, mesmo sem o vigia ter mandado (_preferDda nulo)");
        Exigir(TrocaPorAcessoPerdido.ProximaCaptura(true, false, false, r.QuerGfxcapture(M1)) == MetodoDaCaptura.GfxcaptureMonitor,
            "a troca por 887a0026 vale mais que um _preferDda = true antigo do vigia");
    }

    static void EscolhaDaCaptura()
    {
        Exigir(TrocaPorAcessoPerdido.ProximaCaptura(null, false, false, false) == MetodoDaCaptura.Padrao, "monitor sem nada: padrão");
        Exigir(TrocaPorAcessoPerdido.ProximaCaptura(true, false, false, false) == MetodoDaCaptura.Ddagrab, "o vigia mandou o ddagrab");
        Exigir(TrocaPorAcessoPerdido.ProximaCaptura(false, false, false, false) == MetodoDaCaptura.GfxcaptureMonitor, "o vigia mandou o gfxcapture do monitor");
        Exigir(TrocaPorAcessoPerdido.ProximaCaptura(false, true, true, false) == MetodoDaCaptura.Janela, "o vigia mandou a janela");
        Exigir(TrocaPorAcessoPerdido.ProximaCaptura(false, true, false, false) == MetodoDaCaptura.Padrao, "janela que não existe mais: padrão, como sempre");
        Exigir(TrocaPorAcessoPerdido.ProximaCaptura(null, true, true, true) == MetodoDaCaptura.Padrao, "a troca por 887a0026 é só do monitor: fonte janela segue o padrão");
    }

    // a troca espera o monitor voltar ao DXGI (o gfxcapture também precisa dele); a do vigia, com o ddagrab vivo, não
    static void TrocaEsperaOMonitor()
    {
        Exigir(VoltaDaCaptura.AguardaMonitor(preferDda: null, gfxPorAcessoPerdido: true), "troca por 887a0026 com o padrão: espera");
        Exigir(VoltaDaCaptura.AguardaMonitor(false, gfxPorAcessoPerdido: true), "troca por 887a0026 vale mesmo com o vigia tendo mandado o gfxcapture");
        Exigir(!VoltaDaCaptura.AguardaMonitor(false, false), "só a troca do vigia continua sem esperar");
        Exigir(VoltaDaCaptura.AguardaMonitor(null, false) && VoltaDaCaptura.AguardaMonitor(true, false), "ddagrab (padrão ou do vigia) espera como antes");
        // a queda de verdade: o ddagrab acabou de cair, o monitor ainda fora do DXGI → espera; com o monitor de volta, não
        bool aguarda = VoltaDaCaptura.AguardaMonitor(null, true);
        Exigir(VoltaDaCaptura.EsperaMonitor(caiuNoDdagrab: true, aguarda, monitorNoDxgi: false, esperouMs: 0), "monitor ausente logo depois do 887a0026: espera antes de abrir o gfxcapture");
        Exigir(!VoltaDaCaptura.EsperaMonitor(true, aguarda, monitorNoDxgi: true, esperouMs: 250), "o monitor voltou: abre o gfxcapture na hora");
        Exigir(!VoltaDaCaptura.EsperaMonitor(true, aguarda, false, VoltaDaCaptura.EsperaMaximaMs), "no limite desiste (e a reabertura cai no ddagrab com a troca pendente)");
        Exigir(!VoltaDaCaptura.EsperaMonitor(caiuNoDdagrab: false, aguarda, false, 0), "o gfxcapture que já estava aberto e caiu: não há o que esperar");
    }

    // o gfxcapture abriu 0x0 (monitor ainda fora): keepOld reabre o ddagrab, e a troca continua valendo na queda seguinte
    static void TrocaPendente()
    {
        var r = NoGfxcapture();
        Exigir(VoltaDaCaptura.MantemAnterior(mudou: true, fonteJanela: false, janelaSumiu: false, 0, 0), "fonte 0x0: a reabertura fica com o ddagrab anterior");
        // a reabertura que ficou no ddagrab não desfez nada: a regra segue querendo o gfxcapture e a próxima abertura tenta de novo
        Exigir(r.QuerGfxcapture(M1) && r.Estado == EstadoDaTroca.NoGfxcapture, "a troca continua pendente");
        Exigir(TrocaPorAcessoPerdido.ProximaCaptura(null, false, false, r.QuerGfxcapture(M1)) == MetodoDaCaptura.GfxcaptureMonitor, "a abertura seguinte tenta o gfxcapture de novo");
        // o ddagrab reaberto cai de novo com 887a0026: não decide outra vez (Registra devolve falso) e a troca segue pendente
        Exigir(!Queda(r, 10), "a queda do ddagrab reaberto não decide outra vez");
        Exigir(r.QuerGfxcapture(M1), "e a troca continua pendente");
        for (int i = 0; i < 5; i++) { Queda(r, 20 + i); Exigir(r.QuerGfxcapture(M1), $"queda {i + 1} depois: ainda pendente, até o gfxcapture abrir"); }
    }

    // gfxcapture a ~1/s (RoR2, 2026-11-12) e ddagrab pegando bem mais: volta ao ddagrab, uma vez só
    static void VoltaUmaVez()
    {
        var r = NoGfxcapture();
        Exigir(r.PodeVoltar(capturaEhGfxcapture: true, fps: 60, gfxSonda: 1, gfxNaLive: 2, ddagrabSonda: 58), "gfxcapture a ~1/s e ddagrab a 58/s: pode voltar");
        Exigir(r.Volta(), "volta");
        Exigir(r.Estado == EstadoDaTroca.VoltouAoDdagrab && r.PermiteDdagrab && !r.QuerGfxcapture(M1), "depois da volta o ddagrab é permitido e a regra não quer mais o gfxcapture");
        Exigir(TrocaPorAcessoPerdido.ProximaCaptura(true, false, false, r.QuerGfxcapture(M1)) == MetodoDaCaptura.Ddagrab, "a abertura seguinte é o ddagrab");
        // o 887a0026 volta: nada de contar, nada de trocar, por mais que caia (congelar 2 s a cada 20 s é melhor que vaivém)
        for (int i = 0; i < 50; i++) Exigir(!Queda(r, 100 + i * 10), "só uma volta: nunca mais troca");
        Exigir(r.Estado == EstadoDaTroca.VoltouAoDdagrab && !r.QuerGfxcapture(M1), "continua no ddagrab até o fim da live");
        Exigir(!r.PodeVoltar(true, 60, 1, 1, 58) && !r.Volta(), "e não há uma segunda volta");
    }

    static void DdagrabTambemRuimFica()
    {
        var r = NoGfxcapture();
        Exigir(!r.PodeVoltar(true, 60, gfxSonda: 1, gfxNaLive: 1, ddagrabSonda: 1), "os dois a ~1/s (o jogo está parado): fica");
        Exigir(!r.PodeVoltar(true, 60, 2, 2, ddagrabSonda: 20), "ddagrab a 20/s ainda não é 60% da meta: fica");
        Exigir(!r.PodeVoltar(true, 60, 30, 30, ddagrabSonda: 40), "gfxcapture a 30/s: passa do piso e o ddagrab não pega 1,5x: fica");
        Exigir(r.NoGfxcapture, "continua no gfxcapture");
    }

    static void GfxcaptureAcimaDoPisoFica()
    {
        var r = NoGfxcapture();
        Exigir(!r.PodeVoltar(true, 60, gfxSonda: 25, gfxNaLive: 25, ddagrabSonda: 58), "gfxcapture a 25/s é ruim mas assistível: o ddagrab cai a cada ~20 s, então fica");
        Exigir(!r.PodeVoltar(true, 60, gfxSonda: 3, gfxNaLive: 25, ddagrabSonda: 58), "a live ainda recebe 25/s (a sonda foi pior): fica");
        Exigir(!r.PodeVoltar(true, 60, gfxSonda: 10, gfxNaLive: 5, ddagrabSonda: 58), "exatamente no piso (10/s) ainda não volta: o piso é estrito");
        Exigir(r.PodeVoltar(true, 60, gfxSonda: 9, gfxNaLive: 5, ddagrabSonda: 58), "logo abaixo do piso volta");
    }

    static void SoVoltaComACapturaDaTroca()
    {
        var pendente = NoGfxcapture();
        Exigir(!pendente.PodeVoltar(capturaEhGfxcapture: false, 60, 1, 1, 58), "a troca pendente (a captura ainda é o ddagrab): nada para medir, não volta");
        var semTroca = new TrocaPorAcessoPerdido();
        Exigir(!semTroca.PodeVoltar(true, 60, 1, 1, 58), "sem troca por 887a0026 o vigia usa a regra de sempre, não esta volta");
        Exigir(!semTroca.Volta() && semTroca.Estado == EstadoDaTroca.Ddagrab, "Volta sem troca não muda nada");
    }

    static void PisoAcompanhaAMeta()
    {
        var r = NoGfxcapture();
        Exigir(r.PodeVoltar(true, 30, 8, 8, 28) && !r.PodeVoltar(true, 30, 9, 9, 28), "a 30 fps o piso é 9/s (30% da meta), não 10");
        Exigir(r.PodeVoltar(true, 144, 9, 9, 140) && !r.PodeVoltar(true, 144, 10, 10, 140), "a 144 fps o piso continua 10/s");
        Exigir(TrocaPorAcessoPerdido.PisoGfxcapture == 10, "piso de 10 quadros novos/s");
    }

    // o mesmo critério do vigia (ShouldSwitchMethod): o outro método pega >= 60% da meta e mais de 1,5x o atual
    static void MesmoCriterioDoVigia()
    {
        Exigir(TrocaPorAcessoPerdido.OutroPegaBemMais(60, 5, 36), "36/s é 60% de 60 e 7x o atual");
        Exigir(!TrocaPorAcessoPerdido.OutroPegaBemMais(60, 5, 35), "35/s é menos de 60% da meta");
        Exigir(!TrocaPorAcessoPerdido.OutroPegaBemMais(60, 30, 45), "45/s não passa de 1,5x os 30/s atuais");
        Exigir(TrocaPorAcessoPerdido.OutroPegaBemMais(60, 30, 46), "46/s passa de 1,5x os 30/s");
    }

    // o fim do stderr de um ffmpeg antigo chega depois de o novo abrir
    static void LinhaDeProcessoAntigo()
    {
        Exigir(TrocaPorAcessoPerdido.LinhaDoProcessoAtual(7, 7), "linha do processo atual vale");
        Exigir(!TrocaPorAcessoPerdido.LinhaDoProcessoAtual(6, 7), "linha do processo anterior não marca nem suja o estado do novo");
        Exigir(!TrocaPorAcessoPerdido.LinhaDoProcessoAtual(8, 7), "nem uma geração que ainda não abriu");
    }

    // a troca é do monitor onde aconteceu: outro monitor ou uma janela recomeça a regra
    static void OutroMonitorReinicia()
    {
        var r = NoGfxcapture(M1);
        Exigir(r.QuerGfxcapture(M1) && !r.QuerGfxcapture(M2), "a troca vale para o monitor 1 e não para o 2 (a escolha da captura de InitialCapture)");
        Exigir(r.Fonte(M2), "trocar a fonte para outro monitor reinicia a regra");
        Exigir(r.Estado == EstadoDaTroca.Ddagrab && r.PermiteDdagrab && !r.QuerGfxcapture(M2), "no monitor 2 o ddagrab volta a ser o padrão");
        Exigir(!r.QuerGfxcapture(M1), "e voltar ao monitor 1 não ressuscita a troca");
        // janela
        var j = NoGfxcapture(M1);
        Exigir(j.Fonte(TrocaPorAcessoPerdido.Chave(true, null)) && !j.NoGfxcapture, "uma janela reinicia");
        // depois da volta ao ddagrab também: outro monitor não herda o "não troca mais"
        var v = NoGfxcapture(M1); v.Volta();
        v.Fonte(M2);
        Exigir(!Queda(v, 0, chave: M2) && !Queda(v, 1, chave: M2) && Queda(v, 2, chave: M2), "outro monitor depois da volta: a regra vale de novo");
    }

    static void MesmaFonteMantem()
    {
        var r = NoGfxcapture(M1);
        Exigir(!r.Fonte(M1), "mesma fonte (só a qualidade mudou): nada a descartar");
        Exigir(r.NoGfxcapture && r.QuerGfxcapture(M1), "e a troca continua valendo");
        // o nome do dispositivo não distingue maiúsculas
        Exigir(r.QuerGfxcapture(TrocaPorAcessoPerdido.Chave(false, @"\\.\display1")), "o nome do monitor não depende de maiúsculas");
        // contagem em andamento também se mantém
        var c = new TrocaPorAcessoPerdido();
        Queda(c, 0); Queda(c, 1);
        Exigir(!c.Fonte(M1), "mesma fonte com 2 quedas contadas");
        Exigir(Queda(c, 2), "a 3ª ainda troca");
    }

    static void QuedaDeOutroMonitorNaoSoma()
    {
        var r = new TrocaPorAcessoPerdido();
        Queda(r, 0); Queda(r, 1);
        Exigir(!Queda(r, 2, chave: M2), "a 3ª queda foi em OUTRO monitor: as 2 do primeiro não somam");
        Exigir(!Queda(r, 3, chave: M2) && Queda(r, 4, chave: M2), "o monitor 2 conta do zero");
    }

    static void Interrompe()
    {
        Exigir(!VoltaDaCaptura.Interrompe(rodando: true, pausaMudou: false, qualidadeMudou: false), "nada novo: segue esperando");
        Exigir(VoltaDaCaptura.Interrompe(false, false, false), "a live parou: não espera nem abre captura");
        Exigir(VoltaDaCaptura.Interrompe(true, true, false), "a trava de foco pediu pausa (o UAC rouba o foco): pausa já");
        Exigir(VoltaDaCaptura.Interrompe(true, false, true), "a qualidade mudou: o laço troca já");
    }
}
