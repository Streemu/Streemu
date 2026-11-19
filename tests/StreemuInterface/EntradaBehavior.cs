using System.Reflection;
using Streemu.Ui;

/// <summary>
/// --entrada-check [pasta]: a tela de entrada da conta cabe na janela em vários tamanhos, antes e depois do primeiro clique (com o campo do código).
/// Em 2026-11-18 o cartão passava por cima do rodapé numa janela de 1212 × 810 a 150 %. Sem rede, sem conta e sem abrir janela.
/// </summary>
static class EntradaBehavior
{
    const BindingFlags Oculto = BindingFlags.NonPublic | BindingFlags.Instance;

    public static void Run(string? saida)
    {
        if (saida != null) Directory.CreateDirectory(saida);
        using var tela = new EntradaView();
        tela.CreateControl();
        int casos = 0;
        foreach (var tamanho in new[] { new Size(1920, 1036), new Size(1366, 705), new Size(1212, 810), new Size(1000, 600), new Size(800, 560), new Size(640, 480) })
        {
            foreach (int passo in new[] { 1, 2 })
            {
                tela.Size = tamanho;
                tela.Passo(passo);
                tela.GetType().GetMethod("Recalcular", Oculto)!.Invoke(tela, null); // escondida, a tela só marca; aqui força
                var m = tela.GetType().GetField("_medidas", Oculto)!.GetValue(tela)!;
                Rectangle R(string nome) => (Rectangle)m.GetType().GetProperty(nome)!.GetValue(m)!;
                int I(string nome) => (int)m.GetType().GetProperty(nome)!.GetValue(m)!;
                var cartao = R("Cartao");
                string onde = $"{tamanho.Width}x{tamanho.Height} passo {passo}";
                // O mínimo de encolhimento é 60 %: numa janela menor do que ele cobre (depende do DPI e da escala deste PC), cortar é aceito
                var tipo = m.GetType();
                double escalaBase = tela.DeviceDpi / 96.0 * Streemu.Util.UiScale.Factor(tela);
                double encolhe = (double)tipo.GetMethod("Encolhimento")!.Invoke(null, new object[] { tamanho, escalaBase, passo == 2 })!;
                if (encolhe <= (double)tipo.GetField("EncolhimentoMinimo")!.GetValue(null)!) { Console.WriteLine($"  {onde}: no mínimo de 60 % (pode cortar)"); continue; }
                Exige(cartao.Top >= I("LinhaCabecalho") && cartao.Bottom <= I("LinhaRodape"), $"{onde}: cartão {cartao} fora da faixa entre o cabeçalho ({I("LinhaCabecalho")}) e o rodapé ({I("LinhaRodape")})");
                Exige(cartao.Left >= 0 && cartao.Right <= tamanho.Width, $"{onde}: cartão passa da largura");
                foreach (var nome in passo == 2 ? new[] { "Titulo", "Campo", "Botao", "Conta" } : new[] { "Titulo", "Botao", "Conta" })
                    Exige(cartao.Contains(R(nome)), $"{onde}: {nome} {R(nome)} fora do cartão {cartao}");
                if ((bool)tipo.GetProperty("TemMascote")!.GetValue(m)!)
                {
                    var heroi = R("Heroi");
                    var fonteHeroi = (Font)fontesDe(tela).GetType().GetProperty("Heroi")!.GetValue(fontesDe(tela))!;
                    var medida = TextRenderer.MeasureText("Chega aí! Junte-se à galera!", fonteHeroi, new Size(heroi.Width, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
                    Exige(heroi.Left >= 0 && heroi.Right <= cartao.Left, $"{onde}: a frase sob a Mumu ({heroi}) sai da janela ou entra no cartão");
                    Exige(medida.Width <= heroi.Width && medida.Height <= heroi.Height && heroi.Bottom <= I("LinhaRodape"), $"{onde}: a frase sob a Mumu ({medida}) não cabe na caixa {heroi}");
                }
                var entrar = (Control)tela.GetType().GetField("_entrar", Oculto)!.GetValue(tela)!;
                Exige(cartao.Contains(entrar.Bounds), $"{onde}: o botão Entrar ({entrar.Bounds}) saiu do cartão");
                var titulo = R("Titulo");
                var fontes = tela.GetType().GetField("_fontes", Oculto)!.GetValue(tela)!;
                var fonteTitulo = (Font)fontes.GetType().GetProperty("Titulo")!.GetValue(fontes)!;
                int larguraTitulo = TextRenderer.MeasureText("Boas-vindas ao Streemu", fonteTitulo).Width;
                Exige(larguraTitulo <= titulo.Width + 8 && fonteTitulo.Height <= titulo.Height + 8, $"{onde}: o título ({larguraTitulo}×{fonteTitulo.Height}) não cabe na faixa {titulo.Size}");
                if (saida != null)
                {
                    using var imagem = new Bitmap(tela.Width, tela.Height);
                    tela.DrawToBitmap(imagem, tela.ClientRectangle);
                    imagem.Save(Path.Combine(saida, $"entrada-{tamanho.Width}x{tamanho.Height}-passo{passo}.png"));
                }
                casos++;
            }
        }
        Console.WriteLine($"OK: tela de entrada cabe na janela em {casos} combinações de tamanho e passo (DPI {tela.DeviceDpi}). Sem rede, conta nem janela.");
    }

    static object fontesDe(EntradaView tela) => tela.GetType().GetField("_fontes", Oculto)!.GetValue(tela)!;

    static void Exige(bool ok, string erro) { if (!ok) throw new Exception(erro); }
}
