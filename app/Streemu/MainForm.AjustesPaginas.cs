using Streemu.Ui;
using B = Streemu.Ui.AjusteBlocos;

namespace Streemu;

/// <summary>
/// O conteúdo de cada página de Configurações em cartões (repaginação de 2026-11-18). Só reúne os controles que o MainForm já tem, com seus eventos,
/// em <see cref="CartaoDeAjuste"/>s; os textos de apoio saíram dos rótulos compridos e foram para notas embaixo de cada interruptor.
/// </summary>
public sealed partial class MainForm
{
    /// <summary>
    /// A página Transmissão saiu em 2026-11-19: fonte, qualidade e áudio já ficam na tela Transmitir, o sinal de ao vivo e a minha live vieram para cá
    /// e Encoder/Latência SRT foram para Diagnóstico.
    /// </summary>
    Control PaginaAparencia()
    {
        var (sinal, live) = MinhaLiveOpcoes();
        return SettingsSection("Aparência",
            B.Cartao(B.Legenda("Barra lateral"), _pinSidebar,
                B.Nota("Ligada, a barra lateral não recolhe ao assistir nem em janela estreita. Desligada, ela vira um trilho de avatares nesses casos."),
                _hoverSidebar,
                B.Nota("Com a barra recolhida, passar o mouse sobre ela abre a navegação. Desligado, ela só abre pelo botão » .")),
            B.Cartao([B.Legenda("Sinal de ao vivo"), .. sinal]),
            B.Cartao([B.Legenda("Minha live"), .. live]));
    }

    Control PaginaAssistir() => SettingsSection("Assistir",
        B.Cartao(B.Legenda("Janela de streams"),
            B.Nota("Você pode assistir até 4 streams ao mesmo tempo.", alinhadaAoInterruptor: false),
            _hwDecode, B.Nota("Decodificar na placa deixa a imagem mais fluida. Desligue se a imagem ficar preta.")));

    Control PaginaNotificacoes() => SettingsSection("Notificações",
        B.Cartao(B.Legenda("Avisos de amigos"), AvisosOpcao(),
            B.Nota("Um aviso curto quando alguém liga uma live ou abre um filme. Vários avisos juntos viram um só."),
            B.Nota("O sino da navegação permite silenciar os avisos por um período. Para ajustar os avisos de um amigo, use o menu do avatar dele.")));

    Control PaginaConexao() => SettingsSection("Conexão",
        B.Cartao(B.Legenda("Rede"), B.Botoes(_firewall),
            B.Nota("A sala do grupo conecta sozinha. Se os amigos não conseguem chegar até você, libere o app no Firewall do Windows.", alinhadaAoInterruptor: false)));

    Control PaginaSistema() => SettingsSection("Sistema",
        B.Cartao(B.Legenda("Versão"), B.Nota($"Streemu · versão {Program.Version}", alinhadaAoInterruptor: false), B.Botoes(_checkUpdate),
            B.Subtitulo("Canal de atualização"), CanalAtualizacaoOpcao(),
            B.Nota("Stable recebe as versões compiladas no repositório público, que qualquer pessoa pode conferir. Nightly recebe as versões de teste primeiro. Por enquanto os dois recebem as mesmas atualizações.", alinhadaAoInterruptor: false)),
        B.Cartao(B.Legenda("Inicialização"), _startWithWindows, B.Nota("O Streemu abre escondido na bandeja quando você entra no Windows.")));

    Control PaginaDiagnostico() => SettingsSection("Diagnóstico",
        B.Cartao(_diagnosisSummary),
        B.Cartao(B.Legenda("Envio"), _teleEnviar,
            B.Nota("Dados do PC e logs para a equipe do Streemu, sem IP ou senhas. Desligado, continua subindo para consertar bugs, mas anônimo: sem seu nome, seu Discord nem o nome dos amigos."),
            B.Botoes(_teleMarcar, _teleVer, _teleSobre)),
        B.Cartao(B.Legenda("Ferramentas"), B.Botoes(_report, _openLogs)),
        B.Cartao(B.Legenda("Avançado"), B.Subtitulo("Encoder"), _encoder, B.Subtitulo("Latência SRT"), _latency,
            B.Nota("Deixe o encoder em Automático, a menos que a live não comece. A latência vale também para as lives que você assiste. Os dois voltam ao padrão quando o app abre.", alinhadaAoInterruptor: false)),
        Disclosure("Detalhes técnicos", _teleStatus), LogToggle());
}
