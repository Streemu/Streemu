using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Streemu.Telemetria;

/// <summary>Onde a mídia falhou. Lista fechada: o painel filtra por ela, então código novo só entra aqui, com o mesmo nome na doc (TELEMETRIA.md).</summary>
public enum EtapaMidia { Captura, Escala, Encoder, Envio, Decodificacao, Renderizacao }

/// <summary>Por que falhou. Lista fechada: nada de texto do ffmpeg, do jogo ou da janela sai daqui. O que não se reconhece vira <see cref="Outro"/>.</summary>
public enum MotivoMidia
{
    // envio: teste do encoder e processo da captura (ver CaminhoMidia.ClassificarFfmpeg); DispositivoRemovido também vem do player (VK_ERROR_DEVICE_LOST no ffplay, lado viewer)
    EncoderIndisponivel, DriverIncompativel, SemMemoria, ParametroRecusado, TexturaRecusada, DispositivoRemovido, CapturaNaoAbriu,
    Demorou, SaiuComErro, NenhumEncoder, ProcessoSaiu, CaiuRepetidas,
    // envio: o vigia da captura e o envio para quem assiste
    CapturaTravada, PoucosQuadros, SaidaAosTrancos, AudioVideoAfastados, EnvioNaoAbre,
    // reprodução
    VulkanFalhou, JanelaNaoAbriu, SaiuCedo, D3d11Indisponivel, D3d11NaoAbriu, D3d11SemHardware, D3d11NaoManteve,
    Outro,
}

/// <summary>O que fez a tentativa começar. Lista fechada, como etapa e motivo.</summary>
public enum GatilhoMidia { Inicio, TrocaConfig, Reabertura, Recuperacao, ReaberturaJanela, Outro }

/// <summary>
/// Caminho de mídia efetivamente usado em cada tentativa (proposta de compatibilidade das lives, TELEMETRIA.md). Só observa: nada aqui muda decisão,
/// prioridade ou recuperação do app. Antes o painel só cruzava uma falha com o perfil de hardware mais recente do PC, que muitas vezes chegava depois
/// dela (driver novo instalado no dia seguinte, por exemplo); agora cada tentativa diz o encoder, a captura e a placa que de fato usou.
///
/// Quatro eventos: <c>midia_tentativa</c> (o pipeline começa), <c>midia_video_ok</c> (primeiro vídeo confirmado), <c>midia_falha</c> e
/// <c>midia_recuperada</c> (o vídeo voltou depois de uma falha). Os da mesma tentativa têm o mesmo <c>tentativa</c>.
///
/// Este arquivo não depende do resto do app (o tests/ViewerFallback compila o Viewer.cs com ele, sem o app): quem diz versão e placa é
/// <c>ContextoMidia</c>, ligado pelo MainForm. Quem chama (laço da captura, stderr do ffmpeg, interface) só põe o evento numa fila;
/// uma thread própria, de prioridade baixa, monta o JSON (DXGI, versões) e entrega ao <c>SessaoLocal.Evento</c>, que já respeita o
/// diagnóstico desligado e o modo anônimo como qualquer outro evento.
/// </summary>
public static class CaminhoMidia
{
    public const string TipoTentativa = "midia_tentativa", TipoVideoOk = "midia_video_ok", TipoFalha = "midia_falha", TipoRecuperada = "midia_recuperada";
    public static readonly string[] Tipos = { TipoTentativa, TipoVideoOk, TipoFalha, TipoRecuperada };
    public const string Host = "host", Viewer = "viewer";

    /// <summary>
    /// Teto por abertura do app. Tentativa e vídeo confirmado têm um, falha e recuperação outro: uma tarde de Alt+Tab e janelas redimensionadas não pode
    /// gastar a cota das falhas, que são o que interessa.
    /// </summary>
    public static int MaxComuns { get; set; } = 400;
    public static int MaxFalhas { get; set; } = 400;

    static volatile Action<string, JsonObject>? _grava;
    /// <summary>Versão do app e dos binários deste lado (<c>ContextoMidia</c>); nulo nos testes que não ligam.</summary>
    public static Func<string, JsonObject?>? Versoes { get; set; }

    /// <summary>Quais lives estão ativas (o mesmo provedor do Vigia, ligado pelo MainForm); nulo = eventos sem <c>lives</c>. Lê na thread da fila.</summary>
    public static Func<JsonNode?>? Lives { get; set; }

    /// <summary>Quem monta o caminho do player com a placa (<c>ContextoMidia.Player</c>); nulo = só os códigos, sem placa (tests/ViewerFallback).</summary>
    public static Func<string, string, bool, Rectangle?, Func<JsonObject?>>? ProvedorDoPlayer { get; set; }

    /// <summary>O caminho da reprodução: decodificador, renderizador e, com o app ligado, a placa do monitor do player.</summary>
    public static Func<JsonObject?> CaminhoDoPlayer(string decodificador, string renderizador, bool previa, Rectangle? onde) =>
        ProvedorDoPlayer?.Invoke(decodificador, renderizador, previa, onde) ?? (() => new JsonObject
        {
            ["decodificador"] = DaLista(decodificador, Decodificadores), ["renderizador"] = DaLista(renderizador, Renderizadores),
            ["codec"] = "h264", ["previa"] = previa,
        });

    /// <summary>O destino dos eventos (a sessão da telemetria). Nulo = nada é registrado (teste automático sem servidor, CaptureTest).</summary>
    public static void Ligar(Action<string, JsonObject>? grava) => _grava = grava;
    public static bool Ligado => _grava != null;

    static int _comuns, _falhas, _descartados;
    public static int Descartados => Volatile.Read(ref _descartados);

    /// <summary>Só os testes.</summary>
    public static void Reiniciar() { _comuns = 0; _falhas = 0; _descartados = 0; Descarregar(TimeSpan.FromSeconds(2)); }

    // ------------------------------------------------------------------ fila

    static readonly BlockingCollection<(string Tipo, Func<JsonObject?> Montar)> _fila = new(256);
    static readonly object _inicio = new();
    static Thread? _thread;
    static int _pendentes;
    /// <summary>Quantos eventos esperam a fila (testes).</summary>
    public static int Pendentes => Volatile.Read(ref _pendentes);

    static void Enfileirar(string tipo, Func<JsonObject?> montar)
    {
        // chamado de dentro do StreamSender e do Viewer: nenhuma exceção daqui pode subir para a live
        try { EnfileirarSemProtecao(tipo, montar); } catch { }
    }

    static void EnfileirarSemProtecao(string tipo, Func<JsonObject?> montar)
    {
        if (_grava == null) return;
        bool falha = tipo is TipoFalha or TipoRecuperada;
        int n = falha ? Interlocked.Increment(ref _falhas) : Interlocked.Increment(ref _comuns);
        if (n > (falha ? MaxFalhas : MaxComuns)) { Interlocked.Increment(ref _descartados); return; }
        Interlocked.Increment(ref _pendentes);
        if (!_fila.TryAdd((tipo, montar))) { Interlocked.Decrement(ref _pendentes); Interlocked.Increment(ref _descartados); return; }
        Garantir();
    }

    static void Garantir()
    {
        if (_thread != null) return;
        lock (_inicio)
        {
            if (_thread != null) return;
            // prioridade normal: o trabalho é mínimo (montar um JSON pequeno), e abaixo do normal a fila parava por segundos com o app minimizado
            // atrás de um jogo (Windows em modo de eficiência), justamente quando a falha acontece
            _thread = new Thread(Laco) { IsBackground = true, Name = "telemetria-midia" };
            _thread.Start();
        }
    }

    static void Laco()
    {
        foreach (var (tipo, montar) in _fila.GetConsumingEnumerable())
        {
            try
            {
                var o = montar();
                if (o != null) _grava?.Invoke(tipo, o);
            }
            catch { /* telemetria nunca derruba a live nem chega ao usuário */ }
            finally { Interlocked.Decrement(ref _pendentes); }
        }
    }

    /// <summary>Espera a fila esvaziar (testes). Falso se passou do prazo.</summary>
    public static bool Descarregar(TimeSpan prazo)
    {
        var fim = DateTime.UtcNow + prazo;
        while (Volatile.Read(ref _pendentes) > 0 && DateTime.UtcNow < fim) Thread.Sleep(5);
        return Volatile.Read(ref _pendentes) == 0;
    }

    // ------------------------------------------------------------------ códigos fechados

    /// <summary>EncoderIndisponivel → encoder_indisponivel; D3d11NaoAbriu → d3d11_nao_abriu.</summary>
    public static string Codigo(Enum valor)
    {
        string nome = valor.ToString();
        var sb = new StringBuilder(nome.Length + 4);
        for (int i = 0; i < nome.Length; i++)
        {
            char c = nome[i];
            if (char.IsUpper(c) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    public static string Codigo(EtapaMidia e) => Codigo((Enum)e);
    public static string Codigo(MotivoMidia m) => Codigo((Enum)m);
    public static string Codigo(GatilhoMidia g) => Codigo((Enum)g);

    /// <summary>h264_nvenc → nvenc, libx264 → x264...; desconhecido → outro.</summary>
    public static string Encoder(string? ffmpeg) => ffmpeg switch
    {
        "h264_nvenc" or "hevc_nvenc" or "av1_nvenc" => "nvenc",
        "h264_amf" or "hevc_amf" or "av1_amf" => "amf",
        "h264_qsv" or "hevc_qsv" or "av1_qsv" => "qsv",
        "h264_mf" or "hevc_mf" => "mf",
        "libx264" => "x264",
        _ => "outro",
    };

    public static string Codec(string? ffmpeg) => ffmpeg switch
    {
        null => "outro",
        "libx264" => "h264",
        _ when ffmpeg.StartsWith("h264_") => "h264",
        _ when ffmpeg.StartsWith("hevc_") => "hevc",
        _ when ffmpeg.StartsWith("av1_") => "av1",
        _ => "outro",
    };

    /// <summary>Nome do CaptureSpec → código. Os três são os únicos que o Capture.cs cria.</summary>
    public static string Captura(string? nome) => nome switch
    {
        "ddagrab" => "ddagrab",
        "gfxcapture-janela" => "gfxcapture_janela",
        "gfxcapture-monitor" => "gfxcapture_monitor",
        _ => "outro",
    };

    public static readonly string[] Decodificadores = { "vulkan", "d3d11va", "cpu" };
    public static readonly string[] Renderizadores = { "vulkan", "sdl", "d3d11" };
    public static string DaLista(string? valor, string[] lista) => valor != null && lista.Contains(valor) ? valor : "outro";

    public static string Resolucao(int w, int h) => $"{Math.Clamp(w, 0, 16384)}x{Math.Clamp(h, 0, 16384)}";

    /// <summary>
    /// Texto curto que vem do sistema (nome da placa, versão do driver ou do binário): sem marcas (R)/(TM), sem espaço repetido, só letras, números e
    /// pontuação simples, no máximo <paramref name="max"/> caracteres. Não serve para texto da pessoa: esse nunca entra nestes eventos.
    /// </summary>
    public static string? Curto(string? texto, int max = 64)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        string t = texto.Replace("(R)", "", StringComparison.OrdinalIgnoreCase).Replace("(TM)", "", StringComparison.OrdinalIgnoreCase).Replace("®", "").Replace("™", "");
        var sb = new StringBuilder(t.Length);
        bool espaco = false;
        foreach (char c in t)
        {
            bool ok = char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '+' or '/' or '(' or ')';
            if (!ok) { espaco = sb.Length > 0; continue; }
            if (espaco) { sb.Append(' '); espaco = false; }
            sb.Append(c);
            if (sb.Length >= max) break;
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>
    /// Etapa e motivo de uma linha de erro do ffmpeg (teste do encoder ou processo da captura que saiu). Só o código sai do PC; a linha fica no log.
    /// A ordem importa: "out of memory" do NVENC é memória mesmo vindo do encoder, e erro do gfxcapture/ddagrab é da captura mesmo citando um filtro.
    /// </summary>
    public static (EtapaMidia Etapa, MotivoMidia Motivo) ClassificarFfmpeg(string? linha, bool demorou, EtapaMidia padrao, MotivoMidia motivoPadrao)
    {
        if (demorou) return (padrao, MotivoMidia.Demorou);
        string l = (linha ?? "").ToLowerInvariant();
        if (l.Length == 0) return (padrao, motivoPadrao);
        if (l.Contains("device_removed") || l.Contains("device removed") || l.Contains("887a0005") || l.Contains("887a0006") || l.Contains("device hung"))
            return (padrao, MotivoMidia.DispositivoRemovido);
        if (l.Contains("out of memory") || l.Contains("outofmemory") || l.Contains("out_of_device_memory") || l.Contains("insufficient memory"))
            return (padrao, MotivoMidia.SemMemoria);
        if (l.Contains("driver does not support") || l.Contains("minimum required") || l.Contains("api version") || l.Contains("update your driver") || l.Contains("driver version"))
            return (EtapaMidia.Encoder, MotivoMidia.DriverIncompativel);
        if (l.Contains("no nvenc capable") || l.Contains("no capable devices") || l.Contains("cannot load nvcuda") || l.Contains("cannot load nvencodeapi")
            || l.Contains("amfrt64") || l.Contains("failed to load amf") || l.Contains("mfx session") || l.Contains("libmfx") || l.Contains("no device available")
            || l.Contains("unknown encoder") || l.Contains("could not find any mft") || l.Contains("no mft"))
            return (EtapaMidia.Encoder, MotivoMidia.EncoderIndisponivel);
        if (l.Contains("gfxcapture") || l.Contains("ddagrab") || l.Contains("duplicateoutput") || l.Contains("desktop duplication") || l.Contains("graphics capture"))
            return (EtapaMidia.Captura, MotivoMidia.CapturaNaoAbriu);
        if (l.Contains("hwmap") || l.Contains("hwdownload") || l.Contains("vpp_amf") || l.Contains("impossible to convert") || l.Contains("failed to map")
            || l.Contains("hw_frames_ctx") || l.Contains("reinitializing filters") || l.Contains("configure output pad") || l.Contains("scaler"))
            return (EtapaMidia.Escala, MotivoMidia.TexturaRecusada);
        if (l.Contains("opening encoder") || l.Contains("open encoder") || l.Contains("initializeencoder") || l.Contains("invalid argument")
            || l.Contains("not supported") || l.Contains("invalid param") || l.Contains("incorrect parameters"))
            return (EtapaMidia.Encoder, MotivoMidia.ParametroRecusado);
        return (padrao, motivoPadrao);
    }

    /// <summary>
    /// O motivo da reabertura da captura (texto do StreamSender) vira etapa e motivo de falha; nulo = não é falha (reinício pedido por teste).
    /// Casa com os textos do próprio StreamSender: mudou lá, o tests/CaminhoMidia reprova.
    /// </summary>
    public static (EtapaMidia Etapa, MotivoMidia Motivo)? FalhaDaReabertura(string? motivo)
    {
        if (motivo == null) return null;
        if (motivo.StartsWith("o ffmpeg da captura saiu sozinho", StringComparison.Ordinal)) return (EtapaMidia.Captura, MotivoMidia.ProcessoSaiu);
        if (motivo.StartsWith("captura travada", StringComparison.Ordinal)) return (EtapaMidia.Captura, MotivoMidia.CapturaTravada);
        if (motivo.StartsWith("trocando para ", StringComparison.Ordinal)) return (EtapaMidia.Captura, MotivoMidia.PoucosQuadros);
        if (motivo.StartsWith("encoder aos trancos", StringComparison.Ordinal)) return (EtapaMidia.Encoder, MotivoMidia.SaidaAosTrancos);
        if (motivo.StartsWith("tempos de áudio e vídeo", StringComparison.Ordinal)) return (EtapaMidia.Captura, MotivoMidia.AudioVideoAfastados);
        return null;
    }

    // ------------------------------------------------------------------ tentativas

    /// <summary>
    /// As tentativas de um lado numa live (um <c>StreamSender</c> ou um <c>Viewer</c>). Numera, liga os eventos pelo id e mede o tempo até o vídeo e
    /// até voltar. Seguro entre threads; nada aqui espera disco ou rede.
    /// </summary>
    public sealed class Tentativas
    {
        readonly string _lado;
        readonly object _g = new();
        int _numero;
        Tentativa? _atual, _proxima;
        (string Id, long Ticks)? _falhaSemVolta;

        public Tentativas(string lado) => _lado = lado;

        public Tentativa? Atual { get { lock (_g) return _atual; } }

        /// <summary>Começo da live (ou do player): numeração do 1 e nenhuma falha esperando recuperação.</summary>
        public void NovaLive() { lock (_g) { _numero = 0; _atual = null; _proxima = null; _falhaSemVolta = null; } }

        /// <summary>Fim da live: falha sem volta fica sem <c>midia_recuperada</c> (é assim que o painel vê "não recuperou").</summary>
        public void Encerrar() { lock (_g) { _atual = null; _proxima = null; _falhaSemVolta = null; } }

        /// <summary>A próxima tentativa, antes de o pipeline abrir (o teste dos encoders já fala em nome dela).</summary>
        public Tentativa Preparar(GatilhoMidia gatilho)
        {
            // uma preparada que não chegou a abrir (a live parou, a troca voltou atrás) cede o número: a numeração conta só o que abriu ou falhou ao abrir
            lock (_g) return _proxima = new Tentativa(this, _lado, _proxima?.Numero ?? ++_numero, gatilho);
        }

        /// <summary>O pipeline abriu: usa a preparada (ou cria uma com <paramref name="seNaoPreparada"/>) e registra <c>midia_tentativa</c>.</summary>
        public Tentativa Comecar(GatilhoMidia seNaoPreparada, Func<JsonObject?> caminho)
        {
            Tentativa t;
            lock (_g)
            {
                t = _proxima ?? new Tentativa(this, _lado, ++_numero, seNaoPreparada);
                _proxima = null;
                _atual = t;
            }
            t.Comecou(caminho);
            return t;
        }

        /// <summary>A preparada, se houver; senão a atual. É a quem o teste do encoder atribui as falhas dele.</summary>
        public Tentativa? PreparadaOuAtual { get { lock (_g) return _proxima ?? _atual; } }

        public bool TemPreparada { get { lock (_g) return _proxima != null; } }

        internal void Falhou(Tentativa t, bool interrompe, bool fatal)
        {
            lock (_g)
            {
                if (fatal) _falhaSemVolta = null;
                else if (interrompe) _falhaSemVolta ??= (t.Id, Stopwatch.GetTimestamp());
            }
        }

        internal (string Id, long Ticks)? VoltouDepoisDe(Tentativa t)
        {
            lock (_g)
            {
                if (_falhaSemVolta is not { } f || f.Id == t.Id) return null; // a própria tentativa que falhou não "volta": quem volta é a seguinte
                _falhaSemVolta = null;
                return f;
            }
        }
    }

    /// <summary>Uma abertura do pipeline. Os eventos dela levam o mesmo <see cref="Id"/>.</summary>
    public sealed class Tentativa
    {
        readonly Tentativas _dono;
        public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
        public string Lado { get; }
        public int Numero { get; }
        public GatilhoMidia Gatilho { get; }
        readonly long _criada = Stopwatch.GetTimestamp();
        long _comecou, _video;
        int _videoOk;
        /// <summary>O caminho montado (só a thread da fila escreve e lê).</summary>
        JsonObject? _caminho;

        internal Tentativa(Tentativas dono, string lado, int numero, GatilhoMidia gatilho) { _dono = dono; Lado = lado; Numero = numero; Gatilho = gatilho; }

        public bool TemVideo => Volatile.Read(ref _videoOk) == 1;

        /// <summary>Campos de todo evento; as versões só na tentativa e na falha (no resto, o id liga ao evento que as tem).</summary>
        JsonObject Base(bool comVersoes = false)
        {
            var o = new JsonObject { ["lado"] = Lado, ["tentativa"] = Id, ["numero"] = Numero, ["gatilho"] = Codigo(Gatilho) };
            if (comVersoes && Versoes?.Invoke(Lado) is { } v) foreach (var (k, val) in v) o[k] = val?.DeepClone();
            try { if (Lives?.Invoke() is { } lives) o["lives"] = lives; } catch { } // como no Vigia: sem o contexto, o evento sai mesmo assim
            return o;
        }

        static void Juntar(JsonObject o, JsonObject? extra)
        {
            if (extra == null) return;
            foreach (var (k, v) in extra) o[k] = v?.DeepClone();
        }

        internal void Comecou(Func<JsonObject?> caminho)
        {
            Interlocked.Exchange(ref _comecou, Stopwatch.GetTimestamp());
            Enfileirar(TipoTentativa, () =>
            {
                _caminho = caminho();
                var o = Base(comVersoes: true);
                Juntar(o, _caminho);
                return o;
            });
        }

        /// <summary>Primeiro vídeo confirmado. Só o primeiro conta; chamar de novo é barato (o laço de 1 s chama a cada segundo).</summary>
        public void VideoOk(Func<JsonObject?>? medidas = null)
        {
            if (Interlocked.Exchange(ref _videoOk, 1) == 1) return;
            long agora = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref _video, agora);
            long desde = Volatile.Read(ref _comecou) is long c and > 0 ? c : _criada;
            long ms = (long)Stopwatch.GetElapsedTime(desde, agora).TotalMilliseconds;
            var volta = _dono.VoltouDepoisDe(this);
            Enfileirar(TipoVideoOk, () =>
            {
                var o = Base();
                o["ms"] = ms;
                Resumo(o);
                Juntar(o, medidas?.Invoke());
                return o;
            });
            if (volta is { } f)
            {
                long msVolta = (long)Stopwatch.GetElapsedTime(f.Ticks, agora).TotalMilliseconds;
                Enfileirar(TipoRecuperada, () =>
                {
                    var o = Base();
                    o["ms"] = msVolta;
                    o["tentativa_falha"] = f.Id;
                    Resumo(o);
                    return o;
                });
            }
        }

        /// <summary>
        /// Falha. <paramref name="teste"/>: o teste de encoder antes de abrir (não interrompe nada). <paramref name="interrompe"/>: falso quando o vídeo
        /// não para por causa dela (envio de um espectador). <paramref name="caminho"/>: o caminho de quem falhou, quando não é o da tentativa
        /// (o candidato do teste de encoder).
        /// </summary>
        public void Falhou(EtapaMidia etapa, MotivoMidia motivo, bool fatal = false, bool teste = false, bool interrompe = true,
            Func<JsonObject?>? medidas = null, Func<JsonObject?>? caminho = null)
        {
            long agora = Stopwatch.GetTimestamp();
            long video = Volatile.Read(ref _video);
            long? util = video > 0 ? (long)Stopwatch.GetElapsedTime(video, agora).TotalMilliseconds : null;
            if (!teste) _dono.Falhou(this, interrompe, fatal);
            Enfileirar(TipoFalha, () =>
            {
                var o = Base(comVersoes: true);
                o["etapa"] = Codigo(etapa);
                o["motivo"] = Codigo(motivo);
                o["fatal"] = fatal;
                o["teste"] = teste;
                o["duracao_util_ms"] = util;
                try { if (CargaRecente.Do(Lado)?.Resumo() is { } carga) o["carga"] = carga; } catch { } // sem leitura = sem o campo, nunca 0%
                Juntar(o, caminho != null ? caminho() : _caminho);
                Juntar(o, medidas?.Invoke());
                return o;
            });
        }

        /// <summary>No vídeo confirmado e na volta, só o essencial do caminho (o resto está no <c>midia_tentativa</c> com o mesmo id).</summary>
        void Resumo(JsonObject o)
        {
            if (_caminho == null) return;
            foreach (var k in new[] { "captura", "encoder", "caminho", "decodificador", "renderizador", "resolucao", "fps", "fps_pedido", "monitor_hz", "beta" })
                if (_caminho[k] is { } v) o[k] = v.DeepClone();
        }
    }
}
