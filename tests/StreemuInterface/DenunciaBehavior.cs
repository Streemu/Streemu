using System.Reflection;
using Streemu;
using Streemu.Media;
using Streemu.Net;
using Streemu.Ui;
using Streemu.Ui.Multistream;
using Streemu.Util;

/// <summary>
/// A denúncia da live (spec 029, E3), sem player, sem rede e com as janelas fora da tela (nada aparece para quem usa o PC): a janela da denúncia (categoria obrigatória,
/// caixa da imagem que some em abuso infantil, contador, 500 caracteres), a bandeira na barra do player (inclusive na barra estreita, onde os outros botões saem
/// antes dela) e o que acontece ao clicar sem alvo. Prova que NADA é capturado ao abrir a janela, ao escolher categoria ou em abuso infantil: a captura (um contador injetado)
/// só roda no <c>CapturarAoEnviar</c>, com a janela escondida e a caixa marcada. Confere a checagem de "outra janela na frente" do plano B da captura com funções falsas e que
/// nada disso escreve no log. Não prova a captura de um ffplay de verdade nem o envio ao servidor.
/// </summary>
static class DenunciaBehavior
{
    static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Hidden)!.GetValue(o)!;
    static object? Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, Hidden)!.Invoke(o, args);
    static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 1, 2, 3];

    static readonly List<string> Logged = new();
    static void Listen(string line) { lock (Logged) Logged.Add(line); }

    public static void Run()
    {
        Dialog();
        Cover();
        Bar();
        Check(Logged.Count == 0, "a denúncia escreveu no log: " + string.Join(" | ", Logged));
        Console.WriteLine("OK: janela da denúncia (captura só no Enviar, nunca em abuso infantil), conferência da janela na frente, bandeira na barra do player (larga e estreita) e nada no log.");
    }

    static DenunciaForm Open(string nick, Func<byte[]?>? capture)
    {
        var form = new DenunciaForm(nick, capture) { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) };
        form.Show();
        Application.DoEvents();
        return form;
    }

    static void Dialog()
    {
        Log.Line += Listen;
        try { DialogSteps(); } finally { Log.Line -= Listen; }
    }

    static void DialogSteps()
    {
        // a captura é um contador injetado: tem que continuar em zero até o Enviar
        int captures = 0; bool hiddenWhenCaptured = false; DenunciaForm? current = null;
        Func<byte[]?> capture = () => { captures++; hiddenWhenCaptured = current is { Visible: false }; return Jpeg; };
        using var form = Open("Bruno", capture);
        current = form;
        var options = Field<List<(RadioButton Button, CategoriaDenuncia Category)>>(form, "_opcoes");
        var attach = Field<CheckBox>(form, "_anexar");
        var send = Field<Button>(form, "_enviar");
        var text = Field<TextBox>(form, "_texto");
        var counter = Field<Label>(form, "_contador");
        Check(form.Text == "Denunciar a live de Bruno", "título da janela: " + form.Text);
        Check(options.Select(o => o.Button.Text).SequenceEqual(Denuncia.Categorias.Select(c => c.Rotulo)), "as cinco categorias, na ordem");
        Check(!send.Enabled && form.Categoria == null, "Enviar habilitado sem categoria");
        Check(attach.Visible && attach.Checked && attach.Text == "Anexar imagem da tela", "a caixa da imagem deveria vir marcada e à vista");
        Check(captures == 0 && form.CapturarAoEnviar() == null && captures == 0, "abrir a janela (sem categoria) já capturou");
        Check(text.MaxLength == 500 && counter.Text == "0/500", "limite ou contador do texto: " + counter.Text);
        text.Text = "  golpe do Pix  ";
        Check(counter.Text == "16/500" && form.Texto == "golpe do Pix", "contador ou texto limpo: " + counter.Text);
        options.First(o => o.Category == CategoriaDenuncia.Odio).Button.Checked = true;
        Check(send.Enabled && form.Categoria == CategoriaDenuncia.Odio && attach.Visible && form.DeveAnexarImagem, "ódio: Enviar habilita e a imagem continua pedida");
        Check(captures == 0, "escolher a categoria já capturou");
        attach.Checked = false;
        Check(!form.DeveAnexarImagem && form.CapturarAoEnviar() == null && captures == 0, "caixa desmarcada: nada pode ser capturado");
        attach.Checked = true;
        // abuso infantil: a caixa some e nada é capturado, nem no envio
        options.First(o => o.Category == CategoriaDenuncia.AbusoInfantil).Button.Checked = true;
        Check(!attach.Visible && !form.DeveAnexarImagem, "abuso infantil: a caixa tinha que sumir");
        Check(form.CapturarAoEnviar() == null && captures == 0, "abuso infantil: capturou uma imagem");
        var childBody = Denuncia.MontarCorpo(new DenunciaAlvo("abcd2345", "sala", "Bruno"), CategoriaDenuncia.AbusoInfantil, form.Texto, form.CapturarAoEnviar());
        Check(!childBody.ContainsKey("captura") && captures == 0, "abuso infantil: o corpo levou imagem");
        // outra categoria: a caixa volta (nada foi guardado) e a captura só acontece no CapturarAoEnviar, com a janela já escondida
        options.First(o => o.Category == CategoriaDenuncia.Golpe).Button.Checked = true;
        Check(attach.Visible && form.DeveAnexarImagem && captures == 0, "golpe: a caixa devia voltar sem ter capturado nada");
        send.PerformClick();
        Check(form.DialogResult == DialogResult.OK && captures == 0, "Enviar não fechou com OK ou já capturou");
        var image = form.CapturarAoEnviar();
        Check(captures == 1 && image == Jpeg && hiddenWhenCaptured && !form.Visible, $"captura no envio: {captures} chamada(s), janela escondida: {hiddenWhenCaptured}");
        var body = Denuncia.MontarCorpo(new DenunciaAlvo("abcd2345", "sala", "Bruno"), form.Categoria!.Value, form.Texto, image);
        Check(body["categoria"]!.GetValue<string>() == "golpe" && body.ContainsKey("captura"), "corpo do envio");

        // sem imagem capturada (janela preta ou minimizada): nada de caixa
        using var semImagem = Open("Ana", null);
        Check(!Field<CheckBox>(semImagem, "_anexar").Visible && !semImagem.DeveAnexarImagem && semImagem.CapturarAoEnviar() == null, "sem player a caixa não faz sentido");
        // cancelar não devolve nada
        Field<Button>(semImagem, "_cancelar").PerformClick();
        Check(semImagem.DialogResult == DialogResult.Cancel && semImagem.Categoria == null, "Cancelar");
        // 600 caracteres: o MaxLength só barra digitação e colagem (um Text posto por código passa), então o texto que sai também é cortado em 500
        using var longo = Open("Caio", () => Jpeg);
        Field<TextBox>(longo, "_texto").Text = new string('x', 600);
        Check(longo.Texto!.Length == 500, "texto de 600 caracteres não foi cortado em 500");
    }

    /// <summary>A conferência de "outra janela na frente" do plano B (CopyFromScreen), com WindowFromPoint e GetAncestor falsos.</summary>
    static void Cover()
    {
        var video = new Rectangle(100, 50, 800, 600);
        IntPtr player = 100, child = 101, host = 50, other = 200;
        // o player é janela de cima; 101 é filha dele; 50 hospeda o player (a janela de streams); 200 é de outro programa
        IntPtr Root(IntPtr h) => h == child ? player : h;
        bool Free(Func<Point, IntPtr> at, IntPtr window, Func<IntPtr, IntPtr>? root = null) => DenunciaCaptura.VideoLivre(window, video, at, root ?? Root);
        Check(Free(_ => player, player), "o player em todos os pontos tinha que valer");
        Check(Free(_ => child, player), "uma janela filha do player tinha que valer");
        Check(!Free(_ => other, player), "outra janela em todos os pontos não pode valer");
        Check(!Free(p => p == new Point(video.X + video.Width / 2, video.Y + video.Height / 2) ? other : player, player), "outra janela no centro não pode valer");
        Check(!Free(p => p.X == video.X + video.Width / 4 && p.Y == video.Y + video.Height * 3 / 4 ? other : player, player), "outra janela num canto interno não pode valer");
        Check(!Free(p => p.X == video.X + video.Width * 3 / 4 && p.Y == video.Y + video.Height / 4 ? IntPtr.Zero : player, player), "ponto sem janela não pode valer");
        // player dentro da janela de streams: o que está na frente é o hospedeiro (mesma raiz)
        IntPtr RootHosted(IntPtr h) => h == player || h == host ? host : h;
        Check(Free(_ => host, player, RootHosted), "o hospedeiro do player (mesma raiz) tinha que valer");
        Check(!Free(_ => other, player, RootHosted), "outra janela na frente do hospedeiro não pode valer");
        Check(!DenunciaCaptura.VideoLivre(IntPtr.Zero, video, _ => player, Root) && !DenunciaCaptura.VideoLivre(player, new Rectangle(0, 0, 8, 8), _ => player, Root), "sem janela ou com vídeo minúsculo não vale");
        // os cinco pontos pedidos: o centro e quatro internos, todos dentro do vídeo
        var seen = new List<Point>();
        DenunciaCaptura.VideoLivre(player, video, p => { seen.Add(p); return player; }, Root);
        Check(seen.Count == 5 && seen.All(video.Contains) && seen.Distinct().Count() == 5, "a conferência devia olhar 5 pontos distintos dentro do vídeo");
    }

    static void Bar()
    {
        using var main = new MainForm(true);
        using var stage = new PlayerStage(main);
        var viewer = new Viewer();
        stage.Add(viewer);

        // sem gancho (como a prévia da própria live) não há bandeira
        using (var semGancho = PlayerOverlay.ShowFor(viewer, () => "720p", () => [], stage: stage))
        {
            semGancho.Bounds = new Rectangle(-8000, 0, 1000, semGancho.LogicalToDeviceUnits(48));
            semGancho.Show();
            Application.DoEvents();
            Check(!Field<Button>(semGancho, "_report").Visible, "a bandeira apareceu sem gancho de denúncia");
            semGancho.Close();
        }

        DenunciaAlvo? alvo = new("abcd2345", "sala", "Bruno");
        int opened = 0; string? notice = null; Func<byte[]?>? lastCapture = () => Jpeg;
        var hook = new PlayerOverlay.ReportHook(() => alvo, (_, capture) => { opened++; lastCapture = capture; }, text => notice = text);
        using var overlay = PlayerOverlay.ShowFor(viewer, () => "1392p 60 FPS", () => [], stage: stage, report: hook);
        var report = Field<Button>(overlay, "_report");
        Check(report.AccessibleName == "Denunciar esta live" && report.Width > 0, "bandeira sem nome acessível ou sem tamanho");
        foreach (int width in new[] { 1600, 1000, 560, 330, 240 })
        {
            overlay.Bounds = new Rectangle(-8000, 0, width, overlay.LogicalToDeviceUnits(48));
            if (!overlay.Visible) overlay.Show();
            Application.DoEvents();
            overlay.Bounds = new Rectangle(-8000, 0, width, overlay.LogicalToDeviceUnits(48));
            Call(overlay, "LayoutZones");
            Check(report.Visible && Field<Button>(overlay, "_close").Visible, $"largura {width}: bandeira ou sair sumiram");
            var right = Field<FlowLayoutPanel>(overlay, "_right");
            Check(right.Controls.IndexOf(report) == right.Controls.IndexOf(Field<Button>(overlay, "_close")) - 1, "a bandeira tem que ficar logo antes do sair");
            // na barra estreita saem os outros opcionais, nunca a bandeira
            if (!Field<Button>(overlay, "_fullscreen").Visible) Check(report.Visible, $"largura {width}: a bandeira saiu junto com a tela cheia");
        }
        overlay.Bounds = new Rectangle(-8000, 0, 330, overlay.LogicalToDeviceUnits(48));
        Call(overlay, "LayoutZones");
        Check(!Field<Button>(overlay, "_fullscreen").Visible && !Field<Button>(overlay, "_mini").Visible && report.Visible, "barra de 330 px: tela cheia e janela pequena saem, a bandeira fica");

        // sem alvo: aviso, e a janela não abre (a criação da janela principal e da barra loga o que é dela; só o caminho da denúncia é vigiado)
        Log.Line += Listen;
        alvo = null;
        Call(overlay, "RefreshReport");
        Check(report.AccessibleName == Denuncia.SemAlvo, "dica sem alvo: " + report.AccessibleName);
        Call(overlay, "OnReport");
        Check(notice == Denuncia.SemAlvo && opened == 0, "sem alvo: o aviso não saiu ou a janela abriu");
        // com alvo e sem janela de player (nem ffplay): abre sem função de captura (a caixa da imagem nem aparece)
        alvo = new("abcd2345", "sala", "Bruno");
        Call(overlay, "RefreshReport");
        Check(report.AccessibleName == "Denunciar esta live", "a dica não voltou");
        Call(overlay, "OnReport");
        Check(opened == 1 && lastCapture == null, "com alvo e sem player a janela deveria abrir sem captura");
        Log.Line -= Listen;
        overlay.Close();
    }
}
