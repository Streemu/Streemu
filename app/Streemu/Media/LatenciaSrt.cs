namespace Streemu.Media;

/// <summary>
/// Latência do SRT de quem ASSISTE (o SRT usa a maior entre a do receptor e a do transmissor, negociada no handshake; subir no transmissor atrasaria TODOS os
/// espectadores da live, então só o espectador escolhe). Campo (2026-11-22, live de 4 h, PC para celular pelo relay): ida e volta de ~106 ms e ~18% de perda no túnel
/// com latência de 80 ms nos dois lados; com a latência menor que uma ida e volta o SRT não retransmite a tempo e a imagem sai em blocos.
/// Lógica pura (sem túnel, sem relógio) para o teste cobrir os números.
/// </summary>
public static class LatenciaSrt
{
    public const int PadraoMs = 80, MinimoMs = 80, MaximoMs = 600, TetoEscaladaMs = 1000, PisoRelayMs = 200, MaximoSubidas = 4;
    /// <summary>Ida e volta presumida quando o túnel ainda não mediu nenhuma (a latência não pode esperar a medida: o player abre agora).</summary>
    public const double RttPresumidoMs = 120;

    /// <summary>
    /// Sem ida e volta medida, o tempo que a ligação levou para conectar limita a presunção (conectar leva pelo menos uma ida e volta): campo 2026-11-22, notebook e PC na
    /// mesma rede, conexão em 10 ms e ida e volta real de 6 ms, mas o player abriu com 380 ms (presunção de 120) e a live ficou ~300 ms mais atrasada que o necessário.
    /// Nunca passa de <see cref="RttPresumidoMs"/>: conexão lenta (furo de NAT) não diz nada sobre a ida e volta.
    /// </summary>
    internal static double RttPresumido(long conectouEmMs) => conectouEmMs > 0 ? Math.Min(RttPresumidoMs, conectouEmMs) : RttPresumidoMs;

    /// <summary>3 idas e voltas + 20 ms, entre 80 e 600 ms; pelo relay, no mínimo 200 ms. Em rede local dá 80 ms, como o padrão antigo.</summary>
    public static int Calcular(double rttMs, bool pelaRelay, long conectouEmMs = -1)
    {
        double rtt = rttMs > 0 ? rttMs : RttPresumido(conectouEmMs);
        int ms = (int)Math.Clamp(Math.Round(3 * rtt + 20), MinimoMs, MaximoMs);
        return pelaRelay ? Math.Max(ms, PisoRelayMs) : ms;
    }

    /// <summary>Próxima latência depois de perda sustentada: x1,5 até 1000 ms.</summary>
    public static int Subir(int atualMs) => (int)Math.Min(TetoEscaladaMs, Math.Round(atualMs * 1.5));
}

/// <summary>
/// Decide quando a latência deve SUBIR na próxima abertura do player: perda do túnel acima de 2% por ~6 s seguidos, no máximo 4 vezes por live.
/// Não reabre o player: reabrir corta a imagem de todo mundo nessa janela (<c>Restarting</c> avisa o transmissor, que reinicia o envio), e a escalada vale
/// na próxima abertura que acontecer por outro motivo (queda, reabertura do player, volta da decodificação na CPU).
/// </summary>
public sealed class EscaladaLatencia
{
    public const double LimitePerdaPct = 2.0;
    public const long SustentadaMs = 6000;
    /// <summary>Pacotes mínimos por amostra para a porcentagem valer (com 3 pacotes, 1 perdido seria 25%).</summary>
    public const long MinPacotes = 50;

    long _recebidos, _perdidos, _desdeMs = -1;
    bool _tem;
    public int Subidas { get; private set; }

    /// <summary>Latência a usar na próxima abertura (parte da inicial e só sobe).</summary>
    public int ProximaMs { get; private set; }
    public void Iniciar(int inicialMs) { ProximaMs = inicialMs; Subidas = 0; _tem = false; _desdeMs = -1; }

    /// <summary>
    /// Uma leitura dos contadores acumulados do túnel (recebidos e perdidos desde o começo), a cada ~1 s. Devolve true quando <see cref="ProximaMs"/> acabou de subir.
    /// A perda do intervalo vem da diferença entre as leituras, sem consumir a janela que a interface usa (<c>TakeWindowLossPct</c>).
    /// </summary>
    public bool Observar(long agoraMs, long recebidos, long perdidos)
    {
        if (!_tem) { _tem = true; _recebidos = recebidos; _perdidos = perdidos; return false; }
        long dr = Math.Max(0, recebidos - _recebidos), dp = Math.Max(0, perdidos - _perdidos);
        _recebidos = recebidos; _perdidos = perdidos;
        if (dr + dp < MinPacotes) return false; // sem tráfego suficiente: nem confirma nem quebra a sequência
        double pct = 100.0 * dp / (dr + dp);
        if (pct <= LimitePerdaPct) { _desdeMs = -1; return false; }
        if (_desdeMs < 0) _desdeMs = agoraMs;
        if (agoraMs - _desdeMs < SustentadaMs || Subidas >= LatenciaSrt.MaximoSubidas || ProximaMs >= LatenciaSrt.TetoEscaladaMs) return false;
        ProximaMs = LatenciaSrt.Subir(ProximaMs);
        Subidas++;
        _desdeMs = agoraMs; // a próxima subida pede mais 6 s de perda
        return true;
    }
}
