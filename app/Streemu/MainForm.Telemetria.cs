using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Streemu.Net;
using Streemu.Telemetria;
using Streemu.Util;
using DiagState = Streemu.Ui.DiagState;

namespace Streemu;

/// <summary>
/// Telemetria (spec 009): liga o envio automático, o botão Marcar problema (barra lateral, bandeja e Ctrl+Alt+B), o "envie agora" da sala e os
/// relatórios automáticos. Fica fora do MainForm.cs e do MainForm.Grupo.cs para dar menos conflito: lá só há chamadas de uma linha para cá.
/// </summary>
public sealed partial class MainForm
{
    Envio? _tele;
    SessaoLocal? _teleSessao;
    SerieQuadros? _teleSerie;
    Vigia? _vigia;
    volatile UsoGpu? _teleGpu;
    AtalhoProblema? _atalhoProblema;
    volatile Identidade _teleQuem = new(null, null, null, null);
    readonly System.Windows.Forms.Timer _teleTimer = new() { Interval = 1000 };
    readonly CheckBox _teleEnviar = new Ui.ToggleSwitch { Text = "Enviar diagnóstico", AutoSize = true };
    readonly Ui.DiagnosisSummary _diagnosisSummary = new();
    readonly Button _teleVer = new() { Text = "Ver o que é enviado", AutoSize = true };
    readonly Button _teleMarcar = new() { Text = $"Marcar problema ({AtalhoProblema.Nome})", AutoSize = true };
    // MaximumSize com largura: entra nos textos que quebram na largura da janela (WrapLabelsTo); sem isso a mensagem de erro saía cortada numa linha só
    readonly Label _teleStatus = new() { AutoSize = true, MaximumSize = new Size(620, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 2, 3, 6) };
    /// <summary>Ligação → id da live, guardado no começo (no fim o amigo pode já ter saído da sala).</summary>
    readonly Dictionary<string, string?> _teleLiveDaLigacao = new();
    DateTime _teleUltimaFalha = DateTime.MinValue;
    readonly Gravidade.Freio _freioRelatorio = new();
    volatile string _teleLives = "[]";
    /// <summary>Quem assiste a própria live agora (a cópia que a tela atualiza a cada segundo: a série de quadros é gravada na thread da captura).</summary>
    volatile int _teleAssistindo;

    /// <summary>Quando o envio nem chegou a ligar (sem servidor, erro ao iniciar, teste): o que o indicador mostra no lugar da situação do envio.</summary>
    SituacaoEnvio? _teleNaoLigou;

    /// <summary>Chamado no Load. Em teste, sem STREEMU_TELEMETRIA_URL não há para onde enviar: nada é criado (e o indicador diz isso).</summary>
    void IniciarTelemetria()
    {
        _teleEnviar.Checked = !_state.TelemetriaDesligada;
        _teleEnviar.CheckedChanged += (_, _) => MudarEnvioTelemetria(_teleEnviar.Checked);
        _teleVer.Click += (_, _) => { try { Directory.CreateDirectory(Pasta.Raiz); Process.Start(new ProcessStartInfo("explorer.exe", Pasta.Raiz) { UseShellExecute = true }); } catch { } };
        _teleMarcar.Click += (_, _) => MarcarProblema(pedirNota: true);
        _side.ProblemClicked += () => MarcarProblema(pedirNota: true);
        _side.DiagClicked += () => ShowSettingsAt("Diagnóstico"); // o detalhe do sinal está lá
        _teleTimer.Tick += (_, _) => { _vigia?.Batimento(); TickTelemetria(); TalvezMostrarAvisoDiagnostico(); TalvezMostrarConvitePesquisa(); }; // o batimento prova que a interface está respondendo
        _teleTimer.Start(); // roda mesmo sem envio: o indicador precisa dizer por que não está enviando

        string? url = Environment.GetEnvironmentVariable("STREEMU_TELEMETRIA_URL");
        if (url == null)
        {
            // testes automáticos não sobem nada, a não ser que apontem para um servidor de teste (wrangler dev)
            if (Automated) { _teleNaoLigou = new(EstadoEnvio.Neutro, "Diagnóstico desligado", "Modo de teste: o diagnóstico só liga com STREEMU_TELEMETRIA_URL."); TickTelemetria(); return; }
            url = RelayConfig.Load(_state)!.Url; // fora de teste nunca é nulo (Automated já saiu acima)
        }

        try
        {
            string id = Path.GetFileNameWithoutExtension(Log.FilePath).Replace("sessao-", "");
            if (Environment.GetEnvironmentVariable("STREEMU_TEST_ROLE") is string papel) id += "-" + papel;
            AtualizarQuem();
            _teleSessao = SessaoLocal.Criar(id, Log.FilePath, naoEnviar: false, reduzida: _state.TelemetriaDesligada);
            _serieSincronia = new SerieSincronia(_teleSessao);
            _serieRede = new SerieRede(_teleSessao);
            Binarios.LigarTelemetria((tipo, dados) => _teleSessao?.Evento(tipo, dados)); // binario_invalido (Util/Binarios.cs)
            _teleSessao.Evento("sessao_inicio", new JsonObject
            {
                ["args"] = string.Join(' ', LaunchArgs ?? Array.Empty<string>()), ["atualizado_agora"] = JustUpdated,
                ["versao"] = Program.Version, ["canal"] = BuildInfo.Canal,
            });
            _vigia = new Vigia(_teleSessao, () => JsonNode.Parse(_teleLives));
            if (string.IsNullOrEmpty(_state.TelemetriaIdAnonimo)) { _state.TelemetriaIdAnonimo = Guid.NewGuid().ToString("N"); if (!Automated) _state.Save(); }
            _teleInicio = DateTime.Now;
            _tele = new Envio(url, _teleSessao, () => _teleQuem, _state.TelemetriaIdAnonimo) { Token = TokenDaConta, Bilhete = () => _conta?.Bilhete, Habilitado = !_state.TelemetriaDesligada, Aguardando = !Automated && !_state.TelemetriaAvisoVisto }; // instalação nova: nada sobe antes do aviso
            var serie = _teleSerie = new SerieQuadros(_teleSessao);
            _sender.SecondMeasured += (novos, saindo, medida) =>
            {
                // o uso da placa só é lido enquanto há live (abre no primeiro segundo medido e fecha quando a transmissão para)
                var gpu = _teleGpu ?? (_sender.Running ? _teleGpu = new UsoGpu(() => _sender.Current is { Source.IsWindow: true } c ? c.Source.ProcessId : null) : null);
                serie.Segundo(novos, saindo, _teleAssistindo, _sender.CaptureUsed?.Name, _sender.GameInFront, medida, gpu?.Agora);
            };
            _sender.Stopped += () => { var g = _teleGpu; _teleGpu = null; g?.Dispose(); };
            _tele.PerfilAceito += perfil => Ui(() =>
            {
                _state.TelemetriaPerfilHash = Telemetria.Perfil.Hash(perfil);
                _state.TelemetriaPerfilEm = DateTime.Now;
                if (!Automated) _state.Save();
            });
            var sessao = _teleSessao;
            var envio = _tele;
            string? hashAntes = _state.TelemetriaPerfilHash;
            bool perfilVelho = DateTime.Now - _state.TelemetriaPerfilEm > TimeSpan.FromDays(7);
            // fora da tela e com prioridade baixa: o WMI do perfil leva até alguns segundos
            var t = new Thread(() =>
            {
                try
                {
                    envio.Iniciar(Manutencao.Anteriores(sessao));
                    var perfil = Telemetria.Perfil.Coletar();
                    string hash = Telemetria.Perfil.Hash(perfil);
                    sessao.Retrato($"perfil-{hash[..8]}.json", perfil.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                    if (hash != hashAntes) sessao.Evento("perfil", new JsonObject { ["hash"] = hash, ["antes"] = hashAntes });
                    envio.DefinirPerfil(hash != hashAntes || perfilVelho ? perfil : null, Ids.Hardware());
                    Log.Info($"[telemetria] sessão {sessao.Id}, PC {envio.Pc}: perfil do PC pronto ({Resumo(perfil)}).");
                    Manutencao.Limpar(Log.FilePath, sessao, aoDescartar: envio.Esquecer);
                }
                catch (Exception e) { Log.Info("[telemetria] não consegui montar o perfil do PC: " + e.GetBaseException().Message); }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "telemetria-perfil" };
            t.Start();

            _atalhoProblema = new AtalhoProblema(() => MarcarProblema(pedirNota: false));
            if (!_atalhoProblema.Registrado) Log.Info($"[telemetria] o atalho {AtalhoProblema.Nome} já é de outro programa; para marcar problema use a bandeira da barra lateral ou a bandeja.");
            Log.Line += VigiarFalhas;
            if (_room != null) _room.TelemetryNow += () => _tele?.Agora();
            // teste: marca um problema N s depois de abrir, como o atalho faria (testes-locais confere que chega ao índice com a live)
            if (int.TryParse(Environment.GetEnvironmentVariable("STREEMU_TEST_MARCAR"), out int marcarEm))
            {
                var marca = new System.Windows.Forms.Timer { Interval = Math.Max(1, marcarEm) * 1000 };
                marca.Tick += (_, _) => { marca.Dispose(); TickTelemetria(); MarcarProblema(pedirNota: false); };
                marca.Start();
            }

            // o aviso fica na tela até a pessoa fechar (antes era uma notificação que sumia); ver MainForm.SobreDiagnostico.cs
            IniciarSobreDiagnostico();
        }
        catch (Exception e)
        {
            Log.Info("[telemetria] não consegui ligar: " + e.GetBaseException().Message);
            _tele = null;
            _teleNaoLigou = new(EstadoEnvio.Falha, "Diagnóstico não ligou", $"O diagnóstico não conseguiu começar ({e.GetBaseException().Message}). Feche pela bandeja (Sair) e abra de novo.");
        }
        TickTelemetria();
    }

    static string Resumo(JsonObject perfil)
    {
        var e = perfil["estavel"];
        string gpu = string.Join(" + ", (e?["gpus"] as JsonArray ?? new JsonArray()).Select(g => g?["nome"]?.ToString()).Where(n => n != null));
        return $"{e?["cpu"]?["nome"]}, {e?["ram"]?["total_gb"]} GB, {gpu}, {e?["windows"]?["produto"]} {e?["windows"]?["versao"]} {e?["windows"]?["build"]}.{e?["windows"]?["ubr"]}, {e?["maquina"]?["tipo"]}";
    }

    DateTime _teleInicio = DateTime.MaxValue;

    void AtualizarQuem() => _teleQuem = new Identidade(_state.DiscordId, _state.DiscordNome, Nick, _state.InstallId, _conta is { TemConta: true } c ? c.ContaId : null);

    /// <summary>
    /// Ensina ao <see cref="Anonimizador"/> os nomes que o app conhece (a própria pessoa e os amigos da sala), para o diagnóstico anônimo trocá-los por
    /// pessoa-N. Só custa algo quando a pessoa desligou o diagnóstico. Fica pronto quando a sala trouxe os amigos ou depois de 60 s sem sala.
    /// </summary>
    void EnsinarNomesAoAnonimizador()
    {
        if (!_state.TelemetriaDesligada) return;
        Anonimizador.Registrar(Nick); Anonimizador.Registrar(_state.DiscordNome); Anonimizador.Registrar(_state.DiscordId); Anonimizador.Registrar(_state.InstallId);
        Anonimizador.Registrar(_state.ContaId);
        foreach (var apelido in Apelidos.Todos.Values.ToList()) Anonimizador.Registrar(apelido); // apelidos locais (plano 019, etapa 7): segunda barreira, depois do filtro do log
        if (_room != null) foreach (var a in _room.Ausentes.ToList()) { Anonimizador.Registrar(a.Nick); Anonimizador.Registrar(a.Id); }
        bool achouAmigos = false;
        if (_room != null) foreach (var m in _room.Friends.ToList()) { achouAmigos = true; Anonimizador.Registrar(m.Nick); Anonimizador.Registrar(m.Id); }
        if (!Anonimizador.Pronto && (achouAmigos || DateTime.Now - _teleInicio > TimeSpan.FromSeconds(60))) Anonimizador.MarcarPronto();
    }

    void TickTelemetria()
    {
        GravarSincronia();
        GravarRede();
        EnsinarNomesAoAnonimizador();
        if (_tele != null)
        {
            AtualizarQuem();
            _teleLives = LivesAgora().ToJsonString();
            _teleAssistindo = WatchingCount;
            _tele.Ativo = _sender.Running || _linkViewers.Values.Any(v => v.Running);
        }
        // uma fonte só para a barra lateral, a barra flutuante e os ajustes: os três nunca dizem coisas diferentes
        var s = _tele?.Situacao() ?? _teleNaoLigou ?? new SituacaoEnvio(EstadoEnvio.Neutro, "Preparando diagnóstico", "Ligando o diagnóstico.");
        var estado = s.Estado switch { EstadoEnvio.Ok => DiagState.Ok, EstadoEnvio.Falha => DiagState.Fail, _ => DiagState.Neutral };
        _side.SetDiag(estado, s.Titulo, s.Detalhe);
        _flyout?.Panel.SetDiag(estado, s.Titulo, s.Detalhe);
        if (_teleUltimoEstado != s.Estado)
        {
            // mudança de estado vai para o log (é o que explica, depois, um "ficou vermelho" que alguém viu)
            if (_teleUltimoEstado != null) Log.Info($"[telemetria] indicador: {s.Titulo}. {s.Detalhe}");
            _teleUltimoEstado = s.Estado;
        }
        if (!_settingsHost.Visible) return;
        _diagnosisSummary.SetStatus(estado, s.Titulo, s.Estado == EstadoEnvio.Ok ? "Envio confirmado pelo servidor" : s.Detalhe);
        string sufixo = _tele != null ? $" (PC {_tele.Pc}, sessão {_teleSessao?.Id})" : "";
        _teleStatus.Text = $"{(s.Estado == EstadoEnvio.Ok ? "✓" : s.Estado == EstadoEnvio.Falha ? "✕" : "○")} {s.Titulo}: {s.Detalhe}{sufixo}";
        _teleStatus.ForeColor = s.Estado == EstadoEnvio.Falha ? Theme.Error : s.Estado == EstadoEnvio.Ok ? Theme.Ok : SystemColors.GrayText;
    }

    EstadoEnvio? _teleUltimoEstado;

    void MudarEnvioTelemetria(bool ligado)
    {
        _state.TelemetriaDesligada = !ligado;
        if (!Automated) _state.Save();
        if (_tele != null)
        {
            _tele.Habilitado = ligado;
            // desligou no meio da sessão: o que ainda não subiu desta sessão não sobe mais
            if (!ligado && _teleSessao != null) { _teleSessao.E.NaoEnviar = true; _teleSessao.Salvar(); }
        }
        Log.Info(ligado ? "Diagnóstico automático ligado (vale a partir da próxima abertura para o que já passou)." : "Diagnóstico automático desligado: o que ainda não subiu desta sessão não sobe mais; nas próximas aberturas sobe anônimo (log e dados do PC sem o seu nome, o seu Discord nem o nome dos amigos).");
    }

    /// <summary>Linha de erro ou aviso do app (não dos ffmpeg): vira evento "falha" e o envio sai na hora, para os segundos do problema já estarem lá.</summary>
    void VigiarFalhas(string linha)
    {
        if (_teleSessao == null || _tele == null) return;
        if (!Gravidade.EhFalha(linha)) return;
        if (DateTime.Now - _teleUltimaFalha > TimeSpan.FromSeconds(3))
        {
            _teleUltimaFalha = DateTime.Now;
            string texto = linha.Length > 13 ? linha[13..] : linha; // sem a hora local (o evento tem a UTC)
            // o log é escrito de qualquer thread: as lives vêm da cópia que a tela atualiza a cada segundo
            _teleSessao.Evento("falha", new JsonObject { ["linha"] = texto.Length > 400 ? texto[..400] : texto, ["lives"] = JsonNode.Parse(_teleLives), ["tipo_falha"] = Gravidade.Classificar(linha) ?? "outra" });
        }
        RelatorioDeFalhaGrave(linha);
        _tele.Agora();
    }

    /// <summary>As lives em que este PC está agora (a própria e as que assiste), para o evento saber de qual live se trata.</summary>
    JsonArray LivesAgora()
    {
        var arr = new JsonArray();
        if (_streamId != null) arr.Add(new JsonObject { ["transmissao"] = _streamId, ["papel"] = "transmite", ["espectadores"] = _links.Values.Count(l => l.IAmSender) });
        foreach (var l in _links.Values.Where(l => !l.IAmSender))
            arr.Add(new JsonObject { ["transmissao"] = _teleLiveDaLigacao.GetValueOrDefault(l.SessionId), ["papel"] = "assiste", ["ligacao"] = l.SessionId, ["de"] = NomeParaTelemetria(l) });
        return arr;
    }

    /// <summary>
    /// Marcar problema: grava a hora exata e as lives abertas, um relatório completo, e pede para todo mundo das mesmas lives enviar já (pela sala).
    /// Pelo atalho (jogo em tela cheia) não abre nada por cima: só o balão da bandeja.
    /// </summary>
    void MarcarProblema(bool pedirNota)
    {
        if (_tele == null || _teleSessao == null)
        {
            Notify("Sem servidor do grupo: não tenho para onde mandar o diagnóstico. Use \"Copiar relatório\" em Ajustes > Diagnóstico.");
            return;
        }
        var agora = DateTime.Now;
        var lives = LivesAgora();
        string eid = $"marca-{agora:HHmmssfff}";
        _teleSessao.Evento("marcacao", new JsonObject { ["marca"] = eid, ["lives"] = lives, ["filme"] = FilmeTele(), ["por"] = pedirNota ? "botão" : "atalho" });
        RelatorioAutomatico("marcacao");
        _tele.Agora();
        var outros = _links.Values.Select(l => l.PeerId).Distinct().ToList();
        if (outros.Count > 0) _room?.SendTelemetryNow(outros);
        _side.FlashProblem();
        string quem = lives.Count == 0 ? "" : " Os amigos das mesmas lives também enviam agora.";
        if (!_tele.Habilitado) Notify($"Problema marcado às {agora:HH:mm:ss}, mas o diagnóstico está desligado em Ajustes: a marcação sobe só anônima, sem a sua nota.");
        else if (!pedirNota || !Visible) Notify($"Problema marcado às {agora:HH:mm:ss}. Os logs já estão subindo; pode contar para a IA o que aconteceu.{quem}");
        else
        {
            Log.Info($"Problema marcado às {agora:HH:mm:ss}.{quem}");
            PedirNota(agora, eid);
        }
    }

    /// <summary>Caixinha opcional para descrever o problema. Não é modal e some sozinha em 60 s.</summary>
    void PedirNota(DateTime quando, string marca)
    {
        var f = new Form
        {
            Text = "Problema marcado", FormBorderStyle = FormBorderStyle.FixedToolWindow, StartPosition = FormStartPosition.CenterParent,
            ShowInTaskbar = false, TopMost = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12),
            BackColor = Theme.Surface, ForeColor = Theme.Text,
        };
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        layout.Controls.Add(new Label { Text = $"Marcado às {quando:HH:mm:ss}; os logs já estão subindo.\nQuer dizer o que aconteceu? (opcional)", AutoSize = true });
        var box = new TextBox { Width = UiScale.Dp(this, 360), PlaceholderText = "ex.: a live do Bruno travou, áudio estourado" };
        var enviar = new Button { Text = "Enviar", AutoSize = true };
        Theme.StyleButton(enviar, true);
        layout.Controls.Add(new Ui.CampoArredondado(box, 360));
        layout.Controls.Add(enviar);
        f.Controls.Add(layout);
        f.AcceptButton = enviar;
        enviar.Click += (_, _) =>
        {
            if (box.Text.Trim() is { Length: > 0 } texto && _teleSessao != null)
            {
                _teleSessao.Evento("marcacao_nota", new JsonObject { ["marca"] = marca, ["texto"] = texto.Length > 500 ? texto[..500] : texto });
                _tele?.Agora();
                Log.Info("Nota do problema: " + texto);
            }
            f.Close();
        };
        var fecha = new System.Windows.Forms.Timer { Interval = 60_000 };
        fecha.Tick += (_, _) => { fecha.Dispose(); if (!f.IsDisposed && box.TextLength == 0) f.Close(); };
        f.FormClosed += (_, _) => { fecha.Dispose(); f.Dispose(); };
        fecha.Start();
        f.Show(this);
        box.Focus();
    }

    /// <summary>
    /// Falha grave (<see cref="Gravidade"/>): além do evento, o relatório completo na hora, com o freio da sessão. Roda na thread da interface (o
    /// relatório lê a tela); se a interface é a que está travada, o evento <c>ui_travada</c> já cobre. Exceção fatal: não há interface a esperar,
    /// monta ali mesmo e espera o disco até 1 s, porque o processo está morrendo.
    /// </summary>
    void RelatorioDeFalhaGrave(string linha)
    {
        string? tipo = Gravidade.Classificar(linha);
        if (tipo == null || _teleSessao == null) return;
        string texto = linha.Length > 13 ? linha[13..] : linha;
        var foco = new JsonObject { ["falha_grave"] = tipo, ["linha"] = texto.Length > 400 ? texto[..400] : texto };
        if (tipo == "fatal")
        {
            lock (_freioRelatorio) { if (!_freioRelatorio.Permitir(tipo, Environment.TickCount64)) return; }
            RelatorioAutomatico("falha-" + tipo, foco);
            Telemetria.Gravador.Descarregar(TimeSpan.FromSeconds(1));
            return;
        }
        Ui(() =>
        {
            lock (_freioRelatorio) { if (!_freioRelatorio.Permitir(tipo, Environment.TickCount64)) return; }
            RelatorioAutomatico("falha-" + tipo, foco);
        });
    }

    /// <summary>O mesmo JSON do "Copiar relatório", gravado sozinho (fim de live, fim de assistir, marcação, saída).</summary>
    void RelatorioAutomatico(string motivo, JsonObject? foco = null)
    {
        if (_teleSessao == null || _tele == null) return;
        try
        {
            string slug = new string(motivo.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD)
                .Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
            _teleSessao.Retrato($"relatorios/{DateTime.Now:yyyyMMdd-HHmmss}-{slug}.json", SemQuemEstavaComQuem(BuildReport(foco)));
            _tele.Agora();
        }
        catch (Exception e) { Log.Info("[telemetria] relatório automático falhou: " + e.Message); }
    }

    /// <summary>Um evento <c>live_resumo</c> (linha do tempo por minuto) ao fim da transmissão ou de assistir uma ligação; passa pela sessão como os outros eventos.</summary>
    void EmitirResumoLive(ResumoLive resumo, string ligacao)
    {
        try { if (resumo.Fechar(ligacao) is { } ev) _teleSessao?.Evento("live_resumo", ev); }
        catch (Exception e) { Log.Info("[telemetria] live_resumo falhou: " + e.Message); }
    }

    /// <summary>Começo e fim da própria live (chamado em OnLiveStarted / OnLiveStopped).</summary>
    void TelemetriaLive(bool comecou)
    {
        if (_teleSessao == null || _streamId == null) return;
        if (comecou)
        {
            _teleSessao.Evento("live", new JsonObject { ["transmissao"] = _streamId, ["papel"] = "transmite", ["ligacao"] = "propria", ["jogo"] = _liveGame });
            ResumoLive.Host.Iniciar();
        }
        else
        {
            _teleSerie?.Descarregar();
            RelatorioAutomatico("fim-da-transmissao");
            EmitirResumoLive(ResumoLive.Host, "");
            _teleSessao.Evento("live_fim", new JsonObject { ["transmissao"] = _streamId, ["ligacao"] = "propria" });
        }
        _tele?.Agora();
    }

    /// <summary>Começo e fim de cada ligação: no transmissor, uma por espectador; em quem assiste, uma por live (multistream).</summary>
    void TelemetriaLigacao(PeerLink link, bool comecou)
    {
        if (_teleSessao == null) return;
        if (comecou)
        {
            MarcarSePrivada(link);
            string? live = link.IAmSender ? _streamId : _room?.Find(link.PeerId)?.Live?.StreamId;
            _teleLiveDaLigacao[link.SessionId] = live;
            if (!link.IAmSender) ResumoLive.Viewer.Iniciar(link.SessionId);
            if (live == null) return;
            var evento = new JsonObject { ["transmissao"] = live, ["ligacao"] = link.SessionId, ["papel"] = link.IAmSender ? "envia" : "assiste" };
            if (!LigacaoPrivada(link)) // privada: só dados técnicos, sem quem estava com quem (D5)
            {
                evento["outro_install"] = link.PeerId;
                if (_salaDeConta) evento["outro_conta"] = link.PeerId; // na sala com conta o id da sala É o id da conta
                evento["outro_apelido"] = link.PeerNick;
            }
            _teleSessao.Evento("live", evento);
            return;
        }
        if (!link.IAmSender) EmitirResumoLive(ResumoLive.Viewer, link.SessionId);
        if (!link.IAmSender)
            RelatorioAutomatico("fim-assistir-" + NomeParaTelemetria(link), new JsonObject
            {
                ["ligacao"] = link.SessionId, ["amigo"] = NomeParaTelemetria(link),
                ["transmissao"] = _teleLiveDaLigacao.GetValueOrDefault(link.SessionId),
            });
        if (_teleLiveDaLigacao.Remove(link.SessionId, out var t) && t != null)
            _teleSessao.Evento("live_fim", new JsonObject { ["transmissao"] = t, ["ligacao"] = link.SessionId });
        _ligacoesPrivadas.Remove(link.SessionId);
        _tele?.Agora();
    }

    /// <summary>Ao sair pela bandeja: relatório final e último envio (no máximo 2 s, para não segurar o fechamento).</summary>
    void FecharTelemetria()
    {
        _teleTimer.Stop();
        _serieSincronia?.Dispose(); _serieSincronia = null;
        _vigia?.Dispose(); _vigia = null;
        _atalhoProblema?.Dispose();
        _atalhoProblema = null;
        Log.Line -= VigiarFalhas;
        // Só ao sair de verdade (fechar a janela pode ir para a bandeja). Salva o fim e o lote parcial antes do último envio.
        _filmeForm?.Fechar();
        _filmeSerieEncerrada = true;
        GravarSerieFilme();
        if (_tele == null) return;
        // saiu ainda ao vivo: fecha a live no índice (com o relatório dela); as ligações já foram encerradas, cada uma com o seu
        if (_streamId != null) TelemetriaLive(comecou: false);
        else if (_sender.Running) RelatorioAutomatico("saindo");
        _teleSerie?.Descarregar();
        Log.Info("[telemetria] fechando: último envio.");
        _tele.Fechar(TimeSpan.FromSeconds(2));
        _tele.Dispose();
        _tele = null;
    }

    /// <summary>
    /// Teste automático: espera o primeiro envio ser confirmado (o indicador sai do cinza "Preparando") antes de gravar o relatório. Na bateria de
    /// 11/11, B e C gravaram o relatório com o primeiro envio ainda a caminho (três instâncias mandando log e perfil ao wrangler dev ao mesmo tempo)
    /// e o teste leu o cinza como falha.
    /// </summary>
    async Task TesteEsperarTelemetriaAsync(int maxS = 10)
    {
        var ate = DateTime.Now.AddSeconds(maxS);
        while (_tele is { } t && t.Situacao().Estado == EstadoEnvio.Neutro && DateTime.Now < ate) await Task.Delay(200);
    }

    static bool TesteRetomarTelemetria => Environment.GetEnvironmentVariable("STREEMU_TEST_TELEMETRIA_RETOMAR") == "1";

    /// <summary>
    /// Teste da retomada (spec 009): o último envio ao fechar tem no máximo 2 s e o que não coube fica no disco. Esta instância abre na mesma pasta de
    /// teste, manda o que as sessões anteriores deixaram e fecha assim que o servidor confirma tudo (indicador verde), gravando o relatório.
    /// </summary>
    async Task RunTestRetomadaAsync()
    {
        var ate = DateTime.Now.AddSeconds(45);
        while (DateTime.Now < ate && _tele?.Situacao().Estado != EstadoEnvio.Ok) await Task.Delay(200);
        Log.Info($"teste: retomada da telemetria {(_tele?.Situacao().Estado == EstadoEnvio.Ok ? "confirmada pelo servidor" : "sem confirmação em 45 s")}.");
        File.WriteAllText(Path.Combine(TestDir!, TestRole + "-relatorio.json"), BuildReport());
        ExitApp();
    }

    /// <summary>Entra no "Copiar relatório": liga o relatório colado no Discord ao PC e à sessão no servidor.</summary>
    JsonObject? TelemetriaParaRelatorio()
    {
        // o que o indicador da barra lateral está mostrando agora (os testes locais conferem: verde com servidor, vermelho sem)
        var s = _tele?.Situacao() ?? _teleNaoLigou;
        if (_tele == null) return s == null ? null : new JsonObject { ["indicador"] = s.Estado.ToString(), ["indicador_texto"] = s.Titulo };
        return new JsonObject
        {
            ["pc"] = _tele.Pc, ["sessao"] = _teleSessao?.Id, ["ligada"] = _tele.Habilitado,
            ["ultimo_envio"] = _tele.UltimoOk?.ToString("s"), ["erro"] = _tele.UltimoErro, ["relogio_dif_ms"] = _tele.RelogioDifMs,
            ["indicador"] = s?.Estado.ToString(), ["indicador_texto"] = s?.Titulo, ["indicador_detalhe"] = s?.Detalhe,
        };
    }

}
