using Streemu.Net;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// O painel da atividade recente, aberto pelo ícone de pulso da barra lateral. Antes era um bloco fixo no Início, e 20 linhas empurravam a Mumu e os
/// cartões para baixo (relato de 2026-11-18); aqui ele só aparece quando a pessoa pede, com altura máxima e rolagem.
/// Fecha ao perder o foco ou com Esc. Cores só dos tokens do <see cref="Theme"/>, relidas a cada abertura (tema claro e escuro).
/// Uso: <c>popup.Mostrar(dono, itemNaTela, linhas, grupos)</c>; com ele aberto, <c>popup.Atualizar(linhas, grupos)</c> a cada segundo.
/// </summary>
public sealed class AtividadesPopup : Form
{
    const int LarguraAt96 = 340, AlturaMaximaAt96 = 380, MargemAt96 = 12;

    readonly Panel _moldura = new() { Dock = DockStyle.Fill };
    readonly Panel _rolagem = new() { AutoScroll = true };
    readonly AtividadesView _lista = new() { Visible = true };
    /// <summary>Quando fechou por perder o foco: o clique no próprio ícone fecha primeiro e não deve reabrir na mesma hora.</summary>
    long _fechouEm;

    public AtividadesPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        Padding = new Padding(1); // a borda de 1 px é o fundo do formulário aparecendo em volta da moldura
        Text = "Atividade recente";
        _rolagem.Controls.Add(_lista);
        _moldura.Controls.Add(_rolagem);
        Controls.Add(_moldura);
        ScrollSkin.Attach(_rolagem);
        _moldura.Resize += (_, _) => Arrumar();
        Deactivate += (_, _) => Fechar();
    }

    /// <summary>Fechou agora há pouco por perder o foco (o clique no ícone que o fechou não deve reabrir).</summary>
    public bool AcabouDeFechar => Environment.TickCount64 - _fechouEm < 300;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: fora do Alt+Tab
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW: a sombra dos menus do Windows
            return cp;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Fechar(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void Fechar()
    {
        if (!Visible) return;
        _fechouEm = Environment.TickCount64;
        Hide();
    }

    /// <summary>Abre ao lado do ícone (à direita da barra), com o pé alinhado ao item e sem passar da tela.</summary>
    public void Mostrar(Form dono, Rectangle itemNaTela, IReadOnlyList<LinhaDeAtividade> linhas, IReadOnlyList<string>? grupos)
    {
        Preparar(dono.Font, itemNaTela, linhas, grupos);
        if (!Visible) Show(dono);
        Activate();
    }

    /// <summary>Tudo do <see cref="Mostrar"/> menos abrir a janela (o teste de interface desenha sem mostrar nada na tela de quem roda).</summary>
    public void Preparar(Font fonte, Rectangle itemNaTela, IReadOnlyList<LinhaDeAtividade> linhas, IReadOnlyList<string>? grupos)
    {
        Font = fonte;
        Pintar();
        _lista.Mostrar(linhas, grupos);
        int margem = LogicalToDeviceUnits(MargemAt96);
        var tamanho = new Size(LogicalToDeviceUnits(LarguraAt96), Math.Min(LogicalToDeviceUnits(AlturaMaximaAt96), _lista.AlturaNecessaria + margem + 2));
        var tela = Screen.FromRectangle(itemNaTela).WorkingArea;
        int x = Math.Clamp(itemNaTela.Right + LogicalToDeviceUnits(8), tela.Left, Math.Max(tela.Left, tela.Right - tamanho.Width));
        int y = Math.Clamp(itemNaTela.Bottom - tamanho.Height, tela.Top, Math.Max(tela.Top, tela.Bottom - tamanho.Height));
        Bounds = new Rectangle(x, y, tamanho.Width, tamanho.Height);
        Arrumar();
        _rolagem.AutoScrollPosition = Point.Empty; // abre no topo: a mais nova
    }

    /// <summary>A lista rola (tem mais linhas do que cabem na altura máxima)?</summary>
    public bool Rola => _lista.AlturaNecessaria > _rolagem.ClientSize.Height;
    public Rectangle AreaDaLista => _lista.Bounds;
    public Size AreaVisivel => _rolagem.ClientSize;

    /// <summary>Com o painel aberto, troca as linhas (novas atividades ou a hora relativa que mudou) sem mexer na rolagem.</summary>
    public void Atualizar(IReadOnlyList<LinhaDeAtividade> linhas, IReadOnlyList<string>? grupos)
    {
        int antes = _lista.Height;
        _lista.Mostrar(linhas, grupos);
        if (_lista.Height != antes) Arrumar();
    }

    void Pintar()
    {
        BackColor = Theme.Border.A == 255 ? Theme.Border : Theme.Neutral; // Form não aceita cor com transparência
        _moldura.BackColor = _rolagem.BackColor = Theme.SurfaceRaised;
        _lista.ForeColor = Theme.Text;
        _lista.Invalidate();
    }

    /// <summary>A lista ocupa a largura útil (menos a barra de rolagem, se houver) e a altura que precisa; a moldura rola o resto.</summary>
    void Arrumar()
    {
        int margem = LogicalToDeviceUnits(MargemAt96);
        _rolagem.Bounds = new Rectangle(margem, margem / 2, Math.Max(1, _moldura.ClientSize.Width - margem - margem / 2), Math.Max(1, _moldura.ClientSize.Height - margem));
        bool rola = _lista.AlturaNecessaria > _rolagem.ClientSize.Height;
        int largura = Math.Max(1, _rolagem.ClientSize.Width - (rola && !_rolagem.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0) - margem / 2);
        _lista.Bounds = new Rectangle(0, _rolagem.AutoScrollPosition.Y, largura, _lista.AlturaNecessaria);
    }

    public IReadOnlyList<LinhaDeAtividade> Linhas => _lista.Linhas;
}
