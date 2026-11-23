namespace Streemu.Media;

/// <summary>
/// Decide quando oferecer "usar 30 FPS" a quem transmite em 60. Campo (2026-11-22, notebook i5-8250U): em 60 FPS a captura entregava ~35–48 quadros novos/s
/// com quedas a ~31 e a CPU do app a ~74% de um núcleo; em 30 FPS os quadros ficaram em 27–29/s, a CPU caiu para ~52% e o vigia não reabriu nada.
/// O app não sabe se o limite é do jogo ou do PC (os dois dão poucos quadros novos), e para a oferta tanto faz: 30 estáveis ajudam nos dois casos.
/// Só mede, nunca troca sozinho: quem decide é a pessoa. Lógica pura (sem relógio, sem interface) para o teste cobrir os números.
/// </summary>
public sealed class SugestaoDe30Fps
{
    /// <summary>Segundos de abertura ignorados (a captura e o encoder ainda esquentam).</summary>
    public const int SegundosDeAbertura = 10;
    /// <summary>Segundos abaixo da meta, líquidos, para mostrar a oferta.</summary>
    public const int SegundosAbaixoParaMostrar = 15;
    /// <summary>Segundos bons seguidos para a oferta sumir sozinha (o jogo ou o PC se recuperou).</summary>
    public const int SegundosBonsParaSumir = 10;
    /// <summary>Abaixo disto, da meta, o segundo conta como ruim.</summary>
    public const double FracaoDaMeta = 0.75;
    const int JanelaDaMediana = 15;

    readonly List<int> _ultimos = new();
    int _abaixo, _bons, _vistos;
    bool _recusada;

    public bool Visivel { get; private set; }

    /// <summary>Mediana dos últimos quadros novos/s medidos; o que o card mostra ("chegam ~36 quadros por segundo").</summary>
    public int Tipico => _ultimos.Count == 0 ? 0 : _ultimos.OrderBy(x => x).ElementAt(_ultimos.Count / 2);

    /// <summary>Nova live: recomeça do zero, inclusive o "agora não" da anterior.</summary>
    public void Reiniciar() { _ultimos.Clear(); _abaixo = _bons = _vistos = 0; _recusada = false; Visivel = false; }

    /// <summary>A pessoa recusou: não oferece de novo nesta live.</summary>
    public void Recusar() { _recusada = true; Visivel = false; }

    /// <summary>Esconde sem recusar (a pessoa aceitou, a live parou ou o preset mudou).</summary>
    public void Esconder() { _abaixo = _bons = 0; _ultimos.Clear(); Visivel = false; }

    /// <summary>
    /// Um segundo medido. <paramref name="medida"/> é a do <c>SecondMeasured</c>: só "sensor" e "mudo" contam; "pausa" (jogo fora da frente) e "sem_medida" são neutros.
    /// Devolve true quando <see cref="Visivel"/> mudou neste segundo.
    /// </summary>
    public bool Observar(int fpsPedido, int quadrosNovos, string medida)
    {
        bool antes = Visivel;
        // só em 60: a oferta diz "abaixo de 60" e troca para 30; quem pediu 90 ou 120 (beta) escolheu outra coisa e não é o caso desta oferta
        if (fpsPedido != 60) { Esconder(); return antes != Visivel; }
        if (medida is not ("sensor" or "mudo")) return false;
        if (++_vistos <= SegundosDeAbertura) return false;
        if (quadrosNovos > fpsPedido + 10) return false; // despejo do contador depois de um engasgo, não o ritmo do jogo
        _ultimos.Add(quadrosNovos);
        if (_ultimos.Count > JanelaDaMediana) _ultimos.RemoveAt(0);
        Contar(quadrosNovos < fpsPedido * FracaoDaMeta);
        if (!Visivel && !_recusada && _abaixo >= SegundosAbaixoParaMostrar) Visivel = true;
        else if (Visivel && _bons >= SegundosBonsParaSumir) Visivel = false;
        return antes != Visivel;
    }

    // um segundo bom abate dois ruins: engasgo isolado não acumula, queda sustentada sim
    void Contar(bool ruim)
    {
        if (ruim) { _abaixo++; _bons = 0; return; }
        _bons++; _abaixo = Math.Max(0, _abaixo - 2);
    }
}
