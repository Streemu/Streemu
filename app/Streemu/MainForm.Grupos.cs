using System.Text.Json.Nodes;
using Streemu.Net;
using Streemu.Ui;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// Vários grupos na janela principal (plano 019, etapa 8): a tela "Grupos" das Configurações (criar, entrar por código ou link, sair, renomear, trocar convite,
/// remover), o seletor de grupo da barra lateral e o link <c>streemu://grupo/&lt;código&gt;</c>. A lógica fica aqui e a tela em <c>Ui/GruposView.cs</c>:
/// quem restilizar a tela não precisa mexer neste arquivo. As salas em si são de <c>MainForm.Conta.cs</c> (<c>SincronizarSalas</c>).
/// </summary>
public sealed partial class MainForm
{
    GruposView? _gruposLista;
    GruposTela? _gruposTela;
    /// <summary>A tela Grupos está aberta (vista da janela, como _arquivosView).</summary>
    bool _gruposView;
    /// <summary>Detalhes de cada grupo (membros, papel, convite) e quando vieram: o servidor só dá isso a membro, e a lista muda com quem entra e sai.</summary>
    readonly Dictionary<string, (GrupoInfo Info, DateTime Em)> _gruposInfo = new();
    bool _gruposCarregando;
    /// <summary>Grupo escolhido no seletor da barra lateral (nulo = todos). Filtro SÓ de tela: as salas, as lives e as notificações seguem de todos os grupos.</summary>
    string? _filtroGrupo;
    /// <summary>Convite que chegou por link e espera a pessoa entrar na conta (ou a janela abrir) para perguntar.</summary>
    string? _convitePendente;

    // ------------------------------------------------------------------ tela "Grupos" (item da barra lateral)

    void AdicionarAjustesGrupos()
    {
        if (_gruposLista != null) return;
        var v = _gruposLista = new GruposView();
        v.PedirCriar += nome => _ = ExecutarGrupoAsync(() => CriarGrupoUiAsync(nome));
        v.PedirEntrar += texto => _ = ExecutarGrupoAsync(() => EntrarGrupoUiAsync(texto));
        v.PedirCopiarConvite += id => _ = ExecutarGrupoAsync(() => CopiarConviteUiAsync(id));
        v.PedirSair += id => { if (ConfirmarGrupo(GruposView.ConfirmaSair, "Sair do grupo")) _ = ExecutarGrupoAsync(() => SairGrupoUiAsync(id)); };
        v.PedirRenomear += id => { if (NomeDoGrupo(id) is { } atual && DialogoTexto.Pedir(this, "Renomear o grupo", "Novo nome do grupo (até 40 letras):", atual) is { } novo) _ = ExecutarGrupoAsync(() => RenomearGrupoUiAsync(id, novo)); };
        v.PedirTrocarConvite += id => { if (ConfirmarGrupo("O convite antigo deixa de valer agora. Quem ainda não entrou precisa do novo. Trocar?", "Trocar o convite")) _ = ExecutarGrupoAsync(() => TrocarConviteUiAsync(id)); };
        v.PedirRemover += (id, conta, apelido) => { if (ConfirmarGrupo($"Remover {(apelido.Length > 0 ? apelido : "esta pessoa")} do grupo? Ela é avisada e só volta com um convite novo.", "Remover do grupo")) _ = ExecutarGrupoAsync(() => RemoverMembroUiAsync(id, conta)); };
        // a tela nasce aqui, depois do Theme.Apply da janela (como a do Cinema e a do Enviar arquivo): o GruposView se pinta sozinho ao nascer
        _gruposTela = new GruposTela(v);
        _gruposHost.BackColor = Theme.Surface;
        _gruposHost.Controls.Add(_gruposTela);
        _side.GroupFilterClicked += MostrarMenuGrupos;
        _side.GruposClicked += MostrarGrupos;
        _side.GruposVisivel = true;
        if (_flyout != null) _flyout.Panel.GruposVisivel = true; // a flutuante já ligou o clique ao nascer (EnsureFlyout)
        AjustarAlturaMinima(); // um item a mais na navegação: a janela não pode ficar mais baixa que a barra
        AtualizarGruposUi();
    }

    /// <summary>O item "Grupos" da barra lateral: abre a tela no lugar do Início (mesmo jeito do Cinema e do Enviar arquivo).</summary>
    void MostrarGrupos()
    {
        if (_gruposTela == null) return;
        HideFlyout();
        if (_preparationBusy) return;
        if (ContaBloqueia()) return;
        _preparationView = _stageView = _settingsView = _filmeView = _arquivosView = _novidadesView = _mumuView = _apoiarView = false;
        _gruposView = true;
        AtualizarGruposUi();
        ApplyView();
    }

    bool ConfirmarGrupo(string texto, string titulo) =>
        MessageBox.Show(this, texto, titulo, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    string? NomeDoGrupo(string id) => _conta?.Grupos.FirstOrDefault(g => g.Id == id)?.Nome;

    /// <summary>Refaz a lista da tela com o que a conta sabe; busca os detalhes que faltam (ou passaram de 1 minuto) sem travar a tela.</summary>
    void AtualizarGruposUi()
    {
        if (_gruposLista == null || _conta == null) return;
        var gs = _conta.Grupos;
        var agora = DateTime.Now;
        foreach (var velho in _gruposInfo.Keys.Where(k => gs.All(g => g.Id != k)).ToList()) _gruposInfo.Remove(velho);
        _gruposLista.Mostrar(gs.Select(g => _gruposInfo.TryGetValue(g.Id, out var d)
            ? new GrupoLinha(g.Id, g.Nome, d.Info.Papel, d.Info.Membros)
            : new GrupoLinha(g.Id, g.Nome, null, null)).ToList());
        if (!_gruposCarregando && _conta.TemConta && gs.Any(g => !_gruposInfo.TryGetValue(g.Id, out var d) || agora - d.Em > TimeSpan.FromMinutes(1)))
            _ = CarregarInfosAsync();
    }

    async Task CarregarInfosAsync()
    {
        var conta = _conta; if (conta == null) return;
        _gruposCarregando = true;
        try
        {
            foreach (var g in conta.Grupos.ToList())
            {
                if (_gruposInfo.TryGetValue(g.Id, out var d) && DateTime.Now - d.Em <= TimeSpan.FromMinutes(1)) continue;
                var (info, _) = await conta.InfoAsync(g.Id);
                if (IsDisposed) return;
                if (info != null) _gruposInfo[g.Id] = (info, DateTime.Now);
            }
        }
        finally { _gruposCarregando = false; }
        Ui(() => { if (_gruposLista != null && _conta != null) _gruposLista.Mostrar(_conta.Grupos.Select(g => _gruposInfo.TryGetValue(g.Id, out var d) ? new GrupoLinha(g.Id, g.Nome, d.Info.Papel, d.Info.Membros) : new GrupoLinha(g.Id, g.Nome, null, null)).ToList()); });
    }

    async Task ExecutarGrupoAsync(Func<Task> f)
    {
        if (_gruposLista == null) return;
        _gruposLista.Travar(true);
        try { await f(); }
        finally { if (!IsDisposed) _gruposLista.Travar(false); }
    }

    void AvisoGrupos(string texto, bool erro = false) { _gruposLista?.Aviso(texto, erro); if (erro) Log.Info("Grupos: " + texto); }

    static void CopiarParaAreaDeTransferencia(string texto) { try { Clipboard.SetText(texto); } catch (Exception e) { Log.Info("Grupos: não consegui copiar (" + e.GetType().Name + ")."); } }

    async Task CriarGrupoUiAsync(string nome)
    {
        var conta = _conta; if (conta == null) return;
        var r = await conta.CriarGrupoAsync(nome, Nick);
        if (!r.Ok) { AvisoGrupos(r.Texto ?? "Não deu certo. Tente de novo.", true); return; }
        SincronizarSalas();
        _gruposLista?.LimparCampos();
        _gruposInfo.Remove(r.Id!);
        string aviso = $"Grupo \"{r.Nome}\" criado.";
        if (r.Convite != null) { CopiarParaAreaDeTransferencia(conta.LinkDoConvite(r.Convite)); aviso += " O link do convite foi copiado: mande a quem você quer no grupo."; }
        AvisoGrupos(aviso);
        AtualizarGruposUi();
    }

    /// <summary>Entra por convite (código, com ou sem hífens, ou link). Devolve a frase para a tela e se deu certo.</summary>
    async Task<(bool Ok, string Texto)> EntrarPorConviteAsync(string texto)
    {
        var conta = _conta;
        if (conta == null) return (false, "Entre na sua conta primeiro.");
        var codigo = AppLink.NormalizeInvite(texto);
        if (codigo == null) return (false, "Esse convite não parece certo. Cole o código (16 letras e números) ou o link inteiro.");
        var r = await conta.EntrarAsync(codigo, Nick);
        if (!r.Ok) return (false, r.Texto ?? "Não deu certo. Tente de novo.");
        SincronizarSalas();
        _gruposInfo.Remove(r.Id!);
        AtualizarGruposUi();
        return (true, $"Você entrou no grupo \"{r.Nome}\".");
    }

    async Task EntrarGrupoUiAsync(string texto)
    {
        var (ok, frase) = await EntrarPorConviteAsync(texto);
        if (ok) _gruposLista?.LimparCampos();
        AvisoGrupos(frase, !ok);
    }

    async Task CopiarConviteUiAsync(string id)
    {
        var conta = _conta; if (conta == null) return;
        if (!_gruposInfo.TryGetValue(id, out var d) || d.Info.Convite == null)
        {
            var (info, texto) = await conta.InfoAsync(id);
            if (info?.Convite == null) { AvisoGrupos(texto ?? "Não consegui o convite agora. Tente de novo.", true); return; }
            _gruposInfo[id] = (info, DateTime.Now);
            d = _gruposInfo[id];
        }
        CopiarParaAreaDeTransferencia(conta.LinkDoConvite(d.Info.Convite!));
        AvisoGrupos("Link do convite copiado. Quem abrir entra no grupo depois de confirmar.");
    }

    async Task SairGrupoUiAsync(string id)
    {
        var conta = _conta; if (conta == null) return;
        string nome = NomeDoGrupo(id) ?? "";
        var r = await conta.SairDoGrupoAsync(id);
        if (!r.Ok) { AvisoGrupos(r.Texto ?? "Não deu certo. Tente de novo.", true); return; }
        _salaUnida?.Remover(id);
        _gruposInfo.Remove(id);
        SincronizarSalas();
        AvisoGrupos($"Você saiu do grupo \"{nome}\"." + (r.Acabou ? " Era a última pessoa: o grupo foi apagado." : ""));
    }

    async Task RenomearGrupoUiAsync(string id, string nome)
    {
        var conta = _conta; if (conta == null) return;
        var r = await conta.RenomearAsync(id, nome);
        if (!r.Ok) { AvisoGrupos(r.Texto ?? "Não deu certo. Tente de novo.", true); return; }
        _gruposInfo.Remove(id);
        SincronizarSalas();
        AvisoGrupos($"Grupo renomeado para \"{r.Nome}\".");
    }

    async Task TrocarConviteUiAsync(string id)
    {
        var conta = _conta; if (conta == null) return;
        var r = await conta.TrocarConviteAsync(id);
        if (!r.Ok) { AvisoGrupos(r.Texto ?? "Não deu certo. Tente de novo.", true); return; }
        _gruposInfo.Remove(id);
        CopiarParaAreaDeTransferencia(conta.LinkDoConvite(r.Convite!));
        AvisoGrupos("Convite novo gerado e copiado. O antigo não vale mais.");
        AtualizarGruposUi();
    }

    async Task RemoverMembroUiAsync(string id, string contaDoMembro)
    {
        var conta = _conta; if (conta == null) return;
        var r = await conta.RemoverAsync(id, contaDoMembro);
        if (!r.Ok) { AvisoGrupos(r.Texto ?? "Não deu certo. Tente de novo.", true); return; }
        _gruposInfo.Remove(id);
        AvisoGrupos("Pessoa removida do grupo.");
        AtualizarGruposUi();
    }

    // ------------------------------------------------------------------ seletor de grupo da barra lateral

    /// <summary>Com mais de um grupo, o título "Amigos" da barra vira um seletor ("Todos os grupos" ou um grupo). Com um só, não aparece.</summary>
    void AtualizarFiltroGrupos()
    {
        int n = _salaUnida?.Quantidade ?? 0;
        if (n < 2 || _filtroGrupo != null && _salaUnida?.Tem(_filtroGrupo) != true) _filtroGrupo = null;
        string? texto = n < 2 ? null : _filtroGrupo == null ? "Todos os grupos" : _salaUnida!.Grupos.FirstOrDefault(g => g.Id == _filtroGrupo).Nome;
        _side.GroupFilterText = texto;
        if (_flyout != null) _flyout.Panel.GroupFilterText = texto;
    }

    /// <summary>Amigos da lista da barra, só do grupo escolhido no seletor (sem escolha, todos).</summary>
    IEnumerable<Member> FiltrarPorGrupo(IEnumerable<Member> amigos) =>
        _filtroGrupo == null || _salaUnida == null ? amigos : amigos.Where(m => _salaUnida.GruposDe(m.Id).Contains(_filtroGrupo));

    void MostrarMenuGrupos(Rectangle itemNaTela)
    {
        if (_salaUnida is not { Quantidade: > 1 } unida) return;
        var menu = new ContextMenuStrip();
        TrayVisuals.Style(menu);
        void Escolher(string? id) { _filtroGrupo = id; AtualizarFiltroGrupos(); RefreshSide(); }
        menu.Items.Add(new ToolStripMenuItem("Todos os grupos", null, (_, _) => Escolher(null)) { Checked = _filtroGrupo == null });
        foreach (var (id, nome) in unida.Grupos)
            menu.Items.Add(new ToolStripMenuItem(nome.Length > 0 ? nome : "(sem nome)", null, (_, _) => Escolher(id)) { Checked = _filtroGrupo == id });
        menu.Closed += (_, _) => menu.BeginInvoke(menu.Dispose);
        SegurarBarraFlutuante(menu); // a barra flutuante não abre por cima do menu
        menu.Show(new Point(itemNaTela.Left, itemNaTela.Bottom));
    }

    // ------------------------------------------------------------------ link streemu://grupo/<código>

    /// <summary>Chegou um convite por link (clique no navegador ou no Discord). NUNCA entra sozinho: pergunta antes. Sem conta, guarda o convite para depois do login.</summary>
    void ReceberConvite(string codigo)
    {
        ShowMain();
        _convitePendente = codigo;
        if (_conta is not { TemConta: true })
        {
            MostrarEntrada(true);
            Notify("Entre na sua conta para aceitar o convite. Ele fica guardado até você entrar.");
            return;
        }
        _ = ProcessarConvitePendenteAsync();
    }

    /// <summary>Chamado quando a conta está de pé (depois do login, ou ao abrir o app já entrado) e há um convite guardado.</summary>
    async Task ProcessarConvitePendenteAsync()
    {
        var codigo = _convitePendente;
        if (codigo == null || _conta is not { TemConta: true }) return;
        _convitePendente = null;
        ShowMain();
        var resposta = MessageBox.Show(this,
            "Você recebeu um convite para entrar num grupo do " + UiBrand.Name + ".\n\nEntrando, as pessoas do grupo passam a ver quando você está online e ao vivo, e você vê as delas. Só aceite se você conhece quem mandou.\n\nEntrar no grupo?",
            "Convite para um grupo", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (resposta != DialogResult.Yes) { Notify("Você não entrou no grupo. O convite continua valendo para quem o recebeu."); return; }
        var (ok, frase) = await EntrarPorConviteAsync(codigo);
        Notify(frase);
        AvisoGrupos(frase, !ok);
    }

    // ------------------------------------------------------------------ roteiro de teste (STREEMU_TEST_GRUPO)

    /// <summary>
    /// STREEMU_TEST_GRUPO="passo;passo;...": depois de entrar na conta, o teste cria, entra e sai de grupos como a pessoa faria (as chamadas são as mesmas da tela, só não passa
    /// pelas caixas de confirmação). Os apps combinam entre si por arquivos em STREEMU_TEST_DIR. Passos: <c>criar:G</c>, <c>entrar:PAPEL:G</c> (espera o convite do papel),
    /// <c>entrar-falha:PAPEL:G</c> (o convite NÃO pode valer), <c>amigo:PAPEL:G</c> (espera o papel aparecer no grupo), <c>fora:PAPEL:G</c> (espera ele sair do grupo),
    /// <c>offline:PAPEL</c> (espera ele sumir dos online e aparecer em Ausentes: a seção OFFLINE da etapa 7), <c>sair:G</c>, <c>marca:NOME</c> e <c>espera:PAPEL:NOME</c> (sinais entre os apps), <c>filtro:G</c>, <c>sem-filtro</c>, <c>naovejo:PAPEL</c>, <c>dorme:SEGUNDOS</c>.
    /// O resultado vai em <c>sala.roteiro</c> do relatório.
    /// </summary>
    static string? TesteGrupoRoteiro => TestConta ? Environment.GetEnvironmentVariable("STREEMU_TEST_GRUPO") : null;
    JsonObject? _roteiroResultado;

    async Task<bool> EsperarAsync(Func<bool> cond, int segundos = 60)
    {
        var limite = DateTime.Now.AddSeconds(segundos);
        while (!cond() && DateTime.Now < limite) await Task.Delay(200);
        return cond();
    }

    async Task TesteGrupoAsync(string roteiro)
    {
        var conta = _conta!;
        var ids = new Dictionary<string, string> { ["grupo"] = "grupo" }; // nome → id do grupo
        var amigos = new Dictionary<string, string>(); // papel → id (conta) visto
        var passos = new JsonArray();
        var filtros = new JsonObject();
        foreach (var passo in roteiro.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = passo.Split(':');
            bool ok = false; string nota = "";
            try
            {
                switch (p[0])
                {
                    case "criar":
                    {
                        var r = await conta.CriarGrupoAsync(p[1], Nick);
                        ok = r.Ok; nota = r.Erro ?? "";
                        if (ok) { ids[p[1]] = r.Id!; SincronizarSalas(); File.WriteAllText(TestArq("convite-" + p[1] + ".txt"), r.Convite!.Replace("-", "")); }
                        break;
                    }
                    case "entrar" or "entrar-falha":
                    {
                        string arq = Path.Combine(TestDir!, p[1] + "-convite-" + p[2] + ".txt");
                        if (!await EsperarAsync(() => File.Exists(arq) && new FileInfo(arq).Length >= 16)) { nota = "sem convite"; break; }
                        var r = await conta.EntrarAsync(File.ReadAllText(arq).Trim(), Nick);
                        nota = r.Erro ?? "";
                        if (r.Ok) { ids[p[2]] = r.Id!; SincronizarSalas(); }
                        ok = p[0] == "entrar" ? r.Ok : !r.Ok;
                        break;
                    }
                    case "amigo":
                    {
                        string nick = "teste-" + p[1];
                        ok = await EsperarAsync(() =>
                        {
                            var m = _salaUnida?.Members.FirstOrDefault(x => x.Nick == nick);
                            if (m == null || !ids.TryGetValue(p[2], out var gid) || !_salaUnida!.GruposDe(m.Id).Contains(gid)) return false;
                            amigos[p[1]] = m.Id; return true;
                        });
                        break;
                    }
                    case "fora":
                        ok = amigos.TryGetValue(p[1], out var quem) && ids.TryGetValue(p[2], out var g2) && await EsperarAsync(() => _salaUnida?.GruposDe(quem).Contains(g2) != true);
                        break;
                    case "offline":
                    {
                        string nickOff = "teste-" + p[1];
                        ok = await EsperarAsync(() => _room!.Ausentes.Any(a => a.Nick == nickOff) && _room.Friends.All(m => m.Nick != nickOff));
                        break;
                    }
                    case "sair":
                    {
                        var r = await conta.SairDoGrupoAsync(ids[p[1]]);
                        ok = r.Ok; nota = r.Acabou ? "acabou" : (r.Erro ?? "");
                        if (ok) { _salaUnida?.Remover(ids[p[1]]); SincronizarSalas(); }
                        break;
                    }
                    case "marca": File.WriteAllText(TestArq(p[1] + ".txt"), "ok"); ok = true; break;
                    case "espera": ok = await EsperarAsync(() => File.Exists(Path.Combine(TestDir!, p[1] + "-" + p[2] + ".txt")), 90); break;
                    case "filtro":
                    {
                        _filtroGrupo = ids[p[1]]; AtualizarFiltroGrupos();
                        filtros[p[1]] = new JsonArray(FiltrarPorGrupo(_room!.Friends).Select(m => (JsonNode)JsonValue.Create(m.Nick)!).ToArray());
                        ok = true; break;
                    }
                    case "sem-filtro": _filtroGrupo = null; AtualizarFiltroGrupos(); ok = true; break;
                    case "naovejo":
                        await Task.Delay(3000);
                        ok = _room!.Friends.All(m => m.Nick != "teste-" + p[1]); break;
                    case "dorme": await Task.Delay(int.Parse(p[1]) * 1000); ok = true; break;
                }
            }
            catch (Exception e) { nota = e.GetType().Name; }
            passos.Add(new JsonObject { ["passo"] = passo, ["ok"] = ok, ["nota"] = nota });
            if (!ok) break;
        }
        var unida = _salaUnida;
        _roteiroResultado = new JsonObject
        {
            ["passos"] = passos,
            ["filtros"] = filtros,
            ["grupos"] = new JsonArray((unida?.Grupos ?? []).Select(g => (JsonNode)new JsonObject
            {
                ["nome"] = g.Nome,
                ["amigos"] = new JsonArray((unida?.Members ?? []).Where(m => m.Id != unida!.MyId && unida.GruposDe(m.Id).Contains(g.Id)).Select(m => (JsonNode)JsonValue.Create(m.Nick)!).ToArray()),
            }).ToArray()),
            ["offline_na_barra"] = new JsonArray(AusentesParaBarra().Select(f => (JsonNode)JsonValue.Create(f.Nick)!).ToArray()), // MainForm.Apelidos.cs
            ["amigos_unidos"] = new JsonArray((_room?.Friends ?? []).Select(m => (JsonNode)JsonValue.Create(m.Nick)!).ToArray()),
            ["seletor_visivel"] = _side.GroupFilterText != null,
        };
        await Task.Delay(1500);
        File.WriteAllText(TestArq("relatorio.json"), BuildReport());
    }
}
