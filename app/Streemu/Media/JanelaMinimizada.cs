namespace Streemu.Media;

/// <summary>
/// O que fazer quando a janela transmitida some (minimiza, vai para a bandeja ou fica com tamanho que nenhuma captura útil tem), sem Win32: o
/// <c>StreamSender</c> lê o estado da janela e pergunta aqui. Testado em tests/JanelaMinimizada.
/// Campo 2026-11-23 (LIV-138): a pessoa transmitiu a janela de uma chamada do Discord. O app restaurou a janela e a live abriu, mas o Discord minimizou
/// de novo quando o Streemu ganhou o foco. A captura passou a ver 144x20, o app testou os 9 encoders 5 vezes (44 falhas em 14 s) e encerrou a live com
/// "caiu 5 vezes seguidas". Janela minimizada não entrega quadro: nenhum encoder resolve. O certo é pausar e esperar a janela voltar.
/// </summary>
static class JanelaMinimizada
{
    /// <summary>Menor área útil de janela. O Discord minimizado aparece com 144x20; janela de verdade nunca é tão pequena.</summary>
    public const int LarguraMinima = 160, AlturaMinima = 90;

    /// <summary>Quanto tempo a janela fica sumida antes de o app dizer à pessoa o que fazer (Alt+Tab rápido e minimizar de passagem não avisam).</summary>
    public static readonly TimeSpan AvisoDepois = TimeSpan.FromSeconds(10);

    /// <summary>A janela sumiu: minimizada, escondida (bandeja) ou com área menor que <see cref="LarguraMinima"/>x<see cref="AlturaMinima"/>.</summary>
    public static bool Sumiu(bool minimizadaOuEscondida, int largura, int altura) =>
        minimizadaOuEscondida || largura < LarguraMinima || altura < AlturaMinima;

    /// <summary>
    /// A live mostra a tela "Transmissão pausada"? Capturando pelo monitor vale a trava de foco (<paramref name="mostraJogo"/>, regra do
    /// <c>StreamSender.ShowsGame</c>). Capturando pela janela a live segue com a janela atrás de outra, mas pausa quando ela some.
    /// </summary>
    public static bool Pausa(bool capturaPeloMonitor, bool mostraJogo, bool janelaSumiu) =>
        janelaSumiu || (capturaPeloMonitor && !mostraJogo);

    /// <summary>A captura que caiu conta para "caiu 5 vezes seguidas logo ao abrir"? Não com a janela sumida: a queda é dela, não do ffmpeg.</summary>
    public static bool ContaQueda(bool janelaSumiu, double segundosDesdeAbrir) => !janelaSumiu && segundosDesdeAbrir < 5;

    /// <summary>Com a captura reaberta numa fonte nova, testa o encoder de novo? Não se a fonte é uma janela sumida: nenhum encoder recebe quadro dela.</summary>
    public static bool TestaEncoder(bool fonteJanela, bool janelaSumiu) => !(fonteJanela && janelaSumiu);

    /// <summary>
    /// Já é hora de avisar a pessoa? Só quando a captura é pela JANELA: jogo em tela cheia capturado pelo monitor que minimiza no Alt+Tab pausa em
    /// silêncio, como sempre (a trava de foco já explica na barra de status). E no máximo uma vez por live: quem minimiza de novo depois do aviso já sabe.
    /// </summary>
    public static bool Avisa(bool capturaPelaJanela, TimeSpan sumidaHa, bool jaAvisouNestaLive) =>
        capturaPelaJanela && !jaAvisouNestaLive && sumidaHa >= AvisoDepois;

    /// <summary>Janela que o app restaurou e que minimiza de novo nesse prazo depois de a live abrir é minimização que ninguém pediu (o Discord faz isso).</summary>
    public static readonly TimeSpan RestauraDeNovoAte = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Restaura a janela mais uma vez, sem roubar o foco? Só se foi o app que a restaurou ao abrir a live, ela sumiu de novo nos primeiros
    /// <see cref="RestauraDeNovoAte"/> da live (contados da abertura, não da restauração: o teste do encoder fica no meio e leva alguns segundos) e ainda
    /// não houve a segunda restauração desta live. Depois disso vale a pausa e o aviso.
    /// Campo 2026-11-23 (LIV-138): o Discord minimizou ~0,4 s depois de o app restaurar a janela dele.
    /// </summary>
    public static bool RestauraDeNovo(bool appRestaurou, TimeSpan desdeAAberturaDaLive, bool jaRestaurouDeNovo) =>
        appRestaurou && !jaRestaurouDeNovo && desdeAAberturaDaLive < RestauraDeNovoAte;

    /// <summary>O texto da barra de status na hora em que a live pausa por janela sumida (o balão do <see cref="Aviso"/> só vem depois de <see cref="AvisoDepois"/>).</summary>
    public static string TextoDaBarra(string nomeDaJanela) =>
        $"Transmissão pausada: a janela {nomeDaJanela} está minimizada. Abra ela na frente (ou transmita o monitor) e a live volta sozinha.";

    /// <summary>O aviso curto: o que aconteceu e o que fazer.</summary>
    public static string Aviso(string nomeDaJanela) =>
        $"A janela {nomeDaJanela} está minimizada e os amigos veem \"Transmissão pausada\". Abra ela na frente (ou transmita o monitor) e a live volta sozinha.";
}
