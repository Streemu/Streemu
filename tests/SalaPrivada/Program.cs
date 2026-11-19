using Streemu.Net;

static class Program
{
    static readonly HashSet<string> Online = new() { "a", "b", "c", "d", "e", "f", "g" };

    static int Main(string[] args)
    {
        var casos = new (string Nome, Action Corpo)[]
        {
            ("codigo-por-convidado", CodigoPorConvidado),
            ("sexto-convidado-sala-cheia", SalaCheia),
            ("offline-falha-com-motivo", OfflineFalha),
            ("remover-tira-da-lista", RemoverTira),
            ("encerrar-limpa-tudo", EncerrarLimpa),
            ("id-forjado-sem-codigo", IdForjado),
            ("sem-privada-ninguem-entra", SemPrivada),
            ("convite-traz-so-o-necessario", ConviteMinimo),
        };
        var linhas = new List<string>();
        foreach (var (nome, corpo) in casos)
        {
            try { corpo(); linhas.Add($"OK    privada-{nome}"); }
            catch (Exception e) { linhas.Add($"FALHA privada-{nome}: {e.Message}"); }
        }
        linhas.ForEach(Console.WriteLine);
        if (args.Length == 1) File.WriteAllLines(args[0], linhas);
        return linhas.Any(l => l.StartsWith("FALHA")) ? 1 : 0;
    }

    static SalaPrivada Nova() { var s = new SalaPrivada(Online.Contains); s.Iniciar("stream1"); return s; }

    static void Exigir(bool condicao, string erro) { if (!condicao) throw new Exception(erro); }

    static void CodigoPorConvidado()
    {
        var s = Nova();
        var b = s.Convidar("b", "B").Convidado!; var c = s.Convidar("c", "C").Convidado!;
        Exigir(b.Codigo != c.Codigo && b.Codigo.Length == 32, $"códigos iguais ou curtos: {b.Codigo} / {c.Codigo}");
        Exigir(s.Permite("b", b.Codigo) && s.Permite("c", c.Codigo), "convidado com o próprio código deveria entrar");
        Exigir(!s.Permite("b", c.Codigo), "o código de C não pode valer para B");
        Exigir(s.Convidar("b", "B").Convidado!.Codigo == b.Codigo, "convidar de novo deveria devolver o mesmo código");
    }

    static void SalaCheia()
    {
        var s = Nova();
        foreach (var id in new[] { "b", "c", "d", "e", "f" }) Exigir(s.Convidar(id, id).Ok, $"{id} deveria caber (5 convidados + anfitrião = {SalaPrivada.Limite})");
        var sexto = s.Convidar("g", "G");
        Exigir(!sexto.Ok && sexto.Motivo!.Contains("cheia"), $"o sexto convidado deveria receber 'cheia', veio: {sexto.Motivo}");
    }

    static void OfflineFalha()
    {
        var r = Nova().Convidar("fantasma", "Fantasma");
        Exigir(!r.Ok && r.Motivo!.Contains("Fantasma") && r.Motivo.Contains("online"), $"motivo sem apelido ou 'online': {r.Motivo}");
    }

    static void RemoverTira()
    {
        var s = Nova(); var b = s.Convidar("b", "B").Convidado!;
        Exigir(s.EstaNaLista("b"), "convidado deveria estar na lista");
        Exigir(s.Remover("b") && !s.Permite("b", b.Codigo) && !s.EstaNaLista("b"), "removido não pode mais entrar nem estar na lista");
        Exigir(!s.Remover("b"), "remover quem não está na lista deveria devolver falso");
        Exigir(s.Convidar("b", "B").Convidado!.Codigo != b.Codigo, "convidar de novo depois de remover precisa gerar código novo");
    }

    static void EncerrarLimpa()
    {
        var s = Nova(); var b = s.Convidar("b", "B").Convidado!;
        s.Encerrar();
        Exigir(!s.Ligada && s.Convidados.Count == 0 && !s.Permite("b", b.Codigo), "depois de encerrar nada pode valer");
    }

    static void IdForjado()
    {
        var s = Nova(); s.Convidar("b", "B");
        Exigir(!s.Permite("b", null) && !s.Permite("b", "") && !s.Permite("b", "chute") && !s.Permite("x", "chute"), "id certo sem o código certo (ou fora da lista) deve ser recusado");
    }

    static void SemPrivada()
    {
        var s = new SalaPrivada(Online.Contains);
        Exigir(!s.Permite("b", "qualquer") && !s.Convidar("b", "B").Ok, "sem Iniciar, ninguém é convidado nem permitido");
    }

    static void ConviteMinimo()
    {
        var s = Nova(); var b = s.Convidar("b", "B").Convidado!;
        var chaves = string.Join(",", s.MensagemConvite(b, "Ana").Select(p => p.Key).Order());
        Exigir(chaves == "anfitriao,codigo_convite,sala,tipo", $"campos do convite: {chaves}");
    }
}
