using Streemu.Avisos;

static class Program
{
    static DateTime _hora = new(2026, 11, 12, 14, 0, 0);

    static int Main(string[] args)
    {
        var casos = new (string Nome, Action Corpo)[]
        {
            ("prazo-acaba-sozinho", PrazoAcabaSozinho),
            ("ate-ligar-de-novo", AteLigarDeNovo),
            ("ligar-desfaz", LigarDesfaz),
            ("estado-volta-ao-reabrir", EstadoVoltaAoReabrir),
            ("prazo-vencido-guardado-nao-vale", PrazoVencido),
            ("descricao-do-sino", Descricao),
            ("aviso-novo-so-pergunta-ao-porteiro", AvisoNovo),
        };
        var linhas = new List<string>();
        foreach (var (nome, corpo) in casos)
        {
            _hora = new DateTime(2026, 11, 12, 14, 0, 0);
            try { corpo(); linhas.Add($"OK    silencio-{nome}"); }
            catch (Exception e) { linhas.Add($"FALHA silencio-{nome}: {e.Message}"); }
        }
        linhas.ForEach(Console.WriteLine);
        if (args.Length == 1) File.WriteAllLines(args[0], linhas);
        return linhas.Any(l => l.StartsWith("FALHA")) ? 1 : 0;
    }

    static Silencio Novo(bool indefinido = false, DateTime? ate = null) => new(indefinido, ate, () => _hora);
    static void Exigir(bool condicao, string erro) { if (!condicao) throw new Exception(erro); }

    static void PrazoAcabaSozinho()
    {
        var s = Novo(); s.Silenciar(TimeSpan.FromMinutes(15));
        Exigir(s.Ativo && !s.PodeAvisar, "recém silenciado deveria estar ativo");
        _hora = _hora.AddMinutes(14);
        Exigir(s.Ativo, "aos 14 min ainda deveria estar ativo");
        _hora = _hora.AddMinutes(2);
        Exigir(!s.Ativo && s.PodeAvisar && s.Ate == null, "aos 16 min o sino deveria ter voltado sozinho");
    }

    static void AteLigarDeNovo()
    {
        var s = Novo(); s.Silenciar(null);
        _hora = _hora.AddDays(30);
        Exigir(s.Ativo && s.Indefinido && s.Ate == null, "'até ligar de novo' não pode vencer");
    }

    static void LigarDesfaz()
    {
        int avisos = 0; var s = Novo(); s.Mudou += () => avisos++;
        s.Silenciar(TimeSpan.FromHours(1)); s.Ligar();
        Exigir(!s.Ativo && avisos == 2, $"ligar deveria desfazer e avisar quem exibe (avisos={avisos})");
    }

    static void EstadoVoltaAoReabrir()
    {
        var a = Novo(); a.Silenciar(TimeSpan.FromHours(3));
        var b = Novo(a.Indefinido, a.Ate);
        Exigir(b.Ativo && b.Ate == a.Ate, "o app reaberto com o estado guardado deveria continuar silenciado");
        var c = Novo(); c.Silenciar(null);
        Exigir(Novo(c.Indefinido, c.Ate).Ativo, "'até ligar de novo' deveria voltar ao reabrir");
    }

    static void PrazoVencido() => Exigir(!Novo(false, _hora.AddMinutes(-1)).Ativo, "um prazo guardado que já passou não pode silenciar");

    static void Descricao()
    {
        var s = Novo(); s.Silenciar(TimeSpan.FromMinutes(90));
        Exigir(s.Descricao() == "até 15:30", $"mesmo dia: '{s.Descricao()}'");
        s.Silenciar(TimeSpan.FromHours(24));
        Exigir(s.Descricao() == "até 13/11 14:00", $"outro dia: '{s.Descricao()}'");
        s.Silenciar(null);
        Exigir(s.Descricao() == "até você ligar de novo", $"indefinido: '{s.Descricao()}'");
        s.Ligar();
        Exigir(s.Descricao() == "", "ligado: sem texto");
    }

    // Um aviso escrito depois só precisa perguntar PodeAvisar: não conhece o sino nem o prazo.
    static void AvisoNovo()
    {
        var s = Novo(); var mostrados = new List<string>();
        void AvisoDeAmanha(string texto) { if (s.PodeAvisar) mostrados.Add(texto); }
        AvisoDeAmanha("ligado"); s.Silenciar(TimeSpan.FromHours(1)); AvisoDeAmanha("silenciado");
        Exigir(mostrados.SequenceEqual(new[] { "ligado" }), $"mostrados: {string.Join(",", mostrados)}");
    }
}
