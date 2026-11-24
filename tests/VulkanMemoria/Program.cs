using System.Text.Json;
using Streemu.Media;
using Streemu.Telemetria;

static class Program
{
    const string Rx7800 = "AMD Radeon RX 7800 XT (driver 32.0.32029.2010)";
    const string Rx7800Novo = "AMD Radeon RX 7800 XT (driver 32.0.32030.1)";
    const string Rx7600 = "AMD Radeon RX 7600 (driver 32.0.32029.2010)";
    const string Igpu = "AMD Radeon(TM) Graphics (driver 32.0.12011.1036)";
    static readonly DateTimeOffset Hoje = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    static int Main(string[] args)
    {
        var casos = new (string Nome, Action Corpo)[]
        {
            ("sem-registro-nao-evita", SemRegistro),
            ("uma-falha-nao-evita", UmaFalha),
            ("duas-falhas-evitam", DuasFalhas),
            ("driver-diferente-zera", DriverDiferente),
            ("placa-diferente-zera", PlacaDiferente),
            ("sucesso-apaga", SucessoApaga),
            ("reteste-depois-de-30-dias-libera", RetesteLibera),
            ("antes-do-reteste-ainda-evita", AntesDoReteste),
            ("falha-depois-do-reteste-volta-a-evitar", FalhaDepoisDoReteste),
            ("falha-ja-evitando-nao-prolonga", FalhaJaEvitando),
            ("chave-ordenada-e-sem-placa-desconhecida", ChaveDasPlacas),
            ("serializacao-ida-e-volta", Serializacao),
            ("assinatura-da-decodificacao-evita-na-primeira", AssinaturaEvitaNaPrimeira),
            ("chave-ignora-placas-virtuais", ChaveSemVirtuais),
            ("decisao-previa-e-live-juntas-contam-uma-vez", DecisaoContaUmaVez),
            ("decisao-segunda-falha-depois-dos-20-min-evita", DecisaoSegundaFalha),
            ("decisao-janela-nao-abriu-so-abre-a-janela", DecisaoJanelaNaoAbriu),
            ("decisao-assinatura-evita-na-primeira", DecisaoAssinatura),
            ("decisao-registro-igual-nao-salva", DecisaoRegistroIgual),
            ("decisao-vram-cheia-sem-assinatura-pede-duas", DecisaoVramCheia),
            ("decisao-placa-reiniciou-nao-vai-para-o-disco", DecisaoPlacaReiniciou),
        };
        var linhas = new List<string>();
        foreach (var (nome, corpo) in casos)
        {
            try { corpo(); linhas.Add($"OK    vulkan-memoria-{nome}"); }
            catch (Exception e) { linhas.Add($"FALHA vulkan-memoria-{nome}: {e.Message}"); }
        }
        linhas.ForEach(Console.WriteLine);
        if (args.Length == 1) File.WriteAllLines(args[0], linhas);
        return linhas.Any(l => l.StartsWith("FALHA")) ? 1 : 0;
    }

    static void Exigir(bool condicao, string erro) { if (!condicao) throw new Exception(erro); }

    static VulkanBloqueio? Evitando(string placas, DateTimeOffset desde)
        => VulkanMemoria.RegistrarFalha(VulkanMemoria.RegistrarFalha(null, placas, desde), placas, desde);

    static void SemRegistro()
        => Exigir(!VulkanMemoria.Evitar(null, Rx7800, Hoje), "sem registro não pode evitar o Vulkan");

    static void UmaFalha()
    {
        var r = VulkanMemoria.RegistrarFalha(null, Rx7800, Hoje);
        Exigir(r is { Falhas: 1, Evitar: false }, "a primeira falha conta 1 e ainda não evita (pode ser VRAM cheia de verdade)");
        Exigir(!VulkanMemoria.Evitar(r, Rx7800, Hoje), "uma falha não evita");
    }

    static void DuasFalhas()
    {
        var r = VulkanMemoria.RegistrarFalha(VulkanMemoria.RegistrarFalha(null, Rx7800, Hoje), Rx7800, Hoje.AddMinutes(30));
        Exigir(r is { Falhas: 2, Evitar: true }, "duas falhas passam a evitar");
        Exigir(r!.DesdeUnixMs == Hoje.AddMinutes(30).ToUnixTimeMilliseconds(), "grava o instante em que passou a evitar");
        Exigir(VulkanMemoria.Evitar(r, Rx7800, Hoje.AddHours(2)), "evita nas lives seguintes");
        Exigir(VulkanMemoria.FalhasParaEvitar == 2, "a regra é de duas falhas");
    }

    static void DriverDiferente()
    {
        var r = Evitando(Rx7800, Hoje);
        Exigir(!VulkanMemoria.Evitar(r, Rx7800Novo, Hoje.AddDays(1)), "driver novo volta a testar o Vulkan");
        var depois = VulkanMemoria.RegistrarFalha(r, Rx7800Novo, Hoje.AddDays(1));
        Exigir(depois is { Falhas: 1, Evitar: false } && depois.Placas == Rx7800Novo, "falha com outro driver zera e começa em 1");
    }

    static void PlacaDiferente()
    {
        string duas = VulkanMemoria.Chave(new[] { Rx7800, Igpu });
        var r = Evitando(duas, Hoje);
        Exigir(!VulkanMemoria.Evitar(r, VulkanMemoria.Chave(new[] { Rx7600, Igpu }), Hoje), "outra placa volta a testar o Vulkan");
        Exigir(!VulkanMemoria.Evitar(r, VulkanMemoria.Chave(new[] { Igpu }), Hoje), "placa tirada volta a testar o Vulkan");
        var depois = VulkanMemoria.RegistrarFalha(r, VulkanMemoria.Chave(new[] { Rx7600, Igpu }), Hoje);
        Exigir(depois is { Falhas: 1, Evitar: false }, "falha em outra placa zera e começa em 1");
    }

    static void SucessoApaga()
    {
        var r = VulkanMemoria.RegistrarFalha(null, Rx7800, Hoje);
        Exigir(VulkanMemoria.RegistrarSucesso(r, Rx7800) == null, "o Vulkan funcionou: apaga a falha contada");
        Exigir(VulkanMemoria.RegistrarSucesso(Evitando(Rx7800, Hoje), Rx7800) == null, "o Vulkan funcionou: apaga até o registro que evitava");
        Exigir(VulkanMemoria.RegistrarSucesso(null, Rx7800) == null, "sem registro continua sem registro");
        // depois do sucesso, uma falha isolada volta a contar do 1
        var seguinte = VulkanMemoria.RegistrarFalha(VulkanMemoria.RegistrarSucesso(r, Rx7800), Rx7800, Hoje);
        Exigir(seguinte is { Falhas: 1, Evitar: false }, "falha depois do sucesso conta do 1");
    }

    static void AntesDoReteste()
    {
        var r = Evitando(Rx7800, Hoje);
        Exigir(VulkanMemoria.Evitar(r, Rx7800, Hoje + VulkanMemoria.Reteste - TimeSpan.FromMinutes(1)), "um minuto antes dos 30 dias ainda evita");
    }

    static void RetesteLibera()
    {
        var r = Evitando(Rx7800, Hoje);
        Exigir(!VulkanMemoria.Evitar(r, Rx7800, Hoje + VulkanMemoria.Reteste), "aos 30 dias o app testa o Vulkan de novo");
        Exigir(!VulkanMemoria.Evitar(r, Rx7800, Hoje.AddDays(90)), "depois dos 30 dias continua liberado");
        Exigir(VulkanMemoria.Reteste == TimeSpan.FromDays(30), "o reteste é de 30 dias");
    }

    static void FalhaDepoisDoReteste()
    {
        var r = Evitando(Rx7800, Hoje);
        var reteste = Hoje.AddDays(31);
        var depois = VulkanMemoria.RegistrarFalha(r, Rx7800, reteste);
        Exigir(depois is { Evitar: true } && VulkanMemoria.Evitar(depois, Rx7800, reteste.AddDays(1)), "falhou no reteste: volta a evitar sem precisar de duas");
        Exigir(depois!.DesdeUnixMs == reteste.ToUnixTimeMilliseconds(), "mais 30 dias contados da falha do reteste");
        Exigir(!VulkanMemoria.Evitar(depois, Rx7800, reteste.AddDays(31)), "e libera de novo no reteste seguinte");
    }

    static void FalhaJaEvitando()
    {
        var r = Evitando(Rx7800, Hoje);
        var depois = VulkanMemoria.RegistrarFalha(r, Rx7800, Hoje.AddDays(10));
        Exigir(depois == r, "abertura que já estava em andamento e falhou depois não prolonga o prazo");
    }

    static void ChaveDasPlacas()
    {
        Exigir(VulkanMemoria.Chave(new[] { Rx7800, Igpu }) == VulkanMemoria.Chave(new[] { Igpu, Rx7800 }), "a ordem das placas não muda a chave");
        Exigir(VulkanMemoria.Chave(new[] { Rx7800 }) != VulkanMemoria.Chave(new[] { Rx7800Novo }), "o driver faz parte da chave");
        Exigir(VulkanMemoria.Chave(Array.Empty<string>()) == "", "sem placa, chave vazia");
        string erro = VulkanMemoria.Chave(new[] { "desconhecida: acesso negado" });
        Exigir(erro == "", "leitura do WMI que falhou não vira chave");
        Exigir(!VulkanMemoria.Evitar(Evitando("", Hoje), "", Hoje) && VulkanMemoria.RegistrarFalha(null, "", Hoje) == null, "chave vazia não lembra nada");
    }

    static void Serializacao()
    {
        // o AppState grava com System.Text.Json (reflexão, WriteIndented); o registro tem que voltar igual
        var opcoes = new JsonSerializerOptions { WriteIndented = true };
        var original = Evitando(VulkanMemoria.Chave(new[] { Rx7800, Igpu }), Hoje);
        string json = JsonSerializer.Serialize(new Estado { VulkanBloqueio = original }, opcoes);
        var volta = JsonSerializer.Deserialize<Estado>(json)!.VulkanBloqueio;
        Exigir(volta == original && volta is { Evitar: true, Falhas: 2 }, "o registro não voltou igual do JSON");
        var vazio = JsonSerializer.Deserialize<Estado>(JsonSerializer.Serialize(new Estado(), opcoes))!;
        Exigir(vazio.VulkanBloqueio == null, "sem registro volta nulo");
        Exigir(JsonSerializer.Deserialize<Estado>("{\"outroCampo\":1}")!.VulkanBloqueio == null, "estado antigo, sem o campo, abre sem registro");
    }

    // ---- assinatura da decodificação (get_buffer() failed): determinística nas RDNA3, então a primeira falha já evita

    static void AssinaturaEvitaNaPrimeira()
    {
        var r = VulkanMemoria.RegistrarFalha(null, Rx7800, Hoje, assinaturaDaDecodificacao: true);
        Exigir(r is { Falhas: 1, Evitar: true }, "a assinatura da decodificação evita já na primeira falha");
        Exigir(VulkanMemoria.Evitar(r, Rx7800, Hoje.AddHours(1)), "e as lives seguintes evitam");
        var contada = VulkanMemoria.RegistrarFalha(VulkanMemoria.RegistrarFalha(null, Rx7800, Hoje), Rx7800, Hoje.AddMinutes(30), assinaturaDaDecodificacao: true);
        Exigir(contada is { Falhas: 2, Evitar: true }, "com uma falha antes, a assinatura soma e evita");
    }

    // ---- chave sem adaptadores virtuais

    static void ChaveSemVirtuais()
    {
        const string parsec = "Parsec Virtual Display Adapter (driver 0.45.0.0)";
        const string basico = "Microsoft Basic Display Adapter (driver 10.0.26100.1)";
        string sem = VulkanMemoria.Chave(new[] { Rx7800 });
        Exigir(VulkanMemoria.Chave(new[] { Rx7800, parsec }) == sem, "o Parsec entrando não muda a chave");
        Exigir(VulkanMemoria.Chave(new[] { parsec, Rx7800, basico }) == sem, "o Parsec e o adaptador básico do Windows não mudam a chave");
        foreach (var adaptador in new[] { "Remote Display Adapter", "spacedesk Graphics Adapter", "Citrix Indirect Display Adapter", "IddSampleDriver Device", "Virtual Desktop Monitor", "Microsoft Basic Render Driver", "PARSEC DISPLAY" })
            Exigir(VulkanMemoria.Chave(new[] { Rx7800, adaptador + " (driver 1.0)" }) == sem, $"{adaptador} não entra na chave");
        Exigir(VulkanMemoria.Chave(new[] { parsec }) == "", "só adaptador virtual: nada é lembrado");
        Exigir(VulkanMemoria.Chave(new[] { parsec, basico }) == "", "só adaptadores virtuais: chave vazia");
        // o Parsec entra e sai sem zerar o que já foi lembrado
        var r = Evitando(VulkanMemoria.Chave(new[] { Rx7800, Igpu }), Hoje);
        Exigir(VulkanMemoria.Evitar(r, VulkanMemoria.Chave(new[] { Igpu, parsec, Rx7800 }), Hoje.AddDays(1)), "com o Parsec ligado o registro continua valendo");
        var depois = VulkanMemoria.RegistrarFalha(r, VulkanMemoria.Chave(new[] { Rx7800, Igpu, parsec }), Hoje.AddDays(1));
        Exigir(depois == r, "o Parsec entrando não zera nem conta falha a mais num registro que já evita");
        var uma = VulkanMemoria.RegistrarFalha(null, VulkanMemoria.Chave(new[] { Rx7800, parsec }), Hoje);
        var duas = VulkanMemoria.RegistrarFalha(uma, VulkanMemoria.Chave(new[] { Rx7800 }), Hoje.AddMinutes(30));
        Exigir(duas is { Falhas: 2, Evitar: true }, "a primeira falha com o Parsec e a segunda sem ele são a mesma placa");
    }

    // ---- a decisão de uma falha (a ligação do MainForm): janela de 20 min, registro, salvar

    const long Min = 60_000;
    static DecisaoDeFalha Decidir(long janela, long agora, MotivoMidia motivo, bool decodificacao, VulkanBloqueio? registro, DateTimeOffset? quando = null)
        => VulkanMemoria.DecidirFalha(janela, agora, motivo, decodificacao, registro, Rx7800, quando ?? Hoje.AddMilliseconds(agora));

    static void DecisaoContaUmaVez()
    {
        // a prévia da própria live e uma live de amigo falham juntas, na mesma abertura: só a primeira conta
        long t = 1_000_000;
        var a = Decidir(0, t, MotivoMidia.SemMemoria, false, null);
        Exigir(a is { Registrou: true, Salvar: true, PassouAEvitar: false } && a.Registro is { Falhas: 1, Evitar: false }, "a primeira falha conta 1 e salva");
        Exigir(a.JanelaAteTicks == t + 20 * Min && VulkanMemoria.JanelaMs == 20 * Min, "abre a janela de 20 min");
        var b = Decidir(a.JanelaAteTicks, t + 300, MotivoMidia.SemMemoria, false, a.Registro);
        Exigir(b is { Registrou: false, Salvar: false, PassouAEvitar: false } && b.Registro == a.Registro, "a segunda falha na mesma abertura não conta nem salva");
        Exigir(b.JanelaAteTicks == t + 300 + 20 * Min, "mas a janela de 20 min continua valendo");
    }

    static void DecisaoSegundaFalha()
    {
        long t = 1_000_000;
        var a = Decidir(0, t, MotivoMidia.VulkanFalhou, false, null);
        var antes = Decidir(a.JanelaAteTicks, t + 19 * Min, MotivoMidia.VulkanFalhou, false, a.Registro);
        Exigir(!antes.Registrou && antes.Registro is { Falhas: 1 }, "aos 19 min ainda é a mesma abertura");
        var b = Decidir(a.JanelaAteTicks, t + 20 * Min, MotivoMidia.VulkanFalhou, false, a.Registro);
        Exigir(b is { Registrou: true, Salvar: true, PassouAEvitar: true } && b.Registro is { Falhas: 2, Evitar: true }, "a falha depois dos 20 min é a segunda: passa a evitar e salva");
    }

    static void DecisaoJanelaNaoAbriu()
    {
        long t = 1_000_000;
        var a = Decidir(0, t, MotivoMidia.JanelaNaoAbriu, false, null);
        Exigir(a is { Registrou: false, Salvar: false, PassouAEvitar: false } && a.Registro == null && a.JanelaAteTicks == t + 20 * Min, "janela que não abriu só abre a janela de 20 min");
        foreach (var m in new[] { MotivoMidia.SaiuCedo, MotivoMidia.Outro, MotivoMidia.D3d11NaoAbriu })
            Exigir(!Decidir(0, t, m, true, null).Registrou, $"{m} não entra na conta do disco, nem com a assinatura");
    }

    static void DecisaoAssinatura()
    {
        long t = 1_000_000;
        var a = Decidir(0, t, MotivoMidia.SemMemoria, decodificacao: true, null);
        Exigir(a is { Registrou: true, Salvar: true, PassouAEvitar: true } && a.Registro is { Falhas: 1, Evitar: true }, "a assinatura da decodificação evita na primeira falha");
        // a assinatura só vale com falta de memória
        var b = Decidir(0, t, MotivoMidia.VulkanFalhou, decodificacao: true, null);
        Exigir(b is { PassouAEvitar: false } && b.Registro is { Falhas: 1, Evitar: false }, "outro motivo continua pedindo duas falhas");
    }

    static void DecisaoVramCheia()
    {
        long t = 1_000_000;
        var a = Decidir(0, t, MotivoMidia.SemMemoria, decodificacao: false, null);
        Exigir(a is { PassouAEvitar: false } && a.Registro is { Falhas: 1, Evitar: false }, "VRAM cheia de verdade (sem get_buffer) não evita na primeira");
    }

    static void DecisaoRegistroIgual()
    {
        long t = 1_000_000;
        var evitando = Evitando(Rx7800, Hoje);
        var a = Decidir(0, t, MotivoMidia.SemMemoria, true, evitando, Hoje.AddDays(1));
        Exigir(a is { Registrou: true, Salvar: false, PassouAEvitar: false } && a.Registro == evitando, "já evitando e dentro do prazo: o registro volta igual e não é salvo de novo");
        var semPlaca = VulkanMemoria.DecidirFalha(0, t, MotivoMidia.SemMemoria, true, null, "", Hoje);
        Exigir(semPlaca is { Registrou: true, Salvar: false } && semPlaca.Registro == null, "sem chave de placa nada é lembrado nem salvo");
    }

    // reset do driver (VK_ERROR_DEVICE_LOST no ffplay, 887a0026 na captura, LIV-140 em 2026-11-23): a placa volta sozinha; as lives dos próximos
    // 2 min abrem pelo Direct3D 11 (o reset é passageiro), mas nada vai para o estado.json, nem somado a uma falha de verdade já lembrada
    static void DecisaoPlacaReiniciou()
    {
        long t = 1_000_000;
        var a = Decidir(0, t, MotivoMidia.DispositivoRemovido, false, null);
        Exigir(a is { Registrou: false, Salvar: false, PassouAEvitar: false } && a.Registro == null && a.JanelaAteTicks == t + 2 * Min && VulkanMemoria.JanelaResetMs == 2 * Min,
            "reset do driver só abre a janela de 2 min");
        // um reset dentro da janela de 20 min de uma falha de verdade não a encurta
        var oom = Decidir(0, t, MotivoMidia.SemMemoria, false, null);
        var reset = Decidir(oom.JanelaAteTicks, t + 60_000, MotivoMidia.DispositivoRemovido, false, oom.Registro);
        Exigir(reset.JanelaAteTicks == oom.JanelaAteTicks && !reset.Registrou && !reset.Salvar, "o reset dentro dos 20 min da falha não encurta a janela nem grava");
        var umaFalha = VulkanMemoria.RegistrarFalha(null, Rx7800, Hoje);
        var b = Decidir(0, t, MotivoMidia.DispositivoRemovido, false, umaFalha);
        Exigir(!b.Registrou && b.Registro == umaFalha, "reset do driver não vira a segunda falha de quem já tinha uma");
    }

    sealed class Estado { public VulkanBloqueio? VulkanBloqueio { get; set; } }
}
