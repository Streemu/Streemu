using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// A aba "Histórico" do Enviar arquivo (spec 020, mockup 3): Recebidos e Enviados, uma linha por arquivo com o estado e as ações, e
/// "Limpar histórico". Não sabe de onde vêm as linhas: recebe-as prontas e devolve o código da ação com o índice da linha.
/// Uso: <c>historico.Mostrar(recebidos, enviados); historico.Acao += (recebido, indice, codigo) => ...;</c>
/// </summary>
public sealed class HistoricoArquivosView : UserControl
{
    const int MaxLargura = 880;
    readonly Segmented _lado = new("Recebidos", "Enviados");
    readonly Button _limpar = new() { Text = "Limpar histórico", AutoSize = true, AccessibleName = "Limpar histórico" };
    readonly Label _vazio = new() { AutoSize = false, ForeColor = Theme.TextMuted, Text = "Nada por aqui ainda." };
    readonly Label _nota = new() { AutoSize = true, ForeColor = Theme.TextMuted, Text = "Fica só neste PC: os últimos 200 arquivos ou 90 dias." };
    readonly List<LinhaDeArquivo> _linhas = new();
    IReadOnlyList<DadosDaLinha> _recebidos = [], _enviados = [];

    /// <summary>(recebido?, índice da linha na lista daquele lado, código da ação).</summary>
    public event Action<bool, int, string>? Acao;
    public event Action? LimparPedido;

    public HistoricoArquivosView()
    {
        BackColor = Theme.Surface; ForeColor = Theme.Text; AutoScroll = true;
        ScrollSkin.Attach(this);
        Theme.StyleButton(_limpar, false);
        Controls.AddRange([_lado, _limpar, _vazio, _nota]);
        _lado.SelectedChanged += _ => Recompor();
        _limpar.Click += (_, _) => LimparPedido?.Invoke();
        Resize += (_, _) => Arrumar();
        ClientSizeChanged += (_, _) => Arrumar();
    }

    bool Recebidos => _lado.Selected == 0;

    public void Mostrar(IReadOnlyList<DadosDaLinha> recebidos, IReadOnlyList<DadosDaLinha> enviados)
    {
        bool mudou = !recebidos.SequenceEqual(_recebidos, ComparaLinha.Instancia) || !enviados.SequenceEqual(_enviados, ComparaLinha.Instancia);
        _recebidos = recebidos; _enviados = enviados;
        if (mudou) Recompor();
    }

    void Recompor()
    {
        var lista = Recebidos ? _recebidos : _enviados;
        SuspendLayout();
        while (_linhas.Count > lista.Count) { var l = _linhas[^1]; _linhas.RemoveAt(_linhas.Count - 1); Controls.Remove(l); l.Dispose(); }
        while (_linhas.Count < lista.Count) NovaLinha();
        for (int i = 0; i < lista.Count; i++) _linhas[i].Mostrar(lista[i]);
        _vazio.Visible = lista.Count == 0;
        _limpar.Visible = _recebidos.Count + _enviados.Count > 0;
        ResumeLayout();
        Arrumar();
    }

    void NovaLinha()
    {
        var linha = new LinhaDeArquivo();
        int indice = _linhas.Count;
        linha.Acao += codigo => Acao?.Invoke(Recebidos, indice, codigo);
        _linhas.Add(linha); Controls.Add(linha);
    }

    bool _arrumando;

    // mudar a posição das linhas muda a área útil, que chama o Arrumar de novo: uma volta só por vez
    void Arrumar()
    {
        if (_arrumando || IsDisposed) return;
        _arrumando = true;
        try { ArrumarLinhas(); } finally { _arrumando = false; }
    }

    void ArrumarLinhas()
    {
        int pad = UiScale.Dp(this, UiMetrics.PagePadding), gap = UiScale.Dp(this, 6);
        int reserva = VerticalScroll.Visible ? 0 : SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi); // a barra vertical não abre a horizontal
        int largura = Math.Min(UiScale.Dp(this, MaxLargura), Math.Max(1, ClientSize.Width - 2 * pad - reserva));
        int y = AutoScrollPosition.Y + UiScale.Dp(this, 4);
        _lado.SetBounds(pad, y, _lado.GetPreferredSize(Size.Empty).Width, _lado.GetPreferredSize(Size.Empty).Height);
        _limpar.Location = new Point(pad + largura - _limpar.Width, y + (_lado.Height - _limpar.Height) / 2);
        y += _lado.Height + UiScale.Dp(this, UiMetrics.Gap);
        _vazio.SetBounds(pad, y, largura, UiScale.Dp(this, 40));
        foreach (var l in _linhas) { l.SetBounds(pad, y, largura, l.Height); y += l.Height + gap; }
        if (_vazio.Visible) y += _vazio.Height;
        _nota.Location = new Point(pad, y + UiScale.Dp(this, 8));
        AutoScrollMinSize = new Size(0, _nota.Bottom - AutoScrollPosition.Y + pad);
    }

    /// <summary>Compara pelo conteúdo (o record compara a lista de ações por referência).</summary>
    sealed class ComparaLinha : IEqualityComparer<DadosDaLinha>
    {
        public static readonly ComparaLinha Instancia = new();
        public bool Equals(DadosDaLinha? a, DadosDaLinha? b) => a?.Igual(b) ?? b == null;
        public int GetHashCode(DadosDaLinha d) => HashCode.Combine(d.Titulo, d.Subtitulo, d.Selo);
    }
}
