using Streemu.Media;
using Streemu.Util;

namespace Streemu.Ui.Multistream;

/// <summary>
/// A janela principal vista pelo palco (spec 006, janela única): onde os vídeos podem ficar, se o palco está à vista e o que fazer quando as streams
/// mudam. Quem implementa é a janela principal do app (<c>MainForm.Janela.cs</c>); o palco só conversa com esta interface.
/// </summary>
/// <summary>O cabeçalho de um vídeo: onde fica (pixels de tela), o texto ("Amigo · Jogo · ao vivo há 12 min") e o avatar de quem transmite (nulo = a inicial colorida).</summary>
public sealed record StageCaption(Rectangle Area, string Text, Streemu.Ui.Avatars.AvatarImage? Avatar);

public interface IStageHost
{
    /// <summary>Área (pixels de tela) onde os vídeos ficam. Nula se a janela está escondida ou minimizada: aí nada é mexido.</summary>
    Rectangle? VideoArea { get; }
    /// <summary>O palco está à vista? Falso com o início aberto: os vídeos ficam estacionados fora da tela, sem fechar.</summary>
    bool StageShown { get; }
    /// <summary>A janela dona dos vídeos (eles somem e minimizam com ela e ficam sempre por cima dela).</summary>
    IntPtr Handle { get; }
    bool SidebarOpen { get; }
    /// <summary>Pixels de 96 DPI convertidos para os reais desta janela (espaço entre vídeos, altura da legenda).</summary>
    int Dp(int at96);
    void StreamsChanged(int count);
    /// <summary>Legendas no alto de cada vídeo (áreas em pixels de tela).</summary>
    void ShowCaptions(IReadOnlyList<StageCaption> captions);
    void ToggleSidebar();
    void ToggleFullscreen();
    /// <summary>O palco ocupa o monitor (sem barra lateral nem moldura): os controles trocam "Tela cheia" por "Sair da tela cheia".</summary>
    bool Fullscreen { get; }
    /// <summary>O usuário arrasta a legenda de um vídeo (índice, posição na tela, soltou?): é a alça para trocar os vídeos de lugar.</summary>
    event Action<int, Point, bool>? CaptionDragged;
    /// <summary>Destaca a legenda que vai receber o vídeo arrastado (-1 limpa).</summary>
    void HighlightCaption(int index);
}

/// <summary>
/// A "janela de streams" (spec 006): os vídeos de todas as streams, em grade ou com uma em destaque, DENTRO da janela principal do app, ao lado da barra lateral.
/// Cada stream segue sendo um ffplay (ele não vira filho de outra janela, spec 002); aqui o app o torna DEPENDENTE da janela principal (some, minimiza e fica
/// por cima junto com ela), tira a moldura e põe cada um no retângulo que a <see cref="TileLayout"/> calculou: o maior possível, na proporção do vídeo (16:9 até o ffplay dizer), com espaço entre eles.
/// Com o início aberto os vídeos ficam estacionados fora da tela (continuam tocando). Na janela pequena o vídeo sai do palco e volta depois.
/// Uso: <c>stage = new PlayerStage(host); stage.Add(viewer, () =&gt; "Amigo · Jogo");</c> (thread da tela).
/// </summary>
public sealed class PlayerStage : IDisposable
{
    /// <summary>Primeira entrega do multistream: até 4 streams ao mesmo tempo (a conta serve para mais; o limite é de carga e de teste).</summary>
    public const int MaxStreams = 4;
    const int CaptionAt96 = 36, PadAt96 = 8; // 36: o cabeçalho do desenho (nome, jogo e a pílula AO VIVO) pede mais altura que a legenda de antes (22)
    static readonly Point Parked = new(-30000, -30000);

    readonly IStageHost _host;
    readonly List<Viewer> _tiles = new();
    readonly Dictionary<Viewer, Func<string?>> _captions = new();
    readonly Dictionary<Viewer, Func<Streemu.Ui.Avatars.AvatarImage?>> _avatars = new();
    readonly Dictionary<Viewer, IntPtr> _owned = new();
    readonly HashSet<Viewer> _small = new();
    readonly SynchronizationContext _ui = SynchronizationContext.Current ?? new SynchronizationContext();
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 250 };
    readonly StageEscape _escape;
    Viewer? _emphasized;         // a peça grande (nulo = grade igual para todas)
    Dictionary<Viewer, Rectangle> _targets = new();
    List<Rectangle> _cells = new(); // célula inteira (com a legenda) de cada peça gerida, na ordem de Managed

    public PlayerStage(IStageHost host)
    {
        _host = host;
        _escape = new StageEscape(() => { if (_host.Fullscreen) _host.ToggleFullscreen(); });
        _host.CaptionDragged += OnCaptionDragged;
        _tick.Tick += (_, _) => Relayout();
        _tick.Start();
    }

    public int Count => _tiles.Count;
    public bool Full => _tiles.Count >= MaxStreams;
    /// <summary>Com pelo menos uma stream, quem manda no tamanho dos vídeos é o palco.</summary>
    public bool Active => _tiles.Count >= 1;
    public bool PanelOpen => _host.SidebarOpen;

    /// <summary>
    /// A janela principal está minimizada ou escondida (bandeja)? Os vídeos são filhos dela e somem junto, mas o palco continua com as posições deles:
    /// quem desenha por cima do vídeo (barra, cabeçalho) precisa checar isto, senão flutua sobre a área de trabalho (2026-11-17; só olhar a janela do
    /// ffplay não bastava quando o handle dele ainda não tinha sido reencontrado).
    /// </summary>
    public bool HostHidden => Native.IsIconic(_host.Handle) || !Native.IsWindowVisible(_host.Handle);
    public bool IsEmphasized(Viewer viewer) => _emphasized == viewer;

    /// <summary>Acabou de pôr os vídeos no lugar (a barra de controles de cada um se realinha na hora, sem esperar o relógio dela).</summary>
    public event Action? Placed;

    /// <summary>
    /// Onde o vídeo deve estar agora. O ffplay muda de lugar de forma assíncrona (SWP_ASYNCWINDOWPOS), então ler a janela logo após mexer devolve a posição
    /// antiga: quem acompanha o vídeo (a barra de controles) usa este valor. Nulo se a peça está fora do palco (janela pequena).
    /// </summary>
    public Rectangle? TargetOf(Viewer viewer) => _targets.TryGetValue(viewer, out var r) ? r : null;

    /// <summary>Acrescenta a peça. <paramref name="caption"/> dá a legenda sobre o vídeo ("Amigo · Jogo · ao vivo há 12 min").</summary>
    public void Add(Viewer viewer, Func<string?>? caption = null, Func<Streemu.Ui.Avatars.AvatarImage?>? avatar = null)
    {
        if (_tiles.Contains(viewer)) return;
        if (caption != null) _captions[viewer] = caption;
        if (avatar != null) _avatars[viewer] = avatar;
        viewer.OpenAt = PlannedRect(_tiles.Count + 1, _tiles.Count);
        _tiles.Add(viewer);
        viewer.Exited += () => _ui.Post(_ => Remove(viewer), null);
        _host.StreamsChanged(_tiles.Count);
        Relayout();
    }

    public void Remove(Viewer viewer)
    {
        if (!_tiles.Remove(viewer)) return;
        _captions.Remove(viewer); _avatars.Remove(viewer); _owned.Remove(viewer); _small.Remove(viewer);
        if (_emphasized == viewer || _tiles.Count < 2) _emphasized = null;
        _host.StreamsChanged(_tiles.Count);
        Relayout();
    }

    /// <summary>Põe esta peça em destaque (grande, as outras menores ao lado ou embaixo); de novo nela, volta para a grade igual.</summary>
    public void ToggleEmphasis(Viewer viewer)
    {
        if (!_tiles.Contains(viewer) || _tiles.Count < 2) return;
        _emphasized = _emphasized == viewer ? null : viewer;
        Relayout();
    }

    public void TogglePanel() => _host.ToggleSidebar();
    public void OpenPanel() { if (!_host.SidebarOpen) _host.ToggleSidebar(); }
    public void ClosePanel() { if (_host.SidebarOpen) _host.ToggleSidebar(); }

    /// <summary>Tela cheia do palco inteiro (a tecla F do ffplay só serviria a uma peça): a janela principal ocupa a tela e a barra lateral some.</summary>
    public void ToggleFullscreen() => _host.ToggleFullscreen();
    public bool Fullscreen => _host.Fullscreen;

    /// <summary>
    /// A peça foi para a janela pequena (canto da tela, sempre por cima): o palco a solta e a deixa em paz até ela voltar. Solta também o vínculo com a
    /// janela principal, senão minimizar o app esconderia a janela pequena.
    /// </summary>
    public void SetSmall(Viewer viewer, bool small)
    {
        if (!_tiles.Contains(viewer)) return;
        if (small) { _small.Add(viewer); viewer.Window.SetOwner(IntPtr.Zero); _owned.Remove(viewer); }
        else _small.Remove(viewer);
        Relayout();
    }

    // ------------------------------------------------------------------ conta

    int Pad => _host.Dp(PadAt96);
    int CaptionHeight => _host.Dp(CaptionAt96);

    /// <summary>
    /// A folga em volta e a faixa de legenda só existem com 2 ou mais streams (é o que separa uma da outra). Com UMA stream o vídeo preenche o palco todo:
    /// antes a folga de 8 px e a legenda encolhiam a altura e, para manter o 16:9, o vídeo perdia ~50 px de cada lado numa tela 16:9 (visto em tela cheia, 2026-11-09).
    /// </summary>
    int PadFor(int count) => count >= 2 ? Pad : 0;
    /// <summary>
    /// Com 2 ou mais lives o cabeçalho (nome, jogo, AO VIVO) tem faixa própria acima de cada vídeo: diz quem é quem e é a alça para trocar de lugar. Com UMA
    /// o vídeo ocupa o palco inteiro e o cabeçalho vai por cima dele, sumindo junto com a barra de controles (<see cref="HeaderOver"/>; pedido de
    /// 2026-11-16: cabeçalho e barra fixos tiravam a imersão). A barra de controles também fica por cima do vídeo, sem faixa reservada embaixo.
    /// </summary>
    int CaptionFor(int count) => count >= 2 ? CaptionHeight : 0;

    /// <summary>
    /// O cabeçalho que vai POR CIMA do alto do vídeo (pixels de tela): só quando esta é a única live no palco. Nulo com 2 ou mais (o cabeçalho está na faixa)
    /// ou fora do palco (janela pequena). Ex.: <c>stage.HeaderOver(viewer)</c>.
    /// </summary>
    public StageCaption? HeaderOver(Viewer viewer)
    {
        if (Managed is not [var solo] || solo != viewer || TargetOf(viewer) is not { } video) return null;
        var area = new Rectangle(video.X, video.Y, video.Width, Math.Min(CaptionHeight, video.Height));
        return new StageCaption(area, _captions.GetValueOrDefault(viewer)?.Invoke() ?? "", _avatars.GetValueOrDefault(viewer)?.Invoke());
    }

    List<Viewer> Managed => _tiles.Where(t => !_small.Contains(t)).ToList();

    /// <summary>Retângulo da peça <paramref name="index"/> se houvesse <paramref name="count"/> peças (para o ffplay novo já abrir no lugar certo).</summary>
    Rectangle? PlannedRect(int count, int index)
    {
        if (_host.VideoArea is not { } area) return null;
        var cells = TileLayout.Grid(count, Rectangle.Inflate(area, -PadFor(count), -PadFor(count)), Pad);
        return index < cells.Count ? VideoIn(cells[index], count, TileLayout.Aspect) : null;
    }

    /// <summary>Proporção do vídeo desta peça: a do vídeo que chega, ou 16:9 enquanto o ffplay ainda não achou o vídeo.</summary>
    static double AspectOf(Viewer viewer) => viewer.VideoAspect ?? TileLayout.Aspect;

    /// <summary>
    /// O vídeo dentro da célula, na proporção DELE (<paramref name="aspect"/>) e centralizado: um 21:9 numa célula 16:9 fica com faixas em cima e embaixo,
    /// um 16:9 num monitor 21:9 fica com faixas dos lados. Nada é esticado nem cortado (o ffplay na placa esticaria a imagem para caber na janela).
    /// A faixa da legenda fica logo acima do vídeo.
    /// </summary>
    Rectangle VideoIn(Rectangle cell, int count, double aspect)
    {
        int caption = CaptionFor(count);
        int room = Math.Max(0, cell.Height - caption);
        int width = Math.Min(cell.Width, (int)(room * aspect)) & ~1;
        int height = Math.Min(room, (int)(width / aspect)) & ~1;
        return new Rectangle(cell.X + (cell.Width - width) / 2, cell.Y + caption + (room - height) / 2, width, height);
    }

    /// <summary>Com uma peça em destaque (e 2 ou mais peças): ela grande e as outras menores; senão a grade igual.</summary>
    IReadOnlyList<Rectangle> Layout(IReadOnlyList<Viewer> tiles, Rectangle videos)
    {
        int emphasized = _emphasized == null ? -1 : tiles.ToList().IndexOf(_emphasized);
        return emphasized >= 0 && tiles.Count >= 2
            ? TileLayout.Emphasis(tiles.Count, emphasized, videos, Pad)
            : TileLayout.Grid(tiles.Count, videos, Pad);
    }

    // ------------------------------------------------------------------ manutenção (a cada 250 ms e quando a janela mexe)

    /// <summary>Põe cada vídeo no lugar que a grade deu. Se alguém mexeu (tecla F, arrastar), volta: quem manda no tamanho é o palco.</summary>
    public void Relayout()
    {
        var managed = Managed;
        // Esc no vídeo sai da tela cheia (StageEscape) só com o palco em tela cheia e à vista; nos outros casos o gancho nem existe.
        bool escapeActive = _host.Fullscreen && _host.StageShown;
        _escape.Update(escapeActive, escapeActive ? managed.Select(v => v.Window.Handle).Where(h => h != IntPtr.Zero).ToArray() : []);
        if (managed.Count == 0 || _host.VideoArea is not { } area) return;
        bool shown = _host.StageShown;
        int n = managed.Count;
        var cells = Layout(managed, Rectangle.Inflate(area, -PadFor(n), -PadFor(n)));
        var captions = new List<StageCaption>();
        var targets = new Dictionary<Viewer, Rectangle>();
        for (int i = 0; i < managed.Count && i < cells.Count; i++)
        {
            var video = VideoIn(cells[i], n, AspectOf(managed[i]));
            if (CaptionFor(n) > 0) captions.Add(new StageCaption(new Rectangle(video.X, video.Y - CaptionHeight, video.Width, CaptionHeight), _captions.GetValueOrDefault(managed[i])?.Invoke() ?? "", _avatars.GetValueOrDefault(managed[i])?.Invoke()));
            var target = shown ? video : new Rectangle(Parked, video.Size);
            targets[managed[i]] = target;
            Place(managed[i], target);
        }
        _targets = targets;
        _cells = cells.ToList();
        _host.ShowCaptions(shown ? captions : Array.Empty<StageCaption>());
        Placed?.Invoke();
    }

    // ------------------------------------------------------------------ trocar de lugar arrastando a legenda

    /// <summary>Peça (na ordem de <see cref="Managed"/>) cuja célula contém o ponto de tela; -1 se nenhuma.</summary>
    int TileIndexAt(Point screen) => _cells.FindIndex(c => c.Contains(screen));

    /// <summary>Durante o arraste destaca a legenda de destino; ao soltar sobre outra peça, as duas trocam de lugar (e de papel, se uma está em destaque).</summary>
    void OnCaptionDragged(int from, Point screen, bool done)
    {
        var managed = Managed;
        int to = TileIndexAt(screen);
        bool valid = from < managed.Count && to >= 0 && to < managed.Count && to != from;
        _host.HighlightCaption(valid && !done ? to : -1);
        if (done && valid) Swap(managed[from], managed[to]);
    }

    void Swap(Viewer a, Viewer b)
    {
        int i = _tiles.IndexOf(a), j = _tiles.IndexOf(b);
        (_tiles[i], _tiles[j]) = (_tiles[j], _tiles[i]);
        if (_emphasized == a) _emphasized = b; else if (_emphasized == b) _emphasized = a;
        Relayout();
    }

    void Place(Viewer viewer, Rectangle target)
    {
        var window = viewer.Window;
        var hwnd = window.Handle;
        if (hwnd == IntPtr.Zero) return;
        if (_owned.GetValueOrDefault(viewer) != hwnd) { window.SetOwner(_host.Handle); _owned[viewer] = hwnd; }
        if (window.HasFrame) window.SetFrame(false);
        if (!window.TryGetBounds(out var now) || now != target) window.SetBounds(target);
    }

    public void Dispose()
    {
        _host.CaptionDragged -= OnCaptionDragged;
        _escape.Dispose();
        _tick.Stop();
        _tick.Dispose();
    }
}
