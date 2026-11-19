using System.Reflection;
using Streemu;
using Streemu.Ui;

static class Program
{
    static readonly string[] Categories = ["Perfil", "Aparência", "Transmissão", "Assistir", "Notificações", "Conexão", "Sistema", "Diagnóstico"];

    [STAThread]
    static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static int Run(string[] args)
    {
        // Constrói os controles reais sem Load/Show: não abre janela, player, sala nem estado pessoal.
        Environment.SetEnvironmentVariable("STREEMU_NOSTATE", "1");
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        using var form = new MainForm(localMode: true);
        var view = Field<SettingsView>(form, "_settingsPage");
        var side = Field<SidePanel>(form, "_side");
        bool initialRail = side.Collapsed;
        form.GetType().GetMethod("ShowSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
        Check(side.Collapsed, "Configurações não recolheu a navegação principal");
        form.GetType().GetMethod("ShowView", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [false]);
        Check(side.Collapsed == initialRail, "A navegação anterior não foi restaurada");
        view.Parent!.Controls.Remove(view);
        view.Dock = DockStyle.None;
        view.CreateControl();
        var output = args.FirstOrDefault();
        if (output != null) Directory.CreateDirectory(output);
        var nick = Field<TextBox>(form, "_nick");
        int sourceWidth = Field<ComboBox>(form, "_source").Width;
        float fontSize = view.Font.Size;
        nick.Text = "Nome de teste que deve permanecer ao alternar categorias";

        foreach (var size in new[] { new Size(1302, 700), new Size(736, 568), new Size(416, 668), new Size(1302, 700) })
        {
            view.Size = size;
            foreach (var category in Categories)
            {
                view.SelectCategory(category);
                view.PerformLayout();
                Check(view.SelectedCategory == category, "Categoria selecionada incorreta");
                var picker = Field<CategoryPicker>(view, "_category");
                Check(picker.SelectedItem == category && picker.Items.SequenceEqual(Categories), "Seletor perdeu a categoria ou alguma opção");
                var body = Field<Panel>(view, "_body");
                Check(body.Controls.Cast<Control>().Count(c => c.Visible) == 1, "Mais de uma categoria visível");
                Check(!body.HorizontalScroll.Visible, $"Rolagem horizontal em {category}, {size}");
                var page = body.Controls.Cast<Control>().Single(c => c.Visible);
                Check(page.Width <= body.ClientSize.Width, $"Página excedeu a área em {category}, {size}");
                CheckBounds(page, category, size);
                if (size.Width == 736) Check(view.UsesCategorySelector, "Janela 800×600 precisa do seletor");
                if (size.Width == 1302) Check(!view.UsesCategorySelector, "Categorias devem aparecer na janela larga");
            }
            Check(nick.Text.StartsWith("Nome de teste"), "Texto do apelido foi perdido");
            Check(view.Font.Size == fontSize, "Fonte foi reduzida para caber");
            var log = Field<TextBox>(form, "_log");
            Check(!log.Visible, "Log deve começar recolhido");
            var stats = Field<Label>(form, "_teleStatus");
            Check(!stats.Visible, "Detalhes técnicos devem começar recolhidos");
            if (output != null)
            {
                // Apenas cenário visual fictício, isolado do envio real e do app publicado.
                Field<CheckBox>(form, "_teleEnviar").Checked = true;
                Field<DiagnosisSummary>(form, "_diagnosisSummary").SetStatus(DiagState.Ok, "Diagnóstico em dia", "Envio confirmado pelo servidor");
                using var image = new Bitmap(view.Width, view.Height);
                view.DrawToBitmap(image, view.ClientRectangle);
                image.Save(Path.Combine(output, $"diagnostico-{size.Width}x{size.Height}.png"));
            }
        }
        view.SelectCategory("Diagnóstico");
        Check(Field<ComboBox>(form, "_source").Width == sourceWidth, "Campo não recuperou a largura ao ampliar");
        var scrollBody = Field<Panel>(view, "_body");
        view.Size = new Size(736, 400);
        scrollBody.AutoScrollPosition = new Point(0, 120);
        int previousScroll = -scrollBody.AutoScrollPosition.Y;
        view.SelectCategory("Perfil");
        view.SelectCategory("Diagnóstico");
        Check(previousScroll > 0 && -scrollBody.AutoScrollPosition.Y == previousScroll, "Posição de rolagem da categoria foi perdida");
        Check(Field<Panel>(view, "_header").Top == 0 && Field<CategoryPicker>(view, "_category").Visible, "Navegação rolou com o conteúdo");
        foreach (string section in new[] { "Detalhes técnicos", "Log ao vivo" })
        {
            var toggle = Descendants(view).OfType<Button>().Single(b => b.AccessibleName == section);
            toggle.PerformClick();
            Check(section == "Log ao vivo" ? Field<TextBox>(form, "_log").Visible : Field<Label>(form, "_teleStatus").Visible, section + " não abriu");
            CheckBounds(scrollBody.Controls.Cast<Control>().Single(c => c.Visible), "Diagnóstico expandido", view.Size);
            toggle.PerformClick();
        }
        var categoryPicker = Field<CategoryPicker>(view, "_category");
        categoryPicker.SelectedIndex = 0;
        Check(view.SelectedCategory == "Perfil", "Escolha no seletor não trocou o conteúdo");
        var style = Field<Segmented>(form, "_badgeStyle");
        style.FitWidth(140);
        for (int i = 0; i < 3; i++)
        {
            style.GetType().GetMethod("OnMouseClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(style,
                [new MouseEventArgs(MouseButtons.Left, 1, style.Width / 2, i * style.Height / 3 + style.Height / 6, 0)]);
            Check(style.Selected == i, "Opção empilhada do selo não respondeu ao clique");
        }
        Console.WriteLine($"OK: controles reais das 8 categorias, 3 larguras, retorno à janela larga, texto preservado, sem rolagem horizontal e detalhes recolhíveis. DPI atual: {view.DeviceDpi}. Sem abrir janela/player.");
        view.Dispose();
        return 0;
    }

    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
    static void Check(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
    static void CheckBounds(Control root, string category, Size size)
    {
        foreach (Control child in root.Controls)
        {
            if (!child.Visible) continue;
            if (child is Button) Check(child.Height >= TextRenderer.MeasureText(child.Text, child.Font).Height, $"Botão cortado em {category}: {child.Text}");
            Check(child.Top >= 0 && child.Bottom <= root.ClientSize.Height + 1, $"{category}, {size}: altura de {child.GetType().Name} '{child.Text}' cortada ({child.Bounds}, altura disponível {root.ClientSize.Height})");
            Check(child.Left >= 0 && child.Right <= root.ClientSize.Width + 1, $"{category}, {size}: {child.GetType().Name} '{child.Text}' fora da coluna ({child.Bounds}, largura {root.ClientSize.Width})");
            CheckBounds(child, category, size);
        }
    }
}
