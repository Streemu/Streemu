using Streemu.Ui;
using B = Streemu.Ui.AjusteBlocos;

namespace Streemu;

/// <summary>
/// O conteúdo de cada página de Configurações em cartões (repaginação de 2026-11-18). Só reúne os controles que o MainForm já tem, com seus eventos,
/// em <see cref="CartaoDeAjuste"/>s; os textos de apoio saíram dos rótulos compridos e foram para notas embaixo de cada interruptor.
/// </summary>
public sealed partial class MainForm
{
    Control PaginaAparencia() => SettingsSection("Aparência",
        B.Cartao(B.Legenda("Barra lateral"), _pinSidebar,
            B.Nota("Ligada, a barra lateral não recolhe ao assistir nem em janela estreita. Desligada, ela vira um trilho de avatares nesses casos."),
            _hoverSidebar,
            B.Nota("Com a barra recolhida, passar o mouse sobre ela abre a navegação. Desligado, ela só abre pelo botão » .")));

    Control PaginaTransmissao(Label dicaDaFonte)
    {
        dicaDaFonte.ForeColor = SystemColors.GrayText;
        dicaDaFonte.Margin = new Padding(0, 6, 0, 0);
        var (sinal, live) = MinhaLiveOpcoes();
        return SettingsSection("Transmissão",
            B.Cartao(B.Legenda("O que transmitir"), B.Botoes(_source, _refresh), dicaDaFonte),
            B.Cartao(B.Legenda("Qualidade e áudio"), B.Subtitulo("Qualidade"), _preset, B.Subtitulo("Áudio"), _audio, B.Subtitulo("Encoder"), _encoder, B.Subtitulo("Latência SRT"), _latency),
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
        B.Cartao(B.Legenda("Versão"), B.Nota($"Streemu · versão {Program.Version}", alinhadaAoInterruptor: false), B.Botoes(_checkUpdate)),
        B.Cartao(B.Legenda("Inicialização"), _startWithWindows, B.Nota("O Streemu abre escondido na bandeja quando você entra no Windows.")));

    Control PaginaDiagnostico() => SettingsSection("Diagnóstico",
        B.Cartao(_diagnosisSummary),
        B.Cartao(B.Legenda("Envio"), _teleEnviar,
            B.Nota("Dados do PC e logs para a equipe do Streemu, sem IP ou senhas. Desligado, continua subindo para consertar bugs, mas anônimo: sem seu nome, seu Discord nem o nome dos amigos."),
            B.Botoes(_teleMarcar, _teleVer, _teleSobre)),
        B.Cartao(B.Legenda("Ferramentas"), B.Botoes(_report, _openLogs)),
        Disclosure("Detalhes técnicos", _teleStatus), LogToggle());
}
