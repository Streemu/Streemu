using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// As peças das páginas de Configurações repaginadas (2026-11-18): legenda, subtítulo de campo, nota de apoio, linhas de botões e o cartão que
/// agrupa um assunto. Só monta controles que o MainForm já tem; não guarda estado.
/// Uso: <c>AjusteBlocos.Cartao(AjusteBlocos.Legenda("Sinal"), toggle, AjusteBlocos.Nota("Explica o toggle."))</c>.
/// </summary>
public static class AjusteBlocos
{
    /// <summary>Título de um cartão ("Discord", "Qualidade").</summary>
    public static Label Legenda(string texto) =>
        new() { Text = texto, AutoSize = true, Font = UiTypography.Create(UiTypography.Section, FontStyle.Bold), Margin = new Padding(0, 0, 0, 6) };

    /// <summary>Nome pequeno de um campo dentro de um cartão ("Áudio", "Encoder").</summary>
    public static Label Subtitulo(string texto) =>
        new() { Text = texto, AutoSize = true, Font = UiTypography.Create(UiTypography.Caption, FontStyle.Bold), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 8, 0, 4) };

    /// <summary>Texto de apoio embaixo de um campo ou interruptor; alinhado ao texto do interruptor quando for o caso.</summary>
    public static Label Nota(string texto, bool alinhadaAoInterruptor = true)
    {
        var nota = new Label { Text = texto, AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 14) };
        if (!alinhadaAoInterruptor) return nota;
        // o texto do interruptor começa a UiMetrics.ToggleText dp da borda, na escala do monitor (pixels fixos desalinhavam a 150%)
        void Alinhar() => nota.Padding = new Padding(UiScale.Dp(nota, UiMetrics.ToggleText), 0, 0, 0);
        nota.HandleCreated += (_, _) => Alinhar();
        nota.DpiChangedAfterParent += (_, _) => Alinhar();
        return nota;
    }

    /// <summary>
    /// Várias linhas empilhadas, todas começando na MESMA borda esquerda do cartão (botão, título, interruptor e texto), com o mesmo respiro entre elas;
    /// a última perde a margem de baixo (a margem do cartão já dá o respiro). Antes cada controle trazia a margem padrão de 3 px e cada rótulo o recuo
    /// interno do texto, e a 150% de DPI a borda esquerda variava até 6 px de uma linha para outra (print de 2026-11-18).
    /// </summary>
    public static TableLayoutPanel Coluna(params Control[] linhas)
    {
        var tabela = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
        tabela.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var linha in linhas) { NaBordaDoCartao(linha); linha.Anchor = AnchorStyles.Left | AnchorStyles.Top; tabela.Controls.Add(linha, 0, tabela.Controls.Count); }
        var ultima = linhas[^1];
        ultima.Margin = new Padding(ultima.Margin.Left, ultima.Margin.Top, ultima.Margin.Right, 0);
        return tabela;
    }

    /// <summary>O recuo que o Windows põe antes do texto de um rótulo, em dp (medido: 3 px a 150%, 2 px a 100%).</summary>
    const int RecuoDoTextoAt96 = 2;

    /// <summary>
    /// A borda esquerda do cartão é onde começa a LETRA dos rótulos (o rótulo em si começa o recuo antes). Botões, interruptores e campos ganham
    /// esse mesmo recuo de margem para começar no mesmo lugar; o TableLayoutPanel ignora margem negativa, então o ajuste é nos que não são rótulo.
    /// </summary>
    static void NaBordaDoCartao(Control controle)
    {
        var m = controle.Margin;
        // margem padrão do WinForms (3,3,3,3) = ninguém definiu: rótulo solto respira embaixo, interruptor e afins um pouco em cima e embaixo
        m = m == new Padding(3) ? (controle is Label ? new Padding(0, 0, 0, 10) : new Padding(0, 2, 0, 6)) : new Padding(0, m.Top, m.Right, m.Bottom);
        controle.Margin = m;
        if (controle is Label || controle is FlowLayoutPanel { Controls.Count: > 0 } linha && linha.Controls[0] is Label) return; // a letra do rótulo já tem o recuo dela
        void Recuar() { var x = controle.Margin; controle.Margin = new Padding((int)Math.Round(RecuoDoTextoAt96 * controle.DeviceDpi / 96.0), x.Top, x.Right, x.Bottom); }
        Recuar();
        controle.HandleCreated += (_, _) => Recuar();
        controle.DpiChangedAfterParent += (_, _) => Recuar();
    }

    /// <summary>
    /// Um rótulo de estado que começa vazio ("Conectado a...", "Transmitindo...") some enquanto não tem texto, em vez de deixar uma faixa em branco no fim do
    /// cartão. Aparece sozinho quando recebe texto; quem esconde de propósito continua escondendo (só o texto novo o mostra).
    /// </summary>
    public static Label EscondeSeVazio(Label rotulo)
    {
        rotulo.Visible = rotulo.Text.Length > 0;
        rotulo.TextChanged += (_, _) => rotulo.Visible = rotulo.Text.Length > 0;
        return rotulo;
    }

    /// <summary>Controles lado a lado, sem quebrar (avatar + texto, campo + botão).</summary>
    public static FlowLayoutPanel Linha(params Control[] controles)
    {
        var linha = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
        foreach (var controle in controles) { if (controle is Button) controle.Margin = new Padding(0, 4, 12, 0); linha.Controls.Add(controle); }
        return linha;
    }

    /// <summary>Botões lado a lado que descem de linha quando a janela é estreita.</summary>
    public static FlowLayoutPanel Botoes(params Control[] botoes)
    {
        var linha = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0, 0, 0, 10) };
        foreach (var botao in botoes) { botao.Margin = new Padding(0, 4, 12, 4); linha.Controls.Add(botao); }
        return linha;
    }

    /// <summary>Um cartão com as linhas empilhadas dentro.</summary>
    public static CartaoDeAjuste Cartao(params Control[] linhas)
    {
        var cartao = new CartaoDeAjuste();
        cartao.Controls.Add(Coluna(linhas));
        return cartao;
    }
}
