using Streemu.Ui.Avatars;
using Streemu.Util;
using static Streemu.Ui.AjusteBlocos;

namespace Streemu.Ui;

/// <summary>
/// A página Perfil de Configurações (repaginada em 2026-11-18): um cartão com o avatar grande, o nome da conta e os botões da imagem, e um cartão do
/// Discord com interruptores. Só monta o visual com os controles que o MainForm já tem (e seus eventos); não guarda estado.
/// Uso: <c>var cartoes = PerfilLayout.Montar(apelido, avatar, escolher, remover, usarAvatarDiscord, participarSemNavegador);</c>
/// </summary>
public static class PerfilLayout
{
    const int AvatarAt96 = 104;

    public static Control[] Montar(TextBox apelido, AvatarBadge avatar, Button escolher, Button remover, CheckBox usarAvatarDiscord, CheckBox participarSemNavegador)
    {
        return [CartaoDoPerfil(apelido, avatar, escolher, remover), CartaoDoDiscord(usarAvatarDiscord, participarSemNavegador)];
    }

    static CartaoDeAjuste CartaoDoPerfil(TextBox apelido, AvatarBadge avatar, Button escolher, Button remover)
    {
        avatar.Size = new Size(AvatarAt96, AvatarAt96);
        avatar.Margin = new Padding(0, 4, 24, 0);
        apelido.Font = UiTypography.Create(UiTypography.Heading);
        var campo = new CampoArredondado(apelido) { Margin = new Padding(0, 6, 0, 4) };
        var coluna = Coluna(Legenda("Nome da conta"), campo, Nota("É como seus amigos veem você em todos os aparelhos. Sem imagem, aparece a inicial do nome.", alinhadaAoInterruptor: false), Botoes(escolher, remover));
        return Cartao(Linha(avatar, coluna)); // pela Coluna: a linha do avatar começa na mesma borda dos títulos dos outros cartões
    }

    static CartaoDeAjuste CartaoDoDiscord(CheckBox usarAvatar, CheckBox participar)
    {
        participar.Text = "Botão Participar sem abrir o navegador";
        usarAvatar.Margin = new Padding(0, 2, 0, 0);
        participar.Margin = new Padding(0, 2, 0, 0);
        return Cartao(
            Legenda("Discord"),
            usarAvatar, Nota("Mostra a foto da sua conta do Discord quando você não escolheu uma imagem."),
            participar, Nota("Experimental: você e quem entra na sua live precisam estar nesta versão do Streemu."));
    }
}
