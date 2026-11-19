using Streemu.Util;

namespace Streemu;

/// <summary>
/// Escala da interface por Ctrl + / Ctrl - / Ctrl 0, como num navegador (ideia de 14/11): só existe, sem aviso na tela, e vale com a janela em foco.
/// A escala fica em <see cref="UiScale.User"/>: os tamanhos (<see cref="UiScale.Dp"/>) e as fontes registradas a seguem; as demais fontes são refeitas aqui,
/// e o layout todo é recalculado. A escolha fica em estado.json.
/// </summary>
public sealed partial class MainForm
{
    static readonly double[] ScaleSteps = [0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75];

    void InitScale()
    {
        double wanted = double.TryParse(Environment.GetEnvironmentVariable("STREEMU_TEST_ESCALA"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var test) ? test : _state.InterfaceScale;
        _scaleFilter = new ScaleKeyFilter(this); Application.AddMessageFilter(_scaleFilter); // pega Ctrl + / Ctrl - antes de qualquer controle (a cadeia do ProcessCmdKey não chegava aqui no teste com a tecla de verdade)
        Disposed += (_, _) => Application.RemoveMessageFilter(_scaleFilter!);
        if (Math.Abs(wanted - 1) < .001) return;
        Shown += (_, _) => BeginInvoke(() => ApplyUserScale(wanted, save: false));
    }

    /// <summary>Ctrl+ / Ctrl= / Ctrl numérico+ aumentam, Ctrl- / Ctrl numérico- diminuem, Ctrl+0 volta ao normal. Devolve verdadeiro se a tecla foi tratada.</summary>
    bool TryScaleKey(Keys keyData)
    {
        if ((keyData & Keys.Control) == 0 || (keyData & Keys.Alt) != 0) return false;
        switch (keyData & Keys.KeyCode)
        {
            case Keys.Oemplus or Keys.Add: StepScale(+1); return true;
            case Keys.OemMinus or Keys.Subtract: StepScale(-1); return true;
            case Keys.D0 or Keys.NumPad0: ApplyUserScale(1, save: true); return true;
            default: return false;
        }
    }

    ScaleKeyFilter? _scaleFilter;

    /// <summary>Filtro de mensagens do app: Ctrl + / Ctrl - / Ctrl 0 com a janela principal ativa.</summary>
    sealed class ScaleKeyFilter(MainForm owner) : IMessageFilter
    {
        const int WmKeyDown = 0x0100, VkOemPlus = 0xBB, VkOemMinus = 0xBD, VkAdd = 0x6B, VkSubtract = 0x6D, Vk0 = 0x30, VkNumpad0 = 0x60;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WmKeyDown || owner.IsDisposed || Form.ActiveForm != owner) return false;
            var modifiers = Control.ModifierKeys;
            if ((modifiers & Keys.Control) == 0 || (modifiers & Keys.Alt) != 0) return false;
            switch ((int)(long)m.WParam)
            {
                case VkOemPlus or VkAdd: owner.StepScale(+1); return true;
                case VkOemMinus or VkSubtract: owner.StepScale(-1); return true;
                case Vk0 or VkNumpad0: owner.ApplyUserScale(1, save: true); return true;
                default: return false;
            }
        }
    }

    void StepScale(int direction)
    {
        int index = Array.FindIndex(ScaleSteps, s => Math.Abs(s - UiScale.User) < .001);
        if (index < 0) index = Array.FindIndex(ScaleSteps, s => s >= UiScale.User - .001);
        ApplyUserScale(ScaleSteps[Math.Clamp(index + direction, 0, ScaleSteps.Length - 1)], save: true);
    }

    void ApplyUserScale(double next, bool save)
    {
        next = Math.Clamp(next, ScaleSteps[0], ScaleSteps[^1]);
        if (!IsHandleCreated || Math.Abs(next - UiScale.User) < .001) return;
        Log.Info($"Escala da interface: {UiScale.User:P0} -> {next:P0}.");
        double ratio = next / UiScale.User;
        SuspendLayout();
        try
        {
            UiScale.User = next;
            UiScale.ScaleFonts(this, ratio); // fontes de controles comuns; as registradas (WatchFont) se refazem pelo evento
            UiScale.RaiseChanged();
        }
        finally { ResumeLayout(true); }
        PerformLayout();
        Invalidate(true);
        AjustarAlturaMinima(); // MainForm.Janela.cs: com a escala maior, a barra lateral precisa de mais altura
        if (save) { _state.InterfaceScale = next; if (!Automated) _state.Save(); }
    }
}
