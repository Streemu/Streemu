namespace Streemu.Ui.Avatars;

/// <summary>
/// Onde está o mouse, em coordenadas da tela. A animação do avatar dependia do evento "mouse saiu", que o Windows NÃO envia se a
/// janela some com o mouse em cima (a barra do player some sozinha): o GIF ficava animando "sem o mouse". Quem anima consulta isto a
/// cada tick e solta se o mouse já não está sobre ele. Também é o ponto em que os testes simulam o mouse sem mexer no do usuário.
/// </summary>
public static class PointerProbe
{
    /// <summary>Posição atual do mouse na tela; troque nos testes.</summary>
    public static Func<Point> ScreenPosition { get; set; } = () => Cursor.Position;

    /// <summary>O mouse está dentro de <paramref name="area"/> (retângulo do controle em coordenadas do próprio controle)?</summary>
    public static bool IsOver(Control control, Rectangle area) => area.Contains(control.PointToClient(ScreenPosition()));
}
