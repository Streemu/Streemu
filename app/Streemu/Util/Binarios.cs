using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Streemu.Net;

namespace Streemu.Util;

/// <summary>Os três executáveis de terceiros que o app roda: o ffmpeg (captura e envio), o ffplay (player da live) e o mpv (player do filme).</summary>
public enum Binario { Ffmpeg, Ffplay, Mpv }

public enum MotivoBinario { Ok, Falta, Hash, Tamanho, SemPin }

/// <summary>SHA-256 (minúsculo) e tamanho esperados de um arquivo. Tamanho 0 = não conferir o tamanho.</summary>
public sealed record ArquivoPin(string Sha256, long Tamanho);

/// <summary>
/// O que este exe quer de um binário (ffmpeg-desejado.json, ffplay-desejado.json, mpv-desejado.json, embutidos no build): a versão, onde baixar
/// (tag da pré-release e SHA-256 do zip) e o SHA-256 de CADA arquivo que roda. Quem decide é o exe, não o servidor: código e binário testados juntos.
/// </summary>
public sealed record PinBinario(string Versao, string Tag, string ZipSha256, long ZipTamanho, IReadOnlyDictionary<string, ArquivoPin> Arquivos)
{
    /// <summary>
    /// Lê o json. Dois formatos: o do ffmpeg/ffplay (um exe só: "sha256" e "tamanho") e o do mpv ("arquivos": { "mpv.exe": "&lt;sha256&gt;", ... },
    /// o valor pode ser o texto ou { "sha256", "tamanho" }). Nulo se faltar o que é preciso para conferir.
    /// </summary>
    public static PinBinario? Ler(Stream s, string exePrincipal)
    {
        if (JsonNode.Parse(s) is not JsonObject j) return null;
        string Str(string k) => j[k]?.GetValue<string>() ?? "";
        long Num(string k) => j[k]?.GetValue<long>() ?? 0;
        var arquivos = new Dictionary<string, ArquivoPin>(StringComparer.OrdinalIgnoreCase);
        if (j["arquivos"] is JsonObject lista)
        {
            foreach (var (nome, v) in lista)
            {
                string sha = v is JsonObject o ? o["sha256"]?.GetValue<string>() ?? "" : v?.GetValue<string>() ?? "";
                long tam = v is JsonObject o2 ? o2["tamanho"]?.GetValue<long>() ?? 0 : 0;
                arquivos[nome] = new ArquivoPin(sha.ToLowerInvariant(), tam);
            }
        }
        else arquivos[exePrincipal] = new ArquivoPin(Str("sha256").ToLowerInvariant(), Num("tamanho"));
        var pin = new PinBinario(Str("versao"), Str("tag"), Str("zip_sha256").ToLowerInvariant(), Num("zip_tamanho"), arquivos);
        bool completo = pin.Tag.Length > 0 && pin.ZipSha256.Length == 64 && arquivos.ContainsKey(exePrincipal) && arquivos.Values.All(a => a.Sha256.Length == 64);
        return completo ? pin : null;
    }
}

public sealed record DefBinario(Binario Id, string Chave, string Pasta, string Exe, string Recurso, string? VarPin, string Descricao, string Rota);

public sealed record ResultadoBinario(MotivoBinario Motivo, string Exe, string? Arquivo)
{
    public bool Ok => Motivo == MotivoBinario.Ok;
}

/// <summary>Um binário que não pode ser usado agora. A mensagem já diz o motivo provável e o que fazer; quem pega a exceção só a mostra.</summary>
public sealed class BinarioIndisponivelException : InvalidOperationException
{
    public BinarioIndisponivelException(string mensagem) : base(mensagem) { }
}

/// <summary>
/// Confere os arquivos de UM binário contra o pin (spec 004, seção 8; DESCOBERTAS 2026-11-18). Até então o app usava o primeiro ffmpeg que achasse
/// (pasta do app, raiz ou PATH) e qualquer um rodava na live de todos: versões diferentes entre os amigos e arquivo trocado sem ninguém ver.
/// O SHA-256 é calculado na primeira vez e de novo só se o tamanho ou a data do arquivo mudarem. Arquivo diferente vira "&lt;nome&gt;.invalido" para
/// nunca ser executado (o Windows não roda esse nome). Uma instância só confere por vez: quem chega no meio espera a conferência acabar.
/// </summary>
public sealed class ConferidorDeBinario
{
    readonly string _dir, _exe;
    readonly PinBinario? _pin;
    readonly object _gate = new();
    readonly Dictionary<string, string> _ok = new(StringComparer.OrdinalIgnoreCase);
    readonly string? _cache;
    readonly Func<DateTime> _agora;
    Dictionary<string, EntradaCache>? _guardado;

    /// <summary>
    /// Cache entre sessões, por arquivo: tamanho + data de escrita + data de criação + o SHA-256 do pin com que bateu + quando foi a última conferência completa.
    /// Porquê: 570 MB de SHA-256 a cada abertura pesam em notebook com HD. Contra adulteração local o cache não enfraquece nada (quem escreve na pasta do
    /// app troca o próprio Streemu.exe); o que a conferência pega é arquivo corrompido, versão errada e mexida de antivírus, e qualquer um deles muda
    /// tamanho ou data. Vale 7 dias; mudou o arquivo, o pin (app novo) ou passou o prazo: recalcula. Fica fora da pasta do app (<paramref name="arquivoCache"/>,
    /// em %LOCALAPPDATA%\Streemu); nulo = sem cache (testes e STREEMU_NOSTATE, para ele não vazar entre casos).
    /// </summary>
    public ConferidorDeBinario(string dir, PinBinario? pin, string exe, string? arquivoCache = null, Func<DateTime>? agora = null)
    {
        _dir = dir; _pin = pin; _exe = exe; _cache = arquivoCache; _agora = agora ?? (() => DateTime.UtcNow);
    }

    public static readonly TimeSpan ValidadeDoCache = TimeSpan.FromDays(7);

    sealed record EntradaCache(string Chave, string Sha256, DateTime Quando);

    Dictionary<string, EntradaCache> Guardado()
    {
        if (_guardado != null) return _guardado;
        _guardado = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (_cache != null && File.Exists(_cache) && JsonNode.Parse(File.ReadAllText(_cache)) is JsonObject j)
                foreach (var (nome, v) in j)
                    if (v is JsonObject o && o["chave"]?.GetValue<string>() is string c && o["sha256"]?.GetValue<string>() is string s && o["quando"]?.GetValue<DateTime>() is DateTime q)
                        _guardado[nome] = new(c, s, q);
        }
        catch { _guardado.Clear(); } // cache ilegível: recalcula, nada pior
        return _guardado;
    }

    void Guardar(string nome, string chave, string sha)
    {
        if (_cache == null) return;
        try
        {
            var g = Guardado();
            g[nome] = new(chave, sha, _agora());
            var j = new JsonObject();
            foreach (var (n, e) in g) j[n] = new JsonObject { ["chave"] = e.Chave, ["sha256"] = e.Sha256, ["quando"] = e.Quando };
            Directory.CreateDirectory(Path.GetDirectoryName(_cache)!);
            File.WriteAllText(_cache, j.ToJsonString());
        }
        catch { }
    }

    /// <summary>Quantos SHA-256 já foram calculados (o teste usa para provar que o cache funciona).</summary>
    public int HashesCalculados { get; private set; }

    public ResultadoBinario Conferir()
    {
        lock (_gate)
        {
            string exe = Path.Combine(_dir, _exe);
            if (_pin == null) return new(MotivoBinario.SemPin, exe, null);
            foreach (var (nome, esperado) in _pin.Arquivos)
            {
                string p = Path.Combine(_dir, nome);
                FileInfo fi;
                try { fi = new FileInfo(p); if (!fi.Exists) { _ok.Remove(nome); return new(MotivoBinario.Falta, exe, nome); } }
                catch { return new(MotivoBinario.Falta, exe, nome); }
                string chave = $"{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{fi.CreationTimeUtc.Ticks}";
                if (_ok.TryGetValue(nome, out var antes) && antes == chave) continue;
                _ok.Remove(nome);
                if (Guardado().TryGetValue(nome, out var g) && g.Chave == chave && g.Sha256 == esperado.Sha256 && _agora() - g.Quando < ValidadeDoCache && _agora() >= g.Quando)
                {
                    _ok[nome] = chave; // bateu inteiro há menos de 7 dias e o arquivo não mudou: não recalcula
                    continue;
                }
                var motivo = MotivoBinario.Ok;
                if (esperado.Tamanho > 0 && fi.Length != esperado.Tamanho) motivo = MotivoBinario.Tamanho;
                else
                {
                    string? sha;
                    try { HashesCalculados++; sha = Sha256(p); }
                    catch (IOException) { return new(MotivoBinario.Hash, exe, nome); } // antivírus segurando o arquivo: não usa, mas também não renomeia
                    if (sha != esperado.Sha256) motivo = MotivoBinario.Hash;
                }
                if (motivo != MotivoBinario.Ok) { Invalidar(p); return new(motivo, exe, nome); }
                _ok[nome] = chave;
                Guardar(nome, chave, esperado.Sha256);
            }
            return new(MotivoBinario.Ok, exe, null);
        }
    }

    static void Invalidar(string p)
    {
        try { File.Move(p, p + ".invalido", overwrite: true); Log.Info($"[binarios] {Path.GetFileName(p)} não bate com o esperado: renomeado para {Path.GetFileName(p)}.invalido e não será usado."); }
        catch (Exception e) { Log.Info($"[binarios] {Path.GetFileName(p)} não bate com o esperado e não consegui renomear ({e.Message}); não será usado."); }
    }

    public static string Sha256(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }
}

/// <summary>
/// A única porta de entrada para o caminho de ffmpeg, ffplay e mpv. Só devolve o arquivo que vem com o Streemu (ffmpeg\ e mpv\ ao lado do exe) E
/// bate com o pin; nunca o PATH, nunca a raiz do app, nunca um nome solto (o Windows resolveria sozinho e rodaria qualquer ffmpeg).
/// Exceção, só para teste e desenvolvimento (<see cref="SubstituicaoPermitida(bool,bool)"/>): STREEMU_FFMPEG_DIR e STREEMU_MPV.
/// </summary>
public static class Binarios
{
#if DEBUG
    const bool EhDebug = true;
#else
    const bool EhDebug = false;
#endif

    public static readonly DefBinario[] Defs =
    {
        new(Binario.Ffmpeg, "ffmpeg", "ffmpeg", "ffmpeg.exe", "ffmpeg-desejado.json", "STREEMU_FFMPEG_DESEJADO", "ffmpeg (captura e envio da live)", "ffmpeg"),
        new(Binario.Ffplay, "ffplay", "ffmpeg", "ffplay.exe", "ffplay-desejado.json", "STREEMU_FFPLAY_DESEJADO", "player da live (ffplay)", "ffplay"),
        new(Binario.Mpv, "mpv", "mpv", "mpv.exe", "mpv-desejado.json", null, "player do filme (mpv)", "mpv"),
    };

    public static DefBinario Def(Binario b) => Defs[(int)b];

    /// <summary>Debug, ou teste automático (STREEMU_TEST_DIR ou --local, que ligam <see cref="RelayConfig.ModoTeste"/>). No Release fora de teste: nunca.</summary>
    public static bool SubstituicaoPermitida(bool debug, bool emTeste) => debug || emTeste;

    public static bool SubstituicaoLiberada => SubstituicaoPermitida(EhDebug, RelayConfig.ModoTeste);

    /// <summary>
    /// O binário de teste que vale no lugar do embutido, ou nulo (substituição não permitida, variável vazia ou arquivo que não existe).
    /// STREEMU_FFMPEG_DIR é uma pasta com ffmpeg.exe e ffplay.exe; STREEMU_MPV é o caminho do mpv.exe.
    /// </summary>
    public static string? Substituto(Binario b, bool permitida, Func<string, string?> env)
    {
        if (!permitida) return null;
        string? v = b == Binario.Mpv ? env("STREEMU_MPV") : env("STREEMU_FFMPEG_DIR");
        if (string.IsNullOrWhiteSpace(v)) return null;
        string p = b == Binario.Mpv ? v : Path.Combine(v, Def(b).Exe);
        return File.Exists(p) ? p : null;
    }

    /// <summary>Onde o binário embutido mora: &lt;pasta do app&gt;\ffmpeg\ffmpeg.exe, ffmpeg\ffplay.exe e mpv\mpv.exe. Só isso.</summary>
    public static string Embutido(Binario b, string pastaDoApp) => Path.Combine(pastaDoApp, Def(b).Pasta, Def(b).Exe);

    // ------------------------------------------------------------------ o pin de cada binário

    static readonly ConcurrentDictionary<Binario, PinBinario?> _pins = new();

    /// <summary>O pin embutido (STREEMU_FFMPEG_DESEJADO / STREEMU_FFPLAY_DESEJADO=&lt;json&gt; o trocam, mas só onde a substituição vale: serve para testar a troca).</summary>
    public static PinBinario? Pin(Binario b) => _pins.GetOrAdd(b, LerPin);

    static PinBinario? LerPin(Binario b)
    {
        var d = Def(b);
        try
        {
            string? file = SubstituicaoLiberada && d.VarPin != null ? Environment.GetEnvironmentVariable(d.VarPin) : null;
            using Stream? s = !string.IsNullOrEmpty(file) ? File.OpenRead(file) : typeof(Binarios).Assembly.GetManifestResourceStream(d.Recurso);
            return s == null ? null : PinBinario.Ler(s, d.Exe);
        }
        catch (Exception e) { Log.Info($"[binarios] {d.Recurso} inválido ({e.Message}); o {d.Chave} não pode ser conferido e não será usado."); return null; }
    }

    // ------------------------------------------------------------------ estado de cada binário

    enum Reparo { Nenhum, Rodando, SemConta, Falhou }

    sealed class Slot
    {
        public required Binario B;
        public string? Substituto;
        public ConferidorDeBinario Conf = null!;
        public bool Episodio;           // já avisou deste problema (um aviso, um evento, um reparo por vez)
        public Reparo Reparo;
        public string? Falha;           // por que o reparo não deu certo
        public ResultadoBinario? Ultimo;
    }

    static readonly ConcurrentDictionary<Binario, Slot> _slots = new();

    static Slot SlotDe(Binario b) => _slots.GetOrAdd(b, k =>
    {
        var s = new Slot { B = k };
        s.Substituto = Substituto(k, SubstituicaoLiberada, Environment.GetEnvironmentVariable);
        if (s.Substituto != null) Log.Info($"[binarios] usando {Def(k).Chave} de teste em {Path.GetDirectoryName(s.Substituto)} (sem conferência de SHA-256).");
        // sem cache entre sessões em teste e com STREEMU_NOSTATE (nada de estado salvo, e ele não pode vazar entre casos)
        string? cache = RelayConfig.ModoTeste || Environment.GetEnvironmentVariable("STREEMU_NOSTATE") == "1" || Environment.GetEnvironmentVariable("STREEMU_TEST_DIR") != null ? null
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Streemu", $"binarios-{Def(k).Chave}.json");
        s.Conf = new ConferidorDeBinario(Path.Combine(AppContext.BaseDirectory, Def(k).Pasta), Pin(k), Def(k).Exe, cache);
        return s;
    });

    /// <summary>Servidor do grupo, para baixar de novo. Posto pela janela principal; nulo (testes, --capture-test) = não há de onde baixar.</summary>
    public static Func<RelayConfig.Settings?>? Servidor { get; set; }

    /// <summary>Mostra um aviso na tela. <c>precisaDeVoce</c>: o reparo sozinho não vai dar certo (sem conta, download falhou).</summary>
    public static Action<string, bool>? Aviso { get; set; }

    static Action<string, JsonObject>? _tele;
    static readonly List<(string Tipo, JsonObject Dados)> _teleFila = new();

    /// <summary>Liga o evento <c>binario_invalido</c> à sessão da telemetria. O que aconteceu antes de ela existir (a conferência da abertura) sai agora.</summary>
    public static void LigarTelemetria(Action<string, JsonObject> grava)
    {
        lock (_teleFila)
        {
            _tele = grava;
            foreach (var (t, d) in _teleFila) try { grava(t, d); } catch { }
            _teleFila.Clear();
        }
    }

    static void Evento(Binario b, MotivoBinario motivo, string reparo)
    {
        var dados = new JsonObject { ["arquivo"] = Def(b).Chave, ["motivo"] = NomeMotivo(motivo), ["reparo"] = reparo };
        lock (_teleFila)
        {
            if (_tele == null) { _teleFila.Add(("binario_invalido", dados)); return; }
            try { _tele("binario_invalido", dados); } catch { }
        }
    }

    static string NomeMotivo(MotivoBinario m) => m switch { MotivoBinario.Falta => "falta", MotivoBinario.Tamanho => "tamanho", MotivoBinario.SemPin => "sem_pin", _ => "hash" };

    // ------------------------------------------------------------------ uso

    /// <summary>
    /// Caminho completo do binário, já conferido, ou nulo (motivo em <see cref="Motivo"/>). Espera a conferência se ela estiver rodando; nunca a pula.
    /// Chame fora da thread da janela (a primeira conferência de um exe de 200 MB leva ~1 s; <see cref="IniciarConferencia"/> a adianta na abertura).
    /// Com arquivo diferente ou faltando, no ffmpeg e no ffplay o reparo (baixar de novo) começa sozinho; o do mpv é do fluxo do filme, que já mostra o progresso.
    /// </summary>
    public static string? Caminho(Binario b)
    {
        var s = SlotDe(b);
        if (s.Substituto != null) return s.Substituto;
        var r = s.Conf.Conferir();
        if (!s.Episodio || r.Ok) s.Ultimo = r; // dentro de um problema, o motivo que vale é o primeiro (depois de renomear o arquivo só "falta" aparece)
        if (r.Ok)
        {
            lock (s) { if (s.Episodio && s.Reparo != Reparo.Rodando) { s.Episodio = false; s.Reparo = Reparo.Nenhum; s.Falha = null; } }
            return r.Exe;
        }
        Problema(s, r);
        return null;
    }

    /// <summary>Para o perfil da telemetria, sem caminho: "teste" (substituto de teste), "embutido" (o do app, conferido) ou "invalido" (falta ou não bate; o mpv que ainda não foi baixado também).</summary>
    public static string Origem(Binario b) => SlotDe(b).Substituto != null ? "teste" : Caminho(b) != null ? "embutido" : "invalido";

    /// <summary>Como <see cref="Caminho"/>, mas lança <see cref="BinarioIndisponivelException"/> com o motivo e o que fazer.</summary>
    public static string Exigir(Binario b) => Caminho(b) ?? throw new BinarioIndisponivelException(Motivo(b));

    /// <summary>A conferência da abertura, em segundo plano: a primeira live não espera o SHA-256 de 500 MB.</summary>
    public static void IniciarConferencia()
    {
        foreach (var d in Defs)
        {
            var b = d.Id;
            Task.Run(() => { try { Caminho(b); } catch (Exception e) { Log.Info($"[binarios] conferência do {Def(b).Chave}: {e.Message}"); } });
        }
    }

    /// <summary>Texto para o usuário sobre o estado atual do binário (o motivo provável e a próxima ação).</summary>
    public static string Motivo(Binario b)
    {
        var s = SlotDe(b);
        var d = Def(b);
        var r = s.Ultimo;
        string o = $"O {d.Descricao} do Streemu";
        string oque = r?.Motivo switch
        {
            MotivoBinario.Falta => $"{o} não está na pasta do programa.",
            MotivoBinario.SemPin => $"{o} não pode ser conferido neste build (falta o {d.Recurso}). Reinstale o Streemu pelo instalador.",
            _ => $"{o} estava diferente do esperado (arquivo alterado ou corrompido).",
        };
        if (r?.Motivo == MotivoBinario.SemPin) return oque;
        return s.Reparo switch
        {
            Reparo.Rodando => $"{oque} Baixando de novo ({Mb(b)} MB); quando terminar, tente de novo.",
            Reparo.SemConta => $"{oque} Entre na sua conta do Streemu para baixar de novo, ou reinstale o Streemu pelo instalador.",
            Reparo.Falhou => $"{oque} Não consegui baixar de novo ({s.Falha}). Reinstale o Streemu pelo instalador.",
            _ => b == Binario.Mpv ? oque : $"{oque} Reinstale o Streemu pelo instalador.",
        };
    }

    static long Mb(Binario b) => (Pin(b)?.ZipTamanho ?? 0) / 1048576;

    static void Problema(Slot s, ResultadoBinario r)
    {
        var b = s.B;
        // mpv que nunca foi baixado é o normal da primeira vez (o filme o baixa na hora); só arquivo que existia e não bate é problema
        if (b == Binario.Mpv && r.Motivo == MotivoBinario.Falta && !s.Episodio) return;
        bool iniciar;
        lock (s)
        {
            iniciar = !s.Episodio;
            s.Episodio = true;
        }
        if (!iniciar) return;
        string quem = Def(b).Chave;
        Log.Info($"[binarios] {quem} {(r.Motivo == MotivoBinario.Falta ? "faltando" : "diferente do esperado")} ({NomeMotivo(r.Motivo)}, arquivo {r.Arquivo}); não vai ser usado.");
        if (r.Motivo == MotivoBinario.SemPin) { Evento(b, r.Motivo, "falhou"); Avisar(s, true); return; }

        if (b == Binario.Mpv)
        {
            s.Reparo = Reparo.Rodando; // o fluxo do filme baixa; MpvInstalador conta o fim em ReparoDoMpvTerminou
            Evento(b, r.Motivo, "iniciado");
            Avisar(s, false);
            return;
        }
        var cfg = Servidor?.Invoke();
        var pin = Pin(b);
        if (cfg == null || pin == null || RelayConfig.Token == null)
        {
            s.Reparo = Reparo.SemConta;
            Evento(b, r.Motivo, "falhou");
            Avisar(s, true);
            return;
        }
        s.Reparo = Reparo.Rodando;
        Evento(b, r.Motivo, "iniciado");
        Avisar(s, false);
        _ = Task.Run(() => ReparaAsync(s, cfg, r.Motivo));
    }

    static async Task ReparaAsync(Slot s, RelayConfig.Settings cfg, MotivoBinario motivo)
    {
        var b = s.B;
        bool ok = false;
        var up = BinarioUpdater.De(b);
        try { ok = await up.RunAsync(cfg, Pin(b)!, () => true, f => Task.FromResult(f()), CancellationToken.None); }
        catch (Exception e) { up.UltimaFalha = e.Message; }
        if (ok) ok = s.Conf.Conferir().Ok;
        if (ok)
        {
            s.Reparo = Reparo.Nenhum; s.Episodio = false; s.Falha = null;
            Evento(b, motivo, "ok");
            Log.Info($"[binarios] {Def(b).Chave} baixado de novo e conferido.");
            Aviso?.Invoke($"O {Def(b).Descricao} do Streemu está pronto. {(b == Binario.Ffplay ? "Pode assistir." : "Pode iniciar a live.")}", false);
            return;
        }
        s.Falha = up.UltimaFalha ?? "o download não terminou";
        s.Reparo = up.UltimaFalha == RelayConfig.TextoSemConta ? Reparo.SemConta : Reparo.Falhou;
        Evento(b, motivo, "falhou");
        Avisar(s, true);
    }

    static void Avisar(Slot s, bool precisaDeVoce)
    {
        try { Aviso?.Invoke(Motivo(s.B), precisaDeVoce); } catch { }
    }

    /// <summary>O fluxo do filme (MpvInstalador) terminou de baixar o mpv que estava diferente do esperado.</summary>
    public static void ReparoDoMpvTerminou(bool ok, string? falha)
    {
        var s = SlotDe(Binario.Mpv);
        if (!s.Episodio) return;
        var motivo = s.Ultimo?.Motivo ?? MotivoBinario.Hash;
        if (ok) { s.Reparo = Reparo.Nenhum; s.Episodio = false; s.Falha = null; Evento(Binario.Mpv, motivo, "ok"); return; }
        s.Reparo = Reparo.Falhou; s.Falha = falha;
        Evento(Binario.Mpv, motivo, "falhou");
    }

    /// <summary>Confere sem avisar nem reparar (o instalador do mpv, o script de release). Respeita a substituição de teste.</summary>
    public static ResultadoBinario Conferir(Binario b)
    {
        var s = SlotDe(b);
        return s.Substituto != null ? new(MotivoBinario.Ok, s.Substituto, null) : s.Conf.Conferir();
    }
}
