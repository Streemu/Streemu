using Streemu.Ui;
using Streemu.Util;

namespace Streemu;

/// <summary>
/// O menu que abre ao clicar na foto de um amigo na barra lateral (spec 013): Assistir/Fechar, Convidar e Silenciar avisos.
/// Convidar sem live abre a janela Transmitir já em "Só convidados" com ele marcado; com a live privada no ar manda o convite na hora.
/// Novas ações por amigo entram em <see cref="ItensDoMenuAmigo"/>.
/// </summary>
public sealed partial class MainForm
{
    ContextMenuStrip? _menuAmigo; // o menu aberto (o teste o fotografa)

    void LigarMenuAmigo(SidePanel painel) => painel.FriendAvatarClicked += (amigo, fotoNaTela) => MostrarMenuAmigo(amigo, fotoNaTela);

    void MostrarMenuAmigo(SideFriend amigo, Rectangle fotoNaTela)
    {
        var menu = new ContextMenuStrip();
        _menuAmigo = menu;
        TrayVisuals.Style(menu);
        menu.Items.Add(new ToolStripLabel(CabecalhoDoAmigo(amigo)) { ForeColor = Theme.TextMuted });
        foreach (var item in ItensDoMenuAmigo(amigo)) menu.Items.Add(item);
        menu.Closed += (_, _) => menu.BeginInvoke(menu.Dispose);
        SegurarBarraFlutuante(menu); // MainForm.Janela.cs: a barra flutuante não abre por cima do menu
        menu.Show(new Point(fotoNaTela.Right + 4, fotoNaTela.Top));
    }

    IEnumerable<ToolStripItem> ItensDoMenuAmigo(SideFriend amigo)
    {
        if (amigo.Offline) // está com o app fechado: não dá para assistir, enviar nem convidar (plano 019, etapa 7)
        {
            foreach (var item in ItensDeApelido(amigo)) yield return item;
            yield return new ToolStripSeparator();
            yield return ItemSilenciar(amigo);
            yield break;
        }
        if (amigo.Live) yield return ItemAssistir(amigo);
        if (ArquivosLigado) yield return new ToolStripMenuItem("Enviar arquivo", null, (_, _) => BeginInvoke(() => MostrarArquivos(amigo.Id))); // spec 020, D9
        yield return ItemConvidar(amigo);
        yield return new ToolStripSeparator();
        foreach (var item in ItensDeApelido(amigo)) yield return item;
        yield return ItemSilenciar(amigo);
    }

    ToolStripItem ItemAssistir(SideFriend amigo)
    {
        bool assistindo = _links.Values.Any(l => !l.IAmSender && l.PeerId == amigo.Id);
        return new ToolStripMenuItem(assistindo ? "Fechar a live" : "Assistir", null, (_, _) => ToggleWatchFriend(amigo.Id));
    }

    /// <summary>Convidar: o texto e o que acontece dependem da minha live (nenhuma, privada ou pública).</summary>
    ToolStripItem ItemConvidar(SideFriend amigo)
    {
        if (!(_sender.Running || _fakeLive)) // _fakeLive: a live de mentira dos testes automáticos
            return new ToolStripMenuItem("Convidar para uma transmissão privada", null, (_, _) => BeginInvoke(() => OpenGoLive(changing: false, convidarId: amigo.Id)));
        if (!Privada.Ligada)
            return new ToolStripMenuItem("Convidar (sua live é pública: ele já pode assistir)") { Enabled = false };
        if (Privada.EstaNaLista(amigo.Id))
            return new ToolStripMenuItem("Já convidado para a sua transmissão") { Enabled = false };
        return new ToolStripMenuItem("Convidar para a sua transmissão privada", null, (_, _) => _ = ConvidarDoMenuAsync(amigo));
    }

    async Task ConvidarDoMenuAsync(SideFriend amigo)
    {
        string? motivo = await ConvidarAsync(amigo.Id, amigo.Nick);
        Notify(motivo ?? $"Convite enviado para {amigo.Nick}.");
    }

    ToolStripItem ItemSilenciar(SideFriend amigo)
    {
        bool mudo = AmigosMudos.Contem(amigo.Id);
        return new ToolStripMenuItem(mudo ? $"Reativar avisos de {amigo.Nick}" : $"Silenciar avisos de {amigo.Nick}", null, (_, _) =>
        {
            bool agoraMudo = AmigosMudos.Alternar(amigo.Id);
            Notify(agoraMudo ? $"Avisos de {amigo.Nick} silenciados. Convites dele continuam chegando." : $"Avisos de {amigo.Nick} reativados.");
        });
    }
}
