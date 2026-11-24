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

    static void Interrompe()
    {
        Exigir(!VoltaDaCaptura.Interrompe(rodando: true, pausaMudou: false, qualidadeMudou: false), "nada novo: segue esperando");
        Exigir(VoltaDaCaptura.Interrompe(false, false, false), "a live parou: não espera nem abre captura");
        Exigir(VoltaDaCaptura.Interrompe(true, true, false), "a trava de foco pediu pausa (o UAC rouba o foco): pausa já");
        Exigir(VoltaDaCaptura.Interrompe(true, false, true), "a qualidade mudou: o laço troca já");
    }
}
