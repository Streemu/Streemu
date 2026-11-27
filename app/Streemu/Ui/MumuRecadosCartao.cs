using System.Drawing.Drawing2D;
using System.Globalization;
using Streemu.Net;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// O histórico de recados da aba da Mumu (spec 031): do mais novo ao mais antigo, cada um com a data, o assunto e o texto. O mais novo, se a pessoa ainda não o viu,
/// ganha a barra de destaque à esquerda e o selo "NOVO". Sem recado nenhum, mostra só o <see cref="MumuTela.RecadoPadrao"/>. Tudo pintado (sem Labels).
/// </summary>
public sealed class MumuRecadosCartao : MumuCartaoBase
{
    readonly record struct Item(RecadoDaMumu? Recado, Rectangle Cabecalho, Rectangle Texto, Rectangle Bloco);

    IReadOnlyList<RecadoDaMumu> _recados = [];
    bool _primeiroNovo;
    List<Item> _itens = [];

    public MumuRecadosCartao() : base("Recados") => AtualizarNome();

    public IReadOnlyList<RecadoDaMumu> Recados => _recados;
    /// <summary>O primeiro recado está destacado como novo.</summary>
    public bool TemNovo => _primeiroNovo && _recados.Count > 0;

    /// <summary>Troca a lista (já do mais novo ao mais antigo). <paramref name="primeiroNovo"/>: a pessoa ainda não tinha visto o primeiro.</summary>
    public void Mostrar(IReadOnlyList<RecadoDaMumu> recados, bool primeiroNovo)
    {
        _recados = recados; _primeiroNovo = primeiroNovo;
        AtualizarNome();
        Invalidate();
    }

    void AtualizarNome() => AccessibleDescription = _recados.Count == 0 ? MumuTela.RecadoPadrao
        : string.Join(" ", _recados.Select((r, i) => (i == 0 && _primeiroNovo ? "Novo. " : "") + Cabecalho(r) + ". " + r.Texto));

    static string Cabecalho(RecadoDaMumu r)
    {
        string data = r.Data is { } d ? d.ToString("d 'de' MMMM 'de' yyyy", new CultureInfo("pt-BR")) : "";
        string titulo = r.Titulo ?? "Recado da Mumu";
        return data.Length > 0 ? data + " · " + titulo : titulo;
    }

    public int AlturaPara(int largura)
    {
        Preparar();
        int pad = D(PadDp), gap = D(UiMetrics.Gap), inner = Math.Max(1, largura - 2 * pad);
        int y = AlturaDaLegenda();
        _itens = [];
        if (_recados.Count == 0)
        {
            var t = new Rectangle(pad, y, inner, Medir(MumuTela.RecadoPadrao, Font, inner));
            _itens.Add(new Item(null, Rectangle.Empty, t, t));
            return t.Bottom + pad;
        }
        for (int i = 0; i < _recados.Count; i++)
        {
            var r = _recados[i];
            bool novo = i == 0 && _primeiroNovo;
            int recuo = novo ? D(14) : 0, w = Math.Max(1, inner - recuo);
            int topo = y;
            if (novo) y += D(10);
            var cab = new Rectangle(pad + recuo, y, w, Math.Max(FonteForte.Height, FontePequena.Height));
            y = cab.Bottom + D(4);
            var txt = new Rectangle(pad + recuo, y, w, Medir(r.Texto, Font, w));
            y = txt.Bottom;
            if (novo) y += D(10);
            _itens.Add(new Item(r, cab, txt, new Rectangle(pad, topo, inner, y - topo)));
            if (i < _recados.Count - 1) y += gap * 3 / 2;
        }
        return y + pad;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PintarLegenda(g);
        for (int i = 0; i < _itens.Count; i++)
        {
            var it = _itens[i];
            if (it.Recado is not { } r) { TextRenderer.DrawText(g, MumuTela.RecadoPadrao, Font, it.Texto, Theme.Text, Quebra); continue; }
            bool novo = i == 0 && _primeiroNovo;
            if (novo) PintarDestaque(g, it.Bloco);
            if (i > 0)
            {
                int yl = it.Bloco.Top - D(UiMetrics.Gap) * 3 / 4;
                using var linha = new Pen(Theme.Divider);
                g.DrawLine(linha, it.Bloco.Left, yl, it.Bloco.Right, yl);
            }
            // cabeçalho: [NOVO] data · assunto
            int x = it.Cabecalho.X;
            if (novo) x = PintarSelo(g, x, it.Cabecalho) + D(8);
            string data = r.Data is { } d ? d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "";
            if (data.Length > 0)
            {
                int wData = TextRenderer.MeasureText(g, data, FontePequena, Size.Empty, Linha).Width;
                TextRenderer.DrawText(g, data, FontePequena, new Rectangle(x, it.Cabecalho.Y, wData, it.Cabecalho.Height), Theme.TextMuted, Linha | TextFormatFlags.VerticalCenter);
                x += wData + D(10);
            }
            TextRenderer.DrawText(g, r.Titulo ?? "Recado da Mumu", FonteForte, new Rectangle(x, it.Cabecalho.Y, Math.Max(0, it.Cabecalho.Right - x), it.Cabecalho.Height), Theme.Text, Linha | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, r.Texto, Font, it.Texto, novo ? Theme.Text : Theme.TextMuted, Quebra);
        }
    }

    void PintarDestaque(Graphics g, Rectangle bloco)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.RoundedPath(bloco, D(UiMetrics.Radius)))
        using (var fundo = new SolidBrush(Theme.SelectionSoft)) g.FillPath(fundo, path);
        using (var barra = Theme.RoundedPath(new Rectangle(bloco.X + D(5), bloco.Y + D(10), D(4), Math.Max(D(4), bloco.Height - D(20))), D(2)))
        using (var acento = new SolidBrush(Theme.Accent)) g.FillPath(acento, barra);
        g.SmoothingMode = SmoothingMode.None;
    }

    int PintarSelo(Graphics g, int x, Rectangle linha)
    {
        const string texto = "NOVO";
        var tam = TextRenderer.MeasureText(g, texto, FonteLegenda, Size.Empty, Linha);
        var r = new Rectangle(x, linha.Y + (linha.Height - tam.Height - D(4)) / 2, tam.Width + D(12), tam.Height + D(4));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.RoundedPath(r, r.Height / 2))
        using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, path);
        g.SmoothingMode = SmoothingMode.None;
        TextRenderer.DrawText(g, texto, FonteLegenda, r, Theme.AccentText, Linha | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        return r.Right;
    }
}
