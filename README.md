# Streemu

Transmita a tela do seu jogo para os amigos, **direto de PC para PC**, com o Discord continuando só como call de voz. O áudio do Discord nunca entra na transmissão.

Este repositório tem o código do app e do instalador para quem quer conferir o que está rodando no PC. O servidor do grupo **não** está aqui.

## O que é

- Conexão direta entre os PCs (P2P). Quando a rede não deixa, entra um relay de reserva.
- Encoder na placa de vídeo, de 720p30 a 2160p60.
- Áudio por processo: só o jogo, ou o PC todo menos o Discord.
- Sala do grupo (assistir sem código), várias lives ao mesmo tempo, stream privada e modo filme.

Windows 10 (2004 ou mais novo) ou Windows 11.

## Baixar

- Instalador: <https://github.com/Streemu/Streemu/releases/latest/download/Streemu-Instalador.exe>
- Site: <https://www.streemu.com.br>

## Conferir o instalador

O instalador é compilado pelo GitHub Actions **deste repositório** (`.github/workflows/instalador.yml`), a partir do código que está aqui, e cada um sai com um **atestado de origem** assinado pelo GitHub. Com o [GitHub CLI](https://cli.github.com) instalado, na pasta do download:

```powershell
gh attestation verify .\Streemu-Instalador.exe -R Streemu/Streemu
```

A resposta diz de qual commit e de qual workflow o arquivo saiu. Se o arquivo tiver sido trocado ou não tiver saído daqui, a conferência falha.

Sem o GitHub CLI, dá para conferir só o hash: cada release traz um `SHA256SUMS.txt`, e no PowerShell

```powershell
Get-FileHash .\Streemu-Instalador.exe -Algorithm SHA256
```

tem que dar o mesmo valor. O hash prova que o arquivo não mudou depois da release; o atestado prova também de onde ele veio.

### Antivírus

[![antivírus](https://img.shields.io/endpoint?url=https://raw.githubusercontent.com/Streemu/Streemu/status/badge.json)](https://github.com/Streemu/Streemu/releases/latest)

O selo acima mostra a **última versão disponível** do Streemu (o instalador ou a atualização automática, o que for mais novo) e o resultado da varredura dela. Cada arquivo é varrido pelo Microsoft Defender dentro do próprio workflow, **antes** de ser publicado: se ele acusar alguma coisa, o instalador ou a atualização não sai. Depois de publicado, é conferido no [VirusTotal](https://www.virustotal.com) (cerca de 70 antivírus) e no [MetaDefender](https://metadefender.com). Isso vale para o instalador (`Streemu-Instalador.exe`) e para cada atualização automática (`streemu-main.zip`, o que a maioria das pessoas recebe). O resultado fica nas notas da release e em <https://raw.githubusercontent.com/Streemu/Streemu/status/varredura.json>, e é reconferido todo dia, porque os antivírus revisam o veredito com o tempo. Como o app ainda não é assinado, às vezes 1 a 3 antivírus pouco conhecidos o marcam por engano (falso positivo); o resultado é mostrado do jeito que vier. Detalhes em [docs/varredura.md](docs/varredura.md).

### E as atualizações?

Depois de instalado, o Streemu se atualiza sozinho. As atualizações também são compiladas aqui (`.github/workflows/atualizacao.yml`), saem na pré-release `main-atual`, têm atestado e passam pela varredura de antivírus descrita acima. O app confere o SHA-256 do pacote antes de instalar. Para conferir o exe que já está no seu PC:

```powershell
gh attestation verify "$env:LOCALAPPDATA\Programs\Streemu\Streemu.exe" -R Streemu/Streemu
```

O código deste repositório é uma cópia do repositório de trabalho, sem o histórico, atualizada automaticamente a cada mudança no app. A release `instalador-2026-11-18` foi a primeira e foi compilada à mão, antes deste workflow existir: ela não tem atestado.

## O que o app envia e para onde

**Vídeo e áudio da live** vão direto do PC de quem transmite para o PC de quem assiste. Quando a conexão direta não funciona, passam pelo relay de reserva da Cloudflare (TURN), que só repassa pacotes.

**O servidor do grupo** é um Cloudflare Worker. Ele **não** está neste repositório. Ele cuida de:

- login e contas, e grupos por convite;
- a sala do grupo (quem está ao vivo e a troca de dados para conectar);
- as credenciais temporárias do relay de reserva;
- a atualização do app e o download do ffmpeg e do mpv fixados;
- o diagnóstico (abaixo).

**Diagnóstico.** O app manda sozinho, para o servidor do grupo, o log do app e dos problemas marcados, dados do PC (processador, placa de vídeo e driver, memória, Windows, tela, áudio, provedor e região da rede) e métricas das lives (quadros por segundo, atraso, qualidade da conexão). A pessoa pode desligar em Ajustes > Diagnóstico; desligado, o envio continua anônimo (nomes trocados por "pessoa-1", "pessoa-2"...) e a nota do "Marcar problema" não sobe.

**Instalador.** Se não der para instalar (Windows anterior ao 10 versão 2004, Windows de 32 bits, ou a instalação parou no meio), o instalador mostra um relatório com o motivo, a versão do instalador, o Windows (nome, build, se roda 64 bits, idioma) e, se parou no meio, o fim do log da instalação. Ele só vai para o servidor do grupo se a pessoa clicar em "Enviar para a equipe"; também dá para copiar ou salvar e mandar por conta própria. O nome do usuário do Windows sai trocado por `<usuario>`, e o servidor filtra de novo pasta do usuário, e-mail e IP. O que o instalador monta e envia está no `[Code]` de [`instalador/streemu.iss`](instalador/streemu.iss).

**O que nunca vai:** IP inteiro, senha, tokens, o nome do usuário do Windows, o nome do PC, o endereço MAC da placa de rede, e áudio ou imagem das lives. O texto passa por um filtro antes de sair: veja [`app/Streemu/Telemetria/Higiene.cs`](app/Streemu/Telemetria/Higiene.cs). Dentro do app, "Ver o que é enviado" abre a pasta com uma cópia do que vai.

Política de privacidade: <https://www.streemu.com.br/privacidade>

## Compilar

Pré-requisitos: Windows 10 (2004) ou mais novo e o [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build app/Streemu -c Release
```

O ffmpeg, o ffplay e o mpv **não** estão no repositório. O app usa as versões fixadas, com SHA-256, em `app/Streemu/ffmpeg-desejado.json`, `ffplay-desejado.json` e `mpv-desejado.json`, e confere cada arquivo antes de usar. Para rodar uma compilação local com transmissão, coloque o ffmpeg "full" do [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) em `ffmpeg/` ao lado do `Streemu.exe` (ou no PATH).

O instalador sai do `scripts/build-release.ps1`, que precisa do [Inno Setup 6](https://jrsoftware.org/isinfo.php) e de uma pasta com o ffmpeg e o ffplay (`-Ffmpeg <pasta>`). Os dois estão nas releases `ffmpeg-*` e `ffplay-*` deste repositório, nas versões que o app espera; o workflow do instalador mostra como baixar e conferir.

Os projetos em `tests/` são executáveis sem rede nem captura de tela, um por assunto.

## Licença

[GNU GPLv3](LICENSE). Componentes de terceiros e suas licenças: [TERCEIROS.md](TERCEIROS.md).

## Contribuições

**Issues são bem-vindas:** bug, ideia, dúvida ou o que achou estranho no código. Diga a versão do Streemu, o Windows e, se for bug, o que aconteceu e o que você esperava.

Pull requests ainda **não** entram: este repositório recebe uma cópia do código de trabalho, então uma mudança feita aqui seria apagada na próxima cópia. Se tiver uma correção, descreva numa issue.

Problema de segurança **não** vai em issue: veja o [SECURITY.md](SECURITY.md).
