using Streemu.Avisos;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// Avisos de "fulano começou uma live" e "fulano abriu um filme" (pedido de 11/11): só informar, sem encher a pessoa. Por isso:
/// (1) o que chega em 3 s vira UM aviso ("Bruno e Ana começaram uma live"), e entre um aviso e outro passam ao menos 20 s (o que chega no meio entra no próximo);
/// (2) o que já estava no ar quando a sala conectou não avisa, nem a mesma live de novo quando a sala cai e volta (~2 s, ver DESCOBERTAS) ou a pessoa reabre a lista;
/// (3) não avisa a sua própria live nem a de quem você já está assistindo, nem o filme em que você já está.
/// Com a janela do Streemu em foco o aviso vai para a linha de status; fora de foco (jogando), para o balão da bandeja (o "Não perturbe" e o modo jogo do Windows
/// seguram o balão em tela cheia). Ajustes > Assistir desliga.
/// </summary>
public sealed partial class MainForm
{
    readonly CheckBox _avisos = new Ui.ToggleSwitch { Text = "Avisar quando um amigo começar uma live ou abrir um filme", AutoSize = true };
    /// <summary>Lives (StreamId) e filmes já vistos, com a última vez que apareceram: o que está aqui não avisa de novo.</summary>
    readonly Dictionary<string, DateTime> _avisoVisto = new();
    readonly List<(string Nick, string Curto, string Longo)> _avisoFila = new();
    DateTime _avisoFilaDesde, _avisoUltimo = DateTime.MinValue;
    DateTime? _avisoSalaDesde;
    System.Windows.Forms.Timer? _avisoTimer;
    AmigosSilenciados? _amigosSilenciados;

    /// <summary>Quem o usuário calou pelo menu da foto; criado com o que o estado.json guardou. Só filtra live e filme, nunca convite (spec 013).</summary>
    AmigosSilenciados AmigosMudos => _amigosSilenciados ??= CriarAmigosMudos();

    AmigosSilenciados CriarAmigosMudos()
    {
        var mudos = new AmigosSilenciados(_state.AmigosSilenciados);
        mudos.Mudou += () =>
        {
            _state.AmigosSilenciados = mudos.Ids.ToList();
            if (!Automated) _state.Save();
        };
        return mudos;
    }

    /// <summary>Juntar o que chega nesse tempo num aviso só (duas pessoas ligando a live juntas, no começo da noite).</summary>
    static readonly TimeSpan AvisoJuntar = TimeSpan.FromSeconds(3), AvisoIntervalo = TimeSpan.FromSeconds(20);
    /// <summary>Logo depois de a sala conectar chegam todas as lives e filmes que já estavam rolando (os filmes se repetem a cada 5 s): só guardam como vistos.</summary>
    static readonly TimeSpan AvisoSemente = TimeSpan.FromSeconds(8);

    /// <summary>A opção em Ajustes > Assistir.</summary>
    Control AvisosOpcao()
    {
        _avisos.Checked = !_state.AvisosDesligados;
        _avisos.CheckedChanged += (_, _) =>
        {
            _state.AvisosDesligados = !_avisos.Checked;
            if (!_avisos.Checked) { _avisoFila.Clear(); _avisoTimer?.Stop(); }
            if (!Automated) _state.Save();
        };
        Disposed += (_, _) => _avisoTimer?.Dispose();
        return _avisos;
    }

    /// <summary>A cada mudança da sala (OnRoomChanged): lives que começaram agora.</summary>
    void AvisosDaSala()
    {
        if (_room is not { Connected: true } room) { _avisoSalaDesde = null; return; }
        var agora = DateTime.Now;
        _avisoSalaDesde ??= agora;
        bool semente = agora - _avisoSalaDesde < AvisoSemente;
        foreach (var m in room.Friends)
        {
            if (m.Live is not { } live || string.IsNullOrEmpty(live.StreamId)) continue;
            if (!Visto("live:" + live.StreamId) || semente) continue; // marcar como visto antes de filtrar: reativar o amigo depois não anuncia uma live antiga
            if (AmigosMudos.Contem(m.Id)) { Log.Info($"[silêncio] aviso de live de {NomeNaTela(m)} calado (amigo silenciado)."); continue; }
            // começou faz tempo (a pessoa voltou para a sala depois de cair por mais tempo que a folga da lista): não é novidade
            if (live.SinceUnixMs > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - live.SinceUnixMs > 120_000) continue;
            if (_links.Values.Any(l => !l.IAmSender && l.PeerId == m.Id)) continue; // já assistindo
            string jogo = string.IsNullOrWhiteSpace(live.Game) ? "" : live.Game.Trim();
            Enfileirar(NomeNaTela(m), "começou uma live", jogo.Length > 0 ? $"começou uma live: {jogo}" : "começou uma live");
        }
    }

    /// <summary>Um amigo abriu um filme (o filme_aberto da sala, a primeira vez que aparece). <paramref name="from"/> é o id dele na sala.</summary>
    void AvisoFilme(string from, string nick, string nome)
    {
        if (!Visto($"filme:{from}:{nome}")) return;
        if (AmigosMudos.Contem(from)) { Log.Info($"[silêncio] aviso de filme de {NomeNaTela(from, nick)} calado (amigo silenciado)."); return; }
        if (_avisoSalaDesde is not { } desde || DateTime.Now - desde < AvisoSemente) return;
        if (_filmeForm?.Sessao != null) return; // já está num filme (o próprio ou o dele)
        Enfileirar(NomeNaTela(from, nick), "abriu um filme", $"abriu um filme: {nome}. Clique em Filme para assistir junto");
    }

    /// <summary>Marca como visto; verdadeiro só na primeira vez (esquece o que sumiu há mais de 6 h).</summary>
    bool Visto(string chave)
    {
        var agora = DateTime.Now;
        if (_avisoVisto.Count > 200) foreach (var k in _avisoVisto.Where(kv => agora - kv.Value > TimeSpan.FromHours(6)).Select(kv => kv.Key).ToList()) _avisoVisto.Remove(k);
        bool novo = !_avisoVisto.ContainsKey(chave);
        _avisoVisto[chave] = agora;
        return novo;
    }

    void Enfileirar(string nick, string curto, string longo)
    {
        Log.Info($"[amigo] {nick} {longo}."); // não é "[aviso]": isso é informação, e a telemetria conta toda linha "[aviso]" como falha
        if (!_avisos.Checked || Automated) return;
        if (_avisoFila.Count == 0) _avisoFilaDesde = DateTime.Now;
        _avisoFila.RemoveAll(a => a.Nick == nick); // a mesma pessoa duas vezes no mesmo aviso: vale a última
        _avisoFila.Add((nick, curto, longo));
        if (_avisoTimer == null) { _avisoTimer = new System.Windows.Forms.Timer { Interval = 1000 }; _avisoTimer.Tick += (_, _) => DescarregarAvisos(); }
        _avisoTimer.Start();
    }

    void DescarregarAvisos()
    {
        if (!_avisos.Checked || _avisoFila.Count == 0) { _avisoFila.Clear(); _avisoTimer?.Stop(); return; }
        var agora = DateTime.Now;
        if (_avisoFila.Count == 0 || agora - _avisoFilaDesde < AvisoJuntar || agora - _avisoUltimo < AvisoIntervalo) return;
        var f = _avisoFila.ToList();
        _avisoFila.Clear();
        _avisoTimer?.Stop();
        _avisoUltimo = agora;
        string texto = f.Count switch
        {
            1 => $"{f[0].Nick} {f[0].Longo}.",
            2 when f[0].Curto == f[1].Curto => $"{f[0].Nick} e {f[1].Nick} {(f[0].Curto == "começou uma live" ? "começaram uma live" : "abriram um filme")}.",
            2 => $"{f[0].Nick} {f[0].Curto} e {f[1].Nick} {f[1].Curto}.",
            _ => $"{f[0].Nick}, {f[1].Nick} e mais {f.Count - 2} começaram uma live ou abriram um filme.",
        };
        // em foco: a linha de status basta; fora de foco (jogando, outra janela), o balão da bandeja (clicar abre o Streemu)
        _groupStatus.Text = texto;
        if (Form.ActiveForm == null) MostrarBalao(5000, UiBrand.Name, texto, ToolTipIcon.None);
    }
}
