<#
  Streemu - diagnostico de ENTRADA (versao 1.0)

  Responde UMA pergunta: os pacotes do amigo chegam na placa de rede deste PC?
    - se NAO chegam: o bloqueio esta fora do PC (roteador/modem ou operadora);
    - se chegam e o Windows descarta: firewall/antivirus (o script mostra o motivo);
    - se chegam e nao sao descartados: problema no app.

  Como usar:
    1. Abra o Streemu e deixe a ligacao com o amigo aberta (assista a live dele ou deixe ele assistir a sua; o app do amigo tambem aberto).
    2. Rode este script. Ele pede permissao de administrador (o pktmon do Windows precisa disso).
    3. Durante a captura (45 s), o amigo NAO fecha o app.
    4. Mande o arquivo diag-entrada-*.json que aparece na pasta do script.

  O script so le informacoes e captura cabecalhos de pacotes UDP da porta do Streemu.
  Nao instala nada e nao muda configuracoes (os filtros do pktmon sao removidos no fim).
  IPs publicos saem mascarados no resultado.

  Uso: powershell -NoProfile -ExecutionPolicy Bypass -File .\diag-entrada.ps1
#>
param(
    [int]$Segundos = 45,
    [switch]$SemCaptura
)

$ErrorActionPreference = 'Continue'
$pasta = $PSScriptRoot
if (-not $pasta) { $pasta = (Get-Location).Path }

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin -and -not $SemCaptura) {
    Write-Host 'Pedindo permissao de administrador (necessaria para o pktmon)...' -ForegroundColor Yellow
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit', '-File', "`"$PSCommandPath`"", '-Segundos', $Segundos)
    Start-Process powershell -Verb RunAs -ArgumentList $argList
    exit
}

function Mascarar([string]$ip) {
    if ($ip -match '^(\d+)\.(\d+)\.\d+\.\d+$') {
        $a = [int]$Matches[1]; $b = [int]$Matches[2]
        if ($a -eq 10 -or ($a -eq 192 -and $b -eq 168) -or ($a -eq 172 -and $b -ge 16 -and $b -le 31) -or $a -eq 127) { return $ip }
        return "$($Matches[1]).$($Matches[2]).x.x"
    }
    return $ip
}

# O app nao tem mais "codigo do amigo" nem porta fixa (spec 022): cada ligacao da sala usa uma porta UDP propria.
# Nao ha mais amigo esperado salvo no estado; so a captura da porta de uma ligacao aberta.
$amigo = $null

# ---------------------------------------------------------------- app rodando e porta real

$proc = Get-Process Streemu -ErrorAction SilentlyContinue | Select-Object -First 1
$portaApp = $null
if ($proc) {
    $eps = @(Get-NetUDPEndpoint -OwningProcess $proc.Id -ErrorAction SilentlyContinue | Where-Object { $_.LocalAddress -eq '0.0.0.0' })
    if ($eps.Count -gt 0) { $portaApp = $eps[0].LocalPort }
}

Write-Host ''
Write-Host "Streemu aberto: $([bool]$proc)   porta local do app: $portaApp" -ForegroundColor Cyan
if ($amigo) { Write-Host "amigo esperado: $(Mascarar $amigo.ip):$($amigo.porta)" -ForegroundColor Cyan }
if (-not $proc) { Write-Host 'ATENCAO: abra o Streemu e deixe a ligacao com o amigo aberta (assistindo a live dele, ou ele a sua) antes de rodar este teste.' -ForegroundColor Yellow }

# ---------------------------------------------------------------- rede e protecao

$adaptadores = @(Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object Status -eq 'Up' | ForEach-Object {
    $ips = @(Get-NetIPAddress -InterfaceIndex $_.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | ForEach-Object { Mascarar $_.IPAddress })
    [ordered]@{ nome = $_.Name; descricao = $_.InterfaceDescription; tipo = "$($_.PhysicalMediaType)"; ipv4 = $ips }
})
$rota = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Sort-Object RouteMetric | Select-Object -First 1
$saltos = @(tracert -d -h 4 -w 800 1.1.1.1 2>$null | ForEach-Object { if ($_ -match '^\s*(\d+)\s+.*?(\d+\.\d+\.\d+\.\d+)\s*$') { "$($Matches[1]): $(Mascarar $Matches[2])" } })
$antivirus = @(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction SilentlyContinue | ForEach-Object {
    [ordered]@{ nome = $_.displayName; estado = ('0x{0:X6}' -f [int]$_.productState) }
})
$firewallTerceiros = @(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName FirewallProduct -ErrorAction SilentlyContinue | ForEach-Object { $_.displayName })
$servicosSeg = @(Get-Service -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(AVP|kavfs|klif|klnagent|KSDE|ekrn|avast|AvastSvc|aswbIDSAgent|McAfee|mfefire|NortonSecurity|BDAgent|bdredline|WRSA|Sophos)' -or $_.DisplayName -match 'Kaspersky|ESET|Avast|AVG|McAfee|Norton|Bitdefender|Sophos' } |
    ForEach-Object { "$($_.DisplayName) ($($_.Status))" })
$fwPerfis = @(Get-NetFirewallProfile -ErrorAction SilentlyContinue | ForEach-Object { "$($_.Name)=$($_.Enabled)" })
$fwRegras = @(Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue | Where-Object { $_.Program -match 'Streemu' } | ForEach-Object {
    $r = $_ | Get-NetFirewallRule
    "$($r.Direction) $($r.Action) ativo=$($r.Enabled) perfil=$($r.Profile)"
})
$filtrosWfp = $null

# ---------------------------------------------------------------- captura

$captura = [ordered]@{ feita = $false }
if (-not $SemCaptura -and $portaApp) {
    $etl = Join-Path $env:TEMP 'streemu-entrada.etl'
    $txt = Join-Path $env:TEMP 'streemu-entrada.txt'
    Remove-Item $etl, $txt -ErrorAction SilentlyContinue
    pktmon stop 2>&1 | Out-Null
    pktmon filter remove 2>&1 | Out-Null
    pktmon filter add Streemu -t UDP -p $portaApp 2>&1 | Out-Null
    $saidaStart = (pktmon start --capture --pkt-size 128 --file-name $etl 2>&1) -join ' '
    Write-Host ''
    Write-Host "Capturando pacotes UDP da porta $portaApp por $Segundos s. Peca para o amigo deixar o app aberto e conectando..." -ForegroundColor Green
    for ($s = $Segundos; $s -gt 0; $s -= 5) { Write-Host "  ... $s s"; Start-Sleep -Seconds ([Math]::Min(5, $s)) }
    $contadores = (pktmon counters --type drop --drop-reason 2>&1) -join "`n"
    pktmon stop 2>&1 | Out-Null
    pktmon filter remove 2>&1 | Out-Null
    pktmon etl2txt $etl --out $txt 2>&1 | Out-Null

    $linhas = @()
    if (Test-Path $txt) { $linhas = Get-Content $txt }
    $entrada = @{}; $saida = @{}; $amostras = New-Object System.Collections.Generic.List[string]
    $quedas = New-Object System.Collections.Generic.List[string]
    $ultimoCab = ''
    foreach ($l in $linhas) {
        if ($l -match 'PktNumber') { $ultimoCab = $l; continue }
        if ($l -match 'Drop' -or $l -match 'drop') {
            $q = ($l -replace '\d+\.\d+\.\d+\.\d+', { Mascarar $_.Value }).Trim()
            if ($quedas.Count -lt 40 -and -not $quedas.Contains($q)) { $quedas.Add($q) }
        }
        if ($l -match '(\d{1,3}(?:\.\d{1,3}){3})\.(\d+) > (\d{1,3}(?:\.\d{1,3}){3})\.(\d+): UDP') {
            $src = $Matches[1]; $sport = [int]$Matches[2]; $dst = $Matches[3]; $dport = [int]$Matches[4]
            if ($dport -eq $portaApp) { $k = "$(Mascarar $src):$sport"; $entrada[$k] = 1 + [int]$entrada[$k] }
            elseif ($sport -eq $portaApp) { $k = "$(Mascarar $dst):$dport"; $saida[$k] = 1 + [int]$saida[$k] }
            if ($amostras.Count -lt 30) {
                $dir = if ($ultimoCab -match 'Direction (\w+)') { $Matches[1] } else { '?' }
                $comp = if ($ultimoCab -match 'Component (\d+)') { $Matches[1] } else { '?' }
                $amostras.Add("dir=$dir comp=$comp $(Mascarar $src):$sport > $(Mascarar $dst):$dport")
            }
        }
    }
    $captura = [ordered]@{
        feita            = $true
        porta            = $portaApp
        segundos         = $Segundos
        inicio_pktmon    = $saidaStart
        linhas_no_log    = $linhas.Count
        entrada_por_origem = $entrada
        saida_por_destino  = $saida
        quedas           = $quedas
        contadores_queda = ($contadores -replace '\d+\.\d+\.\d+\.\d+', { Mascarar $_.Value })
        amostras         = $amostras
    }
}

# ---------------------------------------------------------------- veredito

$veredito = 'sem captura'
if ($captura.feita) {
    $chegouAmigo = $false; $mandouAmigo = $false
    if ($amigo) {
        $pref = Mascarar $amigo.ip
        foreach ($k in $captura.entrada_por_origem.Keys) { if ($k.StartsWith($pref)) { $chegouAmigo = $true } }
        foreach ($k in $captura.saida_por_destino.Keys) { if ($k.StartsWith($pref)) { $mandouAmigo = $true } }
    } else {
        $chegouAmigo = $captura.entrada_por_origem.Count -gt 0
        $mandouAmigo = $captura.saida_por_destino.Count -gt 0
    }
    if (-not $mandouAmigo) { $veredito = 'Este PC nao enviou nada ao amigo durante a captura: o app estava conectando? (confira se estava aberto e com o codigo do amigo)' }
    elseif (-not $chegouAmigo) { $veredito = 'Os pacotes do amigo NAO chegaram na placa de rede deste PC. O bloqueio esta FORA do PC: roteador/modem (NAT duplo, protecao contra flood, SIP ALG) ou a operadora.' }
    elseif ($captura.quedas.Count -gt 0) { $veredito = 'Os pacotes do amigo CHEGAM na placa, mas o Windows registra descartes: provavel firewall/antivirus. Veja "quedas".' }
    else { $veredito = 'Os pacotes do amigo CHEGAM na placa e nao ha descarte registrado: o problema provavelmente e no app. Mande tambem o log do app.' }
}

$resultado = [ordered]@{
    ferramenta       = 'streemu-diag-entrada'
    versao           = '1.0'
    data             = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
    admin            = $admin
    app_aberto       = [bool]$proc
    porta_app        = $portaApp
    amigo_esperado   = $(if ($amigo) { "$(Mascarar $amigo.ip):$($amigo.porta)" } else { $null })
    veredito         = $veredito
    captura          = $captura
    adaptadores      = $adaptadores
    gateway          = $(if ($rota) { $rota.NextHop } else { $null })
    primeiros_saltos = $saltos
    antivirus        = $antivirus
    firewall_terceiros = $firewallTerceiros
    servicos_seguranca = $servicosSeg
    firewall_windows = $fwPerfis
    regras_streemustream = $fwRegras
}
$arquivo = Join-Path $pasta ("diag-entrada-{0}.json" -f (Get-Date).ToString('yyyyMMdd-HHmm'))
$resultado | ConvertTo-Json -Depth 8 | Out-File -FilePath $arquivo -Encoding utf8

Write-Host ''
Write-Host '================ RESULTADO ================' -ForegroundColor Green
Write-Host $veredito
if ($captura.feita) {
    Write-Host "Chegaram (por origem): $(($captura.entrada_por_origem.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', ')"
    Write-Host "Saiu (por destino):    $(($captura.saida_por_destino.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', ')"
}
Write-Host ''
Write-Host "Arquivo: $arquivo" -ForegroundColor Green
Write-Host 'Mande esse arquivo .json para analise.'
