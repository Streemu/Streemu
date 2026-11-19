namespace Streemu.Telemetria;

/// <summary>
/// O que o Streemu diz à pessoa sobre o diagnóstico: o aviso curto (primeira abertura e quando a regra muda) e a página "Sobre o diagnóstico".
/// Fica num lugar só para o texto não ficar diferente do que o app e o servidor fazem: os números abaixo têm teste que compara com o Worker
/// (<c>SESSAO_GUARDA_MS</c> em relay/telemetria.js) e a spec 009. Quem muda a coleta, o prazo ou o procedimento de exclusão muda aqui no mesmo PR.
/// Frases curtas, no tom do app; não é parecer jurídico (ver spec 018).
/// </summary>
public static class TextoDiagnostico
{
    /// <summary>Quantos dias o servidor guarda logs, séries e relatórios (o mesmo número do Worker).</summary>
    public const int DiasDeGuarda = 120;
    /// <summary>Prazo para apagar o que a pessoa pedir.</summary>
    public const string PrazoExclusao = "5 dias úteis";
    /// <summary>Sobe a cada vez que o texto muda de um jeito que a pessoa precisa ler de novo: quem já tinha visto uma versão menor vê o aviso uma vez.</summary>
    public const int VersaoDoAviso = 2;

    public static string TituloDoAviso => "Sobre o diagnóstico do Streemu";

    /// <summary>O aviso curto. Quem tinha desligado o diagnóstico lê outra coisa: para essa pessoa o que mudou é justamente o que acontece quando desliga.</summary>
    public static string Aviso(bool tinhaDesligado) => tinhaDesligado
        ? "Você tinha desligado o diagnóstico. Agora, desligado, o Streemu ainda manda um diagnóstico ANÔNIMO: log e dados do PC sem o seu nome, o seu Discord nem o nome dos amigos que ele conhece, com um id que não se liga ao que você mandou antes. É para a gente achar bugs que só acontecem com quem desliga. Só a equipe do Streemu lê. Para apagar o que já foi enviado, peça à equipe."
        : $"O Streemu manda diagnóstico para a equipe do Streemu consertar bugs: log do app, dados do PC (processador, placa de vídeo, Windows, rede) e métricas das lives, ligados ao seu nome e ao seu Discord. Sem IP, sem senhas, sem o nome do seu usuário do Windows. Só a equipe lê. Os logs ficam guardados {DiasDeGuarda} dias. Você pode desligar em Ajustes > Diagnóstico (aí ele manda só anônimo) e pedir para apagar tudo.";

    /// <summary>A página completa de Ajustes > Diagnóstico > "Sobre o diagnóstico".</summary>
    public static string Completo => $@"O QUE VAI
- Log do app e relatórios de problema (o mesmo texto do ""Copiar relatório"").
- Dados do PC: processador, placa de vídeo e driver, memória, Windows, tela, áudio e rede (provedor e região, nunca o IP).
- Métricas das lives: quadros por segundo, atraso, qualidade da conexão com cada amigo.
- Quem é você para o servidor: um id do PC e da instalação, seu nome no app e no Discord, e o nome dos amigos que aparecem nos logs.
- Se você usa o ""Marcar problema"", a hora e a nota que escrever.
Não vai: IP, senha, o nome do seu usuário do Windows, o MAC da placa de rede, áudio ou imagem das lives.

PARA QUÊ
Achar e consertar bugs sem pedir para você copiar log. Nada disso é vendido nem usado para outra coisa.

QUEM VÊ
Só a equipe do Streemu, com uma chave que os outros amigos não têm.

POR QUANTO TEMPO
- Logs, métricas e relatórios: {DiasDeGuarda} dias; depois o servidor apaga.
- A lista de quem é cada PC (id, nomes, provedor, perfil do PC) fica sem prazo, até você pedir para apagar.

ONDE FICA
No servidor do grupo, na Cloudflare (Workers e R2). Uma cópia fica no seu PC, na pasta que ""Ver o que é enviado"" abre, até ser enviada e por alguns dias depois.

SE VOCÊ DESLIGAR
O Streemu não zera o diagnóstico: ele passa a mandar ANÔNIMO. O log e os dados do PC continuam, mas o seu nome, o seu Discord e o nome dos amigos que ele conhece viram ""pessoa-1"", ""pessoa-2""...; o id do PC e da instalação passam a ser outros, que não se ligam ao que você já mandou; e a nota do ""Marcar problema"" não sobe. Isso é o melhor esforço: um nome que o app nunca viu (digitado num título de janela, por exemplo), o jogo, o horário e o hardware ainda podem te reconhecer para quem procure. O que já tinha sido enviado continua guardado até acabar o prazo ou você pedir para apagar.

APAGAR
Peça à equipe do Streemu (no grupo). Ela apaga tudo seu do servidor (logs, métricas, relatórios, perfil, nomes e a lista de lives) em até {PrazoExclusao} e confirma. Não dá para apagar: o que os amigos têm nos logs deles sobre você, e o que você mandou em modo anônimo (não há como saber que foi você).

Para ver o que está no seu PC e vai ser enviado: Ajustes > Diagnóstico > ""Ver o que é enviado"".";
}
