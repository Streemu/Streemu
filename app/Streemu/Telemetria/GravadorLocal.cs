using System.Globalization;
using System.Text.Json.Nodes;
using Streemu.Util;

namespace Streemu.Telemetria;

/// <summary>Os quatro disparos que fazem a série por segundo da live subir (spec 018, seção 6). Sem um deles, ela fica só no PC.</summary>
public enum Disparo
{
    /// <summary>A pessoa marcou o problema (botão, atalho ou bandeja).</summary>
    Marcacao = 1,
    /// <summary>Falha grave: exceção fatal, interface travada, captura que travou ou reabriu, queda para CPU (<see cref="Gravidade"/>).</summary>
    FalhaGrave = 2,
    /// <summary>O <c>live_resumo</c> da live mostra um problema que durou (<see cref="GravadorLocal.ResumoComProblema"/>).</summary>
    ResumoComProblema = 3,
    /// <summary>Um amigo da mesma live marcou o problema e a sala pediu "envie agora".</summary>
    Pedido = 4,
}

/// <summary>
/// Gravador local (spec 018, seção 6): as séries por segundo da live (<see cref="SessaoLocal.Serie"/>) ficam 7 dias no PC e só sobem num disparo
/// (<see cref="Disparo"/>), conforme o estado do diagnóstico (<see cref="Permite"/>). Um envio por live em cada PC: o disparo sobe na hora o que já foi gravado e,
/// se havia live aberta, o fim dela (ou a abertura seguinte, se o app morrer) sobe o resto como complemento do MESMO pacote, que é o que o "Apagar" leva embora.
/// Cada live tem os seus arquivos (<c>quadros-N.csv</c>, <c>sincronia-N.ndjson</c>) e o seu pacote: o Worker guarda um pacote por arquivo, e com uma série só por
/// sessão o Apagar de uma segunda live marcada não levaria a série dela.
///
/// Não grava nada novo no disco nem mexe nos arquivos: só decide até que byte cada série pode subir (<see cref="SessaoLocal.Teto"/>); quem sobe é a thread
/// <c>telemetria</c> (<see cref="Envio"/>). Os disparos chegam pelos eventos que a sessão já grava (<see cref="SessaoLocal.AoEvento"/>) e por chamadas da tela.
/// </summary>
public sealed class GravadorLocal
{
    /// <summary>Quanto tempo a série fica no PC (spec 018, seção 6).</summary>
    public static readonly TimeSpan Retencao = TimeSpan.FromDays(7);

    // Limiares do disparo 3. PADRÕES CONSERVADORES, sem calibragem: dependem de números do painel (plano 018, P8). Quem dispara de menos perde a série de um
    // problema; quem dispara demais manda série à toa. Por isso o padrão é só o que é claramente ruim e dura vários minutos seguidos.
    /// <summary>Host: mediana de quadros novos por segundo do minuto abaixo disto conta como minuto ruim (<c>STREEMU_GRAVADOR_FPS_MIN</c>).</summary>
    public static double FpsMinimo { get; set; } = Numero("STREEMU_GRAVADOR_FPS_MIN", 10);
    /// <summary>Quem assiste: o pior desvio de áudio e vídeo do minuto acima disto, em ms, conta como minuto ruim (<c>STREEMU_GRAVADOR_AV_MS</c>).</summary>
    public static int AvMaximoMs { get; set; } = (int)Numero("STREEMU_GRAVADOR_AV_MS", 1500);
    /// <summary>Quantos minutos ruins seguidos fazem o disparo 3 (<c>STREEMU_GRAVADOR_MINUTOS</c>).</summary>
    public static int MinutosSeguidos { get; set; } = (int)Numero("STREEMU_GRAVADOR_MINUTOS", 3);
    /// <summary>Um minuto só entra na conta com pelo menos isto de segundos medidos e fora da pausa de privacidade (minuto cortado no começo ou no fim não prova nada).</summary>
    public const int SegundosMinimos = 30;
    /// <summary>
    /// O disparo 3 fica DESLIGADO até a calibragem (plano 018, P8: calibrar antes de ligar). Liga com <c>STREEMU_GRAVADOR_DISPARO3=1</c> ou, nos testes, por aqui.
    /// </summary>
    public static bool Disparo3Ligado { get; set; } = Environment.GetEnvironmentVariable("STREEMU_GRAVADOR_DISPARO3") == "1";

    static double Numero(string nome, double padrao) =>
        double.TryParse(Environment.GetEnvironmentVariable(nome), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : padrao;

    readonly Envio _envio;
    readonly SessaoLocal _s;
    readonly object _g = new();
    readonly HashSet<string> _lives = new();

    /// <summary>"Só enviar quando eu marcar" (a pergunta depois do primeiro Apagar): só o disparo 1 sobe a série.</summary>
    public Func<bool>? SoQuandoMarcar { get; set; }

    public GravadorLocal(Envio envio)
    {
        _envio = envio; _s = envio.Atual;
        _s.AoEvento = AoEvento;
    }

    /// <summary>
    /// Em que estado cada disparo vale (spec 018, seção 6): Completo e Anônimo sobem; o Anônimo não tem o pedido "agora" (ele não fala com a sala por
    /// identidade). Desligado nunca sobe sozinho, nenhum disparo vale aqui: a marcação segue por outro caminho, que só envia depois de a pessoa ver o que vai
    /// numa janela e apertar "Enviar só isto" (<see cref="EnviarMarcacaoDesligada"/>).
    /// </summary>
    public static bool Permite(ModoEnvio modo, Disparo d, bool soQuandoMarcar = false) => modo switch
    {
        ModoEnvio.Completo => !soQuandoMarcar || d == Disparo.Marcacao,
        ModoEnvio.Anonimo => d != Disparo.Pedido && (!soQuandoMarcar || d == Disparo.Marcacao),
        _ => false,
    };

    void AoEvento(string tipo, JsonObject o)
    {
        switch (tipo)
        {
            case "marcacao": Disparar(Disparo.Marcacao); break;
            case "ui_travada": Disparar(Disparo.FalhaGrave); break;
            case "falha" when o["tipo_falha"]?.GetValue<string>() is string t && t != "outra": Disparar(Disparo.FalhaGrave); break;
            case "live_resumo" when ResumoComProblema(o): Disparar(Disparo.ResumoComProblema); break;
        }
    }

    /// <summary>O amigo da mesma live marcou o problema e a sala pediu "envie agora" (disparo 4).</summary>
    public void Pedido() => Disparar(Disparo.Pedido);

    /// <summary>
    /// Pede para a série subir. Devolve falso se o estado não permite. O teto não muda aqui (esta thread pode ser a da tela e a fila de gravação ainda
    /// não chegou ao disco): fica marcado e a próxima rodada do envio o fixa no fim do arquivo, o que sobe "na hora" (≈ 1 s).
    /// </summary>
    public bool Disparar(Disparo d)
    {
        if (!Permite(_envio.Modo, d, SoQuandoMarcar?.Invoke() ?? false) || _s.E.NaoEnviar) return false;
        lock (_g)
        {
            var e = _s.E;
            // o disparo libera a série da live corrente (ou da última, se já acabou); a de outras lives sem disparo fica só no PC. Se a live já tem pacote
            // (disparo anterior), este entra nele em vez de abrir outro
            if (_s.TeveLive) _s.Pacotear(_s.ArquivosDaLive(), d.ToString());
            if (_lives.Count > 0) e.LiveMarcada = true;
            e.TetoPendente = true;
        }
        _envio.Agora();
        return true;
    }

    /// <summary>Uma live começou (a própria, ou uma ligação de quem assiste). <paramref name="chave"/> é o que a tela usa para a live ("propria" ou o id da ligação).</summary>
    public void LiveComecou(string chave)
    {
        lock (_g)
        {
            // a primeira live aberta (sem outra aberta) usa arquivos novos: um disparo nela não leva a série das lives anteriores
            if (_lives.Count == 0) _s.AbrirLive();
            _lives.Add(chave);
        }
    }

    /// <summary>
    /// A live acabou. Se foi a última aberta e houve disparo nela, o resto da série sobe agora como complemento do mesmo pacote. Chamar DEPOIS de a série
    /// ter sido descarregada (<see cref="SerieQuadros.Descarregar"/>), para o complemento levar os últimos segundos.
    /// </summary>
    public void LiveAcabou(string chave)
    {
        bool completar = false;
        lock (_g)
        {
            _lives.Remove(chave);
            if (_lives.Count == 0 && _s.E.LiveMarcada) { _s.E.LiveMarcada = false; _s.E.TetoPendente = true; completar = true; }
        }
        if (completar) _envio.Agora();
    }

    /// <summary>O app vai fechar: nenhuma live segue aberta, e a que estava marcada completa a série antes do último envio.</summary>
    public void EncerrarTudo()
    {
        string[] abertas; lock (_g) abertas = _lives.ToArray();
        foreach (var c in abertas) LiveAcabou(c);
        lock (_g) if (_s.E.LiveMarcada) { _s.E.LiveMarcada = false; _s.E.TetoPendente = true; }
    }

    /// <summary>
    /// Na abertura seguinte: a sessão de um app que morreu no meio de uma live marcada (ou que fechou sem completar) sobe o resto da série agora
    /// (spec 018, seção 6: "no fim da live ou na abertura seguinte"). O estado diz se o envio daquela sessão é permitido; aqui só se libera o teto.
    /// </summary>
    public static void CompletarSessaoAnterior(SessaoLocal s)
    {
        if (!s.E.LiveMarcada) return;
        s.E.LiveMarcada = false; s.E.TetoPendente = true;
        s.Salvar();
    }

    /// <summary>
    /// O <c>live_resumo</c> (<see cref="ResumoLive.Fechar"/>) mostra um problema que durou: <see cref="MinutosSeguidos"/> minutos seguidos ruins, com amostra
    /// suficiente e fora da pausa de privacidade. Host: mediana de quadros novos abaixo de <see cref="FpsMinimo"/>. Quem assiste: pior desvio de áudio e vídeo acima
    /// de <see cref="AvMaximoMs"/>. Ex.: 3 minutos seguidos com mediana de 6 quadros por segundo dispara; um minuto só, ou 3 separados, não.
    /// </summary>
    public static bool ResumoComProblema(JsonObject resumo)
    {
        if (!Disparo3Ligado || resumo["minutos"] is not JsonArray minutos) return false;
        bool host = resumo["lado"]?.GetValue<string>() == CaminhoMidia.Host;
        int seguidos = 0, ultimo = -2;
        foreach (var n in minutos)
        {
            if (n is not JsonObject m || ResumoLive.Inteiro(m["m"]) is not int idx) continue;
            if (idx != ultimo + 1) seguidos = 0; // minuto sem amostra no meio: a sequência recomeça
            ultimo = idx;
            int s = ResumoLive.Inteiro(m["s"]) ?? 0, pausa = ResumoLive.Inteiro(m["pausa_s"]) ?? 0;
            bool ruim = false;
            if (s >= SegundosMinimos && pausa * 2 < s)
            {
                if (host) ruim = ResumoLive.Inteiro(m["quadros_novos_mediana"]) is int q && q < FpsMinimo;
                else ruim = ResumoLive.Inteiro(m["av_pior_ms"]) is int av && av > AvMaximoMs;
            }
            seguidos = ruim ? seguidos + 1 : 0;
            if (seguidos >= Math.Max(1, MinutosSeguidos)) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- marcação com o diagnóstico desligado (spec 018, seção 6)

    /// <summary>
    /// O que "Enviar só isto" leva: o log e os eventos desta abertura e a série da live mais recente (se houve live e ela já tem arquivo). Nada além: nem o perfil
    /// do PC, nem os relatórios, nem a série de outras lives. É a lista que a janela mostra com os tamanhos.
    /// </summary>
    public List<string> ArquivosDaMarcacaoDesligada()
    {
        var lista = new List<string> { SessaoLocal.Log, SessaoLocal.Eventos };
        if (_s.TeveLive) foreach (var n in _s.ArquivosDaLive()) if (File.Exists(Path.Combine(_s.Dir, n))) lista.Add(n);
        return lista;
    }

    /// <summary>
    /// Com o diagnóstico Desligado, a pessoa viu a janela e apertou "Enviar só isto": sobe UMA vez esses arquivos, como anônimo (sem o nome dela nem o dos amigos,
    /// pelo bilhete da conta), com pacote próprio para o "Apagar". Não muda o modo salvo nem liga nada para depois: o disparo seguinte volta a perguntar.
    /// Bloqueia (fala com o servidor): chamar fora da thread da tela. Nunca lança.
    /// </summary>
    public (bool Ok, string Mensagem) EnviarMarcacaoDesligada()
    {
        if (_envio.Modo != ModoEnvio.Desligado) return (false, "O diagnóstico não está desligado: a marcação sobe pelo envio normal.");
        var nomes = ArquivosDaMarcacaoDesligada();
        lock (_g)
        {
            // um pacote para o log e os eventos da abertura (o servidor guarda o do primeiro envio de cada arquivo) e um para a série da live
            _s.Pacotear(nomes.Where(n => !SessaoLocal.Serie(n)), nameof(Disparo.Marcacao));
            var serie = nomes.Where(SessaoLocal.Serie).ToList();
            if (serie.Count > 0) _s.Pacotear(serie, nameof(Disparo.Marcacao));
        }
        return _envio.EnviarSoIsto(nomes);
    }

    /// <summary>Apaga as séries de uma sessão de mais de 7 dias (<see cref="Retencao"/>), subiram ou não.</summary>
    public static long ApagarSerieVencida(string dir)
    {
        long apagados = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (!SessaoLocal.Serie(Path.GetFileName(f).ToLowerInvariant())) continue;
                try { long tam = new FileInfo(f).Length; File.Delete(f); apagados += tam; } catch { }
            }
        }
        catch { }
        return apagados;
    }
}
