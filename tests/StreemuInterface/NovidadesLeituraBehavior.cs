using Streemu.Ui;
using Streemu.Util;

/// <summary>
/// --novidades-leitura-check [pasta]: a leitura desenhada da tela Novidades (plano 027, etapa 2), sozinha numa janela fora da tela. Para cada tema, escala da
/// interface (100 % e 150 %), largura do controle (320, 480 e 640 dp) e conteúdo (as novidades reais, uma versão com título de 70 letras sem espaço, lista
/// vazia) confere: sem rolagem horizontal, todo retângulo pintado dentro da largura e cartões que não se cobrem, rolagem com as novidades reais,
/// IrPara, a versão do topo que continua a mesma ao estreitar, e a Mumu no estado vazio. Com a pasta salva as fotos.
/// </summary>
static class NovidadesLeituraBehavior
{
    static IReadOnlyList<VersaoNovidade> Conteudo(string nome, out string instalada, out HashSet<string>? vistas)
    {
        vistas = null; instalada = "";
        switch (nome)
        {
            case "real":
                var reais = Novidades.Embutidas();
                instalada = reais.First(v => !v.NaoLancado).Versao;
                vistas = reais.SelectMany(v => v.Itens).Select(i => i.Chave).Where((_, n) => n % 2 == 0).ToHashSet();
                return reais;
            case "longo":
                instalada = "9.9.9";
                string detalhe = string.Join(' ', Enumerable.Repeat("detalhe muito longo que precisa quebrar palavra por palavra sem passar da largura", 4)) + " " + new string('m', 90);
                return
                [
                    new(string.Empty, null, "O que já está no app mas ainda não tem número.", false, true, [new("Novo", "Algo que ainda não saiu numa versão", "Um detalhe simples.", "k0")]),
                    new("9.9.9", new DateOnly(2026, 11, 20), "Um resumo que também é comprido o bastante para quebrar em mais de uma linha no cartão estreito.", true,
                        false, [new("Novo", new string('W', 70), detalhe, "k1"), new("Correções", "Curto", "", "k2")]),
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
            foreach (double escala in new[] { 1.0, 1.5 })
            foreach (int dp in new[] { 320, 480, 640 })
            foreach (var conteudo in new[] { "real", "longo", "vazio" })
            {
                Theme.Use(claro); UiScale.User = escala;
                using var janela = new Form { StartPosition = FormStartPosition.Manual, FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, BackColor = Theme.Surface, ForeColor = Theme.Text };
                using var leitura = new NovidadesLeitura { Dock = DockStyle.Fill };
                janela.Controls.Add(leitura);
                janela.Bounds = new Rectangle(-6000, 40, UiScale.Dp(janela, dp), 760);
                janela.Show();
                Bombear();
                var versoes = Conteudo(conteudo, out var instalada, out var vistas);
                leitura.Mostrar(versoes, instalada, vistas);
                Bombear();
                string onde = $"{(claro ? "claro" : "escuro")} {escala * 100:0}% {dp}dp {conteudo}";
                Confere(leitura, versoes, onde);
                if (conteudo == "real") ConfereRolagem(leitura, janela, dp, onde);
                if (pasta != null && (escala == 1.0 || dp == 480))
                {
                    leitura.IrPara(0);
                    Bombear();
                    Foto(janela, Path.Combine(pasta, $"novidades-{(claro ? "claro" : "escuro")}-{escala * 100:0}-{dp}-{conteudo}.png"));
                }
                casos++;
            }
        }
        finally { UiScale.User = usuario; Theme.Use(claroAntes); }
        Console.WriteLine($"novidades-leitura-check OK: {casos} combinações" + (pasta != null ? $"; fotos em {pasta}" : ""));
    }

    static void Bombear() { for (int i = 0; i < 4; i++) { Application.DoEvents(); Thread.Sleep(30); } }

    static void Confere(NovidadesLeitura leitura, IReadOnlyList<VersaoNovidade> versoes, string onde)
    {
        Exige(!leitura.HorizontalScroll.Visible && leitura.AutoScrollMinSize.Width == 0, $"{onde}: apareceu rolagem horizontal");
        int util = leitura.Width - SystemInformation.GetVerticalScrollBarWidthForDpi(leitura.DeviceDpi);
        var cartoes = leitura.RetangulosDosCartoes();
        Exige(cartoes.Count == versoes.Count, $"{onde}: {cartoes.Count} cartões para {versoes.Count} versões");
        var todos = leitura.RetangulosPintados();
        foreach (var r in todos)
            Exige(r.Left >= 0 && r.Right <= util && r.Width > 0 && r.Height > 0, $"{onde}: o retângulo {r} sai da largura útil ({util})");
        for (int i = 0; i + 1 < cartoes.Count; i++)
            Exige(!cartoes[i].IntersectsWith(cartoes[i + 1]) && cartoes[i].Bottom <= cartoes[i + 1].Top, $"{onde}: cartões {cartoes[i]} e {cartoes[i + 1]} se cobrem");
        foreach (var r in todos.Where(r => !cartoes.Contains(r)))
            Exige(cartoes.Any(c => c.Contains(r)), $"{onde}: o bloco {r} sai do cartão dele");
        if (versoes.Count == 0) Exige(leitura.MumuVisivel, $"{onde}: a Mumu do estado vazio não aparece");
        else Exige(!leitura.MumuVisivel, $"{onde}: a Mumu aparece com conteúdo");
    }

    static void ConfereRolagem(NovidadesLeitura leitura, Form janela, int dp, string onde)
    {
        Exige(leitura.AutoScrollMinSize.Height > leitura.ClientSize.Height, $"{onde}: o conteúdo real cabe sem rolar ({leitura.AutoScrollMinSize.Height} em {leitura.ClientSize.Height})");
        int ultimo = -1;
        leitura.VersaoNoTopoMudou += i => ultimo = i;
        leitura.IrPara(5);
        Bombear();
        Exige(leitura.VersaoNoTopo == 5 && ultimo == 5, $"{onde}: IrPara(5) deixou a versão {leitura.VersaoNoTopo} no topo (evento {ultimo})");
        if (dp == 640)
        {
            janela.Width = UiScale.Dp(janela, 320);
            Bombear();
            Exige(leitura.VersaoNoTopo == 5, $"{onde}: estreitar de 640 para 320 dp trocou a versão do topo para {leitura.VersaoNoTopo}");
            Exige(!leitura.HorizontalScroll.Visible && leitura.AutoScrollMinSize.Width == 0, $"{onde}: rolagem horizontal depois de estreitar");
            janela.Width = UiScale.Dp(janela, 640);
            Bombear();
            Exige(leitura.VersaoNoTopo == 5, $"{onde}: alargar de volta trocou a versão do topo para {leitura.VersaoNoTopo}");
        }
        var v = leitura.Versoes[5];
        Exige(NovidadesLeitura.TextoDaVersao(v).Contains(v.Versao), $"{onde}: TextoDaVersao sem o número");
        Exige(leitura.AccessibilityObject.GetChildCount() == leitura.Versoes.Count, $"{onde}: filhos de acessibilidade diferentes das versões");
    }

    static void Foto(Form janela, string arquivo)
    {
        using var imagem = new Bitmap(janela.Width, janela.Height);
        janela.DrawToBitmap(imagem, new Rectangle(0, 0, janela.Width, janela.Height));
        imagem.Save(arquivo);
    }

    static void Exige(bool ok, string erro) { if (!ok) throw new InvalidOperationException(erro); }
}
