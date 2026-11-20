using Streemu;
using Streemu.Media;
using Streemu.Ui;
using Streemu.Ui.Multistream;
using Streemu.Util;

/// <summary>
/// Bateria de estresse do palco de lives (pedido de 2026-11-20): a janela real fora da tela, sem rede nem player, passa por uma matriz (1 a 4 lives, cada
/// destaque, 5 proporções de vídeo, 6 tamanhos de janela, 3 escalas de interface, barra aberta e no trilho) e depois por 400 passos aleatórios de semente
/// fixa (abrir e fechar lives, redimensionar, Configurações, Transmitir em coluna, janela pequena, destaque, tema). Depois de cada passo confere
/// <see cref="PalcoInvariantes"/> e se alguma exceção escapou para a janela. Não abre tela cheia (maximizaria sobre a tela de quem usa o PC; ver --alt-tab-check).
/// </summary>
static class PalcoEstresseBehavior
{
    static readonly Size[] Sizes = [new(2560, 1400), new(1600, 900), new(1100, 720), new(860, 600), new(760, 440), new(480, 400)];
    static readonly double[] Scales = [1.0, 1.5, 1.75];
    static readonly double[] Aspects = [16.0 / 9, 21.0 / 9, 4.0 / 3, 9.0 / 16, 32.0 / 9];
    /// <summary>Todas as lives na mesma proporção (jogos: 16:9, ultrawide 21:9 e 32:9, jogo antigo 4:3) e uma rodada misturada, com uma em pé.</summary>
    static readonly (string Name, Func<int, double> Of)[] AspectSets =
    [
        ("16:9", _ => Aspects[0]), ("21:9", _ => Aspects[1]), ("32:9", _ => Aspects[4]), ("4:3", _ => Aspects[2]),
        ("misturadas", i => Aspects[i % Aspects.Length]),
    ];
    const int RandomSteps = 400, Seed = 20261120;

    public static void Run(string? output)
    {
        using var form = new MainForm(true) { StartPosition = FormStartPosition.Manual, Location = new Point(-8000, 0), ShowInTaskbar = false };
        var problems = new List<string>();
        Exception? failure = null;
        Application.ThreadException += (_, e) => problems.Add($"exceção na janela: {e.Exception.GetType().Name}: {e.Exception.Message} em {string.Join(" < ", (e.Exception.StackTrace ?? "").Split('\n').Take(6).Select(l => l.Trim()))}");
        form.Shown += async (_, _) =>
        {
            try { await RunAllAsync(form, problems, output); }
            catch (Exception ex) { failure = ex; }
            finally { form.Close(); }
        };
        Application.Run(form);
        if (failure != null) throw failure;
        Report(problems, output);
    }

    static async Task RunAllAsync(MainForm form, List<string> problems, string? output)
    {
        bool wasLight = Theme.IsLight;
        var stage = new PlayerStage(form);
        UiProbe.Set(form, "_playerStage", stage);
        try
        {
            int states = await MatrixAsync(form, stage, problems, output);
            int steps = await RandomWalkAsync(form, stage, problems);
            Console.WriteLine($"Estados conferidos: {states} na matriz e {steps} passos aleatórios (semente {Seed}).");
        }
        finally
        {
            foreach (var v in UiProbe.Field<List<Viewer>>(stage, "_tiles").ToList()) stage.Remove(v);
            UiProbe.Call(form, "ApplyUserScale", 1.0, false);
            ThemeSwitch.Set(form, wasLight);
            UiProbe.Set(form, "_playerStage", null);
            form.StreamsChanged(0);
            stage.Dispose();
        }
        if (UiProbe.Field<System.Windows.Forms.Timer>(stage, "_tick").Enabled) problems.Add("o relógio do palco continua rodando depois de descartado");
    }

    static async Task<int> MatrixAsync(MainForm form, PlayerStage stage, List<string> problems, string? output)
    {
        int states = 0;
        foreach (double scale in Scales)
        {
            UiProbe.Call(form, "ApplyUserScale", scale, false);
            foreach (var size in Sizes)
                foreach (bool rail in new[] { false, true })
                    for (int lives = 1; lives <= PlayerStage.MaxStreams; lives++)
                        foreach (var aspects in AspectSets)
                        {
                            SetLives(form, stage, lives, aspects.Of);
                            form.Size = size;
                            UiProbe.Call(form, "ShowView", true);
                            UiProbe.Field<SidePanel>(form, "_side").Collapsed = rail;
                            Application.DoEvents();
                            foreach (int emphasized in Enumerable.Range(-1, lives >= 2 ? lives + 1 : 1))
                            {
                                Emphasize(stage, emphasized);
                                string label = $"{lives} live(s), proporção {aspects.Name}, destaque {emphasized}, {form.Size.Width}x{form.Size.Height} pedido {size.Width}x{size.Height}, escala {scale:P0}, barra {(rail ? "trilho" : "aberta")}";
                                problems.AddRange(PalcoInvariantes.Check(form, stage, label));
                                if (output != null && !rail && aspects.Name == "16:9" && emphasized <= 0 && size == Sizes[^2]) SavePhoto(form, Path.Combine(output, $"palco-{lives}-{(emphasized < 0 ? "grade" : "destaque")}-{scale * 100:0}.png"));
                                states++;
                            }
                        }
            await UiProbe.SettleAsync(1);
        }
        UiProbe.Call(form, "ApplyUserScale", 1.0, false);
        return states;
    }

    /// <summary>Deixa exatamente <paramref name="lives"/> no palco, a live i com a proporção <paramref name="aspectOf"/>(i).</summary>
    static void SetLives(MainForm form, PlayerStage stage, int lives, Func<int, double> aspectOf)
    {
        var tiles = UiProbe.Field<List<Viewer>>(stage, "_tiles");
        while (tiles.Count > lives) stage.Remove(tiles[^1]);
        while (tiles.Count < lives) { int n = tiles.Count; stage.Add(new Viewer(), () => $"Amigo com um apelido comprido {n} · Jogo com nome bem longo · ao vivo há 3 h"); }
        for (int i = 0; i < tiles.Count; i++) SetAspect(tiles[i], aspectOf(i));
        form.StreamsChanged(lives);
        stage.Relayout();
    }

    /// <summary>Foto do palco (faixas e o lugar dos vídeos; sem player, o lugar mostra o "abrindo a live"), para conferir a olho as faixas apertadas.</summary>
    static void SavePhoto(MainForm form, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = UiProbe.Photo(UiProbe.Field<StageCanvas>(form, "_stageCanvas"));
        image.Save(path);
    }

    static void SetAspect(Viewer viewer, double aspect) => typeof(Viewer).GetProperty(nameof(Viewer.VideoAspect))!.SetValue(viewer, aspect);

    /// <summary>-1 tira o destaque; senão destaca a live de índice <paramref name="index"/>.</summary>
    static void Emphasize(PlayerStage stage, int index)
    {
        var tiles = UiProbe.Field<List<Viewer>>(stage, "_tiles");
        var current = tiles.FirstOrDefault(stage.IsEmphasized);
        if (index < 0) { if (current != null) stage.ToggleEmphasis(current); }
        else if (index < tiles.Count && !stage.IsEmphasized(tiles[index])) stage.ToggleEmphasis(tiles[index]);
        stage.Relayout();
    }

    static async Task<int> RandomWalkAsync(MainForm form, PlayerStage stage, List<string> problems)
    {
        var random = new Random(Seed);
        var actions = PalcoPassos.All(form, stage);
        for (int step = 0; step < RandomSteps; step++)
        {
            var (name, act) = actions[random.Next(actions.Count)];
            string detail = act(random);
            stage.Relayout();
            Application.DoEvents();
            problems.AddRange(PalcoInvariantes.Check(form, stage, $"passo {step} ({name}{detail})"));
            if (step % 25 == 0) await UiProbe.SettleAsync(2); // deixa os relógios do palco e da barra lateral rodarem no meio da bagunça
        }
        return RandomSteps;
    }

    /// <summary>Agrupa as falhas pela regra (o texto sem números) para o relatório não virar milhares de linhas.</summary>
    static void Report(List<string> problems, string? output)
    {
        if (output != null) { Directory.CreateDirectory(output); File.WriteAllLines(Path.Combine(output, "estresse-palco.txt"), problems); }
        var known = problems.Where(p => p.Contains(PalcoInvariantes.KnownLimit)).ToList();
        if (known.Count > 0) Console.WriteLine($"AVISO: {known.Count} caso(s) de limite conhecido (lives de proporções muito diferentes, como uma em pé e uma 32:9, numa área estreita: nenhuma peça igual serve às duas), ex.: {known[0]}");
        problems.RemoveAll(known.Contains);
        if (problems.Count == 0) { Console.WriteLine("OK: o palco manteve todas as regras em todos os estados."); return; }
        var groups = problems.GroupBy(p => System.Text.RegularExpressions.Regex.Replace(p[(p.IndexOf(": ", StringComparison.Ordinal) + 2)..], @"[-\d.,]+", "#"))
            .OrderByDescending(g => g.Count()).Select(g => $"[{g.Count()}x] {g.Key}\n      ex.: {g.First()}");
        throw new Exception($"{problems.Count} falha(s) em {problems.Select(p => p.Split(": ")[0]).Distinct().Count()} estado(s):\n" + string.Join("\n", groups));
    }
}
