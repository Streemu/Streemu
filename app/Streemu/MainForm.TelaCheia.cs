namespace Streemu;

/// <summary>
/// Saída da tela cheia do palco de lives pelo teclado da janela principal. Fica fora do MainForm.Janela.cs para dar menos conflito.
/// </summary>
public sealed partial class MainForm
{
    /// <summary>
    /// Esc sai da tela cheia do palco quando o foco está na janela do app (por exemplo, depois de clicar na margem escura entre os vídeos). Com o foco no
    /// vídeo, quem pega o Esc é o <see cref="Ui.Multistream.StageEscape"/> do palco (decisão de 13/11/2026: sai da tela cheia em vez de fechar a live).
    /// O Cinema trata o próprio Esc (FilmeTela e StreamTela); aqui só o palco de lives, para não alternar duas vezes.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && SairDaTelaCheiaDoPalco()) return true;
        if (TryScaleKey(keyData)) return true; // MainForm.Escala.cs
        return base.ProcessCmdKey(ref msg, keyData);
    }

    bool SairDaTelaCheiaDoPalco()
    {
        if (!_fullscreenStage || !_stageView || _filmeView) return false;
        ToggleFullscreen();
        return true;
    }
}
