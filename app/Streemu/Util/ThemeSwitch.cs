using Streemu.Ui;

namespace Streemu.Util;

/// <summary>
/// Troca o tema do app inteiro: muda a paleta ativa (<see cref="Theme.Use"/>), troca nos controles já criados cada cor da paleta antiga pela equivalente
/// da nova e refaz a barra de título de cada janela aberta, tudo por baixo da transição de <see cref="ThemeFade"/>.
/// Só troca cor que é exatamente uma cor da paleta antiga, então o que foi pintado de propósito (palco preto, texto branco sobre vídeo) fica como está,
/// e a tela de entrada, que é escura nos dois temas, não é tocada.
/// Uso: <c>ThemeSwitch.Set(this, light: true, hole: botao, persist: v =&gt; _state.TemaClaro = v);</c>
/// </summary>
public static class ThemeSwitch
{
    public static void Set(Form main, bool light, Rectangle? holeOnScreen = null, Action<bool>? persist = null)
    {
        if (light == Theme.IsLight) return;
        ThemeFade.Run(main, () => Apply(main, light), holeOnScreen);
        persist?.Invoke(light);
    }

    static void Apply(Form main, bool light)
    {
        var before = Theme.Current;
        Theme.Use(light);
        var pairs = new ColorPairs(ThemePalette.Pairs(before, Theme.Current, includeWhite: true), ThemePalette.Pairs(before, Theme.Current, includeWhite: false));
        // a janela principal entra de qualquer jeito: na abertura do app ela ainda não está em OpenForms
        foreach (Form form in Application.OpenForms.Cast<Form>().Append(main).Distinct().ToList())
        {
            Recolor(form, pairs);
            if (form.IsHandleCreated) Theme.ApplyCaption(form.Handle);
            form.Invalidate(true);
        }
    }

    /// <summary>Branco só vale como fundo (SurfaceInput do claro): como texto é o branco sobre botão colorido e não pode virar a cor de um campo.</summary>
    sealed record ColorPairs(Dictionary<int, Color> Back, Dictionary<int, Color> Fore);

    static void Recolor(Control control, ColorPairs pairs)
    {
        if (control is EntradaView or ThemeFade) return;
        control.SuspendLayout();
        Swap(control, pairs);
        foreach (Control child in control.Controls) Recolor(child, pairs);
        control.ResumeLayout(false);
    }

    static void Swap(Control control, ColorPairs pairs)
    {
        if (pairs.Back.TryGetValue(control.BackColor.ToArgb(), out var back)) control.BackColor = back;
        if (pairs.Fore.TryGetValue(control.ForeColor.ToArgb(), out var fore)) control.ForeColor = fore;
        if (control is Button button) SwapButton(button, pairs);
        if (control is LinkLabel link) SwapLink(link, pairs);
    }

    /// <summary>
    /// O texto que o <see cref="Theme.StyleButton"/> põe (branco ou o texto do neutro) acompanha o fundo: branco sobre botão colorido (destaque, vermelho, verde),
    /// texto do neutro sobre os demais, que no claro são quase brancos. Texto de outra cor, posto por quem criou o botão, é só trocado pela paleta.
    /// </summary>
    static void SwapButton(Button button, ColorPairs pairs)
    {
        var look = button.FlatAppearance;
        if (pairs.Back.TryGetValue(look.MouseOverBackColor.ToArgb(), out var over)) look.MouseOverBackColor = over;
        if (pairs.Back.TryGetValue(look.MouseDownBackColor.ToArgb(), out var down)) look.MouseDownBackColor = down;
        int fore = button.ForeColor.ToArgb();
        if (fore == Theme.AccentText.ToArgb() || fore == Theme.NeutralText.ToArgb()) button.ForeColor = IsColored(button.BackColor) ? Theme.AccentText : Theme.NeutralText;
    }

    static bool IsColored(Color back) =>
        back.ToArgb() is var argb && (argb == Theme.Accent.ToArgb() || argb == Theme.AccentHover.ToArgb() || argb == Theme.AccentMuted.ToArgb()
            || argb == Theme.AccentMutedHover.ToArgb() || argb == Theme.Error.ToArgb() || argb == Theme.SuccessSurface.ToArgb());

    static void SwapLink(LinkLabel link, ColorPairs pairs)
    {
        if (pairs.Fore.TryGetValue(link.LinkColor.ToArgb(), out var normal)) link.LinkColor = normal;
        if (pairs.Fore.TryGetValue(link.ActiveLinkColor.ToArgb(), out var active)) link.ActiveLinkColor = active;
    }
}
