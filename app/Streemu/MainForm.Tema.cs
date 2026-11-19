using Streemu.Ui;
using Streemu.Util;

namespace Streemu;

/// <summary>O botão de tema da barra lateral: escuro (padrão) ou claro, guardado no estado do usuário.</summary>
public sealed partial class MainForm
{
    /// <summary>
    /// Chamado depois do <see cref="Theme.Apply"/>. Tudo nasce no tema escuro (os campos da janela copiam cores antes de o estado ser lido); se o escolhido da
    /// última vez foi o claro, a mesma troca do botão converte os controles já criados, sem transição porque a janela ainda não está na tela.
    /// </summary>
    void InitTheme()
    {
        _side.ThemeClicked += hole => ToggleTheme(hole);
        if (_state.TemaClaro) ThemeSwitch.Set(this, light: true);
        _side.SetThemeLight(Theme.IsLight, animate: false);
    }

    /// <summary>
    /// Troca de tema com a transição suave. <paramref name="holeOnScreen"/> é o botão: fica fora da foto para o puxador deslizar ao vivo.
    /// Testes automáticos e o modo local não gravam a escolha (regra 3 do AGENTS.md).
    /// </summary>
    void ToggleTheme(Rectangle holeOnScreen)
    {
        bool light = !Theme.IsLight;
        _side.SetThemeLight(light, animate: true);
        _flyout?.Panel.SetThemeLight(light, animate: true);
        ThemeSwitch.Set(this, light, holeOnScreen.IsEmpty ? null : holeOnScreen, PersistTheme);
        if (_tray?.ContextMenuStrip is { } trayMenu) TrayVisuals.Style(trayMenu); // o menu da bandeja nasce uma vez e não está em OpenForms
    }

    void PersistTheme(bool light)
    {
        _state.TemaClaro = light;
        if (_localMode || TestDir != null) return;
        _state.Save();
    }
}
