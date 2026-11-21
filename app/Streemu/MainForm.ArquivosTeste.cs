using System.Text.Json;
using System.Text.Json.Nodes;
using Streemu.Arquivos;
using Streemu.Net;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// Teste automático do envio de arquivo pela sala (spec 020, T5), com STREEMU_TEST_ROOM=1 e STREEMU_TEST_ARQUIVO=1. Faz pelas
/// mesmas ações da tela (OferecerArquivosAsync, AceitarPedidoArquivoAsync...), sem mouse, e grava &lt;papel&gt;-arquivo.json com os
/// pedidos e o histórico. Variáveis (docs/VARIAVEIS-DE-TESTE.md): STREEMU_TEST_ARQUIVO_ENVIAR (caminhos separados por |: este
/// papel manda), _PARA (apelido, padrão teste-B), _RESPOSTA (aceitar | recusar | nenhuma), _PASTA, _RELAY_RESPOSTA (sim | nao),
/// _FECHAR (%: quem recebe fecha no meio), _REENVIAR=1, _DE_NOVO (mandar | mudou), _NOME_FALSO, _ESPERA_S, _LIVE_SAINDO=1.
/// </summary>
public partial class MainForm
{
    static string? VarTeste(string nome) => Environment.GetEnvironmentVariable("STREEMU_TEST_ARQUIVO_" + nome) is { Length: > 0 } v ? v : null;

    static bool TesteArquivoAtivo => TestRoom && Environment.GetEnvironmentVariable("STREEMU_TEST_ARQUIVO") == "1";
    static readonly string? TesteArquivoNomeFalso = VarTeste("NOME_FALSO");
    /// <summary>_LIVE_SAINDO=1: quem manda age como se tivesse live saindo, sem transmitir nada (arquivo-com-live).</summary>
    static bool TesteArquivoLiveSaindo => TesteArquivoAtivo && VarTeste("LIVE_SAINDO") == "1"; // propriedade: um campo estático pode ser lido antes do TestRoom (outro arquivo parcial)

    /// <summary>
    /// STREEMU_TEST_ARQUIVO_FOTO=1: fotografa as telas (T6) em &lt;papel&gt;-&lt;nome&gt;.png. A janela vai para fora da tela em 1280x800
    /// (como a do filme no teste): ninguém vê nada abrir (regra 2).
    /// </summary>
    async Task FotoArquivoAsync(string nome)
    {
        if (VarTeste("FOTO") != "1") return;
        if (WindowState != FormWindowState.Normal || Left > -3000)
        {
            StartPosition = FormStartPosition.Manual; WindowState = FormWindowState.Normal;
            Bounds = new Rectangle(-4000, 40, 1280, 800); Show();
        }
        _arquivosSujo = true; AtualizarTelaArquivos();
        await Task.Delay(800);
        using var foto = new Bitmap(Width, Height);
        DrawToBitmap(foto, new Rectangle(0, 0, Width, Height));
        foto.Save(Path.Combine(TestDir!, $"{TestRole}-{nome}.png"));
    }

    async Task TesteArquivoAsync()
    {
        var espera = TimeSpan.FromSeconds(int.TryParse(VarTeste("ESPERA_S"), out var s) ? s : 90);
        if (VarTeste("PASTA") is string pasta && ArquivosLigado) HistoricoArquivos.PastaRecebidos = pasta;
        var amigo = await TestePrivadaEsperarAmigoAsync(VarTeste("PARA") ?? (TestRole == "A" ? "teste-B" : "teste-A"));
        if (amigo == null) Log.Info("teste-arquivo: o amigo não apareceu na sala.");
        else if (VarTeste("ENVIAR") is string caminhos) await TesteArquivoMandarAsync(amigo.Id, caminhos.Split('|'), DateTime.Now + espera);
        else await TesteArquivoReceberAsync(DateTime.Now + espera);
        await TesteEsperarTelemetriaAsync(); // MainForm.Telemetria.cs
        File.WriteAllText(Path.Combine(TestDir!, TestRole + "-arquivo.json"), RelatorioArquivoTeste().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // ------------------------------------------------------------------ quem manda

    /// <summary>O amigo a quem mando já esteve fora da sala (para reconhecer a volta dele com o mesmo id de conta).</summary>
    bool _testeArquivoAmigoSaiu;

    async Task TesteArquivoMandarAsync(string amigoId, string[] caminhos, DateTime ate)
    {
        if (VarTeste("FOTO") == "1") { MostrarArquivos(amigoId); _arquivosTela?.AdicionarArquivos(caminhos); await FotoArquivoAsync("1-enviar"); _arquivosTela?.LimparEscolhidos(); }
        if (await OferecerArquivosAsync(amigoId, caminhos) is string erro) { Log.Info($"teste-arquivo: a oferta não saiu: {erro}"); return; }
        await FotoArquivoAsync("2-esperando");
        bool reenviou = false;
        _testeArquivoAmigoSaiu = false;
        while (DateTime.Now < ate)
        {
            if (!(_room?.IsPresent(amigoId) ?? false)) _testeArquivoAmigoSaiu = true; // a saída pode vir e passar antes de a ligação cair e o pedido terminar
            await TesteArquivoAtenderDeNovoAsync(caminhos[0]);
            if (!reenviou && VarTeste("REENVIAR") == "1" && PedidosArquivo.Any(p => p.Terminado && p.Fase != FaseArquivo.Conferido))
            {
                reenviou = true;
                await TesteArquivoReenviarAsync(caminhos, amigoId, ate);
            }
            if (TesteArquivoMandouTudo(reenviou)) break;
            await Task.Delay(200);
        }
        await FotoArquivoAsync("3-fim");
    }

    /// <summary>O amigo pediu de novo: "mandar" atende; "mudou" muda o arquivo antes (D6: tem que recusar com o motivo).</summary>
    async Task TesteArquivoAtenderDeNovoAsync(string caminho)
    {
        foreach (var p in PedidosArquivo.Where(p => p.Fase == FaseArquivo.PedidoDeNovo))
        {
            if (VarTeste("DE_NOVO") == "mudou") File.AppendAllText(caminho, "mudou");
            if (await MandarDeNovoAsync(p.Pedido) is string erro) Log.Info($"teste-arquivo: mandar de novo: {erro}");
        }
    }

    /// <summary>
    /// Quem recebe fechou no meio e voltou: oferece os mesmos arquivos de novo, que continuam de onde pararam. Com conta o id é o da conta e não muda, então a volta
    /// se reconhece por "saiu da sala e está de novo nela" (<see cref="ISala.IsPresent"/>, sem a folga de 10 s da lista de amigos), e não por um id novo.
    /// </summary>
    async Task TesteArquivoReenviarAsync(string[] caminhos, string amigoId, DateTime ate)
    {
        bool voltou = false;
        while (DateTime.Now < ate)
        {
            bool aqui = _room?.IsPresent(amigoId) ?? false;
            if (!aqui) _testeArquivoAmigoSaiu = true;
            else if (_testeArquivoAmigoSaiu) { voltou = true; break; }
            await Task.Delay(200);
        }
        if (!voltou) { Log.Info("teste-arquivo: quem recebe não voltou."); return; }
        if (await OferecerArquivosAsync(amigoId, caminhos) is string erro) Log.Info($"teste-arquivo: a segunda oferta não saiu: {erro}");
    }

    /// <summary>Acabou quando nada está em curso e, se o teste pede um "pedir de novo", ele já veio e terminou.</summary>
    bool TesteArquivoMandouTudo(bool reenviou)
    {
        var pedidos = PedidosArquivo;
        if (pedidos.Count == 0 || pedidos.Any(p => !p.Terminado)) return false;
        if (VarTeste("REENVIAR") == "1" && !reenviou) return false;
        if (VarTeste("REENVIAR") == "1" && pedidos.All(p => p.Fase != FaseArquivo.Conferido)) return false;
        return VarTeste("DE_NOVO") == null || pedidos.Count >= 2;
    }

    // ------------------------------------------------------------------ quem recebe

    async Task TesteArquivoReceberAsync(DateTime ate)
    {
        bool pediuDeNovo = false;
        while (DateTime.Now < ate)
        {
            await TesteArquivoResponderAsync();
            TesteArquivoFecharNoMeio();
            if (!pediuDeNovo && VarTeste("DE_NOVO") != null && HistoricoArquivos.Itens.FirstOrDefault(i => i.Resultado == Resultado.Conferido) is { } item)
            {
                pediuDeNovo = true;
                if (await PedirArquivoDeNovoAsync(item) is string erro) Log.Info($"teste-arquivo: pedir de novo: {erro}");
            }
            var pedidos = PedidosArquivo;
            if (pedidos.Count > 0 && pedidos.All(p => p.Terminado) && (VarTeste("DE_NOVO") == null || pediuDeNovo)) break;
            await Task.Delay(200);
        }
        if (VarTeste("FOTO") != "1" || !ArquivosLigado) return;
        MostrarArquivos(); await FotoArquivoAsync("3-andamento");
        _arquivosTela?.AbrirAba(1); await FotoArquivoAsync("4-historico");
    }

    async Task TesteArquivoResponderAsync()
    {
        foreach (var p in PedidosArquivo.Where(p => p.Sentido == Sentido.Recebido))
        {
            // o card no Início, antes de responder (mockup 2 e o aviso de cota da D1)
            if (TextoDoPedido.EsperaPorMim(p) && VarTeste("FOTO") == "1") { ShowView(stage: false); await FotoArquivoAsync(p.Fase == FaseArquivo.ConfirmandoRelay ? "2-relay" : "1-pedido"); }
            if (p.Fase == FaseArquivo.ConfirmandoRelay) ResponderRelayArquivo(p.Pedido, VarTeste("RELAY_RESPOSTA") != "nao");
            if (p.Fase != FaseArquivo.Esperando) continue;
            string resposta = VarTeste("RESPOSTA") ?? "aceitar";
            if (resposta == "recusar") RecusarPedidoArquivo(p.Pedido);
            else if (resposta == "aceitar" && await AceitarPedidoArquivoAsync(p.Pedido) is string erro) Log.Info($"teste-arquivo: aceitar: {erro}");
        }
    }

    /// <summary>Como o "fechar no meio" do Cinema: sai na hora, sem salvar nada além do que o download já salvou.</summary>
    void TesteArquivoFecharNoMeio()
    {
        if (!int.TryParse(VarTeste("FECHAR"), out int pct)) return;
        if (PedidosArquivo.FirstOrDefault(p => p.Fase == FaseArquivo.Transferindo)?.Progresso is { Fase: "baixando" } pr && pr.Pct >= pct)
        {
            Log.Info($"teste-arquivo: fechando o app no meio do recebimento ({pr.Pct}%).");
            Environment.Exit(0);
        }
    }

    // ------------------------------------------------------------------ relatório

    JsonObject RelatorioArquivoTeste() => new()
    {
        ["ligado"] = ArquivosLigado,
        ["pasta"] = ArquivosLigado ? HistoricoArquivos.PastaRecebidos : null,
        ["pedidos"] = new JsonArray(PedidosArquivo.Select(p => (JsonNode)new JsonObject
        {
            ["pedido"] = p.Pedido, ["sentido"] = p.Sentido.ToString(), ["fase"] = p.Fase.ToString(), ["motivo"] = p.Motivo,
            ["segundos"] = p.TerminouEm is { } fim ? Math.Round((fim - p.Criado).TotalSeconds, 1) : null,
            ["refeitos"] = p.PedacosRefeitos, ["arquivos"] = p.Oferta.Arquivos.Count,
            ["pelo_relay"] = p.PeloRelay, ["perguntou_relay"] = p.PerguntouEm != null,
            ["caminhos"] = new JsonArray(p.Caminhos.Values.Select(c => (JsonNode)c).ToArray()),
        }).ToArray()),
        ["historico"] = ArquivosLigado ? new JsonArray(HistoricoArquivos.Itens.Select(i => (JsonNode)new JsonObject
        {
            ["sentido"] = i.Sentido.ToString(), ["nome"] = i.Nome, ["resultado"] = i.Resultado.ToString(), ["caminho"] = i.Caminho,
        }).ToArray()) : null,
    };
}
