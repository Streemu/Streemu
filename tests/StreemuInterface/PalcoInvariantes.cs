using Streemu;
using Streemu.Media;
using Streemu.Ui.Multistream;
using Streemu.Util;

/// <summary>
/// O que tem que valer no palco em QUALQUER estado (quantas lives, tamanho, escala, barra lateral, destaque, proporção do vídeo, telas): é o que a bateria de
/// estresse confere depois de cada passo. Cada regra devolve frases com o valor encontrado e o esperado.
/// Uso: <c>problems.AddRange(PalcoInvariantes.Check(form, stage, "4 lives 760x440"));</c>
/// </summary>
static class PalcoInvariantes
{
    /// <summary>Menor vídeo que ainda dá para assistir (largura em pixels de 96 DPI): abaixo disso o palco "quebrou" para quem olha.</summary>
    public const int MinVideoWidthAt96 = 120;
    /// <summary>Largura par e contas inteiras tiram até ~4 px de um vídeo em pé (1 px de largura vira 1,8 de altura): a régua do mínimo tem essa folga.</summary>
    const int RoundingSlackPx = 6;
    const int AspectSlackPx = 3;  // largura e altura saem pares (& ~1) e arredondadas

    /// <summary>Prefixo do que a bateria só avisa: o desenho atual não resolve (ver o comentário em <see cref="Shown"/>), e falhar esconderia regressões novas.</summary>
    public const string KnownLimit = "limite conhecido: ";

    public static IEnumerable<string> Check(MainForm form, PlayerStage stage, string label)
    {
        var managed = UiProbe.Field<List<Viewer>>(stage, "_tiles").Where(v => !UiProbe.Field<HashSet<Viewer>>(stage, "_small").Contains(v)).ToList();
        var canvas = UiProbe.Field<StageCanvas>(form, "_stageCanvas");
        var captions = UiProbe.Field<IReadOnlyList<StageCaption>>(canvas, "_captions");
        var problems = form.StageShown && form.VideoArea is { } area
            ? Shown(form, stage, managed, captions, area).ToList()
            : Parked(stage, managed, captions).ToList();
        problems.AddRange(Paints(canvas));
        return problems.Select(p => $"{label}: {p}");
    }

    /// <summary>Palco escondido (Início, Configurações): os vídeos ficam fora da tela, tocando, e não sobra faixa de nome.</summary>
    static IEnumerable<string> Parked(PlayerStage stage, List<Viewer> managed, IReadOnlyList<StageCaption> captions)
    {
        if (captions.Count > 0) yield return $"{captions.Count} faixa(s) de nome com o palco escondido (esperado: nenhuma)";
        foreach (var v in managed)
            if (stage.TargetOf(v) is { } t && t.X > -20000) yield return $"vídeo em {t} com o palco escondido (esperado: estacionado fora da tela)";
    }

    static IEnumerable<string> Shown(MainForm form, PlayerStage stage, List<Viewer> managed, IReadOnlyList<StageCaption> captions, Rectangle area)
    {
        var videos = managed.Select(v => (Viewer: v, Rect: stage.TargetOf(v))).ToList();
        bool mixed = managed.Select(v => Math.Round(v.VideoAspect ?? TileLayout.Aspect, 2)).Distinct().Count() > 1;
        foreach (var (v, rect) in videos)
        {
            if (rect is not { } r) { yield return "live do palco sem lugar"; continue; }
            foreach (var p in OneVideo(form, v, r, area))
                // a grade tem peças iguais: numa mistura muito diferente (em pé e 32:9) numa área estreita, nenhuma proporção de peça serve a todos
                yield return mixed ? KnownLimit + p : p;
        }
        foreach (var p in Captions(form, managed.Count, captions, area)) yield return p;
        foreach (var p in NoOverlap(videos.Where(x => x.Rect != null).Select(x => x.Rect!.Value).Concat(captions.Select(c => c.Area)).ToList())) yield return p;
        foreach (var p in Header(stage, managed)) yield return p;
    }

    static IEnumerable<string> OneVideo(MainForm form, Viewer viewer, Rectangle r, Rectangle area)
    {
        if (!area.Contains(r)) yield return $"vídeo {r} sai da área do palco {area}";
        double aspect = viewer.VideoAspect ?? TileLayout.Aspect;
        int expectedHeight = (int)(r.Width / aspect);
        if (r.Width > 0 && Math.Abs(expectedHeight - r.Height) > AspectSlackPx && Math.Abs((int)(r.Height * aspect) - r.Width) > AspectSlackPx)
            yield return $"vídeo {r.Size} esticado (proporção {aspect:0.00}:1, esperado ~{r.Width}x{expectedHeight})";
        // assistível: lado maior >= 120 dp e menor >= 9/16 disso, em qualquer orientação; depende do monitor (DPI), não do Ctrl + da interface
        int minLong = (int)(MinVideoWidthAt96 * form.DeviceDpi / 96f), minShort = minLong * 9 / 16;
        int longSide = Math.Max(r.Width, r.Height), shortSide = Math.Min(r.Width, r.Height);
        if (longSide < minLong - RoundingSlackPx || shortSide < minShort - RoundingSlackPx)
            yield return $"vídeo de {r.Width}x{r.Height} em {area.Size} (mínimo assistível: {minLong}x{minShort} em qualquer orientação)";
        var bar = Streemu.Ui.PlayerOverlay.BarRect(r, form.DeviceDpi);
        if (!r.Contains(bar)) yield return $"barra de controles {bar} sai do vídeo {r}: passa por cima da faixa do nome";
    }

    /// <summary>Com 2+ lives, uma faixa de nome por vídeo, dentro do palco e larga o bastante para a pílula AO VIVO não cobrir a foto.</summary>
    static IEnumerable<string> Captions(MainForm form, int lives, IReadOnlyList<StageCaption> captions, Rectangle area)
    {
        int expected = lives >= 2 ? lives : 0;
        if (captions.Count != expected) yield return $"{captions.Count} faixa(s) de nome para {lives} live(s) (esperado: {expected})";
        foreach (var c in captions)
        {
            if (!area.Contains(c.Area)) yield return $"faixa de nome {c.Area} sai da área do palco {area}";
            var parts = LiveCaptionPainter.Parts(c.Area, form.Dp);
            if (parts.Avatar.IntersectsWith(parts.Pill)) yield return $"faixa de nome com {c.Area.Width} px: a pílula AO VIVO cobre a foto";
            int name = form.Dp(LiveCaptionPainter.MinNameAt96);
            if (parts.Text.Width < name) yield return $"faixa de nome com {c.Area.Width} px: sobram {parts.Text.Width} px para o nome (mínimo {name} px)";
        }
    }

    static IEnumerable<string> NoOverlap(List<Rectangle> rects)
    {
        for (int i = 0; i < rects.Count; i++)
            for (int j = i + 1; j < rects.Count; j++)
                if (rects[i].IntersectsWith(rects[j])) yield return $"{rects[i]} e {rects[j]} se cobrem";
    }

    static IEnumerable<string> Header(PlayerStage stage, List<Viewer> managed)
    {
        if (managed.Count == 1 && stage.HeaderOver(managed[0]) is not { } h) yield return "live sozinha sem cabeçalho por cima do vídeo";
        if (managed.Count >= 2 && managed.Any(v => stage.HeaderOver(v) != null)) yield return "com várias lives um cabeçalho foi por cima do vídeo";
    }

    static IEnumerable<string> Paints(Control canvas)
    {
        string? error = null;
        try { using var _ = UiProbe.Photo(canvas); }
        catch (Exception ex) { error = $"o palco falhou ao desenhar: {ex.GetType().Name}: {ex.Message}"; }
        if (error != null) yield return error;
    }
}
