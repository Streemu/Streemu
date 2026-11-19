using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Streemu.Util;

/// <summary>
/// Paleta Streemu (spec 014) aplicada aos controles do WinForms. Fica fora do MainForm.cs de propósito:
/// esse arquivo concentra conflitos entre os três, então a tela só chama <see cref="Apply"/> uma vez.
/// Uso: <c>Theme.Apply(this);</c>
/// </summary>
public static class Theme
{
    // Identidade Streemu (spec 014), compartilhada pelas superfícies nativas. Os valores vêm da paleta ativa (escura por padrão; ver ThemePalette e ThemeSwitch).
    static ThemePalette _current = ThemePalette.Dark;
    public static ThemePalette Current => _current;
    public static bool IsLight => ReferenceEquals(_current, ThemePalette.Light);
    /// <summary>Disparado depois que a paleta muda: controles desenhados à mão que guardam imagem ou pincel refazem o que guardaram.</summary>
    public static event Action? Changed;

    /// <summary>Troca a paleta ativa. Só muda os valores lidos daqui em diante; quem já copiou uma cor para um controle é repintado por <see cref="ThemeSwitch"/>.</summary>
    public static void Use(bool light)
    {
        var next = light ? ThemePalette.Light : ThemePalette.Dark;
        if (ReferenceEquals(next, _current)) return;
        _current = next;
        Changed?.Invoke();
    }

    public static Color Surface => _current.Surface;
    public static Color SurfaceRaised => _current.SurfaceRaised;
    public static Color SurfaceInput => _current.SurfaceInput;
    public static Color Border => _current.Border;
    public static Color Text => _current.Text;
    public static Color TextMuted => _current.TextMuted;
    public static Color Accent => _current.Accent;
    public static Color AccentHover => _current.AccentHover;
    /// <summary>Botão roxo mais apagado que o principal ("Baixar e assistir" e "Escolher arquivo" na entrada do Cinema).</summary>
    public static Color AccentMuted => _current.AccentMuted;
    public static Color AccentMutedHover => _current.AccentMutedHover;
    public static readonly Color AccentText = Color.White; // texto sobre o destaque e sobre o vermelho (os dois temas têm destaque escuro o bastante)
    public static Color Neutral => _current.Neutral;
    public static Color NeutralHover => _current.NeutralHover;
    /// <summary>Texto sobre <see cref="Neutral"/> (branco no escuro, cinza-roxo escuro no claro).</summary>
    public static Color NeutralText => _current.NeutralText;
    public static Color Ok => _current.Ok;
    public static Color Warn => _current.Warn;
    public static Color Error => _current.Error;
    public static Color TitleBar => _current.TitleBar; // a barra de título do desenho
    // cartões e botões de escolha da página Transmitir (desenho): borda, escolhido, e o aviso âmbar
    public static Color CardBorder => _current.CardBorder;
    public static Color CardSelected => _current.CardSelected;
    public static Color CardSelectedBorder => _current.CardSelectedBorder;
    public static Color WarnSurface => _current.WarnSurface;
    public static Color WarnBorder => _current.WarnBorder;
    public static Color Backdrop => _current.Backdrop;
    public static readonly Color VideoMargin = Color.Black;
    public static readonly Color SuccessSurface = ColorTranslator.FromHtml("#15963D");
    /// <summary>Lilás claro do nome Streemu e dos títulos de seção da lateral (mockups 1–24).</summary>
    public static Color AccentSoft => _current.AccentSoft;
    public static Color SectionText => AccentSoft;
    /// <summary>Contorno bem leve do gato da marca: a arte é roxo escuro e, sem ele, o corpo some no fundo da lateral escura (pedido do usuário, 13/11). No claro não há contorno.</summary>
    public static Color MarkOutline => _current.MarkOutline;
    /// <summary>Pílula do item aberto na lateral: cheia, como nos mockups (antes era 60 de opacidade com uma barrinha à esquerda).</summary>
    public static Color Selection => _current.Selection;
    /// <summary>Realce mais leve para linhas que estão "em uso" sem ser a página aberta (amigo que você assiste).</summary>
    public static Color SelectionSoft => _current.SelectionSoft;
    public static Color OptionSelection => _current.OptionSelection;
    public static Color ScrollThumb => _current.ScrollThumb;
    /// <summary>A capa provisória do Cinema (Ui/CinemaPoster.cs): o ícone meio apagado e o brilho que some nas bordas.</summary>
    public static Color PosterIcon => _current.PosterIcon;
    public static Color PosterGlow(Color c) => Color.FromArgb(120, c);
    public static Color PosterGlowEdge(Color c) => Color.FromArgb(0, c);
    public static Color SourceCaptionShade => Color.FromArgb(210, 14, 10, 24);
    public static Color SourceCaptionClear => Color.FromArgb(0, 0, 0, 0);
    public static Color LiveHalo => _current.LiveHalo;
    public static Color Divider => _current.Divider;
    public static Color WatchedVeil => _current.WatchedVeil;
    /// <summary>Linha entre grupos da lateral (o <see cref="Divider"/> some no fundo da lateral escura).</summary>
    public static Color Separator => _current.Separator;
    public static Color SwitchThumbOn => _current.SwitchThumbOn;
    public static Color SwitchThumbOff => _current.SwitchThumbOff;


    // Sobre vídeo, sobre vermelho/destaque e no selo AO VIVO: o texto precisa ser branco puro para ler em cima de qualquer jogo.
    public static readonly Color OnMedia = Color.White;
    public static Color OnMediaSoft => Color.FromArgb(235, OnMedia);
    public static Color OnMediaDim => Color.FromArgb(120, OnMedia);
    public static Color MediaEdge => Color.FromArgb(200, OnMedia);
    /// <summary>Peça do palco sob o arraste (soltar aqui troca de lugar).</summary>
    public static Color DropTarget => Color.FromArgb(150, Accent);
    /// <summary>Texto da linha que está sendo arrastada na fila do Cinema (Ui/FilaFilmeView.cs): meio apagado enquanto anda.</summary>
    public static Color Dragged(Color c) => Color.FromArgb(110, c);

    /// <summary>Fundo das iniciais de quem não tem foto: a cor sai do apelido, então cada amigo mantém a dele.</summary>
    public static readonly Color[] AvatarPalette =
    [
        ColorTranslator.FromHtml("#5865F2"), ColorTranslator.FromHtml("#23A559"), ColorTranslator.FromHtml("#EB459E"),
        ColorTranslator.FromHtml("#F0B232"), ColorTranslator.FromHtml("#3BA5C3"), ColorTranslator.FromHtml("#9B59B6"),
        ColorTranslator.FromHtml("#E67E22"), ColorTranslator.FromHtml("#1ABC9C"),
    ];

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Pinta a janela e todos os filhos; os botões em <paramref name="primary"/> ganham a cor de destaque.</summary>
    public static void Apply(Form form, params Button[] primary)
    {
        form.BackColor = Surface;
        form.ForeColor = Text;
        Paint(form, new HashSet<Button>(primary));
        form.HandleCreated += (_, _) => ApplyCaption(form.Handle);
        if (form.IsHandleCreated) ApplyCaption(form.Handle); // se a janela já existe, o evento acima não vem mais
    }

    public static void Paint(Control parent, HashSet<Button> primary)
    {
        foreach (Control c in parent.Controls)
        {
            PaintOne(c, primary);
            Paint(c, primary);
        }
        if (parent is Ui.SettingsView settings) settings.ApplyAppearance();
        if (parent is Ui.GoLiveView preparation) preparation.ApplyAppearance();
    }

    /// <summary>
    /// Qualquer controle dentro de um cartão (GroupBox) leva o fundo do cartão, não só os filhos diretos e os de uma tabela: rótulos e caixas de seleção em
    /// painéis de linha ou em tabelas dentro de tabelas saíam com o fundo da janela, e apareciam como retângulos mais claros (diagnóstico de UX, item 5).
    /// </summary>
    static bool HasCardAncestor(Control c)
    {
        for (var p = c.Parent; p != null; p = p.Parent) if (p is GroupBox) return true;
        return false;
    }

    static void PaintOne(Control c, HashSet<Button> primary)
    {
        if (c.AccessibleRole == AccessibleRole.Separator) { c.BackColor = Border; return; }
        c.ForeColor = c.ForeColor == SystemColors.GrayText ? TextMuted : Text;
        c.BackColor = c switch
        {
            GroupBox => SurfaceRaised,
            TextBox or ComboBox => SurfaceInput,
            _ => HasCardAncestor(c) ? SurfaceRaised : Surface,
        };
        if (c is Button b && c is not Ui.CategoryPicker) StyleButton(b, primary.Contains(b));
        if (c is ComboBox box) box.FlatStyle = FlatStyle.Flat; // sem recorte arredondado: cortava o texto e a seta do menu
        // dentro de um CampoArredondado quem pinta a borda é o campo: o recorte por região quebrava a borda nativa (2026-11-18)
        if (c is TextBox { Multiline: false } tb && tb.Parent is not Ui.CampoArredondado) { tb.BorderStyle = BorderStyle.FixedSingle; RoundRegion(tb); }
    }

    /// <summary>Retângulo de cantos arredondados (raio em pixels reais), para os controles desenhados à mão.</summary>
    public static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        int d = Math.Max(1, Math.Min(Math.Min(r.Width, r.Height), Math.Max(2, radius * 2)));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    sealed class ButtonState
    {
        public bool Hover, Down;
        /// <summary>O foco chegou pelo teclado (Tab, setas): só então o contorno e o retângulo pontilhado de foco aparecem; depois de um clique, não.</summary>
        public bool Keyboard;
        public readonly UiMotion Motion;
        public ButtonState(Button button) => Motion = new(button, button.Invalidate);
        public void Update(Button button) => Motion.To(!button.Enabled ? 0 : Down ? 1 : Hover ? .65f : 0, Down ? UiMotion.PressMs : UiMotion.HoverMs);
    }
    static readonly ConditionalWeakTable<Button, ButtonState> ButtonStates = new();
    static readonly ConditionalWeakTable<Control, object> Rounded = new();

    /// <summary>
    /// Botão de cantos arredondados e serrilhado suave: por cima do desenho do botão plano do Windows, que é quadrado, a borda arredondada é pintada
    /// no evento Paint (cor normal, de passar o mouse, de apertado e de desabilitado). Vale para todo botão que passa por <see cref="StyleButton"/>.
    /// </summary>
    static void RoundPaint(Button b)
    {
        if (ButtonStates.TryGetValue(b, out _)) return;
        var state = new ButtonState(b);
        ButtonStates.Add(b, state);
        b.MouseEnter += (_, _) => { state.Hover = true; state.Update(b); };
        b.MouseLeave += (_, _) => { state.Hover = state.Down = false; state.Update(b); };
        b.MouseDown += (_, e) => { state.Keyboard = false; if (e.Button == MouseButtons.Left) { state.Down = true; state.Update(b); } };
        b.MouseUp += (_, _) => { state.Down = false; state.Update(b); };
        b.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Tab or Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Space or Keys.Enter) { state.Keyboard = true; b.Invalidate(); }
            if (e.KeyCode == Keys.Space) { state.Down = true; state.Update(b); }
        };
        b.KeyUp += (_, e) => { if (e.KeyCode == Keys.Space) { state.Down = false; state.Update(b); } };
        b.LostFocus += (_, _) => { state.Down = false; state.Keyboard = false; state.Update(b); b.Invalidate(); };
        b.GotFocus += (_, _) => { state.Keyboard = Control.MouseButtons == MouseButtons.None; b.Invalidate(); }; // clique dá foco com o botão do mouse apertado; Tab, não
        b.EnabledChanged += (_, _) => { state.Down = false; state.Update(b); };
        b.VisibleChanged += (_, _) => { if (!b.Visible) { state.Hover = state.Down = false; state.Update(b); } };
        b.MouseCaptureChanged += (_, _) => { if (!b.Capture) { state.Down = false; state.Update(b); } };
        b.Paint += (_, e) => DrawRoundButton(b, state, e.Graphics);
    }

    static void DrawRoundButton(Button b, ButtonState state, Graphics g)
    {
        var back = b.Parent?.BackColor ?? Surface;
        using (var clear = new SolidBrush(back)) g.FillRectangle(clear, 0, 0, b.Width, b.Height);
        Color fill = UiMotion.Blend(b.BackColor, b.FlatAppearance.MouseOverBackColor, Math.Min(1, state.Motion.Value / .65f));
        if (state.Motion.Value > .65f) fill = UiMotion.Blend(fill, SurfaceRaised, (state.Motion.Value - .65f) * .7f);
        if (!b.Enabled) fill = Color.FromArgb((fill.R + back.R) / 2, (fill.G + back.G) / 2, (fill.B + back.B) / 2);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = RoundedPath(new Rectangle(0, 0, b.Width - 1, b.Height - 1), UiScale.Dp(b, UiMetrics.SmallRadius)))
        using (var brush = new SolidBrush(fill))
        {
            g.FillPath(brush, path);
            bool cue = b.Focused && state.Keyboard;
            if (cue || state.Motion.Value > 0)
            {
                using var edge = new Pen(UiMotion.Blend(fill, AccentHover, cue ? .9f : state.Motion.Value * .55f), cue ? UiScale.Dp(b, 2) : 1);
                g.DrawPath(edge, path);
            }
        }
        bool left = b.TextAlign is ContentAlignment.TopLeft or ContentAlignment.MiddleLeft or ContentAlignment.BottomLeft;
        var textBounds = new Rectangle(left ? b.Padding.Left : 0, 0, Math.Max(0, b.Width - (left ? b.Padding.Horizontal : 0)), b.Height);
        if (b.Image != null && !left) { textBounds = DrawButtonImage(b, g, textBounds); left = true; }
        TextRenderer.DrawText(g, b.Text, b.Font, textBounds, b.Enabled ? b.ForeColor : TextMuted,
            (left ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter) | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        if (b.Focused && state.Keyboard) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(b.ClientRectangle, -UiScale.Dp(b, 4), -UiScale.Dp(b, 4)), b.ForeColor, fill);
    }

    /// <summary>Ícone antes do texto centrado (o botão "Começar a transmitir"): o grupo ícone+texto fica centrado e devolve onde o texto vai.</summary>
    static Rectangle DrawButtonImage(Button b, Graphics g, Rectangle textBounds)
    {
        int gap = UiScale.Dp(b, 8), textWidth = TextRenderer.MeasureText(g, b.Text, b.Font).Width;
        int x = Math.Max(0, (b.Width - (b.Image!.Width + gap + textWidth)) / 2);
        g.DrawImage(b.Image, x, (b.Height - b.Image.Height) / 2, b.Image.Width, b.Image.Height);
        int textX = x + b.Image.Width + gap;
        return new Rectangle(textX, 0, Math.Max(0, b.Width - textX), b.Height);
    }

    /// <summary>Campo de texto ou lista com os cantos recortados (a forma do controle acompanha o tamanho dele).</summary>
    static void RoundRegion(Control c)
    {
        if (Rounded.TryGetValue(c, out _)) return;
        Rounded.Add(c, new object());
        c.SizeChanged += (_, _) => ApplyRegion(c);
        ApplyRegion(c);
    }

    static void ApplyRegion(Control c)
    {
        if (c.Width < 4 || c.Height < 4) return;
        using var path = RoundedPath(new Rectangle(0, 0, c.Width, c.Height), UiScale.Dp(c, UiMetrics.SmallRadius));
        c.Region?.Dispose();
        c.Region = new Region(path);
    }

    /// <summary>Para botões criados depois do <see cref="Apply"/> (ex.: a lista do grupo, que muda sozinha).</summary>
    public static void StyleButton(Button b, bool isPrimary)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.UseVisualStyleBackColor = false;
        b.BackColor = isPrimary ? Accent : Neutral;
        b.FlatAppearance.MouseOverBackColor = isPrimary ? AccentHover : NeutralHover;
        b.ForeColor = isPrimary ? AccentText : NeutralText;
        b.Padding = new Padding(10, 3, 10, 3);
        b.Cursor = Cursors.Hand;
        RoundPaint(b);
    }

    // atributo 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1+); em versão antiga a chamada só falha e a barra fica clara
    /// <summary>Barra de título no tema de agora; também é chamada pelo <see cref="ThemeSwitch"/> em cada janela aberta quando o tema muda.</summary>
    public static void ApplyCaption(IntPtr handle)
    {
        int on = IsLight ? 0 : 1;
        _ = DwmSetWindowAttribute(handle, 20, ref on, sizeof(int));
        PaintCaption(handle);
    }

    /// <summary>
    /// A barra de título na cor roxa escura do desenho (#171523) com o texto claro, em vez da barra clara/cinza do Windows. Atributos 34 a 36 existem só no Windows 11
    /// (build 22000+); em versão antiga a chamada falha e fica a barra escura do modo dark.
    /// </summary>
    static void PaintCaption(IntPtr handle)
    {
        int border = ToColorRef(TitleBar), caption = border, text = ToColorRef(Text);
        _ = DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
    }

    static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);
}
