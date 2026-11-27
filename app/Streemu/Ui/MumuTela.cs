using System.Diagnostics;
using System.Drawing.Drawing2D;
using Streemu.Net;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>Os destinos de link da aba da Mumu (spec 031): só estes abrem no navegador.</summary>
public static class MumuLinks
{
    /// <summary>O perfil oficial do Streemu no Instagram (existe desde 2026-11-27, spec 031, D3).</summary>
    public const string Instagram = "https://www.instagram.com/streemuapp/";
}

/// <summary>
/// As poses da Mumu acordada (spec 031, 5). Recado com <c>sprite</c> conhecido usa aquela pose (a <c>pulando</c> é só do recado de lançamento da v1 e nunca entra no sorteio).
/// Sem, sai uma das <see cref="Sorteaveis"/> que ainda não saíram; usadas todas, a roda recomeça. O mesmo recado mantém a pose enquanto for o mais novo.
/// </summary>
public static class MumuPoses
{
    public const string Pulando = "mumu-pulando.png";
    public static readonly string[] Sorteaveis = ["mumu-acenando.png", "mumu-carta.png", "mumu-animada.png", "mumu-apontando.png", "mumu-balao.png", "mumu-prancheta.png"];

    /// <summary>O arquivo da pose pelo nome curto do recado ("pulando" = <see cref="Pulando"/>); nulo se não for uma pose conhecida.</summary>
    public static string? Arquivo(string? sprite)
    {
        if (string.IsNullOrWhiteSpace(sprite)) return null;
        string nome = "mumu-" + sprite.Trim().ToLowerInvariant() + ".png";
        return nome == Pulando || Sorteaveis.Contains(nome) ? nome : null;
    }

    /// <summary>
    /// A pose do recado <paramref name="recado"/>. <paramref name="idGuardado"/> e <paramref name="poseGuardada"/> são a última escolha; <paramref name="usadas"/> ganha a pose
    /// quando sai uma nova. Exemplo: <c>MumuPoses.Escolher(recado, state.MumuPoseRecado, state.MumuPose, usadas, Random.Shared)</c>.
    /// </summary>
    public static string Escolher(RecadoDaMumu recado, string? idGuardado, string? poseGuardada, List<string> usadas, Random sorte)
    {
        if (Arquivo(recado.Sprite) is { } fixa) return fixa;
        if (idGuardado == recado.Id && poseGuardada != null && Sorteaveis.Contains(poseGuardada)) return poseGuardada;
        usadas.RemoveAll(u => !Sorteaveis.Contains(u));
        var restantes = Sorteaveis.Where(p => !usadas.Contains(p)).ToList();
        if (restantes.Count == 0)
        {
            usadas.Clear();
            restantes = Sorteaveis.Where(p => p != poseGuardada).ToList(); // a roda recomeça sem repetir a última na hora
        }
        string pose = restantes[sorte.Next(restantes.Count)];
        usadas.Add(pose);
        return pose;
    }
}

/// <summary>
/// A aba da Mumu (spec 031): o Streemu fala com quem usa. A Mumu, grande, ocupa a coluna esquerda do topo do primeiro cartão até perto do fim dos cartões
/// (pedido de 2026-11-27: ênfase na Mumu); à direita, o título, o histórico de recados (<see cref="MumuRecadosCartao"/>), o feedback (<see cref="MumuFeedbackCartao"/>)
/// e onde nos encontrar. Janela estreita: a Mumu sobe para cima do título. Com recado novo a Mumu está acordada numa pose de <see cref="MumuPoses"/>; sem, dormindo. Não pede nada à rede: quem dá os recados e manda o feedback é o dono.
/// Mede em dp para acompanhar o DPI e o Ctrl + / −; rola inteira se a janela for baixa.
/// </summary>
public sealed class MumuTela : UserControl
{
    public const string RecadoPadrao = "Oi! Aqui é a Mumu. Ainda não tenho recado novo, mas passei só para dizer: obrigada por usar o Streemu com a gente.";
    public const string TextoFeedback = "Qualquer opinião serve: o que gostou, o que incomodou, o que faltou. A gente lê tudo com carinho.";
    public const string TextoRedes = "Siga o Streemu no Instagram para ver as novidades e encontrar quem também usa.";
    const int ConteudoMaxDp = 620, LargoMinDp = 720, ArteMinDp = 220, ArteMaxDp = 460, ArteEstreitaDp = 200;

    readonly MumuArte _mumu = new();
    readonly MumuTexto _titulo = new(UiBrand.TituloDaTelaMumu, true);
    readonly MumuRecadosCartao _recados = new();
    readonly MumuFeedbackCartao _feedback = new(TextoFeedback);
    readonly MumuCartao _redes = new("Onde nos encontrar");
    bool _arrumando;

    public MumuTela()
    {
        Dock = DockStyle.Fill; AutoScroll = true; BackColor = Theme.Surface; ForeColor = Theme.Text;
        _redes.Texto = TextoRedes;
        _redes.TextoDoBotao = "Abrir o Instagram";
        _redes.BotaoClicado += () => AbrirLink(MumuLinks.Instagram);
        _feedback.AlturaMudou += Arrumar;
        Controls.AddRange([_mumu, _titulo, _recados, _feedback, _redes]);
        VisibleChanged += (_, _) => Arrumar();
        ClientSizeChanged += (_, _) => Arrumar();
        FontChanged += (_, _) => Arrumar();
        Theme.Changed += AplicarCores;
        AplicarCores();
    }

    public MumuArte Mumu => _mumu;
    public MumuRecadosCartao Recados => _recados;
    public MumuFeedbackCartao Feedback => _feedback;
    public MumuCartao Redes => _redes;

    /// <summary>
    /// Mostra os recados (do mais novo ao mais antigo; vazio = o <see cref="RecadoPadrao"/>). Com o primeiro ainda não visto, a Mumu acorda na <paramref name="pose"/>
    /// (de <see cref="MumuPoses"/>) e ele ganha destaque.
    /// </summary>
    public void MostrarRecados(IReadOnlyList<RecadoDaMumu> recados, bool primeiroNovo, string? pose = null)
    {
        _recados.Mostrar(recados, primeiroNovo);
        _mumu.Pose = _recados.TemNovo ? pose ?? MumuPoses.Sorteaveis[0] : null;
        Arrumar();
    }

    /// <summary>Abre só os links da lista (<see cref="MumuLinks"/>); sem navegador, avisa no log e não incomoda.</summary>
    static void AbrirLink(string url)
    {
        if (url != MumuLinks.Instagram) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Info("Aba da Mumu: não abri o link (" + ex.GetType().Name + ")"); }
    }

    void AplicarCores()
    {
        BackColor = Theme.Surface;
        Invalidate(true);
    }

    // Escondida, o layout mediria tudo errado: arruma quando aparece, como a tela Novidades.
    void Arrumar()
    {
        if (_arrumando || IsDisposed || !Visible || ClientSize.Width <= 0) return;
        _arrumando = true;
        try { Colocar(); }
        finally { _arrumando = false; }
    }

    void Colocar()
    {
        double k = UiScale.Dp(this, 10000) / 10000.0;
        int D(int dp) => (int)Math.Round(dp * k);
        // margens confortáveis: o conjunto (Mumu + título + cartões) fica no meio da área, nos dois eixos quando sobra espaço (pedido de 2026-11-27)
        int pad = Math.Max(D(48), D(UiMetrics.PagePadding) + D(8)), padY = D(32), gap = D(UiMetrics.Gap);
        var scroll = AutoScrollPosition; // os Bounds abaixo são em coordenadas de tela; a rolagem desloca tudo
        int util = Math.Max(1, ClientSize.Width - 2 * pad); // ClientSize já desconta a barra de rolagem quando ela aparece
        bool largo = util >= D(LargoMinDp);
        int arte, x, largura, y;
        if (largo)
        {
            // a coluna da Mumu leva ~40% da largura útil (era ~30%): ela é o destaque da aba
            arte = Math.Clamp((int)(util * .4), D(ArteMinDp), D(ArteMaxDp));
            int folga = gap * 3;
            largura = Math.Min(D(ConteudoMaxDp), util - arte - folga);
            int x0 = pad + Math.Max(0, (util - (arte + folga + largura)) / 2);
            x = x0 + arte + folga;
            int topo = Cartoes(x, padY, largura, scroll, out y);
            // do topo do primeiro cartão até perto do fim dos cartões; com cartões baixos, mantém um mínimo para a Mumu não encolher
            _mumu.Bounds = new Rectangle(x0, topo + scroll.Y, arte, Math.Max(y - topo - D(24), (int)(arte * 1.15)));
        }
        else
        {
            arte = D(ArteEstreitaDp);
            largura = Math.Min(D(ConteudoMaxDp), util);
            x = pad + Math.Max(0, (util - largura) / 2);
            _mumu.Bounds = new Rectangle(x + (largura - arte) / 2, padY + scroll.Y, arte, (int)(arte * 1.25));
            Cartoes(x, _mumu.Bottom - scroll.Y + gap, largura, scroll, out y);
        }
        int fim = Math.Max(y, _mumu.Bottom - scroll.Y) + padY;
        int sobra = (ClientSize.Height - fim) / 2;
        if (sobra > 0)
        {
            foreach (Control c in new Control[] { _mumu, _titulo, _recados, _feedback, _redes }) c.Top += sobra;
            fim += sobra;
        }
        AutoScrollMinSize = new Size(0, fim);
        Invalidate();
    }

    /// <summary>Título e cartões em coluna a partir de <paramref name="y"/>; devolve onde o primeiro cartão começa e, em <paramref name="fim"/>, onde o último termina.</summary>
    int Cartoes(int x, int y, int largura, Point scroll, out int fim)
    {
        int gap = UiScale.Dp(this, UiMetrics.Gap);
        y = Por(_titulo, x, y, largura, _titulo.AlturaPara(largura), scroll) + gap * 2;
        int topo = y;
        y = Por(_recados, x, y, largura, _recados.AlturaPara(largura), scroll) + gap * 3 / 2;
        y = Por(_feedback, x, y, largura, _feedback.AlturaPara(largura), scroll) + gap * 3 / 2;
        fim = Por(_redes, x, y, largura, _redes.AlturaPara(largura), scroll);
        return topo;
    }

    static int Por(Control c, int x, int y, int largura, int altura, Point scroll)
    {
        c.Bounds = new Rectangle(x, y + scroll.Y, largura, altura);
        return y + altura;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= AplicarCores;
        base.Dispose(disposing);
    }
}

/// <summary>Título ou linha de apoio da aba da Mumu, pintado direto no fundo da tela (sem a faixa de fundo do Label).</summary>
public sealed class MumuTexto : Control
{
    readonly bool _titulo;
    Font? _fonte;
    float _fonteBase;

    public MumuTexto(string texto, bool titulo)
    {
        _titulo = titulo;
        Text = texto;
        AccessibleName = texto;
        AccessibleRole = titulo ? AccessibleRole.Grouping : AccessibleRole.StaticText;
        TabStop = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    Font Fonte
    {
        get
        {
            if (_fonte != null && _fonteBase == Font.Size) return _fonte;
            _fonte?.Dispose();
            _fonteBase = Font.Size;
            float esc = Font.Size / UiTypography.Body;
            _fonte = _titulo ? UiTypography.Create(UiTypography.PageTitle * esc, FontStyle.Bold) : UiTypography.Create(UiTypography.Body * 1.05f * esc);
            return _fonte;
        }
    }

    const TextFormatFlags Quebra = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

    public int AlturaPara(int largura) => TextRenderer.MeasureText(Text, Fonte, new Size(Math.Max(1, largura), int.MaxValue), Quebra).Height;

    protected override void OnPaint(PaintEventArgs e)
    {
        using (var fundo = new SolidBrush(Parent?.BackColor ?? Theme.Surface)) e.Graphics.FillRectangle(fundo, ClientRectangle);
        TextRenderer.DrawText(e.Graphics, Text, Fonte, ClientRectangle, _titulo ? Theme.Text : Theme.TextMuted, Quebra);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _fonte?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// A Mumu da aba, pintada inteira e centrada no quadro que a tela der (a <see cref="ArtView"/> é quadrada). Dormindo (<see cref="UiBrand.SleepingMumu"/>) sem recado novo;
/// acordada numa pose de <see cref="MumuPoses"/> com recado ainda não visto. Libera o bitmap ao esconder, como a ArtView.
/// </summary>
public sealed class MumuArte : Control
{
    Bitmap? _fonte, _reduzida;
    string? _carregada, _pose;

    public MumuArte()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        AccessibleRole = AccessibleRole.Graphic;
        AtualizarNome();
        Theme.Changed += Liberar;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? Pose
    {
        get => _pose;
        set { if (_pose == value) return; _pose = value; AtualizarNome(); Liberar(); }
    }

    public bool Acordada => _pose != null;

    /// <summary>O nome da arte de agora (para o teste conferir a pose).</summary>
    public string NomeDaArte => _pose ?? UiBrand.SleepingMumu;

    void AtualizarNome() => AccessibleName = Acordada ? "Mumu acordada: tem recado novo" : "Mumu dormindo";

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var fundo = new SolidBrush(Parent?.BackColor ?? Theme.Surface)) g.FillRectangle(fundo, ClientRectangle);
        if (Width <= 0 || Height <= 0) return;
        string nome = UiBrand.ThemedName(NomeDaArte);
        try
        {
            if (_fonte == null || _carregada != nome) { Liberar(); _fonte = UiBrand.LoadArt(nome); _carregada = nome; }
        }
        catch (Exception ex) { Log.Info($"Arte {nome} não carregou: {ex.Message}"); return; }
        float esc = Math.Min((float)Width / _fonte.Width, (float)Height / _fonte.Height);
        var tam = new Size(Math.Max(1, (int)(_fonte.Width * esc)), Math.Max(1, (int)(_fonte.Height * esc)));
        if (_reduzida == null || _reduzida.Size != tam)
        {
            _reduzida?.Dispose();
            _reduzida = new Bitmap(tam.Width, tam.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using var gr = Graphics.FromImage(_reduzida);
            gr.InterpolationMode = InterpolationMode.HighQualityBicubic;
            gr.PixelOffsetMode = PixelOffsetMode.HighQuality;
            gr.CompositingQuality = CompositingQuality.HighQuality;
            gr.DrawImage(_fonte, 0, 0, tam.Width, tam.Height);
        }
        g.DrawImage(_reduzida, (Width - tam.Width) / 2, (Height - tam.Height) / 2, tam.Width, tam.Height);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) Liberar();
    }

    void Liberar()
    {
        _reduzida?.Dispose(); _reduzida = null;
        _fonte?.Dispose(); _fonte = null; _carregada = null;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Theme.Changed -= Liberar; _reduzida?.Dispose(); _fonte?.Dispose(); }
        base.Dispose(disposing);
    }
}
