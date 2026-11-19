using System.Text;

namespace Streemu.Telemetria;

/// <summary>
/// Grava, por segundo de transmissão, quantos quadros novos a captura entregou e quantos o encoder soltou, com a hora,
/// em <c>quadros.csv</c> na pasta da sessão (sobe como o log). Motivo (telemetria de 10/11, live de um PC do grupo no Hookshot):
/// o relatório só tinha mín/mediana/máx da live inteira, e os avisos do vigia só aparecem depois de vários segundos
/// ruins; não dava para dizer quando a captura começou a cair nem se bateu com alguém entrando ou com um Alt+Tab.
/// Não vira evento: um por segundo lotaria o índice do servidor e a linha do tempo do LEIA-ME.
/// Junta 10 s na memória e grava de uma vez (a medida chega na thread da captura e não pode esperar disco).
/// </summary>
public sealed class SerieQuadros
{
    public const string Arquivo = "quadros.csv";
    const int Bloco = 10;

    readonly SessaoLocal _sessao;
    readonly object _gate = new();
    readonly StringBuilder _pendente = new();
    int _linhas;
    bool _cabecalho;

    public SerieQuadros(SessaoLocal sessao)
    {
        _sessao = sessao;
        _cabecalho = File.Exists(Path.Combine(sessao.Dir, Arquivo));
    }

    /// <param name="espectadores">Quantos envios a live tem agora (para cruzar a queda com alguém entrando).</param>
    /// <param name="captura">Método da captura agora (ex.: ddagrab, gfxcapture-janela): o vigia troca no meio da live.</param>
    /// <param name="medida">
    /// De onde veio o segundo: "sensor" (o contador de quadros novos falou), "mudo" (o contador está aberto mas passou mais de 1,5 s calado: vale 0
    /// quadros novos), "sem_medida" (o contador fechou e está sendo reaberto: quadros novos vazio) ou "pausa" (o jogo saiu da frente). Antes só havia linha quando o contador falava: numa live de 12/11
    /// às 12:19 ele ficou mudo 2 min 20 s e o quadros.csv simplesmente acabou, sem dizer que a imagem estava parada.
    /// </param>
    /// <param name="gpu">Uso da placa (3D total, 3D do jogo, codificador), em %; nulo = sem leitura.</param>
    public void Segundo(long novos, long saindo, int espectadores, string? captura, bool foco, string medida = "sensor", UsoGpu.Amostra? gpu = null)
    {
        string? gravar = null;
        lock (_gate)
        {
            if (!_cabecalho)
            {
                _pendente.Append("hora;quadros_novos_s;quadros_saindo_s;espectadores;captura;jogo_na_frente;medida;gpu_3d_pct;gpu_jogo_3d_pct;gpu_encoder_pct;gpu_decoder_pct;gpu_app_3d_pct;gpu_dwm_3d_pct;cpu_app_nucleo_pct;cpu_jogo_nucleo_pct\n");
                _cabecalho = true;
            }
            // novos < 0 = sem medida (o contador tinha fechado e estava sendo reaberto): coluna vazia, nunca um 0 que pareça imagem parada
            _pendente.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(';').Append(novos >= 0 ? novos.ToString() : "").Append(';').Append(saindo)
                .Append(';').Append(espectadores).Append(';').Append(captura ?? "").Append(';').Append(foco ? 1 : 0)
                .Append(';').Append(medida)
                .Append(';').Append(gpu?.Total3d.ToString() ?? "").Append(';').Append(gpu?.Jogo3d.ToString() ?? "").Append(';').Append(gpu?.Encoder.ToString() ?? "")
                .Append(';').Append(gpu?.Decoder.ToString() ?? "")
                .Append(';').Append(gpu?.App3d.ToString() ?? "").Append(';').Append(gpu?.Dwm3d.ToString() ?? "")
                .Append(';').Append(gpu?.AppCpu.ToString() ?? "").Append(';').Append(gpu?.JogoCpu.ToString() ?? "").Append('\n');
            if (++_linhas >= Bloco) gravar = Tirar();
        }
        if (gravar != null) _sessao.Acrescentar(Arquivo, gravar);
    }

    /// <summary>Grava o que estiver na memória (fim da live, saída do app).</summary>
    public void Descarregar()
    {
        string? gravar;
        lock (_gate) gravar = _linhas > 0 || _pendente.Length > 0 ? Tirar() : null;
        if (gravar != null) _sessao.Acrescentar(Arquivo, gravar);
    }

    string Tirar()
    {
        string s = _pendente.ToString();
        _pendente.Clear();
        _linhas = 0;
        return s;
    }
}
