using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Streemu.Telemetria;

// Teste da telemetria do caminho de mídia (Telemetria/CaminhoMidia.cs e ContextoMidia.cs): códigos fechados, normalização, classificação das
// linhas do ffmpeg, montagem e ligação dos eventos (tentativa, vídeo, falha, recuperação), teto por abertura e gravação pela SessaoLocal no modo
// normal e no anônimo. Sem janela, sem ffmpeg, sem rede, numa pasta temporária própria (nunca %LOCALAPPDATA%\Streemu).
// O caminho do player de ponta a ponta (Vulkan → Direct3D 11 → CPU) fica no tests/ViewerFallback, que roda o Viewer com players de mentira.
Console.OutputEncoding = new UTF8Encoding(false);
string tmp = Path.Combine(Path.GetTempPath(), "streemu-caminho-midia-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Environment.ProcessId);
Directory.CreateDirectory(tmp);
// antes de tocar em qualquer tipo de Streemu.Telemetria: Pasta.Raiz lê STREEMU_TEST_DIR uma vez só
Environment.SetEnvironmentVariable("STREEMU_TEST_DIR", tmp);
Environment.SetEnvironmentVariable("STREEMU_TELEMETRIA_URL", null);

int falhas = 0;
void Caso(string nome, Action corpo)
{
    try { corpo(); Console.WriteLine($"OK    {nome}"); }
    catch (Exception e) { falhas++; Console.WriteLine($"FALHA {nome}: {e.Message}"); }
    finally { CaminhoMidia.Ligar(null); CaminhoMidia.MaxComuns = 400; CaminhoMidia.MaxFalhas = 400; CaminhoMidia.Reiniciar(); }
}
void Exigir(bool ok, string msg) { if (!ok) throw new Exception(msg); }

var codigo = new Regex("^[a-z0-9_]{1,32}$");

// Coletor: o que chegaria à sessão da telemetria, na ordem.
List<(string Tipo, JsonObject Dados)> Coletar()
{
    var l = new List<(string, JsonObject)>();
    CaminhoMidia.Ligar((t, d) => { lock (l) l.Add((t, d)); });
    return l;
}
List<(string Tipo, JsonObject Dados)> Esvaziar(List<(string Tipo, JsonObject Dados)> l)
{
    Exigir(CaminhoMidia.Descarregar(TimeSpan.FromSeconds(15)), "a fila não esvaziou");
    lock (l) return l.ToList();
}
string S(JsonObject o, string k) => o[k]?.GetValue<string>() ?? "";

// 90/120 FPS (beta): Preset.Pick normaliza e limita a 1440p; 30/60 continuam como eram; o limite pela taxa do monitor
Caso("preset-fps-beta", () =>
{
    Exigir(Streemu.Media.Preset.Pick(1080, 60).Name == "1080p60" && Streemu.Media.Preset.Pick(720, 30).Name == "720p30" && Streemu.Media.Preset.Pick(0, 60).Name == "Fonte60", "30/60 mudaram");
    Exigir(Streemu.Media.Preset.Pick(1080, 45).Name == "1080p30" && Streemu.Media.Preset.Pick(1080, 75).Name == "1080p60", "normalização para baixo");
    Exigir(Streemu.Media.Preset.Pick(720, 90).Name == "720p90" && Streemu.Media.Preset.Pick(1440, 120).Name == "1440p120" && Streemu.Media.Preset.Pick(1080, 100).Name == "1080p90", "90/120");
    Exigir(Streemu.Media.Preset.Pick(2160, 120).Name == "1440p120" && Streemu.Media.Preset.Pick(0, 120).Height == 1440 && Streemu.Media.Preset.Pick(0, 90).Name == "Fonte90", "limite de 1440p");
    Exigir(Streemu.Media.Preset.Pick(2160, 60).Name == "2160p60" && Streemu.Media.Preset.Pick(0, 60).Height == 2160, "60 não é limitado a 1440p");
    Exigir(Streemu.Media.Preset.Pick(1080, 90).Beta && !Streemu.Media.Preset.Pick(1080, 60).Beta, "beta só acima de 60");
    Exigir(Streemu.Media.Preset.FpsForRefresh(120, 60) == 60 && Streemu.Media.Preset.FpsForRefresh(120, 59) == 60 && Streemu.Media.Preset.FpsForRefresh(120, 100) == 90
        && Streemu.Media.Preset.FpsForRefresh(120, 144) == 120 && Streemu.Media.Preset.FpsForRefresh(60, 30) == 30 && Streemu.Media.Preset.FpsForRefresh(90, 20) == 30
        && Streemu.Media.Preset.FpsForRefresh(120, null) == 120 && Streemu.Media.Preset.FpsForRefresh(30, 60) == 30, "limite pela taxa do monitor");
    Exigir(Streemu.Media.Preset.Pick(0, 120).WithFps(60).Name == "Fonte60" && Streemu.Media.Preset.Pick(1080, 90).WithFps(60).Name == "1080p60", "WithFps");
});

Caso("codigos-fechados", () =>
{
    // a lista que está no docs/TELEMETRIA.md ("Caminho de mídia"): mudou aqui, muda lá
    string etapas = "captura escala encoder envio decodificacao renderizacao";
    string motivos = "encoder_indisponivel driver_incompativel sem_memoria parametro_recusado textura_recusada dispositivo_removido captura_nao_abriu " +
                     "demorou saiu_com_erro nenhum_encoder processo_saiu caiu_repetidas captura_travada poucos_quadros saida_aos_trancos audio_video_afastados " +
                     "envio_nao_abre vulkan_falhou janela_nao_abriu saiu_cedo d3d11_indisponivel d3d11_nao_abriu d3d11_sem_hardware d3d11_nao_manteve outro";
    string gatilhos = "inicio troca_config reabertura recuperacao reabertura_janela outro";
    Exigir(string.Join(" ", Enum.GetValues<EtapaMidia>().Select(CaminhoMidia.Codigo)) == etapas, "etapas fora da lista documentada");
    Exigir(string.Join(" ", Enum.GetValues<MotivoMidia>().Select(CaminhoMidia.Codigo)) == motivos, "motivos fora da lista documentada");
    Exigir(string.Join(" ", Enum.GetValues<GatilhoMidia>().Select(CaminhoMidia.Codigo)) == gatilhos, "gatilhos fora da lista documentada");
    foreach (var c in (etapas + " " + motivos + " " + gatilhos).Split(' ')) Exigir(codigo.IsMatch(c), $"código inválido: {c}");
    Exigir(CaminhoMidia.Tipos.All(t => t.Length <= 32 && codigo.IsMatch(t)), "tipo de evento maior que os 32 caracteres que o índice guarda");
});

Caso("normalizacao", () =>
{
    Exigir(CaminhoMidia.Encoder("h264_nvenc") == "nvenc" && CaminhoMidia.Encoder("h264_amf") == "amf" && CaminhoMidia.Encoder("h264_qsv") == "qsv"
        && CaminhoMidia.Encoder("h264_mf") == "mf" && CaminhoMidia.Encoder("libx264") == "x264" && CaminhoMidia.Encoder("h264_vaapi") == "outro"
        && CaminhoMidia.Encoder(null) == "outro", "encoder");
    Exigir(CaminhoMidia.Codec("libx264") == "h264" && CaminhoMidia.Codec("h264_nvenc") == "h264" && CaminhoMidia.Codec("hevc_amf") == "hevc" && CaminhoMidia.Codec("x") == "outro", "codec");
    Exigir(CaminhoMidia.Captura("ddagrab") == "ddagrab" && CaminhoMidia.Captura("gfxcapture-janela") == "gfxcapture_janela"
        && CaminhoMidia.Captura("gfxcapture-monitor") == "gfxcapture_monitor" && CaminhoMidia.Captura("Cyberpunk 2077") == "outro", "captura");
    Exigir(CaminhoMidia.DaLista("vulkan", CaminhoMidia.Decodificadores) == "vulkan" && CaminhoMidia.DaLista("nvdec", CaminhoMidia.Decodificadores) == "outro", "decodificador");
    Exigir(CaminhoMidia.Resolucao(1920, 1080) == "1920x1080" && CaminhoMidia.Resolucao(-5, 99999) == "0x16384", "resolução");
    Exigir(CaminhoMidia.Curto("NVIDIA GeForce(R) RTX(TM)   5070") == "NVIDIA GeForce RTX 5070", "marcas e espaços");
    Exigir(CaminhoMidia.Curto("Intel® UHD Graphics 620™") == "Intel UHD Graphics 620", "símbolos de marca");
    string? caminho = CaminhoMidia.Curto(@"X:\Jogos\Teste\""jogo"";x");
    Exigir(caminho != null && !caminho.Contains('\\') && !caminho.Contains(':') && !caminho.Contains('"') && !caminho.Contains(';'), "separadores de caminho passaram");
    Exigir(CaminhoMidia.Curto(new string('a', 200))!.Length == 64 && CaminhoMidia.Curto("   ") == null, "limite de tamanho");
    Exigir(ContextoMidia.Preset("1080p60") == "1080p60" && ContextoMidia.Preset("1440p60 ultrawide") == "1440p60_ultrawide" && ContextoMidia.Preset("meu preset") == "outro", "preset");
});

Caso("classificacao-ffmpeg", () =>
{
    var casos = new (string Linha, bool Demorou, string Esperado)[]
    {
        ("[h264_nvenc @ 0000] OpenEncodeSessionEx failed: out of memory (10): (no details)", false, "encoder/sem_memoria"),
        ("[h264_nvenc @ 0000] No capable devices found", false, "encoder/encoder_indisponivel"),
        ("[h264_nvenc @ 0000] Driver does not support the required nvenc API version. Required: 13.0 Found: 12.1", false, "encoder/driver_incompativel"),
        ("[h264_amf @ 0000] DLL amfrt64.dll failed to open", false, "encoder/encoder_indisponivel"),
        ("[h264_qsv @ 0000] Error creating a MFX session: -9.", false, "encoder/encoder_indisponivel"),
        ("[h264_mf @ 0000] could not find any MFT for the given media type", false, "encoder/encoder_indisponivel"),
        ("[Parsed_gfxcapture_0 @ 0000] Failed to create capture item", false, "captura/captura_nao_abriu"),
        ("[Parsed_ddagrab_0 @ 0000] DuplicateOutput failed: 887a0004", false, "captura/captura_nao_abriu"),
        ("[Parsed_hwmap_1 @ 0000] Failed to map frame: -38.", false, "escala/textura_recusada"),
        ("Impossible to convert between the formats supported by the filter 'Parsed_hwdownload_1' and the filter 'auto_scale_0'", false, "escala/textura_recusada"),
        ("Error while opening encoder - maybe incorrect parameters such as bit_rate, rate, width or height", false, "encoder/parametro_recusado"),
        ("[h264_amf @ 0000] DXGI_ERROR_DEVICE_REMOVED", false, "encoder/dispositivo_removido"),
        ("", true, "encoder/demorou"),
        ("Conversion failed!", false, "encoder/saiu_com_erro"),
    };
    foreach (var (linha, demorou, esperado) in casos)
    {
        var (e, m) = CaminhoMidia.ClassificarFfmpeg(linha, demorou, EtapaMidia.Encoder, MotivoMidia.SaiuComErro);
        string veio = CaminhoMidia.Codigo(e) + "/" + CaminhoMidia.Codigo(m);
        Exigir(veio == esperado, $"\"{linha}\": esperava {esperado}, veio {veio}");
    }
    // o processo da captura que sai sem linha conhecida fica com o padrão de quem chama
    var (e2, m2) = CaminhoMidia.ClassificarFfmpeg(null, false, EtapaMidia.Captura, MotivoMidia.ProcessoSaiu);
    Exigir(e2 == EtapaMidia.Captura && m2 == MotivoMidia.ProcessoSaiu, "sem linha");
});

Caso("motivos-da-reabertura-batem-com-o-streamsender", () =>
{
    Exigir(CaminhoMidia.FalhaDaReabertura("o ffmpeg da captura saiu sozinho (código 1)") == (EtapaMidia.Captura, MotivoMidia.ProcessoSaiu), "saiu sozinho");
    Exigir(CaminhoMidia.FalhaDaReabertura("captura travada") == (EtapaMidia.Captura, MotivoMidia.CapturaTravada), "travada");
    Exigir(CaminhoMidia.FalhaDaReabertura("captura travada de novo") == (EtapaMidia.Captura, MotivoMidia.CapturaTravada), "travada de novo");
    Exigir(CaminhoMidia.FalhaDaReabertura("trocando para ddagrab") == (EtapaMidia.Captura, MotivoMidia.PoucosQuadros), "troca de método");
    Exigir(CaminhoMidia.FalhaDaReabertura("encoder aos trancos") == (EtapaMidia.Encoder, MotivoMidia.SaidaAosTrancos), "trancos");
    Exigir(CaminhoMidia.FalhaDaReabertura("tempos de áudio e vídeo da origem afastados por mais de 5 s") == (EtapaMidia.Captura, MotivoMidia.AudioVideoAfastados), "sincronia");
    Exigir(CaminhoMidia.FalhaDaReabertura("teste: reinício forçado") == null, "reinício de teste não é falha");
    // cada motivo que o StreamSender escreve hoje tem que ser reconhecido: texto mudou lá sem mudar aqui, o evento perderia a etapa
    string? raiz = AppContext.BaseDirectory;
    while (raiz != null && !File.Exists(Path.Combine(raiz, "app", "Streemu", "Media", "StreamSender.cs"))) raiz = Path.GetDirectoryName(raiz);
    Exigir(raiz != null, "não achei o StreamSender.cs");
    string fonte = File.ReadAllText(Path.Combine(raiz!, "app", "Streemu", "Media", "StreamSender.cs"));
    var textos = Regex.Matches(fonte, @"(?:_restartPending = |Schedule\(|RestartCapture\()\$?""([^""{]+)").Select(m => m.Groups[1].Value).Distinct().ToList();
    Exigir(textos.Count >= 6, $"esperava ao menos 6 motivos de reabertura no StreamSender, achei {textos.Count}");
    foreach (var t in textos) Exigir(CaminhoMidia.FalhaDaReabertura(t) != null, $"motivo de reabertura sem código: \"{t}\"");
});

Caso("tentativas-ligadas-pelo-id", () =>
{
    var l = Coletar();
    var midia = new CaminhoMidia.Tentativas(CaminhoMidia.Host);
    midia.NovaLive();
    var t1 = midia.Preparar(GatilhoMidia.Inicio);
    // o teste do encoder reprova um candidato antes de o pipeline abrir: fala em nome da tentativa preparada
    midia.PreparadaOuAtual!.Falhou(EtapaMidia.Encoder, MotivoMidia.EncoderIndisponivel, teste: true,
        caminho: () => new JsonObject { ["encoder"] = "amf", ["caminho"] = "amf_map" });
    var c1 = midia.Comecar(GatilhoMidia.Outro, () => new JsonObject { ["captura"] = "ddagrab", ["encoder"] = "nvenc", ["caminho"] = "gpu", ["resolucao"] = "1920x1080", ["fps"] = 60 });
    Exigir(ReferenceEquals(c1, t1) && c1.Numero == 1, "a tentativa que abriu não é a preparada");
    Thread.Sleep(30);
    c1.VideoOk();
    c1.VideoOk(); // o laço de 1 s chama a cada segundo: só o primeiro conta
    c1.Falhou(EtapaMidia.Captura, MotivoMidia.CapturaTravada, medidas: () => new JsonObject { ["quadros_novos_s"] = 1 });
    midia.Preparar(GatilhoMidia.Reabertura);
    var c2 = midia.Comecar(GatilhoMidia.Outro, () => new JsonObject { ["captura"] = "gfxcapture_janela", ["encoder"] = "nvenc", ["caminho"] = "gpu" });
    c2.VideoOk();
    var ev = Esvaziar(l);
    string seq = string.Join(" ", ev.Select(e => e.Tipo + (e.Dados["teste"]?.GetValue<bool>() == true ? "(teste)" : "")));
    Exigir(seq == "midia_falha(teste) midia_tentativa midia_video_ok midia_falha midia_tentativa midia_video_ok midia_recuperada", "sequência: " + seq);
    Exigir(ev.Take(4).All(e => S(e.Dados, "tentativa") == c1.Id) && ev.Skip(4).All(e => S(e.Dados, "tentativa") == c2.Id), "id da tentativa não liga os eventos");
    Exigir(S(ev[0].Dados, "encoder") == "amf" && S(ev[0].Dados, "motivo") == "encoder_indisponivel", "a falha do teste não leva o caminho do candidato");
    Exigir(S(ev[1].Dados, "gatilho") == "inicio" && ev[1].Dados["numero"]!.GetValue<int>() == 1, "gatilho/número da primeira");
    Exigir(ev[2].Dados["ms"]!.GetValue<long>() >= 25 && S(ev[2].Dados, "captura") == "ddagrab", "tempo até o vídeo ou resumo do caminho");
    var falha = ev[3].Dados;
    Exigir(S(falha, "etapa") == "captura" && S(falha, "motivo") == "captura_travada" && falha["fatal"]!.GetValue<bool>() == false
        && falha["duracao_util_ms"] != null && falha["quadros_novos_s"]!.GetValue<int>() == 1 && S(falha, "encoder") == "nvenc", "campos da falha");
    Exigir(S(ev[4].Dados, "gatilho") == "reabertura" && ev[4].Dados["numero"]!.GetValue<int>() == 2, "gatilho/número da reabertura");
    Exigir(S(ev[6].Dados, "tentativa_falha") == c1.Id && ev[6].Dados["ms"]!.GetValue<long>() >= 0 && S(ev[6].Dados, "captura") == "gfxcapture_janela", "recuperação");
    // nada de texto livre: todo valor de texto é código, id, resolução ou versão
    foreach (var (tipo, d) in ev)
        foreach (var (k, v) in d)
            if (v is JsonValue jv && jv.TryGetValue<string>(out var s))
                Exigir(codigo.IsMatch(s) || Regex.IsMatch(s, @"^\d+x\d+$"), $"{tipo}.{k} = \"{s}\" não é código");
});

Caso("fatal-envio-e-fim-nao-viram-recuperacao", () =>
{
    var l = Coletar();
    var midia = new CaminhoMidia.Tentativas(CaminhoMidia.Host);
    midia.NovaLive();
    var a = midia.Comecar(GatilhoMidia.Inicio, () => new JsonObject());
    a.VideoOk();
    a.Falhou(EtapaMidia.Envio, MotivoMidia.EnvioNaoAbre, interrompe: false); // um espectador sem envio não para o vídeo dos outros
    midia.Preparar(GatilhoMidia.Reabertura);
    midia.Comecar(GatilhoMidia.Outro, () => new JsonObject()).VideoOk();
    var b = midia.Atual!;
    b.Falhou(EtapaMidia.Captura, MotivoMidia.CaiuRepetidas, fatal: true);
    midia.Comecar(GatilhoMidia.Reabertura, () => new JsonObject()).VideoOk();
    midia.Atual!.Falhou(EtapaMidia.Captura, MotivoMidia.ProcessoSaiu);
    midia.Encerrar(); // a live acabou antes de voltar
    midia.NovaLive();
    var nova = midia.Comecar(GatilhoMidia.Inicio, () => new JsonObject());
    nova.VideoOk();
    var ev = Esvaziar(l);
    Exigir(ev.All(e => e.Tipo != CaminhoMidia.TipoRecuperada), "recuperação onde não houve: " + string.Join(" ", ev.Select(e => e.Tipo)));
    Exigir(nova.Numero == 1, "a live nova não recomeçou do 1");
    // a preparada que não abriu cede o número
    midia.Preparar(GatilhoMidia.TrocaConfig);
    var p2 = midia.Preparar(GatilhoMidia.Reabertura);
    Exigir(p2.Numero == 2, "duas preparadas seguidas pularam número");
});

Caso("teto-por-abertura", () =>
{
    var l = Coletar();
    CaminhoMidia.MaxComuns = 3;
    CaminhoMidia.MaxFalhas = 2;
    var midia = new CaminhoMidia.Tentativas(CaminhoMidia.Viewer);
    for (int i = 0; i < 5; i++) midia.Comecar(GatilhoMidia.Outro, () => new JsonObject()).Falhou(EtapaMidia.Renderizacao, MotivoMidia.VulkanFalhou);
    var ev = Esvaziar(l);
    Exigir(ev.Count(e => e.Tipo == CaminhoMidia.TipoTentativa) == 3 && ev.Count(e => e.Tipo == CaminhoMidia.TipoFalha) == 2, "o teto não segurou");
    Exigir(CaminhoMidia.Descartados == 5, $"descartados: {CaminhoMidia.Descartados}");
});

Caso("desligado-nao-registra", () =>
{
    CaminhoMidia.Ligar(null);
    var midia = new CaminhoMidia.Tentativas(CaminhoMidia.Host);
    midia.Comecar(GatilhoMidia.Inicio, () => throw new Exception("não devia montar nada")).Falhou(EtapaMidia.Encoder, MotivoMidia.Outro);
    Exigir(CaminhoMidia.Pendentes == 0 && CaminhoMidia.Descartados == 0, "sem destino, algo entrou na fila");
});

Caso("erro-ao-montar-nao-derruba", () =>
{
    var l = Coletar();
    var midia = new CaminhoMidia.Tentativas(CaminhoMidia.Host);
    midia.Comecar(GatilhoMidia.Inicio, () => throw new InvalidOperationException("DXGI falhou"));
    midia.Comecar(GatilhoMidia.Inicio, () => new JsonObject { ["encoder"] = "x264" });
    var ev = Esvaziar(l);
    Exigir(ev.Count == 1 && S(ev[0].Dados, "encoder") == "x264", "um evento que falha ao montar levou o seguinte junto");
});

Caso("placas", () =>
{
    var gpus = new[] { "Intel(R) UHD Graphics 630 (driver 31.0.101.2111)", "NVIDIA GeForce RTX 3060 Laptop GPU (driver 32.0.15.6094)",
                       "AMD Radeon(TM) Graphics (driver 31.0.21001.45002)", "AMD Radeon RX 6750 XT (driver 31.0.21001.45002)" };
    Exigir(ContextoMidia.LerGpu(gpus[1]) == ("NVIDIA GeForce RTX 3060 Laptop GPU", "32.0.15.6094"), "leitura do WMI");
    Exigir(ContextoMidia.PlacaDoEncoder("nvenc", gpus) == "NVIDIA GeForce RTX 3060 Laptop GPU", "NVENC num híbrido");
    Exigir(ContextoMidia.PlacaDoEncoder("amf", gpus) == "AMD Radeon RX 6750 XT", "AMF prefere a dedicada");
    Exigir(ContextoMidia.PlacaDoEncoder("qsv", gpus) == "Intel(R) UHD Graphics 630", "Quick Sync");
    Exigir(ContextoMidia.PlacaDoEncoder("x264", gpus) == null && ContextoMidia.PlacaDoEncoder("mf", gpus) == null, "CPU e Media Foundation não têm placa própria");
    var ad = ContextoMidia.Adaptador("Intel(R) UHD Graphics 630", 0x8086, gpus)!;
    Exigir(S(ad, "nome") == "Intel UHD Graphics 630" && S(ad, "fabricante") == "intel" && S(ad, "driver") == "31.0.101.2111" && ad["integrada"]!.GetValue<bool>(), "integrada");
    var rtx = ContextoMidia.Adaptador("NVIDIA GeForce RTX 3060 Laptop GPU", 0x10DE, gpus)!;
    Exigir(S(rtx, "fabricante") == "nvidia" && S(rtx, "driver") == "32.0.15.6094" && !rtx["integrada"]!.GetValue<bool>(), "dedicada");
    Exigir(ContextoMidia.Fabricante(0x1414, "Microsoft Basic Render Driver") == "microsoft" && ContextoMidia.Fabricante(0, "Parsec Virtual Display Adapter") == "outro", "fabricante");
    Exigir(ContextoMidia.Adaptador(null, 0, gpus) == null, "sem nome");
});

Caso("caminho-do-envio-neste-pc", () =>
{
    // DXGI de verdade (só leitura, sem janela); o WMI não é lido aqui: sem a lista da live, o driver fica nulo
    var montar = ContextoMidia.Envio("ddagrab", 0, IntPtr.Zero, "h264_nvenc", "gpu", (2560, 1440), (1920, 1080), 60, 8000, "1080p60", "auto", lembrado: true);
    var o = montar()!;
    Exigir(S(o, "captura") == "ddagrab" && S(o, "encoder") == "nvenc" && S(o, "codec") == "h264" && o["escala"]!.GetValue<bool>()
        && S(o, "fonte") == "2560x1440" && S(o, "resolucao") == "1920x1080" && S(o, "preset") == "1080p60" && o["lembrado"]!.GetValue<bool>(), "campos do caminho");
    foreach (var k in new[] { "adaptador", "adaptador_captura" })
        if (o[k] is JsonObject a) Exigir(a.Select(x => x.Key).OrderBy(x => x).SequenceEqual(new[] { "driver", "fabricante", "integrada", "nome" }), $"{k} com campos a mais");
    var p = ContextoMidia.Player("vulkan", "vulkan", previa: false, onde: null)()!;
    Exigir(S(p, "decodificador") == "vulkan" && S(p, "renderizador") == "vulkan" && S(p, "codec") == "h264", "caminho do player");
});

Caso("grava-pela-sessao-normal-e-anonima", () =>
{
    foreach (bool anonima in new[] { false, true })
    {
        var sessao = SessaoLocal.Criar("teste-midia-" + (anonima ? "anonima" : "normal"), null, naoEnviar: false, reduzida: anonima);
        ContextoMidia.Ligar((t, d) => sessao.Evento(t, d));
        var midia = new CaminhoMidia.Tentativas(CaminhoMidia.Viewer);
        midia.NovaLive();
        var t = midia.Comecar(GatilhoMidia.Inicio, CaminhoMidia.CaminhoDoPlayer("d3d11va", "d3d11", false, null));
        t.Falhou(EtapaMidia.Decodificacao, MotivoMidia.D3d11SemHardware);
        Exigir(CaminhoMidia.Descarregar(TimeSpan.FromSeconds(15)) && Gravador.Descarregar(TimeSpan.FromSeconds(5)), "não chegou ao disco");
        var linhas = File.ReadAllLines(Path.Combine(sessao.Dir, SessaoLocal.Eventos)).Select(x => JsonNode.Parse(x)!.AsObject()).ToList();
        Exigir(linhas.Select(x => S(x, "tipo")).SequenceEqual(new[] { "midia_tentativa", "midia_falha" }), "eventos gravados: " + string.Join(",", linhas.Select(x => S(x, "tipo"))));
        var f = linhas[1];
        Exigir(S(f, "sessao") == sessao.Id && S(f, "eid").Length > 0 && S(f, "lado") == "viewer" && S(f, "motivo") == "d3d11_sem_hardware" && S(f, "versao").Length > 0, "campos gravados");
        // no modo anônimo o envio passa cada evento pelo Anonimizador: nenhum campo do caminho é de identidade, então nada some
        var subido = Anonimizador.Evento(f.DeepClone().AsObject())!;
        Exigir(subido.Count == f.Count, "o anonimizador tirou campo do caminho de mídia");
        CaminhoMidia.Ligar(null);
    }
});

Caso("carga-recente-resumo", () =>
{
    long agora = 100_000;
    var c = new CargaRecente(10, () => agora);
    Exigir(c.Resumo() == null, "buffer vazio devia ficar sem carga");
    c.Registrar("gpu_3d_pct", null);
    Exigir(c.Resumo() == null, "valor nulo virou leitura");
    foreach (int v in new[] { 10, 90, 20 }) { c.Registrar("gpu_3d_pct", v); agora += 1000; }
    c.Registrar("gpu_encoder_pct", null); // sem leitura: a métrica fica fora, nunca 0
    var r = c.Resumo()!;
    var m = r["metricas"]!.AsObject();
    Exigir(r["janela_s"]!.GetValue<int>() == 10, "janela_s");
    Exigir(m["gpu_3d_pct"]!["mediana"]!.GetValue<int>() == 20 && m["gpu_3d_pct"]!["max"]!.GetValue<int>() == 90 && m["gpu_3d_pct"]!["n_amostras"]!.GetValue<int>() == 3, "mediana, máximo e n_amostras");
    Exigir(!m.ContainsKey("gpu_encoder_pct"), "métrica sem leitura apareceu");
    c.Registrar("cpu_app_nucleo_pct", 0); // 0 medido é leitura de verdade
    Exigir(c.Resumo()!["metricas"]!["cpu_app_nucleo_pct"]!["max"]!.GetValue<int>() == 0, "0 medido sumiu");
    agora += 8500; // as três primeiras leituras (a mais nova com 8,5 s + 1 s) ... a última tem 8,5 s: ainda dentro; as outras saem
    var depois = c.Resumo()!["metricas"]!["gpu_3d_pct"]!;
    Exigir(depois["n_amostras"]!.GetValue<int>() == 1 && depois["max"]!.GetValue<int>() == 20, "janela não expirou as leituras antigas");
    agora += 5000;
    Exigir(c.Resumo() == null, "tudo expirado devia ficar sem carga");
    Exigir(CargaRecente.Host.JanelaS == 10 && CargaRecente.Viewer.JanelaS == 12, "janelas do host e do viewer");
});

Caso("resumo-live-minutos-limite-e-formato", () =>
{
    long agora = 0; var utc = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    CaminhoMidia.Lives = () => JsonNode.Parse("[{\"id\":\"abc\"}]");
    var r = new ResumoLive("host", () => agora, () => utc.AddMilliseconds(agora));
    r.Iniciar();
    foreach (int q in new[] { 60, 20, 40 }) { r.Marcar(); r.Registrar(ResumoLive.QuadrosNovos, q); r.Registrar("gpu_3d_pct", q + 10); r.Registrar(ResumoLive.Espectadores, q / 20); agora += 1000; }
    r.Marcar(); r.Registrar("gpu_encoder_pct", null); // sem leitura: não vira 0
    agora = 61_000; r.Marcar(); r.Registrar("gpu_3d_pct", 5);
    agora = (ResumoLive.MaxMinutos + 2) * 60_000L; r.Marcar(); r.Registrar("gpu_3d_pct", 99); // além de 180 min: só conta como descartado
    var e = r.Fechar()!;
    CaminhoMidia.Lives = null;
    var min = e["minutos"]!.AsArray();
    Exigir(min.Count == 2 && min[0]!["m"]!.GetValue<int>() == 0 && min[1]!["m"]!.GetValue<int>() == 1, "um item por minuto, com o índice m");
    var m0 = min[0]!.AsObject();
    Exigir(m0["s"]!.GetValue<int>() == 4 && m0["quadros_novos_min"]!.GetValue<int>() == 20 && m0["quadros_novos_mediana"]!.GetValue<int>() == 40 && m0["espectadores_max"]!.GetValue<int>() == 3, "agregação do minuto");
    Exigir(m0["metricas"]!["gpu_3d_pct"]!["mediana"]!.GetValue<int>() == 50 && m0["metricas"]!["gpu_3d_pct"]!["max"]!.GetValue<int>() == 70, "mediana e máximo da carga");
    Exigir(m0["metricas"]!["gpu_encoder_pct"] == null, "métrica sem leitura apareceu");
    Exigir(min[1]!["quadros_novos_min"] == null && min[1]!["espectadores_max"] == null, "sem leitura virou campo");
    Exigir(e["minutos_descartados"]!.GetValue<int>() == 1 && e["lado"]!.GetValue<string>() == "host" && e["lives"] is JsonArray, "descartados, lado e lives");
    Exigir(e["inicio_utc"]!.GetValue<string>() == "2026-11-20T12:00:00Z" && e["fim_utc"]!.GetValue<string>().EndsWith("Z"), "inicio_utc e fim_utc");
    Exigir(!e.ToJsonString().Contains('.'), "números não estão arredondados para inteiros");
    Exigir(r.Fechar() == null, "fechar duas vezes devia ficar sem evento");
    // viewer, por ligação: pior A-V em módulo e descartes; o que não veio na amostra fica ausente
    var v = new ResumoLive("viewer", () => agora, () => utc);
    agora = 0; v.Marcar("L1"); v.Registrar(ResumoLive.AvMs, -120, "L1"); v.Registrar(ResumoLive.AvMs, 40, "L1"); v.Registrar(ResumoLive.Descartes, null, "L1");
    v.Marcar("L2"); v.Registrar(ResumoLive.Descartes, 7, "L2");
    var v1 = v.Fechar("L1")!["minutos"]![0]!.AsObject(); var v2 = v.Fechar("L2")!["minutos"]![0]!.AsObject();
    Exigir(v1["av_pior_ms"]!.GetValue<int>() == 120 && v1["descartes"] == null && v2["descartes"]!.GetValue<int>() == 7 && v2["av_pior_ms"] == null, "viewer por ligação");
    Exigir(m0["pausa_s"] == null, "minuto sem pausa ganhou pausa_s");
});

Caso("resumo-live-pausa-de-privacidade", () =>
{
    // live de 23/11: os minutos com o jogo fora da frente saíam com mediana 0 e o painel os lia como captura parada
    long agora = 0; var utc = new DateTime(2026, 11, 23, 23, 0, 0, DateTimeKind.Utc);
    var r = new ResumoLive("host", () => agora, () => utc.AddMilliseconds(agora));
    r.Iniciar();
    for (int i = 0; i < 3; i++) { r.Marcar(); r.Registrar(ResumoLive.QuadrosNovos, 60); agora += 1000; }
    for (int i = 0; i < 2; i++) { r.MarcarPausa(); agora += 1000; } // minuto 0: 3 s de captura e 2 s em pausa
    agora = 60_000;
    for (int i = 0; i < 4; i++) { r.MarcarPausa(); agora += 1000; } // minuto 1: só pausa
    var min = r.Fechar()!["minutos"]!.AsArray();
    var m0 = min[0]!.AsObject(); var m1 = min[1]!.AsObject();
    Exigir(m0["s"]!.GetValue<int>() == 5 && m0["pausa_s"]!.GetValue<int>() == 2, "segundos e pausa_s do minuto misto");
    Exigir(m0["quadros_novos_mediana"]!.GetValue<int>() == 60 && m0["quadros_novos_min"]!.GetValue<int>() == 60, "a pausa entrou nos quadros novos");
    Exigir(m1["s"]!.GetValue<int>() == 4 && m1["pausa_s"]!.GetValue<int>() == 4 && m1["quadros_novos_mediana"] == null, "minuto só em pausa");
});

Caso("resumo-live-segundo-da-serie-de-quadros", () =>
{
    // a ligação entre SerieQuadros.Segundo e o resumo: "pausa" vira pausa_s e fica fora dos quadros novos; os outros valores seguem normais
    long agora = 0; var utc = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
    var r = new ResumoLive("host", () => agora, () => utc.AddMilliseconds(agora));
    r.Iniciar();
    var gpu = new UsoGpu.Amostra(40, 30, 10, 0, 5, 1, 20, 50);
    for (int i = 0; i < 3; i++) { SerieQuadros.SomarNoResumo(r, 60, 2, "sensor", gpu); agora += 1000; }
    for (int i = 0; i < 2; i++) { SerieQuadros.SomarNoResumo(r, 0, 2, "pausa", null); agora += 1000; }
    var m0 = r.Fechar()!["minutos"]![0]!.AsObject();
    Exigir(m0["s"]!.GetValue<int>() == 5 && m0["pausa_s"]!.GetValue<int>() == 2, "s e pausa_s do minuto");
    Exigir(m0["quadros_novos_min"]!.GetValue<int>() == 60 && m0["quadros_novos_mediana"]!.GetValue<int>() == 60, "a pausa entrou em quadros_novos_*");
    Exigir(m0["espectadores_max"]!.GetValue<int>() == 2, "espectadores_max");
    Exigir(m0["metricas"]!["gpu_3d_pct"]!["max"]!.GetValue<int>() == 40, "métrica de GPU do segundo medido");
});

Caso("falha-leva-carga-e-lives", () =>
{
    var l = Coletar();
    CargaRecente.Host.Registrar("gpu_3d_pct", 77);
    CaminhoMidia.Lives = () => JsonNode.Parse("[{\"id\":\"abc\"}]");
    var midia = new CaminhoMidia.Tentativas(CaminhoMidia.Host);
    midia.NovaLive();
    var t = midia.Comecar(GatilhoMidia.Inicio, () => new JsonObject { ["encoder"] = "nvenc" });
    t.Falhou(EtapaMidia.Encoder, MotivoMidia.SaidaAosTrancos);
    var ev = Esvaziar(l);
    CaminhoMidia.Lives = null;
    Exigir(ev.All(e => e.Dados["lives"] is JsonArray), "todo evento midia_* devia ter lives");
    var f = ev.Single(e => e.Tipo == "midia_falha").Dados;
    Exigir(f["carga"]?["metricas"]?["gpu_3d_pct"]?["max"]?.GetValue<int>() == 77, "midia_falha sem carga");
    Exigir(ev.Where(e => e.Tipo != "midia_falha").All(e => e.Dados["carga"] == null), "carga só vai na falha");
    Exigir(SessaoLocal.EventoUrgente("midia_falha") && !SessaoLocal.EventoUrgente("midia_tentativa"), "só midia_falha é urgente");
});

Caso("lives-que-lanca-nao-derruba-o-evento", () =>
{
    var l = Coletar();
    CaminhoMidia.Lives = () => throw new InvalidOperationException("sem contexto");
    var midia = new CaminhoMidia.Tentativas(CaminhoMidia.Viewer);
    midia.NovaLive();
    var t = midia.Comecar(GatilhoMidia.Inicio, () => new JsonObject());
    t.Falhou(EtapaMidia.Decodificacao, MotivoMidia.Outro);
    var ev = Esvaziar(l);
    CaminhoMidia.Lives = null;
    Exigir(ev.Count == 2 && ev.All(e => e.Dados["lives"] == null), "o evento devia sair, sem lives, quando o provedor lança");
});

try { Directory.Delete(tmp, true); } catch { }
Console.WriteLine(falhas == 0 ? "caminho-midia: tudo OK" : $"caminho-midia: {falhas} falha(s)");
return falhas == 0 ? 0 : 1;
