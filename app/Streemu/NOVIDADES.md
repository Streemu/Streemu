<!--
Este arquivo é o que a tela "Novidades" do app mostra. Ele é escrito para quem USA o Streemu: frases curtas, sem causa técnica e sem nome de pessoa.
PR comum só acrescenta itens em "## Não lançado". Quem lança a versão renomeia essa seção para "## X.Y.Z — AAAA-MM-DD".
Cada item é "- **Título:** detalhe." (título até 70 caracteres, detalhe até 220, resumo da versão até 120).
Atenção: mudar o título de um item faz ele reaparecer como novo (com selo) para todo mundo. Corrija só o detalhe, se der.
-->

# Novidades

## Não lançado

### Novo
- **Tela Novidades:** o que mudou em cada versão, na barra lateral. Depois de uma atualização ela ganha o selo "NOVO" até você dar uma olhada.

### Melhorias
- **Envio de arquivo mais rápido:** quando os dois PCs se conectam diretamente, o download tem prioridade na conexão, mesmo com uma live ou um jogo aberto.
- **Cinema mais leve para quem transmite um filme 4K:** com placa AMD, o filme que vai para quem não tem o arquivo é preparado na placa de vídeo, e o seu filme não engasga mais.
- **Som da live mais contínuo:** o som da sua live sai num ritmo constante, sem pausas curtas a cada 2 segundos. Quem assiste pelo celular deixa de ouvir estalos.

### Correções
- **Live de celular que gira:** quando quem transmite pelo celular vira o aparelho, a janela da live acompanha, e a imagem não fica mais espremida.
- **Texto atrás de uma live em pé:** o aviso "Nenhuma stream à vista" não aparece mais dos lados de uma live estreita.

## 0.5.1 — 2026-11-20
Correções de tela em janela pequena e no tema claro, e um instalador que explica o que deu errado.

### Novo
- **Selo da atividade no botão da barra de tarefas:** um ponto no canto do botão mostra o que você está fazendo: transmitindo, assistindo ou no Cinema.
- **Instalador explica por que não instalou:** se o PC não serve ou a instalação para no meio, ele mostra o motivo, o que fazer e um relatório para copiar e mandar para a equipe.

### Melhorias
- **Recuperação de vídeo ao assistir uma live:** se a placa de vídeo falha ao abrir a live, o Streemu tenta de novo por outro caminho e, se preciso, pela CPU. Nas placas RTX série 50, a imagem não sai mais borrada.
- **Ícone de atualizar corrigido:** a seta de "Procurar atualização" e do "Tentar de novo" agora sai do círculo, como deveria.

### Correções
- **Grupos não sai mais cortada em janela estreita:** o cartão "Criar um grupo" voltou a ter borda e cantos com o Windows em 150 %, e o "Mais" do cartão do grupo desce para a linha de baixo quando falta espaço.
- **Tema claro sem pedaços do escuro nas lives:** ao trocar de tema com lives abertas, a barra de controles de cada live agora já vem no tema novo. O nome na faixa da live voltou a ser legível no claro.
- **Lives em janela pequena ou com a interface ampliada:** a live não some mais nem trava o palco com um X vermelho, e a barra de controles e os textos não passam mais do vídeo.

## 0.5.0 — 2026-11-19
!obrigatoria
Conta obrigatória, conexão mais segura, Grupos redesenhado, tema claro e fila de episódios no Cinema.

### Novo
- **Conta para entrar:** na primeira vez o app abre o site para você entrar com Google ou e-mail e confirmar com um código de 6 dígitos. Em Configurações > Conta você vê quem entrou e pode sair.
- **Vários grupos:** crie grupos, entre por convite e saia quando quiser. Você fica em todos ao mesmo tempo e vê as pessoas numa lista só. Quem está em dois grupos aparece uma vez.
- **Modo claro:** o botão de lua e sol na barra lateral troca entre o tema escuro e um tema claro violeta, com transição suave.
- **Enviar arquivo para um amigo:** escolha um amigo online e os arquivos, pelo botão ou arrastando. Ele aceita ou recusa, e o arquivo chega inteiro e conferido, retomando sozinho se a conexão cair.
- **Fila de episódios no Cinema:** escolha vários arquivos e o próximo começa sozinho quando o atual acaba. O anfitrião pula, volta, reordena e remove itens pela fila.
- **Ícone que muda conforme o que você faz:** na bandeja, o gatinho tem um ícone para cada atividade: ocioso, assistindo, transmitindo e Cinema.
- **Amigos offline e apelidos que só você vê:** quem tem o Streemu fechado aparece na barra lateral em "OFFLINE", e o menu da foto deixa você dar um apelido que só aparece para você.
- **Atividade recente na barra lateral:** o ícone de pulso abre a lista de quem ficou ao vivo, abriu um filme ou entrou no grupo, com a hora de cada coisa.
- **Aceitar um convite de live privada:** o cartão do convite fica no topo do Início, e as outras lives não abrem junto.
- **Canal de atualização:** em Configurações > Sistema você escolhe Nightly (padrão) ou Stable.
- **Conta: vincular o Google e sair de todos os dispositivos:** em Configurações > Conta você também vincula o Google, sai de todos os PCs de uma vez e pode excluir a conta.

### Melhorias
- **Páginas de Configurações em cartões:** todas as páginas seguem o visual do Perfil, com textos e controles alinhados. Aparência ganhou o sinal de ao vivo e a opção de abrir a barra ao passar o mouse.
- **Página Grupos redesenhada:** sem grupo, abre com "Criar um grupo" e "Entrar com um convite" lado a lado. Com grupos, cada um vira um cartão com "Copiar convite" à vista.
- **Transmitir mostra todas as telas e janelas numa grade:** as opções ocupam quantas colunas couberem na janela, em vez de uma coluna só escondendo o resto.
- **Primeira abertura mais acolhedora:** a tela de entrada abre maior, com a Mumu ao lado do cartão, e quem ainda não tem grupo cai direto na página Grupos.
- **Logo e arte novos:** logo novo na barra lateral e na tela de entrada, e Mumu nova nos dois temas.
- **Perfil e Enviar arquivo com visual novo:** o Perfil ganhou cartões com o avatar grande e as opções do Discord. Em Enviar arquivo, o "Para quem" mostra foto e apelido, e a área de soltar ficou maior.
- **Aviso do diagnóstico mais tarde e menor:** em vez de uma janela na primeira abertura, uma faixa curta aparece depois que você entra num grupo, com "Entendi", "Desligar" e "Saiba mais".
- **Cinema mostra todo mundo na sessão:** o anfitrião vê todos, inclusive quem assiste pela transmissão sem baixar o filme.
- **Mais proteção contra o PC de um amigo infectado:** o app só aceita o formato certo de vídeo, confere o filme baixado antes de abrir e recusa códigos de amigo que apontam para fora da internet pública.
- **Só roda o ffmpeg, o ffplay e o mpv que vêm com o Streemu:** o app confere cada um antes de usar, para um programa trocado não rodar na live de todos.
- **Relay de reserva se protege do gasto:** se um grupo usar demais, o relay dele pausa até o fim do mês. A conexão direta continua normal.

### Correções
- **Conexão entre os PCs agora é cifrada:** os comandos entre os PCs iam abertos e podiam ser lidos ou forjados por quem estivesse no caminho. Agora vão protegidos. Quem não atualizar não conecta com quem atualizou.
- **A live não perde mais o áudio depois do Alt+Tab:** ao trocar de janela, o som da live podia sumir para quem assistia por vários minutos.
- **Envio de arquivo não fica mais lento enquanto você transmite:** a live cede a vez ao envio de forma justa, e o arquivo não fica preso numa velocidade baixíssima.
- **O mesmo amigo não aparece mais duas vezes:** quem reabria o app ou usava dois PCs podia aparecer repetido na barra e nas listas por alguns segundos.
- **Fotos dos amigos voltam a aparecer depois que o app reabre:** depois de uma atualização automática, os amigos já online não ficam mais só com as iniciais.
- **Enviar arquivo não fecha mais o app ao abrir o "Para quem":** o app fechava sozinho nessa lista. Também sumiu a bagunça no layout ao abrir a aba.
- **Minimizar não deixa mais a barra da live solta na área de trabalho:** a barra e o cabeçalho só aparecem com o vídeo à vista.
- **Campos de texto sem borda quebrada:** o apelido, o nome do grupo, o convite e o motivo do "Marcar problema" voltaram a ter a borda inteira, com cantos arredondados.
- **Tela de entrada em janela pequena:** o cartão não passa mais por cima do rodapé, e a frase sob a Mumu não sai mais cortada.
- **Nome do filme baixado fica dentro da pasta escolhida:** nomes estranhos de arquivo vindos do anfitrião são limpos antes de gravar.

## 0.4.0 — 2026-11-15
!obrigatoria
O app agora se chama Streemu em tudo. Todo o grupo precisa atualizar junto.

### Novo
- **O Streemu tem um nome só, em tudo:** programa, pasta dos ajustes e link do Discord mudaram. Desinstale o app antigo e instale esta versão. Quem ficar na versão antiga não conecta com quem atualizou.
- **Som quando alguém entra ou sai da live:** quem já assiste ouve um som curto e suave quando alguém entra e outro, mais discreto, quando sai.
- **Escala da interface com Ctrl +, Ctrl - e Ctrl 0:** aumenta, diminui e volta ao normal, de 80 % a 175 %, como no navegador.
- **Cabeçalho em cada live:** acima do vídeo aparecem a foto, o nome, o jogo e a pílula vermelha AO VIVO, inclusive em tela cheia.
- **Barra de controles com palavras:** a barra do player diz "Tela cheia" e "Sair da live" (em vermelho), em vez de só um ícone.

### Melhorias
- **Início e Transmitir como no desenho novo:** cartões das lives com selo AO VIVO e botão Assistir, painel Transmitir em cartões, e telas que se ajustam ao tamanho da janela, inclusive em monitor ultrawide.
- **Barra lateral renovada:** o Diagnóstico virou item de navegação, o logo ficou maior e a barra de título da janela ficou no roxo escuro do Streemu.
- **Barras de rolagem com a cara do Streemu:** um traço fino roxo no lugar da barra branca do Windows, sem piscar ao passar o mouse ou arrastar.
- **Painel "Sua transmissão" mais claro:** mostra AO VIVO, o jogo com a qualidade, quantos assistem e o botão "Parar transmissão" em vermelho.
- **Sincronia melhor entre som e imagem nas lives:** a captura e a tela de pausa seguem o mesmo relógio, e sob sobrecarga o som recente é preservado.

### Correções
- **Ctrl + e Ctrl - funcionam em qualquer lugar da janela:** antes um campo com o foco podia engolir as teclas.
- **Arrastar a barra de rolagem não deixa mais a página "fantasma":** a rolagem agora acompanha o mouse sem o branco do Windows.

## 0.3.0 — 2026-11-13
O app ganhou o nome Streemu, com barra lateral, Início com cartões das lives, Cinema e instalador novo.

### Novo
- **Nome, ícone e instalador novos:** o gato é a marca na janela, na bandeja e no exe, e o instalador traz a Mumu no assistente.
- **Barra lateral, Início com cartões e Transmitir ao lado:** o app tem uma nova interface roxa, com um item Transmitir na barra e Configurações com uma categoria por vez.
- **Transmissão privada:** em "Só convidados" a live fica visível só para quem você marcar, até 6 pessoas contando você. Quem não foi convidado não vê a live na lista, no Discord nem nos avisos.
- **Sino para silenciar notificações:** cala balões, sons e a faixa de atualização por 15 min, 1 h, 3 h, 8 h, 24 h ou até você ligar de novo.
- **Menu ao clicar na foto de um amigo:** convide para uma live privada direto ou silencie só os avisos dele.
- **Cinema para ver filmes juntos:** abra um arquivo e todos assistem sincronizados. Quem não tem o arquivo assiste pela transmissão, sem baixar, ou baixa do anfitrião. A interface fica dentro do player.
- **Avisos curtos quando um amigo começa uma live ou abre um filme:** o aviso se desliga em Ajustes > Assistir.
- **Encerrar a sua live sem sair da live do amigo:** a barra lateral ganhou "Encerrar live".
- **Procurar atualização na barra lateral:** quando há versão nova, o botão vira "Atualizar agora".

### Melhorias
- **O grave de golpes e explosões não estoura mais o fone:** o som do jogo é capturado sem cortes e um limitador segura os graves muito fortes.
- **A live começa mais rápido em PC sem placa AMD:** cerca de 2 segundos a menos.
- **Mais proteção da sua privacidade no diagnóstico:** o endereço do Wi-Fi e a senha da ligação não saem mais do PC.
- **O sinal de ao vivo virou uma bolinha vermelha translúcida:** você escolhe o tamanho e o lugar em Ajustes.
- **Lista de Transmitir acompanha o que você abre e fecha:** um jogo novo aparece sozinho na lista, e o jogo minimizado também aparece.
- **Atualização automática mais esperta:** ela espera quem está abrindo ou assistindo um filme, e o erro de atualização agora diz o motivo.

### Correções
- **A live não fica mais em 1 fps depois de um engasgo:** o app corrige a captura ruim sozinho e mais cedo, sem ninguém reconectar.
- **Transmitir o monitor com jogo em tela cheia não vira mais slideshow:** a captura agora vem direto da placa.
- **A live não pausa mais quando você olha o Streemu em outro monitor.**
- **A live de monitor ultrawide não aparece mais esticada** para quem assiste num monitor 16:9.
- **A live do amigo não fecha mais quando a sala do grupo pisca:** a lista de amigos também não pisca.
- **O sinal de ao vivo não some mais depois do Alt+Tab.**
- **Os avatares não somem mais quando a atualização reabre o app.**
- **Live de um app em janela não mostra mais a sua área de trabalho.**
- **O atalho da janela pequena volta a funcionar em PCs com NVIDIA.**
- **Live em PC só com Intel agora abre para quem assiste.**
- **Filme pela transmissão não fica mais parado num ponto antigo** depois de uma pausa ou pulo do anfitrião.

## 0.2.5 — 2026-11-08
Instalador, avatares, janela pequena do player e iniciar com o Windows.

### Novo
- **Instalador:** instala sem pedir administrador, cria atalhos, aparece em "Aplicativos" do Windows e pode ser desinstalado de lá.
- **Avatares:** escolha uma imagem do PC (JPEG, PNG, GIF ou BMP) como avatar. GIF de qualquer tamanho anima para os amigos.
- **Janela pequena do player:** reabre a live numa janelinha de canto, sempre por cima e sem moldura. O atalho Ctrl+Alt+M alterna entre ela e a janela normal sem sair do jogo.
- **Iniciar com o Windows:** o Streemu abre sozinho na bandeja quando você entra no Windows.
- **Roda do mouse e tecla M no player:** a roda muda o volume de 5 em 5 % e o M liga e desliga o mudo.
- **Liberar o Firewall com um clique:** o Windows pede permissão de administrador uma vez.

### Melhorias
- **Barra do player no estilo do Discord:** mudo, volume e qualidade no centro, pessoas à esquerda e tela cheia à direita.

### Correções
- **A faixa de participantes só mostra quem transmite e quem assiste**, e não mais todo mundo da sala.
- **A janela pequena não abre mais em tela cheia.**

## 0.2.4 — 2026-11-07
Controles na janela de quem assiste e botão para procurar atualização.

### Novo
- **Barra de controles na janela de quem assiste:** ao passar o mouse sobre o vídeo aparecem mudo, volume, qualidade, tela cheia e fechar.
- **Faixa de participantes no player:** círculos com a inicial de cada pessoa, com o selo LIVE em quem transmite.
- **Botão "Procurar atualização" sempre visível:** procura versão nova na hora e diz o resultado.

## 0.2.3 — 2026-11-07
Atualização automática: as versões novas passam a chegar sozinhas.

### Novo
- **Atualização automática:** quando há versão nova, o app mostra "Atualizar agora" no topo da janela e se atualiza sem você baixar nada.
- **Data do build no título da janela:** a janela mostra a versão e a data do build.

## 0.2.2 — 2026-11-07

### Correções
- **O player não abre mais sozinho por cima do jogo:** "Abrir sozinho quando o amigo começar a transmitir" agora vem desligado e o app lembra a sua escolha.

## 0.2.1 — 2026-11-07

### Novo
- **Participar pelo Discord, sem navegador (experimental):** amigos e quem está na mesma call entram direto na transmissão, sem o pedido que o Discord mandava por mensagem.

## 0.2.0 — 2026-11-06
!obrigatoria
A forma de conectar mudou: amigos se acham pela sala do grupo. Todos atualizam juntos.

### Novo
- **Assistir sem código:** o app entra numa sala do grupo e mostra quem está online e quem está ao vivo. Basta clicar para assistir.
- **Vários amigos na mesma live:** cada um assiste sem pesar no seu PC mais do que o necessário.
- **Status no Discord enquanto você transmite:** mostra o jogo, a qualidade, quantos assistem e um botão Assistir.
- **Bandeja do Windows:** fechar a janela deixa o app na bandeja. Pelo botão direito você começa a transmitir em dois cliques.
- **Janela Transmitir no estilo do Discord:** abas Aplicativos e Telas com miniaturas, resolução, FPS e áudio.

### Melhorias
- **Transmitir um aplicativo mostra só ele**, como no Discord.
- **Começar a transmitir ficou rápido:** o app lembra o que funcionou para a sua placa. Na AMD caiu de cerca de 10 s para menos de 1 s.
- **A qualidade acompanha a imagem que realmente sai:** não gasta mais banda à toa quando a tela é menor que o preset.

### Correções
- **Menos travamentos depois do Alt+Tab.**
- **O player acha o vídeo de 1440p em bitrate alto**, que antes ficava sem imagem.

## 0.1.12 — 2026-11-06

### Correções
- **O vídeo aparece para o amigo quando você transmite com placa AMD:** antes só o som tocava e o player não abria.

## 0.1.11 — 2026-11-06

### Melhorias
- **O player guarda o volume e o mudo entre transmissões:** isso só vale para o player e não mexe no volume do Windows.

## 0.1.10 — 2026-11-06

### Novo
- **Visual novo no tema escuro:** cartões arredondados, campos escuros e botões planos.

### Correções
- **A transmissão se recupera sozinha quando a captura trava**, e o amigo vê só cerca de 1 segundo parado.
- **O envio se refaz na hora quando o amigo fecha o player:** reabrir o player ficou bem mais rápido.

## 0.1.9 — 2026-11-06

### Melhorias
- **Jogo em tela cheia ou sem bordas capturado direto da placa:** a captura rende bem mais quadros por segundo.
- **Player em PC sem memória de vídeo sobrando:** se o jogo ocupa a memória, o player reabre sozinho usando a CPU.

## 0.1.8 — 2026-11-06

### Melhorias
- **A lista de fontes mostra primeiro o jogo em tela cheia:** ele já vem selecionado.

### Correções
- **O relatório não mostra mais tempo de conexão negativo** depois de uma reconexão.

## 0.1.7 — 2026-11-06

### Novo
- **Relay de reserva do grupo no ar:** quem não consegue receber conexão direta passa a conseguir assistir.

## 0.1.6 — 2026-11-06
!obrigatoria
O código para ligar o relay ficou maior. Todos precisam desta versão para usar o relay.

### Novo
- **Relay de reserva:** para quem não consegue receber conexão direta, o tráfego passa por um servidor de apoio.

### Melhorias
- **Mais tentativas com placa AMD** antes de usar a CPU.

## 0.1.5 — 2026-11-06

### Melhorias
- **Resoluções menores mostram a tela inteira reduzida**, em vez de cortar.
- **O player usa a placa de vídeo**, com volta automática para a CPU.
- **O app avisa quando a captura entrega poucos quadros por segundo.**

## 0.1.4 — 2026-11-06
!obrigatoria
O código de pareamento ficou mais comprido. Todos precisam atualizar.

### Melhorias
- **Conecta em mais casos entre operadoras:** o código leva mais informações de rede, o que resolveu uma dupla de amigos que não se conectava.

### Correções
- **Acentos corrigidos nas mensagens.**

## 0.1.3 — 2026-11-06

### Correções
- **A imagem não congela mais com a tela parada.**

## 0.1.2 — 2026-11-05

### Melhorias
- **Os avisos dizem quem está tentando conectar.**
- **O app tenta outras portas quando nada chega.**

## 0.1.1 — 2026-11-05

### Melhorias
- **O código e a porta ficam guardados** entre aberturas, e o app reconecta sozinho.
- **Avisos de código antigo e de conexão só de ida.**

## 0.1.0 — 2026-11-05

### Novo
- **Primeira versão:** transmita a tela do jogo para um amigo com a conexão direta entre os PCs, com som do jogo e escolha do que transmitir.
