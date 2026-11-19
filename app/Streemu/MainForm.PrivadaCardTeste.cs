using System.Reflection;
using System.Text.Json.Nodes;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// STREEMU_TEST_PRIVADA_CARD=1 (com a privada de teste e STREEMU_TEST_LIVE="C,D"): o convidado aceita pelo botão Aceitar do card no Início,
/// com outras lives públicas no ar, e depois clica de novo no mesmo ponto (o clique duplo). Reproduz o relato de 2026-11-15:
/// uma pessoa aceitava a privada de um amigo e abriam também as lives de todo mundo que estava ao vivo.
/// </summary>
public sealed partial class MainForm
{
    static bool TesteAceitarPeloCard => Environment.GetEnvironmentVariable("STREEMU_TEST_PRIVADA_CARD") == "1";

    /// <summary>Clica Aceitar como o mouse, espera o card sair e clica de novo no mesmo ponto da tela. Devolve o que havia sob o ponto antes e depois.</summary>
    async Task<JsonObject> TesteAceitarPeloCardAsync()
    {
        await TesteEsperarLivesPublicasAsync(2);
        TesteRestaurarForaDaTela();
        ShowView(stage: false);
        await Task.Delay(2500); // o card aparece no próximo ciclo de 1 s
        var aceitar = _conviteCards.Controls.Cast<Control>().SelectMany(c => c.Controls.OfType<Button>()).FirstOrDefault(b => b.Text == "Aceitar");
        if (aceitar == null) return new JsonObject { ["achou_aceitar"] = false };
        var ponto = aceitar.PointToScreen(new Point(aceitar.Width / 2, aceitar.Height / 2));
        string antes = TesteControleNoPonto(ponto);
        TesteClicar(aceitar);
        await Task.Delay(1800); // a ligação entra; o card fica no lugar, desativado ("Conectando"), até o player ter janela
        string depois = TesteControleNoPonto(ponto);
        var sob = TesteBotaoNoPonto(ponto);
        bool sobAtivo = sob is { Enabled: true };
        // O segundo clique é forçado (o OnClick ignora o Enabled): simula os dois cliques que chegam juntos, que o AceitarConvite tem que segurar.
        if (sob != null) TesteClicar(sob);
        await Task.Delay(500);
        return new JsonObject { ["achou_aceitar"] = true, ["antes"] = antes, ["depois"] = depois, ["botao_ativo_depois"] = sobAtivo, ["todos_juntos_visivel"] = _watchAllLive.Visible };
    }

    /// <summary>
    /// Minimizado o Início não tem tamanho e nada se arruma; restaurado na tela, a janela roubaria o foco de quem está jogando (regra 2 do AGENTS).
    /// Então guarda o tamanho normal longe da tela (o WinForms grava como "tamanho restaurado" enquanto minimizado) e restaura sem ativar.
    /// </summary>
    void TesteRestaurarForaDaTela()
    {
        const int SwShowNoActivate = 4;
        Bounds = new Rectangle(-6000, -6000, 1100, 760);
        Native.ShowWindow(Handle, SwShowNoActivate);
        Bounds = new Rectangle(-6000, -6000, 1100, 760);
    }

    async Task TesteEsperarLivesPublicasAsync(int quantas)
    {
        var ate = DateTime.Now.AddSeconds(20);
        while ((_room?.Friends.Count(f => f.Live != null) ?? 0) < quantas && DateTime.Now < ate) await Task.Delay(200);
    }

    /// <summary>O controle mais fundo da janela sob um ponto da tela, como "tipo:texto".</summary>
    string TesteControleNoPonto(Point naTela) => TesteFolhaNoPonto(naTela) is var c ? $"{c.GetType().Name}:{c.Text}" : "";

    Button? TesteBotaoNoPonto(Point naTela) => TesteFolhaNoPonto(naTela) as Button;

    Control TesteFolhaNoPonto(Point naTela)
    {
        Control atual = this;
        while (atual.GetChildAtPoint(atual.PointToClient(naTela), GetChildAtPointSkip.Invisible) is { } filho) atual = filho;
        return atual;
    }

    /// <summary>Dispara o Click do botão sem mover o mouse do usuário (o OnClick é o mesmo que o clique de verdade chama).</summary>
    static void TesteClicar(Button botao)
    {
        Log.Info($"teste: clicando \"{botao.Text}\".");
        typeof(Control).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(botao, new object[] { EventArgs.Empty });
    }

    JsonArray TesteQuemAssisto() =>
        new(_links.Values.Where(l => !l.IAmSender).Select(l => (JsonNode)JsonValue.Create(l.PeerNick)!).ToArray());
}
