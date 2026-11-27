using System.Reflection;
using Streemu;
using Streemu.Media;
using Streemu.Net;
using Streemu.Ui;
using Streemu.Ui.Multistream;

static class Behavior
{
    static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Native()
    {
        // Inicialização real, minimizada e local. UI_ONLY impede listagem/captura de miniaturas.
        using var form = new MainForm(true) { WindowState = FormWindowState.Minimized, ShowInTaskbar = false };
        Exception? failure = null;
        using var timer = new System.Windows.Forms.Timer { Interval = 400 };
        // A inicialização de fonte/DPI pode ajustar a janela durante Show; o cenário de teste deve continuar minimizado.
        form.Shown += (_, _) => { form.WindowState = FormWindowState.Minimized; timer.Start(); };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                Check(form.WindowState == FormWindowState.Minimized, "Teste perdeu a minimização");
                Call(form, "ShowView", false);
                var home = Field<Panel>(form, "_homeHost"); var side = Field<SidePanel>(form, "_side");
                Check(home.Left >= side.Right, "Montagem nativa cobriu a lateral");
                Call(form, "OpenGoLive", false, null!);
                var preparation = Field<GoLiveView>(form, "_preparation");
                Check(preparation.Visible && Field<Panel>(form, "_preparationHost").Visible && !home.Visible, "Preparação nativa não substituiu a Home");
                Check(!Field<System.Windows.Forms.Timer>(preparation, "_watch").Enabled, "Preparação minimizada continuou atualizando fontes");
                Call(form, "ReturnFromPreparation");
                Check(home.Visible && !Field<Panel>(form, "_preparationHost").Visible, "Retorno nativo não restaurou a Home");
                Check(!Field<StreamSender>(form, "_sender").Running, "Navegação iniciou captura");
                Call(form, "AbrirFilme");
                Check(Field<Panel>(form, "_filmeHost").Visible && !home.Visible, "Cinema não entrou na navegação nativa");
                Check(Field<Streemu.Filme.FilmeTela>(form, "_filmeForm").Player == null, "Abrir Cinema iniciou player");
                var cinema = Field<Streemu.Filme.FilmeTela>(form, "_filmeForm");
                using var presentation = new Streemu.Filme.StreamTela("Cinema isolado", "srt://127.0.0.1:1", 90, "", () => null);
                cinema.MostrarTransmissao(presentation);
                var video = Field<Panel>(presentation, "_tela"); var videoHandle = video.Handle;
                Check(!presentation.TopLevel && cinema.Ocupado, "Transmissão não foi integrada ao Cinema nativo");
                Call(form, "ShowSettings");
                Check(!presentation.Visible && !presentation.IsDisposed, "Configurações encerrou a apresentação");
                Call(form, "AbrirFilme");
                Check(Field<Streemu.Filme.FilmeTela>(form, "_filmeForm") == cinema, "Retorno recriou a página com participação");
                Field<Button>(Field<Streemu.Ui.CinemaView>(cinema, "_cinema"), "_resume").PerformClick();
                Call(form, "OpenGoLive", false, null!);
                Check(!presentation.Visible && !presentation.IsDisposed, "Preparação encerrou a apresentação");
                Call(form, "ReturnFromPreparation");
                Check(presentation.Visible && video.Handle == videoHandle && form.WindowState == FormWindowState.Minimized,
                    "Voltar da preparação perdeu HWND, página ou minimização");
                presentation.Close();
                Call(form, "ShowView", false);
                NovidadesNaJanela(form, home, side);
                ApoiarNaJanela(form, home);
            }
            catch (Exception ex) { failure = ex; }
            finally { form.Close(); }
        };
        Application.Run(form);
        if (failure != null) throw failure;
        Console.WriteLine("OK: Load/Shown e navegação reais em modo local minimizado, sem transmissão, player, miniaturas ou estado pessoal.");
    }
    /// <summary>
    /// Plano 027, etapa 4: o item "Novidades" abre a página no lugar do Início, o selo "NOVO" continua enquanto a pessoa lê e some quando ela sai; o Cinema não deixa a
    /// página por baixo; um estado antigo (sem as chaves de agora) volta a mostrar o selo. Sem gravar nada: o MainForm(true) é local e não salva o estado (regra 3 do AGENTS.md).
    /// </summary>
    static void NovidadesNaJanela(MainForm form, Panel home, SidePanel side)
    {
        var host = Field<Panel>(form, "_novidadesHost");
        var estado = Field<Streemu.Util.AppState>(form, "_state");
        Check(side.NovidadesVisivel, "O item Novidades não apareceu na barra lateral");
        Check(!side.NovidadesNovas && !host.Visible, "A primeira vez com Novidades mostrou selo ou a página");
        estado.NovidadesVistas = ["chave-de-antes"]; // estado de antes das chaves de hoje: tudo o que existe conta como não visto
        Call(form, "AtualizarSeloNovidades");
        Check(side.NovidadesNovas, "Estado antigo não acendeu o selo de Novidades");
        Call(form, "MostrarNovidades");
        Check(host.Visible && !home.Visible && Field<Streemu.Ui.NovidadesTela>(form, "_novidadesTela").Visible, "Novidades não substituiu o Início");
        Check(side.NovidadesNovas, "O selo sumiu ao ENTRAR em Novidades (a pessoa precisa das bolinhas enquanto lê)");
        Call(form, "ShowView", false);
        Check(home.Visible && !host.Visible, "Voltar ao Início não escondeu Novidades");
        Check(!side.NovidadesNovas, "O selo continuou depois de sair de Novidades");
        Check(Streemu.Util.Novidades.NaoLidas(Streemu.Util.Novidades.Embutidas(), estado.NovidadesVistas) == 0, "Sair de Novidades não marcou as novidades como vistas");
        Call(form, "MostrarNovidades");
        Call(form, "AbrirFilme");
        Check(!host.Visible && !Field<bool>(form, "_novidadesView"), "Novidades ficou por baixo do Cinema");
        Call(form, "ShowSettings");
        Call(form, "MostrarNovidades");
        Check(host.Visible && !Field<Panel>(form, "_settingsHost").Visible, "Novidades não saiu de cima das Configurações");
        Call(form, "ShowView", false);
    }

    /// <summary>
    /// Plano 035: o item "Apoiar" abre a tela no lugar do Início e não pede conta; o Cinema, as Configurações e o Início não deixam a tela por baixo, e o botão abre só
    /// a página combinada (o navegador é falso: nada abre de verdade).
    /// </summary>
    static void ApoiarNaJanela(MainForm form, Panel home)
    {
        var host = Field<Panel>(form, "_apoiarHost");
        var tela = Field<Streemu.Ui.ApoiarTela>(form, "_apoiarTela");
        var abertos = new List<string>();
        tela.AbrirEndereco = url => abertos.Add(url);
        Check(!host.Visible, "A tela Apoiar apareceu sem ninguém pedir");
        Call(form, "MostrarApoiar");
        Check(host.Visible && !home.Visible && tela.Visible && Field<bool>(form, "_apoiarView"), "Apoiar não substituiu o Início");
        tela.Botao.PerformClick();
        Check(abertos.SequenceEqual([Streemu.Util.UiBrand.UrlApoio]), "O botão da tela Apoiar não abriu a página de apoio");
        Call(form, "ShowView", false);
        Check(home.Visible && !host.Visible && !Field<bool>(form, "_apoiarView"), "Voltar ao Início não escondeu Apoiar");
        Call(form, "MostrarApoiar");
        Call(form, "AbrirFilme");
        Check(!host.Visible && !Field<bool>(form, "_apoiarView"), "Apoiar ficou por baixo do Cinema");
        Call(form, "ShowSettings");
        Call(form, "MostrarApoiar");
        Check(host.Visible && !Field<Panel>(form, "_settingsHost").Visible, "Apoiar não saiu de cima das Configurações");
        Call(form, "MostrarNovidades");
        Check(!host.Visible && !Field<bool>(form, "_apoiarView"), "Novidades não tirou o Apoiar da frente");
        Call(form, "ShowView", false);
    }

    public static void Run(string? output)
    {
        if (output != null) Directory.CreateDirectory(output);
        using var form = new MainForm(true);
        var side = Field<SidePanel>(form, "_side");
        using var surface = new Panel { Font = form.Font };
        Field<TextBox>(form, "_nick").Text = "Rafa";
        foreach (var name in new[] { "_homeHost", "_settingsHost", "_preparationHost", "_stageCanvas", "_filmeHost", "_side" })
        {
            var part = Field<Control>(form, name); form.Controls.Remove(part); surface.Controls.Add(part);
        }
        side.Visible = true;
        surface.Size = new Size(1600, 900); surface.CreateControl();
        Call(form, "ShowView", false);
        var cards = Field<LiveCardsView>(form, "_liveCards");
        var friends = Enumerable.Range(0, 40).Select(n => new SideFriend($"amigo{n}", $"Amigo com nome longo {n}", "Jogo de teste", true, n < 2, null)).ToList();
        foreach (var size in new[] { new Size(1600, 900), new Size(1000, 700), new Size(800, 600), new Size(480, 700), new Size(3440, 1440) })
        {
            surface.Size = size;
            Check(side.Visible, "Navegação principal invisível");
            side.Collapsed = size.Width < 1300;
            var settling = System.Diagnostics.Stopwatch.StartNew();
            while (settling.ElapsedMilliseconds < 180) { Application.DoEvents(); Thread.Sleep(5); }
            side.SetProfile("Rafa", null);
            Call(form, "ShowView", false);
            Check(Field<Panel>(form, "_homeHost").Left >= side.Right, "Home cobriu a navegação principal");
            side.SetData(friends, 2, SideView.Home, "Sala de teste");
            foreach (int count in new[] { 0, 1, 2, 5, 30 })
            {
                cards.SetCards(Enumerable.Range(0, count).Select(n => new LiveCard($"amigo{n}", $"Amigo com nome longo {n}", "Jogo de teste", "1080p · 60 FPS", null, n == 0)).ToList());
                Field<Label>(form, "_emptyLive").Visible = count == 0;
                var home = Field<HomeView>(form, "_homePage");
                home.PerformLayout();
                Check(!Field<Panel>(home, "_scroll").HorizontalScroll.Visible, $"Home com rolagem horizontal: {size}, {count}");
                // Mumu dormindo: só no Início vazio, inteira dentro da área visível e centrada com a frase; com card, some e libera a arte.
                var mumu = Field<ArtView>(home, "_mumu");
                var scroll = Field<Panel>(home, "_scroll");
                var empty = Field<Label>(form, "_emptyLive");
                if (count > 0) Check(!mumu.Visible && !mumu.Loaded, $"Mumu apareceu (ou ficou na memória) com {count} cards: {size}");
                else
                {
                    Check(mumu.Visible || size.Height < 700, $"Mumu ausente no Início vazio: {size}");
                    if (mumu.Visible)
                        Check(mumu.Top >= 0 && mumu.Bottom <= empty.Top && empty.Bottom <= scroll.ClientSize.Height && mumu.Right <= scroll.ClientSize.Width
                            && Math.Abs(mumu.Left + mumu.Width / 2 - (empty.Left + empty.Width / 2)) <= 2, $"Mumu fora do lugar: {size}, Mumu {mumu.Bounds}, frase {empty.Bounds}");
                }
                for (int i = 0; i < count; i++)
                {
                    var rect = (Rectangle)Call(cards, "CardRect", i)!;
                    Check(rect.Left >= 0 && rect.Right <= cards.Width && rect.Bottom <= cards.Height, "Card fora dos limites");
                    Check(rect.Width <= Streemu.Util.UiScale.Dp(cards, count <= 2 ? 640 : 480), "Card esticou além do teto de descoberta");
                }
                if (output != null && count is 0 or 2 or 5) Render(surface, Path.Combine(output, $"home-{size.Width}x{size.Height}-{count}.png"));
            }
            Call(side, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, 20, 250, -12000));
            Check(Field<int>(side, "_friendScroll") > 0 || size.Height >= 1440, "Amigos não rolaram");
            Call(form, "OpenGoLive", false, null!);
            var view = Field<GoLiveView>(form, "_preparation");
            var source = new CaptureSource { Handle = (IntPtr)123456789, IsWindow = true, IsFullScreen = true, Label = "Jogo de teste", ProcessName = "Teste" };
            Set(view, "_sources", new List<CaptureSource> { source }); Call(view, "ChooseInitialSource");
            Field<Segmented>(view, "_resSeg").Selected = 3;
            view.RefreshFriends([new Member("amigo", "Amigo de teste", "", null)]); view.Invite("amigo");
            view.SetFailure("Não foi possível capturar o jogo. Abra o jogo e tente novamente.");
            view.PerformLayout();
            var body = Field<Panel>(view, "_body");
            // Item 10 do diagnóstico de UX (#138): com uma fonte só, a lista mostra a peça inteira e não reserva a altura de duas fileiras.
            var grid = Field<Panel>(view, "_grid");
            var only = grid.Controls.Cast<Control>().First();
            Check(grid.Height < view.LogicalToDeviceUnits(240) && only.Bottom <= grid.ClientSize.Height, $"Lista de fontes não acompanhou uma fonte: {size}, lista {grid.Size}, peça {only.Bounds}");
            Check(!body.HorizontalScroll.Visible, $"Preparação com rolagem horizontal: {size}, corpo {body.ClientSize}, " + string.Join("; ", body.Controls.Cast<Control>().Select(c => c.GetType().Name + " " + c.Text + " " + c.Bounds)));
            var go = Field<Button>(view, "_go");
            var footer = Field<Panel>(view, "_footer");
            Check(footer.Bottom <= view.Height && go.Bottom <= footer.Height && go.Width > 0 && footer.Top >= body.Bottom, "Ação principal cortada ou coberta");
            Check(view.SelectedPreset.IsSource && view.Privada && view.ConvidadosIds.SequenceEqual(["amigo"]), "Original/privacidade/convite perderam significado");
            if (output != null) Render(surface, Path.Combine(output, $"preparacao-{size.Width}x{size.Height}.png"));
            Call(form, "ReturnFromPreparation");
            Call(form, "OpenGoLive", false, null!);
            Check(ReferenceEquals(view, Field<GoLiveView>(form, "_preparation")) && view.Privada && view.SelectedPreset.IsSource && view.SelectedSource == source, "Voltar perdeu o rascunho");
            // Fonte inexistente falha antes de iniciar captura, rede ou encoder.
            var submit = (Task)Call(form, "SubmitPreparationAsync", view)!;
            while (!submit.IsCompleted) Application.DoEvents(); submit.GetAwaiter().GetResult();
            Check(Field<bool>(form, "_preparationView") && Field<Control>(view, "_failure").Visible && view.SelectedSource == source && view.Privada, "Falha retornou ou perdeu escolhas");
            Check(!Field<StreamSender>(form, "_sender").Running, "Abrir preparação iniciou transmissão");
            Call(form, "ReturnFromPreparation");
        }
        // Objetos do player sem Start: prova de navegação/organização, sem áudio ou vídeo real.
        using var stage = new PlayerStage(form);
        Set(form, "_playerStage", stage);
        var a = new Viewer(); var b = new Viewer();
        stage.Add(a); stage.Add(b); stage.ToggleEmphasis(b);
        var original = Field<List<Viewer>>(stage, "_tiles").ToArray();
        var links = Field<Dictionary<string, PeerLink>>(form, "_links");
        Call(form, "OpenGoLive", false, null!);
        Check(!form.StageShown && stage.Count == 2 && stage.IsEmphasized(b), "Preparação desfez o palco");
        form.StreamsChanged(2);
        Check(!form.StageShown && Field<bool>(form, "_preparationView"), "Mudança do palco interrompeu a preparação");
        Call(form, "ReturnFromPreparation");
        Check(form.StageShown && original.SequenceEqual(Field<List<Viewer>>(stage, "_tiles")) && stage.IsEmphasized(b), "Retorno perdeu ordem ou destaque");
        Check(ReferenceEquals(links, Field<Dictionary<string, PeerLink>>(form, "_links")), "Navegação trocou ligações");
        Call(form, "ShowSettings"); Call(form, "OpenGoLive", false, null!); Call(form, "ReturnFromPreparation");
        Check(Field<bool>(form, "_settingsView"), "Retorno não recuperou Configurações");
        OfflineEApelidos(form, output); // plano 019, etapa 7
        AtividadesNoPainel(form, output); // plano 019, etapa 9 (no painel do ícone desde 2026-11-18)
        PlayerControls(form, stage, a);
        using (var navigation = new SidePanel { Size = new Size(80, 600) })
        {
            int requested = 0; navigation.TransmitClicked += () => requested++;
            navigation.SetData([], 0, SideView.Home, "");
            var access = navigation.AccessibilityObject;
            var transmit = Enumerable.Range(0, access.GetChildCount()).Select(access.GetChild).Single(c => c?.Name == "Transmitir")!;
            transmit.DoDefaultAction();
            Check(requested == 1, "Ação acessível de transmitir não respondeu");
            // #137: sem live, a linha única Transmitir explica o que faz (a dica do #137) e o clique do mouse chega ao mesmo evento.
            var built = Call(navigation, "BuildRows")!; Set(navigation, "_rows", built); // SetData igual ao estado inicial não remonta as linhas
            var rowsIdle = ((System.Collections.IEnumerable)built).Cast<object>().ToList();
            string KindOf(object row) => row.GetType().GetProperty("Kind")!.GetValue(row)!.ToString()!;
            var goBox = (Rectangle)rowsIdle.Single(r => KindOf(r) == "Transmit").GetType().GetProperty("Bounds")!.GetValue(rowsIdle.Single(r => KindOf(r) == "Transmit"))!;
            Call(navigation, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, goBox.X + goBox.Width / 2, goBox.Y + goBox.Height / 2, 0));
            Check(Field<ToolTip>(navigation, "_tip").GetToolTip(navigation) == "Escolher o que transmitir e começar a live", "Linha Transmitir sem a dica do #137");
            Call(navigation, "OnMouseClick", new MouseEventArgs(MouseButtons.Left, 1, goBox.X + goBox.Width / 2, goBox.Y + goBox.Height / 2, 0));
            Check(requested == 2, "Clique em Transmitir não chegou ao evento único");
            int stopped = 0; navigation.StopLiveClicked += () => stopped++;
            foreach (var size in new[] { new Size(290, 600), new Size(80, 600), new Size(80, 480) })
            {
                navigation.Size = size; navigation.SetData([], 2, SideView.Stage, "", new SideSelf("Jogo de teste", 2, false));
                var rows = ((System.Collections.IEnumerable)Call(navigation, "BuildRows")!).Cast<object>().ToList();
                string Kind(object row) => row.GetType().GetProperty("Kind")!.GetValue(row)!.ToString()!;
                Rectangle Bounds(object row) => (Rectangle)row.GetType().GetProperty("Bounds")!.GetValue(row)!;
                var stop = rows.Single(r => Kind(r) == "StopLive");
                var own = rows.Single(r => Kind(r) == "Transmit");
                Check(Bounds(stop).Top >= Bounds(own).Bottom && Bounds(stop).Bottom < size.Height, "Encerrar live perdeu o lugar sob Sua transmissão");
                int before = stopped; var box = Bounds(stop);
                Call(navigation, "OnMouseClick", new MouseEventArgs(MouseButtons.Left, 1, box.X + box.Width / 2, box.Y + box.Height / 2, 0));
                int mouse = stopped;
                Set(navigation, "_focusRow", rows.IndexOf(stop)); Call(navigation, "OnKeyDown", new KeyEventArgs(Keys.Space));
                int keyboard = stopped;
                var stopAccess = Enumerable.Range(0, access.GetChildCount()).Select(access.GetChild).Single(c => c?.Name == "Encerrar live")!;
                stopAccess.DoDefaultAction(); Check(stopped == before + 3, $"Encerrar live não encaminhou mouse/teclado/acessibilidade em {size}: {before}/{mouse}/{keyboard}/{stopped}; DPI {navigation.DeviceDpi}; linhas no ponto do clique: {string.Join(", ", rows.Where(r => Bounds(r).Contains(box.X + box.Width / 2, box.Y + box.Height / 2)).Select(r => Kind(r) + " " + Bounds(r)))}");
            }
        }
        var resolution = Field<Segmented>(Field<GoLiveView>(form, "_preparation"), "_resSeg");
        resolution.Selected = 0; Call(resolution, "OnKeyDown", new KeyEventArgs(Keys.Right));
        Check(resolution.Selected == 1, "Seletor de qualidade não respondeu ao teclado");
        DateTime now = DateTime.UtcNow;
        using (var thumbs = new LiveThumbnails(() => now))
        using (var image = new Bitmap(192, 108))
        using (var bytes = new MemoryStream())
        {
            image.Save(bytes, System.Drawing.Imaging.ImageFormat.Jpeg);
            var message = ThumbnailMessage.Image("transmissao", bytes.ToArray());
            Check(thumbs.Apply(message), "Miniatura válida não foi recebida");
            var originalImage = thumbs.Get("transmissao"); now += TimeSpan.FromSeconds(5);
            Check(!thumbs.Apply(message) && ReferenceEquals(originalImage, thumbs.Get("transmissao")), "JPEG repetido foi decodificado novamente");
            for (int i = 0; i < 40; i++) thumbs.Apply(ThumbnailMessage.Image($"live{i}", bytes.ToArray()));
            Check(thumbs.Count <= ThumbnailLimits.MaxStreams, "Cache ultrapassou o limite");
            thumbs.Dispose();
            Check(!thumbs.Apply(ThumbnailMessage.Image("depois", bytes.ToArray())) && thumbs.Count == 0, "Decodificação reabriu cache descartado");
        }
        IdentityBehavior.Run(output == null ? null : Path.Combine(output, "identidade"));
        Console.WriteLine($"OK: Home 0/1/2/5/30 cards em 5 tamanhos, preparação/voltar/falha com escolhas privadas, lista de fontes na altura de uma fonte, Original, rodapé acessível, amigos roláveis, palco/ordem/destaque preservados, controles do player com nome acessível e saída da tela cheia identificável/Esc da janela e do vídeo (decisão do gancho). DPI {surface.DeviceDpi}. Sem Show/Load, player, captura, áudio ou rede.");
    }
    /// <summary>
    /// Controles do player de lives sem Start: o botão de tela cheia troca ícone/dica/nome conforme o palco, os botões de ícone têm nome acessível, e o Esc
    /// da janela principal sai da tela cheia do palco restaurando moldura e lateral. A janela não é mostrada e o ffplay não existe: do Esc com foco no vídeo
    /// só a decisão do StageEscape é exercitada (janela fictícia); o gancho real com o ffplay em foco precisa de teste com player.
    /// </summary>
    static void PlayerControls(MainForm form, PlayerStage stage, Viewer viewer)
    {
        var looks = new[] { PlayerOverlay.FullscreenLook(false, false), PlayerOverlay.FullscreenLook(true, false), PlayerOverlay.FullscreenLook(true, true) };
        Check(looks[1].Glyph != looks[2].Glyph && looks[2].Text == "Sair da tela cheia" && looks[1].Text == "Tela cheia" && looks[0].Text.Contains("(F)"),
            "Tela cheia e saída com o mesmo ícone ou sem texto");
        var side = Field<SidePanel>(form, "_side");
        Call(form, "ShowView", true);
        Check(form.StageShown && !stage.Fullscreen, "Palco não abriu em janela");
        var border = form.FormBorderStyle;
        using var overlay = PlayerOverlay.ShowFor(viewer, () => "1080p · 60 FPS", () => [], stage: stage);
        Button Button(string name) => Field<Button>(overlay, name);
        Check(Button("_close").AccessibleName == "Parar de assistir esta live" && Button("_mute").AccessibleName is "Silenciar (M)" or "Ativar o som (M)"
              && !string.IsNullOrEmpty(Button("_mini").AccessibleName) && !string.IsNullOrEmpty(Button("_liveButton").AccessibleName)
              && Field<VolumeSlider>(overlay, "_volume").AccessibleName == "Volume", "Botões de ícone sem nome acessível");
        Check(Button("_fullscreen").AccessibleName == "Tela cheia" && Button("_fullscreen").Text == looks[1].Glyph, "Botão de tela cheia fora do estado em janela");
        form.ToggleFullscreen();
        Check(stage.Fullscreen && !side.Visible, "Palco não entrou em tela cheia");
        Call(overlay, "RefreshFullscreen");
        Check(Button("_fullscreen").AccessibleName == "Sair da tela cheia" && Button("_fullscreen").Text == looks[2].Glyph, "Saída da tela cheia sem ícone de recolher ou texto");
        var esc = new Message { Msg = 0x100, WParam = (IntPtr)Keys.Escape };
        Check((bool)Call(form, "ProcessCmdKey", esc, Keys.Escape)!, "Esc da janela principal não saiu da tela cheia");
        Check(!stage.Fullscreen && side.Visible && form.FormBorderStyle == border && form.StageShown, "Saída da tela cheia não restaurou moldura, lateral ou palco");
        Check(!(bool)Call(form, "ProcessCmdKey", esc, Keys.Escape)!, "Esc fora da tela cheia foi engolido pela janela");
        Call(overlay, "RefreshFullscreen");
        Check(Button("_fullscreen").AccessibleName == "Tela cheia" && Button("_fullscreen").Text == looks[1].Glyph, "Botão não voltou ao estado em janela");
        // Esc com o foco no vídeo (decisão de 13/11): a decisão do gancho é exercitada direto, com uma janela fictícia, sem instalar gancho de verdade.
        var escape = Field<StageEscape>(stage, "_escape");
        Check(!escape.Installed, "Gancho do Esc ligado fora da tela cheia");
        form.ToggleFullscreen();
        Check(stage.Fullscreen && !escape.Installed, "Gancho do Esc ligado sem janela de vídeo no palco");
        var video = (IntPtr)0x5A5A;
        Set(escape, "_videos", new[] { video });
        Check(!escape.OnKey(0x1B, true, false, (IntPtr)0x1234) && !escape.OnKey(0x1B, true, true, video) && !escape.OnKey(0x51, true, false, video) && stage.Fullscreen,
            "Esc de outra janela, Esc com modificador ou Q foram engolidos");
        Check(escape.OnKey(0x1B, true, false, video) && !stage.Fullscreen && side.Visible && form.FormBorderStyle == border && form.StageShown,
            "Esc no vídeo não saiu da tela cheia do palco");
        Check(escape.OnKey(0x1B, true, false, video) && !stage.Fullscreen, "Esc segurado alternou a tela cheia de novo");
        Check(escape.OnKey(0x1B, false, false, IntPtr.Zero) && !escape.OnKey(0x1B, false, false, IntPtr.Zero), "Soltar o Esc engolido vazou, ou outro soltar foi engolido");
        Check(!escape.Installed, "Gancho do Esc continuou depois de sair da tela cheia");
        overlay.Close();
    }
    static void Render(Control control, string path) { using var image = new Bitmap(control.Width, control.Height); control.DrawToBitmap(image, control.ClientRectangle); image.Save(path); }
    /// <summary>
    /// Plano 019, etapa 7: a seção "OFFLINE — N" da barra (aberta: depois dos online, em ordem; recolhida: não existe), o clique que não assiste, e o apelido local
    /// (tela com o apelido, cabeçalho "apelido (nome real)", log só com o nome real).
    /// </summary>
    static void OfflineEApelidos(MainForm form, string? output)
    {
        var lista = new List<SideFriend>
        {
            new("a1", "Ana", "Jogo de teste", true, false, null), new("b1", "Bia", "online", false, false, null), new("c1", "Caio", "online", false, false, null),
            new("z1", "Zeca", "offline", false, false, null, Offline: true), new("d1", "Duda", "offline", false, false, null, Offline: true),
        }; // a ordem de quem monta a lista (RefreshSide) já põe os offline por último; a barra só desenha na ordem recebida
        string KindOf(object row) => row.GetType().GetProperty("Kind")!.GetValue(row)!.ToString()!;
        Rectangle BoundsOf(object row) => (Rectangle)row.GetType().GetProperty("Bounds")!.GetValue(row)!;
        string TextOf(object row) => (string)row.GetType().GetProperty("Text")!.GetValue(row)!;
        SideFriend? FriendOf(object row) => (SideFriend?)row.GetType().GetProperty("Friend")!.GetValue(row);
        foreach (bool trilho in new[] { false, true })
        {
            using var painel = new SidePanel { Size = new Size(trilho ? 80 : 290, 1100) }; // 900 cabia com 5 amigos antes do item Apoiar (plano 035); com ele a lista perdia o último a 125 %
            painel.CreateControl();
            painel.SetData(lista, 0, SideView.Home, "Sala");
            var linhas = ((System.Collections.IEnumerable)Call(painel, "BuildRows")!).Cast<object>().ToList();
            var amigos = linhas.Where(r => KindOf(r) == "Friend").ToList();
            var secao = linhas.Where(r => KindOf(r) == "Section" && TextOf(r).StartsWith("OFFLINE")).ToList();
            if (trilho)
            {
                Check(secao.Count == 0, "O trilho não devia ter a seção OFFLINE");
                Check(amigos.Count == 3 && amigos.All(r => !FriendOf(r)!.Offline), $"O trilho devia mostrar só os 3 que não estão offline, mostrou {amigos.Count}");
                continue;
            }
            Check(secao.Count == 1 && TextOf(secao[0]) == "OFFLINE — 2", $"A seção devia ser 'OFFLINE — 2': {string.Join("|", secao.Select(TextOf))}");
            var online = amigos.Where(r => !FriendOf(r)!.Offline).ToList(); var offline = amigos.Where(r => FriendOf(r)!.Offline).ToList();
            Check(offline.Count == 2 && online.Count == 3, $"Devia ter 3 online e 2 offline: {online.Count}/{offline.Count}");
            Check(BoundsOf(secao[0]).Top >= online.Max(r => BoundsOf(r).Bottom) && BoundsOf(secao[0]).Bottom <= offline.Min(r => BoundsOf(r).Top), "A seção OFFLINE devia ficar entre os online e os offline");
            Check(offline.Select(r => FriendOf(r)!.Nick).SequenceEqual(["Zeca", "Duda"]), "A barra desenha na ordem recebida");
            // clique na linha de um offline (fora da foto) não abre nada; clique na foto abre o menu dele
            int assistir = 0, menu = 0; painel.FriendClicked += _ => assistir++; painel.FriendAvatarClicked += (_, _) => menu++;
            var alvo = BoundsOf(offline[0]);
            Call(painel, "OnMouseClick", new MouseEventArgs(MouseButtons.Left, 1, alvo.Right - 20, alvo.Y + alvo.Height / 2, 0));
            Check(assistir == 0, "Clique num offline não pode tentar assistir");
            Call(painel, "OnMouseClick", new MouseEventArgs(MouseButtons.Left, 1, alvo.X + painel.LogicalToDeviceUnits(6) + 8, alvo.Y + alvo.Height / 2, 0));
            Check(menu == 1, "Clique na foto de um offline devia abrir o menu");
            if (output != null) { using var bmp = new Bitmap(painel.Width, painel.Height); painel.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height)); bmp.Save(Path.Combine(output, "barra-offline.png")); }
        }
        // sala antiga: sem offline, nada de seção
        using (var antigo = new SidePanel { Size = new Size(290, 900) })
        {
            antigo.CreateControl(); antigo.SetData(lista.Where(f => !f.Offline).ToList(), 0, SideView.Home, "Sala");
            var linhas = ((System.Collections.IEnumerable)Call(antigo, "BuildRows")!).Cast<object>().ToList();
            Check(linhas.All(r => !(KindOf(r) == "Section" && TextOf(r).StartsWith("OFFLINE"))), "Sem offline a seção não devia aparecer");
        }
        // apelido local
        var apelidos = (Streemu.Util.ApelidosLocais)typeof(MainForm).GetProperty("Apelidos", Hidden)!.GetValue(form)!;
        Field<Dictionary<string, string>>(form, "_nomesReais")["z1"] = "Zeca";
        try
        {
            var nomeNaTelaM = typeof(MainForm).GetMethod("NomeNaTela", Hidden, [typeof(Member)])!;
            string nomeNaTela(Member m) => (string)nomeNaTelaM.Invoke(form, [m])!;
            var membro = new Member("z1", "Zeca", "", null);
            Check((string)nomeNaTela(membro)! == "Zeca", "Sem apelido a tela mostra o nome real");
            apelidos.Definir("z1", "Zequinha");
            Check((string)nomeNaTela(membro)! == "Zequinha", "Com apelido a tela mostra o apelido");
            Check((string)Call(form, "CabecalhoDoAmigo", new SideFriend("z1", "Zequinha", "offline", false, false, null, true))! == "Zequinha (Zeca)", "O cabeçalho do menu devia ser 'apelido (nome real)'");
            Check(Streemu.Util.Log.Filtro is { } filtro && filtro("Zequinha saiu.") == "Zeca saiu.", "O log devia trocar o apelido pelo nome real");
            Streemu.Util.Log.Info("teste: Zequinha abriu um filme.");
            Check(!string.Join(" ", Streemu.Util.Log.Tail(5)).Contains("Zequinha"), "O apelido chegou ao log");
        }
        finally { apelidos.Tirar("z1"); }
        Check(Streemu.Util.Log.Filtro == null, "Sem apelidos o filtro do log devia sair");
    }

    /// <summary>
    /// Plano 019, etapa 9: a atividade recente saiu do Início (20 linhas poluíam a tela, 2026-11-18) e foi para o painel do ícone de pulso da barra.
    /// Confere: o Início não tem mais o bloco; o ícone está na fileira de ferramentas; o painel cheio tem altura máxima e rola, sem rolagem horizontal;
    /// o vazio mostra o texto de vazio e fica baixo; o ponto de novidade acende e apaga.
    /// </summary>
    static void AtividadesNoPainel(MainForm form, string? output)
    {
        var home = Field<HomeView>(form, "_homePage");
        Check(!home.GetType().GetFields(Hidden).Any(f => f.FieldType == typeof(AtividadesView)), "O Início ainda tem o bloco de atividades");
        var side = Field<SidePanel>(form, "_side");
        var kinds = ((System.Collections.IEnumerable)Field<object>(side, "_rows")).Cast<object>().Select(r => r.GetType().GetProperty("Kind")!.GetValue(r)!.ToString()).ToList();
        Check(kinds.Contains("Atividade"), "A barra lateral não tem o ícone de atividade");
        side.SetAtividade(true, "2 novidades no grupo. Clique para ver.");
        Check(Field<bool>(side, "_atividadeNova"), "O ponto de novidade não acendeu");
        side.SetAtividade(false, "Atividade recente do grupo");
        Check(!Field<bool>(side, "_atividadeNova"), "O ponto de novidade não apagou");

        long agora = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string[] tipos = ["live", "assistindo", "filme", "entrou", "saiu", "live_fim"];
        var linhas = Enumerable.Range(0, 20).Select(i => new Streemu.Net.LinhaDeAtividade($"Pessoa com nome comprido {i} {tipos[i % 6]} um texto bem longo para testar o corte", $"há {i + 1} min", tipos[i % 6], agora - i * 60000)).ToList();
        using var popup = new AtividadesPopup();
        var item = new Rectangle(100, 600, 40, 30);
        foreach (var caso in new[] { "cheio", "vazio" })
        {
            popup.Preparar(form.Font, item, caso == "cheio" ? linhas : Array.Empty<Streemu.Net.LinhaDeAtividade>(), null);
            Check(popup.Height <= popup.LogicalToDeviceUnits(380), $"Painel de atividades alto demais: {popup.Size}, {caso}");
            Check(popup.AreaDaLista.Width <= popup.AreaVisivel.Width, $"Painel de atividades com rolagem horizontal: lista {popup.AreaDaLista}, área {popup.AreaVisivel}, {caso}");
            Check(caso == "cheio" ? popup.Rola && popup.Linhas.Count == 20 : !popup.Rola && popup.Linhas.Count == 0, $"Rolagem e linhas do painel: {caso}, rola {popup.Rola}, {popup.Linhas.Count} linhas");
            if (output != null) // o Form nunca aberto não desenha os filhos sozinho: desenha cada um no seu lugar
            {
                using var imagem = new Bitmap(popup.Width, popup.Height);
                void Desenhar(Control c, Point em)
                {
                    c.DrawToBitmap(imagem, new Rectangle(em, c.Size));
                    foreach (Control filho in c.Controls)
                        Desenhar(filho, new Point(em.X + filho.Left, em.Y + filho.Top)); // Visible é falso com o Form fechado
                }
                Desenhar(popup, Point.Empty);
                imagem.Save(Path.Combine(output, $"atividades-{caso}.png"));
            }
        }
    }

    static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Hidden)!.GetValue(o)!;
    static void Set(object o, string name, object value) => o.GetType().GetField(name, Hidden)!.SetValue(o, value);
    static object? Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, Hidden)!.Invoke(o, args);
    static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
}
