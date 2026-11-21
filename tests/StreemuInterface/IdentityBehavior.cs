using System.Reflection;
using System.Text.Json.Nodes;
using Streemu.Filme;
using Streemu.Net;
using Streemu.Ui;
using Streemu.Ui.Avatars;
using Streemu.Util;

/// <summary>
/// Identidade visual (bloco 6) sem janela mostrada: posição fixa de Transmitir, marca na lateral aberta e no trilho, lista de amigos sem vazar sobre o
/// rodapé, ícone da janela/bandeja em vários tamanhos e cores/fontes/glifos num lugar só. Não mede contraste nem leitura no monitor real.
/// </summary>
static class IdentityBehavior
{
    static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(string? output)
    {
        if (output != null) Directory.CreateDirectory(output);
        var friends = Enumerable.Range(0, 30).Select(i => new SideFriend($"f{i}", $"Amigo com nome longo {i}", "Jogo de teste", i % 3 == 0, i < 2, null)).ToList();
        foreach (bool filme in new[] { false, true })
        foreach (var size in new[] { new Size(290, 700), new Size(80, 700), new Size(290, 480), new Size(80, 480) })
        {
            using var side = new SidePanel { Size = size, FilmeVisivel = filme };
            side.SetProfile("Leo", null);
            side.CreateControl();
            // Transmitir não anda: com e sem live, com e sem vídeos no palco, com e sem amigos (e com a lista rolada).
            int? transmitTop = null;
            foreach (var (list, streams, self) in new (IReadOnlyList<SideFriend>, int, SideSelf?)[]
            {
                ([], 0, null), (friends, 2, null), (friends, 2, new SideSelf("Jogo", 3, false)), ([], 5, new SideSelf("Jogo", 0, true)),
            })
            {
                side.SetData(list, streams, SideView.Home, "Sala de teste", self);
                Call(side, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, 10, 400, -120));
                var rows = Rows(side);
                var transmit = rows.Single(r => r.Kind == "Transmit").Bounds;
                transmitTop ??= transmit.Top;
                Check(transmit.Top == transmitTop, $"Transmitir mudou de lugar em {size} (filme {filme}): {transmit.Top} ≠ {transmitTop}");
                // O espaço do grupo fica antes de Transmitir, qualquer que seja o item de cima (Início desde o #273; antes era Configurações).
                // Fora a lista de amigos: rolada, o título "Amigos" sobe para trás da navegação (recortado) e não conta como vizinho.
                var above = rows.Where(r => r.Kind is not ("Section" or "GroupFilter" or "Friend") && r.Bounds.Bottom <= transmit.Top).MaxBy(r => r.Bounds.Bottom);
                Check(transmit.Top - above.Bounds.Bottom >= side.LogicalToDeviceUnits(10), $"Transmitir colado em {above.Kind} em {size}: não é um grupo à parte");
                var profile = rows.Single(r => r.Kind == "Profile").Bounds;
                Check(profile.Bottom <= size.Height && rows.Where(r => r.Kind is "Problem" or "Update" or "Bell" or "Atividade" or "Diag").All(r => r.Bounds.Bottom <= profile.Top),
                    $"Perfil saiu do rodapé em {size}");
            }

            // A lista rolável não pode desenhar por cima das ferramentas do rodapé: com 30 amigos, a faixa das ferramentas sai igual à de 0 amigos.
            side.SetData([], 2, SideView.Home, "Sala de teste");
            using var empty = Render(side);
            side.SetData(friends, 2, SideView.Home, "Sala de teste");
            for (int i = 0; i < 3; i++) Call(side, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, 10, 400, -120));
            using var full = Render(side);
            var tools = Rows(side).Where(r => r.Kind is "Problem" or "Update" or "Bell" or "Atividade" or "Diag" or "Profile").Select(r => r.Bounds).ToList();
            var viewport = (Rectangle)side.GetType().GetField("_friendsViewport", Hidden)!.GetValue(side)!;
            foreach (var box in tools)
            {
                Check(viewport.Height == 0 || !box.IntersectsWith(viewport), $"Ferramenta {box} dentro da área dos amigos {viewport} em {size}");
                Check(SamePixels(empty, full, box), $"Lista de amigos vazou sobre o rodapé em {size} ({box})");
            }

            // A marca aparece aberta (o logo inteiro, à esquerda) e no trilho (só o gato): o topo não pode ser só o fundo da lateral.
            int markSize = UiScale.Dp(side, 30);
            bool rail = side.Width < UiScale.Dp(side, (UiMetrics.RailWidth + UiMetrics.SidebarWidth) / 2);
            var mark = rail ? new Rectangle((side.Width - markSize) / 2 - UiScale.Dp(side, 6), UiScale.Dp(side, 10), markSize, UiScale.Dp(side, 34))
                : new Rectangle(UiScale.Dp(side, 14), UiScale.Dp(side, 10), UiScale.Dp(side, 150), UiScale.Dp(side, 34));
            Check(Drawn(full, mark, full.GetPixel(1, 1)) > 40, $"Logo ausente no topo em {size}");
            if (output != null && !filme) full.Save(Path.Combine(output, $"lateral-{size.Width}x{size.Height}.png"));
        }

        // Na altura mínima da janela (440 dp com a barra de título; 410 dp de área útil), sem live e sem Cinema, sobra ao menos um amigo visível.
        using (var low = new SidePanel())
        {
            low.Size = new Size(low.LogicalToDeviceUnits(232), low.LogicalToDeviceUnits(410));
            low.SetProfile("Leo", null);
            low.SetData(friends, 2, SideView.Home, "Sala de teste");
            Check(Rows(low).Count(r => r.Kind == "Friend") >= 1, "Na altura mínima a lateral não mostra nenhum amigo");
        }

        // Ícones: um .ico por atividade, cada um com vários tamanhos (o Windows escolhe), todos diferentes entre si e com arte desenhada.
        var atividades = Enum.GetValues<AtividadeIcone>();
        var porAtividade = atividades.ToDictionary(x => x, x => UiBrand.CreateIcon(x));
        try
        {
            foreach (var (atividade, icone) in porAtividade)
                foreach (int s in new[] { 16, 20, 24, 32, 40, 48, 64 })
                {
                    using var exact = new Icon(icone, s, s);
                    Check(exact.Width == s, $"Ícone {atividade} sem o tamanho {s}");
                }
            var bitmaps = porAtividade.ToDictionary(kv => kv.Key, kv => { using var i = new Icon(kv.Value, 32, 32); return i.ToBitmap(); });
            try
            {
                foreach (var (atividade, bmp) in bitmaps)
                    Check(Drawn(bmp, new Rectangle(0, 0, 32, 32), Color.Transparent) > 200, $"Ícone {atividade} vazio");
                foreach (var x in atividades)
                    foreach (var y in atividades.Where(y => y > x))
                        Check(!SamePixels(bitmaps[x], bitmaps[y], new Rectangle(0, 0, 32, 32)), $"Ícone {x} igual ao {y}");
                if (output != null)
                    using (var sheet = new Bitmap(6 * 72, atividades.Length * 72))
                    using (var g = Graphics.FromImage(sheet))
                    {
                        g.Clear(Theme.Backdrop);
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                        int linha = 0;
                        foreach (var (_, icone) in porAtividade)
                        {
                            int column = 0;
                            foreach (int s in new[] { 16, 20, 24, 32, 48, 64 })
                            {
                                using var i1 = new Icon(icone, s, s); using var p1 = i1.ToBitmap();
                                g.DrawImage(p1, column * 72 + 4, linha * 72 + 4, 64, 64);
                                column++;
                            }
                            linha++;
                        }
                        sheet.Save(Path.Combine(output, "icones-16-20-24-32-48-64.png"));
                    }
            }
            finally { foreach (var bmp in bitmaps.Values) bmp.Dispose(); }
        }
        finally { foreach (var icone in porAtividade.Values) icone.Dispose(); }
        Atividade();
        using (var tray = TrayVisuals.CreateIcon(AtividadeIcone.Transmitindo)) Check(tray.Size == SystemInformation.SmallIconSize, $"Bandeja fora do tamanho pequeno do sistema: {tray.Size}");
        Check(PlayerOverlay.FullscreenLook(true, false).Glyph == UiGlyphs.Fullscreen && PlayerOverlay.FullscreenLook(true, true).Glyph == UiGlyphs.ExitFullscreen,
            "Barra do vídeo não usa a tabela única de glifos");

        Sources();
        SameIconAsExe();
        PessoasPorId();
        Console.WriteLine($"OK: identidade — Transmitir na mesma posição (com/sem live, palco, amigos, Cinema) e em grupo à parte, perfil no rodapé, lista sem vazar no rodapé, marca aberta/trilho, ícone de 16 a 64 px e bandeja no tamanho do sistema, cores/fontes/glifos centralizados, dois amigos de mesmo nome (faixa do player, foto do Cinema, sessão do filme e lista Na sessão) cada um pelo seu id. Ícone pequeno do sistema {SystemInformation.SmallIconSize.Width} px.");
    }

    /// <summary>
    /// Spec 027, seção 7: dois amigos com o MESMO nome no grupo. Cada um tem de continuar sendo ele: a faixa do player, a foto do cartão do Cinema,
    /// a sessão do filme e a lista "Na sessão" se separam pelo id da conta, e o nome é só o texto. Sem janela mostrada nem rede.
    /// </summary>
    static void PessoasPorId()
    {
        // faixa do player: duas linhas "Bia", cada uma com o id e a foto da sua conta; fixar uma não anima a outra
        using var fotoA = new AvatarImage([new Bitmap(2, 2)], []);
        using var fotoB = new AvatarImage([new Bitmap(2, 2)], []);
        var sala = new SalaFalsa("c-eu", new Member("c-eu", "Eu", "", null), new Member("c-ana", "Ana", "", new LiveInfo("s1", "Jogo", "720p", 0, 0)),
            new Member("c-bia1", "Bia", "", null), new Member("c-bia2", "Bia", "", null));
        var fotos = new Dictionary<string, AvatarImage> { ["c-bia1"] = fotoA, ["c-bia2"] = fotoB };
        var faixa = RosterEntry.FromWatchers(sala, "c-ana", new HashSet<string> { "c-bia1", "c-bia2" }, id => fotos.GetValueOrDefault(id));
        var bias = faixa.Where(e => e.Nick == "Bia").ToList();
        Check(faixa.Count == 4 && bias.Count == 2, $"A faixa perdeu um dos dois amigos de mesmo nome: {faixa.Count} linhas");
        Check(bias.Select(e => e.Id).OrderBy(i => i).SequenceEqual(["c-bia1", "c-bia2"]), "As linhas da faixa não trazem o id da conta");
        Check(ReferenceEquals(bias.Single(e => e.Id == "c-bia1").Avatar, fotoA) && ReferenceEquals(bias.Single(e => e.Id == "c-bia2").Avatar, fotoB), "A foto da faixa foi para a pessoa errada");
        using (var strip = new RosterStrip())
        {
            strip.SetEntries(faixa);
            var lista = faixa.ToList();
            int primeiro = lista.FindIndex(e => e.Id == "c-bia1"), segundo = lista.FindIndex(e => e.Id == "c-bia2");
            typeof(RosterStrip).GetField("_pinned", Hidden)!.SetValue(strip, lista[primeiro].Key);
            var anima = typeof(RosterStrip).GetMethod("IsAnimating", Hidden)!;
            Check((bool)anima.Invoke(strip, [primeiro])! && !(bool)anima.Invoke(strip, [segundo])!, "Fixar um amigo animou o outro de mesmo nome");
        }

        // cartão do Cinema: o anfitrião e os dois "Bia" (ids em "quem_ids") são três pessoas e a foto sai pelo id de cada uma
        var anuncio = new JsonObject
        {
            ["de"] = "c-ana", ["filme"] = "f1", ["nome"] = "Filme", ["anfitriao"] = "Ana",
            ["quem"] = new JsonArray("Bia", "Bia"), ["quem_ids"] = new JsonArray("c-bia1", "c-bia2"),
        };
        var pedidos = new List<string>();
        var cartaoTipo = typeof(CinemaView).Assembly.GetType("Streemu.Ui.CinemaSessionCard")!;
        Func<string?, string, AvatarImage?> fotoDe = (id, _) => { pedidos.Add(id ?? "?"); return id != null ? fotos.GetValueOrDefault(id) : null; };
        Func<JsonObject, Image?> capaDe = _ => null;
        using var cartao = (Control)Activator.CreateInstance(cartaoTipo, [fotoDe, capaDe])!;
        cartaoTipo.GetMethod("SetAnnouncement")!.Invoke(cartao, [anuncio]);
        var quem = (System.Collections.IEnumerable)cartaoTipo.GetMethod("Watching", Hidden)!.Invoke(cartao, null)!;
        var ids = quem.Cast<object>().Select(t => (string?)t.GetType().GetField("Item1")!.GetValue(t)).ToList();
        Check(ids.SequenceEqual(["c-ana", "c-bia1", "c-bia2"]), "O cartão do Cinema juntou os dois amigos de mesmo nome: " + string.Join(",", ids));
        cartao.Size = new Size(640, 420); cartao.CreateControl();
        using (var quadro = Render(cartao)) { }
        Check(pedidos.Contains("c-bia1") && pedidos.Contains("c-bia2") && !pedidos.Contains("?"), "O cartão não pediu a foto de cada um pelo id: " + string.Join(",", pedidos));
        // anfitrião de versão antiga (sem "quem_ids"): volta ao nome, como era, sem perder a pessoa
        anuncio.Remove("quem_ids");
        cartaoTipo.GetMethod("SetAnnouncement")!.Invoke(cartao, [anuncio]);
        var antigo = (System.Collections.IEnumerable)cartaoTipo.GetMethod("Watching", Hidden)!.Invoke(cartao, null)!;
        Check(antigo.Cast<object>().Count() == 2, "Anúncio sem ids deixou de mostrar quem assiste");

        // sessão do filme (anfitrião): as mensagens chegam com o id da ligação (o MainForm carimba); mesmo nome, duas pessoas
        var s = new Sessao(true, "f1", "Filme", "Ana", "Ana", null!, () => 0, () => 0, _ => { }, "c-ana");
        s.Receber(new JsonObject { ["type"] = "filme_pronto", ["filme"] = "f1", ["nome"] = "Bia", ["id"] = "c-bia1", ["pronto"] = true });
        s.Receber(new JsonObject { ["type"] = "filme_pronto", ["filme"] = "f1", ["nome"] = "Bia", ["id"] = "c-bia2", ["baixando"] = new JsonObject { ["pct"] = 40, ["eta_s"] = -1.0 } });
        Check(s.Amigos.Count == 2 && s.Amigos["c-bia1"] == "pronto" && s.Amigos["c-bia2"].StartsWith("baixando"), "A sessão misturou os dois amigos de mesmo nome");
        Check(s.NomeDe("c-bia1") == "Bia" && s.NomeDe("c-bia2") == "Bia", "O nome na tela não é o que a pessoa mandou");
        s.NomeNaSala = id => id == "c-bia2" ? "Beatriz" : null; // trocar de nome muda o texto, não a pessoa
        Check(s.NomeDe("c-bia2") == "Beatriz" && s.Amigos.ContainsKey("c-bia2"), "A troca de nome mudou quem a pessoa é");
        s.AmigoSaiu("c-bia1");
        Check(s.Amigos.Count == 1 && s.Amigos.ContainsKey("c-bia2"), "Quem saiu levou o outro de mesmo nome junto");
        // app antigo (sem id em lugar nenhum): vale o nome, como antes
        s.Receber(new JsonObject { ["type"] = "filme_pronto", ["filme"] = "f1", ["nome"] = "Leo", ["pronto"] = true });
        Check(s.Amigos.ContainsKey("Leo") && s.NomeDe("Leo") == "Leo", "Mensagem sem id deixou de funcionar");

        // lista "Na sessão": "Você" é achado pelo meu id, não pelo nome
        var pessoas = new[] { new MpvUi.Pessoa("Bia", null, "Pronto", "ok", Id: "c-bia1"), new MpvUi.Pessoa("Bia", null, "Baixando", "espera", Id: "c-bia2") };
        Check(pessoas.Count(p => p.Sou("c-bia2", "Bia")) == 1 && pessoas[1].Sou("c-bia2", "Bia") && !pessoas[0].Sou("c-bia2", "Bia"), "A lista Na sessão não separou eu do outro de mesmo nome");
        Check(new MpvUi.Pessoa("Bia", null, null).Sou("c-bia2", "Bia"), "Anfitrião antigo (sem id na lista) deixou de achar a pessoa pelo nome");
    }

    /// <summary>ISala mínima só com a lista de membros (o que a faixa do player lê).</summary>
    sealed class SalaFalsa(string meuId, params Member[] membros) : ISala
    {
        public string MyId => meuId;
        public bool Connected => true;
        public string Status => "";
        public IReadOnlyList<Member> Members => membros;
        public IEnumerable<Member> Friends => membros.Where(m => m.Id != meuId);
        public IReadOnlyList<Ausente> Ausentes => [];
        public bool IsPresent(string id) => membros.Any(m => m.Id == id);
        public Member? Find(string id) => membros.FirstOrDefault(m => m.Id == id);
        public Member? FindByStream(string streamId) => null;
        public void SetNick(string nick) { }
        public void SetLive(LiveInfo? live) { }
        public Task SendTo(string id, JsonObject msg) => Task.CompletedTask;
        public Task SendTelemetryNow(IEnumerable<string> ids) => Task.CompletedTask;
        public event Action? Changed { add { } remove { } }
        public event Action<string, string, JsonObject>? Message { add { } remove { } }
        public event Action<string, JsonObject?>? Undeliverable { add { } remove { } }
        public event Action? TelemetryNow { add { } remove { } }
        public event Action<string>? Encerrada { add { } remove { } }
        public void Dispose() { }
    }

    /// <summary>
    /// Guarda da manutenção (spec 014, 7.4): cor em hexadecimal, nome de fonte e glifo da fonte de símbolos só no Theme/UiResources. As exceções são
    /// listadas com o motivo; uma nova ocorrência fora delas falha o teste para alguém decidir se vira token.
    /// </summary>
    static void Sources()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "app", "Streemu"))) root = root.Parent;
        if (root == null) { Console.WriteLine("Aviso: código-fonte não encontrado; guarda de literais pulada."); return; }
        var app = Path.Combine(root.FullName, "app", "Streemu");
        var allowed = new Dictionary<string, string>
        {
            ["Util/Theme.cs"] = "a paleta",
            ["Util/ThemePalette.cs"] = "os valores das paletas escura e clara",
            ["Util/UiResources.cs"] = "tipografia, glifos e marca",
            ["Media/StreamSender.cs"] = "texto desenhado dentro do vídeo enviado, não é interface",
            ["Ui/Avatars/AvatarDecoder.cs"] = "fundo em que GIFs com transparência são achatados no cache de avatares",
        };
        var pattern = new System.Text.RegularExpressions.Regex("ColorTranslator\\.FromHtml|\"Segoe UI|\"Consolas\"|\"Segoe MDL2|Color\\.FromArgb\\(\\s*\\d|[\\uE700-\\uF8FF]");
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(app, file).Replace('\\', '/');
            if (rel.StartsWith("bin/") || rel.StartsWith("obj/") || allowed.ContainsKey(rel)) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (pattern.IsMatch(lines[i]) && !lines[i].TrimStart().StartsWith("//")) found.Add($"{rel}:{i + 1}");
        }
        Check(found.Count == 0, "Cor, fonte ou glifo fora do Theme/UiResources: " + string.Join(", ", found));
    }

    /// <summary>Prioridade do ícone por atividade: Cinema, depois transmitindo, depois assistindo, depois ocioso. As 8 combinações.</summary>
    static void Atividade()
    {
        for (int bits = 0; bits < 8; bits++)
        {
            bool cinema = (bits & 4) != 0, transmitindo = (bits & 2) != 0, assistindo = (bits & 1) != 0;
            var esperado = cinema ? AtividadeIcone.Cinema : transmitindo ? AtividadeIcone.Transmitindo : assistindo ? AtividadeIcone.Assistindo : AtividadeIcone.Ocioso;
            Check(IconeAtividade.Escolher(cinema, transmitindo, assistindo) == esperado,
                $"Ícone errado para cinema={cinema} transmitindo={transmitindo} assistindo={assistindo}: veio {IconeAtividade.Escolher(cinema, transmitindo, assistindo)}, esperado {esperado}");
        }
        Check(Enum.GetValues<AtividadeIcone>().Select(IconeAtividade.Arquivo).Distinct().Count() == 4, "Duas atividades apontam para o mesmo .ico");
        Selos();
    }

    /// <summary>Selo da barra de tarefas: ocioso sem selo; os outros três têm ícone 16x16 (e 32x32) com desenho e diferentes entre si. O COM real só roda com a barra.</summary>
    static void Selos()
    {
        Check(SeloBarraTarefas.Criar(AtividadeIcone.Ocioso, 16) == null, "Ocioso não devia ter selo");
        var comSelo = Enum.GetValues<AtividadeIcone>().Where(SeloBarraTarefas.TemSelo).ToArray();
        Check(comSelo.Length == 3, "Esperava selo para transmitindo, assistindo e Cinema");
        foreach (int tamanho in new[] { 16, 32 })
        {
            var bitmaps = comSelo.ToDictionary(a => a, a =>
            {
                var icone = SeloBarraTarefas.Criar(a, tamanho);
                Check(icone != null && icone.Width == tamanho && icone.Height == tamanho, $"Selo {a} não tem {tamanho}x{tamanho}");
                Check(!string.IsNullOrWhiteSpace(SeloBarraTarefas.Descricao(a)), $"Selo {a} sem descrição");
                return icone!.ToBitmap();
            });
            try
            {
                foreach (var (a, bmp) in bitmaps) Check(Drawn(bmp, new Rectangle(0, 0, tamanho, tamanho), Color.Transparent) > tamanho, $"Selo {a} vazio em {tamanho}px");
                foreach (var x in comSelo) foreach (var y in comSelo.Where(y => y > x))
                    Check(!SamePixels(bitmaps[x], bitmaps[y], new Rectangle(0, 0, tamanho, tamanho)), $"Selo {x} igual ao {y} em {tamanho}px");
            }
            finally { foreach (var bmp in bitmaps.Values) bmp.Dispose(); }
        }
        var rascunho = Environment.GetEnvironmentVariable("STREEMU_SELO_SAIDA");
        if (rascunho != null)
        {
            // folha de conferência: cada selo ampliado 8x em fundo claro e escuro
            using var folha = new Bitmap(3 * 144, 2 * 144);
            using var g = Graphics.FromImage(folha);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            for (int linha = 0; linha < 2; linha++)
                for (int c = 0; c < comSelo.Length; c++)
                {
                    using var fundo = new SolidBrush(linha == 0 ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32));
                    g.FillRectangle(fundo, c * 144, linha * 144, 144, 144);
                    using var bmp = SeloBarraTarefas.Desenhar(comSelo[c], 16);
                    g.DrawImage(bmp, c * 144 + 8, linha * 144 + 8, 128, 128);
                }
            Directory.CreateDirectory(rascunho);
            folha.Save(Path.Combine(rascunho, "selos-8x.png"));
        }
    }

    /// <summary>A janela e a bandeja usam o mesmo .ico do exe e do instalador (embutido no build): trocar a arte num lugar troca em todos.</summary>
    static void SameIconAsExe()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "app", "Streemu"))) root = root.Parent;
        if (root == null) { Console.WriteLine("Aviso: código-fonte não encontrado; comparação do ícone pulada."); return; }
        foreach (var atividade in Enum.GetValues<AtividadeIcone>())
        {
            string arquivo = IconeAtividade.Arquivo(atividade);
            using var embedded = typeof(UiBrand).Assembly.GetManifestResourceStream("Streemu.Arte." + arquivo)!;
            using var copy = new MemoryStream(); embedded.CopyTo(copy);
            Check(copy.ToArray().AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(root.FullName, "app", "Streemu", arquivo))),
                $"Ícone embutido diferente do {arquivo} do app (o ocioso é o mesmo do exe)");
        }
        foreach (var art in new[] { UiBrand.Mark, UiBrand.SleepingMumu })
            using (var image = UiBrand.LoadArt(art)) Check(image.Width == image.Height && image.Width >= 256, $"Arte {art} fora do formato (quadrada, 256 px ou mais)");
    }

    /// <summary>Quantos pixels opacos fogem do fundo: a arte é colorida, então qualquer desenho dela dá dezenas.</summary>
    static int Drawn(Bitmap image, Rectangle box, Color background)
    {
        box.Intersect(new Rectangle(0, 0, image.Width, image.Height));
        int count = 0;
        for (int y = box.Top; y < box.Bottom; y++) for (int x = box.Left; x < box.Right; x++)
        {
            var p = image.GetPixel(x, y);
            if (p.A > 200 && Math.Abs(p.R - background.R) + Math.Abs(p.G - background.G) + Math.Abs(p.B - background.B) > 60) count++;
        }
        return count;
    }

    readonly record struct RowInfo(string Kind, Rectangle Bounds);
    static List<RowInfo> Rows(SidePanel side) => ((System.Collections.IEnumerable)side.GetType().GetMethod("BuildRows", Hidden)!.Invoke(side, null)!).Cast<object>()
        .Select(r => new RowInfo(r.GetType().GetProperty("Kind")!.GetValue(r)!.ToString()!, (Rectangle)r.GetType().GetProperty("Bounds")!.GetValue(r)!)).ToList();

    static Bitmap Render(Control c) { var image = new Bitmap(c.Width, c.Height); c.DrawToBitmap(image, c.ClientRectangle); return image; }

    static bool SamePixels(Bitmap a, Bitmap b, Rectangle box)
    {
        box.Intersect(new Rectangle(0, 0, Math.Min(a.Width, b.Width), Math.Min(a.Height, b.Height)));
        for (int y = box.Top; y < box.Bottom; y++) for (int x = box.Left; x < box.Right; x++) if (a.GetPixel(x, y) != b.GetPixel(x, y)) return false;
        return true;
    }

    static bool HasColor(Bitmap image, Rectangle box, Color color)
    {
        box.Intersect(new Rectangle(0, 0, image.Width, image.Height));
        for (int y = box.Top; y < box.Bottom; y++) for (int x = box.Left; x < box.Right; x++)
        {
            var p = image.GetPixel(x, y);
            if (p.A > 200 && Math.Abs(p.R - color.R) < 12 && Math.Abs(p.G - color.G) < 12 && Math.Abs(p.B - color.B) < 12) return true;
        }
        return false;
    }

    static void Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, Hidden)!.Invoke(o, args);
    static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
