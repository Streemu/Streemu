using Streemu.Media;

// Regra da janela transmitida que some (Media/JanelaMinimizada.cs), com os números do campo de 2026-11-23 (LIV-138):
// janela do Discord que voltou a minimizar e a captura passou a ver 144x20.
static class Program
{
    static int Main(string[] args)
    {
        var casos = new (string Nome, Action Corpo)[]
        {
            ("minimizada-sumiu", Minimizada),
            ("discord-144x20-sumiu", Discord144x20),
            ("janela-normal-nao-sumiu", JanelaNormal),
            ("limite-160x90", Limite),
            ("janela-sumida-pausa-mesmo-pela-janela", PausaPelaJanela),
            ("janela-atras-de-outra-nao-pausa-pela-janela", AtrasNaoPausa),
            ("trava-de-foco-do-monitor-continua", TravaDeFoco),
            ("queda-com-janela-sumida-nao-conta", QuedaNaoConta),
            ("queda-normal-continua-contando", QuedaConta),
            ("nao-testa-encoder-com-janela-sumida", NaoTestaEncoder),
            ("testa-encoder-com-monitor-ou-janela-visivel", TestaEncoder),
            ("aviso-so-depois-de-10-s-e-uma-vez-por-live", Aviso),
            ("aviso-so-quando-captura-pela-janela", AvisoSoPelaJanela),
            ("restaura-de-novo-so-nos-primeiros-5-s-e-uma-vez", RestauraDeNovo),
            ("texto-do-aviso-diz-o-que-fazer", TextoDoAviso),
            ("texto-da-barra-comeca-por-transmissao-pausada", TextoDaBarra),
        };
        var linhas = new List<string>();
        foreach (var (nome, corpo) in casos)
        {
            try { corpo(); linhas.Add($"OK    janela-minimizada-{nome}"); }
            catch (Exception e) { linhas.Add($"FALHA janela-minimizada-{nome}: {e.Message}"); }
        }
        linhas.ForEach(Console.WriteLine);
        if (args.Length == 1) File.WriteAllLines(args[0], linhas);
        return linhas.Any(l => l.StartsWith("FALHA")) ? 1 : 0;
    }

    static void Exigir(bool condicao, string erro) { if (!condicao) throw new Exception(erro); }

    static void Minimizada() => Exigir(JanelaMinimizada.Sumiu(true, 1920, 1078), "janela minimizada (ou na bandeja) sumiu, qualquer que seja o tamanho guardado");
    static void Discord144x20() => Exigir(JanelaMinimizada.Sumiu(false, 144, 20), "a captura vendo 144x20 (campo 2026-11-23) é janela sumida");
    static void JanelaNormal() => Exigir(!JanelaMinimizada.Sumiu(false, 1920, 1078), "janela de 1920x1078 visível não sumiu");

    static void Limite()
    {
        Exigir(!JanelaMinimizada.Sumiu(false, 160, 90), "160x90 ainda é janela");
        Exigir(JanelaMinimizada.Sumiu(false, 159, 600), "largura menor que 160 é sumida");
        Exigir(JanelaMinimizada.Sumiu(false, 800, 89), "altura menor que 90 é sumida");
    }

    static void PausaPelaJanela()
    {
        Exigir(JanelaMinimizada.Pausa(capturaPeloMonitor: false, mostraJogo: false, janelaSumiu: true), "captura pela janela com ela minimizada pausa (antes caía e testava os encoders)");
        Exigir(JanelaMinimizada.Pausa(capturaPeloMonitor: true, mostraJogo: true, janelaSumiu: true), "janela sumida pausa também capturando pelo monitor");
    }

    static void AtrasNaoPausa() =>
        Exigir(!JanelaMinimizada.Pausa(capturaPeloMonitor: false, mostraJogo: false, janelaSumiu: false), "pela janela, a janela atrás de outra continua sendo capturada: não pausa");

    static void TravaDeFoco()
    {
        Exigir(JanelaMinimizada.Pausa(capturaPeloMonitor: true, mostraJogo: false, janelaSumiu: false), "pelo monitor, jogo fora da frente pausa (trava de foco de antes)");
        Exigir(!JanelaMinimizada.Pausa(capturaPeloMonitor: true, mostraJogo: true, janelaSumiu: false), "pelo monitor, jogo na frente não pausa");
    }

    static void QuedaNaoConta()
    {
        Exigir(!JanelaMinimizada.ContaQueda(janelaSumiu: true, 0.2), "queda logo ao abrir com a janela minimizada não conta para \"caiu 5 vezes seguidas\"");
    }

    static void QuedaConta()
    {
        Exigir(JanelaMinimizada.ContaQueda(janelaSumiu: false, 0.2), "queda logo ao abrir com a janela visível conta");
        Exigir(!JanelaMinimizada.ContaQueda(janelaSumiu: false, 30), "queda depois de 5 s aberta não é \"logo ao abrir\"");
    }

    static void NaoTestaEncoder() =>
        Exigir(!JanelaMinimizada.TestaEncoder(fonteJanela: true, janelaSumiu: true), "fonte de janela sumida não testa encoder (antes: 9 encoders, ~2,8 s por rodada)");

    static void TestaEncoder()
    {
        Exigir(JanelaMinimizada.TestaEncoder(fonteJanela: true, janelaSumiu: false), "janela visível que mudou de tamanho testa o encoder");
        Exigir(JanelaMinimizada.TestaEncoder(fonteJanela: false, janelaSumiu: false), "monitor que mudou testa o encoder");
    }

    static void Aviso()
    {
        Exigir(!JanelaMinimizada.Avisa(true, TimeSpan.FromSeconds(3), jaAvisouNestaLive: false), "minimizar de passagem (3 s) não avisa");
        Exigir(JanelaMinimizada.Avisa(true, TimeSpan.FromSeconds(10), jaAvisouNestaLive: false), "10 s minimizada avisa");
        Exigir(!JanelaMinimizada.Avisa(true, TimeSpan.FromMinutes(5), jaAvisouNestaLive: true), "avisa uma vez só por live, não a cada sumiço");
    }

    static void AvisoSoPelaJanela()
    {
        Exigir(!JanelaMinimizada.Avisa(capturaPelaJanela: false, TimeSpan.FromMinutes(5), jaAvisouNestaLive: false),
            "jogo em tela cheia capturado pelo monitor que minimiza no Alt+Tab pausa em silêncio (sem aviso nem balão)");
        Exigir(JanelaMinimizada.Avisa(capturaPelaJanela: true, JanelaMinimizada.AvisoDepois, jaAvisouNestaLive: false), "pela janela, exatamente 10 s já avisa");
    }

    static void RestauraDeNovo()
    {
        Exigir(JanelaMinimizada.RestauraDeNovo(appRestaurou: true, TimeSpan.FromSeconds(0.4), jaRestaurouDeNovo: false),
            "campo 2026-11-23: o Discord minimizou ~0,4 s depois; o app restaura mais uma vez");
        Exigir(JanelaMinimizada.RestauraDeNovo(appRestaurou: true, TimeSpan.FromSeconds(4.9), jaRestaurouDeNovo: false), "ainda dentro dos 5 s");
        Exigir(!JanelaMinimizada.RestauraDeNovo(appRestaurou: true, TimeSpan.FromSeconds(5), jaRestaurouDeNovo: false), "depois de 5 s da live a minimização vale como pedido da pessoa");
        Exigir(!JanelaMinimizada.RestauraDeNovo(appRestaurou: true, TimeSpan.FromSeconds(1), jaRestaurouDeNovo: true), "só uma segunda restauração por live");
        Exigir(!JanelaMinimizada.RestauraDeNovo(appRestaurou: false, TimeSpan.FromSeconds(1), jaRestaurouDeNovo: false),
            "se o app não restaurou a janela (ela já estava visível), minimizar é decisão da pessoa: não restaura");
    }

    static void TextoDoAviso()
    {
        string t = JanelaMinimizada.Aviso("Discord");
        Exigir(t.Contains("Discord") && t.Contains("minimizada"), "o aviso diz qual janela e o que aconteceu");
        Exigir(t.Contains("Abra ela na frente") && t.Contains("monitor"), "o aviso diz o que fazer (abrir na frente ou transmitir o monitor)");
        Exigir(t.Contains("volta sozinha"), "o aviso diz que a live volta sem precisar reiniciar");
    }

    static void TextoDaBarra()
    {
        string t = JanelaMinimizada.TextoDaBarra("Discord");
        Exigir(t.StartsWith("Transmissão pausada:") && t.Contains("Discord") && t.Contains("minimizada"), "a barra diz que pausou, qual janela e por quê");
        Exigir(t.Contains("Abra ela na frente") && t.Contains("monitor") && t.Contains("volta sozinha"), "a barra diz o que fazer, e não \"volte para o jogo\"");
    }
}
