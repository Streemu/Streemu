namespace Streemu.Ui;

/// <summary>Estilo de texto da tela Apoiar: o recado em Normal, o botão em Semibold, e a assinatura à mão.</summary>
public enum ApoiarEstilo { Normal, Semibold, Assinatura }

/// <summary>De que lado do balão sai o bico: à esquerda (arranjo largo, aponta para a arte) ou em cima (arranjo estreito).</summary>
public enum ApoiarBico { Esquerda, Cima }

/// <summary>Uma linha de texto já quebrada e a área em que ela é desenhada.</summary>
public readonly record struct ApoiarLinha(string Texto, Rectangle Area);

/// <summary>
/// Onde fica cada peça da tela Apoiar para uma área, e a unidade <see cref="U"/> em que tudo foi medido. Só retângulos: quem desenha é a <see cref="ApoiarTela"/>.
/// </summary>
public sealed class ApoiarGeometria
{
    /// <summary>A unidade da composição, em pixels de tela: o tamanho do texto do recado (plano 035, D5).</summary>
    public float U { get; init; }
    /// <summary>Arranjo largo (arte à esquerda, balão à direita) ou estreito (arte em cima).</summary>
    public bool Largo { get; init; }
    /// <summary>Falso só quando nem a menor unidade fez a composição caber (nunca nos tamanhos que o app permite).</summary>
    public bool Cabe { get; init; }
    public Rectangle Arte { get; init; }
    public Rectangle Balao { get; init; }
    /// <summary>A caixa do triângulo do bico, que sai do balão para <see cref="LadoDoBico"/>.</summary>
    public Rectangle Bico { get; init; }
    public ApoiarBico LadoDoBico { get; init; }
    public IReadOnlyList<ApoiarLinha> Recado { get; init; } = [];
    /// <summary>O coração pequeno logo depois do ponto final do recado.</summary>
    public Rectangle Coracao { get; init; }
    public Rectangle Botao { get; init; }
    public ApoiarLinha Assinatura { get; init; }
    /// <summary>O aviso de falha ao abrir o navegador (vazio quando não há falha) e o "Copiar link" dele.</summary>
    public IReadOnlyList<ApoiarLinha> Aviso { get; init; } = [];
    public Rectangle AvisoBotao { get; init; }
    /// <summary>Tamanhos de fonte em pixels, todos múltiplos de <see cref="U"/>.</summary>
    public float TamRecado => 1.05f * U;
    public float TamBotao => 1.02f * U;
    public float TamAssinatura => 1.05f * U;
    public float TamAviso => .88f * U;
    /// <summary>Tudo o que a composição ocupa (arte e balão com o bico), para centrar e para o teste conferir que cabe.</summary>
    public Rectangle Composicao { get; init; }
}

/// <summary>
/// A conta da tela Apoiar (plano 035, D4 a D6): uma função pura que, de uma área e do medidor de texto, devolve os retângulos de tudo. Fica separada do desenho
/// para o teste (<c>--apoiar-check</c>) conferir muitos tamanhos e escalas sem abrir janela. Medidas em múltiplos de <c>u</c>, tiradas do mockup aprovado.
/// </summary>
public static class ApoiarLayout
{
    public const string Recado = "Oi! O Streemu é feito por dois amigos, nas horas livres, e não tem anúncio. Se ele deixou suas jogatinas e sessões de filme melhores, você pode dar uma força para mantê-lo no ar.";
    public const string TextoBotao = "Apoiar no site";
    public const string Assinatura = "— Mumu e Streemu";
    public const string TextoCopiar = "Copiar link";
    public const string EnderecoCurto = "streemu.com.br/apoie";
    public const string TextoAviso = "Não deu para abrir o navegador. Copie o endereço: " + EnderecoCurto;
    /// <summary>Medida de <c>Arte/mumu-apoio.png</c>.</summary>
    public const int ArteLargura = 1200, ArteAltura = 1024;
    /// <summary>Do botão: folga de cada lado, ícone e espaço entre as peças, em múltiplos de u (os mesmos que o <see cref="BotaoPilula"/> usa para desenhar).</summary>
    public const float BotaoFolga = 1.45f, BotaoIcone = 1.15f, BotaoEspaco = .6f, BotaoAltura = 2.85f, CopiarAltura = 2.2f, CopiarFolga = 1f;

    /// <summary>
    /// Calcula a disposição para <paramref name="area"/> (a área de conteúdo da janela, em pixels). <paramref name="dp"/> é a escala de tela (<c>Dp(1)</c>: DPI do monitor e escala do
    /// usuário); <paramref name="medir"/> devolve o tamanho de um texto de UMA linha (texto, tamanho da fonte em pixels, estilo). Com <paramref name="aviso"/>, abre espaço
    /// para o aviso de falha embaixo do botão. A unidade desce de 1 em 1 px até a composição caber.
    /// </summary>
    /// <example><code>var g = ApoiarLayout.Calcular(new Size(1686, 1006), 1f, ApoiarTela.Medir); // g.U ≈ 22,3</code></example>
    public static ApoiarGeometria Calcular(Size area, float dp, Func<string, float, ApoiarEstilo, Size> medir, bool aviso = false)
    {
        float w = Math.Max(1, area.Width), h = Math.Max(1, area.Height);
        float minimo = 12 * dp, u0 = Math.Clamp(Math.Min(.0132f * w, .0235f * h), 14 * dp, 36 * dp);
        for (float u = u0; ; u -= 1)
        {
            u = Math.Max(u, minimo);
            var g = Montar(area, dp, u, medir, aviso);
            if (g.Cabe || u <= minimo) return g;
        }
    }

    static ApoiarGeometria Montar(Size area, float dp, float u, Func<string, float, ApoiarEstilo, Size> medir, bool aviso)
    {
        float w = area.Width, h = area.Height, pad = 1.7f * u, gap = 1.3f * u, folgaX = 1.6f * u;
        bool largo = w >= 560 * dp;
        float tRec = 1.05f * u, tBot = 1.02f * u, tAss = 1.05f * u, tAv = .88f * u;
        float quadro = Math.Max(1, Math.Min(50 * u, w - 2 * pad));
        float arteW = largo ? Math.Min(19 * u, Math.Min(.42f * w, .62f * h * ArteLargura / ArteAltura)) : 9 * u;
        float arteH = arteW * ArteAltura / ArteLargura;
        float balaoW = largo ? quadro - arteW - gap : quadro, interno = balaoW - 2 * folgaX;
        bool cabe = interno > 4 * u;

        // tudo abaixo em coordenadas do balão (origem no canto de cima, à esquerda)
        float y = 1.4f * u;

        // o recado: quebra por palavra, uma linha por vez (o TextRenderer não tem entrelinha), com o coração como última "palavra"
        float entrelinha = 1.6f * tRec, coracao = .95f * tRec, folgaCoracao = .3f * u;
        var linhas = new List<string>();
        string atual = "";
        foreach (var palavra in Recado.Split(' '))
        {
            string candidata = atual.Length == 0 ? palavra : atual + " " + palavra;
            if (atual.Length > 0 && medir(candidata, tRec, ApoiarEstilo.Normal).Width > interno) { linhas.Add(atual); atual = palavra; }
            else atual = candidata;
        }
        linhas.Add(atual);
        if (medir(atual, tRec, ApoiarEstilo.Normal).Width + folgaCoracao + coracao > interno) linhas.Add(""); // sem lugar na última linha: o coração desce
        var recado = new List<ApoiarLinha>();
        for (int i = 0; i < linhas.Count; i++)
        {
            var m = medir(linhas[i], tRec, ApoiarEstilo.Normal);
            if (m.Width > interno + .5f) cabe = false;
            recado.Add(new ApoiarLinha(linhas[i], Ret(folgaX, y + i * entrelinha, m.Width, entrelinha)));
        }
        var ultima = recado[^1].Area;
        var rCoracao = Ret(ultima.Right + (linhas[^1].Length == 0 ? 0 : folgaCoracao), ultima.Y + (entrelinha - coracao) / 2, coracao, coracao);
        y += linhas.Count * entrelinha + .2f * u;

        // formato de carta: a assinatura numa linha própria, à direita, e o botão embaixo, à esquerda
        var ass = medir(Assinatura, tAss, ApoiarEstilo.Assinatura);
        if (ass.Width > interno) cabe = false;
        var assinatura = new ApoiarLinha(Assinatura, Ret(folgaX + interno - ass.Width, y, ass.Width, ass.Height));
        y += ass.Height + .9f * u;
        float textoBotao = medir(TextoBotao, tBot, ApoiarEstilo.Semibold).Width;
        float botaoW = 2 * BotaoFolga * u + BotaoIcone * u + BotaoEspaco * u + textoBotao + BotaoEspaco * u + BotaoIcone * u, botaoH = BotaoAltura * u;
        if (botaoW > interno) cabe = false;
        var rBotao = Ret(folgaX, y, botaoW, botaoH);
        y += botaoH;

        var linhasAviso = new List<ApoiarLinha>();
        var rCopiar = Rectangle.Empty;
        if (aviso)
        {
            y += .75f * u;
            float passo = 1.4f * tAv;
            string a = "";
            var textos = new List<string>();
            foreach (var palavra in TextoAviso.Split(' '))
            {
                string c = a.Length == 0 ? palavra : a + " " + palavra;
                if (a.Length > 0 && medir(c, tAv, ApoiarEstilo.Normal).Width > interno) { textos.Add(a); a = palavra; } else a = c;
            }
            textos.Add(a);
            foreach (var t in textos)
            {
                var m = medir(t, tAv, ApoiarEstilo.Normal);
                if (m.Width > interno + .5f) cabe = false;
                float altura = Math.Max(passo, m.Height); // a caixa nunca é menor que a fonte (a 1,4 x o tamanho, uma fonte alta ficaria cortada)
                linhasAviso.Add(new ApoiarLinha(t, Ret(folgaX, y, m.Width, altura)));
                y += altura;
            }
            y += .5f * u;
            float copiarW = medir(TextoCopiar, tAv, ApoiarEstilo.Semibold).Width + 2 * CopiarFolga * u;
            rCopiar = Ret(folgaX, y, copiarW, CopiarAltura * u);
            y += CopiarAltura * u;
        }
        float balaoH = y + 1.3f * u;

        float margemBaixo = 3.2f * u, quadroH = largo ? Math.Max(arteH, balaoH + margemBaixo) : arteH + balaoH;
        if (quadroH + 1.6f * u > h) cabe = false;
        float x0 = (w - quadro) / 2, y0 = (h - quadroH) / 2;
        float bx, by, ax, ay = largo ? y0 + quadroH - arteH : y0;
        ax = x0;
        if (largo) { bx = x0 + arteW + gap; by = y0 + quadroH - margemBaixo - balaoH; }
        else { bx = x0; by = (float)(Math.Round(y0) + Math.Round(arteH)); } // soma dos arredondados: a arte e o balão se encostam sem sobrepor 1 px
        Rectangle Mover(Rectangle r) => new(r.X + (int)Math.Round(bx), r.Y + (int)Math.Round(by), r.Width, r.Height);
        ApoiarLinha Mover1(ApoiarLinha l) => new(l.Texto, Mover(l.Area));

        var balao = Ret(bx, by, balaoW, balaoH);
        Rectangle bico;
        ApoiarBico lado;
        if (largo)
        {
            float cy = by + balaoH - 1.9f * u - .575f * u;
            var b = Ret(0, cy - .8f * u, .8f * u, 1.6f * u);
            bico = new Rectangle(balao.Left - b.Width, b.Y, b.Width, b.Height); lado = ApoiarBico.Esquerda; // encostado no balão, sem sobra de arredondamento
        }
        else
        {
            float cx = bx + 3f * u + .575f * u;
            var b = Ret(cx - .8f * u, 0, 1.6f * u, .8f * u);
            bico = new Rectangle(b.X, balao.Top - b.Height, b.Width, b.Height); lado = ApoiarBico.Cima;
        }
        var arte = Ret(ax, ay, arteW, arteH);
        var composicao = Rectangle.Union(Rectangle.Union(arte, balao), bico);
        return new ApoiarGeometria
        {
            U = u, Largo = largo, Cabe = cabe, Arte = arte, Balao = balao, Bico = bico, LadoDoBico = lado, Composicao = composicao,
            Recado = recado.Select(Mover1).ToList(), Coracao = Mover(rCoracao),
            Botao = Mover(rBotao), Assinatura = Mover1(assinatura), Aviso = linhasAviso.Select(Mover1).ToList(), AvisoBotao = rCopiar.IsEmpty ? Rectangle.Empty : Mover(rCopiar),
        };
    }

    static Rectangle Ret(float x, float y, float largura, float altura) =>
        new((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(largura), (int)Math.Round(altura));
}
