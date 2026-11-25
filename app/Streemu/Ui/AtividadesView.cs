using Streemu.Net;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// O bloco "Atividade recente" do Início (plano 019, etapa 9): até 20 linhas com o que o grupo fez, a hora relativa à direita, e o texto de vazio quando
/// ainda não há nada. Só desenha o que recebe (as frases vêm prontas de <see cref="FrasesDeAtividade"/>); cores só dos tokens do <see cref="Theme"/>,
/// lidos a cada desenho, então o modo claro e o escuro funcionam sem refazer nada.
/// Uso: <c>view.Mostrar(linhas)</c> (lista vazia = o texto de vazio) e <c>view.Visible = false</c> para esconder (sala antiga).
/// </summary>
public sealed class AtividadesView : Control
{
    IReadOnlyList<LinhaDeAtividade> _linhas = Array.Empty<LinhaDeAtividade>();
    IReadOnlyList<string> _quando = Array.Empty<string>();

    public AtividadesView()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        Visible = false;
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "Atividade recente";
    }

    public IReadOnlyList<LinhaDeAtividade> Linhas => _linhas;

    /// <summary>Mostra as linhas. <paramref name="complemento"/> acrescenta algo depois da hora (o nome do grupo, quando há mais de um): índice = linha.</summary>
    public void Mostrar(IReadOnlyList<LinhaDeAtividade> linhas, IReadOnlyList<string>? complemento = null)
    {
        var quando = linhas.Select((l, i) => complemento != null && i < complemento.Count && complemento[i].Length > 0 ? l.Quando + " · " + complemento[i] : l.Quando).ToList();
        if (linhas.SequenceEqual(_linhas) && quando.SequenceEqual(_quando)) return;
        _linhas = linhas; _quando = quando;
        AccessibleDescription = linhas.Count == 0 ? FrasesDeAtividade.Vazio : string.Join(". ", linhas.Select((l, i) => l.Texto + ", " + quando[i]));
        Height = AlturaNecessaria; // o Início refaz o layout quando a altura muda
        Invalidate();
    }

    int TituloAltura => Dp(30);
    int LinhaAltura => Dp(28);

    /// <summary>A altura que o bloco precisa na largura atual.</summary>
    public int AlturaNecessaria => TituloAltura + (_linhas.Count == 0 ? LinhaAltura : _linhas.Count * LinhaAltura) + Dp(4);

    int Dp(int v) => UiScale.Dp(this, v);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
        TextRenderer.DrawText(g, "Atividade recente", Font, new Rectangle(0, 0, Width, TituloAltura), Theme.SectionText, flags | TextFormatFlags.Left);
        int y = TituloAltura;
        if (_linhas.Count == 0)
        {
            TextRenderer.DrawText(g, FrasesDeAtividade.Vazio, Font, new Rectangle(0, y, Width, LinhaAltura), Theme.TextMuted, flags);
            return;
        }
        using var linha = new Pen(Theme.Separator);
        for (int i = 0; i < _linhas.Count; i++)
        {
            // a hora e o grupo valem mais que a frase (que ganha reticências): até 60% da linha, em vez de cortar "há 51 min · Grupo do St"
            int quandoLargura = Math.Min(Width * 3 / 5,TextRenderer.MeasureText(g, _quando[i], Font, new Size(Width, LinhaAltura), TextFormatFlags.NoPadding).Width + Dp(8));
            var texto = new Rectangle(0, y, Math.Max(0, Width - quandoLargura - Dp(8)), LinhaAltura);
            TextRenderer.DrawText(g, _linhas[i].Texto, Font, texto, Theme.Text, flags);
            TextRenderer.DrawText(g, _quando[i], Font, new Rectangle(Width - quandoLargura, y, quandoLargura, LinhaAltura), Theme.TextMuted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            y += LinhaAltura;
            if (i < _linhas.Count - 1) g.DrawLine(linha, 0, y - 1, Width, y - 1);
        }
    }
}
