using System.Drawing.Drawing2D;
using Streemu.Net;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// O cartão "Dar feedback" da aba da Mumu (spec 031, D5): caixa de texto de até <see cref="FeedbackDaMumu.TextoMax"/> letras, "Mandar com meu nome" (desmarcado)
/// e o botão Enviar. O que sai do PC está na spec 031 (4.2), não na tela (pedido de 2026-11-27). Não fala com a rede: o dono escuta
/// <see cref="EnviarClicado"/> e responde com <see cref="MostrarResultado"/>.
/// A caixa não tem barra de rolagem (a do Windows não segue o tema): ela cresce com o texto até <see cref="CaixaMaxDp"/> e, dali em diante, rola pela roda e pelo teclado.
/// </summary>
public sealed class MumuFeedbackCartao : MumuCartaoBase
{
    const int AlturaBotaoDp = 38, CaixaMinDp = 96, CaixaMaxDp = 220, FolgaCaixaDp = 10;
    readonly TextBox _caixa = new() { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.None, WordWrap = true, MaxLength = FeedbackDaMumu.TextoMax, BorderStyle = BorderStyle.None, AccessibleName = "Sua mensagem para a equipe" };
    readonly CaixaDeMarcar _comNome = new() { Text = "Mandar com meu nome", Checked = false, AccessibleName = "Mandar com meu nome" };
    readonly Button _enviar = new() { AutoSize = false, Text = "Enviar", AccessibleName = "Enviar feedback" };
    readonly string _texto;
    string _resultado = "";
    Rectangle _areaTexto, _moldura, _areaResultado;
    bool _ocupado, _resultadoOk;
    int _alturaCaixa;

    public MumuFeedbackCartao(string texto) : base("Dar feedback")
    {
        _texto = texto;
        AccessibleDescription = texto;
        _caixa.PlaceholderText = "Escreva aqui...";
        Controls.AddRange([_caixa, _comNome, _enviar]);
        Theme.StyleButton(_enviar, true);
        _caixa.TextChanged += (_, _) =>
        {
            bool mudou = _resultado.Length > 0;
            _resultado = "";
            if (mudou || AlturaDaCaixa() != _alturaCaixa) AlturaMudou?.Invoke();
            AtualizarBotao();
        };
        _caixa.GotFocus += (_, _) => Invalidate(_moldura);
        _caixa.LostFocus += (_, _) => Invalidate(_moldura);
        _enviar.Click += (_, _) => { if (!_ocupado && _caixa.Text.Trim().Length > 0) EnviarClicado?.Invoke(); };
        AplicarCores();
        AtualizarBotao();
    }

    /// <summary>A pessoa tocou em Enviar com texto na caixa. O dono manda e responde com <see cref="MostrarResultado"/>.</summary>
    public event Action? EnviarClicado;
    /// <summary>A altura do cartão mudou (a caixa cresceu, a frase do resultado apareceu ou sumiu): a tela refaz o layout.</summary>
    public event Action? AlturaMudou;

    public string Mensagem => _caixa.Text;
    public bool ComNome => _comNome.Checked;
    public TextBox Caixa => _caixa;
    public CheckBox CaixaComNome => _comNome;
    public Button Botao => _enviar;
    public string TextoDoResultado => _resultado;

    /// <summary>Enquanto o envio está em andamento o botão fica apagado e a caixa trava, para não mandar duas vezes.</summary>
    public void Ocupado(bool sim)
    {
        _ocupado = sim;
        _caixa.ReadOnly = sim; _comNome.Enabled = !sim;
        _enviar.Text = sim ? "Enviando..." : "Enviar";
        AtualizarBotao();
    }

    /// <summary>Mostra a frase do resultado. Deu certo: esvazia a caixa e desmarca o nome. Falhou: o texto fica na caixa para tentar de novo.</summary>
    public void MostrarResultado(bool ok, string frase)
    {
        if (ok) { _caixa.Clear(); _comNome.Checked = false; }
        _resultado = frase; _resultadoOk = ok;
        AccessibleDescription = _texto + " " + frase;
        AlturaMudou?.Invoke();
        Invalidate();
    }

    void AtualizarBotao() => _enviar.Enabled = !_ocupado && _caixa.Text.Trim().Length > 0;

    void AplicarCores()
    {
        _caixa.BackColor = Theme.SurfaceInput; _caixa.ForeColor = Theme.Text;
        _comNome.Fundo = _comNome.BackColor = Theme.SurfaceRaised; _comNome.ForeColor = Theme.Text; _comNome.Invalidate();
        Theme.StyleButton(_enviar, true);
    }

    protected override void AoMudarTema()
    {
        AplicarCores();
        base.AoMudarTema();
    }

    /// <summary>A caixa acompanha o texto entre o mínimo e o máximo; a largura vem do último layout.</summary>
    int AlturaDaCaixa()
    {
        int w = Math.Max(1, _caixa.Width > 0 ? _caixa.Width : 200);
        string medir = _caixa.Text.Length == 0 ? "M" : _caixa.Text + (_caixa.Text.EndsWith('\n') ? "M" : "");
        int texto = TextRenderer.MeasureText(medir, _caixa.Font, new Size(w, int.MaxValue), Quebra).Height + D(6);
        return Math.Clamp(texto, D(CaixaMinDp) - 2 * D(FolgaCaixaDp), D(CaixaMaxDp) - 2 * D(FolgaCaixaDp));
    }

    /// <summary>A altura que o cartão pede para esta largura, em pixels; também posiciona as peças de dentro.</summary>
    public int AlturaPara(int largura)
    {
        Preparar();
        int pad = D(PadDp), gap = D(UiMetrics.Gap), inner = Math.Max(1, largura - 2 * pad), folga = D(FolgaCaixaDp);
        int y = AlturaDaLegenda();
        _areaTexto = new Rectangle(pad, y, inner, Medir(_texto, Font, inner));
        y = _areaTexto.Bottom + gap;
        _caixa.Width = Math.Max(1, inner - 2 * folga);
        _alturaCaixa = AlturaDaCaixa();
        _moldura = new Rectangle(pad, y, inner, _alturaCaixa + 2 * folga);
        _caixa.Bounds = new Rectangle(pad + folga, y + folga, inner - 2 * folga, _alturaCaixa);
        y = _moldura.Bottom + gap;
        int alturaBotao = D(AlturaBotaoDp), wEnviar = Math.Min(inner, Math.Max(D(120), TextRenderer.MeasureText("Enviando...", _enviar.Font).Width + D(40)));
        _enviar.Bounds = new Rectangle(pad + inner - wEnviar, y, wEnviar, alturaBotao);
        _comNome.Bounds = new Rectangle(pad, y + (alturaBotao - Math.Max(D(28), _comNome.Font.Height + D(10))) / 2, Math.Max(1, inner - wEnviar - gap), Math.Max(D(28), _comNome.Font.Height + D(10)));
        y = _enviar.Bottom;
        if (_resultado.Length > 0) { _areaResultado = new Rectangle(pad, y + gap, inner, Medir(_resultado, Font, inner)); y = _areaResultado.Bottom; }
        return y + pad;
    }

    // a moldura é maior que a caixa: clicar na folga também começa a escrever
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_moldura.Contains(e.Location)) _caixa.Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PintarLegenda(g);
        TextRenderer.DrawText(g, _texto, Font, _areaTexto, Theme.Text, Quebra);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.RoundedPath(_moldura, D(UiMetrics.Radius)))
        {
            using (var fundo = new SolidBrush(Theme.SurfaceInput)) g.FillPath(fundo, path);
            using var borda = new Pen(_caixa.Focused ? Theme.Accent : Theme.Border, _caixa.Focused ? Math.Max(1, D(2) - 1) : 1);
            g.DrawPath(borda, path);
        }
        g.SmoothingMode = SmoothingMode.None;
        if (_resultado.Length > 0) TextRenderer.DrawText(g, _resultado, Font, _areaResultado, _resultadoOk ? Theme.Ok : Theme.Error, Quebra);
    }
}
