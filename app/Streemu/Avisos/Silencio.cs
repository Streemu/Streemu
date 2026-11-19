namespace Streemu.Avisos;

/// <summary>Uma duração do menu do sino. <c>Duracao</c> nula = "até ligar de novo".</summary>
public sealed record PrazoSilencio(string Texto, TimeSpan? Duracao);

/// <summary>
/// O interruptor das notificações do app (spec 012, parte B): um só lugar onde todo aviso (balão da bandeja, som, faixa de atualização)
/// pergunta se pode aparecer; um aviso novo que passe por aqui já nasce silenciável. Não cala o que fica na tela (contadores, cards, o próprio sino).
/// O estado (<see cref="Indefinido"/> e o fim do prazo) é entregue a quem guarda; ao reabrir o app, quem criou devolve os mesmos valores no construtor.
/// Uso: <c>var s = new Silencio(false, null); s.Silenciar(TimeSpan.FromHours(1)); if (s.PodeAvisar) MostrarBalao();</c>
/// </summary>
public sealed class Silencio
{
    /// <summary>Os prazos do menu, na ordem pedida pelo grupo (como no Discord).</summary>
    public static readonly IReadOnlyList<PrazoSilencio> Prazos = new[]
    {
        new PrazoSilencio("15 minutos", TimeSpan.FromMinutes(15)),
        new PrazoSilencio("1 hora", TimeSpan.FromHours(1)),
        new PrazoSilencio("3 horas", TimeSpan.FromHours(3)),
        new PrazoSilencio("8 horas", TimeSpan.FromHours(8)),
        new PrazoSilencio("24 horas", TimeSpan.FromHours(24)),
        new PrazoSilencio("Até ligar de novo", null),
    };

    readonly Func<DateTime> _agora;
    DateTime? _ate;

    /// <param name="ate">Fim do prazo guardado; ignorado se já passou.</param>
    /// <param name="agora">Relógio (injetável para teste); o padrão é a hora local.</param>
    public Silencio(bool indefinido, DateTime? ate, Func<DateTime>? agora = null)
    {
        _agora = agora ?? (() => DateTime.Now);
        Indefinido = indefinido;
        _ate = ate;
    }

    /// <summary>Disparado quando o usuário liga ou silencia (não quando o prazo acaba sozinho: quem exibe confere <see cref="Ativo"/> a cada segundo).</summary>
    public event Action? Mudou;

    public bool Indefinido { get; private set; }
    public bool Ativo => Indefinido || (_ate is { } fim && _agora() < fim);
    public bool PodeAvisar => !Ativo;
    /// <summary>Quando o silêncio acaba; nulo se está ligado, ou se é "até ligar de novo".</summary>
    public DateTime? Ate => !Indefinido && Ativo ? _ate : null;

    public void Silenciar(TimeSpan? duracao)
    {
        Indefinido = duracao == null;
        _ate = duracao == null ? null : _agora() + duracao;
        Mudou?.Invoke();
    }

    public void Ligar()
    {
        Indefinido = false;
        _ate = null;
        Mudou?.Invoke();
    }

    /// <summary>Texto curto para a dica do sino: "até 15:30", "até 13/11 09:00" ou "até você ligar de novo".</summary>
    public string Descricao()
    {
        if (!Ativo) return "";
        if (Indefinido) return "até você ligar de novo";
        var fim = _ate!.Value;
        return fim.Date == _agora().Date ? $"até {fim:HH:mm}" : $"até {fim:dd/MM HH:mm}";
    }
}
