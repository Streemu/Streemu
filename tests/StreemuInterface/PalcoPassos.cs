using Streemu;
using Streemu.Media;
using Streemu.Ui.Multistream;
using Streemu.Util;

/// <summary>
/// Os passos que alguém faz com lives abertas, para a bateria de estresse sortear: cada um mexe na janela de verdade (pelos mesmos métodos que os cliques
/// chamam) e devolve o detalhe do que fez, para a falha dizer o caminho.
/// Uso: <c>var (nome, fazer) = PalcoPassos.All(form, stage)[i]; string detalhe = fazer(random);</c>
/// </summary>
static class PalcoPassos
{
    static readonly double[] Aspects = [16.0 / 9, 21.0 / 9, 4.0 / 3, 9.0 / 16, 32.0 / 9, 1.0];
    static readonly double[] Scales = [0.8, 1.0, 1.25, 1.5, 1.75];

    public static List<(string Name, Func<Random, string> Act)> All(MainForm form, PlayerStage stage) =>
    [
        ("abrir live", r => Open(form, stage, r)),
        ("abrir live", r => Open(form, stage, r)), // abrir pesa o dobro: senão o palco passa a maior parte do tempo vazio
        ("fechar live", r => Close(form, stage, r)),
        ("destaque", r => Pick(stage, r) is { } v ? Do(() => stage.ToggleEmphasis(v), "") : " (sem live)"),
        ("trocar de lugar", r => Swap(stage, r)),
        ("redimensionar", r => Resize(form, r)),
        ("barra lateral", _ => Do(form.ToggleSidebar, "")),
        ("configurações", _ => Do(() => UiProbe.Call(form, "ShowSettings"), "")),
        ("palco", _ => Do(() => UiProbe.Call(form, "ShowView", true), "")),
        ("início", _ => Do(() => UiProbe.Call(form, "ShowView", false), "")),
        ("transmitir em coluna", _ => Do(() => UiProbe.Call(form, "OpenGoLive", false, null!), "")),
        ("voltar do transmitir", _ => Do(() => UiProbe.Call(form, "ReturnFromPreparation"), "")),
        ("janela pequena", r => Small(stage, r)),
        ("escala", r => Scale(form, r)),
        ("tema", _ => Do(() => ThemeSwitch.Set(form, !Theme.IsLight), Theme.IsLight ? " → escuro" : " → claro")),
        ("proporção nova", r => NewAspect(stage, r)),
    ];

    static string Do(Action act, string detail) { act(); return detail; }

    static List<Viewer> Tiles(PlayerStage stage) => UiProbe.Field<List<Viewer>>(stage, "_tiles");

    static Viewer? Pick(PlayerStage stage, Random r) => Tiles(stage) is { Count: > 0 } t ? t[r.Next(t.Count)] : null;

    static string Open(MainForm form, PlayerStage stage, Random r)
    {
        if (stage.Full) return " (palco cheio)";
        var viewer = new Viewer();
        double aspect = Aspects[r.Next(Aspects.Length)];
        typeof(Viewer).GetProperty(nameof(Viewer.VideoAspect))!.SetValue(viewer, aspect);
        int n = stage.Count;
        stage.Add(viewer, () => $"Amigo {n} · Jogo · ao vivo há {n} min");
        form.StreamsChanged(stage.Count);
        return $" {aspect:0.00}:1, agora {stage.Count}";
    }

    static string Close(MainForm form, PlayerStage stage, Random r)
    {
        if (Pick(stage, r) is not { } v) return " (sem live)";
        stage.Remove(v);
        form.StreamsChanged(stage.Count);
        return $", agora {stage.Count}";
    }

    /// <summary>Arrasta a faixa de nome de uma live e solta sobre outra (o que o mouse faz no palco).</summary>
    static string Swap(PlayerStage stage, Random r)
    {
        var cells = UiProbe.Field<List<Rectangle>>(stage, "_cells");
        if (cells.Count < 2) return " (menos de 2 lives)";
        int from = r.Next(cells.Count), to = (from + 1 + r.Next(cells.Count - 1)) % cells.Count;
        var target = new Point(cells[to].X + cells[to].Width / 2, cells[to].Y + cells[to].Height / 2);
        UiProbe.Call(stage, "OnCaptionDragged", from, target, true);
        return $" {from} → {to}";
    }

    static string Resize(MainForm form, Random r)
    {
        form.Size = new Size(r.Next(400, 2700), r.Next(300, 1500));
        return $" {form.Size.Width}x{form.Size.Height}";
    }

    static string Small(PlayerStage stage, Random r)
    {
        if (Pick(stage, r) is not { } v) return " (sem live)";
        bool small = !UiProbe.Field<HashSet<Viewer>>(stage, "_small").Contains(v);
        stage.SetSmall(v, small);
        return small ? " (sai do palco)" : " (volta ao palco)";
    }

    static string Scale(MainForm form, Random r)
    {
        double scale = Scales[r.Next(Scales.Length)];
        UiProbe.Call(form, "ApplyUserScale", scale, false);
        return $" {scale:P0}";
    }

    /// <summary>O ffplay descobre a proporção do vídeo só depois de abrir, e o amigo pode trocar de jogo (outra resolução) no meio da live.</summary>
    static string NewAspect(PlayerStage stage, Random r)
    {
        if (Pick(stage, r) is not { } v) return " (sem live)";
        double aspect = Aspects[r.Next(Aspects.Length)];
        typeof(Viewer).GetProperty(nameof(Viewer.VideoAspect))!.SetValue(v, aspect);
        return $" {aspect:0.00}:1";
    }
}
