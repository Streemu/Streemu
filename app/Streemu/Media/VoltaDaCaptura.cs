namespace Streemu.Media;

/// <summary>
/// O que fazer quando a captura do monitor (ddagrab) cai e o app vai reabrir, sem Win32: o <c>StreamSender</c> olha o DXGI e pergunta aqui.
/// Testado em tests/VoltaDaCaptura.
/// Campo 2026-11-23 (LIV-141, RX 6600, três vezes na mesma live): o ddagrab saiu com <c>887a0026</c> (DXGI_ERROR_ACCESS_LOST: troca de modo, tela
/// cheia exclusiva, UAC, reset do driver). Na reabertura, ~5 ms depois, o DXGI ainda não listava o monitor e nem o Windows dava o tamanho dele: o app
/// caiu para o gfxcapture com fonte 0x0, testou os 6 encoders contra ela (~1,6 s, todos com <c>0x80070057</c>) e só então reabriu o ddagrab, que já
/// funcionava. Voltou em 3,8 a 5,3 s; num PC em que o monitor já aparecia na hora (RTX 3070, LIV-149), ~2,5 s.
/// </summary>
static class VoltaDaCaptura
{
    /// <summary>Intervalo entre uma olhada e outra no DXGI enquanto o monitor não volta.</summary>
    public const int PassoMs = 250;

    /// <summary>Quanto uma reabertura espera o monitor voltar antes de escolher a captura (a queda real continua indo para o gfxcapture, 1,5 s depois).</summary>
    public const int EsperaMaximaMs = 1500;

    /// <summary>
    /// Quanto tempo, somando as reaberturas, a fonte pode ficar sem tamanho sem que a queda conte para "caiu 5 vezes seguidas". Um reset do driver
    /// (22:18 da LIV-141, junto com <c>VK_ERROR_DEVICE_LOST</c>) pode tirar o monitor por alguns segundos: sem isso, 5 reaberturas de ~1,7 s
    /// encerravam a live por algo que se resolve sozinho.
    /// </summary>
    public const int EsperaTotalMs = 10_000;

    /// <summary>
    /// Espera mais um passo antes de escolher a captura? Só quando a captura que caiu era o ddagrab, o app ainda quer o ddagrab (o vigia não mandou
    /// trocar para o gfxcapture), o monitor ainda não aparece no DXGI e a espera não passou de <see cref="EsperaMaximaMs"/>.
    /// </summary>
    public static bool EsperaMonitor(bool caiuNoDdagrab, bool aindaQuerDdagrab, bool monitorNoDxgi, long esperouMs) =>
        caiuNoDdagrab && aindaQuerDdagrab && !monitorNoDxgi && esperouMs < EsperaMaximaMs;

    /// <summary>
    /// Para de esperar no meio porque o laço da live tem coisa mais urgente: parar, pôr ou tirar a pausa (trava de foco: o UAC rouba o foco no mesmo
    /// instante do ACCESS_LOST) ou trocar a qualidade. Quem assiste vê "Transmissão pausada" em vez da imagem parada.
    /// </summary>
    public static bool Interrompe(bool rodando, bool pausaMudou, bool qualidadeMudou) => !rodando || pausaMudou || qualidadeMudou;

    /// <summary>
    /// Acha o monitor no DXGI: pelo identificador (HMONITOR) e, se ele não aparece, pelo nome do dispositivo (<c>\\.\DISPLAY1</c>) visto na última
    /// abertura. Fonte "monitor" guarda o HMONITOR da hora em que foi escolhida; na LIV-141 o Windows deu 0x0 para esse identificador logo depois do
    /// ACCESS_LOST e, se ele recria o monitor com outro identificador, só o nome reencontra. <paramref name="porNome"/> diz que o identificador mudou.
    /// </summary>
    public static (bool Achou, IntPtr Monitor, bool PorNome) Acha(IReadOnlyList<(string Nome, IntPtr Monitor)> saidas, IntPtr monitor, string? nome)
    {
        foreach (var s in saidas) if (s.Monitor == monitor) return (true, monitor, false);
        if (!string.IsNullOrEmpty(nome))
            foreach (var s in saidas)
                if (string.Equals(s.Nome, nome, StringComparison.OrdinalIgnoreCase) && s.Monitor != IntPtr.Zero) return (true, s.Monitor, true);
        return (false, monitor, false);
    }

    /// <summary>Fonte sem tamanho: o Windows ainda não devolveu o monitor (ou a janela). Não é uma fonte, é "a tela ainda não voltou".</summary>
    public static bool FonteVazia(int largura, int altura) => largura <= 0 || altura <= 0;

    /// <summary>Com a captura reaberta numa fonte nova, testa o encoder de novo? Nunca com fonte vazia: nenhum encoder abre a partir dela.</summary>
    public static bool TestaEncoder(int largura, int altura) => !FonteVazia(largura, altura);

    /// <summary>
    /// A decisão da reabertura: com a fonte mudada, fica com a captura e o encoder que funcionavam (sem testar encoder) quando a fonte nova é vazia
    /// (monitor que ainda não voltou, LIV-141) ou é uma janela sumida (minimizada ou 144x20, LIV-138, regra de <see cref="JanelaMinimizada"/>).
    /// </summary>
    public static bool MantemAnterior(bool mudou, bool fonteJanela, bool janelaSumiu, int largura, int altura) =>
        mudou && (FonteVazia(largura, altura) || !JanelaMinimizada.TestaEncoder(fonteJanela, janelaSumiu));

    /// <summary>
    /// A queda da captura conta para "caiu 5 vezes seguidas"? A regra de sempre (<paramref name="contaNormal"/>, de
    /// <see cref="JanelaMinimizada.ContaQueda"/>), menos enquanto a fonte está sem tamanho há menos de <see cref="EsperaTotalMs"/>.
    /// </summary>
    public static bool ContaQueda(bool contaNormal, long? vaziaHaMs) => contaNormal && !(vaziaHaMs is long ms && ms < EsperaTotalMs);
}
