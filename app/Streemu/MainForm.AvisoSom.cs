using Streemu.Media;

namespace Streemu;

/// <summary>
/// Aviso do som do amigo na própria live (diagnóstico de UX de 13/11, item 1). O modo "PC todo menos Discord" só consegue excluir UM programa (o Discord);
/// o player da live de um amigo é outro processo, então o som dele entrava na transmissão de quem assiste e transmite ao mesmo tempo.
/// O conserto de verdade é o <see cref="AppAudioSubtractor"/> (Media/), que tira o som do Streemu dessa captura; este aviso só aparece se ele não está
/// rodando (o Windows recusou a segunda captura, ou STREEMU_SOM_AMIGO=vaza). Antes só ia uma linha para o log, e só para o player do código manual.
/// </summary>
public sealed partial class MainForm
{
    const string AvisoSomDoAmigo = "Seu áudio é \"PC todo menos Discord\": o som da live que você assiste vai na sua. Transmitindo uma janela, \"Só o app\" evita isso.";

    /// <summary>Verdadeiro se o som de quem eu assisto vai parar na minha live (live no ar nesse modo + pelo menos uma live sendo assistida).</summary>
    internal static bool SomDoAmigoVazaNaLive(AudioMode? modoDaLive, int livesAssistidas) => modoDaLive == AudioMode.AllExceptDiscord && livesAssistidas > 0;

    int LivesAssistidas => _links.Values.Count(l => !l.IAmSender);

    bool SomDoAmigoVazaAgora => !_sender.AppAudioRemoved && SomDoAmigoVazaNaLive(_sender.Running ? _sender.Current?.Audio : null, LivesAssistidas);

    /// <summary>Chamado quando uma live começa ou uma live passa a ser assistida: avisa uma vez (o texto fica na linha de status enquanto o problema durar).</summary>
    void AvisarSomDoAmigoNaLive()
    {
        if (!SomDoAmigoVazaAgora) return;
        Notify(AvisoSomDoAmigo); // já vai para o log
    }
}
