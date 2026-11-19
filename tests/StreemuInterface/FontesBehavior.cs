using System.Reflection;
using Streemu.Media;
using Streemu.Ui;
using Streemu.Util;

/// <summary>
/// "O que mostrar" do Transmitir (teste de primeiro uso, 2026-11-19): as telas e janelas ficam numa grade com quantas colunas a largura real comporta
/// (antes eram no máximo 2, e numa só depois de "Mostrar mais"), todas à vista, sem "Mostrar mais", sem cartão cortado nem sobreposto.
/// Uso: <c>--fontes-check [pasta das fotos]</c>. A janela de teste fica minimizada; fontes fictícias, sem miniatura nem captura.
/// </summary>
static class FontesBehavior
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(string? fotos)
    {
        if (fotos != null) Directory.CreateDirectory(fotos);
        var casos = new List<string>();
        // (largura da vista, altura, no painel do Início?, quantas fontes)
        foreach (var inline in new[] { false, true })
            foreach (int largura in new[] { 360, 520, 760, 1100, 1700 })
                foreach (int fontes in new[] { 1, 3, 9, 25 })
                    casos.Add(Caso(fotos, largura, 800, inline, fontes));
        Console.WriteLine("OK: fontes em grade, todas à vista e dentro da lista, sem \"Mostrar mais\" (" + casos.Count + " casos).");
        foreach (var c in casos.Where(c => c.Contains("[foto]") || c.Contains("colunas 1"))) Console.WriteLine("  " + c);
    }

    static string Caso(string? fotos, int largura, int altura, bool inline, int quantas)
    {
        using var form = new Form { WindowState = FormWindowState.Minimized, ShowInTaskbar = false, Size = new Size(largura, altura) };
        using var view = new GoLiveView(null, null, 1080, 60, 1, false, []);
        view.SetInline(inline);
        form.Controls.Add(view);
        string resultado = "";
        Exception? falha = null;
        form.Shown += (_, _) => form.BeginInvoke(() =>
        {
            try
            {
                view.Dock = DockStyle.None;
                view.Size = new Size(largura, altura);
                var fontes = Enumerable.Range(0, quantas).Select(i => new CaptureSource
                {
                    Handle = (IntPtr)(1000 + i), Label = i < 2 ? "Tela" : "Janela", IsWindow = i >= 2,
                    ProcessName = i < 2 ? "" : $"Jogo {i}", IsPrimary = i == 0
                }).ToList();
                typeof(GoLiveView).GetField("_sources", Hidden)!.SetValue(view, fontes);
                typeof(GoLiveView).GetField("_loaded", Hidden)!.SetValue(view, true);
                typeof(GoLiveView).GetMethod("ChooseInitialSource", Hidden)!.Invoke(view, null);
                var body = Campo<Panel>(view, "_body");
                var grid = Campo<Panel>(view, "_grid");
                _ = body.Handle; _ = grid.Handle;
                view.PerformLayout();
                view.PerformLayout();
                Application.DoEvents();
                var tiles = grid.Controls.OfType<Control>().Where(c => c.GetType().Name == "Tile").ToList();
                string quem = $"{(inline ? "painel" : "página")} {largura}px, {quantas} fontes";
                Check(typeof(GoLiveView).GetField("_more", Hidden) == null, quem + ": ainda existe \"Mostrar mais\"");
                Check(tiles.Count == quantas && tiles.All(t => t.Visible), quem + ": nem todas as fontes estão à vista");
                var colunas = tiles.Select(t => t.Left).Distinct().Count();
                double escala = UiScale.Dp(view, 1000) / 1000.0; // a mesma escala do app (DPI, compensação de QHD e Ctrl +)
                Check(tiles.All(t => t.Left >= 0 && t.Right <= grid.ClientSize.Width), quem + ": cartão cortado à direita da lista");
                Check(tiles.All(t => t.Width >= (inline ? 128 : 150) * escala - 2), quem + $": cartão estreito demais ({tiles[0].Width}px)");
                Check(body.DisplayRectangle.Width <= body.ClientSize.Width, quem + ": rolagem horizontal");
                for (int i = 0; i < tiles.Count; i++)
                    for (int j = i + 1; j < tiles.Count; j++)
                        Check(!tiles[i].Bounds.IntersectsWith(tiles[j].Bounds), quem + ": cartões sobrepostos");
                Check(grid.Bottom <= body.DisplayRectangle.Bottom, quem + ": lista passa do fim da rolagem");
                // com espaço para mais uma coluna sem passar do mínimo legível, ela tem que existir (era o "1 coluna com espaço de sobra")
                int minimo = (int)((inline ? 128 : 150) * escala);
                int cabem = Math.Max(1, (grid.Width + (int)(10 * escala)) / (minimo + (int)(10 * escala)));
                Check(colunas >= Math.Min(quantas, cabem), quem + $": só {colunas} colunas, cabiam {cabem} (lista {grid.Width}px, cartão {tiles[0].Width}px, escala {escala})");
                if (quantas >= 25) JanelasQueAbremEFecham(view, body, fontes, quem);
                resultado = $"{quem}: colunas {colunas}, cartão {tiles[0].Width}px, lista {grid.Width}px";
                if (fotos != null)
                {
                    using var foto = new Bitmap(largura, altura);
                    view.DrawToBitmap(foto, new Rectangle(0, 0, largura, altura));
                    foto.Save(Path.Combine(fotos, $"fontes-{(inline ? "painel" : "pagina")}-{largura}-{quantas}.png"));
                    resultado += " [foto]";
                }
            }
            catch (Exception e) { falha = e; }
            finally { form.Close(); }
        });
        Application.Run(form);
        if (falha != null) throw falha;
        return resultado;
    }

    /// <summary>Uma janela fecha com a lista rolada e a escolhida segue escolhida: a rolagem não pula.</summary>
    static void JanelasQueAbremEFecham(GoLiveView view, Panel body, List<CaptureSource> fontes, string quem)
    {
        body.AutoScrollPosition = new Point(0, 150);
        int antes = -body.AutoScrollPosition.Y;
        Check(antes > 0, quem + ": a página não rola com 25 fontes");
        var escolhida = fontes[5];
        var lista = (System.Collections.IList)view.GetType().GetField("_tiles", Hidden)!.GetValue(view)!;
        typeof(Control).GetMethod("OnClick", Hidden)!.Invoke(lista[5], [EventArgs.Empty]);
        var restantes = fontes.Where(f => f != fontes[24]).ToList();
        view.GetType().GetMethod("SyncSources", Hidden)!.Invoke(view, [restantes]);
        view.PerformLayout();
        Check(-body.AutoScrollPosition.Y == antes, quem + $": a rolagem pulou ({antes} -> {-body.AutoScrollPosition.Y}) quando uma janela fechou");
        Check(view.SelectedSource == escolhida, quem + ": a fonte escolhida se perdeu quando outra janela fechou");
        var maisUma = restantes.Append(new CaptureSource { Handle = (IntPtr)5000, Label = "Janela", IsWindow = true, ProcessName = "Novo" }).ToList();
        view.GetType().GetMethod("SyncSources", Hidden)!.Invoke(view, [maisUma]);
        view.PerformLayout();
        Check(-body.AutoScrollPosition.Y == antes, quem + ": a rolagem pulou quando uma janela abriu");
        Check(view.SelectedSource == escolhida, quem + ": a fonte escolhida se perdeu quando uma janela abriu");
    }

    static T Campo<T>(object dono, string nome) => (T)dono.GetType().GetField(nome, Hidden)!.GetValue(dono)!;
    static void Check(bool valido, string mensagem) { if (!valido) throw new InvalidOperationException(mensagem); }
}
