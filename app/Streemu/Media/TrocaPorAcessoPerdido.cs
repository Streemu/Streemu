namespace Streemu.Media;

/// <summary>Em que ponto está a regra do <c>887a0026</c> nesta live (ver <see cref="TrocaPorAcessoPerdido"/>).</summary>
enum EstadoDaTroca
{
    /// <summary>Monitor no ddagrab, contando quedas. É o começo de toda live.</summary>
    Ddagrab,
    /// <summary>A 3ª queda decidiu: o monitor passa a abrir pelo gfxcapture. Vale desde a decisão, mesmo que a abertura ainda não tenha dado certo (pendente).</summary>
    NoGfxcapture,
    /// <summary>O gfxcapture rendeu quase nada e o ddagrab muito mais: voltou UMA vez e esta regra não troca mais nesta live (sem vaivém).</summary>
    VoltouAoDdagrab,
}

/// <summary>Que captura abrir na reabertura (ver <see cref="TrocaPorAcessoPerdido.ProximaCaptura"/>).</summary>
enum MetodoDaCaptura { Padrao, Ddagrab, Janela, GfxcaptureMonitor }

/// <summary>
/// Quando a captura do MONITOR pelo ddagrab cai de novo e de novo com <c>887a0026</c> (DXGI_ERROR_ACCESS_LOST), reabrir pelo mesmo ddagrab não adianta:
/// passa a capturar o monitor pelo gfxcapture. Lógica pura (recebe os horários e as medidas; sem relógio, sem ffmpeg) para o teste cobrir os números.
/// Campo 2026-11-24 (LIV-151, RTX 2070, Windows 22H2, monitor inteiro 1360x768@60 com ddagrab + nvenc): em 49 min o ffmpeg da captura saiu sozinho 42 vezes,
/// sempre com <c>AcquireNextFrame failed: 887a0026</c> (mediana de 19 s entre quedas, a menor com 0,8 s) e cada reabertura deixava quem assiste ~2,3 s parado.
/// O monitor nunca sumiu do DXGI (então a espera de <see cref="VoltaDaCaptura"/> não agia) e a queda dura mais de 5 s em geral (então o "caiu 5 vezes
/// seguidas" zerava). O Windows tira o acesso do ddagrab quando o jogo em tela cheia troca de modo ou de foco; não controlamos isso, mas o gfxcapture do
/// mesmo monitor funcionou no mesmo PC (a sonda abriu).
/// Borda amarela: no Windows 10 (o PC da LIV-151 é 19045) o gfxcapture desenha a borda na tela de quem transmite, e ela não vai para a stream. Não há
/// como tirá-la ali: <c>IsBorderRequired</c> só existe a partir do Windows 11 (22000), e o ffmpeg já a usa quando existe. É o preço aceito para parar de congelar.
/// Três estados (<see cref="EstadoDaTroca"/>): ddagrab contando quedas, gfxcapture, e "voltou ao ddagrab" (uma vez só). O gfxcapture do monitor com jogo em
/// tela cheia já entregou ~1 quadro/s (2026-11-12, RoR2 em 4K): por isso o vigia pode devolver a live ao ddagrab, mas uma vez: congelar 2 s a cada ~20 s
/// é melhor que um slideshow contínuo, e vaivém é pior que os dois. A regra vale para UM monitor (<see cref="Chave"/>): outro monitor ou uma janela a reinicia.
/// Testado em tests/VoltaDaCaptura.
/// </summary>
sealed class TrocaPorAcessoPerdido
{
    /// <summary>Quantas quedas por <c>887a0026</c> dentro da janela trocam a captura. A 1ª e a 2ª ainda reabrem pelo ddagrab (uma queda isolada é comum e se resolve).</summary>
    public const int QuedasParaTrocar = 3;

    /// <summary>Janela, em segundos, em que as quedas contam juntas. Espalhadas por mais tempo que isso (uma troca de modo de vez em quando) não trocam nada.</summary>
    public const int JanelaSegundos = 120;

    /// <summary>
    /// Piso do gfxcapture, em quadros novos/s, para o vigia devolver a live ao ddagrab: abaixo disso (nunca mais que 30% da meta) é slideshow, e uma volta
    /// custa ~2,3 s parado. Acima, o gfxcapture é ruim mas assistível, e o ddagrab que cai a cada ~20 s (~12% do tempo congelado) não é claramente melhor.
    /// </summary>
    public const double PisoGfxcapture = 10;

    readonly object _gate = new();
    readonly Queue<DateTime> _quedas = new();
    EstadoDaTroca _estado;
    string? _fonte;
    (int Quedas, double Segundos) _ultimaTroca;

    public EstadoDaTroca Estado { get { lock (_gate) return _estado; } }

    /// <summary>O monitor está (ou vai ficar, se a abertura estiver pendente) no gfxcapture por causa do <c>887a0026</c>.</summary>
    public bool NoGfxcapture => Estado == EstadoDaTroca.NoGfxcapture;

    /// <summary>O ddagrab pode ser usado nesta live? Falso só enquanto a troca vale: o vigia não escolhe o ddagrab (a não ser pela volta de <see cref="PodeVoltar"/>).</summary>
    public bool PermiteDdagrab => !NoGfxcapture;

    /// <summary>Quantas quedas e em quantos segundos (da primeira à última) fizeram a troca; só vale depois dela.</summary>
    public (int Quedas, double Segundos) UltimaTroca { get { lock (_gate) return _ultimaTroca; } }

    /// <summary>Nova live: zera tudo.</summary>
    public void Reiniciar() { lock (_gate) { _quedas.Clear(); _estado = EstadoDaTroca.Ddagrab; _ultimaTroca = default; _fonte = null; } }

    /// <summary>
    /// Identifica a fonte onde a regra vale: o monitor (pelo nome do dispositivo, que sobrevive à troca de identificador) ou "janela". O estado é de UM
    /// monitor: a troca num não pode valer para outro (o outro pode capturar bem pelo ddagrab).
    /// </summary>
    public static string Chave(bool fonteJanela, string? nomeDoMonitor) => fonteJanela ? "janela" : "monitor|" + (nomeDoMonitor ?? "?").ToLowerInvariant();

    /// <summary>
    /// A fonte da live é esta. Se for outra que a da regra (outro monitor, ou uma janela), a regra recomeça do zero; a mesma fonte com outra qualidade
    /// mantém tudo. Devolve true se havia algo (quedas contadas ou uma troca) que foi descartado.
    /// </summary>
    public bool Fonte(string chave)
    {
        lock (_gate)
        {
            bool descartou = false;
            if (_fonte != null && _fonte != chave)
            {
                descartou = _estado != EstadoDaTroca.Ddagrab || _quedas.Count > 0;
                _quedas.Clear(); _estado = EstadoDaTroca.Ddagrab; _ultimaTroca = default;
            }
            _fonte = chave;
            return descartou;
        }
    }

    /// <summary>A próxima abertura deste monitor é pelo gfxcapture por causa do <c>887a0026</c>? Só para o monitor onde a troca aconteceu.</summary>
    public bool QuerGfxcapture(string chave) { lock (_gate) return _estado == EstadoDaTroca.NoGfxcapture && _fonte == chave; }

    /// <summary>
    /// A captura acabou de cair. Devolve true UMA vez, na queda que completa <see cref="QuedasParaTrocar"/> dentro da janela. Só conta queda de
    /// monitor no ddagrab (<paramref name="monitorNoDdagrab"/>; janela e gfxcapture ficam de fora) e que trouxe <c>887a0026</c> no stderr
    /// (<paramref name="acessoPerdido"/>; uma queda por outro motivo não conta nem zera a contagem). Depois da decisão (e depois da volta ao ddagrab)
    /// não conta mais: a abertura pelo gfxcapture que ficou pendente se repete sozinha a cada reabertura, por <see cref="QuerGfxcapture"/>.
    /// </summary>
    public bool Registra(DateTime agora, bool monitorNoDdagrab, bool acessoPerdido, string chave)
    {
        lock (_gate)
        {
            if (!monitorNoDdagrab || !acessoPerdido) return false;
            if (_fonte != null && _fonte != chave) { _quedas.Clear(); _estado = EstadoDaTroca.Ddagrab; _ultimaTroca = default; }
            _fonte = chave;
            if (_estado != EstadoDaTroca.Ddagrab) return false;
            while (_quedas.Count > 0 && (agora - _quedas.Peek()).TotalSeconds > JanelaSegundos) _quedas.Dequeue();
            _quedas.Enqueue(agora);
            if (_quedas.Count < QuedasParaTrocar) return false;
            _estado = EstadoDaTroca.NoGfxcapture;
            _ultimaTroca = (_quedas.Count, (agora - _quedas.Peek()).TotalSeconds);
            _quedas.Clear();
            return true;
        }
    }

    /// <summary>
    /// O vigia mediu o gfxcapture que a troca abriu e a sonda do ddagrab: devolve a live ao ddagrab? Só se (1) a regra está no gfxcapture e a captura
    /// da live É o gfxcapture do monitor (uma troca ainda pendente não tem o que medir), (2) o gfxcapture está abaixo do piso, tanto na sonda quanto no
    /// que a live recebe, e (3) o ddagrab pega bem mais (o mesmo critério de <see cref="OutroPegaBemMais"/>). Se o ddagrab também está ruim, fica.
    /// </summary>
    public bool PodeVoltar(bool capturaEhGfxcapture, int fps, double gfxSonda, double gfxNaLive, double ddagrabSonda)
    {
        if (!NoGfxcapture || !capturaEhGfxcapture) return false;
        double piso = Math.Min(PisoGfxcapture, fps * 0.3);
        return Math.Max(gfxSonda, gfxNaLive) < piso && OutroPegaBemMais(fps, gfxSonda, ddagrabSonda);
    }

    /// <summary>Fez a volta ao ddagrab: a regra não troca mais nesta live. Só tem efeito no gfxcapture da troca.</summary>
    public bool Volta()
    {
        lock (_gate)
        {
            if (_estado != EstadoDaTroca.NoGfxcapture) return false;
            _estado = EstadoDaTroca.VoltouAoDdagrab;
            return true;
        }
    }

    /// <summary>O critério do vigia para trocar de método: o outro pega ao menos 60% da meta e mais de 1,5x o atual (o mesmo de <c>ShouldSwitchMethod</c>).</summary>
    public static bool OutroPegaBemMais(int fps, double atual, double outro) => outro >= fps * 0.6 && outro > atual * 1.5;

    /// <summary>
    /// Que captura abrir. <paramref name="preferDda"/> é o que o vigia mandou (nulo = padrão); <paramref name="gfxPorAcessoPerdido"/> é a troca desta regra
    /// para o monitor da fonte e vale primeiro (inclusive quando a abertura anterior ficou no ddagrab: a troca continua pendente e a próxima abertura tenta
    /// o gfxcapture de novo). Janela que não existe mais cai no padrão, como sempre.
    /// </summary>
    public static MetodoDaCaptura ProximaCaptura(bool? preferDda, bool fonteJanela, bool janelaExiste, bool gfxPorAcessoPerdido)
    {
        if (!fonteJanela && gfxPorAcessoPerdido) return MetodoDaCaptura.GfxcaptureMonitor;
        return preferDda switch
        {
            true => MetodoDaCaptura.Ddagrab,
            false when fonteJanela && janelaExiste => MetodoDaCaptura.Janela,
            // monitor: o vigia viu o ddagrab quase sem quadros e o gfxcapture do mesmo monitor pegando bem mais
            false when !fonteJanela => MetodoDaCaptura.GfxcaptureMonitor,
            _ => MetodoDaCaptura.Padrao
        };
    }

    /// <summary>
    /// A linha do stderr é do ffmpeg que está rodando agora? Cada abertura da captura tem um número (geração); o fim do stderr de um ffmpeg antigo ainda
    /// pode chegar depois de o novo abrir, e um <c>887a0026</c> dele marcaria o novo como "tela perdida" (e contaria queda que não houve).
    /// </summary>
    public static bool LinhaDoProcessoAtual(int geracaoDaLinha, int geracaoAtual) => geracaoDaLinha == geracaoAtual;
}
