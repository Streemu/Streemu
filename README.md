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

Cada release traz um `SHA256SUMS.txt` com o hash do instalador. No PowerShell, na pasta do download:

```powershell
Get-FileHash .\Streemu-Instalador.exe -Algorithm SHA256
```

O resultado tem que ser igual ao do `SHA256SUMS.txt` da mesma release.

Sendo honesto sobre o que isso prova: hoje o instalador é compilado no PC de um dos mantenedores, a partir do repositório privado de trabalho, e este repositório recebe uma cópia do código sem o histórico. O hash garante que o arquivo não mudou depois da release, **não** que ele saiu exatamente deste código. Compilar o instalador pelo GitHub Actions, com atestado de origem (`gh attestation verify`), está planejado.

## O que o app envia e para onde

**Vídeo e áudio da live** vão direto do PC de quem transmite para o PC de quem assiste. Quando a conexão direta não funciona, passam pelo relay de reserva da Cloudflare (TURN), que só repassa pacotes.

**O servidor do grupo** é um Cloudflare Worker. Ele **não** está neste repositório. Ele cuida de:

- login e contas, e grupos por convite;
- a sala do grupo (quem está ao vivo e a troca de dados para conectar);
- as credenciais temporárias do relay de reserva;
- a atualização do app e o download do ffmpeg e do mpv fixados;
- o diagnóstico (abaixo).

**Diagnóstico.** O app manda sozinho, para o servidor do grupo, o log do app e dos problemas marcados, dados do PC (processador, placa de vídeo e driver, memória, Windows, tela, áudio, provedor e região da rede) e métricas das lives (quadros por segundo, atraso, qualidade da conexão). A pessoa pode desligar em Ajustes > Diagnóstico; desligado, o envio continua anônimo (nomes trocados por "pessoa-1", "pessoa-2"...) e a nota do "Marcar problema" não sobe.

**O que nunca vai:** IP inteiro, senha, tokens, o nome do usuário do Windows, o nome do PC, o endereço MAC da placa de rede, e áudio ou imagem das lives. O texto passa por um filtro antes de sair: veja [`app/Streemu/Telemetria/Higiene.cs`](app/Streemu/Telemetria/Higiene.cs). Dentro do app, "Ver o que é enviado" abre a pasta com uma cópia do que vai.

Política de privacidade: <https://www.streemu.com.br/privacidade>

## Compilar

Pré-requisitos: Windows 10 (2004) ou mais novo e o [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build app/Streemu -c Release
```

O ffmpeg, o ffplay e o mpv **não** estão no repositório. O app usa as versões fixadas, com SHA-256, em `app/Streemu/ffmpeg-desejado.json`, `ffplay-desejado.json` e `mpv-desejado.json`, e confere cada arquivo antes de usar. Para rodar uma compilação local com transmissão, coloque o ffmpeg "full" do [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) em `ffmpeg/` ao lado do `Streemu.exe` (ou no PATH).

O instalador sai do `scripts/build-release.ps1`, que precisa do [Inno Setup 6](https://jrsoftware.org/isinfo.php) e de uma pasta com o ffmpeg e o ffplay (`-Ffmpeg <pasta>`).

Os projetos em `tests/` são executáveis sem rede nem captura de tela, um por assunto.

## Licença

[GNU GPLv3](LICENSE). Componentes de terceiros e suas licenças: [TERCEIROS.md](TERCEIROS.md).

## Contribuições

**Issues são bem-vindas:** bug, ideia, dúvida ou o que achou estranho no código. Diga a versão do Streemu, o Windows e, se for bug, o que aconteceu e o que você esperava.

Pull requests ainda **não** entram: este repositório recebe uma cópia do código de trabalho, então uma mudança feita aqui seria apagada na próxima cópia. Se tiver uma correção, descreva numa issue.

Problema de segurança **não** vai em issue: veja o [SECURITY.md](SECURITY.md).
