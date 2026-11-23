using System.Reflection;
using Streemu.Media;
using Streemu.Ui;
using Streemu.Util;

/// <summary>
/// "O que mostrar" do Transmitir (teste de primeiro uso, 2026-11-19): as telas e janelas ficam numa grade com quantas colunas a largura real comporta
/// (antes eram no máximo 2, e numa só depois de "Mostrar mais"), todas à vista, sem "Mostrar mais", sem cartão cortado nem sobreposto.
/// Uso: <c>--fontes-check [pasta das fotos]</c>. A janela de teste fica minimizada; fontes e miniaturas fictícias, sem captura.
/// </summary>
static class FontesBehavior
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(string? fotos)
    {
        if (fotos != null) Directory.CreateDirectory(fotos);
        var casos = new List<string>();
        double escalaAnterior = UiScale.User;
        try
        {
            foreach (double escala in new[] { 1.0, 1.25, 1.5 })
            {
                UiScale.User = escala;
                foreach (bool inline in new[] { false, true })
                {
                    var colunasVistas = new HashSet<int>();
                    foreach (int larguraDp in new[] { 200, 280, 344, 400, 440, 580, 760, 1100 })
                        foreach (int fontes in new[] { 1, 3, 9, 25 })
                            casos.Add(Caso(fotos, larguraDp, 640, inline, fontes, colunasVistas, escala));
                    casos.Add(Caso(null, 580, 400, inline, 9, colunasVistas, escala));
                    Check(colunasVistas.SetEquals([1, 2, 3]), $"{(inline ? "painel" : "página")} escala {escala}: faltou testar uma das grades de 1 a 3 colunas (4 só com cartão de 220 dp ou mais, fora do alcance da largura máxima): {string.Join(", ", colunasVistas)}");
                }
            }
        }
        finally { UiScale.User = escalaAnterior; }
        Console.WriteLine("OK: fontes em grade, todas à vista e dentro da lista, sem \"Mostrar mais\" (" + casos.Count + " casos).");
        foreach (var c in casos.Where(c => c.Contains("[foto]") || c.Contains("colunas 1"))) Console.WriteLine("  " + c);
    }

    static string Caso(string? fotos, int larguraDp, int alturaDp, bool inline, int quantas, HashSet<int> colunasVistas, double escalaUsuario)
    {
        using var form = new Form { WindowState = FormWindowState.Minimized, ShowInTaskbar = false, BackColor = Theme.Surface };
        using var view = new GoLiveView(null, null, 1080, 60, 1, false, []);
        view.SetInline(inline);
        UiScale.ScaleFonts(view, escalaUsuario);
        form.Controls.Add(view);
        string resultado = "";
        Exception? falha = null;
        form.Shown += (_, _) => form.BeginInvoke(() =>
        {
            try
            {
                view.Dock = DockStyle.None;
                int largura = UiScale.Dp(view, larguraDp), altura = UiScale.Dp(view, alturaDp);
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
                string quem = $"{(inline ? "painel" : "página")} {larguraDp}dp ({largura}px), DPI {view.DeviceDpi}, escala {escalaUsuario}, {quantas} fontes";
                Check(typeof(GoLiveView).GetField("_more", Hidden) == null, quem + ": ainda existe \"Mostrar mais\"");
                Check(tiles.Count == quantas && tiles.All(t => t.Visible), quem + ": nem todas as fontes estão à vista");
                var colunas = tiles.Select(t => t.Left).Distinct().Count();
                if (quantas >= 9) colunasVistas.Add(colunas);
                double escala = UiScale.Dp(view, 1000) / 1000.0; // a mesma escala do app (DPI, compensação de QHD e Ctrl +)
                Check(tiles.All(t => t.Left >= 0 && t.Right <= grid.ClientSize.Width), quem + ": cartão cortado à direita da lista");
                int minimo = UiScale.Dp(view, inline ? 80 : 96);
                if (colunas > 1) Check(tiles.All(t => t.Width >= minimo - 2), quem + $": cartão estreito demais ({tiles[0].Width}px, mínimo {minimo}px)");
                Check(body.DisplayRectangle.Width <= body.ClientSize.Width, quem + ": rolagem horizontal");
                for (int i = 0; i < tiles.Count; i++)
                    for (int j = i + 1; j < tiles.Count; j++)
                        Check(!tiles[i].Bounds.IntersectsWith(tiles[j].Bounds), quem + ": cartões sobrepostos");
                var footer = Campo<Panel>(view, "_footer");
                var botao = Campo<Button>(view, "_go");
                var voltar = Campo<Label>(view, "_back");
                var opcoes = Campo<Control>(view, "_options");
                Check(grid.Left == botao.Left && grid.Width == botao.Width && opcoes.Left == grid.Left, quem + ": grade, opções e botão desalinhados");
                if (voltar.Visible) Check(voltar.Left == grid.Left, quem + ": voltar desalinhado");
                Check(footer.Bottom == view.ClientSize.Height && botao.Bottom <= footer.Height, quem + ": botão fora do rodapé fixo");
                Check(grid.Bottom <= body.DisplayRectangle.Bottom, quem + ": lista passa do fim da rolagem");
                // a regra: 3 colunas por padrão; 2 e depois 1 quando o cartão ficaria menor que o mínimo; 4 só com cartão de 220 dp ou mais
                int espaco = UiScale.Dp(view, 10);
                int esperadas = GoLiveView.ColunasDaGrade(grid.Width, espaco, minimo, UiScale.Dp(view, 220));
                Check(colunas == Math.Min(quantas, esperadas), quem + $": {colunas} colunas, a regra pedia {esperadas} (lista {grid.Width}px, cartão {tiles[0].Width}px, escala {escala})");
                // o padrão visível: a coluna normal do Início (400 dp) e a página (>= 440 dp) mostram 3 colunas
                if (quantas >= 3 && ((inline && larguraDp >= 344) || (!inline && larguraDp >= 440))) Check(colunas == 3, quem + $": o padrão são 3 colunas, vieram {colunas}");
                if (larguraDp == 200) Check(colunas == 1 && quantas >= 3 ? true : quantas < 3, quem + ": coluna muito estreita devia cair para 1 coluna");
                ConfereImagemESelecao(view, tiles[0], quem);
                if (quantas >= 25) JanelasQueAbremEFecham(view, body, fontes, quem);
                resultado = $"{quem}: colunas {colunas}, cartão {tiles[0].Width}px, lista {grid.Width}px";
                if (fotos != null && quantas == 9 && larguraDp is 400 or 580 or 760 && escalaUsuario is 1 or 1.5)
                {
                    foreach (var t in tiles.Skip(1))
                    {
                        var exemplo = new Bitmap(320, 180);
                        using (var g = Graphics.FromImage(exemplo))
                        {
                            g.Clear(Color.FromArgb(40, 73, 110));
                            using var faixa = new SolidBrush(Color.FromArgb(112, 160, 198));
                            g.FillRectangle(faixa, 20, 20, 280, 60);
                            using var destaque = new SolidBrush(Color.FromArgb(188, 210, 225));
                            g.FillRectangle(destaque, 20, 100, 150, 50);
                        }
                        t.GetType().GetProperty("Image")!.SetValue(t, exemplo);
                    }
                    using var foto = new Bitmap(largura, altura);
                    view.DrawToBitmap(foto, new Rectangle(0, 0, largura, altura));
                    foto.Save(Path.Combine(fotos, $"fontes-{(inline ? "painel" : "pagina")}-{larguraDp}dp-{escalaUsuario}.png"));
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

    static void ConfereImagemESelecao(GoLiveView view, Control tile, string quem)
    {
        int envios = 0;
        view.SubmitRequested += () => envios++;
        using var antes = new Bitmap(tile.Width, tile.Height);
        tile.DrawToBitmap(antes, tile.ClientRectangle);
        var tamanho = (Size)tile.GetType().GetProperty("ThumbSize")!.GetValue(tile)!;
        Check(tamanho == new Size(320, 180), quem + ": resolução da prévia mudou com a escala");
        var imagem = new Bitmap(32, 18);
        using (var g = Graphics.FromImage(imagem)) g.Clear(Color.Magenta);
        tile.GetType().GetProperty("Image")!.SetValue(tile, imagem); // o cartão passa a ser dono da imagem
        using var depois = new Bitmap(tile.Width, tile.Height);
        tile.DrawToBitmap(depois, tile.ClientRectangle);
        int x = tile.Width / 2, y = 5 + Math.Max(1, tile.Width - 10) * 9 / 32;
        Check(antes.GetPixel(x, y).ToArgb() != depois.GetPixel(x, y).ToArgb(), quem + ": a prévia não alterou o desenho");
        var pixel = depois.GetPixel(x, y);
        Check(pixel.R > 240 && pixel.B > 240 && pixel.G < 15, quem + ": a imagem da prévia não foi desenhada");
        typeof(Control).GetMethod("OnClick", Hidden)!.Invoke(tile, [EventArgs.Empty]);
        Check(ReferenceEquals(view.SelectedSource, tile.GetType().GetProperty("Source")!.GetValue(tile)), quem + ": clique não selecionou a fonte");
        Check(envios == 0, quem + ": selecionar uma fonte iniciou transmissão");
        int largura = view.Width;
        view.Width += 30;
        view.PerformLayout();
        view.Width = largura;
        view.PerformLayout();
        Check(ReferenceEquals(imagem, tile.GetType().GetProperty("Image")!.GetValue(tile)), quem + ": redimensionamento descartou a prévia");
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
