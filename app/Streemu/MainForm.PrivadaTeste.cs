using System.Reflection;
using System.Text.Json.Nodes;
using Streemu.Net;
using Streemu.Ui;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// Teste automático da stream privada com 3 instâncias (STREEMU_TEST_ROOM=1, papéis A, B e C; spec 012, T7).
/// STREEMU_TEST_PRIVADA="B": A transmite em privado e convida só o B. O B aceita o convite; o C não foi convidado e,
/// sabendo o id da transmissão (o pior caso), tenta assistir. A miniatura não entra aqui: o teste não captura de verdade, então nenhuma é gerada. Cada visitante grava &lt;papel&gt;-privada.json.
/// </summary>
public sealed partial class MainForm
{
    static readonly string[] TestePrivadaConvidados =
        (Environment.GetEnvironmentVariable("STREEMU_TEST_PRIVADA") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static bool TestePrivadaAtiva => TestRoom && TestePrivadaConvidados.Length > 0;
    // Só o A é o anfitrião privado: com STREEMU_TEST_LIVE os outros transmissores ficam ao vivo em público (como no grupo em 2026-11-15).
    static bool TestePrivadaAnfitriao => TestePrivadaAtiva && TestRole == "A";

    /// <summary>Anfitrião: grava o id da transmissão (para o C forjar o pedido), espera os convidados de teste entrarem na sala e manda o convite.</summary>
    async Task TestePrivadaConvidarAsync()
    {
        File.WriteAllText(Path.Combine(TestDir!, "A-streamid.txt"), _streamId ?? "");
        await TestePrivadaEsperarSalaDeContaAsync();
        foreach (string papel in TestePrivadaConvidados)
        {
            var amigo = await TestePrivadaEsperarAmigoAsync("teste-" + papel);
            if (amigo == null) { Log.Info($"teste: {papel} não apareceu na sala para ser convidado."); continue; }
            if (TesteMenuConvidar) { await TesteConvidarPeloMenuAsync(amigo); continue; }
            var r = Privada.Convidar(amigo.Id, amigo.Nick);
            if (r.Ok) await _room!.SendTo(amigo.Id, Privada.MensagemConvite(r.Convidado!, Nick));
            else Log.Info($"teste: convite para {papel} falhou: {r.Motivo}");
        }
        if (TesteSomManual) await TesteSomManualAsync(); // MainForm.SomNaLiveTeste.cs
        if (Environment.GetEnvironmentVariable("STREEMU_TEST_PRIVADA_REMOVER") == "1") await TestePrivadaRemoverAsync();
        if (Environment.GetEnvironmentVariable("STREEMU_TEST_PRIVADA_FOTO") == "1") { await Task.Delay(1500); FotografarJanela(new ConvidadosForm(ListarPessoasParaConvite, ConvidarAsync, RemoverConvidado, SalaPrivada.Limite), "A-convidados.png"); }
    }

    static bool TesteMenuConvidar => Environment.GetEnvironmentVariable("STREEMU_TEST_MENU_CONVIDAR") == "1";

    /// <summary>
    /// STREEMU_TEST_MENU_CONVIDAR=1 (spec 013): o anfitrião convida pelos itens do menu da foto, clicando-os por código (sem mouse), e
    /// grava A-menu-convidar.json: o texto do item Convidar antes e depois, e o Silenciar/Reativar de um amigo.
    /// </summary>
    async Task TesteConvidarPeloMenuAsync(Member amigo)
    {
        var alvo = new SideFriend(amigo.Id, amigo.Nick, "", false, false, null);
        string antes = TextoDoItem(alvo, "Convidar");
        ClicarItem(alvo, "Convidar");
        var until = DateTime.Now.AddSeconds(5);
        while (!Privada.EstaNaLista(amigo.Id) && DateTime.Now < until) await Task.Delay(100);
        string depois = TextoDoItem(alvo, "Já convidado");
        ClicarItem(alvo, "Silenciar");
        bool calou = AmigosMudos.Contem(amigo.Id), textoMudo = TextoDoItem(alvo, "Reativar").StartsWith("Reativar");
        ClicarItem(alvo, "Reativar");
        var r = new JsonObject { ["antes"] = antes, ["depois"] = depois, ["calou"] = calou, ["item_reativar"] = textoMudo, ["calado_no_fim"] = AmigosMudos.Contem(amigo.Id), ["na_lista"] = Privada.EstaNaLista(amigo.Id) };
        File.WriteAllText(Path.Combine(TestDir!, "A-menu-convidar.json"), r.ToJsonString());
    }

    ToolStripMenuItem? ItemDoMenu(SideFriend amigo, string comeco) =>
        ItensDoMenuAmigo(amigo).OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Text!.StartsWith(comeco));

    string TextoDoItem(SideFriend amigo, string comeco) =>
        ItemDoMenu(amigo, comeco) is { } item ? item.Text! + (item.Enabled ? "" : " (desativado)") : "";

    void ClicarItem(SideFriend amigo, string comeco) => ItemDoMenu(amigo, comeco)?.PerformClick();

    /// <summary>B aceita o convite; C tenta entrar mesmo sem ele. Grava o que viu e o que aconteceu.</summary>
    async Task TestePrivadaVisitanteAsync()
    {
        await TestePrivadaEsperarSalaDeContaAsync();
        var anfitriao = await TestePrivadaEsperarAmigoAsync("teste-A");
        string streamId = await TestePrivadaLerStreamIdAsync();
        bool convidado = TestePrivadaConvidados.Contains(TestRole);
        if (convidado) await TestePrivadaEsperarConviteAsync();
        bool viuLive = _room?.Friends.Any(f => f.Live != null) == true;
        if (convidado && Environment.GetEnvironmentVariable("STREEMU_TEST_PRIVADA_FOTO") == "1") await TestePrivadaFotografarAsync();
        if (!convidado && Environment.GetEnvironmentVariable("STREEMU_TEST_PRIVADA_FOTO") == "1") FotografarJanela(TesteGoLivePrivado(), "C-transmitir.png");
        if (convidado && Environment.GetEnvironmentVariable("STREEMU_TEST_MENU_AMIGO") == "1") await TesteMenuAmigoAsync(anfitriao);
        var card = new JsonObject();
        if (convidado && TesteAceitarPeloCard) card = await TesteAceitarPeloCardAsync(); // MainForm.PrivadaCardTeste.cs
        else if (anfitriao != null && streamId != "") await AssistirConvidadoAsync(anfitriao.Id, anfitriao.Nick, streamId);
        await Task.Delay(8000);
        var r = new JsonObject { ["papel"] = TestRole, ["convidado"] = convidado, ["viu_live_na_sala"] = viuLive, ["recebeu_convite"] = _convites.Count > 0, ["card"] = card, ["assistindo"] = TesteQuemAssisto() };
        File.WriteAllText(Path.Combine(TestDir!, TestRole + "-privada.json"), r.ToJsonString());
        var until = DateTime.Now.AddSeconds(30);
        while (DateTime.Now < until && !TestStreamersReported()) await Task.Delay(200);
    }

    /// <summary>
    /// Nos testes com conta o app só abre a sala quando o login por arquivo termina, e nela o id é o da conta ("c-..."). Se o outro PC ainda não terminou o
    /// login, o convite feito para ele ficaria preso a um id que ainda vai mudar (a SalaPrivada guarda pelo id) e o pedido do id novo seria recusado.
    /// </summary>
    async Task TestePrivadaEsperarSalaDeContaAsync()
    {
        if (!TestConta) return;
        var until = DateTime.Now.AddSeconds(20);
        while ((!_salaDeConta || _room is not { Connected: true }) && DateTime.Now < until) await Task.Delay(200);
    }

    async Task<Member?> TestePrivadaEsperarAmigoAsync(string apelido)
    {
        await TestePrivadaEsperarSalaDeContaAsync();
        var until = DateTime.Now.AddSeconds(TestConta ? 40 : 20); // o outro app pode levar mais para terminar o login
        Member? amigo;
        // com conta, só vale o amigo que já tem id de conta: antes da troca dele o id ainda é o da sala antiga
        while ((amigo = _room?.Friends.FirstOrDefault(f => f.Nick == apelido && (!TestConta || f.Id.StartsWith("c-")))) == null && DateTime.Now < until) await Task.Delay(200);
        return amigo;
    }

    static async Task<string> TestePrivadaLerStreamIdAsync()
    {
        string arquivo = Path.Combine(TestDir!, "A-streamid.txt");
        var until = DateTime.Now.AddSeconds(20);
        while (DateTime.Now < until && (!File.Exists(arquivo) || new FileInfo(arquivo).Length == 0)) await Task.Delay(200);
        return File.Exists(arquivo) ? File.ReadAllText(arquivo).Trim() : "";
    }

    async Task TestePrivadaEsperarConviteAsync()
    {
        var until = DateTime.Now.AddSeconds(20);
        while (_convites.Count == 0 && DateTime.Now < until) await Task.Delay(200);
    }

    /// <summary>Fotografa a janela com o card de convite na tela (antes de aceitar), para conferir o desenho sem olhar o monitor.</summary>
    async Task TestePrivadaFotografarAsync()
    {
        Show();
        WindowState = FormWindowState.Normal; // o teste roda minimizado: sem isso a "foto" sai do tamanho da barra de título
        Size = new Size(1100, 760);
        await Task.Delay(2500); // o card aparece no próximo ciclo de 1 s
        using var foto = new Bitmap(Width, Height);
        DrawToBitmap(foto, new Rectangle(0, 0, Width, Height));
        foto.Save(Path.Combine(TestDir!, TestRole + "-card.png"));
        ModoSilencio.Silenciar(TimeSpan.FromMinutes(90)); // também fotografa a barra com o sino silenciado (spec 012, parte B)
        await Task.Delay(1500);
        using var fotoSino = new Bitmap(Width, Height);
        DrawToBitmap(fotoSino, new Rectangle(0, 0, Width, Height));
        fotoSino.Save(Path.Combine(TestDir!, TestRole + "-sino.png"));
        ModoSilencio.Ligar();
    }

    /// <summary>
    /// STREEMU_TEST_MENU_AMIGO=1 (com STREEMU_TEST_PRIVADA_FOTO=1): clica na foto do anfitrião na barra lateral como o mouse faria (o mesmo OnMouseClick),
    /// fotografa o menu aberto e grava &lt;papel&gt;-menu.json com os textos dos itens.
    /// </summary>
    async Task TesteMenuAmigoAsync(Member? amigo)
    {
        Show();
        WindowState = FormWindowState.Normal;
        Size = new Size(1100, 760);
        await Task.Delay(2500);
        var linhas = (System.Collections.IEnumerable)typeof(SidePanel).GetField("_rows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_side)!;
        Point? alvo = null;
        foreach (var linha in linhas)
        {
            var tipo = linha.GetType();
            if (tipo.GetProperty("Friend")!.GetValue(linha) is not SideFriend f || f.Id != amigo?.Id) continue;
            var caixa = (Rectangle)tipo.GetProperty("Bounds")!.GetValue(linha)!;
            alvo = new Point(caixa.X + 6 + 16, caixa.Y + caixa.Height / 2); // dentro da foto (a barra aberta põe a foto 6 px depois da borda)
        }
        if (alvo is not { } ponto) { Log.Info("teste: não achei a linha do anfitrião na barra lateral."); return; }
        typeof(SidePanel).GetMethod("OnMouseClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_side, new object[] { new MouseEventArgs(MouseButtons.Left, 1, ponto.X, ponto.Y, 0) });
        await Task.Delay(800);
        var menu = _menuAmigo;
        var itens = menu?.Items.Cast<ToolStripItem>().Select(i => i.Text + (i.Enabled ? "" : " (desativado)")).ToArray() ?? Array.Empty<string>();
        if (menu != null) { using var foto = new Bitmap(menu.Width, menu.Height); menu.DrawToBitmap(foto, new Rectangle(0, 0, menu.Width, menu.Height)); foto.Save(Path.Combine(TestDir!, TestRole + "-menu.png")); menu.Close(); }
        File.WriteAllText(Path.Combine(TestDir!, TestRole + "-menu.json"), new JsonObject { ["abriu"] = menu != null, ["itens"] = new JsonArray(itens.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()) }.ToJsonString());
    }

    /// <summary>A janela Transmitir com "Só convidados" ligado e todos os amigos online na lista (marca o primeiro).</summary>
    GoLiveForm TesteGoLivePrivado()
    {
        var janela = new GoLiveForm(null, null, 1080, 60, 0, false, _room!.Friends.ToList());
        var seg = (Segmented)typeof(GoLiveView).GetField("_privacySeg", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(janela.View)!;
        seg.Selected = 1;
        return janela;
    }

    static void FotografarJanela(Form janela, string arquivo)
    {
        janela.Show();
        Application.DoEvents();
        using var foto = new Bitmap(janela.Width, janela.Height);
        janela.DrawToBitmap(foto, new Rectangle(0, 0, janela.Width, janela.Height));
        foto.Save(Path.Combine(TestDir!, arquivo));
        janela.Dispose();
    }

    /// <summary>STREEMU_TEST_PRIVADA_REMOVER=1: depois que o convidado conecta, o anfitrião o remove e mede em quanto tempo a ligação cai (critério do T2: até 2 s).</summary>
    async Task TestePrivadaRemoverAsync()
    {
        var until = DateTime.Now.AddSeconds(15);
        while (!_links.Values.Any(l => l.IAmSender && l.Connected) && DateTime.Now < until) await Task.Delay(200);
        bool conectou = _links.Values.Any(l => l.IAmSender && l.Connected);
        await Task.Delay(1500);
        var convidado = Privada.Convidados.FirstOrDefault();
        var relogio = System.Diagnostics.Stopwatch.StartNew();
        if (convidado != null) RemoverConvidado(convidado.Id);
        while (_links.Values.Any(l => l.IAmSender) && relogio.ElapsedMilliseconds < 5000) await Task.Delay(50);
        var r = new JsonObject { ["conectou_antes"] = conectou, ["derrubou_em_ms"] = relogio.ElapsedMilliseconds, ["ainda_na_lista"] = Privada.Convidados.Count };
        File.WriteAllText(Path.Combine(TestDir!, "A-remover.json"), r.ToJsonString());
    }
}
