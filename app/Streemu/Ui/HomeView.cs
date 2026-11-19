using Streemu.Util;
using Streemu.Ui.Multistream;

namespace Streemu.Ui;

/// <summary>Descoberta e preparação compartilham o espaço; os comandos continuam no MainForm.</summary>
public sealed class HomeView : UserControl
{
    readonly Panel _scroll = new() { AutoScroll = true };
    readonly Label _heading, _empty, _status;
    readonly Button _all;
    readonly ConviteCards _invites;
    readonly LiveCardsView _cards;
    readonly Panel _preparation = new();
    // A Mumu dormindo só no Início vazio (mockup 01): some com qualquer card e quando a janela não tem altura para ela.
    readonly ArtView _mumu = new(UiBrand.SleepingMumu) { Visible = false };
    const int MumuMaxAt96 = 300, MumuMinAt96 = 120;
    bool _arranging;
    // Quando o bloco de convites/pedidos do topo mudou de altura ou de visibilidade pela última vez: o que está abaixo dele subiu ou desceu
    // nesse instante, e um clique que já vinha a caminho cairia em outro lugar (relato de 2026-11-15).
    bool _invitesWasVisible; int _invitesWasHeight; DateTime _invitesChangedAt = DateTime.MinValue;
    static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(1500);
    /// <summary>Verdadeiro se o topo do Início mudou de lugar há menos de ~1,5 s: cliques em cartões de live devem ser ignorados.</summary>
    public bool TopChangedRecently => DateTime.Now - _invitesChangedAt < SettleTime;
    public event Action? PreparationSpaceChanged;
    public bool HasPreparationSpace => ClientSize.Width >= Dp(UiMetrics.PreparationColumnWidth + 600);
    public Control PreparationHost => _preparation;

    public HomeView(Label heading, Label empty, Label status, Button all, ConviteCards invites, LiveCardsView cards)
    {
        Dock = DockStyle.Fill;
        (_heading, _empty, _status, _all, _invites, _cards) = (heading, empty, status, all, invites, cards);
        _empty.Text = "Ninguém está transmitindo";
        _empty.Font = UiTypography.CreateSemibold(UiTypography.EmptyState);
        _empty.ForeColor = Theme.Text;
        _empty.TextAlign = ContentAlignment.MiddleCenter;
        UiScale.WatchFont(this); UiScale.WatchFont(_heading); UiScale.WatchFont(_empty);
        _scroll.Controls.AddRange([heading, all, invites, cards, _mumu, empty, status]);
        Controls.AddRange([_scroll, _preparation]);
        ScrollSkin.Attach(_scroll);
        foreach (var control in new Control[] { cards, invites, empty, all, heading, status })
        {
            control.SizeChanged += ContentChanged;
            control.VisibleChanged += ContentChanged;
        }
    }
    void ContentChanged(object? sender, EventArgs e) { if (!_arranging) PerformLayout(); }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_arranging || _scroll == null) return;
        _arranging = true;
        try
        {
            bool wide = HasPreparationSpace;
            int prep = wide ? Dp(UiMetrics.PreparationColumnWidth) : 0;
            bool changed = _preparation.Visible != wide;
            _preparation.Visible = wide;
            _preparation.Bounds = new Rectangle(ClientSize.Width - prep, 0, prep, ClientSize.Height);
            _scroll.Bounds = new Rectangle(0, 0, Math.Max(1, ClientSize.Width - prep), ClientSize.Height);
            int pad = Dp(UiMetrics.PagePadding), gap = Dp(UiMetrics.Gap);
            int width = Math.Max(1, _scroll.ClientSize.Width - pad * 2 - SystemInformation.VerticalScrollBarWidth);
            int y = pad + _scroll.AutoScrollPosition.Y;
            // Convites antes do título: ao aceitar, o card sai e no lugar do Aceitar sobe o título (clique nele não faz nada). Embaixo do
            // "Assistir todos juntos", o que subia era o cartão de uma live pública, e o segundo clique a abria junto (relato de 2026-11-15).
            _invites.AjustarLargura(width); _invites.Location = new Point(pad, y);
            if (_invites.Visible) y += _invites.Height + gap;
            int invitesHeight = _invites.Visible ? _invites.Height : 0;
            if (_invites.Visible != _invitesWasVisible || invitesHeight != _invitesWasHeight) { _invitesChangedAt = DateTime.Now; _invitesWasVisible = _invites.Visible; _invitesWasHeight = invitesHeight; }
            _heading.MaximumSize = new Size(width, 0);
            if (_heading.Visible) { _heading.Location = new Point(pad, y); y += _heading.PreferredHeight + gap; }
            if (_all.Visible) { int actionWidth = Math.Min(width, _all.PreferredSize.Width); _all.Bounds = new Rectangle(pad + (width - actionWidth) / 2, y, actionWidth, Dp(UiMetrics.ActionHeight)); y += _all.Height + gap; }
            _cards.Width = width; _cards.Location = new Point(pad, y);
            if (_cards.Visible) y += _cards.Height + gap;
            _empty.MaximumSize = new Size(width, 0);
            if (_empty.Visible)
            {
                // Mumu e frase como um bloco no meio do espaço que sobra (mockup 01); sem altura para a Mumu, fica só a frase.
                var label = _empty.GetPreferredSize(new Size(width, 0));
                int room = _scroll.ClientSize.Height - (y - _scroll.AutoScrollPosition.Y) - pad - _status.PreferredHeight;
                int art = Math.Min(Math.Min(width, Dp(MumuMaxAt96)), room - label.Height - gap);
                bool showArt = art >= Dp(MumuMinAt96);
                int block = (showArt ? art + gap : 0) + label.Height;
                int top = y + Math.Max(0, (room - block) / 2);
                if (showArt) _mumu.Bounds = new Rectangle(pad + (width - art) / 2, top, art, art);
                _mumu.Visible = showArt;
                _empty.Location = new Point(pad + Math.Max(0, (width - label.Width) / 2), top + (showArt ? art + gap : 0));
                y = top + block + gap;
            }
            else
            {
                _mumu.Visible = false;
                _empty.Location = new Point(pad, y);
            }
            // com o Início vazio a frase de status fica centrada sob o título, como a Mumu e "Ninguém está transmitindo" (antes ia para a esquerda e parecia fora do lugar)
            _status.MaximumSize = new Size(width, 0);
            var statusSize = _status.GetPreferredSize(new Size(width, 0));
            _status.Location = new Point(pad + Math.Max(0, (width - statusSize.Width) / 2), y);
            _scroll.AutoScrollMinSize = new Size(0, y - _scroll.AutoScrollPosition.Y + _status.PreferredHeight + pad);
            if (changed) PreparationSpaceChanged?.Invoke();
        }
        finally { _arranging = false; }
    }
    int Dp(int value) => UiScale.Dp(this, value);
}
