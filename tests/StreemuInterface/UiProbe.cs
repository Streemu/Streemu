using System.Reflection;

/// <summary>
/// O que os testes de palco e tema precisam para mexer por dentro da janela real sem rede nem player: campos e métodos privados, espera de pintura e foto.
/// Uso: <c>var stage = UiProbe.Field&lt;PlayerStage&gt;(form, "_playerStage"); await UiProbe.SettleAsync();</c>
/// </summary>
static class UiProbe
{
    const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

    public static T Field<T>(object owner, string name) =>
        (T)(owner.GetType().GetField(name, Hidden) ?? throw new MissingFieldException($"{owner.GetType().Name}.{name} não existe (esperado: campo privado)")).GetValue(owner)!;

    public static void Set(object owner, string name, object? value) =>
        (owner.GetType().GetField(name, Hidden) ?? throw new MissingFieldException($"{owner.GetType().Name}.{name} não existe (esperado: campo privado)")).SetValue(owner, value);

    public static object? Call(object owner, string name, params object[] args) =>
        (owner.GetType().GetMethod(name, Hidden | BindingFlags.Public) ?? throw new MissingMethodException($"{owner.GetType().Name}.{name} não existe")).Invoke(owner, args);

    /// <summary>Deixa as mensagens, os relógios (palco: 250 ms; barra: 100 ms) e as pinturas rodarem.</summary>
    public static async Task SettleAsync(int rounds = 8)
    {
        for (int i = 0; i < rounds; i++) { await Task.Delay(50); Application.DoEvents(); }
    }

    /// <summary>Foto de um controle do jeito que ele se desenha (sem o que está por cima dele na tela).</summary>
    public static Bitmap Photo(Control control)
    {
        var image = new Bitmap(Math.Max(1, control.Width), Math.Max(1, control.Height));
        control.DrawToBitmap(image, new Rectangle(Point.Empty, control.Size));
        return image;
    }

    /// <summary>Contraste WCAG entre duas cores (1 a 21). Texto normal pede 4,5.</summary>
    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    static double Luminance(Color c)
    {
        static double Channel(int v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}
