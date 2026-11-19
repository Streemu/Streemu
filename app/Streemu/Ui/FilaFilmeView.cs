using System.Drawing.Drawing2D;
using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// O painel da fila de reprodução do Cinema (pedido de 2026-11-18): título com "3 de 12", uma linha por episódio e, no rodapé, Anterior, Próximo e Adicionar.
/// Só tela: não sabe de mpv nem de ligação, recebe os nomes e devolve o que a pessoa pediu pelos eventos. O anfitrião toca um item (duplo clique ou o ▶ da linha), tira (×)
/// e arrasta para reordenar; quem assiste vê a mesma lista só para leitura. O arrasto é feito na mão (mouse apertado, linha de destino em destaque) para a lista não
/// depender de OLE, que no WinForms embutido num painel do mpv já deu problema com o foco.
/// Uso: <c>view.Mostrar(nomes, atual, podeMexer: true, "Ana"); view.Tocar += i => ...; view.Mover += (de, para) => ...;</c>
/// </summary>
public sealed class FilaFilmeView : UserControl
{
    string[] _nomes = [];
    int _atual = -1, _hover = -1, _parteHover, _baixo = -1, _arrasto = -1, _destino = -1, _rolagem;
    bool _pode;
    string _dono = "o anfitrião";
    Point _baixoEm;
    readonly Button _anterior = new() { Text = "Anterior", AccessibleName = "Episódio anterior" };
    readonly Button _proximo = new() { Text = "Próximo", AccessibleName = "Próximo episódio" };
    readonly Button _adicionar = new() { Text = "Adicionar", AccessibleName = "Adicionar arquivos à fila" };
    readonly Font _fonte = UiTypography.Create(UiTypography.Body);
    readonly Font _negrito = UiTypography.Create(UiTypography.Body, FontStyle.Bold);
    readonly Font _titulo = UiTypography.Create(UiTypography.Heading, FontStyle.Bold);
    readonly Font _legenda = UiTypography.Create(UiTypography.Caption);
    readonly Font _icone = UiTypography.CreateSymbols(11);

    public event Action<int>? Tocar;
    public event Action<int>? Remover;
    /// <summary>(de, para): o item <c>de</c> vai para o índice <c>para</c> (já descontado o próprio item).</summary>
    public event Action<int, int>? Mover;
    public event Action? Anterior, Proximo, Adicionar, Fechar;

    public FilaFilmeView()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.SurfaceRaised; ForeColor = Theme.Text; TabStop = false;
        foreach (var b in new[] { _anterior, _proximo, _adicionar }) { b.TabStop = false; Theme.StyleButton(b, false); Controls.Add(b); }
        _anterior.Click += (_, _) => Anterior?.Invoke();
        _proximo.Click += (_, _) => Proximo?.Invoke();
        _adicionar.Click += (_, _) => Adicionar?.Invoke();
        Theme.Changed += AoMudarTema;
    }

    void AoMudarTema()
    {
        BackColor = Theme.SurfaceRaised;
        foreach (var b in new[] { _anterior, _proximo, _adicionar }) Theme.StyleButton(b, false);
        Invalidate();
    }

    /// <param name="atual">O índice do que está tocando (-1 nenhum).</param>
    /// <param name="podeMexer">Só o anfitrião toca, tira, arrasta e adiciona; os outros só leem.</param>
    public void Mostrar(IReadOnlyList<string> nomes, int atual, bool podeMexer, string dono)
    {
        bool mudou = !_nomes.SequenceEqual(nomes) || atual != _atual || podeMexer != _pode || dono != _dono;
        if (!mudou) return;
        bool trocouAtual = atual != _atual;
        _nomes = nomes.ToArray(); _atual = atual; _pode = podeMexer; _dono = dono;
        _anterior.Enabled = podeMexer && atual > 0;
        _proximo.Enabled = podeMexer && atual >= 0 && atual + 1 < _nomes.Length;
        _adicionar.Enabled = podeMexer;
        foreach (var b in new[] { _anterior, _proximo, _adicionar }) b.Visible = podeMexer;
        if (_arrasto >= 0 && _arrasto >= _nomes.Length) { _arrasto = _destino = -1; Capture = false; }
        PerformLayout();
        if (trocouAtual) RolarParaAtual();
        LimitarRolagem();
        Invalidate();
    }

    // ------------------------------------------------------------------ medidas

    int Dp(int v) => UiScale.Dp(this, v);
    int AlturaCabecalho => Dp(54);
    int AlturaLinha => Dp(44);
    int AlturaRodape => _pode ? Dp(58) : Dp(52);
    Rectangle AreaLista => new(0, AlturaCabecalho, Width, Math.Max(0, Height - AlturaCabecalho - AlturaRodape));
    Rectangle Linha(int i) => new(Dp(8), AreaLista.Y + i * AlturaLinha - _rolagem, Math.Max(0, Width - Dp(16)), AlturaLinha - Dp(2));
    Rectangle NumeroDe(Rectangle r) => new(r.X, r.Y, Dp(40), r.Height);
    Rectangle XDe(Rectangle r) => new(r.Right - Dp(38), r.Y, Dp(38), r.Height);
    Rectangle BotaoFechar => new(Width - Dp(44), Dp(8), Dp(36), Dp(36));

    void LimitarRolagem()
    {
        int max = Math.Max(0, _nomes.Length * AlturaLinha - AreaLista.Height);
        _rolagem = Math.Clamp(_rolagem, 0, max);
    }

    void RolarParaAtual()
    {
        if (_atual < 0) return;
        int topo = _atual * AlturaLinha, area = AreaLista.Height;
        if (topo < _rolagem) _rolagem = topo;
        else if (topo + AlturaLinha > _rolagem + area) _rolagem = topo + AlturaLinha - area;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (!_pode) return;
        int pad = Dp(12), gap = Dp(8), h = Dp(36);
        int w = (Width - pad * 2 - gap * 2) / 3, y = Height - AlturaRodape + (AlturaRodape - h) / 2;
        _anterior.SetBounds(pad, y, w, h);
        _proximo.SetBounds(pad + w + gap, y, w, h);
        _adicionar.SetBounds(pad + (w + gap) * 2, y, w, h);
        LimitarRolagem();
    }

    // ------------------------------------------------------------------ desenho

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.SurfaceRaised);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        TextRenderer.DrawText(g, "Fila", _titulo, new Rectangle(Dp(16), 0, Dp(60), AlturaCabecalho), Theme.Text, flags);
        if (_nomes.Length > 0 && _atual >= 0)
            TextRenderer.DrawText(g, $"{_atual + 1} de {_nomes.Length}", _legenda, new Rectangle(Dp(16) + Dp(54), 0, Math.Max(0, Width - Dp(130)), AlturaCabecalho), Theme.TextMuted, flags);
        var fechar = BotaoFechar;
        TextRenderer.DrawText(g, UiGlyphs.ChromeClose, _icone, fechar, _hoverFechar ? Theme.Text : Theme.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        var lista = AreaLista;
        if (_nomes.Length == 0)
        {
            TextRenderer.DrawText(g, "A fila está vazia.", _fonte, lista, Theme.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        g.SetClip(lista);
        for (int i = 0; i < _nomes.Length; i++)
        {
            var r = Linha(i);
            if (r.Bottom < lista.Top || r.Top > lista.Bottom) continue;
            bool atual = i == _atual, passou = i == _hover && _arrasto < 0;
            if (atual)
            {
                using var p = Theme.RoundedPath(r, Dp(8));
                using var fundo = new SolidBrush(Theme.CardSelected);
                using var borda = new Pen(Theme.CardSelectedBorder);
                g.FillPath(fundo, p); g.DrawPath(borda, p);
            }
            else if (passou)
            {
                using var p = Theme.RoundedPath(r, Dp(8));
                using var fundo = new SolidBrush(Theme.NeutralHover);
                g.FillPath(fundo, p);
            }
            var cor = atual ? Theme.Accent : Theme.Text;
            if (i == _arrasto) cor = Theme.Dragged(cor);
            var num = NumeroDe(r);
            if (atual) TextRenderer.DrawText(g, UiGlyphs.Play, _icone, num, Theme.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            else if (passou && _pode && _parteHover == 1) TextRenderer.DrawText(g, UiGlyphs.Play, _icone, num, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            else TextRenderer.DrawText(g, (i + 1).ToString(), _fonte, num, Theme.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            int direita = _pode && !atual ? XDe(r).Width : Dp(8);
            TextRenderer.DrawText(g, _nomes[i], atual ? _negrito : _fonte, new Rectangle(num.Right, r.Y, Math.Max(0, r.Width - num.Width - direita), r.Height), cor, flags);
            if (_pode && !atual && (passou || i == _arrasto))
                TextRenderer.DrawText(g, UiGlyphs.Close, _icone, XDe(r), passou && _parteHover == 2 ? Theme.Error : Theme.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        // onde o item arrastado vai cair
        if (_arrasto >= 0 && _destino >= 0)
        {
            int y = lista.Y + _destino * AlturaLinha - _rolagem - Dp(1);
            using var p = new Pen(Theme.Accent, Math.Max(2, Dp(2)));
            g.DrawLine(p, Dp(14), y, Width - Dp(14), y);
        }
        g.ResetClip();
        if (!_pode)
        {
            var nota = new Rectangle(Dp(16), Height - AlturaRodape, Math.Max(0, Width - Dp(32)), AlturaRodape);
            TextRenderer.DrawText(g, $"Só {_dono} mexe na fila.", _legenda, nota, Theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }
        using var linha = new Pen(Theme.Border);
        g.DrawLine(linha, 0, AlturaCabecalho - 1, Width, AlturaCabecalho - 1);
        if (_pode) g.DrawLine(linha, 0, Height - AlturaRodape, Width, Height - AlturaRodape);
    }

    // ------------------------------------------------------------------ mouse

    bool _hoverFechar;

    (int Indice, int Parte) Acertar(Point p)
    {
        if (!AreaLista.Contains(p)) return (-1, 0);
        int i = (p.Y - AreaLista.Y + _rolagem) / AlturaLinha;
        if (i < 0 || i >= _nomes.Length) return (-1, 0);
        var r = Linha(i);
        if (!r.Contains(p)) return (-1, 0);
        if (_pode && NumeroDe(r).Contains(p)) return (i, 1);
        if (_pode && i != _atual && XDe(r).Contains(p)) return (i, 2);
        return (i, 0);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool fechar = BotaoFechar.Contains(e.Location);
        if (fechar != _hoverFechar) { _hoverFechar = fechar; Invalidate(); }
        if (_baixo >= 0 && _pode && _arrasto < 0 && (Math.Abs(e.Y - _baixoEm.Y) > Dp(5) || Math.Abs(e.X - _baixoEm.X) > Dp(5))) { _arrasto = _baixo; Capture = true; }
        if (_arrasto >= 0)
        {
            int gap = (int)Math.Round((e.Y - AreaLista.Y + _rolagem) / (double)AlturaLinha);
            int novo = Math.Clamp(gap, 0, _nomes.Length);
            if (novo != _destino) { _destino = novo; Invalidate(); }
            Cursor = Cursors.SizeNS;
            return;
        }
        var (i, parte) = Acertar(e.Location);
        if (i != _hover || parte != _parteHover) { _hover = i; _parteHover = parte; Invalidate(); }
        Cursor = fechar || (_pode && i >= 0 && parte != 0) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_arrasto >= 0) return;
        _hover = -1; _parteHover = 0; _hoverFechar = false; Cursor = Cursors.Default; Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (BotaoFechar.Contains(e.Location)) { Fechar?.Invoke(); return; }
        var (i, parte) = Acertar(e.Location);
        // arrastar começa em qualquer parte da linha menos nos botões dela (▶ e ×)
        _baixo = i >= 0 && parte == 0 ? i : -1;
        _baixoEm = e.Location;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        int arrasto = _arrasto, destino = _destino;
        _baixo = -1; _arrasto = -1; _destino = -1; Capture = false;
        if (arrasto >= 0)
        {
            Invalidate();
            if (destino >= 0)
            {
                int para = destino > arrasto ? destino - 1 : destino;
                if (para != arrasto) Mover?.Invoke(arrasto, para);
            }
            return;
        }
        var (i, parte) = Acertar(e.Location);
        if (i < 0 || !_pode) return;
        if (parte == 1) Tocar?.Invoke(i);
        else if (parte == 2) Remover?.Invoke(i);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        var (i, parte) = Acertar(e.Location);
        if (_pode && i >= 0 && parte == 0 && i != _atual) Tocar?.Invoke(i);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _rolagem -= Math.Sign(e.Delta) * AlturaLinha;
        LimitarRolagem();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Theme.Changed -= AoMudarTema; _fonte.Dispose(); _negrito.Dispose(); _titulo.Dispose(); _legenda.Dispose(); _icone.Dispose(); }
        base.Dispose(disposing);
    }
}
