using System.Reflection;
using Streemu.Ui;
using Streemu.Util;

/// <summary>
/// --apoiar-check [pasta]: a tela Apoiar (plano 035). Três partes, sem rede, navegador nem player (o "abrir o link" e o "copiar" são falsos):
/// 1) a conta pura (<see cref="ApoiarLayout"/>) numa tabela de áreas e escalas (as do plano, 1080p a 125 e 150 %, 4K a 150 e 200 %, a janela mínima a 150 %, dois
///    arranjos estreitos), com e sem o aviso de falha: tudo dentro da área, arte e balão sem se tocar, nenhum texto sobre outro nem fora do balão nem cortado, o recado inteiro,
///    a unidade crescendo com a área e os números do plano (texto e arte de 1080p e 1440p, ±1 e ±2 px);
/// 2) o controle numa janela fora da tela: Tab chega ao botão, Enter e clique abrem o endereço, a dica, o aviso com "Copiar link" quando o navegador falha, as cores do tema
///    claro e do escuro lidas do desenho, e redimensionar refaz a arte;
/// 3) o coração em 16, 20 e 24 px sem serrilhado, o item da barra lateral nas duas cores, e o peso e a largura da arte.
/// Com a pasta, salva a prévia da tela (nos dois temas e nos tamanhos da tabela), a barra e o coração ampliado.
/// </summary>
static class ApoiarBehavior
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    record Caso(string Nome, int W, int H, float Dp, int? Texto = null, int? Arte = null);

    /// <summary>A área de conteúdo de uma janela, como no mockup: a barra aberta tira 234 dp da largura (trilho de 66 dp abaixo de 900 dp) e a faixa de cima 34 dp da altura.</summary>
    static Size Area(int janelaW, int janelaH, float dp) => new((int)Math.Round(janelaW - (janelaW < 900 * dp ? 66 : 234) * dp), (int)Math.Round(janelaH - 34 * dp));

    static readonly Caso[] Casos =
    [
        new("janela mínima 760x440", 694, 406, 1, 14, 266),
        new("padrão 990x660", 756, 626, 1, 14, 266),
        new("1366x768", 1132, 734, 1, 15, 284),
        new("1920x1080", 1686, 1006, 1, 22, 423),
        new("2560x1440", 2326, 1366, 1, 31, 583),
        new("1920x1080 a 125%", Area(1920, 1080, 1.25f).Width, Area(1920, 1080, 1.25f).Height, 1.25f),
        new("1920x1080 a 150%", Area(1920, 1080, 1.5f).Width, Area(1920, 1080, 1.5f).Height, 1.5f),
        new("3840x2160 a 150%", Area(3840, 2160, 1.5f).Width, Area(3840, 2160, 1.5f).Height, 1.5f),
        new("3840x2160 a 200%", Area(3840, 2160, 2f).Width, Area(3840, 2160, 2f).Height, 2f),
        new("janela mínima a 150%", Area(1140, 660, 1.5f).Width, Area(1140, 660, 1.5f).Height, 1.5f),
        new("janela mínima a 200%", Area(1520, 880, 2f).Width, Area(1520, 880, 2f).Height, 2f),
        new("estreito 520x420", 520, 420, 1),
        new("estreito 430x640", 430, 640, 1),
        new("estreito a 150% 780x700", 780, 700, 1.5f),
        new("largo e baixo 1500x360", 1500, 360, 1),
    ];

    public static void Run(string? pasta)
    {
        if (pasta != null) Directory.CreateDirectory(pasta);
        var usuario = UiScale.User; bool claroAntes = Theme.IsLight;
        int casos = 0;
        try
        {
            casos += Tabela();
            casos += Controle(pasta);
            casos += Coracao(pasta);
            casos += Barra(pasta);
            casos += Arte();
        }
        finally { UiScale.User = usuario; Theme.Use(claroAntes); }
        Console.WriteLine($"apoiar-check OK: {casos} conferências" + (pasta != null ? $"; fotos em {pasta}" : ""));
    }

    // ---------- 1. a conta pura ----------

    static int Tabela()
    {
        int n = 0;
        float anterior = 0;
        foreach (var caso in Casos)
        {
            foreach (bool aviso in new[] { false, true })
            {
                var area = new Size(caso.W, caso.H);
                var g = ApoiarLayout.Calcular(area, caso.Dp, ApoiarTela.Medir, aviso);
                string onde = $"{caso.Nome}{(aviso ? " com aviso" : "")} ({area.Width}x{area.Height}, escala {caso.Dp})";
                Cabe(g, area, caso.Dp, onde);
                if (!aviso && caso.Texto != null) Console.WriteLine($"  {caso.Nome}: texto {g.U:0.0} px (plano {caso.Texto}), arte {g.Arte.Width} px (plano {caso.Arte})");
                if (!aviso && caso.Texto is { } texto) Check(Math.Abs(g.U - texto) <= 1, $"{onde}: o texto do recado ficou com {g.U:0.0} px, o plano diz {texto}");
                if (!aviso && caso.Arte is { } arte) Check(Math.Abs(g.Arte.Width - arte) <= 2, $"{onde}: a arte ficou com {g.Arte.Width} px, o plano diz {arte}");
                n++;
            }
            if (caso.Texto != null)
            {
                float u = ApoiarLayout.Calcular(new Size(caso.W, caso.H), caso.Dp, ApoiarTela.Medir).U;
                Check(u >= anterior, $"{caso.Nome}: a unidade encolheu ({u:0.0} < {anterior:0.0}) com a área maior");
                anterior = u;
            }
        }
        // a unidade cresce junto com a área em toda a faixa (largura e altura juntas)
        float antes = 0;
        for (int largura = 600; largura <= 3600; largura += 100)
        {
            var area = new Size(largura, (int)(largura * .6));
            var g = ApoiarLayout.Calcular(area, 1, ApoiarTela.Medir);
            Check(g.U >= antes - .01f && g.Cabe, $"a unidade encolheu ou não coube em {area} ({g.U:0.0} < {antes:0.0})");
            antes = g.U; n++;
        }
        return n;
    }

    static void Cabe(ApoiarGeometria g, Size area, float dp, string onde)
    {
        var tudo = new Rectangle(Point.Empty, area);
        Check(g.Cabe, $"{onde}: a composição não coube nem na menor unidade ({g.U:0.0} px)");
        Check(g.Largo == (area.Width >= 560 * dp), $"{onde}: arranjo errado (largo = {g.Largo})");
        Check(tudo.Contains(g.Composicao), $"{onde}: a composição {g.Composicao} sai da área {area}");
        Check(!g.Arte.IntersectsWith(g.Balao), $"{onde}: a arte {g.Arte} cobre o balão {g.Balao}");
        Check(g.Arte.Width > 0 && g.Arte.Height > 0 && g.Balao.Width > 0 && g.Balao.Height > 0, $"{onde}: peça sem tamanho");
        // centrado na largura (a arte e o balão juntos; o bico fica de fora da conta)
        var conjunto = Rectangle.Union(g.Arte, g.Balao);
        Check(Math.Abs(conjunto.X + conjunto.Width / 2 - area.Width / 2) <= 2, $"{onde}: o conjunto não está centrado ({conjunto} na área {area})");
        Check(g.U >= 12 * dp - .01f && g.U <= 36 * dp + .01f, $"{onde}: unidade {g.U:0.0} fora de 12 a 36 dp");
        // a arte não passa de 42 % da largura nem de 62 % da altura (arranjo largo)
        if (g.Largo) Check(g.Arte.Width <= .42f * area.Width + 1 && g.Arte.Height <= .62f * area.Height + 1, $"{onde}: arte grande demais ({g.Arte.Size})");

        var dentro = new List<(string Nome, Rectangle Area)> { ("coração", g.Coracao), ("botão", g.Botao), ("assinatura", g.Assinatura.Area) };
        for (int i = 0; i < g.Recado.Count; i++) if (g.Recado[i].Area.Width > 0) dentro.Add(($"recado {i + 1}", g.Recado[i].Area));
        for (int i = 0; i < g.Aviso.Count; i++) dentro.Add(($"aviso {i + 1}", g.Aviso[i].Area));
        if (!g.AvisoBotao.IsEmpty) dentro.Add(("copiar link", g.AvisoBotao));
        foreach (var (nome, r) in dentro)
        {
            Check(r.Width > 0 && r.Height > 0, $"{onde}: {nome} sem tamanho");
            Check(g.Balao.Contains(r), $"{onde}: {nome} {r} sai do balão {g.Balao}");
        }
        for (int i = 0; i < dentro.Count; i++)
            for (int j = i + 1; j < dentro.Count; j++)
                // a caixa de uma linha é a medida cheia da fonte (com entrelinha); a tinta ocupa o miolo, então a sobra de 12 % em cima e embaixo não conta
                Check(!Miolo(dentro[i].Area).IntersectsWith(Miolo(dentro[j].Area)), $"{onde}: {dentro[i].Nome} {dentro[i].Area} cobre {dentro[j].Nome} {dentro[j].Area}");
        // formato de carta: recado, assinatura à direita numa linha própria, botão embaixo à esquerda
        var ultimaLinha = g.Recado[^1].Area;
        Check(g.Assinatura.Area.Top >= ultimaLinha.Bottom - 1 && g.Botao.Top >= g.Assinatura.Area.Bottom - 1, $"{onde}: a ordem recado, assinatura e botão não é de cima para baixo");
        Check(g.Assinatura.Area.Right >= g.Recado[0].Area.Left + (g.Balao.Right - g.Balao.Left) / 2 && g.Assinatura.Area.Left > g.Botao.Left, $"{onde}: a assinatura não está à direita");
        Check(g.Botao.Left <= g.Recado[0].Area.Left + 1, $"{onde}: o botão não está alinhado à esquerda");
        // o bico encosta no balão e fica dentro da área
        Check(tudo.Contains(g.Bico), $"{onde}: o bico {g.Bico} sai da área");
        Check(g.LadoDoBico == ApoiarBico.Esquerda ? g.Bico.Right == g.Balao.Left : g.Bico.Bottom == g.Balao.Top, $"{onde}: o bico não encosta no balão");

        // texto nenhum cortado: cada linha pede o que a fonte mede, e o recado sai inteiro
        void Linha(string nome, ApoiarLinha l, float px, ApoiarEstilo estilo)
        {
            if (l.Texto.Length == 0) return;
            var m = ApoiarTela.Medir(l.Texto, px, estilo);
            Check(l.Area.Width >= m.Width && l.Area.Height >= m.Height, $"{onde}: {nome} cortado ({l.Area.Size} para um texto de {m})");
        }
        foreach (var l in g.Recado) Linha("recado", l, g.TamRecado, ApoiarEstilo.Normal);
        Linha("assinatura", g.Assinatura, g.TamAssinatura, ApoiarEstilo.Assinatura);
        foreach (var l in g.Aviso) Linha("aviso", l, g.TamAviso, ApoiarEstilo.Normal);
        Check(string.Join(" ", g.Recado.Select(l => l.Texto).Where(t => t.Length > 0)) == ApoiarLayout.Recado, $"{onde}: o recado perdeu ou trocou palavras");
        if (g.Aviso.Count > 0) Check(string.Join(" ", g.Aviso.Select(l => l.Texto)) == ApoiarLayout.TextoAviso, $"{onde}: o aviso perdeu ou trocou palavras");
        // o botão cabe com o texto, o coração e o ícone (o BotaoPilula desenha com estas folgas)
        int minimoBotao = (int)Math.Round((2 * ApoiarLayout.BotaoFolga + 2 * ApoiarLayout.BotaoIcone + 2 * ApoiarLayout.BotaoEspaco) * g.U) + ApoiarTela.Medir(ApoiarLayout.TextoBotao, g.TamBotao, ApoiarEstilo.Semibold).Width - 1;
        Check(g.Botao.Width >= minimoBotao, $"{onde}: o botão {g.Botao.Width} px é estreito para o conteúdo ({minimoBotao} px)");
    }

    static Rectangle Miolo(Rectangle r) { int sobra = (int)Math.Round(r.Height * .12f); return new Rectangle(r.X, r.Y + sobra, r.Width, Math.Max(1, r.Height - 2 * sobra)); }

    // ---------- 2. o controle ----------

    /// <summary>Compensa o DPI do Windows para a escala de tela ficar em 1: a tabela do plano é a 100 % e este PC pode estar a 125 %.</summary>
    static void Escala100()
    {
        UiScale.User = 1;
        using var sonda = new Panel { Size = new Size(10, 10) };
        sonda.CreateControl();
        UiScale.User = 1000.0 / UiScale.Dp(sonda, 1000);
    }

    static Panel NovaSuperficie(Size area, out ApoiarTela tela)
    {
        var superficie = new Panel { Size = area, BackColor = Theme.Surface };
        tela = new ApoiarTela { Dock = DockStyle.Fill, AbrirEndereco = _ => { }, CopiarTexto = _ => { } };
        superficie.Controls.Add(tela);
        superficie.CreateControl();
        superficie.PerformLayout();
        Bombear();
        return superficie;
    }

    static int Controle(string? pasta)
    {
        int n = 0;
        Escala100();
        foreach (bool claro in new[] { false, true })
        {
            Theme.Use(claro);
            foreach (var caso in Casos.Take(5))
            {
                var area = new Size(caso.W, caso.H);
                using var superficie = NovaSuperficie(area, out var tela);
                string onde = $"{(claro ? "claro" : "escuro")} {caso.Nome}";
                var g = tela.Geometria ?? throw new InvalidOperationException($"{onde}: a tela não calculou a disposição");
                Check(g.Cabe && Math.Abs(g.U - caso.Texto!.Value) <= 1, $"{onde}: o controle mediu u = {g.U:0.0}, o plano diz {caso.Texto}");
                Check(tela.Botao.Bounds == g.Botao && !tela.BotaoCopiar.Visible, $"{onde}: o botão não está onde a conta mandou");
                using var foto = Foto(superficie);
                ConfereCores(foto, g, claro, onde);
                if (pasta != null) foto.Save(Path.Combine(pasta, $"apoiar-{(claro ? "claro" : "escuro")}-{area.Width}x{area.Height}.png"));
                n += 2;
            }
            // a falha ao abrir o navegador, em tamanho médio, com foto
            using (var superficie = NovaSuperficie(new Size(756, 626), out var tela))
            {
                tela.AbrirEndereco = _ => throw new InvalidOperationException("sem navegador");
                tela.Abrir();
                Bombear();
                Check(tela.Falhou && tela.BotaoCopiar.Visible && tela.Geometria!.Aviso.Count > 0 && tela.Geometria.Cabe, "a falha ao abrir o navegador não mostrou o aviso com o Copiar link");
                Check(tela.BotaoCopiar.Bounds == tela.Geometria.AvisoBotao, "o Copiar link não está onde a conta mandou");
                if (pasta != null) { using var foto = Foto(superficie); foto.Save(Path.Combine(pasta, $"apoiar-{(claro ? "claro" : "escuro")}-aviso-756x626.png")); }
                n += 2;
            }
        }
        Theme.Use(false);

        // a tela inteira numa janela fora da tela: Tab, Enter, clique, dica, aviso, Copiar link
        using var janela = new Form
        {
            StartPosition = FormStartPosition.Manual, FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
            Bounds = new Rectangle(-8000, 40, 900, 600), BackColor = Theme.Surface, ForeColor = Theme.Text, Font = UiTypography.Create(UiTypography.Body),
        };
        var abertos = new List<string>(); var copiados = new List<string>(); bool navegadorQuebrado = false;
        var viva = new ApoiarTela
        {
            Dock = DockStyle.Fill,
            AbrirEndereco = url => { if (navegadorQuebrado) throw new InvalidOperationException("sem navegador"); abertos.Add(url); },
            CopiarTexto = texto => copiados.Add(texto),
        };
        janela.Controls.Add(viva);
        janela.Show();
        Bombear();
        Check(janela.SelectNextControl(null, true, true, true, true) && janela.ActiveControl == viva.Botao, "o Tab não chegou ao botão Apoiar no site");
        Check(viva.Botao.Focused || janela.ActiveControl == viva.Botao, "o botão não ficou com o foco");
        UiProbe.Call(janela, "ProcessDialogKey", Keys.Enter);
        Check(abertos.SequenceEqual([UiBrand.UrlApoio]), "o Enter no botão não abriu a página de apoio (abriu: " + string.Join(", ", abertos) + ")");
        viva.Botao.PerformClick();
        Check(abertos.Count == 2 && abertos.All(a => a == "https://www.streemu.com.br/apoie") && !viva.Falhou, "o clique no botão não abriu a página certa");
        var dica = ((ToolTip)typeof(ApoiarTela).GetField("_dica", Hidden)!.GetValue(viva)!).GetToolTip(viva.Botao);
        Check(dica == "Abre streemu.com.br/apoie no navegador", "dica do botão errada: " + dica);
        Check(viva.Botao.AccessibleName == "Apoiar no site" && viva.AccessibleName == "Apoiar o Streemu", "nomes acessíveis errados");
        navegadorQuebrado = true;
        viva.Botao.PerformClick();
        Bombear();
        Check(viva.Falhou && viva.BotaoCopiar.Visible, "o navegador que falhou não mostrou o Copiar link");
        viva.BotaoCopiar.PerformClick();
        Check(copiados.SequenceEqual([ApoiarLayout.EnderecoCurto]), "o Copiar link não copiou o endereço (copiou: " + string.Join(", ", copiados) + ")");
        navegadorQuebrado = false;
        viva.Botao.PerformClick();
        Bombear();
        Check(!viva.Falhou && !viva.BotaoCopiar.Visible, "abrir de novo com sucesso não escondeu o aviso");
        n += 9;

        // redimensionar: a arte acompanha (em alta qualidade 150 ms depois) e nada sai da área
        using (var superficie = NovaSuperficie(new Size(1686, 1006), out var tela))
        {
            using (Foto(superficie)) { }
            foreach (var passo in new[] { new Size(1500, 900), new Size(1100, 760), new Size(694, 406), new Size(520, 420), new Size(2326, 1366) })
            {
                superficie.Size = passo; superficie.PerformLayout();
                using (Foto(superficie)) { }
                Check(tela.Geometria!.Cabe, $"redimensionar para {passo}: não coube");
            }
            for (int i = 0; i < 8; i++) { Application.DoEvents(); Thread.Sleep(50); }
            using (Foto(superficie)) { }
            var tamanho = (Size)typeof(ApoiarTela).GetField("_arteTamanho", Hidden)!.GetValue(tela)!;
            Check(tamanho == tela.Geometria!.Arte.Size, $"depois de redimensionar a arte ficou em {tamanho}, e a conta pede {tela.Geometria.Arte.Size}");
            n += 7;
        }
        return n;
    }

    /// <summary>Lê da foto o que o desenho pintou: fundo da tela, fundo do balão, botão e coração (todos pelas cores do tema).</summary>
    static void ConfereCores(Bitmap foto, ApoiarGeometria g, bool claro, string onde)
    {
        void Cor(string nome, Point p, Color esperada, int tolerancia = 4)
        {
            var c = foto.GetPixel(p.X, p.Y);
            Check(Math.Abs(c.R - esperada.R) <= tolerancia && Math.Abs(c.G - esperada.G) <= tolerancia && Math.Abs(c.B - esperada.B) <= tolerancia,
                $"{onde}: {nome} em {p} é {c.R},{c.G},{c.B} e devia ser {esperada.R},{esperada.G},{esperada.B} (Theme, tema {(claro ? "claro" : "escuro")})");
        }
        Cor("fundo da tela", new Point(2, 2), Theme.Surface);
        Cor("fundo do balão", new Point(g.Balao.X + g.Balao.Width / 2, g.Balao.Bottom - Math.Max(3, (int)(g.U * .4f))), Theme.SurfaceRaised);
        Cor("botão", new Point(g.Botao.X + g.Botao.Width / 2, g.Botao.Bottom - Math.Max(3, g.Botao.Height / 8)), Theme.Accent);
        Cor("coração do recado", new Point(g.Coracao.X + g.Coracao.Width / 2, g.Coracao.Y + g.Coracao.Height / 2), Theme.Accent);
        // o texto do recado usa Theme.Text: algum pixel da primeira linha tem de ser praticamente dele (o resto é suavização)
        var linha = g.Recado[0].Area; int achou = 0;
        for (int y = linha.Top; y < linha.Bottom; y++) for (int x = linha.Left; x < linha.Right; x += 1)
        { var c = foto.GetPixel(x, y); if (Math.Abs(c.R - Theme.Text.R) < 24 && Math.Abs(c.G - Theme.Text.G) < 24 && Math.Abs(c.B - Theme.Text.B) < 24) achou++; }
        Check(achou > 20, $"{onde}: o recado não foi pintado na cor do texto do tema");
        // a arte está desenhada: a área dela não é só o fundo
        int tinta = 0;
        for (int y = g.Arte.Top; y < g.Arte.Bottom; y += 4) for (int x = g.Arte.Left; x < g.Arte.Right; x += 4) if (foto.GetPixel(x, y).ToArgb() != Theme.Surface.ToArgb()) tinta++;
        Check(tinta > g.Arte.Width * g.Arte.Height / 16 / 3, $"{onde}: a arte não apareceu");
    }

    // ---------- 3. coração, barra e arte ----------

    static int Coracao(string? pasta)
    {
        int n = 0;
        var folha = pasta == null ? null : new Bitmap(3 * 24 * 8 + 40, 2 * 24 * 8 + 30);
        using var gf = folha == null ? null : Graphics.FromImage(folha);
        gf?.Clear(Color.Gray);
        int coluna = 0;
        foreach (bool claro in new[] { false, true })
        {
            Theme.Use(claro);
            int linha = 0;
            foreach (var cor in new[] { Theme.Accent, Theme.AccentSoft })
            {
                foreach (int tamanho in new[] { 16, 20, 24 })
                {
                    using var bmp = new Bitmap(tamanho + 8, tamanho + 8);
                    using (var g = Graphics.FromImage(bmp)) { g.Clear(Theme.SurfaceRaised); UiIcons.Draw(g, UiIcon.Heart, new Rectangle(4, 4, tamanho, tamanho), cor); }
                    int cheios = 0, parciais = 0, fundo = 0;
                    for (int y = 0; y < bmp.Height; y++) for (int x = 0; x < bmp.Width; x++)
                    {
                        var c = bmp.GetPixel(x, y);
                        if (c.ToArgb() == Theme.SurfaceRaised.ToArgb()) fundo++;
                        else if (Math.Abs(c.R - cor.R) < 3 && Math.Abs(c.G - cor.G) < 3 && Math.Abs(c.B - cor.B) < 3) cheios++;
                        else parciais++;
                    }
                    string onde = $"coração {tamanho} px {(claro ? "claro" : "escuro")}";
                    Check(cheios > tamanho * tamanho * .25, $"{onde}: pouco miolo na cor pedida ({cheios} px)");
                    Check(parciais >= tamanho, $"{onde}: sem suavização na borda ({parciais} px parciais): serrilhado");
                    Check(parciais < cheios, $"{onde}: borda maior que o miolo ({parciais} x {cheios})");
                    var centro = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2 - tamanho / 12);
                    Check(centro.ToArgb() == Color.FromArgb(255, cor).ToArgb(), $"{onde}: o centro do coração não é a cor pedida");
                    Check(bmp.GetPixel(0, 0).ToArgb() == Theme.SurfaceRaised.ToArgb(), $"{onde}: pintou fora do quadrado");
                    // o ícone fica centrado no quadrado dele (±1 px)
                    int minX = bmp.Width, maxX = 0;
                    for (int y = 0; y < bmp.Height; y++) for (int x = 0; x < bmp.Width; x++) if (bmp.GetPixel(x, y).ToArgb() != Theme.SurfaceRaised.ToArgb()) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); }
                    Check(Math.Abs((minX + maxX) / 2.0 - (bmp.Width - 1) / 2.0) <= 1.5, $"{onde}: coração fora do centro ({minX}..{maxX} de {bmp.Width})");
                    if (gf != null)
                    {
                        int ix = 10 + (tamanho == 16 ? 0 : tamanho == 20 ? 1 : 2) * 24 * 8, iy = 10 + (linha + coluna * 0) * 24 * 8;
                        gf.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor; gf.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                        gf.DrawImage(bmp, new Rectangle(ix, iy, bmp.Width * 8 - (24 - tamanho) * 0, bmp.Height * 8), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel);
                    }
                    n += 6;
                }
                linha++;
            }
            if (folha != null) { folha.Save(Path.Combine(pasta!, $"coracao-ampliado-{(claro ? "claro" : "escuro")}.png")); gf!.Clear(Color.Gray); }
            coluna++;
        }
        folha?.Dispose();
        Theme.Use(false);
        return n;
    }

    static int Barra(string? pasta)
    {
        int n = 0;
        var amigos = new List<SideFriend> { new("a", "Ana", "online", false, false, null), new("b", "Beto", "online", false, false, null) };
        foreach (bool claro in new[] { false, true })
        foreach (bool trilho in new[] { false, true })
        foreach (bool selecionado in new[] { false, true })
        {
            Theme.Use(claro);
            using var barra = new SidePanel { FilmeVisivel = true, ArquivosVisivel = true, GruposVisivel = true, NovidadesVisivel = true };
            barra.CreateControl();
            int largura = UiScale.Dp(barra, trilho ? UiMetrics.RailWidth : SidePanel.OpenAt96), altura = UiScale.Dp(barra, 700);
            barra.Size = new Size(largura, altura);
            barra.SetData(amigos, 0, selecionado ? SideView.Apoiar : SideView.Home, "Sala");
            if (trilho) barra.Collapsed = true;
            using var foto = new Bitmap(largura, altura);
            barra.DrawToBitmap(foto, new Rectangle(0, 0, largura, altura));
            var linhas = ((System.Collections.IEnumerable)typeof(SidePanel).GetField("_rows", Hidden)!.GetValue(barra)!).Cast<object>()
                .Select(r => (Tipo: r.GetType().GetProperty("Kind")!.GetValue(r)!.ToString()!, Area: (Rectangle)r.GetType().GetProperty("Bounds")!.GetValue(r)!))
                .Where(r => r.Tipo is not ("Friend" or "Section")).ToList();
            string onde = $"{(claro ? "claro" : "escuro")} {(trilho ? "trilho" : "aberta")}{(selecionado ? " selecionado" : "")}";
            int posNovidades = linhas.FindIndex(r => r.Tipo == "Novidades"), posApoiar = linhas.FindIndex(r => r.Tipo == "Apoiar");
            Check(linhas.Count(r => r.Tipo == "Apoiar") == 1, $"{onde}: o item Apoiar não está na barra (ou está duas vezes)");
            Check(posApoiar == posNovidades + 1, $"{onde}: Apoiar devia vir logo depois de Novidades ({string.Join(" > ", linhas.Select(r => r.Tipo))})");
            var linha = linhas[posApoiar].Area;
            Check(linha.Bottom <= altura && linha.Right <= largura, $"{onde}: o item Apoiar {linha} não cabe na barra");
            // o coração é Accent sempre e AccentSoft quando a vista é a do Apoiar (sem isso sumiria dentro da pílula de seleção)
            int tamanhoIcone = Math.Min(UiScale.Dp(barra, 20), Math.Min(linha.Height, UiScale.Dp(barra, 40)) - UiScale.Dp(barra, 4));
            var centro = new Point(linha.X + Math.Min(linha.Width, UiScale.Dp(barra, 40)) / 2, linha.Y + linha.Height / 2 - tamanhoIcone / 12);
            var cor = foto.GetPixel(centro.X, centro.Y);
            var esperada = selecionado ? Theme.AccentSoft : Theme.Accent;
            Check(Math.Abs(cor.R - esperada.R) <= 3 && Math.Abs(cor.G - esperada.G) <= 3 && Math.Abs(cor.B - esperada.B) <= 3, $"{onde}: o coração da barra é {cor.R},{cor.G},{cor.B} e devia ser a cor do tema {esperada.R},{esperada.G},{esperada.B}");
            if (selecionado) { var pilula = Sobre(Theme.Selection, Theme.SurfaceRaised); Check(UiProbe.Contrast(Theme.AccentSoft, pilula) >= Math.Max(2.0, UiProbe.Contrast(Theme.Accent, pilula)), $"{onde}: o coração selecionado tem menos contraste com a pílula ({UiProbe.Contrast(Theme.AccentSoft, pilula):0.0}) que os ícones dos outros itens ({UiProbe.Contrast(Theme.Accent, pilula):0.0})"); }
            else Check(UiProbe.Contrast(Theme.Accent, Theme.SurfaceRaised) >= 2.5, $"{onde}: o coração tem pouco contraste com a barra ({UiProbe.Contrast(Theme.Accent, Theme.SurfaceRaised):0.0})");
            if (pasta != null) foto.Save(Path.Combine(pasta, $"barra-apoiar-{(claro ? "claro" : "escuro")}-{(trilho ? "trilho" : "aberta")}{(selecionado ? "-selecionado" : "")}.png"));
            n += 6;
        }
        Theme.Use(false);
        return n;
    }

    static int Arte()
    {
        var montagem = typeof(ApoiarTela).Assembly;
        using var fluxo = montagem.GetManifestResourceStream("Streemu.Arte.mumu-apoio.png") ?? throw new InvalidOperationException("a arte mumu-apoio.png não está embutida");
        Check(fluxo.Length <= 900 * 1024, $"a arte pesa {fluxo.Length / 1024} KB (o limite do plano é 900 KB)");
        using var bmp = UiBrand.LoadArt("mumu-apoio.png");
        Check(bmp.Width == 1200 && bmp.Width * ApoiarLayout.ArteAltura == bmp.Height * ApoiarLayout.ArteLargura, $"a arte tem {bmp.Width}x{bmp.Height} e o layout supõe {ApoiarLayout.ArteLargura}x{ApoiarLayout.ArteAltura}");
        Check(bmp.GetPixel(2, 2).A == 0, "o canto da arte não é transparente");
        return 3;
    }

    /// <summary>A cor semitransparente <paramref name="frente"/> sobre o fundo opaco: o que o olho vê (a pílula de seleção da barra é translúcida).</summary>
    static Color Sobre(Color frente, Color fundo)
    {
        float a = frente.A / 255f;
        return Color.FromArgb((int)Math.Round(frente.R * a + fundo.R * (1 - a)), (int)Math.Round(frente.G * a + fundo.G * (1 - a)), (int)Math.Round(frente.B * a + fundo.B * (1 - a)));
    }

    static Bitmap Foto(Control controle)
    {
        var imagem = new Bitmap(controle.Width, controle.Height);
        controle.DrawToBitmap(imagem, new Rectangle(Point.Empty, controle.Size));
        return imagem;
    }

    static void Bombear(int vezes = 4) { for (int i = 0; i < vezes; i++) { Application.DoEvents(); Thread.Sleep(30); } }
    static void Check(bool valido, string mensagem) { if (!valido) throw new InvalidOperationException(mensagem); }
}
