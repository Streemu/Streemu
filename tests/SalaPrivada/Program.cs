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
            ("declara-ao-ligar-e-ao-desligar", DeclaraLigarDesligar),
            ("declaracao-que-falha-nao-derruba-a-privada", DeclaracaoQueFalha),
            ("mensagens-para-a-sala", MensagensParaASala),
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

    // Spec 029, emenda A (17.3.1): o servidor da sala passa a saber da privada, para a moderação; o grupo continua sem saber.
    static void DeclaraLigarDesligar()
    {
        var avisos = new List<string?>();
        var s = new SalaPrivada(Online.Contains, declarar: avisos.Add);
        Exigir(avisos.Count == 0, "criar a privada não declara nada");
        s.Iniciar("stream1");
        Exigir(avisos.Count == 1 && avisos[0] == "stream1" && s.StreamId == "stream1", $"ao ligar a sala deveria receber o id da stream: [{string.Join(",", avisos)}]");
        s.Encerrar();
        Exigir(avisos.Count == 2 && avisos[1] == null && s.StreamId == null, "ao desligar a sala deveria receber id nulo");
        s.Encerrar();
        Exigir(avisos.Count == 2, "desligar de novo não declara de novo");
        s.Iniciar("stream2"); s.Iniciar("stream3");
        Exigir(avisos.Skip(2).SequenceEqual(new[] { "stream2", "stream3" }), "ligar outra vez declara a stream nova");
    }

    static void DeclaracaoQueFalha()
    {
        var s = new SalaPrivada(Online.Contains, declarar: _ => throw new InvalidOperationException("sala fora"));
        s.Iniciar("stream1");
        var b = s.Convidar("b", "B");
        Exigir(s.Ligada && b.Ok, "se avisar a sala falhar, a privada continua ligada e convidando");
        s.Encerrar();
        Exigir(!s.Ligada, "e desliga normalmente");
    }

    static void MensagensParaASala()
    {
        var ligar = SalaPrivada.MensagemDeclaracao("stream1");
        Exigir(ligar["t"]!.GetValue<string>() == "privada" && ligar["id"]!.GetValue<string>() == "stream1" && ligar.Count == 2, $"declaração: {ligar.ToJsonString()}");
        var desligar = SalaPrivada.MensagemDeclaracao(null);
        Exigir(desligar["t"]!.GetValue<string>() == "privada" && desligar.ContainsKey("id") && desligar["id"] == null && desligar.Count == 2, $"declaração nula: {desligar.ToJsonString()}");
        var assistindo = SalaPrivada.MensagemAssistindo("c-aaaaaaaaaaaa", "stream1");
        Exigir(string.Join(",", assistindo.Select(p => p.Key).Order()) == "alvo,id,t" && assistindo["t"]!.GetValue<string>() == "assistindo_privada"
            && assistindo["alvo"]!.GetValue<string>() == "c-aaaaaaaaaaaa" && assistindo["id"]!.GetValue<string>() == "stream1", $"assistindo_privada: {assistindo.ToJsonString()}");
        var aceite = SalaPrivada.MensagemAceite("stream1");
        Exigir(aceite["tipo"]!.GetValue<string>() == "convite_resposta" && aceite["sala"]!.GetValue<string>() == "stream1" && aceite["aceito"]!.GetValue<bool>(), $"aceite: {aceite.ToJsonString()}");
        var recusa = SalaPrivada.MensagemRecusa("stream1");
        Exigir(!recusa["aceito"]!.GetValue<bool>(), "a recusa segue com aceito falso");
    }
}
