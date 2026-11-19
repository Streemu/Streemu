using Streemu.Util;

namespace Streemu.Ui;

/// <summary>
/// Fontes da tela de entrada no tamanho do desenho. <paramref name="fator"/> é a escala do usuário (<see cref="UiScale.Factor(Control)"/>);
/// o DPI já entra pelo ponto da fonte. Quem cria descarta.
/// </summary>
internal sealed class EntradaFontes : IDisposable
{
    readonly List<Font> _todas = new();
    public Font Frase { get; }
    public Font Sobretitulo { get; }
    public Font Heroi { get; }
    public Font Apoio { get; }
    public Font Titulo { get; }
    public Font Rotulo { get; }
    public Font Codigo { get; }
    public Font Acao { get; }
    public Font Fino { get; }
    public Font FinoNegrito { get; }
    public Font Rodape { get; }
    public Font RodapeLink { get; }

    public EntradaFontes(float fator)
    {
        Frase = Nova(UiTypography.LoginTagline, FontStyle.Regular, fator);
        Sobretitulo = Nova(UiTypography.LoginEyebrow, FontStyle.Bold, fator);
        Heroi = Nova(UiTypography.LoginHero, FontStyle.Bold, fator);
        Apoio = Nova(UiTypography.LoginLead, FontStyle.Regular, fator);
        Titulo = Nova(UiTypography.LoginTitle, FontStyle.Bold, fator);
        Rotulo = Nova(UiTypography.LoginLabel, FontStyle.Bold, fator);
        Codigo = Nova(UiTypography.LoginCode, FontStyle.Bold, fator);
        Acao = Nova(UiTypography.LoginAction, FontStyle.Bold, fator);
        Fino = Nova(UiTypography.LoginFine, FontStyle.Regular, fator);
        FinoNegrito = Nova(UiTypography.LoginFine, FontStyle.Bold, fator);
        Rodape = Nova(UiTypography.LoginFooter, FontStyle.Regular, fator);
        RodapeLink = Nova(UiTypography.LoginFooterLink, FontStyle.Bold, fator);
    }

    Font Nova(float pontos, FontStyle estilo, float fator)
    {
        var fonte = UiTypography.Create(pontos * fator, estilo);
        _todas.Add(fonte);
        return fonte;
    }

    public void Dispose()
    {
        foreach (var fonte in _todas) fonte.Dispose();
    }
}
