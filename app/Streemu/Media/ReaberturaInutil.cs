namespace Streemu.Media;

/// <summary>
/// Confere se reabrir ou trocar a captura valeu a pena. Campo (2026-11-22, notebook i5-8250U com Intel UHD 620, live de 10 min): 12 reaberturas e 4 trocas
/// ddagrab ↔ gfxcapture, cada uma congelando a imagem de quem assiste por 1–2 s, e depois de TODAS a captura voltava a ~36 quadros novos/s. O notebook estava no limite
/// (GPU a ~35%, ~70% de um núcleo de CPU no app), não com a captura travada; a sonda ainda lia 70–85/s numa tela de 60 Hz e mandava reabrir de novo.
/// Duas reaberturas seguidas sem ganho real encerram as reaberturas do vigia nesta live (<see cref="Desistiu"/>, permanente; o resto do vigia não muda).
/// Segundo caso (2026-11-25, LIV-161, Ryzen 5 3600 + RTX 4060, gfxcapture no monitor, 5 espectadores): 53 reaberturas em 34 min (uma a cada ~37 s; 39 rápidas "caiu de novo
/// para ~0", 11 "encoder aos trancos" e 3 "TRAVOU" da sonda), cada uma deixando quem assiste parado de 2,4 a 5,5 s (mediana 3,8 s, ~215 s no total). Aqui TODA reabertura era
/// útil: depois dela a captura voltava a ~60/s, e isso zerava a contagem das inúteis; ela travava de novo ~30 s depois e o vigia nunca desistia. Por isso há também um limite
/// por janela: no máximo <see cref="ReaberturasNaJanela"/> reaberturas do vigia em <see cref="JanelaSegundos"/> (<see cref="NoLimite"/>), mesmo que cada uma tenha "funcionado".
/// O limite vale só para a reabertura rápida (sem sonda) e para o "encoder aos trancos". A SONDA fica fora de propósito: ela só reabre ou troca quando confirma a trava
/// (a mediana da live ainda embaixo depois de ~2–3 s, enquanto uma captura nova pega), e as pausas de 1–3 s da LIV-161 voltam sozinhas durante a sonda; se a trava for de
/// verdade (0/s sem voltar), a live não pode ficar congelada até o fim nem sem a troca de método. As reaberturas da sonda continuam contando na janela. O limite também não é
/// permanente: quando a reabertura mais antiga sai da janela, a reabertura rápida volta a poder agir.
/// Só entram aqui as reaberturas do vigia. O ffmpeg da captura que morreu (<c>RestartCapture</c> chamado pelo laço) nunca passa por esta classe: sem reabrir a live para.
/// Os métodos são chamados pela thread do vigia e pela da sonda (<c>Task.Run</c>), por isso todo o estado fica atrás de um lock interno.
/// Lógica pura (sem relógio nem ffmpeg) para o teste cobrir os números.
/// </summary>
public sealed class ReaberturaInutil
{
    /// <summary>Tempo para a captura nova estabilizar antes de medir o ganho.</summary>
    public const double AvaliarDepoisDeSegundos = 12;
    /// <summary>Ganho mínimo (em relação aos quadros novos/s de antes) para a reabertura contar como útil.</summary>
    public const double GanhoMinimo = 1.25;
    public const int InuteisParaDesistir = 2;
    /// <summary>Máximo de reaberturas do vigia dentro da janela (o ciclo do campo reabria a cada ~37 s: a 5ª, aos ~2,5 min, já é barrada).</summary>
    public const int ReaberturasNaJanela = 4;
    /// <summary>Largura da janela do limite por frequência (10 min).</summary>
    public const double JanelaSegundos = 600;

    readonly object _gate = new();
    double _antes, _fps, _aberturaEm = -1;
    readonly List<int> _depois = new();
    readonly List<double> _horarios = new();
    int _inuteisSeguidas;

    /// <summary>Verdadeiro quando o vigia não deve mais reabrir nem trocar a captura nesta live (duas reaberturas seguidas sem ganho). Permanente até <see cref="Reiniciar"/>.</summary>
    public bool Desistiu { get { lock (_gate) return _inuteisSeguidas >= InuteisParaDesistir; } }

    /// <summary>Reaberturas do vigia que ainda cabem na janela que termina em <paramref name="agoraS"/> (para o log dizer quantas).</summary>
    public int ReaberturasRecentes(double agoraS) { lock (_gate) return Dentro(agoraS).Count(); }

    /// <summary>Segundos entre a reabertura mais antiga da janela e a mais recente (para o log dizer em quanto tempo).</summary>
    public double SegundosDaJanela(double agoraS)
    {
        lock (_gate)
        {
            var d = Dentro(agoraS).ToArray();
            return d.Length < 2 ? 0 : d.Max() - d.Min();
        }
    }

    /// <summary>
    /// A janela já tem <see cref="ReaberturasNaJanela"/> reaberturas do vigia: a reabertura rápida e a do "encoder aos trancos" esperam a mais antiga sair dela.
    /// Não mexe em <see cref="Desistiu"/> e não vale para a sonda (ver o resumo da classe).
    /// </summary>
    public bool NoLimite(double agoraS) { lock (_gate) return Dentro(agoraS).Count() >= ReaberturasNaJanela; }

    public void Reiniciar()
    {
        lock (_gate) { _aberturaEm = -1; _depois.Clear(); _horarios.Clear(); _inuteisSeguidas = 0; }
    }

    // chamar só com o lock
    IEnumerable<double> Dentro(double agoraS) => _horarios.Where(h => agoraS - h <= JanelaSegundos);

    /// <summary>
    /// Só conta a reabertura para o limite por janela, sem medir ganho (a do "encoder aos trancos": a captura estava saudável, o que se mede ali é outra coisa).
    /// Devolve true no instante em que esta reabertura acabou de encher a janela (uma vez por vez que ela enche, para o log não repetir).
    /// </summary>
    public bool ContarReabertura(double agoraS)
    {
        lock (_gate) return Contar(agoraS);
    }

    bool Contar(double agoraS)
    {
        _horarios.RemoveAll(h => agoraS - h > JanelaSegundos);
        bool estavaCheia = _horarios.Count >= ReaberturasNaJanela;
        _horarios.Add(agoraS);
        return !estavaCheia && _horarios.Count >= ReaberturasNaJanela;
    }

    /// <summary>
    /// O vigia vai reabrir (ou trocar) a captura. <paramref name="medianaAntes"/> é a mediana dos últimos ~20 s, não o fundo do engasgo: no campo os engasgos duravam 2–3 s
    /// (11/s, depois um despejo de 175 e 160) e a captura voltava sozinha ao mesmo ~35/s de antes, o que não pode contar como ganho da reabertura.
    /// </summary>
    /// <returns>true se esta reabertura acabou de encher a janela (<see cref="NoLimite"/> passou a ser verdadeiro).</returns>
    public bool Reabriu(double agoraS, double medianaAntes, int fps)
    {
        lock (_gate)
        {
            _aberturaEm = agoraS; _antes = medianaAntes; _fps = fps; _depois.Clear();
            return Contar(agoraS);
        }
    }

    /// <summary>
    /// Um segundo de quadros novos/s da captura nova. Devolve true no instante em que a reabertura acabou de ser julgada inútil e passou a contar para desistir.
    /// </summary>
    public bool Observar(double agoraS, int quadrosNovos)
    {
        lock (_gate)
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
}
