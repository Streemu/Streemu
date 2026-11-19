namespace Streemu.Util;

/// <summary>
/// Escala da interface: a compensação de leitura pedida para QHD/125% (não altera o DPI do Windows nem o vídeo) e a escala do usuário (Ctrl + / Ctrl -).
/// Os tamanhos desenhados à mão devem sempre passar por <see cref="Dp"/>; os componentes com fonte própria se registram em <see cref="WatchFont"/>.
/// </summary>
public static class UiScale
{
    /// <summary>Escala pedida pelo usuário: 1 = normal. Mudar por <c>MainForm.ApplyUserScale</c>, que também refaz fontes e layout.</summary>
    public static double User { get; set; } = 1;
    public static double Factor(int dpi, Size monitor) => dpi == 120 && monitor == new Size(2560, 1440) ? 1.12 : 1;
    public static double Factor(Control owner) => Factor(owner.DeviceDpi, Screen.FromControl(owner.FindForm() ?? owner).Bounds.Size) * User;
    public static int Dp(Control owner, int value) => (int)Math.Round(value * owner.DeviceDpi / 96.0 * Factor(owner));

    static readonly HashSet<Control> Watched = new();
    static event Action? Changed;
    /// <summary>Avisa os componentes registrados de que a escala do usuário mudou.</summary>
    public static void RaiseChanged() => Changed?.Invoke();

    /// <summary>
    /// Multiplica a fonte de todo controle que tem fonte PRÓPRIA (a que não é a mesma do pai: o resto herda e acompanha). Os registrados em <see cref="WatchFont"/>
    /// ficam de fora: eles refazem a própria fonte a partir do evento.
    /// </summary>
    public static void ScaleFonts(Control root, double ratio)
    {
        foreach (Control child in root.Controls)
        {
            if (!Watched.Contains(child) && child.Parent != null && !ReferenceEquals(child.Font, child.Parent.Font))
            {
                var old = child.Font;
                child.Font = new Font(old.FontFamily, (float)(old.Size * ratio), old.Style, old.Unit);
            }
            ScaleFonts(child, ratio);
        }
    }

    // Só fontes explicitamente registradas: as demais herdam a fonte do componente, sem multiplicar a escala de novo.
    public static void WatchFont(Control owner)
    {
        double applied = 1;
        Font? owned = null;
        Form? form = null;
        var parents = new List<Control>();
        Watched.Add(owner);
        void Refresh(object? sender, EventArgs e)
        {
            if (!owner.IsHandleCreated || owner.IsDisposed) return;
            double next = Factor(owner);
            if (Math.Abs(next - applied) < .001) return;
            var font = new Font(owner.Font.FontFamily, (float)(owner.Font.Size * next / applied), owner.Font.Style, owner.Font.Unit);
            applied = next;
            var previous = owned;
            owner.Font = owned = font;
            previous?.Dispose();
            owner.PerformLayout();
            owner.Invalidate(true);
        }
        void OnChanged() => Refresh(owner, EventArgs.Empty);
        void Bind(object? sender, EventArgs e)
        {
            foreach (var parent in parents) parent.ParentChanged -= Bind;
            parents.Clear();
            for (Control? parent = owner.Parent; parent != null; parent = parent.Parent)
            {
                parents.Add(parent);
                parent.ParentChanged += Bind;
            }
            if (form != null) form.LocationChanged -= Refresh;
            form = owner.FindForm();
            if (form != null) form.LocationChanged += Refresh;
            Refresh(sender, e);
        }
        owner.HandleCreated += Bind;
        owner.ParentChanged += Bind;
        owner.DpiChangedAfterParent += Refresh;
        Changed += OnChanged;
        owner.Disposed += (_, _) => { Changed -= OnChanged; Watched.Remove(owner); if (form != null) form.LocationChanged -= Refresh; foreach (var parent in parents) parent.ParentChanged -= Bind; parents.Clear(); owned?.Dispose(); };
        Bind(owner, EventArgs.Empty);
    }
}
