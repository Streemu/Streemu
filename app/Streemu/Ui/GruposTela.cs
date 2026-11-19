using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// A tela "Grupos" da janela principal (item da barra lateral, ao lado de Cinema e Enviar arquivo): título, uma linha de apoio e o <see cref="GruposView"/>
/// numa coluna rolável de largura confortável. Só moldura: a lista, os botões e a lógica são do GruposView e do MainForm.Grupos.cs. Antes era uma
/// categoria das Configurações; saiu de lá porque criar e gerenciar grupos é uso de todo dia, não ajuste.
/// </summary>
public sealed class GruposTela : UserControl
{
    public const string TextoApoio = "Crie um grupo, entre com um convite ou gerencie quem está nos seus grupos.";

    readonly Label _titulo = new() { Text = "Grupos", AutoSize = true };
    readonly Label _apoio = new() { Text = TextoApoio, AutoSize = true, ForeColor = Theme.TextMuted };
    readonly Panel _cabecalho = new() { Dock = DockStyle.Top };
    readonly Panel _rolagem = new() { AutoScroll = true, Dock = DockStyle.Fill };
    readonly Font _fonteTitulo = UiTypography.TitleFont();
    readonly GruposView _lista;
    bool _arrumando;

    public GruposTela(GruposView lista)
    {
        _lista = lista;
        Dock = DockStyle.Fill; BackColor = Theme.Surface; ForeColor = Theme.Text;
        _titulo.Font = _fonteTitulo;
        _cabecalho.BackColor = _rolagem.BackColor = BackColor;
        _cabecalho.Controls.AddRange([_titulo, _apoio]);
        _lista.Margin = Padding.Empty;
        _rolagem.Controls.Add(_lista);
        ScrollSkin.Attach(_rolagem);
        Controls.Add(_rolagem); // a ordem importa para o encaixe: o que enche vem antes do cabeçalho (mesmo padrão do Cinema)
        Controls.Add(_cabecalho);
        _cabecalho.Resize += (_, _) => Arrumar();
        _rolagem.ClientSizeChanged += (_, _) => Arrumar();
        VisibleChanged += (_, _) => Arrumar();
    }

    // Escondida, o layout mediria tudo errado (os filhos dizem Visible=false): arruma quando aparece, como o Enviar arquivo.
    void Arrumar()
    {
        if (_arrumando || IsDisposed || !Visible) return;
        _arrumando = true;
        try
        {
            int pad = UiScale.Dp(this, UiMetrics.PagePadding), gap = UiScale.Dp(this, UiMetrics.Gap);
            _apoio.MaximumSize = new Size(Math.Max(1, _cabecalho.ClientSize.Width - 2 * pad), 0);
            _titulo.Location = new Point(pad, pad);
            _apoio.Location = new Point(pad, pad + _titulo.PreferredHeight + gap / 2);
            int altura = _apoio.Bottom + gap;
            if (_cabecalho.Height != altura) _cabecalho.Height = altura;
            // reserva a barra vertical antes de ela aparecer: senão ela rouba a largura depois do layout e abre a rolagem horizontal
            int reserva = _rolagem.VerticalScroll.Visible ? 0 : SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi);
            int largura = Math.Max(UiScale.Dp(this, 180), Math.Min(UiScale.Dp(this, UiMetrics.SettingsContentMax), _rolagem.ClientSize.Width - 2 * pad - reserva));
            _rolagem.SuspendLayout();
            SettingsLayout.Constrain(_lista, largura);
            _lista.Location = new Point(pad, gap + _rolagem.AutoScrollPosition.Y);
            _rolagem.ResumeLayout();
        }
        finally { _arrumando = false; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _fonteTitulo.Dispose();
        base.Dispose(disposing);
    }
}
