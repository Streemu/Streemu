using System.Diagnostics;
using System.Text.Json.Nodes;
using Streemu.Filme;
using Streemu.Ui;
using Streemu.Ui.Avatars;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// A capa do filme no Cinema (spec 017). O anfitrião tira a imagem ao abrir o arquivo e põe só o hash no anúncio, que se repete a cada 5 s; quem vê
/// um hash que ainda não tem pede (no máximo 1 vez a cada 30 s por anfitrião e filme) e o anfitrião responde só a quem pediu. Pedido em vez de envio
/// espontâneo pelo motivo do avatar (MainForm.AvatarRede.cs): a atualização automática reabre o app em segundos e "mandar uma vez a cada amigo" nunca
/// se repetiria. A 0.4.0 ignora o campo e as mensagens novas. Só pela sala: a ligação por código fica com o degradê (D8). Fora do MainForm.Filme.cs
/// para dar menos conflito.
/// </summary>
public partial class MainForm
{
    static readonly TimeSpan CapaPedirIntervalo = TimeSpan.FromSeconds(30), CapaResponderIntervalo = TimeSpan.FromSeconds(10);
    readonly CapasRecebidas _capas = new();
    (string Filme, string Hash, byte[] Jpeg)? _capaMinha;
    string? _capaTirando, _capaRecebidaHash;
    readonly Dictionary<string, DateTime> _capaPedidaEm = new(), _capaRespondidaEm = new();
    readonly HashSet<string> _capaDecodificando = new();

    /// <summary>Anfitrião: tira a capa do arquivo aberto uma vez por filme, em segundo plano. O anúncio já saiu sem ela; quando ficar pronta, sai de novo com o hash.</summary>
    void TirarCapaSeFaltar(JsonObject anuncio)
    {
        if ((string?)anuncio["filme"] is not { } filme || _capaMinha?.Filme == filme || _capaTirando == filme) return;
        if (_filmeForm?.ArquivoAnfitriao is not { } arquivo) return;
        _capaTirando = filme;
        _ = TirarCapaAsync(filme, arquivo);
    }

    async Task TirarCapaAsync(string filme, string arquivo)
    {
        var relogio = Stopwatch.StartNew();
        byte[]? jpeg = null;
        try { jpeg = await Task.Run(() => CapaDoFilme.Tirar(Media.Tools.Ffmpeg, arquivo)); }
        catch (Exception e) { Log.Info($"[filme] capa: não deu para tirar ({e.GetType().Name}: {e.Message}); fica o degradê."); }
        if (jpeg == null) Log.Info("[filme] capa: o ffmpeg não devolveu imagem; fica o degradê.");
        Ui(() => GuardarCapaMinha(filme, jpeg, relogio.ElapsedMilliseconds));
    }

    void GuardarCapaMinha(string filme, byte[]? jpeg, long ms)
    {
        if (_capaTirando == filme) _capaTirando = null;
        if (jpeg == null || (string?)_filmeMeu?["filme"] != filme) return;
        _capaMinha = (filme, AvatarMessage.HashOf(jpeg), jpeg);
        Log.Info($"[filme] capa pronta: {jpeg.Length / 1024.0:0.#} KB em {ms} ms.");
        if (_room is not { Connected: true } room || _filmeMeu is not { } meu) return;
        foreach (var f in room.Friends) _ = room.SendTo(f.Id, AnuncioSala(meu));
    }

    /// <summary>Põe o hash da capa no anúncio (campo novo: a 0.4.0 ignora), só depois que a imagem está pronta. A imagem nunca vai aqui (D5).</summary>
    JsonObject ComCapa(JsonObject anuncio)
    {
        if (_capaMinha is { } c && (string?)anuncio["filme"] == c.Filme) anuncio["capa"] = c.Hash;
        return anuncio;
    }

    /// <summary>Quem vê: o anúncio traz um hash que ainda não tenho, então peço ao anfitrião (no máximo 1 vez a cada 30 s por anfitrião e filme).</summary>
    void PedirCapaSeFaltar(string anfitriao, JsonObject anuncio)
    {
        if ((string?)anuncio["filme"] is not { } filme || (string?)anuncio["capa"] is not { } hash || _capas.Tem(filme, hash)) return;
        string chave = $"{anfitriao}|{filme}";
        var agora = DateTime.UtcNow;
        if (_capaPedidaEm.TryGetValue(chave, out var em) && agora - em < CapaPedirIntervalo) return;
        _capaPedidaEm[chave] = agora;
        _ = _room?.SendTo(anfitriao, CapaDoFilme.Pedido(filme, hash));
    }

    /// <summary>Anfitrião: alguém pediu a capa. Responde só se o filme ainda é o meu, no máximo 1 vez a cada 10 s por amigo.</summary>
    void OnCapaPedida(string de, JsonObject msg)
    {
        if (_capaMinha is not { } c || (string?)msg["filme"] != c.Filme || (string?)_filmeMeu?["filme"] != c.Filme) return;
        var agora = DateTime.UtcNow;
        if (_capaRespondidaEm.TryGetValue(de, out var em) && agora - em < CapaResponderIntervalo) return;
        _capaRespondidaEm[de] = agora;
        _ = _room?.SendTo(de, CapaDoFilme.Imagem(c.Filme, c.Jpeg));
    }

    /// <summary>Quem vê: a capa chegou. Só vale a do anfitrião que anunciou esse filme com esse hash; confere e decodifica fora da thread da janela.</summary>
    void OnCapaRecebida(string de, string nick, JsonObject msg)
    {
        if (!_filmeAbertos.TryGetValue(de, out var aberto) || (string?)msg["filme"] is not { } filme) return;
        if ((string?)aberto.Msg["filme"] != filme || (string?)aberto.Msg["capa"] is not { } hash || (string?)msg["hash"] != hash) return;
        if (_capas.Tem(filme, hash) || !_capaDecodificando.Add(filme)) return;
        _ = DecodificarCapaAsync(filme, hash, nick, msg);
    }

    async Task DecodificarCapaAsync(string filme, string hash, string nick, JsonObject msg)
    {
        var imagem = await Task.Run(() => JpegPayload.Read(msg, CapaDoFilme.MaxBytes) is { } jpeg ? CapasRecebidas.Decodificar(jpeg) : null);
        Ui(() =>
        {
            _capaDecodificando.Remove(filme);
            if (imagem == null) { Log.Info($"[filme] capa de {nick} recebida e ignorada (tamanho, formato ou hash não conferem)."); return; }
            _capas.Guardar(filme, hash, imagem);
            _capaRecebidaHash = hash;
            Log.Info($"[filme] capa de {nick} recebida ({imagem.Width}x{imagem.Height}).");
            _filmeForm?.AtualizarCapas();
        });
    }

    /// <summary>A capa do Cinema (cartão e entrada) pelo filme e o hash do anúncio; nulo = o degradê.</summary>
    Image? CapaDe(string filme, string? hash) => _capas.Get(filme, hash);

    /// <summary>Teste da sala: o anfitrião diz que capa tirou; quem vê, que capa recebeu. O testes-locais.ps1 confere que os hashes batem.</summary>
    void RelatorioCapa(JsonObject r)
    {
        r["capa_hash"] = _capaMinha?.Hash ?? _capaRecebidaHash;
        if (_capaMinha is { } c) r["capa_kb"] = Math.Round(c.Jpeg.Length / 1024.0, 1);
    }
}
