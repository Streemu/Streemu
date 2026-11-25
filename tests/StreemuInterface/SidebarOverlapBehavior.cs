using System.Reflection;
using Streemu;
using Streemu.Filme;
using Streemu.Media;
using Streemu.Ui;
using Streemu.Ui.Multistream;

/// <summary>
/// Auditoria da barra lateral: a janela real (fora da tela, sem rede, player ou estado pessoal) passa por Início, Configurações, Transmitir (página e coluna),
/// palco com lives, Cinema (lista, entrada e player) em vários tamanhos, com a barra aberta e recolhida. Em cada caso, nenhum controle visível de fora da
/// barra pode ficar embaixo dela, e nenhuma janela nossa (fora a barra flutuante) pode cobrir a barra aberta. Grava uma foto por caso.
/// </summary>
static class SidebarOverlapBehavior
{
    const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    static readonly Size[] Sizes = [new(1600, 900), new(1100, 720), new(860, 600), new(640, 520)];

    public static void Run(string? output)
    {
        if (output != null) Directory.CreateDirectory(output);
        using var form = new MainForm(true) { StartPosition = FormStartPosition.Manual, Location = new Point(-8000, 0), ShowInTaskbar = false };
        var problems = new List<string>();
        Exception? failure = null;
        form.Shown += async (_, _) =>
        {
            try { await AuditAllAsync(form, output, problems); }
            catch (Exception ex) { failure = ex; }
            finally { form.Close(); }
        };
        Application.Run(form);
        if (failure != null) throw failure;
        if (problems.Count > 0) throw new Exception($"{problems.Count} sobreposição(ões) com a barra lateral:\n" + string.Join("\n", problems.Distinct()));
        Console.WriteLine("OK: nenhuma sobreposição com a barra lateral em Início, Configurações, Transmitir (página e coluna), palco, multistream e Cinema (lista, entrada, player), 4 tamanhos, barra aberta e recolhida; a barra flutuante não abre sobre vídeo nem sobre menu da barra.");
    }

    static async Task AuditAllAsync(MainForm form, string? output, List<string> problems)
    {
        form.Location = new Point(-8000, 0);
        var side = Field<SidePanel>(form, "_side");
        foreach (var size in Sizes)
            foreach (bool rail in new[] { false, true })
            {
                form.Size = size;
                await SettleAsync();
                async Task Case(string name, Action show)
                {
                    show();
                    side.Collapsed = rail;
                    await SettleAsync();
                    string label = $"{name} {size.Width}x{size.Height} {(rail ? "recolhida" : "aberta")}";
                    problems.AddRange(Audit(form, side).Select(p => $"{label}: {p}"));
                    // sem vídeo na tela, o trilho continua abrindo a barra flutuante ao passar o mouse; nas Configurações ela cobria as categorias
                    // (vídeos de 2026-11-16): ali o mouse abre a barra encaixada, empurrando
                    bool settings = name == "ajustes";
                    if (rail && !settings && !Floating(form)) problems.Add($"{label}: barra flutuante bloqueada numa página sem vídeo");
                    if (rail && settings) await CheckOpenByMouseAsync(form, side, label, output == null ? null : Path.Combine(output, $"ajustes-{size.Width}x{size.Height}-mouse.png"), problems);
                    if (output != null) Render(form, Path.Combine(output, $"{name}-{size.Width}x{size.Height}-{(rail ? "trilho" : "aberta")}.png"));
                }
                await Case("inicio", () => Call(form, "ShowView", false));
                await Case("ajustes", () => Call(form, "ShowSettings"));
                await Case("transmitir", () => Call(form, "OpenGoLive", false, null!));
                Call(form, "ReturnFromPreparation");
                await Case("cinema-lista", () => Call(form, "AbrirFilme"));
                await Case("cinema-entrada", () => Field<Button>(Field<CinemaView>(Field<FilmeTela>(form, "_filmeForm"), "_cinema"), "_create").PerformClick());
                Field<Button>(Field<CinemaView>(Field<FilmeTela>(form, "_filmeForm"), "_cinema"), "_back").PerformClick();
            }
        await SequencesAsync(form, side, output, problems);
        await MenuCaseAsync(form, side, problems);
        await StageCasesAsync(form, side, output, problems);
        await CinemaPlayerCasesAsync(form, side, output, problems);
    }

    // palco com 1 e 3 lives (os vídeos não abrem: só o lugar deles e os cabeçalhos) e a preparação em coluna ao lado
    static async Task StageCasesAsync(MainForm form, SidePanel side, string? output, List<string> problems)
    {
        using var stage = new PlayerStage(form);
        Set(form, "_playerStage", stage);
        foreach (int count in new[] { 1, 3 })
        {
            while (stage.Count < count) stage.Add(new Viewer());
            form.StreamsChanged(count);
            foreach (var size in Sizes)
                foreach (bool rail in new[] { false, true })
                {
                    form.Size = size;
                    Call(form, "ShowView", true);
                    side.Collapsed = rail;
                    await SettleAsync();
                    string label = $"palco-{count} {size.Width}x{size.Height} {(rail ? "recolhida" : "aberta")}";
                    problems.AddRange(Audit(form, side).Select(p => $"{label}: {p}"));
                    problems.AddRange(VideoUnderSide(form, side).Select(p => $"{label}: {p}"));
                    problems.AddRange(StageBands(form, stage).Select(p => $"{label}: {p}"));
                    if (rail && Floating(form)) problems.Add($"{label}: a barra flutuante abriria por cima das lives");
                    if (output != null) Render(form, Path.Combine(output, $"palco{count}-{size.Width}x{size.Height}-{(rail ? "trilho" : "aberta")}.png"));
                    Call(form, "OpenGoLive", false, null!);
                    side.Collapsed = rail;
                    await SettleAsync();
                    label = $"palco-{count}+transmitir {size.Width}x{size.Height} {(rail ? "recolhida" : "aberta")}";
                    problems.AddRange(Audit(form, side).Select(p => $"{label}: {p}"));
                    problems.AddRange(VideoUnderSide(form, side).Select(p => $"{label}: {p}"));
                    if (output != null) Render(form, Path.Combine(output, $"palco{count}-transmitir-{size.Width}x{size.Height}-{(rail ? "trilho" : "aberta")}.png"));
                    Call(form, "ReturnFromPreparation");
                }
        }
        Set(form, "_playerStage", null!);
        form.StreamsChanged(0);
    }

    // Com uma live o vídeo ocupa o palco inteiro e o cabeçalho vai por cima dele (some com a barra); com várias, cada uma tem a faixa do cabeçalho e nenhuma
    // vai por cima. Nenhuma faixa fixa embaixo: a barra de controles fica sobre o vídeo e some (2026-11-16).
    static IEnumerable<string> StageBands(MainForm form, PlayerStage stage)
    {
        var tiles = Field<List<Viewer>>(stage, "_tiles");
        if (form.VideoArea is not { } area) yield break;
        if (tiles.Count == 1)
        {
            if (stage.TargetOf(tiles[0]) is not { } video) { yield return "a live sozinha ficou sem lugar no palco"; yield break; }
            if (area.Height - video.Height > 2 && area.Width - video.Width > 2) yield return $"a live sozinha não ocupa o palco ({video} em {area}): sobrou faixa de cabeçalho ou de controles";
            if (stage.HeaderOver(tiles[0]) is not { } header || header.Area.Y != video.Y) yield return "a live sozinha ficou sem cabeçalho por cima do vídeo";
            yield break;
        }
        if (tiles.Any(t => stage.HeaderOver(t) != null)) yield return "com várias lives um cabeçalho foi por cima do vídeo (deveria ficar na faixa)";
    }

    static async Task CinemaPlayerCasesAsync(MainForm form, SidePanel side, string? output, List<string> problems)
    {
        Call(form, "AbrirFilme");
        var cinema = Field<FilmeTela>(form, "_filmeForm");
        using var presentation = new StreamTela("Filme de teste", "srt://127.0.0.1:1", 5400, "", () => null, "Ana");
        cinema.MostrarTransmissao(presentation);
        foreach (var size in Sizes)
            foreach (bool rail in new[] { false, true })
            {
                form.Size = size;
                side.Collapsed = rail;
                await SettleAsync();
                string label = $"cinema-player {size.Width}x{size.Height} {(rail ? "recolhida" : "aberta")}";
                problems.AddRange(Audit(form, side).Select(p => $"{label}: {p}"));
                if (rail && Floating(form)) problems.Add($"{label}: a barra flutuante abriria por cima do player do Cinema");
                if (output != null) Render(form, Path.Combine(output, $"cinema-player-{size.Width}x{size.Height}-{(rail ? "trilho" : "aberta")}.png"));
            }
        presentation.Close();
    }

    // menu aberto pela barra (amigo, sino): a barra flutuante não abre por cima dele; fechado, espera o mouse sair do trilho
    static async Task MenuCaseAsync(MainForm form, SidePanel side, List<string> problems)
    {
        form.Size = Sizes[0];
        Call(form, "ShowView", false);
        side.Collapsed = true;
        await SettleAsync();
        using var menu = new ContextMenuStrip();
        menu.Items.Add("Item de teste");
        Call(form, "SegurarBarraFlutuante", menu);
        menu.Show(new Point(-8000, 0));
        await SettleAsync();
        if (Floating(form)) problems.Add("menu da barra: a barra flutuante abriria por cima do menu aberto");
        menu.Close();
        Application.DoEvents();
        if (!Floating(form) || !Field<bool>(form, "_esperaSairDoTrilho")) problems.Add("menu da barra: ao fechar, a barra flutuante não voltou a ser permitida ou não ficou esperando o mouse sair do trilho");
        await SettleAsync(); // o mouse de verdade não está no trilho (a janela está fora da tela): a espera acaba no próximo tique
        if (Field<bool>(form, "_esperaSairDoTrilho")) problems.Add("menu da barra: a espera não acabou com o mouse fora do trilho");
        side.Collapsed = false;
    }

    // caminhos de verdade até o Cinema com a barra recolhida (o print de 2026-11-16 tinha a página começando embaixo do trilho)
    static async Task SequencesAsync(MainForm form, SidePanel side, string? output, List<string> problems)
    {
        var steps = new (string Name, Action Act)[]
        {
            ("ajustes>cinema", () => { Call(form, "ShowSettings"); Call(form, "AbrirFilme"); }),
            ("cinema>recolher", () => { Call(form, "AbrirFilme"); form.ToggleSidebar(); }),
            ("cinema>recolher>abrir", () => { Call(form, "AbrirFilme"); form.ToggleSidebar(); form.ToggleSidebar(); }),
            ("recolher>cinema", () => { Call(form, "ShowView", false); form.ToggleSidebar(); Call(form, "AbrirFilme"); }),
            ("inicio-recolhido>ajustes>cinema", () => { Call(form, "ShowView", false); form.ToggleSidebar(); Call(form, "ShowSettings"); Call(form, "AbrirFilme"); }),
        };
        foreach (bool maximized in new[] { false, true })
            foreach (var (name, act) in steps)
            {
                form.WindowState = FormWindowState.Normal; form.Size = Sizes[0]; form.Location = new Point(-8000, 0);
                Call(form, "ShowView", false); side.Collapsed = false;
                await SettleAsync();
                if (maximized) { form.WindowState = FormWindowState.Maximized; await SettleAsync(); }
                act();
                string label = $"{name}{(maximized ? " maximizada" : "")} ({(side.Collapsed ? "trilho" : "aberta")})";
                // a troca de tela muda a barra na hora: deslizando, a página nova ficava 150 ms no tamanho antigo (vídeo de 2026-11-16)
                if (SidePanel.Sliding && name.Contains("ajustes")) problems.Add($"{label}: a barra ainda desliza depois da troca de tela");
                await SettleAsync(); await SettleAsync();
                problems.AddRange(Audit(form, side).Select(p => $"{label}: {p}"));
                if (output != null) Render(form, Path.Combine(output, $"seq-{name.Replace('>', '_')}{(maximized ? "-max" : "")}.png"));
            }
        form.WindowState = FormWindowState.Normal;
        side.Collapsed = false;
    }

    /// <summary>Fotografa a barra de baixo do vídeo da live (sem player) em três larguras: o grupo de controles fica junto no meio, só com ícones.</summary>
    public static void PlayerBar(string output)
    {
        Directory.CreateDirectory(output);
        using var form = new MainForm(true);
        using var stage = new PlayerStage(form);
        var viewer = new Viewer();
        stage.Add(viewer);
        using var overlay = PlayerOverlay.ShowFor(viewer, () => "1392p 60 FPS", () => [], stage: stage);
        foreach (int width in new[] { 1600, 1000, 560 })
        {
            overlay.Bounds = new Rectangle(-8000, 0, width, overlay.LogicalToDeviceUnits(48)); // fora da tela: escondida, ela não desenha os botões
            if (!overlay.Visible) overlay.Show();
            Application.DoEvents();
            overlay.Bounds = new Rectangle(-8000, 0, width, overlay.LogicalToDeviceUnits(48));
            Call(overlay, "LayoutZones");
            using var image = new Bitmap(overlay.Width, overlay.Height);
            overlay.DrawToBitmap(image, new Rectangle(Point.Empty, overlay.Size));
            image.Save(Path.Combine(output, $"barra-video-{width}.png"));
        }
        Console.WriteLine("OK: barra do vídeo fotografada em 1600, 1000 e 560 px.");
    }
    static bool Floating(MainForm form) => (bool)Call(form, "FlutuantePermitida")!;

    // Nas Configurações a barra flutuante não abre; o mouse abre a barra encaixada, que empurra as categorias em vez de cobri-las, recolhe quando o mouse
    // sai e fica aberta com o « (vídeos de 2026-11-16). O mouse de verdade não está na janela de teste (fora da tela): o tique a recolhe.
    static async Task CheckOpenByMouseAsync(MainForm form, SidePanel side, string label, string? photo, List<string> problems)
    {
        if (Floating(form)) problems.Add($"{label}: a barra flutuante abriria por cima das Configurações");
        var hover = Field<System.Windows.Forms.Timer>(form, "_hoverTimer");
        hover.Stop(); // o tique de verdade, sem o mouse na janela, recolheria a barra antes de conferir; aqui os tiques são chamados à mão
        try { await OpenByMouseStepsAsync(form, side, label, photo, problems); }
        finally { hover.Start(); }
    }

    static async Task OpenByMouseStepsAsync(MainForm form, SidePanel side, string label, string? photo, List<string> problems)
    {
        Call(form, "AbrirPeloMouse");
        // desde 2026-11-25 a barra desliza nas Configurações (a página adia as quebras de linha até o fim); o que não pode é o deslize não terminar
        await SettleAsync();
        if (SidePanel.Sliding) problems.Add($"{label}: a barra aberta pelo mouse não terminou de deslizar nas Configurações");
        if (side.Collapsed) problems.Add($"{label}: o mouse não abriu a barra nas Configurações");
        problems.AddRange(Audit(form, side).Select(p => $"{label} aberta pelo mouse: {p}"));
        if (photo != null) Render(form, photo);
        Call(form, "TickBarraNasConfiguracoes");
        await SettleAsync();
        if (SidePanel.Sliding) problems.Add($"{label}: a barra não terminou de deslizar ao recolher nas Configurações");
        if (!side.Collapsed) problems.Add($"{label}: a barra aberta pelo mouse não recolheu quando o mouse saiu");
        Call(form, "AbrirPeloMouse");
        form.ToggleSidebar();
        Call(form, "TickBarraNasConfiguracoes");
        await SettleAsync();
        if (side.Collapsed) problems.Add($"{label}: o « não deixou aberta a barra aberta pelo mouse");
        side.Collapsed = true;
        await SettleAsync();
    }

    /// <summary>
    /// As linhas da própria barra (navegação de cima, ferramentas e perfil de baixo) não podem se cobrir: a 150 % e numa janela baixa, com live no ar e
    /// lives em tela, "Encerrar live" caía em cima da fileira de ferramentas (TESTES.md, rodada de 2026-11-15). Da altura útil mínima da janela (440 dp menos a
    /// barra de título, ~409 dp; MainForm.FitToScreen) até 600 dp a mais,
    /// aberta e no trilho, nas escalas 100 %, 125 % e 175 % (Ctrl +).
    /// </summary>
    public static void RowsCheck(string? output)
    {
        var problems = new List<string>();
        var friends = Enumerable.Range(0, 6).Select(n => new SideFriend($"amigo{n}", $"Amigo {n}", "Jogo", n < 2, n == 0, null)).ToList();
        friends.AddRange(Enumerable.Range(0, 3).Select(n => new SideFriend($"off{n}", $"Offline {n}", "offline", false, false, null, Offline: true))); // etapa 7: a seção OFFLINE também não pode cobrir nada
        double before = Streemu.Util.UiScale.User;
        try
        {
            foreach (double scale in new[] { 1.0, 1.25, 1.75 })
            {
                Streemu.Util.UiScale.User = scale;
                foreach (bool rail in new[] { false, true })
                    foreach (bool live in new[] { false, true })
                    foreach (bool novidades in new[] { false, true }) // plano 027: com o item Novidades (e o selo NOVO / a bolinha) ligado, nada se cobre e o item cabe
                        for (int step = 0; step <= 60; step++)
                        {
                            using var panel = new SidePanel { FilmeVisivel = true, ArquivosVisivel = true, GruposVisivel = true, NovidadesVisivel = novidades, NovidadesNovas = novidades };
                            panel.CreateControl();
                            int width = Streemu.Util.UiScale.Dp(panel, rail ? Streemu.Util.UiMetrics.RailWidth : SidePanel.OpenAt96);
                            panel.Size = new Size(width, 2000);
                            panel.SetData(friends, live ? 2 : 0, SideView.Home, "Sala", live ? new SideSelf("Jogo", 2, false) : null);
                            // a janela não fica mais baixa que a mínima de sempre nem que a barra pede (MainForm.AjustarAlturaMinima)
                            int minimum = Math.Max((int)Math.Round(409 * panel.DeviceDpi / 96.0), panel.MinimumHeight());
                            int height = minimum + (int)Math.Round(step * 10 * panel.DeviceDpi / 96.0);
                            panel.Size = new Size(width, height);
                            using (var bitmap = new Bitmap(width, height))
                            {
                                panel.DrawToBitmap(bitmap, new Rectangle(0, 0, width, height)); // desenhar refaz as linhas no tamanho novo
                                if (output != null && step == 0) bitmap.Save(Path.Combine(output, $"barra-{scale * 100:0}-{(rail ? "trilho" : "aberta")}-{(live ? "live" : "sem-live")}.png"));
                            }
                            var rows = ((System.Collections.IEnumerable)Field<object>(panel, "_rows")).Cast<object>()
                                .Select(r => (Kind: r.GetType().GetProperty("Kind")!.GetValue(r)!.ToString()!, Bounds: (Rectangle)r.GetType().GetProperty("Bounds")!.GetValue(r)!))
                                .Where(r => r.Kind is not ("Friend" or "Section")).ToList();
                            if (novidades && rows.Count(r => r.Kind == "Novidades") != 1) problems.Add($"{scale:P0} {(rail ? "trilho" : "aberta")} {(live ? "com live" : "sem live")} {width}x{height}: o item Novidades não está na barra");
                            if (novidades && rows.FirstOrDefault(r => r.Kind == "Novidades") is { Kind: not null } nov && (nov.Bounds.Bottom > height || nov.Bounds.Right > width))
                                problems.Add($"{scale:P0} {(rail ? "trilho" : "aberta")} {(live ? "com live" : "sem live")} {width}x{height}: o item Novidades {nov.Bounds} não cabe na barra");
                            for (int i = 0; i < rows.Count; i++)
                                for (int j = i + 1; j < rows.Count; j++)
                                    if (rows[i].Bounds.IntersectsWith(rows[j].Bounds))
                                        problems.Add($"{scale:P0} {(rail ? "trilho" : "aberta")} {(live ? "com live" : "sem live")} {width}x{height}: {rows[i].Kind} {rows[i].Bounds} cobre {rows[j].Kind} {rows[j].Bounds}");
                        }
            }
        }
        finally { Streemu.Util.UiScale.User = before; }
        if (problems.Count > 0) throw new Exception($"{problems.Count} linha(s) da barra se cobrindo:\n" + string.Join("\n", problems));
        Console.WriteLine("OK: as linhas da barra lateral não se cobrem da altura mínima da janela a +600 dp, aberta e no trilho, com e sem live, em 100/125/175 %.");
    }

    /// <summary>Controles visíveis de fora da barra cuja parte visível (cortada pelos pais) cai embaixo dela.</summary>
    static IEnumerable<string> Audit(MainForm form, SidePanel side)
    {
        if (!side.Visible) yield break;
        // o WinForms reordena os filhos ao criar handles; com a barra antes dos painéis Fill, o Cinema ficava embaixo dela (2026-11-16)
        if ((bool)Call(form, "OrdemDoEncaixeErrada")!) yield return "ordem do encaixe errada: a barra lateral está antes de um painel que enche a janela";
        var bar = side.RectangleToScreen(side.ClientRectangle);
        foreach (Control top in form.Controls)
        {
            if (top == side || !top.Visible) continue;
            foreach (var (control, visible) in VisibleParts(top, top.RectangleToScreen(top.ClientRectangle)))
            {
                var hit = Rectangle.Intersect(visible, bar);
                if (hit.Width > 0 && hit.Height > 0)
                    yield return $"{Describe(control)} em {visible} cobre {hit.Width}x{hit.Height} px da barra {bar}";
            }
        }
    }

    static IEnumerable<(Control, Rectangle)> VisibleParts(Control c, Rectangle clip)
    {
        var own = Rectangle.Intersect(c.RectangleToScreen(new Rectangle(Point.Empty, c.Size)), clip);
        if (own.Width <= 0 || own.Height <= 0) yield break;
        yield return (c, own);
        foreach (Control child in c.Controls)
            if (child.Visible)
                foreach (var part in VisibleParts(child, Rectangle.Intersect(own, c.RectangleToScreen(c.ClientRectangle)))) yield return part;
    }

    // a área dos vídeos (onde o ffplay é posto) não pode começar embaixo da barra
    static IEnumerable<string> VideoUnderSide(MainForm form, SidePanel side)
    {
        if (!side.Visible || form.VideoArea is not Rectangle area) yield break;
        var bar = side.RectangleToScreen(side.ClientRectangle);
        var hit = Rectangle.Intersect(area, bar);
        if (hit.Width > 0 && hit.Height > 0) yield return $"área dos vídeos {area} cobre {hit.Width}x{hit.Height} px da barra {bar}";
    }

    static string Describe(Control c)
    {
        var path = new List<string>();
        for (var p = c; p != null && p is not Form; p = p.Parent) path.Add(string.IsNullOrEmpty(p.Name) ? p.GetType().Name : p.Name);
        path.Reverse();
        string text = c.Text is { Length: > 0 } t ? $" \"{(t.Length > 30 ? t[..30] + "…" : t)}\"" : "";
        return string.Join("/", path) + text;
    }

    static async Task SettleAsync() { for (int i = 0; i < 8; i++) { await Task.Delay(50); Application.DoEvents(); } }

    static void Render(Form form, string path)
    {
        using var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        foreach (var c in form.Controls.Cast<Control>().Reverse().Where(c => c.Visible))
        {
            using var part = new Bitmap(Math.Max(1, c.Width), Math.Max(1, c.Height));
            c.DrawToBitmap(part, new Rectangle(Point.Empty, c.Size));
            using var g = Graphics.FromImage(image);
            g.DrawImage(part, c.Location);
        }
        image.Save(path);
    }

    static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Hidden)!.GetValue(o)!;
    static void Set(object o, string name, object value) => o.GetType().GetField(name, Hidden)!.SetValue(o, value);
    static object? Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, Hidden | BindingFlags.Public)!.Invoke(o, args);
}
