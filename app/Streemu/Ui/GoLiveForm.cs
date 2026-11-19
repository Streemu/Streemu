using Streemu.Media;
using Streemu.Net;
namespace Streemu.Ui;

// Compatibilidade do diagnóstico visual antigo; o fluxo do app usa GoLiveView dentro da janela principal.
public sealed class GoLiveForm : Form
{
    public GoLiveView View { get; }
    public GoLiveForm(CaptureSource? current, string? lastProcess, int height, int fps, int audio, bool changing, IReadOnlyList<Member>? friendsOnline = null, string? convidarId = null)
    {
        View = new GoLiveView(current, lastProcess, height, fps, audio, changing, friendsOnline, convidarId);
        Controls.Add(View); ClientSize = new Size(760, 760);
    }
}
