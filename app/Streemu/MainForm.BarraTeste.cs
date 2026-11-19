using System.Reflection;
using System.Text.Json.Nodes;
using Streemu.Ui;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// STREEMU_TEST_VIEW=encerrar (com STREEMU_TEST_DIR): fotografa a barra lateral aberta e recolhida com a linha "Você está ao vivo" + "Encerrar live" e clica
/// em "Encerrar live" pelo mesmo OnMouseClick do mouse; grava barra-encerrar.json. Não transmite nada: a linha é montada por SetData.
/// </summary>
public sealed partial class MainForm
{
    bool _testeSemJanelaTransmitir; // o teste clica em "Transmitir" da barra sem abrir a preparação

    async Task TesteEncerrarBarraAsync()
    {
        _testeSemJanelaTransmitir = true;
        Show();
        WindowState = FormWindowState.Normal;
        Size = new Size(1100, 760);
        await Task.Delay(1500);
        int cliques = 0, cliquesTransmitir = 0;
        _side.StopLiveClicked += () => cliques++;
        _side.TransmitClicked += () => cliquesTransmitir++; // a linha Transmitir/Sua transmissão da interface Streemu
        FotografarBarra("barra-1.png");
        bool clicouAberta = ClicarNaLinha("StopLive");
        ToggleSidebar();
        await Task.Delay(900);
        FotografarBarra("barra-2.png");
        bool clicouRecolhida = ClicarNaLinha("StopLive");
        FotografarBarra("barra-3.png", aoVivo: false); // sem live: a linha "Transmitir" no lugar de "Você está ao vivo"
        bool transmitirA = ClicarNaLinha("Transmit");
        ToggleSidebar();
        await Task.Delay(900);
        FotografarBarra("barra-4.png", aoVivo: false);
        bool transmitirB = ClicarNaLinha("Transmit");
        var r = new JsonObject { ["achou_linha_aberta"] = clicouAberta, ["achou_linha_recolhida"] = clicouRecolhida, ["cliques_recebidos"] = cliques,
            ["achou_transmitir"] = transmitirA && transmitirB, ["cliques_transmitir"] = cliquesTransmitir };
        File.WriteAllText(Path.Combine(TestDir!, "barra-encerrar.json"), r.ToJsonString());
        ExitApp();
    }

    /// <summary>STREEMU_TEST_FOTO=&lt;arquivo.png&gt;: 3 s depois de abrir (já na tela de STREEMU_TEST_VIEW e no tamanho de STREEMU_TEST_SIZE), fotografa a janela principal e fecha.</summary>
    async Task TesteFotoDaJanelaAsync(string arquivo)
    {
        Show();
        WindowState = FormWindowState.Normal;
        int espera = int.TryParse(Environment.GetEnvironmentVariable("STREEMU_TEST_FOTO_ESPERA"), out var segundos) && segundos > 0 ? segundos : 3; // com a live de teste no ar a foto precisa esperar ela começar
        await Task.Delay(espera * 1000);
        if (Environment.GetEnvironmentVariable("STREEMU_TEST_ROLAR") == "1") { RolarPreparacaoAteOFim(); await Task.Delay(500); } // para ver o fim do painel Transmitir
        using var foto = new Bitmap(Width, Height);
        DrawToBitmap(foto, new Rectangle(0, 0, Width, Height));
        foto.Save(arquivo);
        ExitApp();
    }

    /// <summary>
    /// Teste: rola até o fim, por reflexão (os campos são privados), o painel Transmitir (a lista, as opções e a privacidade) ou, nas Configurações, a seção
    /// aberta (para ver o fim da seção Transmissão, que passa da altura da janela).
    /// </summary>
    void RolarPreparacaoAteOFim()
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object? painel = _settingsView ? _settingsPage : typeof(MainForm).GetField("_preparation", flags)?.GetValue(this);
        if (painel?.GetType().GetField("_body", flags)?.GetValue(painel) is Panel corpo) corpo.AutoScrollPosition = new Point(0, 100000);
    }

    void FotografarBarra(string arquivo, bool aoVivo = true)
    {
        _side.SetData(Array.Empty<SideFriend>(), 0, SideView.Home, "", aoVivo ? new SideSelf("Jogo de teste", 2, false) : null);
        using var foto = new Bitmap(_side.Width, _side.Height);
        _side.DrawToBitmap(foto, new Rectangle(0, 0, _side.Width, _side.Height));
        foto.Save(Path.Combine(TestDir!, arquivo));
    }

    /// <summary>Clica no meio da linha pedida, como o mouse (OnMouseClick do painel). Falso se a linha não existe.</summary>
    bool ClicarNaLinha(string tipo)
    {
        var linhas = (System.Collections.IEnumerable)typeof(SidePanel).GetField("_rows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_side)!;
        foreach (var linha in linhas)
        {
            var t = linha.GetType();
            if (t.GetProperty("Kind")!.GetValue(linha)!.ToString() != tipo) continue;
            var caixa = (Rectangle)t.GetProperty("Bounds")!.GetValue(linha)!;
            typeof(SidePanel).GetMethod("OnMouseClick", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(_side, new object[] { new MouseEventArgs(MouseButtons.Left, 1, caixa.X + caixa.Width / 2, caixa.Y + caixa.Height / 2, 0) });
            return true;
        }
        return false;
    }
}
