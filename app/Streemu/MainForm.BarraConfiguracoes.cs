using Streemu.Util;

namespace Streemu;

/// <summary>
/// A barra lateral nas Configurações. Ali ela vira trilho para dar lugar à coluna de categorias, e a barra flutuante, que abre por cima, cobria as
/// categorias (dois vídeos de 2026-11-16: primeiro sobrava a ponta da categoria escolhida, depois a coluna inteira sumia embaixo dela).
/// Nas Configurações o mouse no trilho abre a barra ENCAIXADA, empurrando as categorias, como o «; tirar o mouse da barra a recolhe de novo.
/// O « com ela aberta pelo mouse a deixa aberta.
/// Aqui a barra não desliza: com ela deslizando as categorias deixavam rastros e a página ficava em branco por alguns quadros (vídeo de 2026-11-16,
/// 18:03); a largura muda de uma vez, com uma pintura só.
/// </summary>
public sealed partial class MainForm
{
    const int EsperaDoMouseMs = 180; // a mesma espera da barra flutuante: passar o mouse de raspão não abre
    bool _abertaPeloMouse;

    void TickBarraNasConfiguracoes()
    {
        if (!SidebarHover) // desligado nos Ajustes: só o « abre a barra; se o mouse a tinha aberto, recolhe
        {
            _hoverSince = DateTime.MinValue;
            if (_abertaPeloMouse) RecolherAbertaPeloMouse();
            return;
        }
        var cursor = Cursor.Position;
        bool sobreABarra = _side.RectangleToScreen(_side.ClientRectangle).Contains(cursor) && IsOurWindowAt(cursor);
        if (_abertaPeloMouse) { if (!sobreABarra && !_menuDaBarraAberto) RecolherAbertaPeloMouse(); return; }
        if (!sobreABarra || !PodeAbrirPeloMouse()) { _hoverSince = DateTime.MinValue; return; }
        if (_hoverSince == DateTime.MinValue) { _hoverSince = DateTime.Now; return; }
        if (DateTime.Now - _hoverSince > TimeSpan.FromMilliseconds(EsperaDoMouseMs)) AbrirPeloMouse();
    }

    bool PodeAbrirPeloMouse() => _side.Visible && _side.Collapsed && Visible && WindowState != FormWindowState.Minimized && !_menuDaBarraAberto;

    void AbrirPeloMouse()
    {
        _hoverSince = DateTime.MinValue;
        _abertaPeloMouse = true;
        TrocarBarraSemDeslizar(false);
    }

    void RecolherAbertaPeloMouse()
    {
        _abertaPeloMouse = false;
        TrocarBarraSemDeslizar(true);
    }

    /// <summary>Abre ou recolhe a barra nas Configurações sem deslizar e pinta a janela uma vez só. Ex.: <c>TrocarBarraSemDeslizar(!_side.Collapsed)</c>.</summary>
    void TrocarBarraSemDeslizar(bool recolhida)
    {
        using var pausa = new PinturaPausada(this);
        _side.SnapCollapsed(recolhida);
        PerformLayout();
        FinishSlide(); // sem deslize não há Slid: refaz aqui as quebras de linha na largura nova
    }

    /// <summary>O « com a barra aberta pelo mouse: ela fica aberta (devolve verdadeiro e o « não faz mais nada).</summary>
    bool FixarAbertaPeloMouse()
    {
        if (!_abertaPeloMouse) return false;
        _abertaPeloMouse = false;
        return true;
    }
}
