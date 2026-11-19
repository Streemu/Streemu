using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// "Onde você recebe arquivos" (desenho de 2026-11-18): ícone de pasta, a legenda e o caminho em negrito, com reticências se não couber.
/// Pintado na hora com o tema de agora. Uso: <c>pasta.Mostrar(@"C:\Users\ana\Downloads\Streemu");</c> (dentro do perfil aparece como "Downloads\Streemu").
/// </summary>
public sealed class PastaDeRecebidos : Control
{
    const int AlturaAt96 = 50, IconeAt96 = 24, EspacoAt96 = 14;
    readonly Font _legenda = UiTypography.Create(UiTypography.Caption), _caminho = UiTypography.Create(UiTypography.Body, FontStyle.Bold);
    string _texto = "";

    public PastaDeRecebidos()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        AccessibleRole = AccessibleRole.StaticText;
    }

    public int AlturaPadrao => UiScale.Dp(this, AlturaAt96);

    /// <summary>O caminho como a pessoa o reconhece: sem a pasta do perfil na frente.</summary>
    public void Mostrar(string pasta)
    {
        string perfil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\') + "\\";
        _texto = pasta.StartsWith(perfil, StringComparison.OrdinalIgnoreCase) ? pasta[perfil.Length..] : pasta;
        AccessibleName = $"Onde você recebe arquivos: {pasta}";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        int icone = UiScale.Dp(this, IconeAt96), x = icone + UiScale.Dp(this, EspacoAt96);
        UiIcons.Draw(g, UiIcon.Folder, new Rectangle(0, (Height - icone) / 2, icone, icone), Theme.TextMuted);
        int alto = _legenda.Height + _caminho.Height + UiScale.Dp(this, 2), topo = (Height - alto) / 2;
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
        int largura = Math.Max(1, Width - x);
        TextRenderer.DrawText(g, "Onde você recebe arquivos", _legenda, new Rectangle(x, topo, largura, _legenda.Height + 2), Theme.TextMuted, flags);
        TextRenderer.DrawText(g, _texto, _caminho, new Rectangle(x, topo + _legenda.Height + UiScale.Dp(this, 2), largura, _caminho.Height + 2), Theme.Text, flags);
    }

    protected override void Dispose(bool disposing) { if (disposing) { _legenda.Dispose(); _caminho.Dispose(); } base.Dispose(disposing); }
}
