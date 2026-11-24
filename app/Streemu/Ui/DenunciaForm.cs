using Streemu.Net;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// A janela da denúncia (spec 029, 6.1), no padrão dos diálogos do app (<c>PedirApelido</c>): uma categoria, um texto opcional de até 500 caracteres e "Anexar imagem da tela".
/// <b>Nenhuma imagem existe enquanto a janela está aberta</b>: ela recebe só uma função de captura e a chama em <see cref="CapturarAoEnviar"/>, depois do Enviar, com a janela
/// escondida, a caixa marcada e categoria que aceite imagem. Com "Abuso infantil" a caixa some e nada é capturado. O JPEG fica só na memória. Não escreve em log.
/// Uso: <c>using var f = new DenunciaForm("Bruno", capturar); if (f.ShowDialog(this) == DialogResult.OK) ... f.Categoria, f.Texto, f.CapturarAoEnviar()</c>.
/// </summary>
public sealed class DenunciaForm : Form
{
    readonly List<(RadioButton Botao, CategoriaDenuncia Categoria)> _opcoes = new();
    readonly TextBox _texto = new() { Multiline = true, MaxLength = Denuncia.TextoMax, AcceptsReturn = false, ScrollBars = ScrollBars.None, AccessibleName = "Conte mais (opcional)" };
    readonly Label _contador = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Right };
    readonly CheckBox _anexar = new() { Text = "Anexar imagem da tela", Checked = true, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
    readonly Button _enviar = new() { Text = "Enviar", AutoSize = true, Enabled = false, DialogResult = DialogResult.OK };
    readonly Button _cancelar = new() { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
    readonly Func<byte[]?>? _capturar;
    CategoriaDenuncia? _categoria;

    /// <param name="apelido">O nome na tela de quem transmite (o apelido local, se a pessoa deu um).</param>
    /// <param name="capturar">Tira o JPEG da janela do player (nulo se não há player para capturar: a caixa nem aparece). Só é chamada por <see cref="CapturarAoEnviar"/>.</param>
    public DenunciaForm(string apelido, Func<byte[]?>? capturar)
    {
        _capturar = capturar;
        Text = $"Denunciar a live de {apelido}";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        TopMost = true; // a barra do player é "sempre por cima" em tela cheia: a janela tem que aparecer na frente dela
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        layout.Controls.Add(new Label { Text = "Qual é o problema? Quem transmite não fica sabendo que foi você.", AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
        foreach (var (categoria, rotulo, _) in Denuncia.Categorias)
        {
            var botao = new RadioButton { Text = rotulo, AutoSize = true, Margin = new Padding(0, 2, 0, 2), AccessibleName = rotulo };
            botao.CheckedChanged += (_, _) => { if (botao.Checked) Escolher(categoria); };
            _opcoes.Add((botao, categoria));
            layout.Controls.Add(botao);
        }
        layout.Controls.Add(new Label { Text = "Quer contar mais? (opcional)", AutoSize = true, Margin = new Padding(0, 10, 0, 4) });
        _texto.Width = UiScale.Dp(this, 360);
        _texto.Height = UiScale.Dp(this, 76);
        _texto.TextChanged += (_, _) => AtualizarContador();
        layout.Controls.Add(_texto);
        _contador.Margin = new Padding(0, 2, 0, 0);
        layout.Controls.Add(_contador);
        layout.Controls.Add(_anexar);
        var botoes = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0, 12, 0, 0) };
        botoes.Controls.Add(_enviar);
        botoes.Controls.Add(_cancelar);
        layout.Controls.Add(botoes);
        Controls.Add(layout);
        AcceptButton = _enviar;
        CancelButton = _cancelar;
        Theme.Apply(this, _enviar);
        _texto.BackColor = Theme.SurfaceInput;
        _contador.ForeColor = Theme.TextMuted;
        AtualizarContador();
        AtualizarImagem();
    }

    /// <summary>A categoria escolhida (nulo enquanto nenhuma foi).</summary>
    public CategoriaDenuncia? Categoria => _categoria;

    /// <summary>O texto, sem espaços nas pontas e até 500 caracteres; nulo se vazio.</summary>
    public string? Texto => Denuncia.LimparTexto(_texto.Text);

    /// <summary>A imagem deve ser tirada? Só com a caixa à vista e marcada, categoria escolhida que aceite imagem (nunca abuso infantil) e uma função de captura.</summary>
    public bool DeveAnexarImagem => _capturar != null && _anexar.Visible && _anexar.Checked && _categoria != null && Denuncia.PodeAnexarImagem(_categoria);

    /// <summary>
    /// Tira a imagem AGORA (chamar só depois do Enviar). Esconde esta janela antes, para ela não sair na imagem; sem <see cref="DeveAnexarImagem"/> não captura nada e devolve nulo.
    /// Quem captura cuida de esconder a barra do player e o cabeçalho, e de mostrá-los de novo.
    /// </summary>
    public byte[]? CapturarAoEnviar()
    {
        if (!DeveAnexarImagem) return null;
        if (Visible) Hide();
        return _capturar!();
    }

    void Escolher(CategoriaDenuncia categoria)
    {
        _categoria = categoria;
        _enviar.Enabled = true;
        AtualizarImagem();
    }

    void AtualizarImagem() => _anexar.Visible = _capturar != null && Denuncia.PodeAnexarImagem(_categoria);

    void AtualizarContador() => _contador.Text = $"{_texto.TextLength}/{Denuncia.TextoMax}";
}
