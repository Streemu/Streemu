namespace Streemu.Media;

/// <summary>
/// Confere se reabrir ou trocar a captura valeu a pena. Campo (2026-11-22, notebook i5-8250U com Intel UHD 620, live de 10 min): 12 reaberturas e 4 trocas
/// ddagrab ↔ gfxcapture, cada uma congelando a imagem de quem assiste por 1–2 s, e depois de TODAS a captura voltava a ~36 quadros novos/s. O notebook estava no limite
/// (GPU a ~35%, ~70% de um núcleo de CPU no app), não com a captura travada; a sonda ainda lia 70–85/s numa tela de 60 Hz e mandava reabrir de novo.
/// Duas reaberturas seguidas sem ganho real encerram as reaberturas do vigia nesta live (o resto do vigia, como o encoder aos trancos, não muda).
/// Lógica pura (sem relógio nem ffmpeg) para o teste cobrir os números.
/// </summary>
public sealed class ReaberturaInutil
{
    /// <summary>Tempo para a captura nova estabilizar antes de medir o ganho.</summary>
    public const double AvaliarDepoisDeSegundos = 12;
    /// <summary>Ganho mínimo (em relação aos quadros novos/s de antes) para a reabertura contar como útil.</summary>
    public const double GanhoMinimo = 1.25;
    public const int InuteisParaDesistir = 2;

    double _antes, _fps, _aberturaEm = -1;
    readonly List<int> _depois = new();
    int _inuteisSeguidas;

    /// <summary>Verdadeiro quando o vigia não deve mais reabrir nem trocar a captura nesta live.</summary>
    public bool Desistiu => _inuteisSeguidas >= InuteisParaDesistir;

    public void Reiniciar() { _aberturaEm = -1; _depois.Clear(); _inuteisSeguidas = 0; }

    /// <summary>
    /// O vigia vai reabrir (ou trocar) a captura. <paramref name="medianaAntes"/> é a mediana dos últimos ~20 s, não o fundo do engasgo: no campo os engasgos duravam 2–3 s
    /// (11/s, depois um despejo de 175 e 160) e a captura voltava sozinha ao mesmo ~35/s de antes, o que não pode contar como ganho da reabertura.
    /// </summary>
    public void Reabriu(double agoraS, double medianaAntes, int fps)
    {
        _aberturaEm = agoraS; _antes = medianaAntes; _fps = fps; _depois.Clear();
    }

    /// <summary>
    /// Um segundo de quadros novos/s da captura nova. Devolve true no instante em que a reabertura acabou de ser julgada inútil e passou a contar para desistir.
    /// </summary>
    public bool Observar(double agoraS, int quadrosNovos)
    {
        if (_aberturaEm < 0) return false;
        double decorrido = agoraS - _aberturaEm;
        if (decorrido > AvaliarDepoisDeSegundos - 6) _depois.Add(quadrosNovos); // só os últimos ~6 s: os primeiros ainda têm a abertura da captura
        if (decorrido < AvaliarDepoisDeSegundos || _depois.Count == 0) return false;
        _aberturaEm = -1;
        var v = _depois.OrderBy(x => x).ToArray();
        double mediana = v[v.Length / 2];
        bool saudavel = mediana >= _fps * 0.75;
        bool ganhou = saudavel || mediana >= Math.Max(1, _antes) * GanhoMinimo;
        _inuteisSeguidas = ganhou ? 0 : _inuteisSeguidas + 1;
        return !ganhou;
    }
}
