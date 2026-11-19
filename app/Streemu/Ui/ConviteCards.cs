using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>Um convite para uma transmissão privada, como o convidado o vê no Início.</summary>
/// <param name="Conectando">Aceito, esperando o player abrir: o card fica no lugar, com os botões desativados.</param>
public sealed record ConviteVisto(string AnfitriaoId, string Apelido, bool Conectando = false);

/// <summary>
/// Os cards de convite da home (spec 012): cadeado, quem convidou e Aceitar/Recusar. Sem som nem toast (D7); só aparece na tela.
/// Uso: <c>cards.Aceitou += id => Assistir(id); cards.Mostrar(convites);</c>
/// </summary>
public sealed class ConviteCards : FlowLayoutPanel
{
    IReadOnlyList<ConviteVisto> _atuais = Array.Empty<ConviteVisto>();

    public event Action<string>? Aceitou;
    public event Action<string>? Recusou;

    public ConviteCards()
    {
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoSize = true;
        Visible = false;
        Margin = new Padding(0, 6, 0, 6);
    }

    public void Mostrar(IReadOnlyList<ConviteVisto> convites)
    {
        if (convites.SequenceEqual(_atuais)) return;
        var anteriores = _atuais;
        _atuais = convites;
        // Só o estado "conectando" mudou: o card (e o botão sob o mouse) fica onde está. Refazer tudo seria arriscar mexer no layout no meio do clique.
        if (convites.Select(c => c.AnfitriaoId).SequenceEqual(anteriores.Select(c => c.AnfitriaoId)))
        {
            var existentes = Controls.OfType<ConviteCard>().ToList();
            for (int i = 0; i < existentes.Count && i < convites.Count; i++) existentes[i].Conectando = convites[i].Conectando;
            return;
        }
        SuspendLayout();
        foreach (var c in Controls.OfType<ConviteCard>().ToList()) { Controls.Remove(c); c.Dispose(); }
        Controls.Clear();
        foreach (var convite in convites) Controls.Add(Montar(convite));
        foreach (var extra in _extras) Controls.Add(extra);
        Visible = Controls.Count > 0;
        ResumeLayout();
    }

    IReadOnlyList<Control> _extras = Array.Empty<Control>();

    /// <summary>
    /// Outros cards que esperam uma resposta (os pedidos de arquivo, spec 020), depois dos convites. Quem os cria é dono deles: aqui só
    /// entram e saem da lista, nunca são descartados.
    /// </summary>
    public void MostrarExtras(IReadOnlyList<Control> extras)
    {
        if (extras.SequenceEqual(_extras)) return;
        SuspendLayout();
        foreach (var velho in _extras) Controls.Remove(velho);
        _extras = extras;
        foreach (var extra in extras) { extra.Width = Width; extra.Margin = new Padding(0, 0, 0, 8); Controls.Add(extra); }
        Visible = Controls.Count > 0;
        ResumeLayout();
    }

    /// <summary>A largura que a home dá aos blocos; os cards acompanham.</summary>
    public void AjustarLargura(int largura)
    {
        Width = largura;
        foreach (Control c in Controls) c.Width = largura;
    }

    Control Montar(ConviteVisto convite)
    {
        var card = new ConviteCard(convite.Apelido) { Width = Width, Margin = new Padding(0, 0, 0, 8), Conectando = convite.Conectando };
        card.Aceitou += () => Aceitou?.Invoke(convite.AnfitriaoId);
        card.Recusou += () => Recusou?.Invoke(convite.AnfitriaoId);
        return card;
    }
}

/// <summary>Um card: cadeado à esquerda, o texto no meio e os dois botões à direita. Cores e cantos vêm do DESIGN.md (surface-raised, rounded.card).</summary>
sealed class ConviteCard : Control
{
    const int RadiusAt96 = 8, HeightAt96 = 68, PadAt96 = 14;
    readonly string _apelido;
    readonly Button _aceitar = new() { Text = "Aceitar", AutoSize = true };
    readonly Button _recusar = new() { Text = "Recusar", AutoSize = true };

    public event Action? Aceitou;
    public event Action? Recusou;

    bool _conectando;
    /// <summary>Aceito e esperando o player: mesmo lugar e mesma altura, botões desativados e o texto "Conectando com ...".</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Conectando
    {
        get => _conectando;
        set { if (_conectando == value) return; _conectando = value; _aceitar.Enabled = _recusar.Enabled = !value; Invalidate(); }
    }

    public ConviteCard(string apelido)
    {
        _apelido = apelido;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        Height = UiScale.Dp(this, HeightAt96);
        Theme.StyleButton(_aceitar, true);
        Theme.StyleButton(_recusar, false);
        _aceitar.Click += (_, _) => Aceitou?.Invoke();
        _recusar.Click += (_, _) => Recusou?.Invoke();
        Controls.Add(_aceitar);
        Controls.Add(_recusar);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int pad = UiScale.Dp(this, PadAt96);
        _aceitar.Location = new Point(Width - pad - _aceitar.Width, (Height - _aceitar.Height) / 2);
        _recusar.Location = new Point(_aceitar.Left - pad / 2 - _recusar.Width, _aceitar.Top);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), UiScale.Dp(this, RadiusAt96)))
        using (var fill = new SolidBrush(Theme.SurfaceRaised)) g.FillPath(fill, path);
        int pad = UiScale.Dp(this, PadAt96);
        int lockSize = UiScale.Dp(this, 24); // cadeado do conjunto compartilhado (antes um glifo da fonte de símbolos, que podia faltar)
        UiIcons.Draw(g, UiIcon.Lock, new Rectangle(pad, (Height - lockSize) / 2, lockSize, lockSize), Theme.Accent);
        int left = pad * 3;
        int right = _recusar.Left - pad;
        using var bold = new Font(Font, FontStyle.Bold);
        TextRenderer.DrawText(g, _conectando ? $"Conectando com {_apelido}…" : $"{_apelido} convidou você", bold, new Rectangle(left, pad - 2, right - left, bold.Height + 2), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, _conectando ? "Abrindo a transmissão. Espere um instante." : "Transmissão privada: só quem foi convidado vê.", Font, new Rectangle(left, pad + bold.Height + 2, right - left, Font.Height + 2), Theme.TextMuted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }
}
