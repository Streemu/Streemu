using System.Drawing.Drawing2D;
using Streemu.Ui.Avatars;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>Qual vista da janela está aberta (a barra marca o item dela).</summary>
public enum SideView { Home, Stage, Settings, Filme, Transmit, Arquivos, Grupos, Novidades }

/// <summary>Estado do indicador do diagnóstico na barra lateral. Hidden = não desenha (antes de o app decidir).</summary>
public enum DiagState { Hidden, Neutral, Ok, Fail }

/// <summary>O botão de atualização da barra lateral: nada novo, procurando, versão nova disponível ou atualizando.</summary>
public enum UpdateState { Idle, Checking, Available, Updating }

/// <summary>Um amigo na barra lateral: quem é, o que está fazendo, e se está ao vivo e/ou em tela.</summary>
/// <summary>Você está ao vivo: o jogo, quantos assistem e se a prévia da sua própria live está aberta (a linha "Minha live" da barra).</summary>
public sealed record SideSelf(string Game, int Watching, bool Previewing);

/// <summary>Membro do grupo. <paramref name="Offline"/>: está com o app fechado (plano 019, etapa 7); não tem live, não abre nada ao clicar e só aparece com a barra aberta.</summary>
public sealed record SideFriend(string Id, string Nick, string Detail, bool Live, bool OnScreen, AvatarImage? Avatar, bool Offline = false);

/// <summary>
/// A barra lateral da janela principal (modelo A + C, 2026-11-09): aberta (236) mostra Início, "Em tela", quem está AO VIVO e quem está ONLINE; recolhida
/// (64) vira um trilho só de avatares, com anel vermelho em quem está ao vivo. Clicar em quem está ao vivo coloca a stream no palco (ou a tira, se já está).
/// Uso: <c>side.SetData(amigos, 2, stageView: true, "Conectado"); side.FriendClicked += f =&gt; Alternar(f.Id);</c>
/// </summary>
public sealed class SidePanel : Control
{
    public const int OpenAt96 = UiMetrics.SidebarWidth;
    const int RailAt96 = UiMetrics.RailWidth, AvatarAt96 = UiMetrics.Avatar;
    enum Kind { Toggle, Home, Filme, Stage, Transmit, Profile, Self, StopLive, Settings, Friend, Section, Problem, Diag, Update, Bell, Arquivos, Grupos, Novidades, Theme, GroupFilter, Atividade }
    readonly record struct NavigationItem(Kind Action, SideView View, string Caption, UiIcon Icon);
    static readonly NavigationItem[] Navigation =
    [
        new(Kind.Home, SideView.Home, "Início", UiIcon.Home),
        new(Kind.Transmit, SideView.Transmit, "Transmitir", UiIcon.Broadcast),
        new(Kind.Filme, SideView.Filme, "Cinema", UiIcon.Cinema),
        new(Kind.Arquivos, SideView.Arquivos, "Enviar arquivo", UiIcon.Upload), // spec 020; só com ArquivosVisivel
        new(Kind.Grupos, SideView.Grupos, "Grupos", UiIcon.People), // só com GruposVisivel (servidor com contas)
        new(Kind.Diag, SideView.Settings, "Diagnóstico", UiIcon.Shield), // item de navegação no desenho; abre os Ajustes, onde está o detalhe
        new(Kind.Settings, SideView.Settings, "Configurações", UiIcon.Settings),
        new(Kind.Novidades, SideView.Novidades, "Novidades", UiIcon.Sparkle), // plano 027; só com NovidadesVisivel
        new(Kind.Stage, SideView.Stage, "Em tela", UiIcon.Stage),
    ];
    readonly record struct Row(Rectangle Bounds, Kind Kind, string Text = "", SideFriend? Friend = null);

    IReadOnlyList<SideFriend> _friends = Array.Empty<SideFriend>();
    SideSelf? _self;
    int _streams;
    SideView _view;
    bool _collapsed;
    string _status = "";
    int _hover = -1;
    List<Row> _rows = new();
    readonly UiFonts _fonts;
    string _profileName = "";
    AvatarImage? _profileImage;
    int _friendScroll, _focusRow;
    Rectangle _friendsViewport;
    int _navigationBottom, _footerTop;
    /// <summary>A marca é maior (branding), mas em janela baixa volta ao tamanho de antes para sobrar espaço aos amigos.</summary>
    int BrandHeightNow => ClientSize.Height < Dp(620) || _measuring ? BrandHeightLowAt96 : BrandHeightAt96;
    const int BrandTopAt96 = 12, BrandHeightAt96 = 46, BrandHeightLowAt96 = 34, // marca maior, para ajudar no branding (pedido de 14/11)
         TransmitGapAt96 = 10, TransmitGapLowAt96 = 6;
    public event Action? TransmitClicked, ProfileClicked;
    /// <summary>Seletor de grupo (plano 019, etapa 8): com texto, o título "Amigos" vira um botão com esse texto ("Todos os grupos"); nulo = sem seletor (um grupo só). O clique devolve o retângulo do botão, em coordenadas de tela, para o menu abrir embaixo.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? GroupFilterText { get => _groupFilterText; set { if (_groupFilterText == value) return; _groupFilterText = value; _rows = BuildRows(); Invalidate(); } }
    string? _groupFilterText;
    public event Action<Rectangle>? GroupFilterClicked;
    public void SetProfile(string name, AvatarImage? image)
    {
        if (_profileName == name && ReferenceEquals(image, _profileImage)) return;
        (_profileName, _profileImage) = (name, image); Invalidate();
    }
    /// <summary>Quadro a quadro do GIF: o mesmo ritmo (100 ms) do avatar da lista do grupo; só roda com o mouse sobre um avatar animado.</summary>
    readonly System.Windows.Forms.Timer _clock = new() { Interval = 100 };

    public event Action? HomeClicked, StageClicked, SettingsClicked, ToggleClicked;
    /// <summary>"Filme", logo abaixo de "Início" (spec 010, 11.1).</summary>
    public event Action? FilmeClicked;
    string? _filmeTexto;
    /// <summary>Até a 0.3.0 o item só aparece com STREEMU_FILME=1.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool FilmeVisivel { get; set; }
    /// <summary>Linha curta quando há filme no grupo (ex.: "Filme · Ana abriu um filme"); nulo = só "Filme".</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? FilmeTexto { get => _filmeTexto; set { _filmeTexto = value; Invalidate(); } }
    /// <summary>"Enviar arquivo" (spec 020), abaixo do Cinema; escondido até o teste de campo (STREEMU_ARQUIVOS=1).</summary>
    public event Action? ArquivosClicked;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ArquivosVisivel { get; set; }
    /// <summary>"Grupos" (criar, entrar por convite, gerenciar), abaixo de "Enviar arquivo"; só aparece quando o servidor tem contas.</summary>
    public event Action? GruposClicked;
    bool _gruposVisivel;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool GruposVisivel { get => _gruposVisivel; set { if (_gruposVisivel == value) return; _gruposVisivel = value; PerformLayout(); Invalidate(); } }
    /// <summary>"Novidades" (o que mudou em cada versão), depois de "Configurações"; falso por padrão e ligado pela janela principal (MainForm.Novidades.cs).</summary>
    public event Action? NovidadesClicked;
    bool _novidadesVisivel, _novidadesNovas;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool NovidadesVisivel { get => _novidadesVisivel; set { if (_novidadesVisivel == value) return; _novidadesVisivel = value; PerformLayout(); Invalidate(); } }
    /// <summary>Tem novidade que a pessoa ainda não viu: selo "NOVO" na barra aberta, bolinha no trilho.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool NovidadesNovas { get => _novidadesNovas; set { if (_novidadesNovas == value) return; _novidadesNovas = value; Invalidate(); } }
    int _arquivosPendentes;
    /// <summary>Pedidos esperando uma resposta minha (aceitar, mandar de novo, cota do relay): aparece como "Enviar arquivo · N".</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int ArquivosPendentes { get => _arquivosPendentes; set { if (_arquivosPendentes == value) return; _arquivosPendentes = value; PerformLayout(); Invalidate(); } }
    /// <summary>Clique em "Minha live": abre ou fecha a prévia da própria transmissão.</summary>
    public event Action? SelfClicked;
    /// <summary>"Encerrar live" (sob "Você está ao vivo"): quem transmite e assiste outra live ao mesmo tempo não precisa voltar ao Início para parar.</summary>
    public event Action? StopLiveClicked;
    /// <summary>A bandeira "Marcar problema" (spec 009): discreta, logo acima da engrenagem, nos dois estados da barra.</summary>
    public event Action? ProblemClicked;
    /// <summary>Clique no indicador do diagnóstico (abre os ajustes, onde está o detalhe).</summary>
    public event Action? DiagClicked;

    /// <summary>"Procurar atualização" / "Atualizar agora" (pedido de 11/11: atualizar à mão sem abrir os Ajustes), logo acima da bandeira.</summary>
    public event Action? UpdateClicked;

    /// <summary>O botão de tema: pede a troca e entrega o retângulo dele em coordenadas da tela (a foto da transição deixa um buraco ali, para o puxador animar ao vivo).</summary>
    public event Action<Rectangle>? ThemeClicked;
    readonly UiMotion _themeMotion;
    /// <summary>Move o puxador do botão de tema para o tema dado; <paramref name="animate"/> falso só posiciona (abertura do app).</summary>
    public void SetThemeLight(bool light, bool animate)
    {
        if (animate) _themeMotion.To(light ? 1 : 0, UiMotion.ThemeMs); else _themeMotion.Snap(light ? 1 : 0);
        Invalidate();
    }
    string ThemeLabel => Theme.IsLight ? "Tema claro ativado. Ativar tema escuro" : "Tema escuro ativado. Ativar tema claro";

    /// <summary>O sino de silenciar as notificações (spec 012); entrega o retângulo do item em coordenadas da tela para o menu abrir ao lado.</summary>
    public event Action<Rectangle>? BellClicked;
    bool _bellSilenced, _bellDot;
    string _bellText = "Notificações", _bellDetail = "";

    /// <summary>Estado do sino: ligado ou silenciado, o texto curto ("Silenciadas até 15:30"), a dica e o ponto de "tem versão obrigatória".</summary>
    public void SetSino(bool silenced, string text, string detail, bool dot)
    {
        if (silenced == _bellSilenced && text == _bellText && detail == _bellDetail && dot == _bellDot) return;
        (_bellSilenced, _bellText, _bellDetail, _bellDot) = (silenced, text, detail, dot);
        if (_hover >= 0 && _hover < _rows.Count && _rows[_hover].Kind == Kind.Bell) _tip.SetToolTip(this, detail);
        Invalidate();
    }
    /// <summary>O ícone de atividade recente do grupo; entrega o retângulo do item na tela para o painel abrir ao lado.</summary>
    public event Action<Rectangle>? AtividadeClicked;
    bool _atividadeNova;
    string _atividadeDica = "Atividade recente do grupo";

    /// <summary>Ponto de "tem atividade nova" no ícone e a dica (o que há de novo).</summary>
    public void SetAtividade(bool nova, string dica)
    {
        if (nova == _atividadeNova && dica == _atividadeDica) return;
        (_atividadeNova, _atividadeDica) = (nova, dica);
        if (_hover >= 0 && _hover < _rows.Count && _rows[_hover].Kind == Kind.Atividade) _tip.SetToolTip(this, dica);
        Invalidate();
    }
    UpdateState _update = UpdateState.Idle;
    string _updateDetail = "";

    /// <summary>O estado do botão de atualização: Available acende em azul ("Atualizar agora"); a dica diz a versão e o que fazer.</summary>
    public void SetUpdate(UpdateState state, string detail)
    {
        if (state == _update && detail == _updateDetail) return;
        _update = state; _updateDetail = detail;
        if (_hover >= 0 && _hover < _rows.Count && _rows[_hover].Kind == Kind.Update) _tip.SetToolTip(this, detail);
        Invalidate();
    }

    DiagState _diag = DiagState.Hidden;
    string _diagTitle = "", _diagDetail = "";
    readonly ToolTip _tip = new() { InitialDelay = 300, AutoPopDelay = 15000 };

    /// <summary>
    /// Indicador do diagnóstico automático (spec 009), acima da bandeira: ✓ verde = o servidor confirmou tudo há pouco; ✗ vermelho = falhando
    /// (com o motivo na dica); ○ cinza = desligado ou preparando. Quem decide o estado é o envio (Telemetria.Envio.Situacao), não a barra.
    /// </summary>
    public void SetDiag(DiagState state, string title, string detail)
    {
        if (state == _diag && title == _diagTitle && detail == _diagDetail) return;
        _diag = state; _diagTitle = title; _diagDetail = detail;
        if (_hover >= 0 && _hover < _rows.Count && _rows[_hover].Kind == Kind.Diag) _tip.SetToolTip(this, detail);
        Invalidate();
    }
    DateTime _problemFlashUntil = DateTime.MinValue;
    readonly System.Windows.Forms.Timer _problemFlashTimer = new() { Interval = 4100 };

    /// <summary>Mostra "Problema marcado ✓" por 4 s no lugar do "Marcar problema" (o clique teve efeito).</summary>
    public void FlashProblem()
    {
        _problemFlashUntil = DateTime.Now.AddSeconds(4);
        Invalidate();
        _problemFlashTimer.Stop(); _problemFlashTimer.Start();
    }
    public event Action<SideFriend>? FriendClicked;
    /// <summary>Clique na foto do amigo (spec 013): o retângulo da foto em coordenadas de tela, para o menu abrir ao lado.</summary>
    public event Action<SideFriend, Rectangle>? FriendAvatarClicked;

    public SidePanel()
    {
        _fonts = new UiFonts(this);
        UiScale.WatchFont(this);
        FontChanged += (_, _) => { if (IsHandleCreated && Parent != null && Dock == DockStyle.Left) Width = Dp(_collapsed ? RailAt96 : OpenAt96); };
        TabStop = true;
        AccessibleName = "Navegação principal";
        AccessibleRole = AccessibleRole.ToolBar;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Left;
        BackColor = Theme.SurfaceRaised;
        Width = Dp(OpenAt96);
        _themeMotion = new UiMotion(this, Invalidate);
        _themeMotion.Snap(Theme.IsLight ? 1 : 0);
        _clock.Tick += (_, _) => Invalidate();
        _widthClock.Tick += (_, _) => StepWidth();
        _problemFlashTimer.Tick += (_, _) => { _problemFlashTimer.Stop(); Invalidate(); };
    }

    bool HoveringAnimatedAvatar => _hover >= 0 && _hover < _rows.Count && _rows[_hover].Friend?.Avatar is { Animated: true };

    void UpdateClock() => _clock.Enabled = HoveringAnimatedAvatar && Visible;

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _fonts.Dispose(); _themeMotion.Dispose(); _problemFlashTimer.Dispose(); _clock.Stop(); _clock.Dispose(); _widthClock.Stop(); _widthClock.Dispose(); _tip.Dispose(); }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Só o fundo, sem as linhas: enquanto a barra flutuante desliza por cima do lugar dela, a barra encaixada já está na largura final mas não pode
    /// aparecer por baixo (MainForm.BarraAnimada.cs).
    /// </summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ContentHidden
    {
        get => _contentHidden;
        set { if (_contentHidden == value) return; _contentHidden = value; Invalidate(); }
    }
    bool _contentHidden;

    /// <summary>Recolhida (trilho de avatares) ou aberta. Muda a largura; quem hospeda refaz o layout.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Collapsed
    {
        get => _collapsed;
        set
        {
            if (_collapsed == value) return;
            _collapsed = value;
            AnimateWidthTo(Dp(value ? RailAt96 : OpenAt96));
            Invalidate();
        }
    }

    /// <summary>
    /// Recolhe ou abre sem deslizar. Na troca de tela a barra deslizando adiava o layout da página nova até o fim (ver <see cref="Sliding"/>), e a página
    /// aparecia por 150 ms desenhada no tamanho antigo (vídeo de 2026-11-16). Ex.: <c>side.SnapCollapsed(true)</c>.
    /// </summary>
    public void SnapCollapsed(bool value)
    {
        bool sliding = _widthClock.Enabled;
        if (_collapsed == value && !sliding) return;
        _collapsed = value;
        _widthClock.Stop();
        Sliding = false;
        if (Dock == DockStyle.Left) Width = Dp(value ? RailAt96 : OpenAt96);
        Invalidate();
        if (sliding) Slid?.Invoke(); // quem esperava o fim do deslize refaz o conteúdo agora
    }

    // ---- animação da largura (o conteúdo ao lado acompanha, porque a barra é encaixada à esquerda)
    readonly System.Windows.Forms.Timer _widthClock = new() { Interval = 15 };
    readonly System.Diagnostics.Stopwatch _widthWatch = new();
    int _widthFrom, _widthTo;
    const int WidthMs = 150;

    /// <summary>
    /// A barra está deslizando. Refazer o conteúdo ao lado a cada passo custava ~31 ms (medido em 2026-11-10: a animação rodava a ~27 quadros/s e parecia
    /// travada); com o conteúdo esperando o fim, cada passo custa ~6 ms (≈60 quadros/s). Quem hospeda adia o que é caro e refaz em <see cref="Slid"/>.
    /// </summary>
    public static bool Sliding { get; private set; }

    /// <summary>A barra terminou de deslizar: hora de refazer o conteúdo ao lado na largura final.</summary>
    public event Action? Slid;

    /// <summary>
    /// Recolher e expandir deslizam (150 ms, começando rápido e terminando suave) em vez de pular. Só a barra encaixada na janela anima; a barra flutuante
    /// (que ocupa a janela dela inteira) muda de largura na hora.
    /// </summary>
    void AnimateWidthTo(int target)
    {
        if (Dock != DockStyle.Left || !IsHandleCreated || !Visible) { _widthClock.Stop(); Sliding = false; if (Dock == DockStyle.Left) Width = target; return; }
        _widthFrom = Width; _widthTo = target;
        _widthWatch.Restart();
        Sliding = true;
        _widthClock.Start();
    }

    void StepWidth()
    {
        double t = Math.Min(1.0, _widthWatch.ElapsedMilliseconds / (double)WidthMs);
        double eased = 1 - Math.Pow(1 - t, 3);
        Width = (int)Math.Round(_widthFrom + (_widthTo - _widthFrom) * eased);
        if (t < 1.0) return;
        _widthClock.Stop();
        Sliding = false;
        Width = _widthTo;
        Slid?.Invoke();
    }

    /// <summary>Com menos de ~160 px de largura (a meio caminho entre o trilho e a barra) a barra se desenha como trilho.</summary>
    bool Rail => Width < Dp((RailAt96 + OpenAt96) / 2);

    public void SetData(IReadOnlyList<SideFriend> friends, int streams, SideView view, string status, SideSelf? self = null)
    {
        if (_streams == streams && _view == view && _status == status && Equals(_self, self) && _friends.SequenceEqual(friends)) return;
        _friends = friends; _streams = streams; _view = view; _status = status; _self = self;
        _rows = BuildRows();
        Invalidate();
    }

    int Dp(int at96) => UiScale.Dp(this, at96);

    // ------------------------------------------------------------------ linhas (a mesma conta serve para desenhar e para o clique)

    /// <summary>
    /// Janela baixa ou escala alta (Ctrl +): a navegação, que desce do topo, encostava nas ferramentas e no perfil, que sobem da base, e as linhas se cobriam
    /// ("Encerrar live" em cima da fileira de ferramentas a 150 %, TESTES.md 2026-11-15; auditoria da barra, 2026-11-16). Sem lugar, as linhas encolhem
    /// juntas na altura até caber, sem passar de <see cref="MinFit"/>; a janela não deixa ficar mais baixa que isso (<see cref="MinimumHeight"/>).
    /// O quanto encolher sai do caso mais alto (com live no ar e lives em tela), não do estado de agora: assim Transmitir não muda de lugar quando a
    /// live começa ou acaba (regra do desenho, conferida no --identity-check).
    /// </summary>
    List<Row> BuildRows()
    {
        var (self, streams) = (_self, _streams);
        _self ??= new SideSelf("", 0, false); _streams = Math.Max(1, _streams); _fit = 1;
        try
        {
            BuildRowsAt();
            for (int i = 0; i < 4 && _overflow > 0 && _fit > MinFit; i++)
            {
                _fit = Math.Max(MinFit, _fit * ClientSize.Height / (ClientSize.Height + (double)_overflow));
                BuildRowsAt();
            }
        }
        finally { (_self, _streams) = (self, streams); }
        return BuildRowsAt();
    }

    /// <summary>
    /// Altura em que as linhas cabem no caso mais alto (com live no ar e lives em tela, no trilho, que tem o rodapé mais alto), já encolhidas até
    /// <see cref="MinFit"/>. A janela principal usa como altura mínima, recalculada quando a escala muda.
    /// </summary>
    /// <example><code>int altura = _side.MinimumHeight();</code></example>
    public int MinimumHeight()
    {
        var (self, streams, fit) = (_self, _streams, _fit);
        _self ??= new SideSelf("", 0, false); _streams = Math.Max(1, _streams); _fit = MinFit; _measuring = true;
        try { BuildRowsAt(); return _needed + Dp(4); }
        finally { (_self, _streams, _fit) = (self, streams, fit); _measuring = false; _rows = BuildRows(); } // refaz as medidas de verdade (viewport, separadores)
    }

    const double MinFit = 0.75;
    double _fit = 1;
    int _overflow, _needed;
    bool _measuring; // MinimumHeight: mede no caso mais apertado (janela baixa e trilho), qualquer que seja o tamanho de agora

    /// <summary>Medida vertical encolhida pelo fator de agora (<see cref="BuildRows"/>); as horizontais continuam em <see cref="Dp"/>.</summary>
    int V(int at96) => Dp((int)Math.Round(at96 * _fit));

    List<Row> BuildRowsAt()
    {
        var rows = new List<Row>();
        bool rail = Rail || _measuring;
        int pad = Dp(8), w = ClientSize.Width, y = V(BrandTopAt96);
        // Em janela baixa, as ferramentas ficam compactas e os espaços entre grupos encolhem, para sobrar ao menos um amigo; amigos rolam sem cobrir
        // os comandos fixos. A conta só depende da altura, então Transmitir continua no mesmo lugar enquanto a janela não muda de tamanho.
        bool low = ClientSize.Height < Dp(620) || _measuring;
        // aberta: a marca à esquerda e o « à direita; no trilho a faixa inteira (gato + ») abre a navegação
        rows.Add(new Row(new Rectangle(rail ? 0 : w - Dp(40), y, rail ? w : Dp(36), V(BrandHeightNow)), Kind.Toggle));
        y += V(BrandHeightNow + (low ? 4 : 12));
        foreach (var item in Navigation)
        {
            if (item.Action == Kind.Filme && !FilmeVisivel || item.Action == Kind.Arquivos && !ArquivosVisivel || item.Action == Kind.Grupos && !GruposVisivel || item.Action == Kind.Novidades && !NovidadesVisivel || item.Action == Kind.Stage && _streams == 0) continue;
            // Transmitir é um grupo à parte (mockups 1–24): o espaço antes dele não depende de live, palco ou largura, então a linha não anda
            // o espaço que separa o grupo não encolhe abaixo de 10 dp (com o espaço da linha de cima), senão Transmitir parece colado no item de cima (Início, desde o #273)
            if (item.Action == Kind.Transmit) y += Math.Max(V(low ? TransmitGapLowAt96 : TransmitGapAt96), Dp(10) - V(4));
            string caption = item.Action switch
            {
                Kind.Filme => _filmeTexto ?? item.Caption,
                Kind.Transmit when _self != null => "Sua transmissão",
                Kind.Stage => $"{item.Caption} · {_streams}",
                Kind.Arquivos when _arquivosPendentes > 0 => $"{item.Caption} · {_arquivosPendentes}",
                _ => item.Caption,
            };
            int height = V(item.Action == Kind.Transmit ? 42 : 36);
            rows.Add(new Row(new Rectangle(pad, y, w - 2 * pad, height), item.Action, caption)); y += height + V(4);
            if (item.Action == Kind.Transmit && _self != null)
            {
                rows.Add(new Row(new Rectangle(pad, y, w - 2 * pad, V(30)), Kind.StopLive, "Encerrar live")); y += V(34);
            }
        }
        _navigationBottom = y + V(2);
        y += V(low ? 4 : 8);
        int height0 = _measuring ? 0 : ClientSize.Height, bottom = height0 - V(8);
        int footerHeight = rail ? V(36) : V(58);
        rows.Add(new Row(new Rectangle(pad, bottom - footerHeight, w - 2 * pad, footerHeight), Kind.Profile, _profileName));
        _footerTop = bottom - footerHeight - V(low ? 3 : 5);
        bottom -= footerHeight + V(low ? 6 : 10);
        var tools = new[] { Kind.Atividade, Kind.Bell, Kind.Update, Kind.Problem, Kind.Theme };
        if (low || !rail) // aberta, as ferramentas viram uma fileira de ícones (o desenho não tem a lista de textos no rodapé); o texto fica na dica
        {
            int columns = rail ? 2 : tools.Length, height = rail ? V(30) * ((tools.Length + 1) / 2) : V(30);
            // aberta: células de ícone de largura igual e o botão de tema (2:1) ocupa o que sobra da fileira (sempre com espaço para ele); no trilho, duas colunas
            int cell = rail ? (w - 2 * pad) / columns : Math.Min(Dp(44), (w - 2 * pad - Dp(52)) / (tools.Length - 1));
            for (int i = 0; i < tools.Length; i++)
            {
                bool themeOpen = !rail && tools[i] == Kind.Theme;
                bool themeSozinho = rail && tools[i] == Kind.Theme && i % columns == 0; // 5 ícones em 2 colunas: o tema ocupa a última fileira inteira, em vez de ficar torto à esquerda
                var bounds = themeSozinho ? new Rectangle(pad, bottom - height + i / columns * V(30), w - 2 * pad, V(30))
                    : themeOpen ? new Rectangle(pad + i * cell, bottom - height, w - 2 * pad - i * cell, V(30))
                    : new Rectangle(pad + i % columns * cell, bottom - height + i / columns * V(30), cell, V(30));
                rows.Add(new Row(bounds, tools[i]));
            }
            bottom -= height + V(4);
        }
        else
        {
            foreach (var kind in tools) { bottom -= V(30); rows.Add(new Row(new Rectangle(pad, bottom, w - 2 * pad, V(28)), kind, kind == Kind.Diag ? _diagTitle : "")); }
        }
        _overflow = y - bottom;
        _needed = y + (height0 - bottom);
        _friendsViewport = new Rectangle(0, y, w, Math.Max(0, bottom - y));
        int rowHeight = Dp(48);
        var visiveis = rail ? _friends.Where(f => !f.Offline).ToList() : _friends.ToList(); // o trilho não mostra offline
        int offlineCount = visiveis.Count(f => f.Offline);
        int maxScroll = Math.Max(0, visiveis.Count * rowHeight + Dp(28) + (offlineCount > 0 ? Dp(28) : 0) - _friendsViewport.Height);
        _friendScroll = Math.Clamp(_friendScroll, 0, maxScroll);
        y -= _friendScroll;
        if (!rail) { rows.Add(new Row(new Rectangle(pad, y, w - 2 * pad, Dp(26)), _groupFilterText == null ? Kind.Section : Kind.GroupFilter, _groupFilterText == null ? "Amigos" : "Amigos · " + _groupFilterText + " ▾")); y += Dp(28); }
        bool secaoOffline = false;
        foreach (var friend in visiveis)
        {
            if (friend.Offline && !secaoOffline)
            {
                secaoOffline = true;
                rows.Add(new Row(new Rectangle(pad, y, w - 2 * pad, Dp(26)), Kind.Section, $"OFFLINE — {offlineCount}")); y += Dp(28);
            }
            var r = new Rectangle(pad, y, w - 2 * pad, rowHeight - Dp(2));
            // no trilho um avatar cortado no meio parecia sobreposto às ferramentas (vídeo de 2026-11-24): só entra quem cabe inteiro; a roda do mouse traz os outros
            bool visivel = rail ? r.Top >= _friendsViewport.Top && r.Bottom <= _friendsViewport.Bottom : r.Bottom > _friendsViewport.Top && r.Top < _friendsViewport.Bottom;
            if (visivel) rows.Add(new Row(r, Kind.Friend, Friend: friend));
            y += rowHeight;
        }
        return rows.OrderBy(r => r.Bounds.Y).ThenBy(r => r.Bounds.X).ToList();
    }
    int RowAt(Point p) { for (int i = 0; i < _rows.Count; i++) if (_rows[i].Kind != Kind.Section && (_rows[i].Kind != Kind.Friend || _friendsViewport.Contains(p)) && _rows[i].Bounds.Contains(p)) return i; return -1; }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int now = RowAt(e.Location);
        if (now == _hover) return;
        _hover = now;
        var kind = now >= 0 ? _rows[now].Kind : Kind.Section;
        if (kind == Kind.Diag) _tip.SetToolTip(this, _diagDetail);
        else if (kind == Kind.Update) _tip.SetToolTip(this, _updateDetail);
        else if (kind == Kind.Bell) _tip.SetToolTip(this, _bellDetail);
        else if (kind == Kind.Atividade) _tip.SetToolTip(this, _atividadeDica);
        else if (kind == Kind.Theme) _tip.SetToolTip(this, ThemeLabel);
        else if (kind == Kind.Toggle) _tip.SetToolTip(this, ToggleTooltip());
        else if (kind == Kind.Problem) _tip.SetToolTip(this, "Marcar problema\nGuarda este momento e envia o diagnóstico para a equipe e para os amigos da live. Use quando algo travar ou falhar.");
        else if (kind == Kind.StopLive) _tip.SetToolTip(this, "Encerra a sua transmissão para todo mundo que está assistindo");
        else if (kind == Kind.Transmit && _self == null) _tip.SetToolTip(this, "Escolher o que transmitir e começar a live"); // dica do #137
        else if (Rail && now >= 0) _tip.SetToolTip(this, LabelFor(_rows[now]));
        else _tip.SetToolTip(this, null);
        Cursor = now >= 0 ? Cursors.Hand : Cursors.Default;
        UpdateClock();
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; _tip.SetToolTip(this, null); UpdateClock(); Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        _rows = BuildRows();
        int i = RowAt(e.Location);
        if (e.Button != MouseButtons.Left || i < 0) return;
        Focus(); _focusRow = i;
        if (_rows[i].Kind == Kind.Friend) ClickFriend(_rows[i], e.Location);
        else Activate(_rows[i]);
    }

    void Activate(Row row)
    {
        switch (row.Kind)
        {
            case Kind.Toggle: ToggleClicked?.Invoke(); break;
            case Kind.Home: HomeClicked?.Invoke(); break;
            case Kind.Filme: FilmeClicked?.Invoke(); break;
            case Kind.Arquivos: ArquivosClicked?.Invoke(); break;
            case Kind.Grupos: GruposClicked?.Invoke(); break;
            case Kind.Novidades: NovidadesClicked?.Invoke(); break;
            case Kind.Stage: StageClicked?.Invoke(); break;
            case Kind.Settings: SettingsClicked?.Invoke(); break;
            case Kind.Transmit: TransmitClicked?.Invoke(); break;
            case Kind.Profile: ProfileClicked?.Invoke(); break;
            case Kind.Self: SelfClicked?.Invoke(); break;
            case Kind.StopLive: StopLiveClicked?.Invoke(); break;
            case Kind.Problem: ProblemClicked?.Invoke(); break;
            case Kind.Diag: DiagClicked?.Invoke(); break;
            case Kind.Update: if (_update is not (UpdateState.Checking or UpdateState.Updating)) UpdateClicked?.Invoke(); break;
            case Kind.Bell: BellClicked?.Invoke(RectangleToScreen(row.Bounds)); break;
            case Kind.Atividade: AtividadeClicked?.Invoke(RectangleToScreen(row.Bounds)); break;
            case Kind.Theme: ThemeClicked?.Invoke(RectangleToScreen(row.Bounds)); break;
            case Kind.GroupFilter: GroupFilterClicked?.Invoke(RectangleToScreen(row.Bounds)); break;
            case Kind.Friend: if (row.Friend!.Live && !row.Friend.Offline) FriendClicked?.Invoke(row.Friend); else FriendAvatarClicked?.Invoke(row.Friend, RectangleToScreen(AvatarRect(row.Bounds))); break;
        }
    }

    /// <summary>A foto abre o menu do amigo; o resto da linha continua a abrir a live dele (só se ele está ao vivo).</summary>
    void ClickFriend(Row row, Point at)
    {
        var f = row.Friend!;
        var avatar = AvatarRect(row.Bounds);
        if (Rail || avatar.Contains(at)) FriendAvatarClicked?.Invoke(f, RectangleToScreen(avatar));
        else if (f.Live && !f.Offline) FriendClicked?.Invoke(f);
    }

    Rectangle AvatarRect(Rectangle r)
    {
        int size = Dp(AvatarAt96);
        return new Rectangle(Rail ? r.X + (r.Width - size) / 2 : r.X + Dp(6), r.Y + (r.Height - size) / 2, size, size);
    }

    string LabelFor(Row row) => row.Kind switch
    {
        Kind.Novidades => _novidadesNovas ? "Novidades: tem coisa nova desde a última vez" : "Novidades",
        Kind.Transmit => _self == null ? "Transmitir" : "Sua transmissão · AO VIVO",
        Kind.Profile => _profileName + " · Perfil", Kind.Friend => row.Friend!.Nick + (row.Friend.OnScreen ? " · Assistindo" : row.Friend.Live ? " · Ao vivo" : row.Friend.Offline ? " · Offline" : " · Online"),
        Kind.Diag => _diagTitle.Length > 0 ? _diagTitle : "Diagnóstico",
        Kind.Bell => _bellText, Kind.Atividade => "Atividade recente do grupo", Kind.Update => _update == UpdateState.Available ? "Atualizar agora" : "Procurar atualização", Kind.Problem => "Marcar problema",
        Kind.Toggle => _collapsed ? "Abrir a barra lateral" : "Recolher a barra lateral", Kind.Theme => ThemeLabel, _ => row.Text
    };
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _friendScroll = Math.Max(0, _friendScroll - Math.Sign(e.Delta) * Dp(96)); _rows = BuildRows(); Invalidate();
    }
    protected override bool IsInputKey(Keys keys) => (keys & Keys.KeyCode) is Keys.Up or Keys.Down || base.IsInputKey(keys);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        _rows = BuildRows();
        if (e.KeyCode is Keys.Up or Keys.Down)
        {
            int delta = e.KeyCode == Keys.Down ? 1 : -1;
            do { _focusRow = (_focusRow + delta + _rows.Count) % _rows.Count; } while (_rows[_focusRow].Kind == Kind.Section);
            AccessibleDescription = LabelFor(_rows[_focusRow]); Invalidate(); e.Handled = e.SuppressKeyPress = true;
        }
        else if (e.KeyCode is Keys.Enter or Keys.Space && _rows.Count > 0) { Activate(_rows[Math.Clamp(_focusRow, 0, _rows.Count - 1)]); e.Handled = e.SuppressKeyPress = true; }
        base.OnKeyDown(e);
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    // ------------------------------------------------------------------ desenho

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.SurfaceRaised);
        if (ContentHidden) return;
        _rows = BuildRows();
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var save = g.Save();
            if (row.Kind is Kind.Friend or Kind.Section or Kind.GroupFilter) g.SetClip(_friendsViewport);
            DrawRow(g, row, i == _hover);
            if (Focused && _focusRow == i && row.Kind is not (Kind.Section or Kind.Theme)) ControlPaint.DrawFocusRectangle(g, row.Bounds);
            g.Restore(save);
        }
        DrawBrand(g);
        using (var line = new Pen(Theme.Separator))
        {
            g.DrawLine(line, Dp(14), _navigationBottom, ClientSize.Width - Dp(14), _navigationBottom);
            g.DrawLine(line, Dp(14), _footerTop, ClientSize.Width - Dp(14), _footerTop);
        }

        using var edge = new Pen(Theme.Divider);
        g.DrawLine(edge, ClientSize.Width - 1, 0, ClientSize.Width - 1, ClientSize.Height);
    }

    void DrawRow(Graphics g, Row row, bool hover)
    {
        switch (row.Kind)
        {
            case Kind.Toggle: DrawToggle(g, row.Bounds, hover); break;
            case Kind.Home or Kind.Filme or Kind.Stage or Kind.Settings or Kind.Arquivos or Kind.Grupos or Kind.Novidades:
                var item = Navigation.First(n => n.Action == row.Kind);
                DrawNav(g, row.Bounds, item.Icon, row.Text, _view == item.View, hover);
                if (row.Kind == Kind.Filme) DrawBeta(g, row.Bounds);
                if (row.Kind == Kind.Arquivos && _arquivosPendentes == 0) DrawSelo(g, row.Bounds, "NOVO", Theme.SuccessSurface);
                if (row.Kind == Kind.Novidades && _novidadesNovas) DrawSelo(g, row.Bounds, "NOVO", Rail ? Theme.Accent : Theme.SuccessSurface); // no trilho vira o ponto de destaque, como o do sino
                break;
            case Kind.Transmit:
                if (_self == null) DrawNav(g, row.Bounds, UiIcon.Broadcast, row.Text, _view == SideView.Transmit, hover);
                else
                {
                    FillItem(g, row.Bounds, _view == SideView.Transmit, hover);
                    var icon = new Rectangle(row.Bounds.X, row.Bounds.Y, Math.Min(row.Bounds.Width, Dp(40)), row.Bounds.Height);
                    UiIcons.Draw(g, UiIcon.Broadcast, IconBounds(icon), Theme.Error);
                    if (!Rail)
                    {
                        var text = new Rectangle(icon.Right, row.Bounds.Y + Dp(2), row.Bounds.Width - icon.Width, row.Bounds.Height / 2);
                        TextRenderer.DrawText(g, "Sua transmissão", Font, text, Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                        text.Y += text.Height;
                        int dot = Dp(7);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        using (var red = new SolidBrush(Theme.Error)) g.FillEllipse(red, text.X, text.Y + (SmallFont.Height - dot) / 2, dot, dot);
                        text.X += dot + Dp(5); text.Width -= dot + Dp(5);
                        TextRenderer.DrawText(g, "AO VIVO", SmallFont, text, Theme.Error, TextFormatFlags.NoPadding);
                    }
                }
                break;
            case Kind.Profile:
                FillItem(g, row.Bounds, false, hover);
                var avatar = AvatarRect(row.Bounds);
                AvatarBadge.Draw(g, avatar, _profileImage, _profileName, 0);
                if (!Rail)
                {
                    int x = avatar.Right + Dp(10), width = Math.Max(0, row.Bounds.Right - x);
                    // no estado normal ("Na sala do grupo como ...") só o nome, centrado, como no desenho; o status só aparece quando diz algo (sem conexão, aviso...)
                    bool normal = _status.StartsWith("Na sala do grupo como", StringComparison.OrdinalIgnoreCase);
                    if (normal) TextRenderer.DrawText(g, _profileName, Font, new Rectangle(x, row.Bounds.Y, width, row.Bounds.Height), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
                    else
                    {
                        TextRenderer.DrawText(g, _profileName, Font, new Rectangle(x, row.Bounds.Y + Dp(6), width, Dp(24)), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                        TextRenderer.DrawText(g, _status, SmallFont, new Rectangle(x, row.Bounds.Y + Dp(30), width, Dp(22)), Theme.TextMuted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    }
                }
                break;
            case Kind.Self: DrawSelf(g, row.Bounds, hover); break;
            case Kind.StopLive: DrawStopLive(g, row.Bounds, hover); break;
            case Kind.Problem: DrawProblem(g, row.Bounds, hover); break;
            case Kind.Diag: DrawDiagNav(g, row.Bounds, hover); break;
            case Kind.Update: DrawUpdate(g, row.Bounds, hover); break;
            case Kind.Bell: DrawBell(g, row.Bounds, hover); break;
            case Kind.Atividade: DrawAtividade(g, row.Bounds, hover); break;
            case Kind.Theme: DrawThemeToggle(g, row.Bounds, hover); break;
            case Kind.GroupFilter: TextRenderer.DrawText(g, row.Text, SmallFont, row.Bounds, hover ? Theme.Text : Theme.SectionText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | Clipped); break;
            case Kind.Section: TextRenderer.DrawText(g, row.Text, SmallFont, row.Bounds, Theme.SectionText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | Clipped); break;
            case Kind.Friend: DrawFriend(g, row, hover); break;
        }
    }

    Font SmallFont => _fonts.Small;

    /// <summary>A assinatura: com a barra aberta, o logo inteiro (arte em <see cref="UiBrand"/>), à esquerda; no trilho só o gato (a seta ao lado continua abrindo).</summary>
    void DrawBrand(Graphics g)
    {
        int size = V(BrandHeightNow - 4), top = V(BrandTopAt96) + (V(BrandHeightNow) - size) / 2;
        if (Rail) { UiBrand.DrawMark(g, new Rectangle((ClientSize.Width - size) / 2 - Dp(6), top, size, size)); return; }
        // medido pela barra aberta, não pela largura de agora: na animação de abrir/fechar o logo não muda de tamanho a cada quadro, só é cortado
        var room = new Rectangle(Dp(14), top, Dp(OpenAt96) - Dp(14) - Dp(44), size);
        var saved = g.Save();
        g.SetClip(new Rectangle(room.X, room.Y, Math.Max(0, ClientSize.Width - room.X - Dp(44)), room.Height));
        UiBrand.DrawWordmark(g, room);
        g.Restore(saved);
    }

    /// <summary>O botão de tema na fileira de ferramentas: 2:1 como no desenho (104 × 52), centrado no que sobra da fileira.</summary>
    static Rectangle ThemeToggleRect(Rectangle row, int dp48)
    {
        int width = Math.Min(dp48, row.Width - dp48 / 6), height = width / 2;
        return new Rectangle(row.X + (row.Width - width) / 2, row.Y + (row.Height - height) / 2, width, height);
    }

    /// <summary>Aberta: o botão inteiro. No trilho (64 px) só cabe o puxador, com a lua ou o sol.</summary>
    void DrawThemeToggle(Graphics g, Rectangle r, bool hover)
    {
        if (!Rail) { ThemeToggleArt.Draw(g, ThemeToggleRect(r, Dp(48)), _themeMotion.Value, hover, focused: Focused && ShowFocusCues && _rows.Count > _focusRow && _rows[_focusRow].Kind == Kind.Theme); return; }
        FillItem(g, r, selected: false, hover);
        ThemeToggleArt.DrawThumbOnly(g, IconBounds(r), _themeMotion.Value);
    }

    /// <summary>Dica de primeira vez: uma bolinha no botão de recolher até a pessoa usá-lo (ou desligar as dicas nos Ajustes).</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool DicaToggle
    {
        get => _dicaToggle;
        set { if (_dicaToggle == value) return; _dicaToggle = value; Invalidate(); }
    }
    bool _dicaToggle;

    /// <summary>
    /// O botão de recolher é um ícone de painel lateral (o desenho que os apps usam para isso), não uma seta solta: a seta não dizia o que fazia.
    /// Aberta o painel esquerdo vem preenchido; no trilho só o contorno.
    /// </summary>
    void DrawToggle(Graphics g, Rectangle r, bool hover)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (hover) { using var path = Theme.RoundedPath(r, Dp(8)); using var back = new SolidBrush(Theme.SurfaceInput); g.FillPath(back, path); }
        int width = Dp(Rail ? 12 : 16), height = Dp(Rail ? 10 : 13); // no trilho cabe só onde ficava a seta, ao lado do gato
        int left = Rail ? r.Right - Dp(17) : r.X + (r.Width - width) / 2;
        var icon = new Rectangle(left, r.Y + (r.Height - height) / 2, width, height);
        var color = hover ? Theme.Text : Theme.TextMuted;
        using (var outline = Theme.RoundedPath(icon, Dp(3))) using (var pen = new Pen(color, Math.Max(1.4f, Dp(1)))) g.DrawPath(pen, outline);
        int divider = icon.X + icon.Width * 2 / 5;
        using (var pen = new Pen(color, Math.Max(1.4f, Dp(1)))) g.DrawLine(pen, divider, icon.Y + Dp(2), divider, icon.Bottom - Dp(2));
        if (!Rail) { using var pane = new SolidBrush(hover ? Theme.TextMuted : Theme.Border); g.FillRectangle(pane, icon.X + Dp(2), icon.Y + Dp(3), divider - icon.X - Dp(3), icon.Height - Dp(6)); }
        if (!_dicaToggle) return;
        using var dot = new SolidBrush(Theme.Accent);
        g.FillEllipse(dot, icon.Right - Dp(3), icon.Y - Dp(4), Dp(7), Dp(7));
    }

    /// <summary>Dica do botão de recolher; a explicação extra só vem enquanto a dica de primeira vez está ligada.</summary>
    string ToggleTooltip()
    {
        string basico = _collapsed ? "Abrir a barra lateral" : "Recolher a barra lateral";
        if (!_dicaToggle) return basico;
        return basico + (_collapsed ? "\nA barra também abre ao passar o mouse nos ícones." : "\nFica só com os ícones. Passe o mouse neles para abrir de novo.");
    }

    /// <summary>Selo "beta" pequeno no canto de cima à direita do item Filme (spec 010: o modo filme ainda está em teste).</summary>
    void DrawBeta(Graphics g, Rectangle r) => DrawSelo(g, r, "BETA", Theme.Accent);

    /// <summary>Selo no fim da linha (BETA do Cinema, NOVO do Enviar arquivo).</summary>
    void DrawSelo(Graphics g, Rectangle r, string texto, Color fundo)
    {
        // no trilho (só o ícone) o selo com texto cobria o ícone do Filme: vira uma bolinha no canto (diagnóstico de UX, item 6, #138)
        if (Rail)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var dot = new SolidBrush(fundo);
            g.FillEllipse(dot, r.Right - Dp(14), r.Y + Dp(4), Dp(8), Dp(8));
            return;
        }
        var f = _fonts.Badge;
        var sz = TextRenderer.MeasureText(g, texto, f, Size.Empty, TextFormatFlags.NoPadding);
        var box = new Rectangle(r.Right - sz.Width - Dp(10), r.Y + Dp(3), sz.Width + Dp(6), sz.Height + Dp(1));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.RoundedPath(box, Dp(3))) using (var b = new SolidBrush(fundo)) g.FillPath(b, path);
        TextRenderer.DrawText(g, texto, f, box, Theme.AccentText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    void DrawNav(Graphics g, Rectangle r, UiIcon glyph, string text, bool selected, bool hover)
    {
        FillItem(g, r, selected, hover);
        var icon = new Rectangle(r.X, r.Y, Math.Min(r.Width, Dp(40)), r.Height);
        UiIcons.Draw(g, glyph, Rectangle.Inflate(icon, -Dp(9), -Dp(9)), selected || glyph == UiIcon.Broadcast && _self != null ? Theme.Accent : Theme.TextMuted);
        if (Rail || r.Width < Dp(100)) return;
        TextRenderer.DrawText(g, text, Font, new Rectangle(icon.Right, r.Y, r.Width - icon.Width, r.Height), Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    /// <summary>Discreta: tudo em cinza, menor que os outros itens; acende em verde por 4 s depois do clique.</summary>
    void DrawProblem(Graphics g, Rectangle r, bool hover)
    {
        bool flash = DateTime.Now < _problemFlashUntil;
        FillItem(g, r, selected: false, hover);
        var color = flash ? Theme.Ok : hover ? Theme.Text : Theme.TextMuted;
        var icon = new Rectangle(r.X, r.Y, Math.Min(r.Width, Dp(40)), r.Height);
        UiIcons.Draw(g, flash ? UiIcon.Check : UiIcon.Problem, IconBounds(icon), color);
        if (Rail || r.Width < Dp(100)) return;
        TextRenderer.DrawText(g, flash ? "Problema marcado" : "Marcar problema", SmallFont, new Rectangle(icon.Right, r.Y, r.Width - icon.Width, r.Height), color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    /// <summary>
    /// Discreto como a bandeira enquanto não há nada novo ("Procurar atualização"); com versão nova, "Atualizar agora" em azul e uma bolinha no ícone (também no trilho),
    /// para dar para atualizar com um clique sem abrir os Ajustes.
    /// </summary>
    void DrawUpdate(Graphics g, Rectangle r, bool hover)
    {
        bool nova = _update == UpdateState.Available;
        FillItem(g, r, selected: false, hover);
        var color = nova ? Theme.Accent : hover ? Theme.Text : Theme.TextMuted;
        var icon = new Rectangle(r.X, r.Y, Math.Min(r.Width, Dp(40)), r.Height);
        UiIcons.Draw(g, UiIcon.Update, IconBounds(icon), color);
        if (nova)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int d = Dp(7);
            using var dot = new SolidBrush(Theme.Accent);
            g.FillEllipse(dot, icon.X + icon.Width / 2 + Dp(5), icon.Y + Dp(5), d, d);
        }
        if (Rail || r.Width < Dp(100)) return;
        string texto = _update switch
        {
            UpdateState.Checking => "Procurando...",
            UpdateState.Available => "Atualizar agora",
            UpdateState.Updating => "Atualizando...",
            _ => "Procurar atualização",
        };
        var font = nova ? _fonts.Bold : SmallFont;
        TextRenderer.DrawText(g, texto, font, new Rectangle(icon.Right, r.Y, r.Width - icon.Width, r.Height), nova && hover ? Theme.Text : color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    /// <summary>O pulso da atividade recente; com novidade, fica na cor de destaque e ganha o ponto no canto (como o sino).</summary>
    void DrawAtividade(Graphics g, Rectangle r, bool hover)
    {
        FillItem(g, r, selected: false, hover);
        var color = _atividadeNova ? Theme.Accent : hover ? Theme.Text : Theme.TextMuted;
        var icon = new Rectangle(r.X, r.Y, Math.Min(r.Width, Dp(40)), r.Height);
        var box = new Rectangle(icon.X + (icon.Width - Dp(16)) / 2, icon.Y + (icon.Height - Dp(16)) / 2, Dp(16), Dp(16));
        UiIcons.Draw(g, UiIcon.Activity, box, color);
        if (_atividadeNova)
        {
            int d = Dp(7);
            using var dot = new SolidBrush(Theme.Accent);
            g.FillEllipse(dot, box.Right - d / 2, box.Y - d / 3, d, d);
        }
        if (Rail || r.Width < Dp(100)) return;
        TextRenderer.DrawText(g, "Atividade recente", SmallFont, new Rectangle(icon.Right, r.Y, r.Width - icon.Width, r.Height), color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    /// <summary>
    /// O sino das notificações, desenhado à mão (nenhuma fonte garante o glifo): cinza quando ligado; silenciado fica em destaque e cortado por uma barra,
    /// com o texto "Silenciadas até 15:30". Um ponto no canto avisa de versão obrigatória enquanto o alerta está calado.
    /// </summary>
    void DrawBell(Graphics g, Rectangle r, bool hover)
    {
        FillItem(g, r, selected: false, hover);
        var color = _bellSilenced ? Theme.Warn : hover ? Theme.Text : Theme.TextMuted;
        var icon = new Rectangle(r.X, r.Y, Math.Min(r.Width, Dp(40)), r.Height);
        var box = new Rectangle(icon.X + (icon.Width - Dp(16)) / 2, icon.Y + (icon.Height - Dp(16)) / 2, Dp(16), Dp(16));
        UiIcons.Draw(g, _bellSilenced ? UiIcon.MutedBell : UiIcon.Bell, box, color);
        if (_bellDot)
        {
            int d = Dp(7);
            using var dot = new SolidBrush(Theme.Accent);
            g.FillEllipse(dot, box.Right - d / 2, box.Y - d / 3, d, d);
        }
        if (Rail || r.Width < Dp(100)) return;
        TextRenderer.DrawText(g, _bellText, SmallFont, new Rectangle(icon.Right, r.Y, r.Width - icon.Width, r.Height), color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    /// <summary>
    /// "Diagnóstico" como item de navegação (escudo e nome), com o estado na ponta: ✓ verde, ✗ vermelho ou ○ cinza. No trilho o estado vira um pontinho no canto do escudo.
    /// O detalhe (a frase curta) fica na dica.
    /// </summary>
    void DrawDiagNav(Graphics g, Rectangle r, bool hover)
    {
        var (glyph, glyphColor) = _diag switch
        {
            DiagState.Ok => (UiIcon.Check, Theme.Ok),
            DiagState.Fail => (UiIcon.Failure, Theme.Error),
            _ => (UiIcon.Pending, Theme.TextMuted),
        };
        DrawNav(g, r, UiIcon.Shield, "Diagnóstico", false, hover);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Rail)
        {
            int d = Dp(8);
            using var dot = new SolidBrush(glyphColor);
            g.FillEllipse(dot, r.Right - Dp(14), r.Y + Dp(4), d, d);
            return;
        }
        int size = Dp(16);
        UiIcons.Draw(g, glyph, new Rectangle(r.Right - size - Dp(12), r.Y + (r.Height - size) / 2, size, size), glyphColor);
    }

    /// <summary>✓ verde, ✗ vermelho ou ○ cinza, com a frase curta ao lado (no trilho, só o símbolo). Vermelho também pinta o texto: tem que ser visto.</summary>
    void DrawDiag(Graphics g, Rectangle r, bool hover)
    {
        FillItem(g, r, selected: false, hover);
        var (glyph, glyphColor, textColor) = _diag switch
        {
            DiagState.Ok => (UiIcon.Check, Theme.Ok, Theme.TextMuted),
            DiagState.Fail => (UiIcon.Failure, Theme.Error, Theme.Error),
            _ => (UiIcon.Pending, Theme.TextMuted, Theme.TextMuted),
        };
        var icon = new Rectangle(r.X, r.Y, Math.Min(r.Width, Dp(40)), r.Height);
        var bold = _fonts.Bold;
        UiIcons.Draw(g, glyph, IconBounds(icon), glyphColor);
        if (Rail || r.Width < Dp(100)) return;
        TextRenderer.DrawText(g, _diagTitle, SmallFont, new Rectangle(icon.Right, r.Y, r.Width - icon.Width, r.Height), hover ? Theme.Text : textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    Rectangle IconBounds(Rectangle r)
    {
        int size = Math.Min(Dp(20), Math.Min(r.Width, r.Height) - Dp(4));
        return new Rectangle(r.X + (r.Width - size) / 2, r.Y + (r.Height - size) / 2, size, size);
    }

    /// <summary>
    /// Pílula do item aberto (cheia, sem a barrinha de antes) ou de passar o mouse. Não mexe no recorte: o realce de um amigo "Em tela" chamava ResetClip
    /// e o resto da linha vazava por cima das ferramentas do rodapé.
    /// </summary>
    void FillItem(Graphics g, Rectangle r, bool selected, bool hover, bool soft = false)
    {
        if (!selected && !hover) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.RoundedPath(r, Dp(8));
        using var back = new SolidBrush(selected ? soft ? Theme.SelectionSoft : Theme.Selection : Theme.SurfaceInput);
        g.FillPath(back, path);
    }

    /// <summary>Texto da lista rolável: o TextRenderer só respeita o recorte da lista com PreserveGraphicsClipping (sem ele, nomes vazavam sobre o rodapé).</summary>
    const TextFormatFlags Clipped = TextFormatFlags.PreserveGraphicsClipping;
    /// <summary>Quanto do fundo da barra cobre o avatar de quem está offline (0 a 255).</summary>
    const int OfflineVeilAlpha = 150;

    void DrawFriend(Graphics g, Row row, bool hover)
    {
        var f = row.Friend!;
        var r = row.Bounds;
        FillItem(g, r, f.OnScreen, hover && f.Live, soft: true);
        var av = AvatarRect(r);
        // só o avatar sob o mouse anda; os outros ficam no primeiro quadro (antes todos avançavam 1 quadro por segundo, o que parecia GIF lento)
        AvatarBadge.Draw(g, av, f.Avatar, f.Nick, hover && f.Avatar is { Animated: true } ? AvatarBadge.NowMs : 0);
        if (f.Offline) { g.SmoothingMode = SmoothingMode.AntiAlias; using var veu = new SolidBrush(Color.FromArgb(OfflineVeilAlpha, Theme.SurfaceRaised)); g.FillEllipse(veu, av); } // esmaecido: o fundo da barra por cima, sem tom fixo
        else if (f.Live) { using var ring = new Pen(Theme.Error, Dp(2)); g.SmoothingMode = SmoothingMode.AntiAlias; g.DrawEllipse(ring, Rectangle.Inflate(av, Dp(2), Dp(2))); }
        else if (Rail && f.OnScreen) DrawWatchingEye(g, av);
        else DrawOnlineDot(g, av);
        if (Rail) return;
        int textX = av.Right + Dp(10);
        int tagWidth = f.Live && !f.OnScreen ? Dp(66) : 0; // o botão "Assistir" do desenho; quem já está em tela não tem botão
        var text = new Rectangle(textX, r.Y, Math.Max(0, r.Right - textX - tagWidth - Dp(6)), r.Height);
        var bold = _fonts.Bold;
        TextRenderer.DrawText(g, f.Nick, bold, new Rectangle(text.X, text.Y + Dp(4), text.Width, text.Height / 2 - Dp(2)), f.Offline ? Theme.TextMuted : Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | Clipped);
        DrawFriendStatus(g, f, new Rectangle(text.X, text.Y + text.Height / 2, text.Width, text.Height / 2 - Dp(4)));
        if (f.Live && !f.OnScreen) DrawWatchButton(g, new Rectangle(r.Right - tagWidth - Dp(4), r.Y + (r.Height - Dp(24)) / 2, tagWidth, Dp(24)), hover);
    }

    /// <summary>A segunda linha do amigo: bolinha vermelha e "AO VIVO", o olho e "Assistindo", ou o detalhe de sempre ("online").</summary>
    void DrawFriendStatus(Graphics g, SideFriend f, Rectangle line)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int icon = Dp(14), gap = Dp(5);
        var textRect = new Rectangle(line.X + (f.Live ? icon + gap : 0), line.Y, Math.Max(0, line.Width - (f.Live ? icon + gap : 0)), line.Height);
        var iconRect = new Rectangle(line.X, line.Y + (line.Height - icon) / 2, icon, icon);
        var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | Clipped;
        if (f.OnScreen) { UiIcons.Draw(g, UiIcon.Eye, iconRect, Theme.Accent); TextRenderer.DrawText(g, "Assistindo", SmallFont, textRect, Theme.TextMuted, flags); return; }
        if (f.Live)
        {
            using (var red = new SolidBrush(Theme.Error)) g.FillEllipse(red, Rectangle.Inflate(iconRect, -Dp(3), -Dp(3)));
            TextRenderer.DrawText(g, "AO VIVO", SmallFont, textRect, Theme.Error, flags);
            return;
        }
        TextRenderer.DrawText(g, f.Detail, SmallFont, line, Theme.TextMuted, flags);
    }

    void DrawWatchButton(Graphics g, Rectangle r, bool hover)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.RoundedPath(r, r.Height / 2)) using (var fill = new SolidBrush(hover ? Theme.AccentHover : Theme.Accent)) g.FillPath(fill, path);
        TextRenderer.DrawText(g, "Assistir", SmallFont, r, Theme.AccentText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | Clipped);
    }

    /// <summary>No trilho não há texto "Você assiste": quem está em tela leva um olhinho no canto da foto (desenho).</summary>
    void DrawWatchingEye(Graphics g, Rectangle avatar)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int d = Dp(16);
        var badge = new Rectangle(avatar.Right - d + Dp(4), avatar.Bottom - d + Dp(4), d, d);
        using (var halo = new SolidBrush(BackColor)) g.FillEllipse(halo, Rectangle.Inflate(badge, Dp(2), Dp(2)));
        using (var fill = new SolidBrush(Theme.Surface)) g.FillEllipse(fill, badge);
        UiIcons.Draw(g, UiIcon.Eye, Rectangle.Inflate(badge, -Dp(2), -Dp(2)), Theme.Text);
    }

    /// <summary>A bolinha verde de "online" no canto de baixo da foto do amigo, com um anel da cor da barra para destacar da foto.</summary>
    void DrawOnlineDot(Graphics g, Rectangle avatar)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int d = Dp(9);
        var dot = new Rectangle(avatar.Right - d + Dp(2), avatar.Bottom - d + Dp(2), d, d);
        using (var halo = new SolidBrush(BackColor)) g.FillEllipse(halo, Rectangle.Inflate(dot, Dp(2), Dp(2)));
        using var ok = new SolidBrush(Theme.Ok);
        g.FillEllipse(ok, dot);
    }

    /// <summary>"Minha live": a bolinha vermelha de quem está no ar, o jogo e quantos assistem, e o botão para ver (ou fechar) a prévia da própria transmissão.</summary>
    void DrawSelf(Graphics g, Rectangle r, bool hover)
    {
        var s = _self!;
        FillItem(g, r, s.Previewing, hover);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int d = Dp(14);
        var dot = new Rectangle(Rail ? r.X + (r.Width - d) / 2 : r.X + Dp(13), r.Y + (r.Height - d) / 2, d, d);
        using (var red = new SolidBrush(Theme.Error)) g.FillEllipse(red, dot);
        using (var ring = new Pen(Theme.LiveHalo, Dp(3))) g.DrawEllipse(ring, Rectangle.Inflate(dot, Dp(3), Dp(3)));
        if (Rail || r.Width < Dp(100)) return;
        int tagWidth = Dp(s.Previewing ? 64 : 40);
        int textX = r.X + Dp(40);
        var text = new Rectangle(textX, r.Y, Math.Max(0, r.Right - textX - tagWidth - Dp(10)), r.Height);
        var bold = _fonts.Bold;
        string watching = s.Watching switch { 0 => "ninguém ainda", 1 => "1 assistindo", var n => $"{n} assistindo" };
        TextRenderer.DrawText(g, "Você está ao vivo", bold, new Rectangle(text.X, text.Y + Dp(4), text.Width, text.Height / 2 - Dp(2)), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, $"{s.Game} · {watching}", SmallFont, new Rectangle(text.X, text.Y + text.Height / 2, text.Width, text.Height / 2 - Dp(4)), Theme.TextMuted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        DrawTag(g, new Rectangle(r.Right - tagWidth - Dp(4), r.Y + (r.Height - Dp(16)) / 2, tagWidth, Dp(16)), s.Previewing ? "FECHAR" : "VER", s.Previewing);
    }

    /// <summary>Um quadrado vermelho (o "parar" de sempre) e, com a barra aberta, o texto. Fica logo abaixo de "Você está ao vivo".</summary>
    void DrawStopLive(Graphics g, Rectangle r, bool hover)
    {
        FillItem(g, r, false, hover);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int d = Dp(12);
        var square = new Rectangle(Rail ? r.X + (r.Width - d) / 2 : r.X + Dp(14), r.Y + (r.Height - d) / 2, d, d);
        using (var path = Theme.RoundedPath(square, Dp(2))) using (var red = new SolidBrush(Theme.Error)) g.FillPath(red, path);
        if (Rail) return;
        int textX = r.X + Dp(40);
        TextRenderer.DrawText(g, "Encerrar live", SmallFont, new Rectangle(textX, r.Y, Math.Max(0, r.Right - textX - Dp(6)), r.Height), hover ? Theme.Text : Theme.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    void DrawTag(Graphics g, Rectangle r, string text, bool accent)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.RoundedPath(r, Dp(4))) using (var fill = new SolidBrush(accent ? Theme.Accent : Theme.Error)) g.FillPath(fill, path);
        var small = _fonts.Badge;
        TextRenderer.DrawText(g, text, small, r, Theme.AccentText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | Clipped);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new NavigationAccess(this);
    sealed class NavigationAccess(SidePanel owner) : ControlAccessibleObject(owner)
    {
        List<Row> Actions => owner.BuildRows().Where(r => r.Kind != Kind.Section).ToList();
        public override int GetChildCount() => Actions.Count;
        public override AccessibleObject? GetChild(int index) => index >= 0 && index < Actions.Count ? new ActionAccess(owner, Actions[index], this) : null;
    }
    sealed class ActionAccess(SidePanel owner, Row row, AccessibleObject parent) : AccessibleObject
    {
        public override string? Name { get => owner.LabelFor(row); set { } }
        public override AccessibleRole Role => AccessibleRole.PushButton;
        public override AccessibleObject? Parent => parent;
        public override Rectangle Bounds => owner.RectangleToScreen(row.Kind == Kind.Friend ? Rectangle.Intersect(row.Bounds, owner._friendsViewport) : row.Bounds);
        public override string? DefaultAction => "Abrir";
        public override void DoDefaultAction() => owner.Activate(row);
    }
}
