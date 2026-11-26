<#
  Gera os pacotes de release em dist/:
    dist/Streemu-X.Y.Z/                    pasta pronta (exe + ffmpeg + diagnostico + LEIA-ME.txt)
    dist/Streemu-X.Y.Z.zip                 completo (para quem e novo)
    dist/Streemu-X.Y.Z-atualizacao.zip     so o exe + LEIA-ME + diagnostico (para quem ja tem a pasta ffmpeg)
    dist/Streemu-X.Y.Z-instalador.exe      instalador (atalhos, iniciar com o Windows, desinstalar, a Mumu no assistente);
                                           precisa do Inno Setup 6 (winget install JRSoftware.InnoSetup; 6.5.2+ para a arte)
                                           sem ele o script so avisa e pula
  Os pacotes podem ter qualquer prefixo (o GitHub Actions procura *-main-atualizacao.zip
  e o publica como streemu-main.zip, o nome que o Worker le).

  A versao vem do Streemu.csproj. O ffmpeg/ffplay vem de -Ffmpeg (pasta) ou do PATH.
  -Ffplay: pasta de onde vem SO o ffplay.exe, quando ele deve ser de outro build que o ffmpeg (padrao: a mesma do -Ffmpeg).
           Ex.: ffmpeg 2026-11-09 (dup_frames na captura de janela) com o ffplay 2025-12-14, porque o ffplay 2026-11-09 cai no
           renderizador Vulkan com o driver NVIDIA (docs/DESCOBERTAS.md, 2026-11-10).
  -Sufixo: pacote de teste de um branch sem mexer na versao nem na pasta da release oficial
           (ex.: -Sufixo teste-discord gera dist/Streemu-0.1.12-teste-discord.zip).
  -Canal: gravado no exe para a atualizacao automatica (spec 004): release (padrao) ou main (GitHub Actions).
  -SoAtualizacao: gera so o zip de atualizacao (sem ffmpeg, sem o zip completo); e o que o GitHub Actions usa.
  Uso: powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-release.ps1 [-Ffmpeg C:\Tools\bin] [-Ffplay <pasta>] [-Sufixo teste-x] [-Canal main] [-SoAtualizacao]
#>
param([string]$Ffmpeg = '', [string]$Ffplay = '', [string]$Sufixo = '', [string]$Canal = 'release', [switch]$SoAtualizacao)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root 'app\Streemu\Streemu.csproj'
$ver = ([regex]::Match([IO.File]::ReadAllText($proj), '<Version>([^<]+)</Version>')).Groups[1].Value
if (-not $ver) { throw "Nao achei <Version> em $proj" }
if ($Sufixo) { $ver = "$ver-$Sufixo" }

if (-not $SoAtualizacao) {
    if (-not $Ffmpeg) {
        $cmd = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
        if ($cmd) { $Ffmpeg = Split-Path $cmd.Source -Parent }
    }
    if (-not $Ffplay) { $Ffplay = $Ffmpeg }
    if (-not (Test-Path (Join-Path $Ffmpeg 'ffmpeg.exe'))) { throw "Nao achei ffmpeg.exe em '$Ffmpeg'. Passe -Ffmpeg <pasta do build full do gyan.dev>." }
    if (-not (Test-Path (Join-Path $Ffplay 'ffplay.exe'))) { throw "Nao achei ffplay.exe em '$Ffplay'. Passe -Ffplay <pasta do build full do gyan.dev>." }
}

# A janela de streams (spec 006) redimensiona o ffplay POR FORA, e isso depende do SDL/ffplay desta versao (sem -noborder a janela e redimensionavel,
# e o app tira a moldura por estilo). Esta e a versao testada em 2026-11-07; outra versao NAO e erro, mas refaca o teste de redimensionar antes de soltar.
$FfplayTestado = '2025-12-14-git-3332b2db84-full_build-www.gyan.dev'
# ffplay que derruba o player de quem assiste: o script para em vez de so avisar (docs/DESCOBERTAS.md, 2026-11-10).
$FfplayRecusados = @{
    '2026-11-09-git-16a345b022-full_build-www.gyan.dev' = 'o renderizador Vulkan dele cai em 6-8 s (0xC0000005 no nvoglv64.dll, driver NVIDIA 32.0.16.1742) e o player de quem assiste fecha'
}
$FfplayVersao = ''
if (-not $SoAtualizacao) {
    $linha = (& (Join-Path $Ffplay 'ffplay.exe') -version 2>&1 | Select-Object -First 1) -as [string]
    $FfplayVersao = ($linha -replace '^ffplay version ', '' -split ' ')[0]
    if ($FfplayRecusados.ContainsKey($FfplayVersao)) {
        throw "O ffplay '$FfplayVersao' em '$Ffplay' nao pode ir para os amigos: $($FfplayRecusados[$FfplayVersao]). Passe -Ffplay <pasta com o ffplay $FfplayTestado> (o ffmpeg pode continuar o novo em -Ffmpeg)."
    }
    # O app so roda o ffmpeg e o ffplay que batem com o SHA-256 dos pins embutidos (ffmpeg-desejado.json e ffplay-desejado.json, Util/Binarios.cs): um exe diferente
    # no pacote seria renomeado para .invalido na primeira abertura e baixado de novo (80 MB). Por isso o script PARA, em vez de so avisar.
    function Ler-PinBinario([string]$arq) { [IO.File]::ReadAllText((Join-Path $root "app\Streemu\$arq"), [Text.Encoding]::UTF8) | ConvertFrom-Json }
    function Conferir-Pin([string]$exe, $pin, [string]$que, [string]$como) {
        if (-not $pin.sha256) { throw "app/Streemu/$que-desejado.json sem sha256: o app nao usa binario sem pin." }
        $sha = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($sha -ne $pin.sha256) {
            throw "O $que em '$exe' (sha256 $sha) nao e o que o app quer: $($pin.versao), sha256 $($pin.sha256) (app/Streemu/$que-desejado.json). $como"
        }
    }
    $pinFfmpeg = Ler-PinBinario 'ffmpeg-desejado.json'
    $pinFfplay = Ler-PinBinario 'ffplay-desejado.json'
    Conferir-Pin (Join-Path $Ffmpeg 'ffmpeg.exe') $pinFfmpeg 'ffmpeg' 'Passe -Ffmpeg <pasta com o ffmpeg do pin> ou troque o pin com scripts/publicar-ffmpeg.ps1.'
    Conferir-Pin (Join-Path $Ffplay 'ffplay.exe') $pinFfplay 'ffplay' "Passe -Ffplay <pasta com o ffplay $FfplayTestado> ou troque o pin com scripts/publicar-ffplay.ps1 (e refaca o teste de redimensionar da janela de streams, docs/specs/006-multistream.md, secao 11)."
    if ($pinFfplay.versao -ne $FfplayTestado) {
        Write-Warning "O ffplay do pin ('$($pinFfplay.versao)') nao e o da versao testada ('$FfplayTestado'). O redimensionamento por fora da janela de streams depende dela: refaca o teste (docs/specs/006-multistream.md, secao 11) e atualize `$FfplayTestado aqui."
    }
    # o mpv nao vai no pacote: o modo filme o baixa na primeira vez (MpvInstalador), conferindo cada arquivo contra mpv-desejado.json
}

$dist = Join-Path $root 'dist'
$out = Join-Path $dist "Streemu-$ver"
Write-Host "Gerando Streemu $ver em $out"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    -p:StreemuCanal=$Canal -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou" }

New-Item -ItemType Directory -Force (Join-Path $out 'diagnostico') | Out-Null
if (-not $SoAtualizacao) {
    New-Item -ItemType Directory -Force (Join-Path $out 'ffmpeg') | Out-Null
    Copy-Item (Join-Path $Ffmpeg 'ffmpeg.exe'), (Join-Path $Ffplay 'ffplay.exe') (Join-Path $out 'ffmpeg')
    [IO.File]::WriteAllText((Join-Path $out 'ffmpeg\VERSAO-FFPLAY.txt'), "$FfplayVersao`r`n", (New-Object Text.UTF8Encoding $false))
}
Copy-Item (Join-Path $root 'diagnostico\diag-entrada.ps1') (Join-Path $out 'diagnostico')

# O pacote NUNCA leva o grupo.json (spec 022): o app ja traz o endereco do servidor embutido e nao usa senha. O grupo.json da raiz
# (fora do git) pode apontar para um servidor de teste ou ainda ter a senha antiga; empacota-lo desviaria o app de todo mundo.

# LEIA-ME.txt = guia do usuario (UTF-8 com BOM para o Bloco de Notas)
$utf8 = [Text.Encoding]::UTF8
$leia = [IO.File]::ReadAllText((Join-Path $root 'docs\LEIA-ME-USUARIO.md'), $utf8)
$texto = $leia.TrimEnd() + "`r`n"
[IO.File]::WriteAllText((Join-Path $out 'LEIA-ME.txt'), $texto, (New-Object Text.UTF8Encoding($true)))

$full = Join-Path $dist "Streemu-$ver.zip"
$upd = Join-Path $dist "Streemu-$ver-atualizacao.zip"
Remove-Item $full, $upd -ErrorAction SilentlyContinue
Push-Location $dist
try {
    if (-not $SoAtualizacao) { tar.exe -a -c -f $full "Streemu-$ver" }
    $tmp = Join-Path $dist "_upd"
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    $updDir = Join-Path $tmp "Streemu-$ver"
    New-Item -ItemType Directory -Force (Join-Path $updDir 'diagnostico') | Out-Null
    Copy-Item (Join-Path $out 'Streemu.exe'), (Join-Path $out 'LEIA-ME.txt') $updDir
    Copy-Item (Join-Path $out 'diagnostico\diag-entrada.ps1') (Join-Path $updDir 'diagnostico')
    Push-Location $tmp
    tar.exe -a -c -f $upd "Streemu-$ver"
    Pop-Location
    Remove-Item $tmp -Recurse -Force
}
finally { Pop-Location }

# instalador (instalador/streemu.iss): leva a mesma pasta do zip completo (sem grupo.json)
$inst = Join-Path $dist "Streemu-$ver-instalador.exe"
if (-not $SoAtualizacao) {
    $iscc = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { $c = Get-Command ISCC.exe -ErrorAction SilentlyContinue; if ($c) { $iscc = $c.Source } }
    if ($iscc) {
        Remove-Item $inst -ErrorAction SilentlyContinue
        # pagina de privacidade do instalador (spec 018, D7): o proprio exe grava o texto do app, para nao existir uma segunda copia
        $priv = Join-Path $dist "privacidade-$ver.txt"
        $p = Start-Process -FilePath (Join-Path $out 'Streemu.exe') -ArgumentList @('--exportar-privacidade', "`"$priv`"") -Wait -PassThru -WindowStyle Hidden
        if ($p.ExitCode -ne 0 -or -not (Test-Path $priv)) { throw "Streemu.exe --exportar-privacidade saiu com $($p.ExitCode) e nao gravou $priv" }
        & $iscc /Q "/DVersao=$ver" "/DOrigem=$out" "/DSaida=$dist" "/DPrivacidade=$priv" (Join-Path $root 'instalador\streemu.iss')
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup falhou ao gerar o instalador" }
    }
    else { Write-Host 'Inno Setup 6 nao encontrado: pulei o instalador. Instale com: winget install JRSoftware.InnoSetup' -ForegroundColor Yellow }
}

Get-Item $full, $upd, $inst -ErrorAction SilentlyContinue | ForEach-Object { "{0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB) }
