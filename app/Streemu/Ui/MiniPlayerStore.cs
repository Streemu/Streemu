using Streemu.Util;

namespace Streemu.Ui;

/// <summary>Onde a janela pequena do player ficou da última vez (posição e tamanho em pixels de tela).</summary>
public interface IMiniPlayerStore
{
    /// <summary>A última posição guardada, ou nulo se nunca houve.</summary>
    Rectangle? Load();

    void Save(Rectangle bounds);
}

/// <summary>Guarda no estado do app (<c>estado.json</c>). Em teste automático não grava nada no disco.</summary>
public sealed class AppStateMiniStore : IMiniPlayerStore
{
    readonly AppState _state;
    readonly bool _persist;

    public AppStateMiniStore(AppState state, bool persist)
    {
        _state = state;
        _persist = persist;
    }

    public Rectangle? Load() => _state.MiniWidth > 0 && _state.MiniHeight > 0
        ? new Rectangle(_state.MiniX, _state.MiniY, _state.MiniWidth, _state.MiniHeight)
        : null;

    public void Save(Rectangle bounds)
    {
        _state.MiniX = bounds.X; _state.MiniY = bounds.Y; _state.MiniWidth = bounds.Width; _state.MiniHeight = bounds.Height;
        if (_persist) _state.Save();
    }
}

/// <summary>
/// Onde abrir a janela pequena: a última posição guardada (trazida de volta para a tela se aquele monitor sumiu) ou, na primeira vez,
/// o canto inferior direito da área útil do monitor com o mouse. Conta pura, separada para testar sem janela.
/// Uso: <c>var r = MiniPlayerPlacement.Choose(store.Load(), Screen.AllScreens.Select(s =&gt; s.WorkingArea), Cursor.Position, DeviceDpi / 96.0);</c>
/// </summary>
public static class MiniPlayerPlacement
{
    const int MarginPx = 24;
    /// <summary>Largura mínima em dip (96 DPI): é o que a barra de controles precisa para caber sem cortar o "%" do volume (3 botões + volume). Em pixels cortava na escala de 150%.</summary>
    const int MinWidthDip = 380, MaxWidthDip = 560;

    public static Rectangle Choose(Rectangle? saved, IEnumerable<Rectangle> workAreas, Point cursor, double scale = 1.0)
    {
        var areas = workAreas.ToList();
        if (areas.Count == 0) throw new ArgumentException("preciso de pelo menos uma área de tela; recebi 0", nameof(workAreas));
        if (saved is { } s && s.Width >= 160 && s.Height >= 90 && !LooksFullscreen(s, areas)) return Clamp(s, areas);
        var area = areas.FirstOrDefault(a => a.Contains(cursor));
        if (area.IsEmpty) area = areas[0];
        int min = (int)Math.Round(MinWidthDip * scale), max = (int)Math.Round(MaxWidthDip * scale);
        int width = Math.Clamp(area.Width / 4, min, Math.Max(min, Math.Min(max, area.Width - 2 * MarginPx)));
        int height = width * 9 / 16;
        return new Rectangle(area.Right - width - MarginPx, area.Bottom - height - MarginPx, width, height);
    }

    /// <summary>
    /// A janela cobre (quase) a largura de uma tela: é tela cheia, não "pequena". Guardar isso fazia TODA janela pequena abrir em tela
    /// cheia dali em diante (relato de 2026-11-07: pequena -> tela cheia -> botão de voltar gravava 1920x1080).
    /// </summary>
    public static bool LooksFullscreen(Rectangle r, IEnumerable<Rectangle> workAreas) =>
        workAreas.Any(a => Rectangle.Intersect(a, r) is { IsEmpty: false } i && i.Width >= a.Width * 0.75 && i.Height >= a.Height * 0.6);

    /// <summary>
    /// A janela pequena na proporção do vídeo que chega: mantém a largura (a barra de controles precisa dela) e o canto de baixo, e ajusta a altura.
    /// Uma live 21:9 numa janela pequena 16:9 sairia esticada (o ffplay na placa estica a imagem até preencher a janela).
    /// Uso: <c>r = MiniPlayerPlacement.MatchAspect(r, viewer.VideoAspect ?? 16.0 / 9);</c>
    /// </summary>
    public static Rectangle MatchAspect(Rectangle r, double aspect)
    {
        if (aspect <= 0 || r.Width <= 0) return r;
        int height = Math.Max(2, (int)Math.Round(r.Width / aspect) & ~1);
        return height == r.Height ? r : new Rectangle(r.X, r.Bottom - height, r.Width, height);
    }

    /// <summary>Se a janela guardada caiu fora de todas as telas (monitor desligado), leva para dentro da mais próxima.</summary>
    static Rectangle Clamp(Rectangle r, List<Rectangle> areas)
    {
        var best = areas.OrderByDescending(a => Rectangle.Intersect(a, r) is { IsEmpty: false } i ? (long)i.Width * i.Height : 0).First();
        int w = Math.Min(r.Width, best.Width), h = Math.Min(r.Height, best.Height);
        return new Rectangle(Math.Clamp(r.X, best.Left, best.Right - w), Math.Clamp(r.Y, best.Top, best.Bottom - h), w, h);
    }
}
