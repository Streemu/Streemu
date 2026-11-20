<#
  Publica o resultado da varredura (pasta feita pelo varredura.ps1, com secao.json e notas-varredura.md):
    1. troca (ou acrescenta) o bloco entre <!-- varredura:inicio --> e <!-- varredura:fim --> nas notas da release -Tag;
    2. no branch orfao "status", le o varredura.json que ja existe, troca so a secao do -Tipo, recalcula "ultima" e "atualizado_em" e grava:
         varredura.json         { atualizado_em, ultima, atualizacao, instalador }  (o site le este; mostra a secao apontada por "ultima")
         badge.json             badge do README: a ultima versao disponivel (a secao de "ultima")
         badge-instalador.json  badge so do instalador (so existe se ja houve varredura do instalador)
         badge-atualizacao.json badge so da atualizacao automatica (so existe se ja houve varredura dela); o README mostra este e o do instalador
  "ultima" e a secao de versao numerica mais nova entre as duas; empate = atualizacao.
  -Tipo instalador (padrao): so mexe no branch se -Tag for a ultima release estavel. -Tipo atualizacao: -Tag e main-atual e o branch sempre e atualizado.
  Usado pelos workflows instalador.yml, atualizacao.yml e varredura.yml. Precisa de GH_TOKEN (contents: write) e do gh e do git.
  Falha ao publicar vira aviso: nunca derruba a release.

  Compativel com pwsh 7 e com o Windows PowerShell 5.1. Arquivo salvo em UTF-8 com BOM.
  Uso: pwsh scripts/publicar-varredura.ps1 -Pasta varredura-out -Tipo instalador -Tag v0.4.1
#>
param([string]$Pasta = 'varredura-out', [ValidateSet('instalador', 'atualizacao')][string]$Tipo = 'instalador', [string]$Tag = '', [string]$Repo = '', [string]$Branch = 'status')

$ErrorActionPreference = 'Stop'
$MarcaIni = '<!-- varredura:inicio -->'
$MarcaFim = '<!-- varredura:fim -->'

function Avisar([string]$msg) {
    if ($env:GITHUB_ACTIONS) { Write-Host "::warning::$msg" } else { Write-Warning $msg }
}

# Troca o bloco (marcadores inclusos) por $bloco; sem marcadores, acrescenta no fim. Nao duplica.
function Set-BlocoVarredura([string]$notas, [string]$bloco) {
    $bloco = $bloco.Trim()
    $i = $notas.IndexOf($MarcaIni, [StringComparison]::Ordinal)
    if ($i -ge 0) {
        $f = $notas.IndexOf($MarcaFim, $i, [StringComparison]::Ordinal)
        if ($f -ge 0) {
            return $notas.Substring(0, $i) + $bloco + $notas.Substring($f + $MarcaFim.Length)
        }
    }
    if (-not $notas.Trim()) { return $bloco + "`n" }
    $notas.TrimEnd() + "`n`n" + $bloco + "`n"
}

# Le um JSON. O pwsh 7 transforma textos em formato de data em DateTime; aqui eles voltam a ser o texto ISO (UTC, com Z), como no 5.1.
function Normalizar-Datas($o) {
    if ($o -is [DateTime]) { return $o.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture) }
    if ($o -is [string] -or $null -eq $o -or $o.GetType().IsPrimitive) { return $o }
    if ($o -is [System.Collections.IEnumerable]) { return ,@($o | ForEach-Object { Normalizar-Datas $_ }) }
    if ($o -is [pscustomobject]) { foreach ($p in $o.PSObject.Properties) { $p.Value = Normalizar-Datas $p.Value } }
    $o
}
function ConvertFrom-JsonTexto([string]$texto) { Normalizar-Datas ($texto | ConvertFrom-Json) }

function Agora-Iso { [DateTime]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture) }

# ---------- a mescla (funcoes puras, sem rede; testaveis) ----------

# Compara versoes numericas ("0.10.0" > "0.9.9"): -1, 0 ou 1. Aceita "v" na frente e sufixo ("0.5.1-beta" vale 0.5.1); parte que nao for numero vale 0.
function Comparar-Versao([string]$a, [string]$b) {
    $pa = @(([string]$a).TrimStart('vV') -split '\.' | ForEach-Object { if ($_ -match '^(\d+)') { [long]$Matches[1] } else { [long]0 } })
    $pb = @(([string]$b).TrimStart('vV') -split '\.' | ForEach-Object { if ($_ -match '^(\d+)') { [long]$Matches[1] } else { [long]0 } })
    $n = [Math]::Max($pa.Count, $pb.Count)
    for ($i = 0; $i -lt $n; $i++) {
        $x = if ($i -lt $pa.Count) { $pa[$i] } else { [long]0 }
        $y = if ($i -lt $pb.Count) { $pb[$i] } else { [long]0 }
        if ($x -lt $y) { return -1 }
        if ($x -gt $y) { return 1 }
    }
    0
}

# Qual secao e a ultima versao disponivel: a de versao mais nova; empate (ou sem versao) = atualizacao. $null se nao houver nenhuma.
function Escolher-Ultima($atualizacao, $instalador) {
    if ($atualizacao -and $instalador) {
        if ((Comparar-Versao ([string]$instalador.versao) ([string]$atualizacao.versao)) -gt 0) { return 'instalador' }
        return 'atualizacao'
    }
    if ($atualizacao) { return 'atualizacao' }
    if ($instalador) { return 'instalador' }
    $null
}

# Junta a secao nova ($secao, com o campo "tipo") ao varredura.json que ja existia ($existente: objeto do ConvertFrom-Json ou $null).
# Formato antigo (sem "atualizacao" nem "instalador") e ignorado. Devolve o varredura.json novo (ordenado: atualizado_em, ultima, atualizacao, instalador).
function Mesclar-Varredura($existente, $secao, [string]$agora) {
    $tipo = [string]$secao.tipo
    if ($tipo -ne 'instalador' -and $tipo -ne 'atualizacao') { throw "secao com tipo invalido: '$tipo'" }
    $sec = [ordered]@{}
    foreach ($p in $secao.PSObject.Properties) { if ($p.Name -ne 'tipo') { $sec[$p.Name] = $p.Value } }
    $sec = [pscustomobject]$sec
    $atu = $null; $ins = $null
    if ($existente) {
        if ($existente.PSObject.Properties['atualizacao'] -and $existente.atualizacao) { $atu = $existente.atualizacao }
        if ($existente.PSObject.Properties['instalador'] -and $existente.instalador) { $ins = $existente.instalador }
    }
    if ($tipo -eq 'atualizacao') { $atu = $sec } else { $ins = $sec }
    $r = [ordered]@{ atualizado_em = $agora; ultima = (Escolher-Ultima $atu $ins) }
    if ($atu) { $r['atualizacao'] = $atu }
    if ($ins) { $r['instalador'] = $ins }
    $r
}

# Badge do shields.io de uma secao: "<versao> · VirusTotal 0/70 · MetaDefender 0/21".
# Verde se ninguem acusou; amarelo de 1 a 3 acusacoes somadas; vermelho acima; cinza se nenhum servico fechou.
function Novo-BadgeSecao($secao) {
    $partes = @(); $soma = 0
    $vt = $secao.virustotal; $md = $secao.metadefender
    foreach ($par in @(@('VirusTotal', $vt), @('MetaDefender', $md))) {
        if ($par[1] -and $par[1].status -eq 'ok') { $partes += "$($par[0]) $($par[1].deteccoes)/$($par[1].total)"; $soma += [int]$par[1].deteccoes }
    }
    if ($partes.Count -eq 0) {
        $aberto = @($vt, $md) | Where-Object { $_ -and $_.status -eq 'em-analise' }
        $msg = if ($aberto) { 'em análise' } else { 'sem resultado' }
        $cor = 'lightgrey'
    }
    else {
        $msg = $partes -join ' · '
        $cor = if ($soma -eq 0) { 'brightgreen' } elseif ($soma -le 3) { 'yellow' } else { 'red' }
    }
    if ($secao.versao) { $msg = "$($secao.versao) · $msg" }
    [ordered]@{ schemaVersion = 1; label = 'antivírus'; message = $msg; color = $cor }
}

# Os arquivos do branch status a partir do varredura.json ja mesclado: nome -> texto.
function Montar-ArquivosStatus($varredura) {
    $a = [ordered]@{}
    $a['varredura.json'] = (($varredura | ConvertTo-Json -Depth 8) + "`n")
    if ($varredura['ultima']) { $a['badge.json'] = ((Novo-BadgeSecao $varredura[[string]$varredura['ultima']]) | ConvertTo-Json) + "`n" }
    if ($varredura['instalador']) { $a['badge-instalador.json'] = ((Novo-BadgeSecao $varredura['instalador']) | ConvertTo-Json) + "`n" }
    if ($varredura['atualizacao']) { $a['badge-atualizacao.json'] = ((Novo-BadgeSecao $varredura['atualizacao']) | ConvertTo-Json) + "`n" }
    $a
}

# ---------- rede e git ----------

function Rodar-Git { & git @args; if ($LASTEXITCODE -ne 0) { throw "git $($args[0]) falhou (codigo $LASTEXITCODE)" } }

function Atualizar-Notas {
    $bloco = [IO.File]::ReadAllText((Join-Path $Pasta 'notas-varredura.md'), [Text.Encoding]::UTF8)
    $atual = (& gh release view $Tag -R $Repo --json body -q .body) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "nao consegui ler as notas da release $Tag" }
    $novo = Set-BlocoVarredura $atual $bloco
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("notas-" + [guid]::NewGuid() + '.md')
    try {
        [IO.File]::WriteAllText($tmp, $novo, (New-Object Text.UTF8Encoding($false)))
        & gh release edit $Tag -R $Repo --notes-file $tmp
        if ($LASTEXITCODE -ne 0) { throw "nao consegui editar as notas da release $Tag" }
    }
    finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
}

function Publicar-Branch {
    if (-not $env:GH_TOKEN) { throw 'GH_TOKEN ausente: nao da para publicar no branch status.' }
    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("x-access-token:$($env:GH_TOKEN)"))
    if ($env:GITHUB_ACTIONS) { Write-Host "::add-mask::$b64" }
    $cab = "http.extraheader=AUTHORIZATION: basic $b64"
    $secao = ConvertFrom-JsonTexto ([IO.File]::ReadAllText((Join-Path $Pasta 'secao.json'), [Text.Encoding]::UTF8))
    $dir = Join-Path ([IO.Path]::GetTempPath()) ('status-' + [guid]::NewGuid())
    New-Item -ItemType Directory $dir | Out-Null
    Push-Location $dir
    try {
        Rodar-Git init -q
        Rodar-Git config user.name 'github-actions[bot]'
        Rodar-Git config user.email '41898282+github-actions[bot]@users.noreply.github.com'
        Rodar-Git remote add origin "https://github.com/$Repo.git"
        for ($t = 1; $t -le 3; $t++) {
            $existe = (& git -c $cab ls-remote --heads origin $Branch)
            if ($LASTEXITCODE -ne 0) { throw 'git ls-remote falhou' }
            if ($existe) {
                Rodar-Git -c $cab fetch -q --depth 1 origin $Branch
                Rodar-Git checkout -q -B $Branch FETCH_HEAD
            }
            else { Rodar-Git checkout -q --orphan $Branch }
            # a cada tentativa le o varredura.json de novo: outro job pode ter gravado a outra secao no meio
            $existente = $null
            if (Test-Path (Join-Path $dir 'varredura.json')) {
                try { $existente = ConvertFrom-JsonTexto ([IO.File]::ReadAllText((Join-Path $dir 'varredura.json'), [Text.Encoding]::UTF8)) }
                catch { Avisar "varredura.json do branch $Branch ilegivel, comeco do zero: $($_.Exception.Message)" }
            }
            $novo = Mesclar-Varredura $existente $secao (Agora-Iso)
            Get-ChildItem -Force | Where-Object { $_.Name -ne '.git' } | Remove-Item -Recurse -Force
            $arquivos = Montar-ArquivosStatus $novo
            foreach ($n in $arquivos.Keys) { [IO.File]::WriteAllText((Join-Path $dir $n), $arquivos[$n], (New-Object Text.UTF8Encoding($false))) }
            Rodar-Git add -A
            & git diff --cached --quiet
            if ($LASTEXITCODE -eq 0) { Write-Host 'branch status ja esta igual'; return }
            Rodar-Git commit -q -m "Varredura de antivirus ($Tipo) $Tag"
            & git -c $cab push -q origin "HEAD:refs/heads/$Branch"
            if ($LASTEXITCODE -eq 0) { Write-Host "branch $Branch atualizado"; return }
            Write-Host "push recusado (tentativa $t); buscando de novo"
        }
        throw "nao consegui publicar no branch $Branch"
    }
    finally { Pop-Location; Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
}

# ---------- principal ----------

# "dot-source" so carrega as funcoes, para teste
if ($MyInvocation.InvocationName -eq '.') { return }

if (-not $Tag -and $Tipo -eq 'atualizacao') { $Tag = 'main-atual' }
if (-not $Tag) { throw 'Passe -Tag.' }
if ($Tipo -eq 'atualizacao' -and $Tag -ne 'main-atual') { throw "A atualizacao e sempre a release main-atual (veio -Tag $Tag)." }
if (-not $Repo) { $Repo = if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { 'Streemu/Streemu' } }
foreach ($n in 'secao.json', 'notas-varredura.md') {
    if (-not (Test-Path (Join-Path $Pasta $n))) { throw "Falta $n em $Pasta" }
}
$sec = ConvertFrom-JsonTexto ([IO.File]::ReadAllText((Join-Path $Pasta 'secao.json'), [Text.Encoding]::UTF8))
if ([string]$sec.tipo -ne $Tipo) { throw "secao.json e do tipo '$($sec.tipo)', mas -Tipo e '$Tipo'." }

# a main-atual e apagada e recriada a cada entrega: se o pacote dela mudou durante a varredura, este resultado e de outro arquivo
if ($Tipo -eq 'atualizacao') {
    $atual = ''
    try {
        $vj = (& gh release download $Tag -R $Repo -p versao.json -O - 2>$null) -join "`n"
        if ($LASTEXITCODE -eq 0 -and $vj) { $atual = [string](ConvertFrom-JsonTexto $vj).sha256 }
    }
    catch { }
    if ($atual -and $atual -ne [string]$sec.sha256) {
        Avisar "A main-atual mudou durante a varredura (SHA-256 $atual; varrido $($sec.sha256)); nada foi publicado. A proxima entrega tem a sua varredura."
        $global:LASTEXITCODE = 0
        return
    }
}

try { Atualizar-Notas } catch { Avisar "Notas da release nao atualizadas: $($_.Exception.Message)" }
if ($Tipo -eq 'instalador') {
    # o branch status mostra a ultima release estavel: reconferir uma tag antiga so mexe nas notas dela
    $ultima = (& gh release view -R $Repo --json tagName -q .tagName) -join ''
    if ($LASTEXITCODE -eq 0 -and $ultima -and $ultima -ne $Tag) {
        Write-Host "A ultima release e $ultima, nao ${Tag}: branch $Branch nao foi mexido."
        $global:LASTEXITCODE = 0
        return
    }
}
try { Publicar-Branch } catch { Avisar "Branch $Branch nao atualizado: $($_.Exception.Message)" }
$global:LASTEXITCODE = 0
