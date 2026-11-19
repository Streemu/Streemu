using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// Tela de entrada da conta Streemu (spec 019): a janela inteira com a Mumu, o cartão "Boas-vindas ao Streemu" e o campo do código de 6 dígitos.
/// Só cuida da tela; quem fala com o servidor é <see cref="Net.Conta"/> (ligada em MainForm.Conta.cs).
/// Campo vazio + botão (ou "Criar no site") abre o navegador, onde a pessoa entra e recebe o código; com os 6 dígitos o botão conclui a entrada.
/// </summary>
public sealed class EntradaView : Panel
{
    const string DicaNavegador = "O navegador abriu: entre ou crie a conta por lá e digite aqui o código de 6 dígitos.";
    readonly CampoDeCodigo _campo = new();
    readonly BotaoEntrar _entrar = new();
    readonly LinkLabel _site = new() { Text = "Criar no site", AutoSize = true, Padding = Padding.Empty, Margin = Padding.Empty };
    readonly LinkLabel _privacidade = new() { Text = "Privacidade", AutoSize = true, Padding = Padding.Empty, Margin = Padding.Empty };
    readonly TextBox _endereco = new() { ReadOnly = true, Visible = false, BorderStyle = BorderStyle.FixedSingle, BackColor = ThemePalette.Dark.LoginField, ForeColor = ThemePalette.Dark.Text };
    EntradaFontes? _fontes;
    EntradaMedidas? _medidas;
    Bitmap? _fundo, _mumu;
    double _fatorDasFontes;
    Size _tamanho;
    int _dpi;
    string _dica = "";
    bool _dicaEhErro, _aguardandoCodigo, _desatualizada = true;

    /// <summary>Clicou em "Entrar no Streemu" com o campo vazio, ou em "Criar no site".</summary>
    public event Action? PedirEntrada;
    /// <summary>Digitou o código e confirmou: os dígitos, sem espaços.</summary>
    public event Action<string>? CodigoConfirmado;
    /// <summary>Esc, quando a conta não é obrigatória.</summary>
    public event Action? Cancelado;
    public event Action? AbrirPrivacidade;
    /// <summary>Com a conta obrigatória não há como fechar a tela sem entrar.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool PodeFechar { get; set; }

    public EntradaView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = ThemePalette.Dark.Backdrop;
        _campo.Visible = false;
        foreach (var link in new[] { _site, _privacidade }) EstilizarLink(link);
        Controls.AddRange(new Control[] { _campo, _entrar, _site, _privacidade, _endereco });
        _entrar.Click += (_, _) => AoEntrar();
        _campo.Enviar += AoEntrar;
        _site.LinkClicked += (_, _) => PedirEntrada?.Invoke();
        _privacidade.LinkClicked += (_, _) => AbrirPrivacidade?.Invoke();
    }

    static void EstilizarLink(LinkLabel link)
    {
        link.BackColor = Color.Transparent;
        link.UseCompatibleTextRendering = false; // mesma régua do TextRenderer que desenha o resto da tela
        link.LinkColor = link.VisitedLinkColor = ThemePalette.Dark.LoginEyebrow;
        link.ActiveLinkColor = Theme.AccentText;
        link.LinkBehavior = LinkBehavior.HoverUnderline;
    }

    void AoEntrar()
    {
        string digitos = new(_campo.Valor.Where(char.IsDigit).ToArray()); // aceita colar com espaço ou hífen ("405 618")
        if (digitos.Length == 0) { PedirEntrada?.Invoke(); return; }
        if (digitos.Length != 6) { MostrarErro("O código tem 6 dígitos. Confira o que a página mostrou e digite de novo."); return; }
        if (!_aguardandoCodigo) { MostrarErro("Primeiro pegue o código: apague o campo e toque em Entrar no Streemu para abrir o navegador."); return; }
        Dica("", false);
        CodigoConfirmado?.Invoke(digitos);
    }

    /// <summary>1 = esperando a pessoa começar; 2 = navegador aberto, esperando o código.</summary>
    public void Passo(int n)
    {
        _aguardandoCodigo = n == 2;
        _campo.Visible = n == 2; // o campo só aparece depois do primeiro clique em Entrar
        // escondida (ou ainda sem tamanho), só marca: o OnVisibleChanged refaz quando ela aparecer
        if (Visible && ClientSize.Width > 0 && ClientSize.Height > 0) Recalcular(); else _desatualizada = true;
        if (n == 2) { Dica(DicaNavegador, false); _campo.Focar(); return; }
        _campo.Limpar();
        _endereco.Visible = false;
        Dica("", false);
    }

    /// <summary>Mostra o endereço para copiar quando o navegador não abriu sozinho.</summary>
    public void MostrarEndereco(string url) { _endereco.Text = url; _endereco.Visible = true; }

    /// <summary>Erro com o motivo provável e o que fazer (nunca o código nem o segredo).</summary>
    public void MostrarErro(string texto) => Dica(texto, true);

    public void Ocupado(bool ocupado) => _entrar.Enabled = !ocupado;

    void Dica(string texto, bool erro)
    {
        _dica = texto; _dicaEhErro = erro;
        if (_medidas != null) Invalidate(_medidas.Dica);
    }

    // ---- tamanho e pintura
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (ClientSize == _tamanho && DeviceDpi == _dpi) return; // o layout também roda quando um filho muda; só o tamanho e o DPI refazem as imagens
        _desatualizada = true;
        if (Visible && ClientSize.Width > 0 && ClientSize.Height > 0) Recalcular();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) return;
        if (_desatualizada) Recalcular();
        _campo.Focar();
    }

    void Recalcular()
    {
        _desatualizada = false;
        _tamanho = ClientSize; _dpi = DeviceDpi;
        double encolhe = EntradaMedidas.Encolhimento(ClientSize, DeviceDpi / 96.0 * UiScale.Factor(this), _aguardandoCodigo);
        double escala = DeviceDpi / 96.0 * UiScale.Factor(this) * encolhe;
        PrepararFontes(UiScale.Factor(this) * encolhe); // as fontes encolhem junto com as medidas, senão o texto passa da caixa
        _medidas = new EntradaMedidas(ClientSize, v => (int)Math.Round(v * escala), _aguardandoCodigo);
        Trocar(ref _fundo, EntradaFundo.Criar(_medidas));
        Trocar(ref _mumu, _medidas.TemMascote ? EntradaArte.Mumu(_medidas.Mascote.Size) : null);
        PosicionarControles(_medidas);
        Invalidate();
    }

    void PrepararFontes(double fator)
    {
        if (_fontes != null && _fatorDasFontes == fator) return;
        _fontes?.Dispose();
        _fontes = new EntradaFontes((float)fator);
        _fatorDasFontes = fator;
        _campo.Fonte(_fontes.Codigo);
        _entrar.Font = _fontes.Acao;
        _site.Font = _fontes.FinoNegrito;
        _privacidade.Font = _fontes.RodapeLink;
    }

    static void Trocar(ref Bitmap? atual, Bitmap? novo) { atual?.Dispose(); atual = novo; }

    void PosicionarControles(EntradaMedidas m)
    {
        _campo.Bounds = m.Campo; _campo.Raio = m.RaioCampo;
        _entrar.Bounds = m.Botao; _entrar.Raio = m.RaioBotao;
        _endereco.Bounds = m.Endereco;
        int apos = EntradaDesenho.Largura("Ainda não tem conta?", _fontes!.Fino) + m.Conta.Height / 8;
        _site.Location = new Point(m.Conta.X + apos, m.Conta.Y + (m.Conta.Height - _fontes.Fino.Height) / 2);
        _privacidade.Location = new Point(m.Area.Width - m.MargemRodape - _privacidade.Width, m.RodapeCentroY - _fontes.RodapeLink.Height / 2);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_medidas == null || _fundo == null || _fontes == null) { e.Graphics.Clear(ThemePalette.Dark.Backdrop); return; }
        e.Graphics.DrawImageUnscaled(_fundo, 0, 0);
        EntradaDesenho.Tudo(e.Graphics, _medidas, _fontes, _mumu, _dica, _dicaEhErro);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && PodeFechar) { Cancelado?.Invoke(); return true; }
        if (keyData is Keys.Tab or (Keys.Tab | Keys.Shift)) // a tela cobre a janela toda: o Tab não pode escapar para os controles de baixo
        {
            SelectNextControl(Controls.Cast<Control>().FirstOrDefault(c => c.ContainsFocus), keyData == Keys.Tab, true, true, true);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _fontes?.Dispose(); _fundo?.Dispose(); _mumu?.Dispose(); }
        base.Dispose(disposing);
    }
}
