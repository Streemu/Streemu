using Streemu.Filme;

// Regras do estado periódico e do sinal do anfitrião no modo filme (spec 010, 7.5). Caso de campo de 2026-11-28.
// Cada caso imprime "OK    filme-sinal-nome" ou "FALHA filme-sinal-nome : motivo". Sem janela, sem rede, sem mpv.

int falhas = 0;
void Caso(string nome, Action f)
{
    try { f(); Console.WriteLine("OK    filme-sinal-" + nome); }
    catch (Exception e) { falhas++; Console.WriteLine($"FALHA filme-sinal-{nome} : {e.Message}"); }
}
void Exige(bool ok, string msg) { if (!ok) throw new Exception(msg); }

var hora = new DateTime(2026, 11, 28, 20, 0, 0, DateTimeKind.Utc);
SinalDoAnfitriao Novo() => new(() => hora);

// ------------------------------------------------------------------ estado periódico: o espectador

Caso("estado-igual-nao-reaplica", () =>
{
    // o espectador está no seq 5: a repetição de 3 em 3 s traz o 5 de novo e não pode mexer em nada
    for (int repeticao = 0; repeticao < 100; repeticao++)
        Exige(!EstadoDoFilme.EhNovo(5, 5), $"o seq 5 repetido (#{repeticao}) não é comando novo");
});

Caso("estado-antigo-nao-aplica", () => Exige(!EstadoDoFilme.EhNovo(5, 4) && !EstadoDoFilme.EhNovo(5, 0), "seq menor é atraso de rede"));

Caso("seq-maior-recuperado-aplica", () =>
{
    // o espectador parou no 4 (o play, seq 5, se perdeu); o anfitrião segue repetindo o 5
    int visto = 4, aplicados = 0;
    foreach (int chegou in new[] { 5, 5, 5 })
        if (EstadoDoFilme.EhNovo(visto, chegou)) { visto = chegou; aplicados++; }
    Exige(visto == 5, $"deveria ter alcançado o 5 e está no {visto}");
    Exige(aplicados == 1, $"o 5 deveria ser aplicado uma vez só (foram {aplicados})");
});

Caso("seq-pulou-varios-aplica-o-ultimo", () => Exige(EstadoDoFilme.EhNovo(2, 9), "perdeu vários comandos: o estado atual vale"));

// ------------------------------------------------------------------ estado periódico: o anfitrião

Caso("anfitriao-so-reenvia-depois-do-primeiro-comando", () =>
{
    Exige(!EstadoDoFilme.DeveReenviar(true, 0, false), "no aguardo (seq 0) não há o que repetir");
    Exige(EstadoDoFilme.DeveReenviar(true, 1, false), "depois do primeiro comando repete");
    Exige(EstadoDoFilme.DeveReenviar(true, 4000, false), "um filme de 3h30 com muitos comandos continua repetindo");
});

Caso("espectador-nunca-reenvia", () => Exige(!EstadoDoFilme.DeveReenviar(false, 7, false), "quem assiste não manda estado"));

Caso("sessao-solta-nao-reenvia", () => Exige(!EstadoDoFilme.DeveReenviar(true, 7, true), "troca de episódio: a sessão velha cala, a nova repete o dela"));

Caso("reenvio-a-cada-3-s", () => Exige(EstadoDoFilme.ReenvioMs == 3000, "o intervalo combinado é 3 s"));

// ------------------------------------------------------------------ sinal do anfitrião (quem assiste)

Caso("sinal-comeca-normal-e-so-perde-depois-de-conectar", () =>
{
    var s = Novo();
    Exige(s.Fase == FaseDoSinal.Normal, "começa normal");
    Exige(!s.Perdeu() && s.Fase == FaseDoSinal.Normal, "a ligação ainda nascendo (nunca conectou) não conta como queda");
    s.Conectou();
    Exige(s.Perdeu() && s.Fase == FaseDoSinal.SemSinal, "depois de conectar, perder vira sem sinal");
});

Caso("sinal-oscilacao-curta-nao-derruba", () =>
{
    var s = Novo(); s.Conectou(); hora = hora.AddMinutes(3);
    Exige(s.Perdeu(), "perdeu");
    hora = hora.AddSeconds(12);
    Exige(!s.Passou(), "aos 12 s ainda é só aviso");
    Exige(s.Conectou(), "voltou antes de 30 s");
    Exige(s.Fase == FaseDoSinal.Normal, "volta ao normal");
    Exige(Math.Round(s.UltimaQueda.TotalSeconds) == 12, $"a queda durou 12 s e a medida diz {s.UltimaQueda.TotalSeconds}");
    hora = hora.AddMinutes(10);
    Exige(!s.Passou(), "normal não cai por si");
});

Caso("sinal-29-s-nao-cai-30-s-cai", () =>
{
    var s = Novo(); s.Conectou(); s.Perdeu();
    hora = hora.AddMilliseconds(29900);
    Exige(!s.Passou() && s.Fase == FaseDoSinal.SemSinal, "aos 29,9 s ainda não");
    hora = hora.AddMilliseconds(100);
    Exige(s.Passou() && s.Fase == FaseDoSinal.Caiu, "aos 30 s cai");
    Exige(!s.Passou(), "só avisa uma vez");
});

Caso("sinal-os-30-s-sao-seguidos", () =>
{
    var s = Novo(); s.Conectou();
    // dois apagões de 20 s com o sinal voltando no meio: nenhum chega a 30 s, e o contador recomeça a cada queda
    s.Perdeu(); hora = hora.AddSeconds(20); Exige(!s.Passou(), "20 s");
    s.Conectou(); hora = hora.AddSeconds(5);
    s.Perdeu(); hora = hora.AddSeconds(20); Exige(!s.Passou() && s.Fase == FaseDoSinal.SemSinal, "o segundo apagão de 20 s não soma com o primeiro");
    hora = hora.AddSeconds(10);
    Exige(s.Passou(), "30 s seguidos desse apagão");
});

Caso("sinal-depois-de-cair-nada-muda", () =>
{
    var s = Novo(); s.Conectou(); s.Perdeu(); hora = hora.AddSeconds(31); s.Passou();
    Exige(!s.Conectou() && s.Fase == FaseDoSinal.Caiu, "a ligação foi encerrada: um pacote atrasado não desfaz a queda");
    Exige(!s.Perdeu() && s.Fase == FaseDoSinal.Caiu, "perder de novo também não");
});

Caso("sinal-perdeu-duas-vezes-seguidas-nao-reinicia-o-tempo", () =>
{
    var s = Novo(); s.Conectou(); s.Perdeu(); hora = hora.AddSeconds(20);
    Exige(!s.Perdeu(), "Lost repetido não é período novo");
    hora = hora.AddSeconds(10);
    Exige(s.Passou(), "os 30 s contam desde a primeira vez");
});

Caso("sinal-tempo-sem-sinal", () =>
{
    var s = Novo(); s.Conectou();
    Exige(s.SemSinalHa == TimeSpan.Zero, "normal: zero");
    s.Perdeu(); hora = hora.AddSeconds(7);
    Exige(Math.Round(s.SemSinalHa.TotalSeconds) == 7, "sete segundos");
});

Caso("sinal-limite-padrao-30-s", () => Exige(SinalDoAnfitriao.Limite == TimeSpan.FromSeconds(30), "o limite combinado é 30 s"));

return falhas == 0 ? 0 : 1;
