using Streemu.Util;

namespace Streemu.Telemetria;

/// <summary>
/// O que o Streemu diz à pessoa sobre o diagnóstico: a faixa da primeira abertura (e de quando a regra muda), o aviso completo da dica e a página
/// "Sobre o diagnóstico", que o instalador também mostra (<see cref="Exportar"/>).
/// Fica num lugar só para o texto não ficar diferente do que o app e o servidor fazem: os números abaixo têm teste que compara com o Worker
/// (<c>SESSAO_GUARDA_MS</c> em relay/telemetria.js) e a spec 018. Quem muda a coleta, o prazo ou o procedimento de exclusão muda aqui no mesmo PR.
/// Frases curtas, no tom do app; não é parecer jurídico (ver spec 018). Quem fala é sempre "a equipe do Streemu", nunca uma pessoa (D35).
/// </summary>
public static class TextoDiagnostico
{
    /// <summary>Quantos dias o servidor guarda logs, séries e relatórios (o mesmo número do Worker).</summary>
    public const int DiasDeGuarda = 120;
    /// <summary>Depois de quantos dias sem contato o servidor apaga o perfil do PC, os nomes e o Discord (spec 018, D24; a limpeza do Worker é a B4).</summary>
    public const int DiasDePerfil = 180;
    /// <summary>Prazo para apagar o que a pessoa pedir.</summary>
    public const string PrazoExclusao = "5 dias úteis";
    /// <summary>A política de privacidade publicada (a mesma que a tela de entrada abre).</summary>
    public const string UrlPolitica = "https://www.streemu.com.br/privacidade";
    /// <summary>O texto do link discreto da faixa.</summary>
    public const string TextoDoLink = "Como cuidamos dos seus dados";
    /// <summary>
    /// Sobe a cada vez que o texto muda de um jeito que a pessoa precisa ler de novo: quem já tinha visto uma versão menor vê a faixa uma vez.
    /// 3 = spec 018: desligado passou a ser desligado de verdade (só a camada essencial), o anônimo virou escolha e entrou a base legal.
    /// </summary>
    public const int VersaoDoAviso = 3;

    public static string TituloDoAviso => "Sobre o diagnóstico do Streemu";

    /// <summary>A linha de Ajustes > Diagnóstico sobre o uso de IA (spec 018, D31); o "Saiba mais" abre a política.</summary>
    public const string LinhaDaIa = "A equipe usa ferramentas de IA para ler o diagnóstico, sem o seu nome.";

    /// <summary>A pessoa ainda não viu esta versão do texto (instalação nova, ou texto que mudou desde a última vez).</summary>
    public static bool AvisoPendente(AppState s) => s.TelemetriaAvisoVersao < VersaoDoAviso;

    /// <summary>Marca a faixa como vista. Não mexe na escolha do diagnóstico: quem tinha desligado continua desligado.</summary>
    public static void MarcarVisto(AppState s)
    {
        s.TelemetriaAvisoVisto = true;
        s.TelemetriaAvisoVersao = VersaoDoAviso;
    }

    /// <summary>
    /// A frase da faixa do topo: curta, sem assustar; o detalhe fica na dica do texto e no "Saiba mais". O Ligado usa a frase da spec 018 (D13); quem
    /// tinha desligado lê o que a camada essencial manda e por que ela não desliga (seção 2.1).
    /// </summary>
    public static string Faixa(ModoEnvio modo) => modo switch
    {
        ModoEnvio.Desligado => "Você tinha desligado o diagnóstico e continua assim: nenhum log nem dado do seu PC sai. O Streemu só conta que o app abriu, que uma live aconteceu ou que algo grave falhou, sem guardar quem foi. Isso não desliga: é o mínimo para a conta e o grupo funcionarem em segurança.",
        ModoEnvio.Anonimo => "Você escolheu o diagnóstico anônimo: o Streemu manda dados técnicos sem o seu nome, o seu Discord nem o nome dos amigos. Anônimo não é anonimato completo. Dá para desligar em Ajustes.",
        _ => "Cada PC é de um jeito. Para o Streemu rodar liso no seu, ele manda uns dados técnicos para a gente caçar bugs que só aparecem em certas combinações de placa, driver e Windows. Prefere não mandar? Pode desligar.",
    };

    /// <summary>O aviso mais longo, na dica da faixa. Quem tinha desligado lê outra coisa: para essa pessoa o que mudou é o que a camada essencial manda.</summary>
    public static string Aviso(ModoEnvio modo) => modo switch
    {
        ModoEnvio.Desligado => "Você tinha desligado o diagnóstico e continua desligado: nenhum log, dado do PC nem série de quadros sai do seu PC. A camada essencial, que não desliga, só conta que o app abriu e fechou, que uma live começou e acabou e que algo grave falhou, e não guarda quem foi (o servidor soma 1 no dia e descarta o pedido). O resto dela é a sua conta, os grupos e a moderação, que o serviço precisa para funcionar com segurança. Se quiser ajudar sem dizer quem é, ligue o diagnóstico anônimo em Ajustes > Diagnóstico.",
        ModoEnvio.Anonimo => "Você escolheu o diagnóstico anônimo: o Streemu manda log e dados do PC sem o seu nome, o seu Discord nem o nome dos amigos que ele conhece. Anônimo não é anonimato completo: o jogo, o horário e o hardware ainda podem te reconhecer para quem procure. Só a equipe do Streemu lê. Para não mandar nada, desligue em Ajustes > Diagnóstico.",
        _ => $"O Streemu manda diagnóstico para a equipe do Streemu consertar bugs: log do app, dados do PC (processador, placa de vídeo, Windows, rede) e métricas das lives, ligados ao seu nome e ao seu Discord. Sem IP, sem senhas, sem o nome do seu usuário do Windows. Só a equipe lê. Os logs ficam guardados {DiasDeGuarda} dias. Se desligar, nada de diagnóstico sai do PC. Dá para pedir para apagar tudo.",
    };

    /// <summary>
    /// Grava o título e <see cref="Completo"/> em UTF-8 com BOM e fim de linha do Windows (o que o Inno Setup lê), para o instalador mostrar o mesmo texto do app.
    /// Devolve o código de saída: 0 gravou, 1 não conseguiu gravar, 2 sem caminho. Ex.: <c>Streemu.exe --exportar-privacidade dist\privacidade.txt</c>.
    /// </summary>
    public static int Exportar(string? arquivo)
    {
        if (string.IsNullOrWhiteSpace(arquivo))
        {
            Console.Error.WriteLine("--exportar-privacidade precisa do caminho do arquivo (ex.: --exportar-privacidade dist\\privacidade.txt); veio vazio.");
            return 2;
        }
        try
        {
            string texto = $"{TituloDoAviso}\n\n{Completo}\n".Replace("\r\n", "\n").Replace("\n", "\r\n");
            File.WriteAllText(arquivo, texto, new System.Text.UTF8Encoding(true));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Não consegui gravar {arquivo}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>A página completa de Ajustes > Diagnóstico > "Sobre o diagnóstico" e da página do instalador.</summary>
    public static string Completo => $@"O QUE VAI NO DIAGNÓSTICO
- Log do app e relatórios de problema (o mesmo texto do ""Copiar relatório"").
- Dados do PC: processador, placa de vídeo e driver, memória, Windows, tela, áudio e rede (provedor e região, nunca o IP).
- Métricas das lives: quadros por segundo, atraso, qualidade da conexão com cada amigo. A série segundo a segundo da live fica 7 dias só no seu PC e só sobe se você marcar um problema, se o app tiver uma falha grave, se a live tiver um problema que dura vários minutos ou se um amigo da mesma live marcar um problema. Em Ajustes > Diagnóstico você vê o que foi enviado e pode apagar.
- Quem é você para o servidor: um id do PC e da instalação, seu nome no app e no Discord, e o nome dos amigos que aparecem nos logs.
- Se você usa o ""Marcar problema"", a hora e a nota que escrever.
Não vai: IP, senha, o nome do seu usuário do Windows, o MAC da placa de rede, áudio ou imagem das lives.

PARA QUÊ
Achar e consertar bugs sem pedir para você copiar log. Nada disso é vendido nem usado para outra coisa.

POR QUE PODEMOS (BASE LEGAL)
Cada parte tem a sua base na LGPD:
- Sua conta e seus grupos: execução do contrato, porque é o serviço que você pediu ao criar a conta.
- Moderação (denúncias e bloqueios, com um código irreversível do aparelho no login): legítimo interesse da comunidade segura e exercício regular de direitos (art. 7º, VI e IX). Se alguém se opuser, a segurança dos outros prevalece.
- O diagnóstico: legítimo interesse de consertar o app que você usa (art. 7º, IX). Por isso ele vem ligado, mas você desliga quando quiser.
- Contagem de uso e de falhas graves: o servidor soma 1 no contador do dia e descarta o pedido. O que fica guardado não é dado de pessoa.

O QUE NUNCA DESLIGA (CAMADA ESSENCIAL)
Conta, grupos, moderação e a contagem de uso: que o app abriu e fechou, que uma live começou e acabou (se você transmitiu ou assistiu, e se era privada) e o tipo de uma falha grave. Sem log, sem hardware, sem nome de jogo nem de amigo. Vale mesmo com o diagnóstico desligado.

O QUE OS AMIGOS E O DISCORD VEEM
Isto não é diagnóstico, mas é bom saber:
- Na conexão direta entre os PCs, o amigo que assiste a sua live (ou cuja live você assiste) recebe o endereço de rede (IP) do seu PC. É assim que a imagem vai direto, sem passar por servidor.
- Com o Discord aberto, o seu perfil mostra que você está no Streemu, se está ao vivo e o jogo. Quem vê depende das suas configurações de privacidade no Discord.

A GENTE USA IA PARA CONSERTAR O STREEMU
O diagnóstico é muito texto para ler à mão, então a equipe do Streemu usa ferramentas de IA (hoje, Claude e Codex) como assistentes: elas leem os relatórios técnicos, apontam a causa provável de um bug e ajudam a escrever a correção. Quem decide e revisa é sempre a equipe.
- A IA lê só o diagnóstico técnico, com um código no lugar do seu nome. Nunca o seu apelido, e-mail, Discord nem o nome do seu PC.
- As ferramentas são configuradas para não treinar com os seus dados, e os provedores apagam as conversas no prazo deles.
- A IA não decide nada sobre você: não bane, não mexe na sua conta, não lê denúncias nem pagamentos.
- Desligou o diagnóstico? Então não tem nada seu para a IA ler.

QUEM VÊ
Só a equipe do Streemu, com uma chave que os outros amigos não têm.

POR QUANTO TEMPO
- Logs, métricas e relatórios: {DiasDeGuarda} dias; depois o servidor apaga.
- A lista de quem é cada PC (id, nomes, provedor, perfil do PC): {DiasDePerfil} dias sem você abrir o app; depois o servidor apaga, ou antes, se você pedir.
- Contadores de uso: não guardam pessoa, então não têm prazo.
- No seu PC: o log fica {Manutencao.DiasDeLog} dias; a série segundo a segundo da live, 7 dias; o histórico do que foi enviado, 30 dias; o avatar guardado, {Manutencao.DiasDeAvatar} dias sem aparecer.

ONDE FICA
No servidor do grupo, na Cloudflare (Workers e R2), que pode guardar fora do Brasil. Uma cópia fica no seu PC, na pasta que ""Ver o que é enviado"" abre, até ser enviada e por alguns dias depois. O que tem gente dentro (log, histórico, nome e conta) fica cifrado no seu PC: só a sua conta do Windows abre. Um programa rodando como você ainda consegue ler.

SE VOCÊ DESLIGAR
Nenhum log, dado do PC ou série sai do seu PC, nem anônimo. O que ainda não tinha subido não sobe mais. A camada essencial segue (só contagem, sem guardar quem foi). O que já tinha sido enviado continua guardado até acabar o prazo ou você pedir para apagar.

DIAGNÓSTICO ANÔNIMO (SÓ SE VOCÊ LIGAR)
Com o diagnóstico desligado, você pode ligar ""Enviar diagnóstico anônimo"" para ajudar sem dizer quem é. O log e os dados do PC sobem, mas o seu nome, o seu Discord e o nome dos amigos que o app conhece viram ""pessoa-1"", ""pessoa-2""...; o id do PC e da instalação são outros, que não se ligam ao que você já mandou; e a nota do ""Marcar problema"" não sobe.
Anônimo não é anonimato completo: é o melhor esforço. Um nome que o app nunca viu (digitado num título de janela, por exemplo), o jogo, o horário e o hardware ainda podem te reconhecer para quem procure e cruze os dados.

APAGAR
Peça à equipe do Streemu (no grupo). Ela apaga tudo seu do servidor (logs, métricas, relatórios, perfil, nomes e a lista de lives) em até {PrazoExclusao} e confirma, dizendo o que ficou e por quê (por exemplo, o bloqueio de quem foi banido e as contagens sem pessoa). Não dá para apagar o que os amigos têm nos logs deles sobre você, nem o que você mandou em modo anônimo (não há como saber que foi você).

Para ver o que está no seu PC e vai ser enviado: Ajustes > Diagnóstico > ""Ver o que é enviado"".
Política completa: {UrlPolitica}";
}
