using System.Text.Json.Nodes;
using Streemu.Arquivos;
using Streemu.Filme;
using Streemu.Net;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// Enviar arquivo (spec 020): a ligação de cada pedido e a transferência, com o transporte do Cinema (Filme/Envio e Filme/Download,
/// as mesmas mensagens filme_baixar/filme_arquivo, mas numa ligação só deste pedido). Uma ligação por pedido, arquivos um depois
/// do outro: o túnel tem um tratador só para os pacotes de arquivo (<c>Tunnel.Arquivo</c>). A sala e as ações: MainForm.Arquivos.cs.
/// </summary>
public partial class MainForm
{
    /// <summary>Teste (T5): trata a ligação como relay de reserva, para exercitar a D1 no loopback, que é sempre direto.</summary>
    static readonly bool TesteArquivoRelay = Environment.GetEnvironmentVariable("STREEMU_TEST_ARQUIVO_RELAY") == "1";
    static readonly TimeSpan EsperaConectar = TimeSpan.FromSeconds(25), DesistirDeConectar = TimeSpan.FromSeconds(40);
    const int SegundosParaDarComoCaida = 35;

    /// <summary>
    /// Cria a ligação do pedido. Quem manda passa a atender os filme_baixar dela, só com os arquivos que ofereceu.
    /// Devolve o motivo, se não deu.
    /// </summary>
    async Task<string?> AbrirLigacaoArquivoAsync(PedidoEmCurso p, bool iAmSender)
    {
        try { p.Link = await PeerLink.CreateAsync("arq" + p.Pedido, p.AmigoId, p.Apelido, iAmSender, _localMode, Nick, CarrierIp, LinkRelay, "arquivo"); }
        catch (Exception e) { return $"Não deu para preparar a conexão com {p.Apelido}: {e.Message}"; }
        if (iAmSender) p.Link.Control += m => { if (Tipo(m) == "filme_baixar") Ui(() => ServirArquivoDoPedido(p, m)); };
        p.Relogio.Start();
        return null;
    }

    /// <summary>Quem manda: o amigo aceitou com o código dele. Liga e responde com o meu ("ligar").</summary>
    async Task AceitoPeloAmigoAsync(PedidoEmCurso p, JsonObject msg)
    {
        if (p.Fase != FaseArquivo.Esperando || MensagemArquivo.CodigoDe(msg) is not string codigo) return;
        p.Fase = FaseArquivo.Conectando;
        if (await AbrirLigacaoArquivoAsync(p, iAmSender: true) is string erro) { EncerrarEAvisar(p, FaseArquivo.Falhou, erro); return; }
        if (p.Terminado) { FecharLigacaoArquivo(p); return; }
        try { p.Link!.Connect(codigo); }
        catch (FormatException e) { EncerrarEAvisar(p, FaseArquivo.Falhou, $"{p.Apelido} mandou um código inválido ({e.Message}). Vocês estão em versões diferentes?"); return; }
        await _room!.SendTo(p.AmigoId, MensagemArquivo.ComCodigo(MensagemArquivo.Ligar, p.Pedido, p.Link.MyCode));
        Log.Info($"[arquivo] {p.Apelido} aceitou; conectando.");
    }

    /// <summary>Quem recebe: chegou o código de quem manda. Liga e começa a receber.</summary>
    void LigarAoRemetente(PedidoEmCurso p, JsonObject msg)
    {
        if (p.Fase != FaseArquivo.Conectando || p.Link == null || MensagemArquivo.CodigoDe(msg) is not string codigo) return;
        try { p.Link.Connect(codigo); }
        catch (FormatException e) { EncerrarEAvisar(p, FaseArquivo.Falhou, $"{p.Apelido} mandou um código inválido ({e.Message}). Vocês estão em versões diferentes?"); return; }
        _ = ReceberArquivosAsync(p);
    }

    // ------------------------------------------------------------------ quem manda

    /// <summary>Um filme_baixar do amigo: serve o arquivo pedido, se ele está nesta oferta (nunca outro arquivo do PC).</summary>
    void ServirArquivoDoPedido(PedidoEmCurso p, JsonObject m)
    {
        string id = m["filme"]?.GetValue<string>() ?? "";
        if (p.Terminado || p.Link == null || !p.Caminhos.TryGetValue(id, out var caminho)) return;
        if (p.EnvioDe != id) TrocarEnvio(p, id, caminho);
        p.Fase = FaseArquivo.Transferindo;
        p.Envio!.Responder();
    }

    /// <summary>Próximo arquivo: o envio anterior sai antes (o túnel só tem um tratador de pacotes de arquivo).</summary>
    void TrocarEnvio(PedidoEmCurso p, string id, string caminho)
    {
        p.Envio?.Dispose();
        // com live saindo deste PC, a transferência fica só com a sobra do upload (a mesma regra do Cinema)
        string nome = TesteArquivoNomeFalso ?? Path.GetFileNameWithoutExtension(caminho); // MainForm.ArquivosTeste.cs (critério 18)
        p.Envio = new Envio(p.Link!.Tunnel, caminho, id, nome, () => TesteArquivoLiveSaindo || _sender.Running || _links.Count > 0);
        p.EnvioDe = id;
        p.BytesAntes = 0;
        p.ArquivoAtual = Math.Max(0, p.Oferta.Arquivos.ToList().FindIndex(a => a.Identidade == id));
    }

    /// <summary>A cada 1 s: o que já saiu do arquivo atual e a velocidade (a tela de quem manda não vê o progresso do outro lado).</summary>
    static void AtualizarProgressoDoEnvio(PedidoEmCurso p)
    {
        if (p.Fase != FaseArquivo.Transferindo || p.Envio?.Emissor is not { } e) return;
        long bytes = e.BytesEnviados, tamanho = p.Oferta.Arquivos[p.ArquivoAtual].Tamanho;
        p.Progresso = new Progresso("enviando", Math.Min(bytes, tamanho), tamanho, Math.Max(0, bytes - p.BytesAntes));
        p.BytesAntes = bytes;
    }

    /// <summary>
    /// Quem manda não fica sabendo se o app do outro fechou: o Envio só para de mandar. Conectando: desiste depois de 40 s sem
    /// ligação. Transferindo: dá como caída depois de 35 s seguidos sem ligação (quem recebe desiste aos 30 s, Download.cs).
    /// </summary>
    static bool LigacaoArquivoCaiu(PedidoEmCurso p)
    {
        if (p.Fase == FaseArquivo.Conectando) return p.Relogio.Elapsed > DesistirDeConectar && p.Link is not { Connected: true };
        if (p.Sentido != Sentido.Enviado || p.Fase != FaseArquivo.Transferindo) return false;
        p.SegundosSemLigacao = p.Link is { Connected: true } ? 0 : p.SegundosSemLigacao + 1;
        return p.SegundosSemLigacao >= SegundosParaDarComoCaida;
    }

    // ------------------------------------------------------------------ quem recebe

    async Task ReceberArquivosAsync(PedidoEmCurso p)
    {
        if (!await EsperarLigacaoAsync(p)) { EncerrarEAvisar(p, FaseArquivo.Falhou, $"Não consegui conectar com {p.Apelido} em 25 s. Tente de novo."); return; }
        if (!await LiberadoPeloRelayAsync(p)) return;
        p.Fase = FaseArquivo.Transferindo;
        AvisarMudancaArquivos();
        string pasta = HistoricoArquivos.PastaRecebidos;
        try
        {
            for (int i = 0; i < p.Oferta.Arquivos.Count; i++) await ReceberUmAsync(p, i, pasta);
        }
        catch (OperationCanceledException) { return; } // quem cancelou já encerrou o pedido
        catch (Exception e) { FalhaAoReceber(p, e); return; }
        _ = _room?.SendTo(p.AmigoId, MensagemArquivo.Simples(MensagemArquivo.Concluido, p.Pedido));
        TerminarPedido(p, FaseArquivo.Conferido, null);
    }

    /// <summary>O motivo daqui pode ter a pasta deste PC (sem espaço, nome recusado): ao amigo vai só o aviso.</summary>
    void FalhaAoReceber(PedidoEmCurso p, Exception e)
    {
        var fase = p.Link is { Connected: true } ? FaseArquivo.Falhou : FaseArquivo.Parou;
        EncerrarEAvisar(p, fase, e.Message, $"{Nick} não conseguiu receber. O que já veio fica guardado com {Nick}.");
    }

    async Task<bool> EsperarLigacaoAsync(PedidoEmCurso p)
    {
        var ate = DateTime.Now + EsperaConectar;
        while (DateTime.Now < ate && !p.Terminado)
        {
            if (p.Link is { Connected: true }) return true;
            await Task.Delay(100);
        }
        return false;
    }

    /// <summary>D1: pelo relay, até 100 MB segue; até 2 GB, a pessoa confirma o gasto da cota (a tela da T6 responde); acima, bloqueia.</summary>
    async Task<bool> LiberadoPeloRelayAsync(PedidoEmCurso p)
    {
        p.PeloRelay = TesteArquivoRelay || p.Link?.Tunnel.Target is { Kind: not CandKind.Direct };
        var decisao = RegraRelay.Decidir(p.PeloRelay, p.Oferta.Total);
        if (decisao == DecisaoRelay.Livre) return true;
        if (decisao == DecisaoRelay.Bloquear)
        {
            EncerrarEAvisar(p, FaseArquivo.Bloqueado, $"A conexão com {p.Apelido} só funciona pelo relay de reserva, e lá o limite é {RegraRelay.Teto / 1e9:N0} GB por envio ({p.Oferta.Total / 1e9:N1} GB aqui). Tente de outra rede ou divida os arquivos.");
            return false;
        }
        p.Fase = FaseArquivo.ConfirmandoRelay;
        p.PerguntouEm = DateTime.Now;
        p.RespostaRelay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Log.Info($"[arquivo] a ligação com {p.Apelido} passa pelo relay: esperando confirmar {p.Oferta.Total / 1e6:N0} MB da cota.");
        AvisarMudancaArquivos();
        bool seguir = await p.RespostaRelay.Task;
        if (!seguir) EncerrarEAvisar(p, FaseArquivo.Cancelado, $"{Nick} preferiu não usar a cota do relay de reserva.");
        return seguir;
    }

    /// <summary>A resposta da pessoa ao aviso de cota do relay (true = receber mesmo assim).</summary>
    internal void ResponderRelayArquivo(string pedido, bool seguir)
    {
        if (_pedidosArquivo.TryGetValue(pedido, out var p) && p.Fase == FaseArquivo.ConfirmandoRelay) p.RespostaRelay?.TrySetResult(seguir);
    }

    /// <summary>Um arquivo: baixa e confere (Download), põe a marca de "baixado da internet" e guarda onde ficou.</summary>
    async Task ReceberUmAsync(PedidoEmCurso p, int i, string pasta)
    {
        var arquivo = p.Oferta.Arquivos[i];
        p.ArquivoAtual = i;
        var textos = new TextosDownload(p.Apelido, "o arquivo", "arquivo", "[arquivo]");
        var download = new Download(p.Link!.Tunnel, arquivo.Identidade, pasta, textos);
        string final = await Task.Run(() => download.RodarAsync(pr => Ui(() => { p.Progresso = pr; AvisarMudancaArquivos(); }), p.Parar.Token));
        p.Caminhos[arquivo.Identidade] = final;
        p.PedacosRefeitos += download.Relatorio()["pedacos_refeitos"]?.GetValue<int>() ?? 0;
        if (!MarcaDaInternet.Marcar(final)) Log.Info("[arquivo] a pasta não aceita a marca de \"baixado da internet\" (FAT32/exFAT?); segui sem ela.");
    }

    // ------------------------------------------------------------------ fim

    /// <summary>Encerra e avisa o outro lado pela sala (a ligação pode já ter caído). <paramref name="paraOAmigo"/>: sem ele, vai o mesmo motivo.</summary>
    void EncerrarEAvisar(PedidoEmCurso p, FaseArquivo fase, string motivo, string? paraOAmigo = null)
    {
        if (p.Terminado) return;
        _ = _room?.SendTo(p.AmigoId, MensagemArquivo.Simples(MensagemArquivo.Cancelado, p.Pedido, paraOAmigo ?? motivo));
        TerminarPedido(p, fase, motivo);
    }

    /// <summary>O fim de um pedido, qualquer que seja: para o que estiver rodando, fecha a ligação e registra no histórico e na telemetria.</summary>
    void TerminarPedido(PedidoEmCurso p, FaseArquivo fase, string? motivo)
    {
        if (p.Terminado) return;
        string? caminho = p.Link?.Tunnel.Target is { } alvo ? (alvo.Kind == CandKind.Direct && !TesteArquivoRelay ? "direto" : "relay") : null;
        p.Fase = fase; p.Motivo = motivo; p.TerminouEm = DateTime.Now;
        p.Parar.Cancel();
        p.RespostaRelay?.TrySetResult(false);
        FecharLigacaoArquivo(p);
        RegistrarPedidoNoHistorico(p);
        TelemetriaDoPedido(p, caminho);
        Log.Info($"[arquivo] pedido {p.Pedido} terminou: {fase}.");
        AvisarMudancaArquivos();
    }

    void FecharLigacaoArquivo(PedidoEmCurso p)
    {
        var (envio, link) = (p.Envio, p.Link);
        p.Envio = null; p.Link = null;
        Task.Run(() => { envio?.Dispose(); link?.Dispose(); });
    }

    /// <summary>
    /// Uma linha por arquivo. Quem recebe sabe arquivo a arquivo (o que já chegou tem caminho); quem manda só sabe o pedido inteiro.
    /// O "pedir de novo" que não chegou a ser oferecido não vira linha.
    /// </summary>
    void RegistrarPedidoNoHistorico(PedidoEmCurso p)
    {
        if (p.Fase == FaseArquivo.PrecisaAtualizar && p.Sentido == Sentido.Recebido) return;
        foreach (var a in p.Oferta.Arquivos)
        {
            bool chegou = p.Sentido == Sentido.Recebido && p.Caminhos.ContainsKey(a.Identidade);
            var resultado = chegou ? Resultado.Conferido : p.ParaHistorico();
            HistoricoArquivos.Registrar(new ItemHistorico(p.Sentido, p.Pedido, p.AmigoId, p.Apelido, a.Nome, a.Tamanho, a.Identidade,
                DateTime.Now, resultado, p.Caminhos.GetValueOrDefault(a.Identidade)));
        }
    }

    /// <summary>Sem nome de arquivo, sem tamanho exato e sem o motivo (que traz apelidos): só a faixa, a fase e o caminho (critério 15).</summary>
    void TelemetriaDoPedido(PedidoEmCurso p, string? caminho)
    {
        _teleSessao?.Evento("arquivo_fim", new JsonObject
        {
            ["sentido"] = p.Sentido == Sentido.Enviado ? "enviado" : "recebido",
            ["arquivos"] = p.Oferta.Arquivos.Count,
            ["faixa"] = PedidoEmCurso.Faixa(p.Oferta.Total),
            ["segundos"] = Math.Round(p.Relogio.Elapsed.TotalSeconds, 1),
            ["caminho"] = caminho,
            ["resultado"] = p.Fase.ToString(),
            ["pedacos_refeitos"] = p.PedacosRefeitos,
        });
    }
}
