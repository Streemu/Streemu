# Varredura antivírus das releases

Dois arquivos são varridos, e cada um passa por três etapas:

- **instalador** (`Streemu-Instalador.exe`, a última release estável, workflow `.github/workflows/instalador.yml`);
- **atualização** (`streemu-main.zip`, a pré-release `main-atual`, workflow `.github/workflows/atualizacao.yml`). É o que a maioria das pessoas recebe, pela atualização automática do app.

As etapas:

1. **Microsoft Defender**, no runner, dentro do job que compila (`scripts/varredura-defender.ps1`), antes do atestado e da publicação. Na atualização ele varre o `streemu-main.zip` e o `Streemu.exe`. Se acusar ameaça em qualquer um, o job falha e nada sai (a `main-atual` anterior continua valendo).
2. **VirusTotal** e **MetaDefender Cloud**, no job `varredura` do mesmo workflow, depois da publicação. Detecção aqui **não** derruba nada (falso positivo é comum); só é registrada.
3. **Reconferência diária** (`.github/workflows/varredura.yml`, 09:17 UTC): consulta de novo o hash dos dois alvos, porque os antivírus revisam o veredito com o tempo. O arquivo só é baixado e enviado se um serviço não conhecer o hash.

O resultado vai para: as notas da release (bloco entre `<!-- varredura:inicio -->` e `<!-- varredura:fim -->`, na release do instalador e na `main-atual`), os badges do README e o `varredura.json` do branch `status` (branch órfão, só com `varredura.json`, `badge.json` e `badge-instalador.json`).

## Configurar as chaves

Contas grátis em <https://www.virustotal.com> e <https://metadefender.com>; a chave de API fica no perfil de cada uma.

No GitHub: **Settings > Secrets and variables > Actions > New repository secret**:

- `VT_API_KEY`: chave do VirusTotal (plano grátis: 4 consultas por minuto, o script já espera entre as chamadas);
- `METADEFENDER_API_KEY`: chave do MetaDefender Cloud.

Sem a chave de um serviço, ele aparece como `"sem-chave"` e a release segue normalmente.

## Formato do varredura.json

URL: `https://raw.githubusercontent.com/Streemu/Streemu/status/varredura.json` (o raw.githubusercontent.com manda CORS liberado, então o site pode fazer `fetch` direto; o cache é de uns 5 minutos).

```json
{
  "atualizado_em": "2026-11-20T09:17:00Z",
  "ultima": "atualizacao",
  "atualizacao": {
    "versao": "0.5.1",
    "tag": "main-atual",
    "arquivo": "streemu-main.zip",
    "sha256": "...",
    "commit": "...",
    "publicado_em": "2026-11-20T08:00:00Z",
    "defender": { "status": "limpo", "versao_assinaturas": "1.451.2.0", "analisado_em": "...", "arquivos": [ { "arquivo": "streemu-main.zip", "status": "limpo" } ] },
    "virustotal": {
      "status": "ok",
      "deteccoes": 0,
      "total": 72,
      "motores_que_acusaram": [ { "motor": "NomeDoMotor", "resultado": "Rotulo.Da.Ameaca" } ],
      "link": "<página do arquivo no VirusTotal>",
      "analisado_em": "2026-11-20T09:15:00Z"
    },
    "metadefender": { "...": "mesmo formato do virustotal" }
  },
  "instalador": {
    "versao": "0.5.0",
    "tag": "v0.5.0",
    "arquivo": "Streemu-Instalador.exe",
    "sha256": "...",
    "publicado_em": "...",
    "defender": { "...": "..." },
    "virustotal": { "...": "..." },
    "metadefender": { "...": "..." }
  }
}
```

- **O site deve mostrar a seção apontada por `ultima`** (`"atualizacao"` ou `"instalador"`): ela é a última versão disponível do app, a de versão numérica mais nova; empate = `atualizacao`. Use `varredura[varredura.ultima]`.
- Cada seção (`atualizacao`, `instalador`) pode faltar se nunca foi varrida. `commit` só existe na `atualizacao`; `publicado_em` pode vir `null`.
- `defender` só aparece quando o resultado do runner existe. `status`: `limpo`, `ameaca` (a release nem sai) ou `indisponivel`; `arquivos` diz o status de cada arquivo varrido. O Defender só roda na hora de compilar: a reconferência diária repete o bloco dele enquanto o `sha256` for o mesmo.
- `virustotal.status` e `metadefender.status`: `ok`, `em-analise`, `sem-chave` ou `erro` (neste caso há um campo `mensagem`). `deteccoes` e `total` só vêm preenchidos com `ok`.
- `total` conta só os motores que deram veredito (no VirusTotal ficam de fora tipo não suportado, timeout e falha).
- `badge.json` (mesmo branch) é o endpoint do shields.io usado no README: a **última versão disponível**, com a versão no texto (ex.: `0.5.1 · VirusTotal 0/70 · MetaDefender 0/21`). `badge-instalador.json` é o mesmo só para o instalador (só existe depois da primeira varredura de um instalador).

## Rodar a reconferência à mão

**Actions > Varredura > Run workflow**, escolhendo o alvo (`ambos`, `instalador` ou `atualizacao`) e, para o instalador, a tag (ex.: `v0.4.1`) ou vazio para a última release. Pela linha de comando: `gh workflow run varredura.yml -f alvo=instalador -f tag=v0.4.1`.

O branch `status` mostra sempre a última versão: rodar com a tag de um instalador antigo atualiza só as notas dessa release, sem mexer nos badges nem no `varredura.json`.

Para testar os scripts no seu PC, sem publicar nada (funciona no Windows PowerShell 5.1 e no pwsh 7):

```powershell
$env:VT_API_KEY = Read-Host "Chave do VirusTotal"; $env:METADEFENDER_API_KEY = Read-Host "Chave do MetaDefender"
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/varredura.ps1 -Tipo instalador -Sha256 <hash> -Tag v0.4.1 -Versao 0.4.1 -Saida $env:TEMP\varredura -Esperar
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/varredura-defender.ps1 -Arquivos Streemu-Instalador.exe -Saida $env:TEMP\defender.json -SemAtualizarAssinaturas
```

(Passe `-Arquivo <arquivo>` em vez de `-Sha256` para enviar o arquivo se os serviços não o conhecerem; para a atualização, use `-Tipo atualizacao`. O script grava `secao.json` e `notas-varredura.md` em `-Saida`; quem junta a seção ao `varredura.json` é o `scripts/publicar-varredura.ps1`.)
