using System.Text.Json.Nodes;
using Streemu.Util;

namespace Streemu.Filme;

/// <summary>
/// A fila de reprodução do Cinema (pedido de 2026-11-18, numa sessão de vários episódios): o anfitrião monta a fila, o episódio seguinte começa sozinho no fim do
/// anterior e todos passam junto. Separado do FilmeTela.cs, que já é grande.
/// <para>
/// <b>A troca não fecha nada.</b> O mpv, as ligações, o anúncio da sala e a transmissão continuam: quem assiste não é desligado e religado a cada episódio. Ordem (anfitrião,
/// <see cref="TrocarArquivoAsync"/>): 1) fora da janela, a identidade do arquivo novo e o que o ffmpeg diz dele; 2) <c>filme_trocar</c> para todos ANTES de mudar, para quem tem
/// o arquivo ir buscá-lo na mesma pasta enquanto o anfitrião também carrega; 3) o mesmo mpv abre o arquivo novo, parado em 0; 4) num trecho só, sem espera: a <see cref="Sessao"/>
/// nova (herda quem entrou e as permissões), o anúncio da sala e o envio da transmissão passam para o arquivo novo; 5) a largada sincronizada do pulo D4
/// (<see cref="Sessao.ComecarDaFilaAsync"/>): todos pausam em 0, avisam que carregaram (até 5 s) e o anfitrião toca com a hora marcada.
/// </para>
/// </summary>
public sealed partial class FilmeTela
{
    readonly Fila _fila = new();
    readonly Ui.FilaFilmeView _filaView = new() { Dock = DockStyle.Right, Width = 300, Visible = false };
    readonly System.Windows.Forms.Timer _difusao = new() { Interval = 1000 };
    bool _filaAberta, _trocando;
    (int Indice, string Motivo)? _trocaPendente;
    /// <summary>Id do arquivo para o qual estou trocando: as mensagens dele que chegam antes de a Sessão nova existir ficam retidas e entram depois (senão o filtro por id as jogava fora).</summary>
    string? _trocandoPara;
    readonly List<JsonObject> _retidas = new();
    /// <summary>O arquivo que eu abri (quem assiste procura o próximo episódio na mesma pasta dele).</summary>
    string? _caminhoArquivo;
    // quem assiste com o arquivo: o que o anfitrião mandou (filme_fila e filme_pessoas); sem isso (anfitrião em versão antiga), a lista é a de antes
    string[] _filaDoAnfitriao = [];
    int _filaDoAnfitriaoAtual = -1;
    List<MpvUi.Pessoa>? _pessoasDoAnfitriao;
    string? _sigPessoas, _sigFila;

    /// <summary>Antes da troca de episódio com a transmissão ligada: o MainForm analisa o arquivo novo (ffmpeg, fora da janela); nulo = sem transmissão ou não deu.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string, Task<FonteInfo?>>? AnalisarParaTransmissao { get; set; }
    /// <summary>Na troca, no mesmo trecho em que a Sessão é trocada: o MainForm passa o envio da transmissão (se ligada) e o que serve o arquivo a quem baixa para o arquivo novo (a análise de <see cref="AnalisarParaTransmissao"/> e a faixa de áudio do mpv).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<FonteInfo?, FaixaAudio?>? AnfitriaoTrocouArquivo { get; set; }

    /// <summary>Quantas vezes a fila passou para o episódio seguinte nesta sessão (relatório do teste).</summary>
    public int TrocasDaFila { get; private set; }
    public IReadOnlyList<string> FilaItens => _fila.Itens;
    /// <summary>Os nomes que a lista "Na sessão" do player mostra agora (teste: quem assiste pela transmissão tem que aparecer para todos).</summary>
    public IReadOnlyList<string> NomesNaSessao => Sessao is { } s ? PessoasDaSessao(s).Select(p => p.Nome).ToList() : [];
    public int FilaIndice => _fila.Atual;

    void InitFila()
    {
        _filaView.Tocar += i => { if (Sessao is { Anfitriao: true }) _ = TrocarArquivoAsync(i, "item"); };
        _filaView.Remover += i => { if (Sessao is { Anfitriao: true } && _fila.Remover(i)) FilaMudou(); };
        _filaView.Mover += (de, para) => { if (Sessao is { Anfitriao: true } && _fila.Mover(de, para)) FilaMudou(); };
        _filaView.Anterior += () => _ = AnteriorAsync();
        _filaView.Proximo += () => _ = ProximoAsync();
        _filaView.Adicionar += AdicionarPeloDialogo;
        _filaView.Fechar += () => AlternarFila(false);
        _difusao.Tick += (_, _) => Difundir();
    }

    // ------------------------------------------------------------------ montar a fila (anfitrião)

    const string FiltroVideos = "Vídeos|*.mkv;*.mp4;*.avi;*.mov;*.webm;*.m4v;*.ts|Todos os arquivos|*.*";

    /// <summary>Acrescenta episódios ao fim da fila (só o anfitrião), na ordem natural dos nomes.</summary>
    public void AdicionarNaFila(IEnumerable<string> caminhos)
    {
        if (Sessao is not { Anfitriao: true }) return;
        int n = _fila.Adicionar(caminhos);
        if (n > 0) FilaMudou();
    }

    void AdicionarPeloDialogo()
    {
        if (Sessao is not { Anfitriao: true }) return;
        using var dlg = new OpenFileDialog { Title = "Acrescentar à fila", Filter = FiltroVideos, Multiselect = true };
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK) AdicionarNaFila(dlg.FileNames);
    }

    void FilaMudou()
    {
        Atualizar();
        Difundir();
    }

    void AlternarFila(bool? abrir = null)
    {
        if (Sessao == null) return;
        bool quer = abrir ?? !_filaAberta;
        if (quer && Sessao is { Anfitriao: false } && _filaDoAnfitriao.Length == 0) { Avisar("Não há fila nesta sessão."); return; }
        _filaAberta = quer;
        Atualizar();
    }

    /// <summary>O painel da fila: encolhe o vídeo só enquanto está aberto (quem assiste a pediu). Chamado no fim do Atualizar.</summary>
    void AtualizarFila(Sessao? s)
    {
        bool mostrar = _filaAberta && s != null && !_catalogo;
        if (s is { Anfitriao: true }) _filaView.Mostrar(_fila.Nomes, _fila.Atual, true, _lig.MeuNome);
        else if (s != null) _filaView.Mostrar(_filaDoAnfitriao, _filaDoAnfitriaoAtual, false, s.NomeAnfitriao);
        if (mostrar) _filaView.Width = UiScale.Dp(this, 300);
        if (_filaView.Visible != mostrar) _filaView.Visible = mostrar;
    }

    /// <summary>O que o Lua desenha: ⏮ e ⏭ (só do anfitrião, quando há mais de um item) e o botão Fila.</summary>
    JsonObject? FilaParaInterface(Sessao s)
    {
        if (s.Anfitriao)
            return new JsonObject { ["total"] = _fila.Total, ["indice"] = _fila.Atual + 1, ["temAnterior"] = _fila.TemAnterior, ["temProximo"] = _fila.TemProximo, ["aberta"] = _filaAberta };
        return _filaDoAnfitriao.Length > 0
            ? new JsonObject { ["total"] = _filaDoAnfitriao.Length, ["indice"] = _filaDoAnfitriaoAtual + 1, ["aberta"] = _filaAberta } : null;
    }

    string? LinhaDaFila(Sessao s)
    {
        int total = s.Anfitriao ? _fila.Total : _filaDoAnfitriao.Length, indice = s.Anfitriao ? _fila.Atual : _filaDoAnfitriaoAtual;
        return total > 1 && indice >= 0 ? $"Episódio {indice + 1} de {total}" : null;
    }

    // ------------------------------------------------------------------ pedir a troca

    public Task ProximoAsync(string motivo = "proximo") => _fila.TemProximo ? TrocarArquivoAsync(_fila.Atual + 1, motivo) : Task.CompletedTask;
    public Task AnteriorAsync() => _fila.TemAnterior ? TrocarArquivoAsync(_fila.Atual - 1, "anterior") : Task.CompletedTask;

    /// <summary>
    /// O fim do episódio, visto pelo anfitrião: o mpv chegou ao fim do arquivo (keep-open o deixa parado ali) com o filme tocando. Uma leitura a cada 400 ms só enquanto há próximo
    /// na fila. O "fim" é a posição a menos de 0,3 s da duração ou o eof-reached do mpv: o que vier primeiro (os dois podem faltar um instante depois do loadfile).
    /// </summary>
    async Task VigiarFimAsync(Sessao s, Mpv mpv)
    {
        while (ReferenceEquals(Sessao, s) && !_disposing && !IsDisposed && !mpv.Exited)
        {
            await Task.Delay(400);
            if (!ReferenceEquals(Sessao, s) || _trocando || !_fila.TemProximo || !s.Tocando) continue;
            bool fim = await mpv.BoolAsync("eof-reached");
            if (!fim)
            {
                double dur = await mpv.NumAsync("duration"), pos = await mpv.NumAsync("time-pos");
                fim = !double.IsNaN(dur) && !double.IsNaN(pos) && dur > 1 && pos >= dur - 0.3;
            }
            if (!fim || !ReferenceEquals(Sessao, s) || _trocando || !s.Tocando) continue;
            Log.Info("[filme] fim do episódio: passando para o próximo da fila.");
            Ui(() => { if (ReferenceEquals(Sessao, s)) _ = ProximoAsync("fim"); });
            await Task.Delay(1500); // a troca começa na thread da janela; não ler o mesmo fim duas vezes
        }
    }

    /// <summary>
    /// Anfitrião: passa para o arquivo <paramref name="indice"/> da fila. Pedido durante outra troca: o último vence (vale depois de a troca em andamento chegar ao ponto de não
    /// volta). Se o arquivo não abre, a fila e o episódio atual ficam como estavam.
    /// </summary>
    async Task TrocarArquivoAsync(int indice, string motivo)
    {
        if (Sessao is not { Anfitriao: true } velha || Player is not { Exited: false } mpv || indice < 0 || indice >= _fila.Total) return;
        if (_trocando) { _trocaPendente = (indice, motivo); return; }
        _trocando = true;
        int de = _fila.Atual;
        string caminho = _fila.Itens[indice], arquivo = Path.GetFileName(caminho), nome = Path.GetFileNameWithoutExtension(caminho);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        FaixaAudio? faixa = null;
        FonteInfo? info = null;
        string id;
        try
        {
            // 1) fora da janela: a identidade e, com a transmissão ligada, a leitura do ffmpeg (o envio só troca de arquivo depois de saber como mandá-lo)
            try
            {
                id = await Task.Run(() => Identidade.De(caminho));
                if (AnalisarParaTransmissao is { } analisar) info = await analisar(caminho);
            }
            catch (Exception e)
            {
                Avisar($"Não consegui abrir \"{nome}\" ({e.Message}). O episódio atual continua; confira se o arquivo ainda está lá.");
                Log.Info($"[filme] fila: não deu para ler o arquivo do próximo episódio: {e.Message}");
                _trocando = false;
                return;
            }
            if (!ReferenceEquals(Sessao, velha) || _disposing || IsDisposed) { _trocando = false; return; }
            _fila.Definir(indice); // já vale: um segundo "Próximo" logo em seguida vai para o item depois deste
            Atualizar();

            // 2) o episódio velho acaba aqui: telemetria (as ligações seguem abertas) e o aviso a todos, ANTES de mudar qualquer coisa
            var rel = velha.Relatorio();
            rel["filme"] = velha.Filme; rel["como"] = _como; rel["fila"] = true; rel["motivo_fim"] = motivo;
            rel["minutos"] = Math.Round((DateTime.Now - _inicioSessao).TotalMinutes, 1);
            rel["amigos"] = string.Join(",", velha.Amigos.Select(kv => $"{kv.Key}={kv.Value}"));
            Telemetria?.Invoke("filme_fim", rel);
            velha.Soltar();
            _trocandoPara = id;
            _lig.Enviar(new JsonObject
            {
                ["type"] = "filme_trocar", ["filme"] = velha.Filme, ["para"] = id, ["nome"] = nome, ["arquivo"] = arquivo,
                ["duracao"] = info?.DuracaoS ?? 0, ["indice"] = indice + 1, ["total"] = _fila.Total,
            });
            Log.Info($"[filme] fila: passando para o episódio {indice + 1} de {_fila.Total} ({motivo}); as ligações continuam.");

            // 3) o mesmo mpv abre o arquivo novo, parado em 0
            await Task.Delay(100); // uma aplicação do estado velho ainda em curso lê a geração e desiste
            await mpv.CarregarAsync(caminho);
            if (info != null) faixa = await Fonte.FaixaDoMpvAsync(mpv); // a faixa de áudio que o mpv escolheu no arquivo novo: o envio já abre com ela
            if (!ReferenceEquals(Sessao, velha) || _disposing || IsDisposed) { _trocando = false; _trocandoPara = null; _retidas.Clear(); return; }
        }
        catch (Exception e)
        {
            // o player não abriu o arquivo novo e os outros já foram avisados: sem o que mostrar, o jeito limpo é encerrar a sessão (filme_fim para todos)
            Log.Info($"[filme] fila: o player não abriu o próximo episódio: {e.Message}");
            _trocando = false; _trocandoPara = null; _retidas.Clear();
            if (ReferenceEquals(Sessao, velha))
            {
                Avisar($"O player não abriu \"{nome}\": {e.Message}. A sessão foi encerrada.");
                SairDoFilme();
            }
            return;
        }

        // 4) a troca, num trecho só e sem esperar nada: Sessão, anúncio da sala e transmissão passam juntas para o arquivo novo
        var nova = new Sessao(true, id, nome, velha.NomeAnfitriao, _lig.MeuNome, mpv, () => Net.Tunnel.NowUs, _lig.IdaVolta, _lig.Enviar);
        nova.HerdarDe(velha);
        Sessao = UltimaSessao = nova;
        ArquivoAnfitriao = _caminhoArquivo = caminho;
        _inicioSessao = DateTime.Now;
        TrocasDaFila++;
        LigarSessao(nova);
        // o anúncio com outro id não fecha as ligações; só o nulo fecha. "fila": o próximo episódio não conta como "abriu um filme" no painel de atividades
        // (fica só no anúncio local: o AnuncioSala copia os campos um a um e não leva este)
        AnunciarMeu(nova, new JsonObject { ["filme"] = id, ["nome"] = nome, ["anfitriao"] = _lig.MeuNome, ["fila"] = true });
        AnfitriaoTrocouArquivo?.Invoke(info, faixa);
        _trocandoPara = null;
        foreach (var m in _retidas.ToList()) Receber(m); // pelo Receber, não direto na Sessão: a lista de pessoas e a fila também esperavam aqui
        _retidas.Clear();
        _ = VigiarFimAsync(nova, mpv);
        Difundir(forcar: true);
        Atualizar();
        _trocando = false;
        if (_trocaPendente is { } pendente)
        {
            // alguém pediu outro episódio no meio da troca: este nem chega a tocar
            _trocaPendente = null;
            _ = TrocarArquivoAsync(pendente.Indice, pendente.Motivo);
            return;
        }

        // 5) a largada sincronizada (D4): todos pausam em 0, carregam e o anfitrião toca com a hora marcada
        await nova.ComecarDaFilaAsync();
        long espera = nova.UltimaEsperaPuloMs;
        bool completa = ReferenceEquals(Sessao, nova) && espera >= 0;
        Telemetria?.Invoke("filme_fila", new JsonObject
        {
            ["de"] = velha.Filme, ["para"] = id, ["motivo"] = motivo, ["indice"] = indice + 1, ["total"] = _fila.Total,
            ["espera_ms"] = completa ? espera : -1, ["quem_atrasou"] = completa ? nova.UltimoAtraso : null, ["troca_ms"] = sw.ElapsedMilliseconds,
        });
        Log.Info($"[filme] fila: episódio {indice + 1} começou" + (completa ? $"; todos carregaram em {espera} ms" + (nova.UltimoAtraso != null ? $" (segui sem {nova.UltimoAtraso})." : ".") : " (interrompido por outro pedido)."));
    }

    // ------------------------------------------------------------------ quem assiste com o arquivo: o anfitrião passou para o próximo

    /// <summary>
    /// filme_trocar: procura o arquivo com o mesmo nome na pasta do atual e confere a identidade; se bate, o mesmo mpv abre o arquivo novo e a Sessão nova segue o anfitrião, na
    /// mesma ligação. Sem o arquivo (ou outra versão), sai da sessão de forma limpa e volta ao Cinema, onde o anúncio novo já está.
    /// </summary>
    async Task ReceberTrocarAsync(JsonObject m)
    {
        if (Sessao is not { Anfitriao: false } velha || m["filme"]?.GetValue<string>() != velha.Filme || _trocando || Player is not { Exited: false } mpv) return;
        string? para = m["para"]?.GetValue<string>(), arquivo = m["arquivo"]?.GetValue<string>();
        string nome = m["nome"]?.GetValue<string>() ?? "o próximo episódio";
        if (!Identidade.Valida(para)) return;
        _trocando = true; _trocandoPara = para;
        string? caminho = null;
        try
        {
            // o nome vem de outro PC: só vale como nome de arquivo, nunca como caminho
            if (_caminhoArquivo != null && arquivo is { Length: > 0 } && arquivo == Path.GetFileName(arquivo) && arquivo.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            {
                string c = Path.Combine(Path.GetDirectoryName(_caminhoArquivo) ?? "", arquivo), idEsperado = para!;
                caminho = await Task.Run(() => { try { return File.Exists(c) && Identidade.De(c) == idEsperado ? c : null; } catch { return null; } });
            }
            if (!ReferenceEquals(Sessao, velha) || _disposing || IsDisposed) return;
            if (caminho == null)
            {
                string dono = velha.NomeAnfitriao;
                Log.Info($"[filme] fila: o anfitrião passou para o próximo episódio e eu não tenho o arquivo; saindo da sessão.");
                _trocando = false; _trocandoPara = null; _retidas.Clear();
                Encerrar();
                MostrarCatalogo();
                Avisar($"{dono} passou para \"{nome}\". Você não tem esse arquivo: assista sem baixar ou baixe.");
                return;
            }
            var rel = velha.Relatorio();
            rel["filme"] = velha.Filme; rel["como"] = _como; rel["fila"] = true; rel["minutos"] = Math.Round((DateTime.Now - _inicioSessao).TotalMinutes, 1);
            Telemetria?.Invoke("filme_fim", rel);
            velha.Soltar();
            await Task.Delay(100);
            await mpv.CarregarAsync(caminho);
            if (!ReferenceEquals(Sessao, velha) || _disposing || IsDisposed) return;
            var tun = _lig.TunelAnfitriao();
            Func<long?> relogio = () => _lig.OffsetAnfitriaoUs() is long o ? Net.Tunnel.NowUs + o : null;
            var nova = new Sessao(false, para!, nome, velha.NomeAnfitriao, _lig.MeuNome, mpv, relogio, _lig.IdaVolta, _lig.Enviar);
            Sessao = UltimaSessao = nova;
            _caminhoArquivo = caminho;
            _inicioSessao = DateTime.Now;
            TrocasDaFila++;
            LigarSessao(nova);
            Log.Info($"[filme] fila: sigo o anfitrião para \"{nome}\" ({para}); a ligação é a mesma.");
            // o anfitrião responde com as permissões e o estado atual (o "primeiro" é o que dispara isso)
            _lig.Enviar(new JsonObject { ["type"] = "filme_pronto", ["filme"] = para, ["pronto"] = true, ["nome"] = _lig.MeuNome, ["primeiro"] = true });
            _trocandoPara = null;
            foreach (var r in _retidas.ToList()) Receber(r);
            _retidas.Clear();
            Atualizar();
        }
        catch (Exception e)
        {
            Log.Info($"[filme] fila: não deu para seguir o anfitrião para o próximo episódio: {e.Message}");
            if (ReferenceEquals(Sessao, velha)) { Avisar($"Não consegui abrir o próximo episódio ({e.Message})."); Encerrar(); MostrarCatalogo(); }
        }
        finally { _trocando = false; _trocandoPara = null; }
    }

    // ------------------------------------------------------------------ o que todos veem: "Na sessão" e a fila

    /// <summary>O anfitrião conta a todos quem está na sessão e como está a fila quando algo muda (e a quem acabou de entrar: entrar muda a lista). Sem mudança, não manda nada.</summary>
    void Difundir(bool forcar = false)
    {
        if (Sessao is not { Anfitriao: true } s) return;
        var pessoas = new JsonArray(PessoasDaSessao(s).Select(p => (JsonNode?)new JsonObject { ["nome"] = p.Nome, ["papel"] = p.Papel, ["estado"] = p.Estado, ["tom"] = p.Tom, ["junto"] = p.Junto }).ToArray());
        var fila = new JsonArray(_fila.Nomes.Select(n => (JsonNode?)n).ToArray());
        string sigP = pessoas.ToJsonString(), sigF = fila.ToJsonString() + _fila.Atual;
        if (!forcar && sigP == _sigPessoas && sigF == _sigFila) return;
        _sigPessoas = sigP; _sigFila = sigF;
        _lig.Enviar(new JsonObject { ["type"] = "filme_pessoas", ["filme"] = s.Filme, ["pessoas"] = pessoas });
        if (_fila.Ativa) _lig.Enviar(new JsonObject { ["type"] = "filme_fila", ["filme"] = s.Filme, ["itens"] = fila, ["atual"] = _fila.Atual });
    }

    /// <summary>Chamado pelo MainForm quando alguém entra ou sai pela transmissão: a lista de quem está na sessão mudou.</summary>
    public void PessoasMudaram() => Difundir();

    /// <summary>Quem assiste com o arquivo: mensagens do anfitrião sobre a fila e a lista de pessoas (já passaram pelo filtro de id).</summary>
    bool ReceberDaFila(JsonObject m)
    {
        switch (m["type"]?.GetValue<string>())
        {
            case "filme_pessoas" when Sessao is { Anfitriao: false }:
                if (m["pessoas"] is JsonArray a)
                {
                    var lista = new List<MpvUi.Pessoa>();
                    foreach (var x in a)
                        if (x is JsonObject o && o["nome"]?.GetValue<string>() is { Length: > 0 } nome)
                            lista.Add(new MpvUi.Pessoa(nome, o["papel"]?.GetValue<string>(), o["estado"]?.GetValue<string>(), o["tom"]?.GetValue<string>() ?? "", o["junto"]?.GetValue<bool>() ?? false));
                    _pessoasDoAnfitriao = lista;
                }
                return true;
            case "filme_fila" when Sessao is { Anfitriao: false }:
                _filaDoAnfitriao = m["itens"] is JsonArray itens ? itens.Select(x => x?.GetValue<string>() ?? "").ToArray() : [];
                _filaDoAnfitriaoAtual = m["atual"]?.GetValue<int>() ?? -1;
                if (_filaDoAnfitriao.Length == 0) _filaAberta = false;
                return true;
        }
        return false;
    }

    /// <summary>
    /// Ligações da Sessão com a tela. Separado do AbrirAsync porque a fila cria uma Sessão nova a cada episódio, sobre o mesmo player (os eventos do mpv, esses, ficam ligados uma vez só).
    /// </summary>
    void LigarSessao(Sessao s)
    {
        s.Mudou += () => Ui(Atualizar);
        s.Aviso += Avisar;
    }

    /// <summary>Anfitrião: conta à sala o meu filme. O card de quem ainda não entrou mostra "rolando" ou "pausado" na hora, sem esperar a repetição de 5 s do anúncio.</summary>
    void AnunciarMeu(Sessao s, JsonObject meu)
    {
        _lig.Anunciar(meu);
        bool tocava = false;
        s.Mudou += () => Ui(() => { if (Sessao == s && s.Tocando != tocava) { tocava = s.Tocando; _lig.Anunciar(meu); } });
    }

    /// <summary>Ao fechar a sessão: a fila e o que veio do anfitrião somem (a fila vale para quem abriu o filme).</summary>
    void LimparFila()
    {
        _fila.Limpar(); _filaAberta = false; _trocando = false; _trocandoPara = null; _trocaPendente = null; _retidas.Clear();
        _filaDoAnfitriao = []; _filaDoAnfitriaoAtual = -1; _pessoasDoAnfitriao = null; _sigPessoas = _sigFila = null; _caminhoArquivo = null;
        _difusao.Stop();
    }

    /// <summary>
    /// Mensagens que o filtro por id do Receber não pode ver: o filme_trocar (o id é o do episódio que acabou) e o que chega para o episódio novo enquanto a Sessão nova não existe.
    /// Devolve verdadeiro se tratou a mensagem.
    /// </summary>
    bool ReceberDaTroca(JsonObject m)
    {
        string? tipo = m["type"]?.GetValue<string>();
        if (tipo == "filme_trocar") { _ = ReceberTrocarAsync(m); return true; }
        if (_trocandoPara != null && m["filme"]?.GetValue<string>() == _trocandoPara) { if (_retidas.Count < 50) _retidas.Add(m); return true; }
        return false;
    }
}
