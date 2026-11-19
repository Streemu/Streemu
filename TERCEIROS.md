# Componentes de terceiros

O código do Streemu é GPLv3 ([LICENSE](LICENSE)). O app e o instalador usam ou distribuem os componentes abaixo, cada um com a sua licença. Para os binários, o código-fonte fica com o projeto de origem.

## Distribuídos junto com o app

| Componente | Licença | Versão | Onde está o código |
|---|---|---|---|
| **ffmpeg** e **ffplay** (build "full" do gyan.dev) | GPLv3 (o build "full" liga bibliotecas GPL) | `2026-11-09-git-16a345b022-full_build-www.gyan.dev`, fixada em `app/Streemu/ffmpeg-desejado.json` (o ffplay, em `ffplay-desejado.json`) | <https://ffmpeg.org> (código) e <https://www.gyan.dev/ffmpeg/builds/> (builds e a lista de bibliotecas de cada build) |
| **mpv** (modo filme; baixado na primeira vez que alguém usa o filme, não vem no instalador) | GPLv2 ou mais nova (o mpv também pode ser compilado como LGPL; vale a licença do build exato) | `20261111-git-bcde6b93cc`, fixada em `app/Streemu/mpv-desejado.json`. Build do projeto shinchiro: `mpv.exe` e `d3dcompiler_43.dll` | <https://mpv.io> e <https://github.com/mpv-player/mpv> (código); <https://github.com/shinchiro/mpv-winbuild-cmake> (os builds para Windows) |
| **NAudio.Wasapi** (captura de áudio) | MIT | 3.3.0 | <https://github.com/naudio/NAudio> |
| **System.Management** (consulta do Windows) | MIT | 10.0.0 | <https://github.com/dotnet/runtime> |
| **Runtime do .NET** (o instalador usa o app autocontido, com o runtime dentro do `Streemu.exe`) | MIT | .NET 10 | <https://github.com/dotnet/runtime> |
| **Segoe UI** e **Segoe MDL2 Assets** | Fontes do Windows, **não** distribuídas por nós: o app só as usa, e o mpv desenha os ícones com a que já está no PC | | |

## Só no instalador

| Componente | Licença | Onde |
|---|---|---|
| **Inno Setup 6** (gera o `Streemu-Instalador.exe`) | Licença própria do Inno Setup (gratuita, inclusive para uso comercial) | <https://jrsoftware.org/isinfo.php> |

## Do projeto

- `app/Streemu/Filme/streemu-osc.lua`: a interface do filme dentro do mpv. É código nosso, escrito para o Streemu (GPLv3). O mpv carrega o script, mas o script não copia o `osc.lua` do mpv.
- Arte (a mascote Mumu e os gatos, em `app/Streemu/Arte/`) e sons (`app/Streemu/Sons/`): fazem parte do projeto e seguem a licença do repositório. Esta lista não afirma a ferramenta ou a origem de cada arquivo; se isso mudar, o `TERCEIROS.md` muda junto.

Achou um componente que falta aqui? Avise pelo caminho do [SECURITY.md](SECURITY.md) (ele também serve para problemas de licença).
