using Streemu.Ui;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// Abrir e recolher a barra lateral encaixada com animação, sem mexer no conteúdo durante o deslize. Deslizar a largura da barra refazia o layout do
/// conteúdo a cada passo e deixava pedaços do quadro anterior na tela (vídeos de 2026-11-25: pontas de botões e barra de rolagem duplicadas por ~0,15 s).
/// Agora a largura muda de uma vez, com uma pintura só (como na troca de tela), e quem desliza é a barra flutuante (<see cref="SideFlyout"/>) por cima do
/// lugar dela: ao recolher ela começa aberta e some; ao abrir ela entra e, no fim, a barra encaixada, que estava só com o fundo, mostra as linhas.
/// Com vídeo na tela a barra continua deslizando a largura, porque os vídeos são janelas à parte que precisam acompanhá-la.
/// </summary>
public sealed partial class MainForm
{
    bool _animandoBarra;

    bool PodeAnimarBarra() => _side.Visible && _side.Dock == DockStyle.Left && IsHandleCreated && Visible
        && WindowState != FormWindowState.Minimized && !_fullscreenStage && !VideoNaTela();

    /// <summary>Abre ou recolhe a barra encaixada deslizando. Ex.: <c>TrocarBarraAnimada(!_side.Collapsed)</c>.</summary>
    void TrocarBarraAnimada(bool recolhida)
    {
        EncerrarAnimacaoDaBarra();
        if (_side.Collapsed == recolhida) return;
        if (!PodeAnimarBarra()) { TrocarBarraDeslizandoLargura(recolhida); return; }
        if (recolhida) RecolherPelaFlutuante(); else AbrirPelaFlutuante();
    }

    void TrocarBarraDeslizandoLargura(bool recolhida)
    {
        _side.Collapsed = recolhida;
        PerformLayout();
        _playerStage?.Relayout();
    }

    /// <summary>A barra encaixada muda de largura na hora, numa pintura só, e refaz o conteúdo na largura final.</summary>
    void TrocarBarraNaHora(bool recolhida)
    {
        using var pausa = new PinturaPausada(this);
        _side.SnapCollapsed(recolhida);
        PerformLayout();
        _playerStage?.Relayout();
        FinishSlide();
    }

    Rectangle BarraAbertaNaTela()
    {
        var at = _side.RectangleToScreen(_side.ClientRectangle);
        return new Rectangle(at.Left, at.Top, UiScale.Dp(this, SidePanel.OpenAt96), at.Height);
    }

    void RecolherPelaFlutuante()
    {
        _animandoBarra = true;
        var flutuante = EnsureFlyout();
        RefreshSide();
        flutuante.ShowOpened(BarraAbertaNaTela(), this); // cobre o lugar da barra antes de o conteúdo andar
        TrocarBarraNaHora(true);
        flutuante.Dismiss();
    }

    void AbrirPelaFlutuante()
    {
        _animandoBarra = true;
        _side.ContentHidden = true;
        TrocarBarraNaHora(false);
        var flutuante = EnsureFlyout();
        RefreshSide();
        flutuante.ShowAt(BarraAbertaNaTela(), this);
    }

    /// <summary>A barra flutuante terminou de abrir (devolve as linhas à encaixada e some) ou de fechar (nada a devolver).</summary>
    void FimDaAnimacaoDaBarra()
    {
        if (!_animandoBarra) return;
        _animandoBarra = false;
        if (!_side.ContentHidden) return;
        _side.ContentHidden = false;
        _side.Update();
        _flyout?.HideNow();
    }

    /// <summary>Outro clique no meio da animação: termina a de antes na hora, sem esperar, e segue.</summary>
    void EncerrarAnimacaoDaBarra()
    {
        if (!_animandoBarra) return;
        _animandoBarra = false;
        _side.ContentHidden = false;
        _flyout?.HideNow();
    }
}
