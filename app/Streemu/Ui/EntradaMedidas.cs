namespace Streemu.Ui;

/// <summary>
/// Onde fica cada peça da tela de entrada. Os números vêm do desenho (área útil de 1920 × 1036 a 100 %) e passam por <c>dp</c>, que aplica DPI e escala.
/// Janela estreita aproxima a Mumu do cartão; mais estreita ainda, a Mumu some e o cartão fica no meio.
/// </summary>
internal sealed class EntradaMedidas
{
    const int CabecalhoAltura = 93, RodapeAltura = 58, CartaoLargura = 582, CartaoMargem = 47, CartaoRaio = 20;
    const int GrupoLargura = 1182, GrupoDeslocamento = 46, GrupoFolga = 24, MascoteColuna = 185, MascoteAltura = 556, ColunaAltura = 681;
    const int ArteLargura = 960, ArteAltura = 1476, LarguraComMascote = 1040;

    readonly Func<int, int> _d;
    public Size Area { get; }
    /// <summary>O campo do código só existe depois do primeiro clique em "Entrar no Streemu".</summary>
    public bool ComCodigo { get; }
    public int LinhaCabecalho { get; }
    public int LinhaRodape { get; }
    public int CentroY { get; }
    public bool TemMascote { get; }
    public int RaioCartao => _d(CartaoRaio);
    public int RaioCampo => _d(15);
    public int RaioBotao => _d(12);
    public Rectangle Cartao { get; private set; }
    public Rectangle Titulo { get; private set; }
    public Rectangle Rotulo { get; private set; }
    public Rectangle Campo { get; private set; }
    public Rectangle Botao { get; private set; }
    public Rectangle Dica { get; private set; }
    public Rectangle Conta { get; private set; }
    public Rectangle Endereco { get; private set; }
    public Rectangle Mascote { get; private set; }
    public Rectangle Heroi { get; private set; }
    public Rectangle Brilho { get; private set; }
    public Rectangle Marca { get; }
    public int CabecalhoCentroY { get; }
    public int RodapeCentroY { get; }

    public EntradaMedidas(Size area, Func<int, int> dp, bool comCodigo)
    {
        _d = dp; Area = area; ComCodigo = comCodigo;
        LinhaCabecalho = dp(CabecalhoAltura);
        LinhaRodape = area.Height - dp(RodapeAltura);
        CentroY = (LinhaCabecalho + LinhaRodape) / 2;
        TemMascote = area.Width >= dp(LarguraComMascote);
        CabecalhoCentroY = dp(48);
        RodapeCentroY = LinhaRodape + dp(30);
        Marca = new Rectangle(dp(86), CabecalhoCentroY - dp(20), dp(40), dp(40));
        var (cartaoX, mascoteX) = ColunasX();
        PosicionarCartao(cartaoX);
        if (TemMascote) PosicionarMascote(mascoteX);
        int brilhoX = TemMascote ? mascoteX + dp(270) : Cartao.X + Cartao.Width / 2;
        Brilho = new Rectangle(brilhoX - dp(640), CentroY + dp(12) - dp(470), dp(1280), dp(940));
    }

    /// <summary>Menor escala da tela de entrada: abaixo disso o texto fica pequeno demais para ler, e o que não couber fica cortado.</summary>
    public const double EncolhimentoMinimo = 0.6;

    /// <summary>
    /// Quanto encolher a tela inteira (medidas e fontes) para caber na janela, de <see cref="EncolhimentoMinimo"/> a 1. O desenho é de 1920 × 1036 e as
    /// medidas eram fixas: numa janela baixa o cartão passava por cima do rodapé (visto em 2026-11-18 numa janela de 1212 × 810 a 150 %).
    /// A altura precisa caber cabeçalho, rodapé e o cartão com folga; a largura, o cartão sem a Mumu e o cabeçalho com a marca e a frase.
    /// </summary>
    public static double Encolhimento(Size area, double escala, bool comCodigo)
    {
        if (area.Width <= 0 || area.Height <= 0 || escala <= 0) return 1;
        double altura = CabecalhoAltura + RodapeAltura + AlturaDoCartao(comCodigo) + 2 * 24, largura = 760;
        double cabe = Math.Min(area.Height / (altura * escala), area.Width / (largura * escala));
        return Math.Clamp(cabe, EncolhimentoMinimo, 1);
    }

    static int AlturaDoCartao(bool comCodigo) => comCodigo ? 470 : 350;

    public int MargemDireita => _d(84);
    public int MargemRodape => _d(92);
    public int MargemEsquerda => _d(88);
    public int TextoMarcaX => _d(144);

    (int CartaoX, int MascoteX) ColunasX()
    {
        int w = Area.Width, cartaoW = Math.Min(_d(CartaoLargura), w - 2 * _d(16));
        if (!TemMascote) return ((w - cartaoW) / 2, 0);
        int grupo = _d(GrupoLargura), folga = w - grupo - 2 * _d(GrupoFolga);
        int encolhe = folga < 0 ? Math.Min(-folga, _d(190)) : 0;
        grupo -= encolhe;
        int esquerda = (w - grupo) / 2 + (encolhe == 0 ? _d(GrupoDeslocamento) : 0);
        esquerda = Math.Clamp(esquerda, _d(GrupoFolga), Math.Max(_d(GrupoFolga), w - grupo - _d(GrupoFolga)));
        return (esquerda + grupo - cartaoW, esquerda + _d(MascoteColuna));
    }

    /// <summary>Sem o campo, o cartão só tem o título e o botão; com ele, entram o rótulo e o campo antes do botão. O cartão sempre fica centrado na região.</summary>
    void PosicionarCartao(int x)
    {
        int largura = Math.Min(_d(CartaoLargura), Area.Width - 2 * _d(16));
        int altura = _d(AlturaDoCartao(ComCodigo));
        int y = Math.Max(LinhaCabecalho + _d(16), CentroY - altura / 2 - _d(6));
        Cartao = new Rectangle(x, y, largura, altura);
        int dentro = x + _d(CartaoMargem), interno = largura - 2 * _d(CartaoMargem);
        Titulo = Faixa(dentro, y, 70, 44, interno);
        int botao = ComCodigo ? 261 : 140;
        if (ComCodigo) { Rotulo = Faixa(dentro, y, 140, 24, interno); Campo = Faixa(dentro, y, 174, 69, interno); }
        Botao = Faixa(dentro, y, botao, 63, interno);
        Dica = Faixa(dentro, y, botao + 73, 38, interno);
        Conta = Faixa(dentro, y, botao + 115, 24, interno);
        Endereco = Faixa(dentro, y, botao + 153, 34, interno);
    }

    Rectangle Faixa(int x, int cartaoY, int topo, int altura, int largura) => new(x, cartaoY + _d(topo), largura, _d(altura));

    /// <summary>Espaço sob a Mumu para a frase, em até duas linhas da fonte LoginHero.</summary>
    const int FraseReserva = 110;

    void PosicionarMascote(int centroX)
    {
        int regiao = LinhaRodape - LinhaCabecalho;
        double fator = Math.Clamp((regiao - _d(64) - _d(FraseReserva)) / (double)_d(MascoteAltura), 0.4, 1); // encolhe a Mumu quando a janela é baixa
        int altura = (int)Math.Round(_d(MascoteAltura) * fator), largura = altura * ArteLargura / ArteAltura;
        int topo = Math.Max(LinhaCabecalho + _d(24), CentroY - _d(20) - (altura + _d(FraseReserva)) / 2);
        Mascote = new Rectangle(centroX - largura / 2, topo, largura, altura);
        // centrada na Mumu, mas sem passar da borda da janela nem entrar no cartão (a 1366 × 705 a frase saía cortada à esquerda)
        int caixa = Math.Min(_d(520), 2 * Math.Max(0, Math.Min(centroX - _d(16), Cartao.X - _d(16) - centroX)));
        Heroi = new Rectangle(centroX - caixa / 2, topo + altura + _d(16), caixa, _d(FraseReserva - 16)); // cabe em duas linhas (EntradaDesenho quebra a frase)
    }
}
