namespace Streemu.Filme;

/// <summary>
/// Regras puras do estado periódico do filme (spec 010, 7.5). Caso de campo de 2026-11-28: o anfitrião pausou por muito tempo, deu play, e o play (um filme_estado
/// mandado uma vez, em 3 cópias UDP em ~300 ms, sem confirmação) não chegou a um espectador, que teve de sair e entrar. Agora o anfitrião repete o estado atual a cada
/// <see cref="ReenvioMs"/> com o MESMO seq; quem está em dia ignora (não reaplica nem procura), quem perdeu um comando vê um seq maior e aplica.
/// </summary>
public static class EstadoDoFilme
{
    /// <summary>De quanto em quanto tempo o anfitrião repete o estado atual. 3 s: um play perdido volta ao ponto em poucos segundos e o custo é uma mensagem pequena por ligação.</summary>
    public const int ReenvioMs = 3000;

    /// <summary>Só um seq maior que o último aplicado é um comando novo; o igual é a repetição periódica e o menor é um atraso de rede.</summary>
    public static bool EhNovo(int seqAtual, int seqRecebido) => seqRecebido > seqAtual;

    /// <summary>
    /// O anfitrião só repete depois do primeiro comando (seq 0 = ainda no aguardo, nada a repetir) e para quando a sessão foi solta
    /// (troca de episódio: a sessão nova repete o dela).
    /// </summary>
    public static bool DeveReenviar(bool anfitriao, int seq, bool soltada) => anfitriao && seq > 0 && !soltada;
}

public enum FaseDoSinal { Normal, SemSinal, Caiu }

/// <summary>
/// O sinal do anfitrião visto por quem assiste (só a lógica do tempo; o MainForm.FilmeSinal.cs liga isto ao túnel). Caso de campo de 2026-11-28: o PC do anfitrião travou
/// e o espectador ficou "conectado" para sempre, porque ninguém olhava o túnel (ele vira Lost depois de 8 s sem pacotes). Aqui: túnel Lost = <see cref="FaseDoSinal.SemSinal"/>
/// (só um aviso: o filme continua, para uma oscilação curta do Wi-Fi não interromper nada); voltou a Connected antes de <see cref="Limite"/> = <see cref="FaseDoSinal.Normal"/>
/// de novo; passou do limite sem voltar = <see cref="FaseDoSinal.Caiu"/> (definitivo).
/// </summary>
public sealed class SinalDoAnfitriao
{
    /// <summary>Quanto tempo seguido sem sinal (contado desde o túnel virar Lost, que já esperou 8 s sem pacotes) até dar o anfitrião por caído.</summary>
    public static readonly TimeSpan Limite = TimeSpan.FromSeconds(30);

    readonly Func<DateTime> _agora;
    readonly TimeSpan _limite;
    DateTime _desde;
    bool _conectou;

    public SinalDoAnfitriao(Func<DateTime>? agora = null, TimeSpan? limite = null)
    {
        _agora = agora ?? (() => DateTime.UtcNow);
        _limite = limite ?? Limite;
    }

    public FaseDoSinal Fase { get; private set; } = FaseDoSinal.Normal;
    /// <summary>Quanto durou o último período sem sinal que terminou com a volta do sinal (para o log).</summary>
    public TimeSpan UltimaQueda { get; private set; }
    /// <summary>Há quanto tempo está sem sinal (zero fora da fase <see cref="FaseDoSinal.SemSinal"/>).</summary>
    public TimeSpan SemSinalHa => Fase == FaseDoSinal.SemSinal ? _agora() - _desde : TimeSpan.Zero;

    /// <summary>O túnel está Connected. Devolve true se a fase mudou (voltou do <see cref="FaseDoSinal.SemSinal"/>). Depois de <see cref="FaseDoSinal.Caiu"/> nada mais muda.</summary>
    public bool Conectou()
    {
        _conectou = true;
        if (Fase != FaseDoSinal.SemSinal) return false;
        UltimaQueda = _agora() - _desde;
        Fase = FaseDoSinal.Normal;
        return true;
    }

    /// <summary>
    /// O túnel está Lost. Só vale depois de ter estado Connected uma vez (antes disso a ligação ainda está nascendo e quem trata é a espera de 25 s da entrada).
    /// Devolve true se começou um período sem sinal.
    /// </summary>
    public bool Perdeu()
    {
        if (!_conectou || Fase != FaseDoSinal.Normal) return false;
        _desde = _agora();
        Fase = FaseDoSinal.SemSinal;
        return true;
    }

    /// <summary>Chamar a cada segundo enquanto estiver sem sinal. Devolve true quando o limite passou e o anfitrião está dado por caído.</summary>
    public bool Passou()
    {
        if (Fase != FaseDoSinal.SemSinal || _agora() - _desde < _limite) return false;
        Fase = FaseDoSinal.Caiu;
        return true;
    }
}
