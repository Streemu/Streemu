using Streemu.Util;

namespace Streemu.Ui;

/// <summary>Ícone e cores do menu da bandeja (tema escuro do DESIGN.md, igual ao menu do Go Live do Discord).</summary>
public static class TrayVisuals
{
    /// <summary>
    /// O gato da marca (<see cref="UiBrand"/>) no tamanho pequeno do sistema; ao vivo ganha um ponto vermelho no canto. Antes era um quadrado
    /// com "L" feito por <c>GetHicon</c>, cujo handle nunca era liberado.
    /// </summary>
    public static Icon CreateIcon(bool live)
    {
        using var all = UiBrand.CreateIcon(live);
        return new Icon(all, SystemInformation.SmallIconSize);
    }

    public static void Style(ContextMenuStrip menu)
    {
        menu.Renderer = new ToolStripProfessionalRenderer(new DarkColors()) { RoundedEdges = false };
        menu.BackColor = Theme.SurfaceInput;
        menu.ForeColor = Theme.Text;
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = true;
    }

    sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.SurfaceInput;
        public override Color MenuBorder => Theme.SurfaceInput;
        public override Color MenuItemBorder => Theme.Accent;
        public override Color MenuItemSelected => Theme.Accent;
        public override Color MenuItemSelectedGradientBegin => Theme.Accent;
        public override Color MenuItemSelectedGradientEnd => Theme.Accent;
        public override Color MenuItemPressedGradientBegin => Theme.AccentHover;
        public override Color MenuItemPressedGradientEnd => Theme.AccentHover;
        public override Color SeparatorDark => Theme.Neutral;
        public override Color SeparatorLight => Theme.SurfaceInput;
        public override Color ImageMarginGradientBegin => Theme.SurfaceInput;
        public override Color ImageMarginGradientMiddle => Theme.SurfaceInput;
        public override Color ImageMarginGradientEnd => Theme.SurfaceInput;
        public override Color CheckBackground => Theme.Accent;
        public override Color CheckSelectedBackground => Theme.AccentHover;
        public override Color CheckPressedBackground => Theme.AccentHover;
    }
}
