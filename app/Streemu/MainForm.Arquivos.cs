using System.Text.Json.Nodes;
using Streemu.Arquivos;
using Streemu.Filme;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// Enviar arquivo para um amigo (spec 020): as mensagens da sala e as ações da tela (oferecer, aceitar, recusar, cancelar, pedir de
/// novo). A ligação e a transferência ficam em MainForm.ArquivosLigacao.cs. Fora do MainForm.cs para dar menos conflito.
/// Liberado para o grupo para o teste de campo (plano T8). STREEMU_ARQUIVOS=0 é o interruptor de emergência: o app deixa de
/// oferecer e de responder, e para quem manda isso parece uma versão antiga ("precisa atualizar").
/// </summary>
public partial class MainForm
{
    static readonly bool ArquivosLigado = Environment.GetEnvironmentVariable("STREEMU_ARQUIVOS") != "0";
    static readonly TimeSpan GuardarTerminado = TimeSpan.FromMinutes(2);

    readonly Dictionary<string, PedidoEmCurso> _pedidosArquivo = new();
    Historico? _historicoArquivos;
    System.Windows.Forms.Timer? _arquivosTimer;

    /// <summary>Algum pedido mudou de fase ou de progresso (a tela da T6 redesenha por aqui).</summary>
    internal event Action? ArquivosMudaram;

    Historico HistoricoArquivos => _historicoArquivos ??= new Historico(Historico.CaminhoPadrao(), () => DateTime.Now, Log.Info);

    internal IReadOnlyList<PedidoEmCurso> PedidosArquivo => _pedidosArquivo.Values.ToList();

    void AvisarMudancaArquivos() => ArquivosMudaram?.Invoke();

    /// <summary>Liga o relógio de 1 s (prazos e progresso) no primeiro uso: sem a seção ligada, nada roda.</summary>
    void IniciarArquivosSeFaltar()
    {
        if (_arquivosTimer != null) return;
        _arquivosTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _arquivosTimer.Tick += (_, _) => ArquivosTick();
        _arquivosTimer.Start();
    }

    // ------------------------------------------------------------------ sala

    /// <summary>Mensagens arquivo_* da sala (chamado pelo OnRoomMessage do MainForm.Grupo.cs).</summary>
    void OnArquivoSala(string from, string nick, JsonObject msg)
    {
        if (!ArquivosLigado) return;
        IniciarArquivosSeFaltar();
        string tipo = Tipo(msg, "tipo");
        if (tipo == MensagemArquivo.Oferta) { OfertaChegou(from, nick, msg); return; }
        if (tipo == MensagemArquivo.DeNovo) { DeNovoChegou(from, nick, msg); return; }
        if (MensagemArquivo.PedidoDe(msg) is not string id || !_pedidosArquivo.TryGetValue(id, out var p) || p.AmigoId != from) return;
        RespostaDoPedido(p, tipo, msg);
        AvisarMudancaArquivos();
    }

    void RespostaDoPedido(PedidoEmCurso p, string tipo, JsonObject msg)
    {
        switch (tipo)
        {
            case MensagemArquivo.Visto: p.Visto = true; break;
            case MensagemArquivo.Aceito when p.Sentido == Sentido.Enviado: _ = AceitoPeloAmigoAsync(p, msg); break;
            case MensagemArquivo.Ligar when p.Sentido == Sentido.Recebido: LigarAoRemetente(p, msg); break;
            case MensagemArquivo.Recusado: TerminarPedido(p, FaseArquivo.Recusado, MensagemArquivo.MotivoDe(msg) ?? $"{p.Apelido} recusou.", "outro_recusou"); break;
            case MensagemArquivo.Cancelado: TerminarPedido(p, FaseArquivo.Cancelado, MensagemArquivo.MotivoDe(msg) ?? $"{p.Apelido} cancelou.", "outro_cancelou"); break;
            case MensagemArquivo.Concluido when p.Sentido == Sentido.Enviado: TerminarPedido(p, FaseArquivo.Conferido, null); break;
        }
    }

    /// <summary>Quem recebe: responde "visto" na hora (é o que diz a quem manda que esta versão entende o pedido) e guarda a oferta.</summary>
    void OfertaChegou(string from, string nick, JsonObject msg)
    {
        if (MensagemArquivo.LerOferta(msg) is not OfertaDeArquivos oferta) { Log.Info($"[arquivo] {nick} mandou uma oferta fora do formato; ignorei."); return; }
        _ = _room?.SendTo(from, MensagemArquivo.Simples(MensagemArquivo.Visto, oferta.Pedido));
        if (_pedidosArquivo.TryGetValue(oferta.Pedido, out var meu)) { OfertaDoMeuDeNovo(meu, from, oferta); return; }
        _pedidosArquivo[oferta.Pedido] = new PedidoEmCurso(Sentido.Recebido, from, nick, oferta, DateTime.Now);
        Log.Info($"[arquivo] {nick} quer mandar {oferta.Arquivos.Count} arquivo(s), {oferta.Total / 1e6:N1} MB.");
        AvisarMudancaArquivos();
    }

    /// <summary>A oferta é a resposta a um "pedir de novo" meu: a pessoa já disse que quer, então aceita sem perguntar.</summary>
    void OfertaDoMeuDeNovo(PedidoEmCurso meu, string from, OfertaDeArquivos oferta)
    {
        if (meu.Sentido != Sentido.Recebido || meu.AmigoId != from || meu.Fase != FaseArquivo.AguardandoDeNovo) return;
        meu.Oferta = oferta;
        meu.Fase = FaseArquivo.Esperando;
        _ = AceitarPedidoArquivoAsync(meu.Pedido);
    }

    /// <summary>Quem mandou: o amigo pede de novo pelo histórico. Só vale para um arquivo que EU mandei para ELE (D6).</summary>
    void DeNovoChegou(string from, string nick, JsonObject msg)
    {
        if (MensagemArquivo.LerDeNovo(msg) is not { } deNovo || _pedidosArquivo.ContainsKey(deNovo.Pedido)) return;
        var (pedido, arquivo) = deNovo;
        _ = _room?.SendTo(from, MensagemArquivo.Simples(MensagemArquivo.Visto, pedido));
        var p = new PedidoEmCurso(Sentido.Enviado, from, nick, new OfertaDeArquivos(pedido, [arquivo]), DateTime.Now) { Fase = FaseArquivo.PedidoDeNovo, Visto = true };
        if (CaminhoEnviado(from, arquivo.Identidade) is string caminho) p.Caminhos[arquivo.Identidade] = caminho;
        _pedidosArquivo[pedido] = p;
        Log.Info($"[arquivo] {nick} pediu de novo um arquivo de {arquivo.Tamanho / 1e6:N1} MB.");
        AvisarMudancaArquivos();
    }

    string? CaminhoEnviado(string amigoId, string identidade) =>
        HistoricoArquivos.Itens.FirstOrDefault(i => i.Sentido == Sentido.Enviado && i.AmigoId == amigoId && i.Identidade == identidade)?.Caminho;

    // ------------------------------------------------------------------ ações (a tela da T6 e o teste da T5 chamam)

    /// <summary>
    /// Quem manda: oferece os arquivos a um amigo da sala. Devolve o motivo, já para a tela, se não deu; nulo se a oferta saiu.
    /// Ex.: <c>await OferecerArquivosAsync(amigo.Id, [@"C:\mumu.png"])</c>.
    /// </summary>
    internal async Task<string?> OferecerArquivosAsync(string amigoId, IReadOnlyList<string> caminhos, string? pedido = null)
    {
        if (DestinoDoEnvio(amigoId, caminhos) is string erro) return erro;
        IniciarArquivosSeFaltar();
        OfertaDeArquivos oferta;
        try { oferta = await Task.Run(() => MontarOferta(pedido ?? RandomId(12), caminhos)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"Não consegui ler um dos arquivos: {e.Message}"; }
        if (oferta.Arquivos.FirstOrDefault(a => a.Tamanho == 0 || a.Nome.Length > MensagemArquivo.MaxNome) is { } ruim)
            return ruim.Tamanho == 0 ? $"\"{ruim.Nome}\" está vazio." : "Um dos nomes de arquivo é longo demais. Encurte e tente de novo.";
        var p = PedidoEnviado(amigoId, oferta, caminhos);
        await _room!.SendTo(amigoId, MensagemArquivo.CriarOferta(oferta));
        Log.Info($"[arquivo] ofereci {oferta.Arquivos.Count} arquivo(s), {oferta.Total / 1e6:N1} MB, a {p.Apelido}.");
        AvisarMudancaArquivos();
        return null;
    }

    /// <summary>Guarda o pedido de quem manda; <paramref name="caminhos"/> vem na mesma ordem dos arquivos da oferta.</summary>
    PedidoEmCurso PedidoEnviado(string amigoId, OfertaDeArquivos oferta, IReadOnlyList<string> caminhos)
    {
        var p = new PedidoEmCurso(Sentido.Enviado, amigoId, _room?.Find(amigoId)?.Nick ?? "amigo", oferta, DateTime.Now);
        for (int i = 0; i < oferta.Arquivos.Count; i++) p.Caminhos[oferta.Arquivos[i].Identidade] = caminhos[i];
        _pedidosArquivo[oferta.Pedido] = p;
        return p;
    }

    string? DestinoDoEnvio(string amigoId, IReadOnlyList<string> caminhos)
    {
        if (!ArquivosLigado) return "O envio de arquivo ainda não está liberado.";
        // Friends, não Find: o Find acha também a mim mesmo
        if (_room is not { Connected: true } room || room.Friends.All(f => f.Id != amigoId)) return "Esse amigo não está na sala agora.";
        if (caminhos.Count == 0 || caminhos.Count > MensagemArquivo.MaxArquivos) return $"Escolha de 1 a {MensagemArquivo.MaxArquivos} arquivos.";
        return null;
    }

    static OfertaDeArquivos MontarOferta(string pedido, IReadOnlyList<string> caminhos) =>
        new(pedido, caminhos.Select(c => new ArquivoOferecido(Path.GetFileName(c), new FileInfo(c).Length, Identidade.De(c))).ToList());

    /// <summary>Quem recebe: aceita. Confere o espaço antes de qualquer coisa (critério 8) e abre a ligação; o resto vem com o "ligar".</summary>
    internal async Task<string?> AceitarPedidoArquivoAsync(string pedido)
    {
        if (!_pedidosArquivo.TryGetValue(pedido, out var p) || p.Sentido != Sentido.Recebido || p.Fase != FaseArquivo.Esperando)
            return "Esse pedido não está mais esperando.";
        if (await Task.Run(() => FaltaEspacoPara(p, HistoricoArquivos.PastaRecebidos)) is string semEspaco) return semEspaco;
        p.Fase = FaseArquivo.Conectando;
        AvisarMudancaArquivos();
        if (await AbrirLigacaoArquivoAsync(p, iAmSender: false) is string erro) { EncerrarEAvisar(p, FaseArquivo.Falhou, erro); return erro; }
        if (p.Terminado) { FecharLigacaoArquivo(p); return null; }
        await _room!.SendTo(p.AmigoId, MensagemArquivo.ComCodigo(MensagemArquivo.Aceito, p.Pedido, p.Link!.MyCode));
        return null;
    }

    static string? FaltaEspacoPara(PedidoEmCurso p, string pasta)
    {
        try { Directory.CreateDirectory(pasta); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"Não consegui usar a pasta de recebidos ({e.Message}). Escolha outra."; }
        return Download.FaltaEspaco(pasta, p.Oferta.Total, p.Oferta.Arquivos.Count == 1 ? "o arquivo" : "os arquivos");
    }

    internal void RecusarPedidoArquivo(string pedido) =>
        EncerrarPorMim(pedido, MensagemArquivo.Recusado, FaseArquivo.Recusado, $"{Nick} recusou.", "Você recusou.", "voce_recusou");

    internal void CancelarPedidoArquivo(string pedido) =>
        EncerrarPorMim(pedido, MensagemArquivo.Cancelado, FaseArquivo.Cancelado, $"{Nick} cancelou.", "Você cancelou.", "voce_cancelou");

    void EncerrarPorMim(string pedido, string tipo, FaseArquivo fase, string paraOAmigo, string aqui, string causa)
    {
        if (!_pedidosArquivo.TryGetValue(pedido, out var p) || p.Terminado) return;
        _ = _room?.SendTo(p.AmigoId, MensagemArquivo.Simples(tipo, pedido, paraOAmigo));
        TerminarPedido(p, fase, aqui, causa);
    }

    /// <summary>Quem recebe, pelo histórico: pede o mesmo arquivo de novo (D6). A oferta que voltar é aceita sozinha.</summary>
    internal async Task<string?> PedirArquivoDeNovoAsync(ItemHistorico item)
    {
        if (!ArquivosLigado) return "O envio de arquivo ainda não está liberado.";
        if (_room is not { Connected: true } room || room.Find(item.AmigoId) == null) return $"{item.Apelido} não está na sala agora.";
        IniciarArquivosSeFaltar();
        var arquivo = new ArquivoOferecido(item.Nome, item.Tamanho, item.Identidade);
        var p = new PedidoEmCurso(Sentido.Recebido, item.AmigoId, item.Apelido, new OfertaDeArquivos(RandomId(12), [arquivo]), DateTime.Now)
        { Fase = FaseArquivo.AguardandoDeNovo };
        _pedidosArquivo[p.Pedido] = p;
        await room.SendTo(item.AmigoId, MensagemArquivo.CriarDeNovo(p.Pedido, arquivo));
        AvisarMudancaArquivos();
        return null;
    }

    /// <summary>Quem mandou: atende o "pedir de novo", se o arquivo ainda está no mesmo lugar e igual; senão, recusa com o motivo.</summary>
    internal async Task<string?> MandarDeNovoAsync(string pedido)
    {
        if (!_pedidosArquivo.TryGetValue(pedido, out var p) || p.Fase != FaseArquivo.PedidoDeNovo) return "Esse pedido não está mais esperando.";
        var arquivo = p.Oferta.Arquivos[0];
        string? caminho = p.Caminhos.GetValueOrDefault(arquivo.Identidade);
        if (caminho == null || !await Task.Run(() => AindaIgual(caminho, arquivo.Identidade)))
        {
            EncerrarPorMim(pedido, MensagemArquivo.Recusado, FaseArquivo.Recusado, $"O arquivo mudou ou não está mais no PC de {Nick}.", "O arquivo mudou ou saiu do lugar.", "arquivo_mudou");
            return "O arquivo mudou ou saiu do lugar desde que você mandou.";
        }
        _pedidosArquivo.Remove(pedido);
        string? erro = await OferecerArquivosAsync(p.AmigoId, [caminho], pedido);
        // sem isso, quem pediu ficaria 10 min esperando uma oferta que não vem
        if (erro != null) _ = _room?.SendTo(p.AmigoId, MensagemArquivo.Simples(MensagemArquivo.Recusado, pedido, $"{Nick} não conseguiu mandar de novo agora."));
        return erro;
    }

    static bool AindaIgual(string caminho, string identidade)
    {
        try { return File.Exists(caminho) && Identidade.De(caminho) == identidade; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    // ------------------------------------------------------------------ prazos

    void ArquivosTick()
    {
        var agora = DateTime.Now;
        foreach (var p in _pedidosArquivo.Values.ToList())
        {
            if (p.Terminado) { if (agora - p.TerminouEm > GuardarTerminado) _pedidosArquivo.Remove(p.Pedido); continue; }
            if (p.SemVisto(agora)) TerminarPedido(p, FaseArquivo.PrecisaAtualizar, MotivoSemVisto(p), _room?.IsPresent(p.AmigoId) == true ? "versao_antiga" : "saiu_da_sala");
            else if (p.Venceu(agora)) ExpirarPedido(p);
            else if (LigacaoArquivoCaiu(p)) TerminarPedido(p, p.Fase == FaseArquivo.Conectando ? FaseArquivo.Falhou : FaseArquivo.Parou, $"A ligação com {p.Apelido} caiu. O que já veio fica guardado.", "ligacao_caiu");
            else AtualizarProgressoDoEnvio(p);
        }
        if (_pedidosArquivo.Values.Any(p => !p.Terminado)) AvisarMudancaArquivos();
    }

    string MotivoSemVisto(PedidoEmCurso p) => _room?.IsPresent(p.AmigoId) == true
        ? $"{p.Apelido} precisa atualizar o {UiBrand.Name} para trocar arquivos."
        : $"{p.Apelido} saiu da sala.";

    /// <summary>
    /// D4: 10 min sem resposta. Pedido esperando aceite expira sozinho dos dois lados, com o mesmo prazo: avisar o outro faria
    /// ele terminar em "cancelado" se o aviso chegasse antes do prazo dele. Só quem recebe parado no aviso de cota avisa, porque do
    /// outro lado a ligação está aberta e sem prazo.
    /// </summary>
    void ExpirarPedido(PedidoEmCurso p)
    {
        if (p.Fase == FaseArquivo.ConfirmandoRelay)
            _ = _room?.SendTo(p.AmigoId, MensagemArquivo.Simples(MensagemArquivo.Cancelado, p.Pedido, "O pedido expirou."));
        // lista fechada: de quem era a vez de responder (o amigo viu a oferta mas não aceitou; eu não respondi à dele; a cota do relay; o pedir de novo sem oferta de volta)
        string causa = p.Fase switch
        {
            FaseArquivo.ConfirmandoRelay => "cota_sem_resposta",
            FaseArquivo.AguardandoDeNovo => "pedido_de_novo_sem_oferta",
            FaseArquivo.PedidoDeNovo => "voce_nao_respondeu",
            _ => p.Sentido == Sentido.Enviado ? "outro_nao_aceitou" : "voce_nao_aceitou",
        };
        TerminarPedido(p, FaseArquivo.Expirou, "O pedido expirou: ninguém respondeu em 10 min.", causa);
    }
}
