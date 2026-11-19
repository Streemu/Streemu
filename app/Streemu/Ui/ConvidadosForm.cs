using Streemu.Util;

namespace Streemu.Ui;

/// <summary>Uma pessoa na janela Convidados: já convidada (pode ser removida) ou só online (pode ser convidada).</summary>
public sealed record PessoaConvite(string Id, string Apelido, bool Convidada);

/// <summary>
/// Janela do anfitrião durante a live privada (spec 012): quem está convidado (Remover) e quem está online e ainda não foi (Convidar).
/// Quem manda é o anfitrião, então a janela só pergunta e pede; a regra do limite e do código fica em <c>SalaPrivada</c>.
/// Uso: <c>new ConvidadosForm(listar, (id, nick) =&gt; ConvidarAsync(id, nick), id =&gt; Remover(id), limite).Show(this);</c>
/// </summary>
public sealed class ConvidadosForm : Form
{
    readonly Func<IReadOnlyList<PessoaConvite>> _listar;
    readonly Func<string, string, Task<string?>> _convidar;
    readonly Action<string> _remover;
    readonly int _limite;
    readonly FlowLayoutPanel _linhas = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
    readonly Label _aviso = new() { AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(0, 8, 0, 0) };
    readonly System.Windows.Forms.Timer _atualizar = new() { Interval = 1000 };
    string _assinatura = "";

    /// <param name="convidar">Recebe o id e o apelido; devolve o motivo da recusa (frase pronta) ou nulo se o convite saiu.</param>
    public ConvidadosForm(Func<IReadOnlyList<PessoaConvite>> listar, Func<string, string, Task<string?>> convidar, Action<string> remover, int limite)
    {
        (_listar, _convidar, _remover, _limite) = (listar, convidar, remover, limite);
        Text = "Convidados da transmissão privada";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = UiTypography.Create(UiTypography.Body);
        ClientSize = new Size(LogicalToDeviceUnits(420), LogicalToDeviceUnits(360));
        var raiz = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(LogicalToDeviceUnits(16)) };
        raiz.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        raiz.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        raiz.Controls.Add(_linhas, 0, 0);
        raiz.Controls.Add(_aviso, 0, 1);
        Controls.Add(raiz);
        Theme.Apply(this);
        _atualizar.Tick += (_, _) => Atualizar();
        _atualizar.Start();
        FormClosed += (_, _) => _atualizar.Dispose();
        Atualizar();
    }

    /// <summary>Só remonta as linhas quando a lista mudou (senão o botão sumiria debaixo do mouse a cada segundo).</summary>
    void Atualizar()
    {
        var pessoas = _listar();
        string assinatura = string.Join("|", pessoas.Select(p => $"{p.Id}:{p.Convidada}"));
        if (assinatura == _assinatura) return;
        _assinatura = assinatura;
        _linhas.SuspendLayout();
        foreach (Control c in _linhas.Controls) c.Dispose();
        _linhas.Controls.Clear();
        foreach (var p in pessoas.OrderByDescending(p => p.Convidada).ThenBy(p => p.Apelido, StringComparer.CurrentCultureIgnoreCase)) _linhas.Controls.Add(Linha(p));
        if (pessoas.Count == 0) _linhas.Controls.Add(new Label { Text = "Ninguém online agora.", AutoSize = true, ForeColor = Theme.TextMuted });
        _linhas.ResumeLayout();
        _aviso.Text = $"{pessoas.Count(p => p.Convidada) + 1} de {_limite} na sala (contando você).";
    }

    Control Linha(PessoaConvite pessoa)
    {
        var linha = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Width = LogicalToDeviceUnits(370), Margin = new Padding(0, 2, 0, 2) };
        linha.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        linha.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var nome = new Label { Text = pessoa.Convidada ? pessoa.Apelido : $"{pessoa.Apelido} (não convidado)", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = pessoa.Convidada ? Theme.Text : Theme.TextMuted };
        var botao = new Button { Text = pessoa.Convidada ? "Remover" : "Convidar", AutoSize = true, Anchor = AnchorStyles.Right };
        Theme.StyleButton(botao, !pessoa.Convidada);
        botao.Click += async (_, _) => await Clicar(pessoa);
        linha.Controls.Add(nome, 0, 0);
        linha.Controls.Add(botao, 1, 0);
        return linha;
    }

    async Task Clicar(PessoaConvite pessoa)
    {
        if (pessoa.Convidada) _remover(pessoa.Id);
        else if (await _convidar(pessoa.Id, pessoa.Apelido) is { } motivo) { _aviso.Text = motivo; return; }
        _assinatura = "";
        Atualizar();
    }
}
