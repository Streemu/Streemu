namespace Streemu.Ui;

/// <summary>
/// Painel que se desenha fora da tela e só então vai para a janela. O Panel comum do WinForms apaga e redesenha por partes a cada passo quando o tamanho
/// muda; com a barra lateral deslizando (várias vezes por segundo) isso aparecia como tremida e quadros truncados.
/// Uso: <c>new BufferedPanel { Dock = DockStyle.Fill }</c>
/// </summary>
public sealed class BufferedPanel : Panel
{
    /// <summary>Pinta a árvore inteira de filhos fora da tela e a mostra de uma vez (WS_EX_COMPOSITED), como a página de Configurações; vale só na criação da janela.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Composited { get; init; }

    public BufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; if (Composited) cp.ExStyle |= 0x02000000; return cp; }
    }
}

/// <summary>A tabela de rolagem das páginas (início e ajustes), pelo mesmo motivo do <see cref="BufferedPanel"/>.</summary>
public sealed class BufferedTable : TableLayoutPanel
{
    public BufferedTable() { DoubleBuffered = true; }
}
