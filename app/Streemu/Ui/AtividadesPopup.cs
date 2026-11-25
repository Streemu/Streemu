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
    const int LarguraAt96 = 400, AlturaMaximaAt96 = 380, MargemAt96 = 14, RaioAt96 = 14;

    readonly Panel _moldura = new() { Dock = DockStyle.Fill };
    readonly RolagemSoVertical _rolagem = new() { AutoScroll = true };
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
        _moldura.Resize += (_, _) => { ArredondarMoldura(); Arrumar(); };
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
            return cp; // sem CS_DROPSHADOW: a sombra do Windows é retangular e aparecia atrás dos cantos arredondados
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
        ArredondarCantos();
        ArredondarMoldura();
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

    /// <summary>Cantos do painel (como a lista de amigos do Enviar arquivo); o contorno de 1 px vem do fundo do formulário que aparece em volta da moldura.</summary>
    void ArredondarCantos()
    {
        using var caminho = Theme.RoundedPath(new Rectangle(0, 0, Width, Height), LogicalToDeviceUnits(RaioAt96));
        Region = new Region(caminho); // o Control descarta a região antiga ao receber a nova (descartá-la antes deixava uma inválida guardada)
    }

    /// <summary>A moldura também é recortada, um pixel para dentro, para o contorno acompanhar a curva em vez de sumir nos cantos.</summary>
    void ArredondarMoldura()
    {
        if (_moldura.Width < 4 || _moldura.Height < 4) return;
        using var caminho = Theme.RoundedPath(new Rectangle(0, 0, _moldura.Width, _moldura.Height), Math.Max(1, LogicalToDeviceUnits(RaioAt96) - 1));
        _moldura.Region = new Region(caminho);
    }

    /// <summary>
    /// A lista ocupa a largura útil e a altura que precisa; a moldura rola o resto. Só rola na vertical: o <c>AutoScrollMinSize</c> sem largura impede a barra
    /// horizontal que aparecia quando a vertical surgia e a lista, medida antes, ficava 17 px larga demais (visto em 2026-11-24).
    /// </summary>
    void Arrumar()
    {
        int margem = LogicalToDeviceUnits(MargemAt96);
        _rolagem.Bounds = new Rectangle(margem, margem / 2, Math.Max(1, _moldura.ClientSize.Width - margem - margem / 2), Math.Max(1, _moldura.ClientSize.Height - margem));
        _rolagem.AutoScrollMinSize = new Size(0, _lista.AlturaNecessaria);
        // a largura sai do tamanho da área (que não muda quando a barra vertical entra) menos a barra, se for rolar: o ClientSize só se acerta depois da arrumação
        // e a lista medida por ele ficava 17 px larga demais, cortando a hora e o grupo (visto na tela, 2026-11-24)
        int barra = _lista.AlturaNecessaria > _rolagem.Height ? SystemInformation.VerticalScrollBarWidth : 0;
        _lista.Bounds = new Rectangle(0, _rolagem.AutoScrollPosition.Y, Math.Max(1, _rolagem.Width - barra - margem / 2), _lista.AlturaNecessaria);
    }

    /// <summary>Painel com rolagem só na vertical: a barra horizontal do Windows nunca aparece, mesmo que algo fique largo demais por um instante.</summary>
    sealed class RolagemSoVertical : Panel
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowScrollBar(IntPtr hwnd, int barra, bool mostrar);
        const int SbHorz = 0;

        protected override void WndProc(ref Message m)
        {
            if (IsHandleCreated) ShowScrollBar(Handle, SbHorz, false);
            base.WndProc(ref m);
        }
    }

    public IReadOnlyList<LinhaDeAtividade> Linhas => _lista.Linhas;

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Region?.Dispose(); _moldura.Region?.Dispose(); }
        base.Dispose(disposing);
    }
}
