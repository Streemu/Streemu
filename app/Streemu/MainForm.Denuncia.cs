using Streemu.Net;
using Streemu.Ui;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// Denunciar uma live (spec 029, E3): a bandeira da barra do player abre a janela, e o envio sai em segundo plano para <c>POST /denuncia</c>. O app mostra SEMPRE o mesmo
/// agradecimento, qualquer que seja a resposta do servidor (spec 029, 3.1). <b>Nada aqui vai ao log nem à telemetria</b> (15.16): nem "denúncia", nem a categoria, nem o
/// alvo. Por isso o aviso NÃO passa pelo <c>Notify</c> (que loga o que mostra) nem pelo <c>MostrarBalao</c> (que loga o balão calado pelo modo silêncio).
/// </summary>
public sealed partial class MainForm
{
    /// <summary>O que a barra do player precisa para denunciar a live desta ligação (quem assiste).</summary>
    PlayerOverlay.ReportHook DenunciaDoPlayer(PeerLink link) => new(() => AlvoDaDenuncia(link), AbrirDenuncia, AvisoDaDenuncia);

    /// <summary>
    /// Grupo e live da ligação, ou nulo se não dá para saber (sem conta, live sem dono na sala, ou dois grupos em comum e nenhum mostra a live). A live é o <c>sala</c> do
    /// <c>ao_vivo</c> do membro (guardado na ligação como <c>StreamId</c>) e o grupo é o da sala em que esse membro aparece transmitindo essa live.
    /// </summary>
    DenunciaAlvo? AlvoDaDenuncia(PeerLink link)
    {
        if (link.IAmSender || link.StreamId is not { Length: > 0 } live || _salaUnida is not { } sala || _conta is not { TemConta: true }) return null;
        if (sala.GrupoDaLive(link.PeerId, live) is not { Length: > 0 } grupo) return null;
        string apelido = sala.Find(link.PeerId) is { } membro ? NomeNaTela(membro) : NomeNaTela(link.PeerId, link.PeerNick);
        return new DenunciaAlvo(grupo, live, apelido);
    }

    /// <summary>Abre a janela (modal) e, ao enviar, tira a imagem (se a pessoa pediu), manda em segundo plano e agradece. Cancelar não faz nada e não captura nada.</summary>
    void AbrirDenuncia(DenunciaAlvo alvo, Func<byte[]?>? capturar)
    {
        using var janela = new DenunciaForm(alvo.Apelido, capturar);
        bool comDono = Visible && WindowState != FormWindowState.Minimized; // minimizado o "centro do dono" cairia fora da tela
        janela.StartPosition = comDono ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
        var resultado = comDono ? janela.ShowDialog(this) : janela.ShowDialog();
        if (resultado != DialogResult.OK || janela.Categoria is not { } categoria) return;
        var corpo = Denuncia.MontarCorpo(alvo, categoria, janela.Texto, janela.CapturarAoEnviar()); // a imagem só nasce aqui, depois do Enviar
        if (_conta is { TemConta: true } conta)
            _ = Task.Run(async () => { try { await conta.DenunciarAsync(corpo); } catch { /* sem log: o agradecimento já foi dado e o servidor não diz se aceitou */ } });
        AvisoDaDenuncia(Denuncia.Agradecimento);
    }

    /// <summary>Aviso curto na barra de status e no balão da bandeja, sem escrever no log (ver o resumo da classe). Não faz nada em teste automático.</summary>
    void AvisoDaDenuncia(string texto)
    {
        _groupStatus.Text = texto;
        if (_tray != null && !Automated && !ModoSilencio.Ativo) _tray.ShowBalloonTip(5000, UiBrand.Name, texto, ToolTipIcon.Info);
    }
}
