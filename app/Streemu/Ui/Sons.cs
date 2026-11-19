using System.Media;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// Sons curtos para quem transmite: a live abriu, fechou, alguém entrou, alguém saiu. Assim o host sabe que deu certo
/// sem tirar o olho do jogo. Os WAVs vão embutidos no exe (pasta Sons/), então a atualização automática leva junto.
/// </summary>
/// <remarks>
/// O SoundPlayer toca pelo próprio processo do streemu: com o áudio em "Só o app transmitido" (o padrão) quem assiste não ouve.
/// Em "PC todo menos Discord" (ou transmitindo um monitor) o som entra na stream, porque a captura só sabe excluir um
/// processo e esse lugar é do Discord.
/// </remarks>
public static class Sons
{
    public enum Som { LiveAbriu, LiveFechou, EspectadorEntrou, EspectadorSaiu, ConviteRecebido, EntrouNaLive, SaiuDaLive }

    static readonly Dictionary<Som, SoundPlayer?> _players = new();

    /// <summary>Toca sem esperar. <paramref name="mudo"/> = teste automático: só registra no log, para não atrapalhar quem joga.</summary>
    public static void Tocar(Som som, bool mudo)
    {
        Log.Info($"Som: {Arquivo(som)}{(mudo ? " (mudo no teste)" : "")}.");
        if (mudo || Carregar(som) is not { } player) return;
        try { player.Play(); }
        catch (Exception e) { Log.Info($"Não consegui tocar {Arquivo(som)}: {e.Message}"); }
    }

    static SoundPlayer? Carregar(Som som)
    {
        if (_players.TryGetValue(som, out var pronto)) return pronto;
        SoundPlayer? player = null;
        try
        {
            var stream = typeof(Sons).Assembly.GetManifestResourceStream("Streemu.Sons." + Arquivo(som));
            if (stream == null) Log.Info($"O som {Arquivo(som)} não está no exe; sigo sem ele.");
            else { player = new SoundPlayer(stream); player.Load(); }
        }
        catch (Exception e)
        {
            Log.Info($"Não consegui carregar o som {Arquivo(som)}, sigo sem ele: {e.Message}");
            player = null;
        }
        _players[som] = player; // não tenta de novo a cada evento se falhou
        return player;
    }

    static string Arquivo(Som som) => som switch
    {
        Som.LiveAbriu => "live-abriu.wav",
        Som.LiveFechou => "live-fechou.wav",
        Som.EspectadorEntrou => "espectador-entrou.wav",
        Som.ConviteRecebido => "convite-recebido.wav",
        Som.EntrouNaLive => "entrou-na-live.wav",
        Som.SaiuDaLive => "saiu-da-live.wav",
        _ => "espectador-saiu.wav"
    };
}
