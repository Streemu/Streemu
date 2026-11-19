using Streemu.Net;

namespace Streemu;

/// <summary>
/// STREEMU_TEST_SOM_NA_LIVE=manual (junto com a privada de teste, STREEMU_TEST_PRIVADA): depois que o convidado conecta, o anfitrião manda o som de entrada e o de saída
/// a TODOS (convidado e não convidado). Só quem assiste o anfitrião deve tocar: o teste confere pelo log de cada um.
/// </summary>
public sealed partial class MainForm
{
    static bool TesteSomManual => Environment.GetEnvironmentVariable("STREEMU_TEST_SOM_NA_LIVE") == "manual";

    async Task TesteSomManualAsync()
    {
        var until = DateTime.Now.AddSeconds(20);
        while (!_links.Values.Any(l => l.IAmSender && l.Connected) && DateTime.Now < until) await Task.Delay(200);
        await Task.Delay(1500);
        foreach (var tipo in new[] { TipoSomNaLive.Entrou, TipoSomNaLive.Saiu })
        {
            foreach (string papel in new[] { "B", "C" })
                if (_room?.Friends.FirstOrDefault(f => f.Nick == "teste-" + papel) is { } amigo)
                    await _room.SendTo(amigo.Id, SomNaLive.Mensagem(tipo, _streamId ?? ""));
            await Task.Delay(1500);
        }
    }
}
