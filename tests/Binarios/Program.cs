using System.Security.Cryptography;
using System.Text;
using Streemu.Media;
using Streemu.Util;

// Testes de Util/Binarios.cs (DESCOBERTAS 2026-11-18): o app só roda o ffmpeg, o ffplay e o mpv que vêm com ele e batem com o SHA-256 do pin.
// Arquivos de mentira em pastas temporárias; nada de ffmpeg de verdade, rede ou estado salvo. Cada caso imprime "OK    binarios-nome" ou "FALHA binarios-nome : motivo".

int falhas = 0;
string raiz = Path.Combine(Path.GetTempPath(), "streemu-teste-binarios-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(raiz);
string pathOriginal = Environment.GetEnvironmentVariable("PATH") ?? "";

void Caso(string nome, Action<string> f)
{
    string dir = Path.Combine(raiz, nome);
    Directory.CreateDirectory(dir);
    try { f(dir); Console.WriteLine("OK    binarios-" + nome); }
    catch (Exception e) { falhas++; Console.WriteLine($"FALHA binarios-{nome} : {e.Message}"); }
    finally { Environment.SetEnvironmentVariable("PATH", pathOriginal); }
}
void Exige(bool ok, string msg) { if (!ok) throw new Exception(msg); }
string Sha(string texto) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(texto))).ToLowerInvariant();
PinBinario Pin(string exe, string conteudo, long tamanho = 0, params (string Nome, string Conteudo)[] outros)
{
    var d = new Dictionary<string, ArquivoPin>(StringComparer.OrdinalIgnoreCase) { [exe] = new(Sha(conteudo), tamanho) };
    foreach (var (n, c) in outros) d[n] = new(Sha(c), 0);
    return new PinBinario("1", "tag", new string('0', 64), 0, d);
}

Caso("path-com-ffmpeg-falso-nunca-e-usado", dir =>
{
    string falsoDir = Path.Combine(dir, "path"); Directory.CreateDirectory(falsoDir);
    File.WriteAllText(Path.Combine(falsoDir, "ffmpeg.exe"), "certo");   // até com o conteúdo certo: o PATH não é lugar de onde o app roda nada
    File.WriteAllText(Path.Combine(falsoDir, "ffplay.exe"), "certo");
    File.WriteAllText(Path.Combine(falsoDir, "mpv.exe"), "certo");
    Environment.SetEnvironmentVariable("PATH", falsoDir + ";" + pathOriginal);
    string app = Path.Combine(dir, "app"); Directory.CreateDirectory(app);
    foreach (var b in new[] { Binario.Ffmpeg, Binario.Ffplay, Binario.Mpv })
    {
        string embutido = Binarios.Embutido(b, app);
        Exige(embutido.StartsWith(app, StringComparison.OrdinalIgnoreCase), $"{b}: o caminho embutido saiu da pasta do app ({embutido})");
        var def = Binarios.Def(b);
        var r = new ConferidorDeBinario(Path.Combine(app, def.Pasta), Pin(def.Exe, "certo"), def.Exe).Conferir();
        Exige(r.Motivo == MotivoBinario.Falta, $"{b}: com o exe só no PATH devia faltar, deu {r.Motivo}");
        Exige(Path.GetDirectoryName(r.Exe)!.StartsWith(app, StringComparison.OrdinalIgnoreCase), $"{b}: o exe devolvido não é o da pasta do app");
        Exige(Binarios.Substituto(b, false, _ => falsoDir, Pin(def.Exe, "certo")) == null, $"{b}: substituto devia ser nulo sem permissão");
    }
    // nome solto nunca vira processo: o Windows o procuraria no PATH
    bool recusou = false;
    try { Tools.Start("ffmpeg.exe", new[] { "-version" }, "teste", redirectStdin: false).Dispose(); }
    catch (BinarioIndisponivelException) { recusou = true; }
    Exige(recusou, "Tools.Start aceitou um nome solto");
});

Caso("hash-errado-nao-e-usado-e-vira-invalido", dir =>
{
    File.WriteAllText(Path.Combine(dir, "ffmpeg.exe"), "adulterado");
    var c = new ConferidorDeBinario(dir, Pin("ffmpeg.exe", "original"), "ffmpeg.exe");
    var r = c.Conferir();
    Exige(r.Motivo == MotivoBinario.Hash && !r.Ok, $"devia recusar por hash, deu {r.Motivo}");
    Exige(!File.Exists(Path.Combine(dir, "ffmpeg.exe")), "o arquivo adulterado continuou com o nome executável");
    Exige(File.Exists(Path.Combine(dir, "ffmpeg.exe.invalido")), "faltou o ffmpeg.exe.invalido");
    Exige(c.Conferir().Motivo == MotivoBinario.Falta, "depois de renomear devia constar como faltando");
});

Caso("tamanho-errado-nao-e-usado", dir =>
{
    File.WriteAllText(Path.Combine(dir, "ffplay.exe"), "original");
    var c = new ConferidorDeBinario(dir, Pin("ffplay.exe", "original", tamanho: 999), "ffplay.exe");
    var r = c.Conferir();
    Exige(r.Motivo == MotivoBinario.Tamanho, $"devia recusar por tamanho, deu {r.Motivo}");
    Exige(c.HashesCalculados == 0, "com o tamanho errado nem precisa calcular o SHA-256");
    Exige(File.Exists(Path.Combine(dir, "ffplay.exe.invalido")), "faltou o ffplay.exe.invalido");
});

Caso("arquivo-certo-e-usado-e-o-cache-nao-reconfere", dir =>
{
    string exe = Path.Combine(dir, "ffmpeg.exe");
    File.WriteAllText(exe, "original");
    var c = new ConferidorDeBinario(dir, Pin("ffmpeg.exe", "original", tamanho: 8), "ffmpeg.exe");
    var r = c.Conferir();
    Exige(r.Ok && r.Exe == exe, $"o arquivo certo devia ser usado, deu {r.Motivo}");
    Exige(c.HashesCalculados == 1, "devia ter calculado o SHA-256 uma vez");
    for (int i = 0; i < 5; i++) Exige(c.Conferir().Ok, "o cache devia continuar valendo");
    Exige(c.HashesCalculados == 1, $"sem mudança no arquivo não pode reconferir (calculou {c.HashesCalculados} vezes)");
    // mudou a data: confere de novo (e continua bom, o conteúdo é o mesmo)
    File.SetLastWriteTimeUtc(exe, DateTime.UtcNow.AddMinutes(-5));
    Exige(c.Conferir().Ok && c.HashesCalculados == 2, "com a data diferente devia reconferir uma vez");
    // trocado por outro conteúdo do mesmo tamanho: a data nova da troca denuncia
    File.WriteAllText(exe, "adulter!");
    var r2 = c.Conferir();
    Exige(r2.Motivo == MotivoBinario.Hash, $"o arquivo trocado devia ser recusado, deu {r2.Motivo}");
});

Caso("cache-entre-sessoes", dir =>
{
    string exe = Path.Combine(dir, "ffmpeg.exe"), cache = Path.Combine(dir, "estado", "binarios-ffmpeg.json");
    File.WriteAllText(exe, "original");
    var hoje = DateTime.UtcNow;
    ConferidorDeBinario Novo(PinBinario p, DateTime? agora = null) => new(dir, p, "ffmpeg.exe", cache, () => agora ?? hoje);
    var pin = Pin("ffmpeg.exe", "original", tamanho: 8);
    var a = Novo(pin);
    Exige(a.Conferir().Ok && a.HashesCalculados == 1 && File.Exists(cache), "a primeira sessão calcula uma vez e grava o cache");
    var b = Novo(pin);
    Exige(b.Conferir().Ok && b.HashesCalculados == 0, "sessão nova com o cache válido não recalcula");
    var velho = Novo(pin, hoje.AddDays(8)); velho.Conferir();
    Exige(velho.HashesCalculados == 1, "depois de 7 dias devia recalcular");
    File.SetLastWriteTimeUtc(exe, DateTime.UtcNow.AddMinutes(-9));
    var d = Novo(pin);
    Exige(d.Conferir().Ok && d.HashesCalculados == 1, "mudou a data: devia recalcular");
    var e = Novo(pin); e.Conferir();
    Exige(e.HashesCalculados == 0, "o cache foi regravado com a data nova");
    File.WriteAllText(exe, "original+");
    var f = Novo(pin);
    Exige(f.Conferir().Motivo == MotivoBinario.Tamanho && f.HashesCalculados == 0, "mudou o tamanho: o pin com tamanho recusa sem hash");
    File.WriteAllText(exe, "original");
    var pinNovo = Pin("ffmpeg.exe", "outra-versao");
    var g = Novo(pinNovo);
    Exige(g.Conferir().Motivo == MotivoBinario.Hash && g.HashesCalculados == 1, "pin diferente (app novo) devia recalcular e recusar");
});

Caso("arquivo-faltando", dir =>
{
    var r = new ConferidorDeBinario(dir, Pin("mpv.exe", "x"), "mpv.exe").Conferir();
    Exige(r.Motivo == MotivoBinario.Falta && r.Arquivo == "mpv.exe", $"devia faltar o mpv.exe, deu {r.Motivo}/{r.Arquivo}");
});

Caso("sem-pin-nao-roda", dir =>
{
    File.WriteAllText(Path.Combine(dir, "ffmpeg.exe"), "qualquer");
    var r = new ConferidorDeBinario(dir, null, "ffmpeg.exe").Conferir();
    Exige(r.Motivo == MotivoBinario.SemPin && File.Exists(Path.Combine(dir, "ffmpeg.exe")), "sem pin recusa, mas não renomeia o que não sabe julgar");
});

Caso("mpv-confere-cada-arquivo", dir =>
{
    File.WriteAllText(Path.Combine(dir, "mpv.exe"), "mpv-certo");
    File.WriteAllText(Path.Combine(dir, "d3dcompiler_43.dll"), "dll-adulterada");
    var pin = Pin("mpv.exe", "mpv-certo", 0, ("d3dcompiler_43.dll", "dll-certa"));
    var c = new ConferidorDeBinario(dir, pin, "mpv.exe");
    var r = c.Conferir();
    Exige(r.Motivo == MotivoBinario.Hash && r.Arquivo == "d3dcompiler_43.dll", $"devia recusar a dll, deu {r.Motivo}/{r.Arquivo}");
    Exige(File.Exists(Path.Combine(dir, "d3dcompiler_43.dll.invalido")), "a dll adulterada devia virar .invalido");
    Exige(File.Exists(Path.Combine(dir, "mpv.exe")), "o mpv.exe certo não pode ser mexido");
});

Caso("substituicao-so-em-teste-ou-debug", dir =>
{
    string ff = Path.Combine(dir, "ff"); Directory.CreateDirectory(ff);
    File.WriteAllText(Path.Combine(ff, "ffmpeg.exe"), "x"); File.WriteAllText(Path.Combine(ff, "ffplay.exe"), "x");
    string mpvDir = Path.Combine(dir, "mpv"); Directory.CreateDirectory(mpvDir);
    string mpv = Path.Combine(mpvDir, "mpv.exe"); File.WriteAllText(mpv, "x");
    string? Env(string k) => k switch { "STREEMU_FFMPEG_DIR" => ff, "STREEMU_MPV" => mpv, _ => null };
    PinBinario PinX(Binario b) => Pin(Binarios.Def(b).Exe, "x");   // o pin bate com os arquivos: só a permissão decide
    Exige(!Binarios.SubstituicaoPermitida(debug: false, emTeste: false), "Release fora de teste não pode permitir a substituição");
    Exige(Binarios.SubstituicaoPermitida(debug: true, emTeste: false), "Debug devia permitir");
    Exige(Binarios.SubstituicaoPermitida(debug: false, emTeste: true), "teste automático devia permitir");
    foreach (var b in new[] { Binario.Ffmpeg, Binario.Ffplay, Binario.Mpv })
    {
        bool negada = Binarios.Substituto(b, Binarios.SubstituicaoPermitida(false, false), Env, PinX(b)) == null;
        Exige(negada, $"{b}: a variável foi obedecida no Release fora de teste");
        Exige(Binarios.Substituto(b, Binarios.SubstituicaoPermitida(false, true), Env, PinX(b)) != null, $"{b}: em teste a variável devia valer");
        Exige(Binarios.Substituto(b, Binarios.SubstituicaoPermitida(true, false), Env, PinX(b)) != null, $"{b}: em Debug a variável devia valer");
    }
    Exige(Binarios.Substituto(Binario.Ffmpeg, true, _ => Path.Combine(dir, "nao-existe"), PinX(Binario.Ffmpeg)) == null, "pasta sem ffmpeg.exe não vale");
    Exige(Binarios.Substituto(Binario.Ffmpeg, true, _ => null, PinX(Binario.Ffmpeg)) == null && Binarios.Substituto(Binario.Mpv, true, _ => "", PinX(Binario.Mpv)) == null, "variável vazia não vale");
});

// 2026-11-24: os testes locais passavam o ffmpeg e o mpv do PATH por estas variáveis, e o substituto rodava sem conferência. Agora ele também tem que
// ser o do Streemu: outra versão (ou um exe de outro nome) é ignorada, sem renomear nada na pasta de quem desenvolve.
Caso("substituto-que-nao-bate-com-o-pin-nao-vale", dir =>
{
    string ff = Path.Combine(dir, "ff"); Directory.CreateDirectory(ff);
    File.WriteAllText(Path.Combine(ff, "ffmpeg.exe"), "outra-versao"); File.WriteAllText(Path.Combine(ff, "ffplay.exe"), "certo");
    string mpvDir = Path.Combine(dir, "mpv"); Directory.CreateDirectory(mpvDir);
    File.WriteAllText(Path.Combine(mpvDir, "mpv.exe"), "certo"); File.WriteAllText(Path.Combine(mpvDir, "d3dcompiler_43.dll"), "dll-de-outra-versao");
    string outroNome = Path.Combine(dir, "meu-mpv.exe"); File.WriteAllText(outroNome, "certo");
    var recusas = new List<string>();
    Exige(Binarios.Substituto(Binario.Ffmpeg, true, _ => ff, Pin("ffmpeg.exe", "certo"), recusas.Add) == null, "ffmpeg de outra versão no STREEMU_FFMPEG_DIR foi aceito");
    Exige(File.Exists(Path.Combine(ff, "ffmpeg.exe")) && !File.Exists(Path.Combine(ff, "ffmpeg.exe.invalido")), "a pasta do substituto não pode ser mexida");
    Exige(Binarios.Substituto(Binario.Ffplay, true, _ => ff, Pin("ffplay.exe", "certo")) != null, "o ffplay certo da mesma pasta devia valer");
    var pinMpv = Pin("mpv.exe", "certo", 0, ("d3dcompiler_43.dll", "dll-certa"));
    Exige(Binarios.Substituto(Binario.Mpv, true, _ => Path.Combine(mpvDir, "mpv.exe"), pinMpv, recusas.Add) == null, "mpv com a DLL de outra versão foi aceito");
    Exige(File.Exists(Path.Combine(mpvDir, "d3dcompiler_43.dll")), "a DLL do substituto não pode virar .invalido");
    Exige(Binarios.Substituto(Binario.Mpv, true, _ => outroNome, Pin("mpv.exe", "certo"), recusas.Add) == null, "STREEMU_MPV com outro nome de exe foi aceito");
    Exige(Binarios.Substituto(Binario.Ffmpeg, true, _ => ff, null, recusas.Add) == null, "sem pin nenhum substituto vale");
    Exige(recusas.Count == 4 && recusas.All(r => r.StartsWith("STREEMU_")), $"cada recusa devia dizer a variável: {string.Join(" | ", recusas)}");
});

// A reconferência do substituto a cada uso (o Slot do Binarios usa ConferirComTentativas + LargaSubstituto): arquivo trocado depois larga o substituto;
// arquivo que só não pôde ser lido (antivírus segurando) NÃO larga, e a leitura que falhou não vira "bom" nem "ruim" no cache.
Caso("substituto-trocado-depois-e-largado", dir =>
{
    string ff = Path.Combine(dir, "ff"); Directory.CreateDirectory(ff);
    string exe = Path.Combine(ff, "ffmpeg.exe");
    File.WriteAllText(exe, "certo");
    var c = Binarios.ConferidorDoSubstituto(Binario.Ffmpeg, true, _ => ff, Pin("ffmpeg.exe", "certo"), null);
    Exige(c != null, "o ffmpeg certo devia ser aceito como substituto");
    var r1 = Binarios.ConferirComTentativas(c!);
    Exige(r1.Ok && !Binarios.LargaSubstituto(r1), $"o substituto certo devia valer, deu {r1.Motivo}");
    File.WriteAllText(exe, "trocado!");   // trocado depois de aceito
    var r2 = Binarios.ConferirComTentativas(c!, esperaMs: 1);
    Exige(!r2.Ok && !r2.Transitorio && r2.Motivo == MotivoBinario.Hash, $"o arquivo trocado devia ser recusado por hash (não transitório), deu {r2.Motivo}/{r2.Transitorio}");
    Exige(Binarios.LargaSubstituto(r2), "o arquivo trocado devia largar o substituto");
    Exige(File.Exists(exe) && !File.Exists(exe + ".invalido"), "a pasta do substituto não pode ser mexida");
});

Caso("substituto-com-leitura-que-falha-nao-e-largado", dir =>
{
    string ff = Path.Combine(dir, "ff"); Directory.CreateDirectory(ff);
    string exe = Path.Combine(ff, "ffmpeg.exe");
    File.WriteAllText(exe, "certo");
    var pin = Pin("ffmpeg.exe", "certo");
    var c = Binarios.ConferidorDoSubstituto(Binario.Ffmpeg, true, _ => ff, pin, null);
    Exige(c != null && Binarios.ConferirComTentativas(c).Ok && c.HashesCalculados == 1, "o substituto certo devia valer, com um SHA-256");
    File.SetLastWriteTimeUtc(exe, DateTime.UtcNow.AddMinutes(-3));   // muda a chave do cache: a próxima conferência tem que ler o arquivo
    ResultadoBinario seguro;
    using (new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.None))   // "antivírus" segurando o arquivo
    {
        seguro = Binarios.ConferirComTentativas(c!, tentativas: 3, esperaMs: 20);
        Exige(!seguro.Ok && seguro.Transitorio, $"com o arquivo preso devia dar falha de leitura transitória, deu {seguro.Motivo}/{seguro.Transitorio}");
        Exige(!Binarios.LargaSubstituto(seguro), "falha de leitura não pode largar o substituto");
        Exige(c!.HashesCalculados == 4, $"devia tentar 3 vezes (1 + 3 tentativas = 4 contas de hash), contou {c.HashesCalculados}");
        Exige(File.Exists(exe) && !File.Exists(exe + ".invalido"), "o arquivo preso não pode ser renomeado");
        // criação do slot com o arquivo preso: o conferidor volta mesmo assim (o Slot o guarda para a próxima chamada), sem dizer "não é o do Streemu"
        var recusas = new List<string>();
        var novo = Binarios.ConferidorDoSubstituto(Binario.Ffmpeg, true, _ => ff, pin, recusas.Add);
        Exige(novo != null && recusas.Count == 0, $"na criação, leitura que falha não pode descartar o substituto ({string.Join(" | ", recusas)})");
        Exige(Binarios.Substituto(Binario.Ffmpeg, true, _ => ff, pin) == null, "com o arquivo preso a função pura não devolve caminho (só esta chamada não o usa)");
    }
    // solto de novo: vale, e como a falha não entrou no cache, o hash é calculado agora
    int antes = c.HashesCalculados;
    var depois = Binarios.ConferirComTentativas(c);
    Exige(depois.Ok && c.HashesCalculados == antes + 1, $"solto o arquivo devia valer calculando o SHA-256 (falha de leitura não pode ir para o cache): {depois.Motivo}, hashes {antes}->{c.HashesCalculados}");
});

Caso("pins-embutidos-completos", dir =>
{
    foreach (var d in Binarios.Defs)
    {
        var pin = Binarios.Pin(d.Id);
        Exige(pin != null, $"{d.Recurso} não foi lido (embutido no build?)");
        Exige(pin!.Arquivos.ContainsKey(d.Exe), $"{d.Recurso} sem o hash de {d.Exe}");
        Exige(pin.Arquivos.Values.All(a => a.Sha256.Length == 64), $"{d.Recurso} com hash que não tem 64 dígitos");
        Exige(pin.ZipSha256.Length == 64 && pin.Tag.StartsWith(d.Chave + "-"), $"{d.Recurso} com tag ou SHA-256 do zip errado");
    }
    Exige(Binarios.Pin(Binario.Mpv)!.Arquivos.ContainsKey("d3dcompiler_43.dll"), "o pin do mpv devia ter a d3dcompiler_43.dll");
    // o formato do mpv, com valor em texto ou em objeto
    string a = new('a', 64), b = new('b', 64), c = new('c', 64);
    string json = "{\"versao\":\"v\",\"tag\":\"mpv-v\",\"zip_sha256\":\"" + a + "\",\"arquivos\":{\"mpv.exe\":\"" + b + "\",\"x.dll\":{\"sha256\":\"" + c + "\",\"tamanho\":5}}}";
    using var s = new MemoryStream(Encoding.UTF8.GetBytes(json));
    var p = PinBinario.Ler(s, "mpv.exe");
    Exige(p != null && p.Arquivos["x.dll"].Tamanho == 5 && p.Arquivos["mpv.exe"].Sha256 == b, "o formato com \"arquivos\" não foi lido");
    using var ruim = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
    Exige(PinBinario.Ler(ruim, "mpv.exe") == null, "json vazio ({}) não pode virar pin: o app não usa binário sem pin");
});

try { Directory.Delete(raiz, true); } catch { }
Console.WriteLine(falhas == 0 ? "binarios: tudo certo" : $"binarios: {falhas} caso(s) falharam");
return falhas == 0 ? 0 : 1;
