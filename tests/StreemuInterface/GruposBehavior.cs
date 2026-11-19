using Streemu.Net;
using Streemu.Ui;
using Streemu.Util;

/// <summary>
/// --grupos-check [pasta]: a página Grupos, com dados de mentira, sem rede e sem conta. Para cada tema, escala (100 % e 150 %), largura de janela e situação
/// (sem grupo, 1 grupo, 3 grupos de nome longo), confere que nenhum controle visível sai do cartão ou da página, que os cartões não se cobrem e que não
/// nasce rolagem horizontal; com a pasta, salva as fotos. Redesenhada em 2026-11-19 porque o cartão do grupo saía cortado e a página sobrava vazia.
/// A janela vai para fora da tela (ninguém vê nada abrir). A escala 150 % é a escala da interface (Ctrl +), não o DPI do Windows.
/// </summary>
static class GruposBehavior
{
    static readonly string Longo = "Os parças que jogam até de madrugada no fim de semana inteiro";

    static GrupoLinha[] Situacao(string nome) => nome switch
    {
        "vazio" => [],
        "um" => [new("1", "Galera do jogo", "dono", [new("a", "Pessoa A", "dono")])],
        _ =>
        [
            new("1", Longo, "dono", [new("a", "Pessoa A", "dono"), new("b", "Pessoa B", "membro"), new("c", "Um apelido bem comprido para conferir o corte do texto", "membro")]),
            new("2", "Família", "membro", [new("a", "Mãe", "dono"), new("b", "Pessoa A", "membro")]),
            new("3", "Casal", "dono", [new("a", "Pessoa A", "dono"), new("b", "Amor", "membro")]),
        ],
    };

    public static void Run(string? pasta)
    {
        if (pasta != null) Directory.CreateDirectory(pasta);
        int casos = 0;
        var usuario = UiScale.User;
        try
        {
            foreach (bool claro in new[] { false, true })
            foreach (double escala in new[] { 1.0, 1.5 })
            foreach (int largura in new[] { 1280, 760, 520 })
            foreach (var situacao in new[] { "vazio", "um", "tres" })
            {
                Theme.Use(claro); UiScale.User = escala;
                using var janela = new Form
                {
                    StartPosition = FormStartPosition.Manual, FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
                    Bounds = new Rectangle(-6000, 40, largura, 1100), BackColor = Theme.Surface,
                    Font = new Font(UiTypography.Family, UiTypography.Body * (float)escala),
                };
                var lista = new GruposView();
                using var tela = new GruposTela(lista);
                janela.Controls.Add(tela);
                janela.Show();
                UiScale.ScaleFonts(janela, escala); // as fontes próprias dos rótulos (títulos) acompanham a escala, como no app
                lista.Mostrar(Situacao(situacao));
                if (situacao == "vazio") lista.Aviso("Esse convite não parece certo. Cole o código (16 letras e números) ou o link inteiro.", true);
                for (int i = 0; i < 4; i++) { Application.DoEvents(); Thread.Sleep(30); }
                string onde = $"{(claro ? "claro" : "escuro")} {escala * 100:0}% {largura}px {situacao}";
                Confere(lista, tela, onde);
                if (pasta != null && (escala == 1.0 || largura == 760))
                    Foto(janela, Path.Combine(pasta, $"grupos-{(claro ? "claro" : "escuro")}-{escala * 100:0}-{largura}-{situacao}.png"));
                casos++;
            }
        }
        finally { UiScale.User = usuario; }
        Console.WriteLine($"grupos-check OK: {casos} combinações" + (pasta != null ? $"; fotos em {pasta}" : ""));
    }

    static void Foto(Form janela, string arquivo)
    {
        using var imagem = new Bitmap(janela.Width, janela.Height);
        janela.DrawToBitmap(imagem, new Rectangle(0, 0, janela.Width, janela.Height));
        imagem.Save(arquivo);
    }

    static void Confere(GruposView lista, GruposTela tela, string onde)
    {
        var rolagem = tela.Controls.OfType<Panel>().First(p => p.AutoScroll);
        Exige(!rolagem.HorizontalScroll.Visible, $"{onde}: apareceu rolagem horizontal");
        // cada controle visível cabe no pai (rótulos com reticências podem ser mais estreitos que o texto, nunca mais largos que o pai)
        void Desce(Control pai)
        {
            foreach (Control c in pai.Controls)
            {
                if (!c.Visible || pai is not (GruposView or CartaoDeAjuste or Panel)) { Desce(c); continue; }
                var area = pai is CartaoDeAjuste ? pai.DisplayRectangle : pai.ClientRectangle;
                Exige(c.Left >= area.Left - (c is Label ? 6 : 0) && c.Right <= area.Right + 1, $"{onde}: {Nome(c)} {c.Bounds} sai da largura de {area}");
                if (c is Button b) Exige(TextRenderer.MeasureText(b.Text, b.Font, Size.Empty, TextFormatFlags.NoPadding).Width + b.Padding.Horizontal <= b.Width + 2, $"{onde}: o botão \"{b.Text}\" corta o texto ({b.Width})");
                Desce(c);
            }
        }
        Desce(lista);
        // cartões da página não se cobrem
        var cartoes = lista.Controls.OfType<CartaoDeAjuste>().Where(c => c.Visible).ToList();
        for (int i = 0; i < cartoes.Count; i++)
            for (int j = i + 1; j < cartoes.Count; j++)
                Exige(!cartoes[i].Bounds.IntersectsWith(cartoes[j].Bounds), $"{onde}: cartões {cartoes[i].Bounds} e {cartoes[j].Bounds} se cobrem");
        Exige(cartoes.All(c => c.Bottom <= lista.Height), $"{onde}: cartão passa da altura da página");
        // o miolo termina antes da margem de baixo do cartão (senão ele cobre a borda de baixo)
        foreach (var c in cartoes)
            Exige(c.Controls[0].Bottom + c.Padding.Bottom <= c.Height, $"{onde}: o miolo ({c.Controls[0].Bounds}) cobre a borda de baixo do cartão (altura {c.Height}, margem {c.Padding.Bottom})");
    }

    static string Nome(Control c) => c is Button or Label ? $"\"{c.Text}\"" : c.GetType().Name;

    static void Exige(bool ok, string erro) { if (!ok) throw new InvalidOperationException(erro); }
}
