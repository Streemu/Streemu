using System.Text.Json.Nodes;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// Ganchos dos testes automáticos do "continuar assistindo" (spec 038). Só rodam com as variáveis abaixo; um app de verdade nunca as tem.
/// <c>STREEMU_TEST_ACOMPANHAR_LIVE="15,6,12"</c> (anfitrião): ao vivo por 15 s, fora do ar por 6 s, ao vivo de novo e espera 12 s.
/// <c>STREEMU_TEST_ACOMPANHAR=1</c> (quem assiste): marca o amigo ao vivo como acompanhado (5 min) antes de assistir. O relatório leva "acompanhar".
/// </summary>
public sealed partial class MainForm
{
    int _acompanharEntradas, _acompanharEsperas, _acompanharDesistencias;

    static bool TesteAcompanharAtivo => Environment.GetEnvironmentVariable("STREEMU_TEST_ACOMPANHAR") == "1";

    /// <summary>Os três tempos (segundos) do roteiro do anfitrião, ou nulo se a variável não existe ou está malformada.</summary>
    static int[]? TesteAcompanharRoteiroDoAnfitriao()
    {
        var partes = (Environment.GetEnvironmentVariable("STREEMU_TEST_ACOMPANHAR_LIVE") ?? "").Split(',', StringSplitOptions.TrimEntries);
        var tempos = partes.Select(p => int.TryParse(p, out int s) && s > 0 ? s : -1).ToArray();
        return tempos.Length == 3 && tempos.All(t => t > 0) ? tempos : null;
    }

    void TesteAcompanharMarcar(Net.Member amigo)
    {
        _state.AmigosAcompanhados[amigo.Id] = 5;
        Log.Info("teste: marquei o amigo ao vivo como acompanhado (5 min).");
    }

    /// <summary>Anfitrião: ao vivo, cai, e volta, como um amigo que fecha a live e abre outra.</summary>
    async Task TesteAcompanharAnfitriaoAsync(int[] tempos)
    {
        await Task.Delay(tempos[0] * 1000);
        OnLiveStopped();
        await Task.Delay(tempos[1] * 1000);
        _fakeLive = true;
        OnLiveStarted("Jogo de teste");
        await Task.Delay(tempos[2] * 1000);
    }

    JsonObject RelatorioDoAcompanhar() => new()
    {
        ["entradas"] = _acompanharEntradas,
        ["esperas"] = _acompanharEsperas,
        ["desistencias"] = _acompanharDesistencias,
    };
}
