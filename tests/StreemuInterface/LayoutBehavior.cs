using System.Reflection;
using System.Runtime.InteropServices;
using Streemu.Ui;
using Streemu.Util;

static class LayoutBehavior
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr w, IntPtr l);

    public static void Run()
    {
        foreach (int dpi in new[] { 96, 120, 144, 192, 288 })
            foreach (var monitor in new[] { new Size(1920, 1080), new Size(2560, 1440), new Size(3840, 2160) })
                Check(UiScale.Factor(dpi, monitor) == (dpi == 120 && monitor.Width == 2560 ? 1.12 : 1), "Escala vazou para outro monitor/DPI");

        using var form = new Form { WindowState = FormWindowState.Minimized, ShowInTaskbar = false, Size = new Size(1100, 900) };
        using var view = new GoLiveView(null, null, 1080, 60, 1, false, []);
        view.SetInline(true);
        form.Controls.Add(view);
        Exception? failure = null;
        form.Shown += (_, _) => form.BeginInvoke(() =>
        {
            try
            {
                view.Dock = DockStyle.None;
                view.Size = new Size(1100, 900);
                var sources = Enumerable.Range(0, 12).Select(i => new Streemu.Media.CaptureSource { Handle = (IntPtr)(1000 + i), Label = "Tela de teste", IsWindow = false }).ToList();
                typeof(GoLiveView).GetField("_sources", Hidden)!.SetValue(view, sources);
                typeof(GoLiveView).GetMethod("ChooseInitialSource", Hidden)!.Invoke(view, null);
                view.PerformLayout();
                var body = Field<Panel>(view, "_body");
                var grid = Field<Panel>(view, "_grid");
                _ = body.Handle; _ = grid.Handle;
                view.PerformLayout();
                var title = Field<Label>(view, "_title");
                var options = Field<TableLayoutPanel>(view, "_options");
                Check(Math.Abs(title.Left - grid.Left) <= 2, "Título fora do alinhamento à esquerda da lista (desenho do Transmitir)");
                foreach (var caption in options.Controls.OfType<Label>().Where(c => c.Visible))
                    Check(caption.Left <= 3, "Opção fora do alinhamento à esquerda: " + caption.Text);
                var table = typeof(ScrollSkin).GetField("Forms", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                object?[] lookup = [form, null];
                Check((bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, lookup)!, "Barras não observaram a janela principal");
                var activation = lookup[1]!;
                var pending = activation.GetType().GetField("_pending", Hidden)!;
                Application.DoEvents();
                for (int cycle = 0; cycle < 3; cycle++)
                {
                    // Mensagens nativas de saída/entrada, sem trazer a janela para a frente de quem está jogando.
                    SendMessage(form.Handle, 0x86, IntPtr.Zero, (IntPtr)(-1));
                    SendMessage(form.Handle, 0x86, (IntPtr)1, (IntPtr)(-1));
                    Check((bool)pending.GetValue(activation)!, "Ativação não agendou pintura das barras filhas");
                    Application.DoEvents();
                    Check(!(bool)pending.GetValue(activation)!, "Pintura ficou pendente após ativação");
                }
                Check(!body.HorizontalScroll.Visible && !grid.HorizontalScroll.Visible, $"Rolagem horizontal apareceu ao reativar: corpo {body.Size}, conteúdo {body.DisplayRectangle}; lista {grid.Size}, conteúdo {grid.DisplayRectangle}; peças " + string.Join("; ", grid.Controls.Cast<Control>().Select(c => c.Bounds)));
                Check(form.WindowState == FormWindowState.Minimized, "Teste roubou a tela");
            }
            catch (Exception e) { failure = e; }
            finally { form.Close(); }
        });
        Application.Run(form);
        if (failure != null) throw failure;
        Console.WriteLine("OK: escala exclusiva QHD/125% (15 combinações), títulos/opções alinhados à esquerda (desenho do Transmitir) e três ciclos nativos de ativação com repintura adiada das barras aninhadas. Janela minimizada, sem player ou captura.");
    }
    static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Hidden)!.GetValue(owner)!;
    static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
}
