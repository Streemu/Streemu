using System.Text.Json.Nodes;
using Streemu.Util;

namespace Streemu.Filme;

/// <summary>
/// A interface do filme desenhada dentro do mpv (Filme/streemu-osc.lua) no lugar dos botões WinForms. Ligada para todos desde o 3º teste do
/// o grupo (11/11); STREEMU_MPV_UI=0 volta para os botões de antes (se a interface der problema no PC de alguém).
/// Os botões do script não mexem no filme: mandam "script-message streemu &lt;ação&gt;" (chega como <see cref="Mpv.Mensagem"/>) e a tela
/// decide como os botões dela decidiam (o anfitrião sincroniza com o grupo; quem assiste pede; quem não controla recebe o aviso).
/// O app manda o estado (<see cref="Mandar"/>) porque só ele sabe a posição do grupo e, no filme pela transmissão, a duração.
/// Um ModernX/uosc pronto não serve por isso: mexeria no mpv direto e tiraria a pessoa de sincronia.
/// </summary>
public static class MpvUi
{
    public static bool Ligada => Environment.GetEnvironmentVariable("STREEMU_MPV_UI") != "0";

    /// <summary>
    /// Opções que ligam o script. Vêm depois das do <see cref="Mpv.OpenAsync"/> e por isso valem por cima delas: o mpv passa a receber
    /// mouse e teclado, mas o --input-default-bindings=no continua, então só os atalhos do script fazem algo.
    /// </summary>
    public static IEnumerable<string> Opcoes()
    {
        string arq = Path.Combine(Path.GetTempPath(), "streemu-mpv-ui", "streemu-osc.lua");
        using var r = typeof(MpvUi).Assembly.GetManifestResourceStream("streemu-osc.lua") ?? throw new InvalidOperationException("a interface do mpv não está no exe");
        using var ms = new MemoryStream();
        r.CopyTo(ms);
        byte[] novo = ms.ToArray();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(arq)!);
            if (!File.Exists(arq) || !File.ReadAllBytes(arq).AsSpan().SequenceEqual(novo)) File.WriteAllBytes(arq, novo);
        }
        catch (IOException) when (File.Exists(arq)) { } // duas instâncias no mesmo PC (teste) gravando ao mesmo tempo: a outra já gravou
        // --window-dragging=no: com ele ligado, o clique esquerdo no mpv vira "arrastar a janela" (no embutido, a janela do mpv dentro do app)
        return new[] { $"--script={arq}", "--input-cursor=yes", "--input-vo-keyboard=yes", "--cursor-autohide=1500", "--window-dragging=no" };
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr pai, IntPtr depois, string classe, string? titulo);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool IsWindowEnabled(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool EnableWindow(IntPtr h, bool ligar);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SetFocus(IntPtr h);

    public static void Focar(IntPtr pai)
    {
        LigarMouse(pai);
        IntPtr h = FindWindowEx(pai, IntPtr.Zero, "mpv", null);
        if (h != IntPtr.Zero) SetFocus(h);
    }

    /// <summary>
    /// Religa o mouse na janela do mpv dentro de <paramref name="pai"/>. Embutido (--wid), o mpv cria a janela dele desabilitada (WS_DISABLED)
    /// para o app de fora receber o mouse, e o Windows não entrega clique a janela desabilitada: no 2º teste (11/11) nenhum clique
    /// chegava ao script, só o teclado. Chamado a cada atualização da tela porque o mpv pode recriar a janela (loadfile do filme pela transmissão).
    /// </summary>
    public static void LigarMouse(IntPtr pai)
    {
        if (pai == IntPtr.Zero) return;
        IntPtr h = FindWindowEx(pai, IntPtr.Zero, "mpv", null);
        if (h == IntPtr.Zero || IsWindowEnabled(h)) return;
        EnableWindow(h, true);
        Log.Info("[filme] interface do mpv: janela do mpv habilitada para receber o mouse.");
    }

    /// <summary>Uma linha de "Na sessão": nome, papel embaixo dele ("Anfitrião", "Você"), estado colorido à direita ou, com <paramref name="Junto"/>, depois do papel.</summary>
    /// <param name="Tom">"ok" (verde), "espera" (amarelo), "erro" (vermelho) ou "" (texto normal).</param>
    public sealed record Pessoa(string Nome, string? Papel, string? Estado, string Tom = "", bool Junto = false);

    /// <summary>O que o player mostra. <paramref name="Dur"/> nulo = o script lê a duração do mpv (arquivo local).</summary>
    /// <param name="Linha">Embaixo do título: o papel ("Com Lucas", "Você é o anfitrião").</param>
    /// <param name="Estado">Terceira linha (quem controla, quem falta, um aviso); <paramref name="EstadoTom"/> "espera" ganha o relógio.</param>
    public sealed record EstadoTela(double Pos, double? Dur, bool Tocando, bool Pode, string Titulo, string Linha, string Estado, string EstadoTom, bool Cheia, IReadOnlyList<Pessoa> Pessoas);

    /// <summary>
    /// Estado para o script desenhar, com as cores do <see cref="Theme"/> (a paleta muda num lugar só). Quem está na sessão vai no canto
    /// de cima, dentro do vídeo (antes era um painel ao lado, que encolhia o vídeo ao pausar).
    /// </summary>
    /// <param name="extra">Campos a mais (o anfitrião manda "anfitriao", "aguardando", "todos", "transmitir", "esperar", "esperarVisivel"; quem assiste sem baixar, "buffer").</param>
    public static void Mandar(Mpv mpv, EstadoTela e, JsonObject? extra = null)
    {
        if (mpv.Exited) return;
        var o = new JsonObject
        {
            ["pos"] = double.IsFinite(e.Pos) ? Math.Round(e.Pos, 3) : 0, ["dur"] = e.Dur is double d && double.IsFinite(d) && d > 0 ? Math.Round(d, 3) : null,
            ["tocando"] = e.Tocando, ["pode"] = e.Pode, ["titulo"] = e.Titulo, ["linha"] = e.Linha, ["estado"] = e.Estado, ["estadoTom"] = e.EstadoTom,
            ["cheia"] = e.Cheia, ["pessoas"] = new JsonArray(e.Pessoas.Select(p => (JsonNode?)PessoaJson(p)).ToArray()), ["tema"] = Tema(),
        };
        if (extra != null) foreach (var kv in extra) o[kv.Key] = kv.Value?.DeepClone();
        _ = mpv.Cmd("script-message", "streemu-estado", o.ToJsonString());
    }

    // a inicial e a cor de cada pessoa são as dos avatares do app (RosterStrip), para a mesma pessoa ter a mesma cara em todo lugar
    static JsonObject PessoaJson(Pessoa p) => new()
    {
        ["n"] = p.Nome, ["i"] = Ui.RosterStrip.Initial(p.Nome), ["c"] = Bgr(Ui.RosterStrip.ColorOf(p.Nome)),
        ["p"] = p.Papel, ["e"] = p.Estado, ["t"] = p.Tom, ["junto"] = p.Junto,
    };

    static JsonObject Tema() => new()
    {
        ["acento"] = Bgr(Theme.Accent), ["acentoClaro"] = Bgr(Theme.AccentHover), ["acentoTexto"] = Bgr(Theme.AccentText),
        ["texto"] = Bgr(Theme.Text), ["apagado"] = Bgr(Theme.TextMuted), ["painel"] = Bgr(Theme.SurfaceRaised), ["borda"] = Bgr(Theme.CardBorder),
        ["ok"] = Bgr(Theme.Ok), ["espera"] = Bgr(Theme.Warn), ["erro"] = Bgr(Theme.Error), ["avatar"] = Bgr(Theme.AccentSoft),
    };

    /// <summary>Cor no formato do ASS (azul, verde, vermelho em hexadecimal), que o script usa direto.</summary>
    static string Bgr(Color c) => $"{c.B:X2}{c.G:X2}{c.R:X2}";

    /// <summary>
    /// O estado de um amigo na sessão (os textos da <see cref="Sessao"/>: "pronto", "baixando 42% (faltam 3 min)", "conferindo 10%", ...) do
    /// jeito curto de "Na sessão": "Pronto", "Baixando 42%".
    /// </summary>
    public static (string Texto, string Tom) EstadoAmigo(string v)
    {
        string pct = System.Text.RegularExpressions.Regex.Match(v, @"\d+%").Value;
        if (v == "pronto") return ("Pronto", "ok");
        if (v.StartsWith("baixando", StringComparison.Ordinal)) return ($"Baixando {pct}".Trim(), "espera");
        if (v.StartsWith("conferindo", StringComparison.Ordinal)) return ($"Conferindo {pct}".Trim(), "espera");
        if (v.StartsWith("arquivo com", StringComparison.Ordinal)) return ("Arquivo com defeito", "erro");
        if (v == "outro arquivo") return ("Outro arquivo", "erro");
        return (v.Length == 0 ? "" : char.ToUpperInvariant(v[0]) + v[1..], "espera");
    }

    /// <summary>"Bruno", "Bruno e Ana", "3 pessoas": quem o anfitrião ainda espera.</summary>
    public static string Juntar(IReadOnlyList<string> nomes) => nomes.Count switch
    {
        0 => "",
        1 => nomes[0],
        2 => $"{nomes[0]} e {nomes[1]}",
        _ => $"{nomes.Count} pessoas",
    };

    /// <summary>
    /// A ação de um botão do script: "tocar", "pular" (± segundos), "ir" (posição), "cheia", "sair", "todos", "transmitir", "esperar" ou "tocar-negado". Nulo se não é do script ou se
    /// é só diagnóstico ("pronto" e "clique", que vão para o log: o 1º teste de 11/11 teve clique que não fazia nada e o log não dizia onde caiu).
    /// </summary>
    public static (string Acao, double? Valor)? Ler(string[] args)
    {
        if (args.Length < 2 || args[0] != "streemu") return null;
        if (args[1] == "pronto") { Log.Info("[filme] a interface do mpv respondeu."); return null; }
        if (args[1] == "clique") { Log.Info($"[filme] interface do mpv: clique em {string.Join(",", args.Skip(2).Take(2))} ({args.ElementAtOrDefault(4)})."); return null; }
        double? v = args.Length > 2 && double.TryParse(args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double x) && double.IsFinite(x) ? x : null;
        return (args[1], v);
    }
}
