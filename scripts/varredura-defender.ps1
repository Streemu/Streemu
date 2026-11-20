<#
  Varredura do Microsoft Defender do runner (Windows) sobre um ou mais arquivos. Usado pelos workflows instalador.yml e atualizacao.yml,
  ANTES do atestado e da publicacao: se o Defender acusar ameaca em qualquer arquivo, este script falha e nada e publicado.

  Grava em -Saida (defender.json) o bloco "defender" que o varredura.ps1 junta ao resultado:
    { "status": "limpo" | "ameaca" | "indisponivel", "versao_assinaturas": "...", "analisado_em": "...", "arquivos": [ { "arquivo": "...", "status": "..." } ] }
  status geral: "ameaca" se QUALQUER arquivo acusar; "limpo" se TODOS estiverem limpos; senao "indisponivel"
  (sem MpCmdRun.exe, ou ele terminou com um codigo que nao e 0 nem 2: vira aviso, nunca derruba a publicacao).
  Se existir $GITHUB_OUTPUT, acrescenta nele "json=<o mesmo bloco, numa linha>" (saida do passo, para o job de varredura na nuvem).

  -Arquivos   um ou mais arquivos (separe por virgula)
  -Saida      onde gravar o json (padrao defender.json)
  -SemAtualizarAssinaturas   nao roda o MpCmdRun -SignatureUpdate (para teste no seu PC)

  Windows PowerShell 5.1 ou pwsh 7. Arquivo salvo em UTF-8 com BOM (por causa dos acentos no 5.1).
  Uso: pwsh scripts/varredura-defender.ps1 -Arquivos pub/streemu-main.zip,pub/Streemu.exe -Saida defender.json
#>
param([Parameter(Mandatory)][string[]]$Arquivos, [string]$Saida = 'defender.json', [switch]$SemAtualizarAssinaturas)

$ErrorActionPreference = 'Stop'

function Avisar([string]$msg) {
    if ($env:GITHUB_ACTIONS) { Write-Host "::warning::$msg" } else { Write-Warning $msg }
}

function Agora-Iso { [DateTime]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture) }

# codigo do MpCmdRun -> status de um arquivo (0 = limpo, 2 = ameaca, o resto = indisponivel). Sem rede nem Defender; testavel.
function ConvertFrom-CodigoDefender([int]$cod) {
    if ($cod -eq 0) { return 'limpo' }
    if ($cod -eq 2) { return 'ameaca' }
    'indisponivel'
}

# status geral a partir dos status de cada arquivo
function Resumir-Defender([string[]]$status) {
    if (@($status | Where-Object { $_ -eq 'ameaca' }).Count -gt 0) { return 'ameaca' }
    if (@($status).Count -gt 0 -and @($status | Where-Object { $_ -ne 'limpo' }).Count -eq 0) { return 'limpo' }
    'indisponivel'
}

# "dot-source" (. ./varredura-defender.ps1) so carrega as funcoes, para teste
if ($MyInvocation.InvocationName -eq '.') { return }

# aceita tambem "a,b" numa string so (quando chamado por -File)
$lista = @($Arquivos | ForEach-Object { $_ -split ',' } | Where-Object { $_.Trim() } | ForEach-Object { $_.Trim() })
if ($lista.Count -eq 0) { throw 'Passe -Arquivos.' }
$caminhos = @()
foreach ($a in $lista) {
    if (-not (Test-Path -LiteralPath $a)) { throw "Nao achei o arquivo '$a'." }
    $caminhos += (Resolve-Path -LiteralPath $a).Path
}

$def = [ordered]@{ status = 'indisponivel'; versao_assinaturas = ''; analisado_em = ''; arquivos = @() }
$mp = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
if (Test-Path $mp) {
    if (-not $SemAtualizarAssinaturas) {
        & $mp -SignatureUpdate
        if ($LASTEXITCODE -ne 0) { Avisar "nao consegui atualizar as assinaturas do Defender (codigo $LASTEXITCODE); uso as que o runner tem" }
    }
    try { $def.versao_assinaturas = [string](Get-MpComputerStatus).AntivirusSignatureVersion } catch { Avisar 'nao consegui ler a versao das assinaturas do Defender' }
    $porArquivo = @()
    foreach ($c in $caminhos) {
        & $mp -Scan -ScanType 3 -File $c -DisableRemediation
        $cod = $LASTEXITCODE
        $st = ConvertFrom-CodigoDefender $cod
        if ($st -eq 'indisponivel') { Avisar "o MpCmdRun terminou com codigo $cod em $(Split-Path $c -Leaf); segui sem o resultado do Defender para ele" }
        $porArquivo += [ordered]@{ arquivo = (Split-Path $c -Leaf); status = $st }
    }
    $def.analisado_em = Agora-Iso
    $def.arquivos = @($porArquivo)
    $def.status = Resumir-Defender @($porArquivo | ForEach-Object { $_.status })
}
else { Avisar 'MpCmdRun.exe nao encontrado; segui sem o Defender' }

$json = $def | ConvertTo-Json -Depth 5 -Compress
$Saida = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Saida)
[void](New-Item -ItemType Directory -Force (Split-Path $Saida -Parent))
[IO.File]::WriteAllText($Saida, $json + "`n", (New-Object Text.UTF8Encoding($false)))
Write-Host $json
if ($env:GITHUB_OUTPUT) { "json=$json" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8 }

if ($def.status -eq 'ameaca') {
    $ruins = @($def.arquivos | Where-Object { $_.status -eq 'ameaca' } | ForEach-Object { $_.arquivo }) -join ', '
    throw "O Microsoft Defender acusou uma ameaca em: $ruins. Nada foi publicado."
}
$global:LASTEXITCODE = 0
