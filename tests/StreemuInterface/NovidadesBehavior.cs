using Streemu.Ui;
using Streemu.Util;

/// <summary>
/// --novidades-check [pasta]: a página Novidades inteira (plano 027, etapa 3), sozinha numa janela fora da tela com a largura da ÁREA DA PÁGINA (a barra lateral
/// não entra). Para cada tema, escala da interface (100, 125 e 150 %), largura da área em pixels (1680, 1300, 1027, 845, 600 e 520), altura (700 e 520) e
/// conteúdo (as novidades reais com metade vista, uma versão, lista vazia, uma versão com título de 70 letras sem espaço) confere: a disposição certa pela
/// conta em dp (calculada aqui, não lida da tela), sem rolagem horizontal, tudo dentro da página, as peças sem se cobrir, leitura de até 640 dp, o conjunto
/// centrado, as pílulas e o "Mais" numa linha só, e que escolher a versão 4 (no índice, na pílula ou no menu) a deixa no topo com a seleção acompanhando.
/// Depois: redimensionar de 1680 para 600 px mantém a versão 4 no topo, e trocar o tema com a página aberta (pelo ThemeSwitch, o caminho do app) refaz as
/// cores. Com a pasta, salva as fotos.
/// </summary>
static class NovidadesBehavior
{
    static readonly int[] Larguras = [1680, 1300, 1027, 845, 600, 520];
    static readonly int[] Alturas = [700, 520, 440];
    const string Rotulo = "0.5.1 · build de 20/11 10:54";

    static IReadOnlyList<VersaoNovidade> Conteudo(string nome, out string instalada, out HashSet<string>? vistas)
    {
        vistas = null; instalada = "";
        var reais = Novidades.Embutidas();
        switch (nome)
        {
            case "real":
                instalada = reais.First(v => !v.NaoLancado).Versao;
                vistas = reais.SelectMany(v => v.Itens).Select(i => i.Chave).Where((_, n) => n % 2 == 0).ToHashSet();
                return reais;
            case "uma":
                var uma = reais.First(v => !v.NaoLancado);
                instalada = uma.Versao;
                return [uma];
            case "longo":
                instalada = "9.9.9";
                return
                [
                    new("9.9.9", new DateOnly(2026, 11, 20), "Um resumo comprido o bastante para quebrar em mais de uma linha.", true, false,
                        [new("Novo", new string('W', 70), string.Join(' ', Enumerable.Repeat("detalhe longo", 12)) + " " + new string('m', 90), "k1")]),
                ];
            default: return [];
        }
    }

    public static void Run(string? pasta)
    {
        if (pasta != null) Directory.CreateDirectory(pasta);
        int casos = 0;
        var usuario = UiScale.User; bool claroAntes = Theme.IsLight;
        try
        {
            foreach (bool claro in new[] { false, true })
            foreach (double escala in new[] { 1.0, 1.25, 1.5 })
            foreach (int largura in Larguras)
            foreach (int altura in Alturas)
            foreach (var conteudo in new[] { "real", "uma", "vazio", "longo" })
            {
                Theme.Use(claro); UiScale.User = escala;
                using var janela = NovaJanela(largura, altura, out var tela);
                var versoes = Conteudo(conteudo, out var instalada, out var vistas);
                tela.Mostrar(versoes, instalada, vistas);
                Bombear();
                string onde = $"{(claro ? "claro" : "escuro")} {escala * 100:0}% {largura}x{altura}px {conteudo}";
                Confere(janela, tela, versoes, onde);
                if (conteudo == "real") ConfereEscolha(tela, onde);
                if (pasta != null && conteudo == "real" && QueroFoto(escala, largura, altura))
                {
                    tela.Leitura.IrPara(0);
                    Bombear();
                    Foto(janela, Path.Combine(pasta, $"novidades-{(claro ? "claro" : "escuro")}-{escala * 100:0}-{largura}x{altura}.png"));
                }
                casos++;
            }
            foreach (bool claro in new[] { false, true })
            foreach (double escala in new[] { 1.0, 1.5 })
            {
                Theme.Use(claro); UiScale.User = escala;
                ConfereRedimensionar(claro, escala);
                casos++;
                ConfereTema(claro, escala, 1680);
                ConfereTema(claro, escala, 600);
                casos += 2;
            }
        }
        finally { UiScale.User = usuario; Theme.Use(claroAntes); }
        Console.WriteLine($"novidades-check OK: {casos} combinações" + (pasta != null ? $"; fotos em {pasta}" : ""));
    }

    static bool QueroFoto(double escala, int largura, int altura) =>
        altura == 700 ? escala == 1.0 || (escala == 1.5 && largura is 1027 or 845)
        : (escala == 1.0 && largura == 1300) || (escala == 1.5 && largura == 600);

    static Form NovaJanela(int largura, int altura, out NovidadesTela tela)
    {
        var janela = new Form
        {
            StartPosition = FormStartPosition.Manual, FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
            Bounds = new Rectangle(-8000, 40, largura, altura), BackColor = Theme.Surface, ForeColor = Theme.Text,
            Font = UiTypography.Create(UiTypography.Body), // como a janela principal: as peças escalam a partir dela (UiScale.WatchFont)
        };
        tela = new NovidadesTela { RotuloVersao = Rotulo };
        janela.Controls.Add(tela);
        janela.Show();
        Bombear();
        return janela;
    }

    static void Bombear(int vezes = 4) { for (int i = 0; i < vezes; i++) { Application.DoEvents(); Thread.Sleep(30); } }

    static void Foto(Form janela, string arquivo)
    {
        using var imagem = new Bitmap(janela.Width, janela.Height);
        janela.DrawToBitmap(imagem, new Rectangle(0, 0, janela.Width, janela.Height));
        imagem.Save(arquivo);
    }

    // ---------- conferências ----------

    static void Confere(Form janela, NovidadesTela tela, IReadOnlyList<VersaoNovidade> versoes, string onde)
    {
        double k = UiScale.Dp(tela, 10000) / 10000.0;
        int w = tela.ClientSize.Width, h = tela.ClientSize.Height;
        Exige(w == janela.ClientSize.Width, $"{onde}: a página não ocupa a janela ({w} de {janela.ClientSize.Width})");
        // a regra do plano (D5), escrita de novo aqui de propósito: 1180 e 880 dp
        double dp = w / k;
        var esperada = dp >= 1180 ? NovidadesTela.Disposicao.Larga : dp >= 880 ? NovidadesTela.Disposicao.Media : NovidadesTela.Disposicao.Estreita;
        Exige(tela.DisposicaoAtual == esperada, $"{onde}: disposição {tela.DisposicaoAtual} para {dp:0} dp (esperada {esperada})");
        var d = esperada;
        Exige(tela.Indice.Visible == (d != NovidadesTela.Disposicao.Estreita), $"{onde}: índice {(tela.Indice.Visible ? "à mostra" : "escondido")} na {d}");
        Exige(tela.Cartao.Visible == (d == NovidadesTela.Disposicao.Larga), $"{onde}: cartão {(tela.Cartao.Visible ? "à mostra" : "escondido")} na {d}");
        // a faixa só some (nunca na Larga, onde ela não existe) quando falta altura para a leitura
        Exige(tela.Faixa.Visible == (d != NovidadesTela.Disposicao.Larga && !tela.FaixaEscondida), $"{onde}: faixa {(tela.Faixa.Visible ? "à mostra" : "escondida")} na {d}");
        Exige(!tela.FaixaEscondida || d != NovidadesTela.Disposicao.Larga, $"{onde}: faixa escondida na Larga");
        // com conteúdo e uma página de pelo menos 440 dp de altura, a leitura visível tem pelo menos 200 dp
        if (versoes.Count > 0 && h / k >= 440)
            Exige(tela.Leitura.Height >= UiScale.Dp(tela, 200), $"{onde}: leitura com {tela.Leitura.Height} px ({tela.Leitura.Height / k:0} dp), menos de 200 dp");
        // a faixa é uma linha só: a altura é a do botão mais as margens, e o botão fica dentro dela
        if (tela.Faixa.Visible)
            Exige(tela.Faixa.Height <= UiScale.Dp(tela, 34 + 2 * 10) + 2 && tela.Faixa.Botao.Bottom <= tela.Faixa.Height, $"{onde}: a faixa tem mais de uma linha ({tela.Faixa.Height} px)");
        Exige(tela.Pilulas.Visible == (d == NovidadesTela.Disposicao.Estreita), $"{onde}: pílulas {(tela.Pilulas.Visible ? "à mostra" : "escondidas")} na {d}");
        Exige(tela.Leitura.Visible && tela.Cabecalho.Visible, $"{onde}: leitura ou cabeçalho escondidos");

        // sem rolagem horizontal em nenhum controle de rolagem
        foreach (var rolavel in new ScrollableControl[] { tela.Leitura, tela.Indice })
            Exige(!rolavel.HorizontalScroll.Visible && rolavel.AutoScrollMinSize.Width == 0, $"{onde}: apareceu rolagem horizontal em {rolavel.GetType().Name}");

        // cada peça visível está dentro da página, tem tamanho de verdade e não cobre outra
        var visiveis = tela.Controls.Cast<Control>().Where(c => c.Visible).ToList();
        foreach (var c in visiveis)
        {
            Exige(c.Width > 0 && c.Height > 0, $"{onde}: {Nome(c)} com tamanho {c.Size}");
            Exige(c.Left >= 0 && c.Top >= 0 && c.Right <= w && c.Bottom <= h, $"{onde}: {Nome(c)} {c.Bounds} sai da página {w}x{h}");
        }
        for (int i = 0; i < visiveis.Count; i++)
            for (int j = i + 1; j < visiveis.Count; j++)
                Exige(!visiveis[i].Bounds.IntersectsWith(visiveis[j].Bounds), $"{onde}: {Nome(visiveis[i])} {visiveis[i].Bounds} e {Nome(visiveis[j])} {visiveis[j].Bounds} se cobrem");

        // leitura de até 640 dp; todo retângulo de texto do cabeçalho cabe na largura
        Exige(tela.Leitura.Width <= UiScale.Dp(tela, 640), $"{onde}: leitura com {tela.Leitura.Width} px (limite {UiScale.Dp(tela, 640)})");
        foreach (var r in tela.Cabecalho.RetangulosDosTextos())
            Exige(r.Left >= 0 && r.Right <= w && r.Width > 0 && r.Height > 0, $"{onde}: texto do cabeçalho {r} fora da página");
        Exige(tela.Leitura.RetangulosDosCartoes().Count == versoes.Count, $"{onde}: cartões da leitura diferentes das versões");

        // o conjunto fica centrado (as margens da esquerda e da direita iguais, com 2 px de folga de arredondamento)
        Control primeiro = d == NovidadesTela.Disposicao.Estreita ? tela.Leitura : tela.Indice;
        Control ultimo = d == NovidadesTela.Disposicao.Larga ? tela.Cartao : tela.Leitura;
        Exige(Math.Abs(primeiro.Left - (w - ultimo.Right)) <= 2, $"{onde}: conjunto fora do centro (esquerda {primeiro.Left}, direita {w - ultimo.Right})");
        Exige(primeiro.Left >= UiScale.Dp(tela, UiMetrics.PagePadding) - 1, $"{onde}: o conjunto passa da margem ({primeiro.Left})");

        if (d != NovidadesTela.Disposicao.Larga) { if (tela.Faixa.Visible) ConfereBotao(tela.Faixa, tela.Faixa.Botao, onde + " faixa"); }
        else
        {
            ConfereBotao(tela.Cartao, tela.Cartao.Botao, onde + " cartão");
            if (tela.Mumu.Visible)
            {
                Exige(tela.Mumu.Width == tela.Mumu.Height && tela.Mumu.Width >= UiScale.Dp(tela, 96) && tela.Mumu.Width <= UiScale.Dp(tela, 260), $"{onde}: a Mumu do cartão com {tela.Mumu.Size}");
                Exige(tela.Mumu.Left >= tela.Cartao.Left && tela.Mumu.Right <= tela.Cartao.Right && tela.Mumu.Top >= tela.Cartao.Bottom, $"{onde}: a Mumu {tela.Mumu.Bounds} fora da coluna do cartão {tela.Cartao.Bounds}");
            }
        }
        if (d == NovidadesTela.Disposicao.Estreita) ConferePilulas(tela, versoes, onde);
        else
        {
            var linhas = tela.Indice.RetangulosDasLinhas();
            Exige(linhas.Count == versoes.Count, $"{onde}: índice com {linhas.Count} linhas para {versoes.Count} versões");
            foreach (var r in linhas) Exige(r.Left >= 0 && r.Right <= tela.Indice.ClientSize.Width && r.Width > 0 && r.Height > 0, $"{onde}: linha do índice {r} fora da largura");
        }
    }

    static void ConfereBotao(Control dono, Button botao, string onde)
    {
        Exige(botao.Visible && botao.Width > 0 && botao.Height > 0, $"{onde}: botão sem tamanho (visível {botao.Visible}, {botao.Bounds}, dono {dono.Visible} {dono.Bounds})");
        Exige(botao.Left >= 0 && botao.Top >= 0 && botao.Right <= dono.Width && botao.Bottom <= dono.Height, $"{onde}: botão {botao.Bounds} sai de {dono.Size}");
        int texto = TextRenderer.MeasureText(botao.Text, botao.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width; // vazio = só o ícone (faixa estreita)
        Exige(texto <= botao.Width, $"{onde}: o botão \"{botao.Text}\" corta o texto ({texto} em {botao.Width})");
    }

    static void ConferePilulas(NovidadesTela tela, IReadOnlyList<VersaoNovidade> versoes, string onde)
    {
        var p = tela.Pilulas;
        var rs = p.RetangulosVisiveis();
        int esperadas = p.Visiveis + (p.TemMais ? 1 : 0);
        Exige(rs.Count == esperadas, $"{onde}: {rs.Count} pílulas para {esperadas}");
        Exige(p.Visiveis <= versoes.Count && (p.TemMais || p.Visiveis == versoes.Count), $"{onde}: contas das pílulas");
        if (versoes.Count > 0 && p.TemMais) Exige(p.Visiveis < versoes.Count, $"{onde}: Mais sem versão escondida");
        foreach (var r in rs)
        {
            Exige(r.Left >= 0 && r.Right <= p.ClientSize.Width && r.Top >= 0 && r.Bottom <= p.ClientSize.Height && r.Width > 0 && r.Height > 0, $"{onde}: pílula {r} fora da fileira {p.ClientSize}");
            Exige(r.Top == rs[0].Top, $"{onde}: as pílulas não estão numa linha só");
        }
        for (int i = 0; i + 1 < rs.Count; i++) Exige(rs[i].Right < rs[i + 1].Left, $"{onde}: pílulas {rs[i]} e {rs[i + 1]} se tocam");
        // o que não está à mostra está no menu
        using var menu = p.CriarMenuMais();
        Exige(menu.Items.Count == versoes.Count - p.Visiveis, $"{onde}: o menu tem {menu.Items.Count} itens para {versoes.Count - p.Visiveis} escondidas");
    }

    /// <summary>Escolher a versão 4 pelo caminho da disposição (índice, pílula ou menu do Mais): ela fica no topo da leitura e a seleção acompanha.</summary>
    static void ConfereEscolha(NovidadesTela tela, string onde)
    {
        Exige(tela.Leitura.Versoes.Count > 5, $"{onde}: as novidades reais têm só {tela.Leitura.Versoes.Count} versões");
        if (tela.DisposicaoAtual == NovidadesTela.Disposicao.Estreita)
        {
            if (tela.Pilulas.Visiveis > 4) tela.Pilulas.Escolher(4);
            else
            {
                using var menu = tela.Pilulas.CriarMenuMais();
                ((ToolStripMenuItem)menu.Items[4 - tela.Pilulas.Visiveis]).PerformClick();
                Exige(tela.Pilulas.MaisEmDestaque, $"{onde}: a versão 4 está no menu e o Mais não ficou em destaque");
            }
        }
        else tela.Indice.Escolher(4);
        Bombear();
        Exige(tela.Leitura.VersaoNoTopo == 4, $"{onde}: escolher a versão 4 deixou a {tela.Leitura.VersaoNoTopo} no topo");
        Exige(tela.Indice.Selecionada == 4 && tela.Pilulas.Selecionada == 4, $"{onde}: seleção fora de sincronia (índice {tela.Indice.Selecionada}, pílulas {tela.Pilulas.Selecionada})");
        // rolar pela leitura leva a seleção junto
        tela.Leitura.IrPara(2);
        Bombear();
        Exige(tela.Indice.Selecionada == 2 && tela.Pilulas.Selecionada == 2, $"{onde}: a seleção não acompanhou a leitura (índice {tela.Indice.Selecionada}, pílulas {tela.Pilulas.Selecionada})");
        Exige(tela.Pilulas.MaisEmDestaque == (tela.Pilulas.TemMais && 2 >= tela.Pilulas.Visiveis), $"{onde}: destaque do Mais incoerente com a versão 2");
    }

    static void ConfereRedimensionar(bool claro, double escala)
    {
        string onde = $"{(claro ? "claro" : "escuro")} {escala * 100:0}% redimensionar";
        using var janela = NovaJanela(1680, 700, out var tela);
        var versoes = Conteudo("real", out var instalada, out var vistas);
        tela.Mostrar(versoes, instalada, vistas);
        Bombear();
        tela.Indice.Escolher(4);
        Bombear();
        Exige(tela.Leitura.VersaoNoTopo == 4, $"{onde}: a versão 4 não ficou no topo antes de redimensionar");
        foreach (int largura in new[] { 1300, 600, 845, 1680 })
        {
            janela.Width = largura;
            Bombear();
            Exige(tela.Leitura.VersaoNoTopo == 4, $"{onde}: ao ir para {largura} px ({tela.DisposicaoAtual}) a versão do topo virou {tela.Leitura.VersaoNoTopo}");
            Exige(tela.Indice.Selecionada == 4 && tela.Pilulas.Selecionada == 4, $"{onde}: seleção perdida ao ir para {largura} px");
        }
    }

    static void ConfereTema(bool claro, double escala, int largura)
    {
        string onde = $"{(claro ? "claro" : "escuro")} {escala * 100:0}% trocar o tema {largura}px";
        using var janela = NovaJanela(largura, 700, out var tela);
        var versoes = Conteudo("real", out var instalada, out var vistas);
        tela.Mostrar(versoes, instalada, vistas);
        Bombear();
        try
        {
            ThemeSwitch.Set(janela, !claro); // o mesmo caminho do app (MainForm.Tema): paleta nova, controles recoloridos, tudo por baixo da transição
            Bombear(25);
            Exige(Theme.IsLight == !claro, $"{onde}: o tema não trocou");
            Exige(tela.BackColor == Theme.Surface, $"{onde}: o fundo da página ficou {tela.BackColor} (devia ser {Theme.Surface})");
            Exige(tela.Leitura.BackColor == Theme.Surface, $"{onde}: o fundo da leitura ficou {tela.Leitura.BackColor}");
            Exige(tela.Indice.BackColor == Theme.Surface && tela.Pilulas.BackColor == Theme.Surface, $"{onde}: o fundo do índice ou das pílulas não trocou");
            Exige(tela.Cartao.BackColor == Theme.SurfaceRaised && tela.Faixa.BackColor == Theme.SurfaceRaised, $"{onde}: o fundo do cartão ou da faixa não trocou");
            foreach (var botao in new[] { tela.Cartao.Botao, tela.Faixa.Botao })
                Exige(botao.BackColor == Theme.Neutral && botao.ForeColor == Theme.NeutralText, $"{onde}: o botão \"{botao.Text}\" ficou com {botao.BackColor}/{botao.ForeColor}");
            Exige(tela.ForeColor == Theme.Text, $"{onde}: o texto da página não trocou");
        }
        finally { ThemeSwitch.Set(janela, claro); Bombear(25); }
    }

    static string Nome(Control c) => c.GetType().Name;

    static void Exige(bool ok, string erro) { if (!ok) throw new InvalidOperationException(erro); }
}
