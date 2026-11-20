<#
  Varredura antivirus de um arquivo do Streemu (o instalador ou o pacote da atualizacao automatica): consulta (e, se preciso, envia o
  arquivo para) o VirusTotal e o MetaDefender Cloud e gera, em -Saida:
    secao.json            a secao desse tipo ("instalador" ou "atualizacao", campo "tipo"); o publicar-varredura.ps1 junta no varredura.json
                          que o site www.streemu.com.br le, e faz o badge do README
    notas-varredura.md    bloco para as notas da release, entre <!-- varredura:inicio --> e <!-- varredura:fim -->
  Se existir defender.json em -Saida (feito pelo varredura-defender.ps1 no runner), ele entra em "defender".

  Chaves por variavel de ambiente: VT_API_KEY e METADEFENDER_API_KEY. Sem a chave, o servico fica "sem-chave" (aviso, nunca erro).
  Erro de rede/HTTP num servico: ele fica "erro" com a mensagem e o script segue. Este script nao falha por causa de deteccao.

  -Tipo        instalador (padrao: Streemu-Instalador.exe da ultima release) ou atualizacao (streemu-main.zip da pre-release main-atual)
  -Arquivo     arquivo a conferir; so e enviado se o servico nao conhecer o hash.
  -Sha256      hash (se nao houver arquivo; sem arquivo o servico que nao conhece o hash fica "erro" com mensagem "hash-desconhecido: ...").
  -Tag         tag da release (padrao: main-atual na atualizacao)
  -Versao      versao do app
  -Commit      commit (so entra na secao se passado; a atualizacao tem no versao.json)
  -PublicadoEm quando a release saiu (ISO 8601; vazio = null)
  -Esperar     espera as analises fecharem (ate ~15 min por servico); sem ele, analise aberta fica "em-analise".

  Compativel com pwsh 7 e com o Windows PowerShell 5.1. Arquivo salvo em UTF-8 com BOM (por causa dos acentos no 5.1).
  Uso: pwsh scripts/varredura.ps1 -Tipo instalador -Arquivo pub/Streemu-Instalador.exe -Tag v0.4.1 -Versao 0.4.1 -Saida varredura-out -Esperar
#>
param([ValidateSet('instalador', 'atualizacao')][string]$Tipo = 'instalador', [string]$Arquivo = '', [string]$Sha256 = '', [string]$Tag = '', [string]$Versao = '',
    [string]$Commit = '', [string]$PublicadoEm = '', [string]$Saida = 'varredura-out', [switch]$Esperar)

$ErrorActionPreference = 'Stop'
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }

# pagina do arquivo no VirusTotal, montada em pedacos: a trava c do espelho publico (scripts/publicar-fonte.ps1)
# confunde um trecho dela com um padrao da lista de nomes privados
$VtPagina = 'https://www.virustotal.com/' + 'g' + 'ui/file/'

# ---------- utilitarios ----------

function Avisar([string]$msg) {
    if ($env:GITHUB_ACTIONS) { Write-Host "::warning::$msg" } else { Write-Warning $msg }
}

function Dormir([int]$s) { Start-Sleep -Seconds $s }

function Agora-Iso { [DateTime]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture) }

function Unix-ParaIso($seg) {
    if (-not $seg) { return $null }
    [DateTimeOffset]::FromUnixTimeSeconds([long]$seg).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture)
}

function Escrever-Utf8([string]$caminho, [string]$texto) {
    [IO.File]::WriteAllText($caminho, $texto, (New-Object Text.UTF8Encoding($false)))
}

# Le um JSON. O pwsh 7 transforma textos em formato de data em DateTime; aqui eles voltam a ser o texto ISO (UTC, com Z), como no 5.1.
function Normalizar-Datas($o) {
    if ($o -is [DateTime]) { return $o.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture) }
    if ($o -is [string] -or $null -eq $o -or $o.GetType().IsPrimitive) { return $o }
    if ($o -is [System.Collections.IEnumerable]) { return ,@($o | ForEach-Object { Normalizar-Datas $_ }) }
    if ($o -is [pscustomobject]) { foreach ($p in $o.PSObject.Properties) { $p.Value = Normalizar-Datas $p.Value } }
    $o
}
function Ler-Json([string]$caminho) { Normalizar-Datas ([IO.File]::ReadAllText($caminho, [Text.Encoding]::UTF8) | ConvertFrom-Json) }

function Novo-Resultado([string]$status, [string]$link) {
    [ordered]@{
        status = $status; deteccoes = $null; total = $null
        motores_que_acusaram = @(); link = $link; analisado_em = $null
    }
}

function Resultado-Com-Mensagem($r, [string]$msg) { $r['mensagem'] = $msg; $r }

# ---------- chamadas HTTP (so aqui se fala com a rede) ----------

# Devolve o objeto JSON; $null se 404; lanca nos outros erros. 429 (limite do plano gratis) espera 60 s e tenta de novo, ate 3 vezes.
function Invoke-Api {
    param([string]$Metodo = 'GET', [Parameter(Mandatory)][string]$Url, [hashtable]$Headers = @{}, [string]$ContentType = '', [string]$InFile = '')
    for ($t = 1; $t -le 3; $t++) {
        try {
            $p = @{ Method = $Metodo; Uri = $Url; Headers = $Headers; TimeoutSec = 600 }
            if ($ContentType) { $p['ContentType'] = $ContentType }
            if ($InFile) { $p['InFile'] = $InFile }
            return Invoke-RestMethod @p
        }
        catch {
            $cod = 0
            if ($_.Exception.Response) { try { $cod = [int]$_.Exception.Response.StatusCode } catch { } }
            if ($cod -eq 404) { return $null }
            if ($cod -eq 429 -and $t -lt 3) { Dormir 60; continue }
            $det = ''
            if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $det = ' ' + $_.ErrorDetails.Message.Substring(0, [Math]::Min(200, $_.ErrorDetails.Message.Length)) }
            throw "HTTP $cod em $Metodo $($Url -replace '\?.*$','')$det ($($_.Exception.Message))"
        }
    }
}

# multipart (campo "file") com HttpClient: funciona igual no 5.1 e no pwsh 7.
# Os cabecalhos vao montados a mao: o .NET poe o boundary entre aspas e acrescenta filename*=utf-8'', e o servidor de arquivos
# grandes do VirusTotal (upload_url, acima de 32 MB) recusa isso com "Malformed multipart body" (visto em 2026-11-20).
function Enviar-Multipart([string]$Url, [hashtable]$Headers, [string]$Arq) {
    Add-Type -AssemblyName System.Net.Http
    $cli = New-Object System.Net.Http.HttpClient
    $cli.Timeout = [TimeSpan]::FromMinutes(20)
    foreach ($k in $Headers.Keys) { [void]$cli.DefaultRequestHeaders.TryAddWithoutValidation($k, [string]$Headers[$k]) }
    $limite = [guid]::NewGuid().ToString('N')
    $mp = New-Object System.Net.Http.MultipartFormDataContent($limite)
    [void]$mp.Headers.Remove('Content-Type')
    [void]$mp.Headers.TryAddWithoutValidation('Content-Type', "multipart/form-data; boundary=$limite")
    $fs = [IO.File]::OpenRead($Arq)
    try {
        $parte = New-Object System.Net.Http.StreamContent($fs)
        [void]$parte.Headers.TryAddWithoutValidation('Content-Disposition', "form-data; name=`"file`"; filename=`"$([IO.Path]::GetFileName($Arq))`"")
        [void]$parte.Headers.TryAddWithoutValidation('Content-Type', 'application/octet-stream')
        $mp.Add($parte)
        $resp = $cli.PostAsync($Url, $mp).GetAwaiter().GetResult()
        $txt = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        # so o host no erro: o upload_url e um endereco assinado de uso unico
        if (-not $resp.IsSuccessStatusCode) { throw "HTTP $([int]$resp.StatusCode) em POST $(([Uri]$Url).GetLeftPart([UriPartial]::Authority)) $($txt.Substring(0, [Math]::Min(200, $txt.Length)))" }
        return ($txt | ConvertFrom-Json)
    }
    finally { $fs.Dispose(); $mp.Dispose(); $cli.Dispose() }
}

# ---------- interpretar respostas (sem rede; testavel com JSON falso) ----------

# VirusTotal: $obj = resposta de GET /files/{sha256}.
# total = malicious + suspicious + undetected + harmless, ou seja, so os motores que deram um veredito.
# Ficam FORA do total: type-unsupported (motor nao se aplica a este tipo de arquivo), timeout, confirmed-timeout e failure (motor nao respondeu).
# deteccoes = malicious + suspicious. Sem nenhum motor com veredito ainda = "em-analise".
function ConvertFrom-VtArquivo($obj, [string]$sha) {
    $r = Novo-Resultado 'em-analise' ($VtPagina + $sha)
    $attr = $obj.data.attributes
    $st = $attr.last_analysis_stats
    if (-not $st) { return $r }
    $mal = [int]$st.malicious; $sus = [int]$st.suspicious
    $total = $mal + $sus + [int]$st.undetected + [int]$st.harmless
    if ($total -eq 0) { return $r }
    $motores = @()
    if ($attr.last_analysis_results) {
        foreach ($p in $attr.last_analysis_results.PSObject.Properties) {
            $v = $p.Value
            if ($v.category -eq 'malicious' -or $v.category -eq 'suspicious') {
                $nome = if ($v.engine_name) { [string]$v.engine_name } else { [string]$p.Name }
                $motores += [ordered]@{ motor = $nome; resultado = [string]$v.result }
            }
        }
    }
    $r['status'] = 'ok'
    $r['deteccoes'] = $mal + $sus
    $r['total'] = $total
    $r['motores_que_acusaram'] = @($motores | Sort-Object { $_.motor })
    $r['analisado_em'] = Unix-ParaIso $attr.last_analysis_date
    $r
}

# MetaDefender: $obj = resposta de GET /hash/{sha256} ou /file/{data_id}. Devolve $null se nao tiver scan_results (hash desconhecido).
# scan_result_i que contam como detectado: 1 (infectado), 2 (suspeito), 4 (limpo/removido apos achar ameaca), 6 (quarentena), 8 (pulado por ser sujo).
# Todo o resto e "sem ameaca" (0) ou "nao escaneado/falhou" (3 falhou, 5 desconhecido, 7 pulado limpo, 9 a 18 limites de arquivo compactado,
# criptografado, nao suportado, etc.) e nao entra na lista de quem acusou.
# deteccoes e total vem do proprio servico (total_detected_avs / total_avs).
function ConvertFrom-MdArquivo($obj, [string]$sha) {
    $sr = $obj.scan_results
    if (-not $sr) { return $null }
    # Hash que o MetaDefender nunca varreu NAO vem 404: vem 200 com o veredito de reputacao de um motor so (Avira), sem data_id
    # nem progress_percentage. Ate um hash inventado volta "Infected 1/1". Isso nao e resultado de varredura: trata como desconhecido.
    if (-not $obj.data_id -and $null -eq $sr.progress_percentage) { return $null }
    $r = Novo-Resultado 'em-analise' "https://metadefender.com/results/hash/$sha"
    if ($null -eq $sr.progress_percentage -or [int]$sr.progress_percentage -lt 100) { return $r }
    $detectam = @(1, 2, 4, 6, 8)
    $motores = @()
    if ($sr.scan_details) {
        foreach ($p in $sr.scan_details.PSObject.Properties) {
            $v = $p.Value
            if ($detectam -contains [int]$v.scan_result_i) {
                $motores += [ordered]@{ motor = [string]$p.Name; resultado = [string]$v.threat_found }
            }
        }
    }
    $det = if ($null -ne $sr.total_detected_avs) { [int]$sr.total_detected_avs } else { $motores.Count }
    $r['status'] = 'ok'
    $r['deteccoes'] = $det
    $r['total'] = [int]$sr.total_avs
    $r['motores_que_acusaram'] = @($motores | Sort-Object { $_.motor })
    $r['analisado_em'] = if ($sr.start_time) { ([DateTimeOffset]::Parse([string]$sr.start_time, [Globalization.CultureInfo]::InvariantCulture)).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture) } else { Agora-Iso }
    $r
}

# ---------- os servicos ----------

function Consultar-VirusTotal([string]$Sha, [string]$Arq, [bool]$Aguardar) {
    $link = ($VtPagina + $Sha)
    if (-not $env:VT_API_KEY) {
        Avisar 'VT_API_KEY nao configurada: VirusTotal fica sem-chave.'
        return (Novo-Resultado 'sem-chave' $link)
    }
    $h = @{ 'x-apikey' = $env:VT_API_KEY }
    $base = 'https://www.virustotal.com/api/v3'
    $prazo = (Get-Date).AddMinutes(15)
    # plano gratis: 4 requisicoes por minuto; por isso 15 s entre as chamadas de espera
    $f = Invoke-Api -Url "$base/files/$Sha" -Headers $h
    if (-not $f) {
        if (-not $Arq) { return (Resultado-Com-Mensagem (Novo-Resultado 'erro' $link) 'hash-desconhecido: o VirusTotal nao conhece este hash e nao ha arquivo para enviar') }
        if ((Get-Item $Arq).Length -gt 32MB) {
            $u = Invoke-Api -Url "$base/files/upload_url" -Headers $h
            $envio = $u.data
        }
        else { $envio = "$base/files" }
        $up = Enviar-Multipart $envio $h $Arq
        $id = $up.data.id
        if (-not $id) { throw 'o VirusTotal nao devolveu o id da analise' }
        if (-not $Aguardar) { return (Novo-Resultado 'em-analise' $link) }
        while ($true) {
            Dormir 15
            $a = Invoke-Api -Url "$base/analyses/$id" -Headers $h
            if ($a -and $a.data.attributes.status -eq 'completed') { break }
            if ((Get-Date) -gt $prazo) { return (Novo-Resultado 'em-analise' $link) }
        }
        $f = $null
        while (-not $f) {
            Dormir 15
            $f = Invoke-Api -Url "$base/files/$Sha" -Headers $h
            if (-not $f -and (Get-Date) -gt $prazo) { return (Novo-Resultado 'em-analise' $link) }
        }
    }
    $r = ConvertFrom-VtArquivo $f $Sha
    # arquivo conhecido mas sem analise fechada (acabou de chegar): espera, se pediram
    while ($r.status -eq 'em-analise' -and $Aguardar -and (Get-Date) -lt $prazo) {
        Dormir 15
        $f = Invoke-Api -Url "$base/files/$Sha" -Headers $h
        if ($f) { $r = ConvertFrom-VtArquivo $f $Sha }
    }
    $r
}

function Consultar-MetaDefender([string]$Sha, [string]$Arq, [bool]$Aguardar) {
    $link = "https://metadefender.com/results/hash/$Sha"
    if (-not $env:METADEFENDER_API_KEY) {
        Avisar 'METADEFENDER_API_KEY nao configurada: MetaDefender fica sem-chave.'
        return (Novo-Resultado 'sem-chave' $link)
    }
    $h = @{ apikey = $env:METADEFENDER_API_KEY }
    $base = 'https://api.metadefender.com/v4'
    $prazo = (Get-Date).AddMinutes(15)
    $r = $null
    $f = Invoke-Api -Url "$base/hash/$Sha" -Headers $h
    if ($f) { $r = ConvertFrom-MdArquivo $f $Sha }
    if (-not $r) {
        if (-not $Arq) { return (Resultado-Com-Mensagem (Novo-Resultado 'erro' $link) 'hash-desconhecido: o MetaDefender nao conhece este hash e nao ha arquivo para enviar') }
        $hh = @{ apikey = $env:METADEFENDER_API_KEY; filename = [IO.Path]::GetFileName($Arq) }
        $up = Invoke-Api -Metodo 'POST' -Url "$base/file" -Headers $hh -ContentType 'application/octet-stream' -InFile $Arq
        $id = $up.data_id
        if (-not $id) { throw 'o MetaDefender nao devolveu o data_id' }
        $r = Novo-Resultado 'em-analise' $link
        if (-not $Aguardar) { return $r }
        while ($true) {
            Dormir 5
            $s = Invoke-Api -Url "$base/file/$id" -Headers $h
            if ($s) { $x = ConvertFrom-MdArquivo $s $Sha; if ($x -and $x.status -eq 'ok') { return $x } }
            if ((Get-Date) -gt $prazo) { return $r }
        }
    }
    while ($r.status -eq 'em-analise' -and $Aguardar -and (Get-Date) -lt $prazo) {
        Dormir 5
        $f = Invoke-Api -Url "$base/hash/$Sha" -Headers $h
        if ($f) { $x = ConvertFrom-MdArquivo $f $Sha; if ($x) { $r = $x } }
    }
    $r
}

# roda um servico sem deixar o erro dele derrubar o script
function Executar-Servico([string]$Nome, [string]$Link, [scriptblock]$Bloco) {
    try { & $Bloco }
    catch {
        Avisar "$Nome falhou: $($_.Exception.Message)"
        Resultado-Com-Mensagem (Novo-Resultado 'erro' $Link) ([string]$_.Exception.Message)
    }
}

# ---------- saidas ----------

function Formatar-Data($iso) {
    if (-not $iso) { return '' }
    try { ([DateTimeOffset]::Parse([string]$iso, [Globalization.CultureInfo]::InvariantCulture)).UtcDateTime.ToString('dd/MM/yyyy HH:mm', [Globalization.CultureInfo]::InvariantCulture) + ' UTC' } catch { [string]$iso }
}

function Linha-Servico([string]$nome, $s) {
    if (-not $s) { return $null }
    $data = Formatar-Data $s.analisado_em
    switch ($s.status) {
        'ok' {
            $t = "- **${nome}:** $($s.deteccoes) de $($s.total) antivírus acusaram. [Ver o resultado]($($s.link))"
            if ($data) { $t += ". Conferido em $data" }
            $t += '.'
            if ($s.motores_que_acusaram -and @($s.motores_que_acusaram).Count -gt 0) {
                $lista = @($s.motores_que_acusaram | Select-Object -First 10 | ForEach-Object { if ($_.resultado) { "$($_.motor) ($($_.resultado))" } else { $_.motor } })
                $t += ' Acusaram: ' + ($lista -join ', ') + '.'
            }
            return $t
        }
        'em-analise' { return "- **${nome}:** análise ainda em andamento. [Ver o resultado]($($s.link))." }
        'sem-chave' { return "- **${nome}:** não conferido nesta release (serviço não configurado)." }
        default { return "- **${nome}:** indisponível no momento. [Ver o resultado]($($s.link))." }
    }
}

# nome do arquivo conferido em cada tipo
function Nome-Arquivo([string]$tipo) { if ($tipo -eq 'atualizacao') { 'streemu-main.zip' } else { 'Streemu-Instalador.exe' } }

# $s = a secao (o mesmo JSON que o site le). O texto diz o nome certo do arquivo.
function Novo-BlocoNotas($s, [string]$repo) {
    $arq = if ($s.arquivo) { [string]$s.arquivo } else { Nome-Arquivo $s.tipo }
    $l = @('<!-- varredura:inicio -->', "**Antivírus** (arquivo conferido: ``$arq``)", '')
    if ($s.defender -and $s.defender.status -eq 'limpo') {
        $ass = if ($s.defender.versao_assinaturas) { " (assinaturas $($s.defender.versao_assinaturas))" } else { '' }
        $quais = ''
        if ($s.defender.arquivos -and @($s.defender.arquivos).Count -gt 0) { $quais = ' em ' + ((@($s.defender.arquivos | ForEach-Object { "``$($_.arquivo)``" })) -join ' e ') }
        $l += "- **Microsoft Defender:** nada encontrado$quais$ass, conferido antes de publicar."
    }
    $l += Linha-Servico 'VirusTotal' $s.virustotal
    $l += Linha-Servico 'MetaDefender' $s.metadefender
    $l += ''
    $quem = if ($s.tipo -eq 'atualizacao') { 'Programas ainda não assinados' } else { 'Instaladores ainda não assinados' }
    $l += "$quem às vezes levam 1 a 3 alertas de antivírus pouco conhecidos (falso positivo). O resultado é mostrado do jeito que vier. Dados completos: [varredura.json](https://raw.githubusercontent.com/$repo/status/varredura.json)."
    $l += '<!-- varredura:fim -->'
    ($l | Where-Object { $null -ne $_ }) -join "`n"
}

# a secao do tipo: o que vai para o varredura.json (sem "tipo", que o publicar-varredura.ps1 usa e tira)
function Nova-Secao([string]$tipo, [string]$versao, [string]$tag, [string]$sha, [string]$commit, [string]$publicadoEm, $defender, $vt, $md) {
    $sec = [ordered]@{ tipo = $tipo; versao = $versao; tag = $tag; arquivo = (Nome-Arquivo $tipo); sha256 = $sha }
    if ($commit) { $sec['commit'] = $commit }
    $sec['publicado_em'] = if ($publicadoEm) { $publicadoEm } else { $null }
    if ($defender) { $sec['defender'] = $defender }
    $sec['virustotal'] = $vt
    $sec['metadefender'] = $md
    $sec
}

# ---------- principal ----------

# "dot-source" (. ./varredura.ps1) so carrega as funcoes, para teste
if ($MyInvocation.InvocationName -eq '.') { return }

if (-not $Sha256 -and -not $Arquivo) { throw 'Passe -Arquivo <exe> ou -Sha256 <hash>.' }
if ($Arquivo -and -not (Test-Path $Arquivo)) { throw "Nao achei o arquivo '$Arquivo'." }
$hash = $Sha256.ToLowerInvariant()
if ($Arquivo) {
    $calc = (Get-FileHash $Arquivo -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -and $hash -ne $calc) { throw "O SHA-256 do arquivo ($calc) nao bate com o -Sha256 ($hash)." }
    $hash = $calc
}
if ($hash -notmatch '^[0-9a-f]{64}$') { throw 'SHA-256 invalido.' }
$repo = if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { 'Streemu/Streemu' }

New-Item -ItemType Directory -Force $Saida | Out-Null
$arq = if ($Arquivo) { (Resolve-Path $Arquivo).Path } else { '' }

$vt = Executar-Servico 'VirusTotal' ($VtPagina + $hash) { Consultar-VirusTotal $hash $arq ([bool]$Esperar) }
$md = Executar-Servico 'MetaDefender' "https://metadefender.com/results/hash/$hash" { Consultar-MetaDefender $hash $arq ([bool]$Esperar) }

if (-not $Tag -and $Tipo -eq 'atualizacao') { $Tag = 'main-atual' }
$defender = $null
$defPath = Join-Path $Saida 'defender.json'
if (Test-Path $defPath) {
    try { $defender = Ler-Json $defPath }
    catch { Avisar "defender.json ilegivel, ignorado: $($_.Exception.Message)" }
}
$secao = Nova-Secao $Tipo $Versao $Tag $hash $Commit $PublicadoEm $defender $vt $md

Escrever-Utf8 (Join-Path $Saida 'secao.json') (($secao | ConvertTo-Json -Depth 8) + "`n")
# o bloco e montado a partir do JSON ja gravado, o mesmo que o site le
$lido = Ler-Json (Join-Path $Saida 'secao.json')
Escrever-Utf8 (Join-Path $Saida 'notas-varredura.md') ((Novo-BlocoNotas $lido $repo) + "`n")

Write-Host "VirusTotal: $($vt.status) $($vt.deteccoes)/$($vt.total) | MetaDefender: $($md.status) $($md.deteccoes)/$($md.total)"
