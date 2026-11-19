<#
  Gera as imagens do assistente do instalador do Streemu (instalador/streemu.iss) a partir da arte da Mumu jogando.
  A arte original (mumu-jogando.png, 1053x1494, fundo transparente) fica fora do git; o que vai para o git sao as
  versoes reduzidas que este script grava em instalador/arte/.

  Tamanhos (ajuda do Inno Setup 6.6+, topicos WizardImageFile e WizardSmallImageFile, fonte e WizardSizePercent padrao):
    imagem lateral (paginas de boas-vindas e de fim): proporcao 164:314
      100% 202x386 | 125% 269x515 | 150% 336x643 | 200% 430x824 | 250% 534x1022
    imagem pequena (canto de cima das outras paginas): quadrada
      100% 58 | 125% 77 | 150% 97 | 200% 124 | 250% 159
  Com varios arquivos separados por virgula, o Setup escolhe o que mais combina com o DPI do PC. PNG com transparencia
  e aceito desde o Inno Setup 6.5.2; o fundo atras da Mumu e a cor de WizardImageBackColor do .iss.

  Uso: powershell -NoProfile -ExecutionPolicy Bypass -File instalador/arte/gerar-arte.ps1 -Origem <mumu-jogando.png>
#>
param([Parameter(Mandatory = $true)][string]$Origem)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$saida = $PSScriptRoot
$src = [System.Drawing.Image]::FromFile((Resolve-Path $Origem).Path)

function Desenhar([int]$w, [int]$h, [System.Drawing.RectangleF]$de, [System.Drawing.RectangleF]$para, [string]$arquivo) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        # sem isso a borda da imagem reduzida puxa pixels de fora (halo escuro no contorno)
        $attr = New-Object System.Drawing.Imaging.ImageAttributes
        $attr.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
        $dest = [System.Drawing.Rectangle]::Round($para)
        $g.DrawImage($src, $dest, $de.X, $de.Y, $de.Width, $de.Height, [System.Drawing.GraphicsUnit]::Pixel, $attr)
        $bmp.Save((Join-Path $saida $arquivo), [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Host ("{0}  {1}x{2}" -f $arquivo, $w, $h)
    }
    finally { $g.Dispose(); $bmp.Dispose() }
}

$todo = New-Object System.Drawing.RectangleF 0, 0, $src.Width, $src.Height

# Lateral: a Mumu inteira, 88% da largura, um pouco abaixo do meio (sobra ar em cima, como numa capa)
foreach ($t in @(202, 386), @(269, 515), @(336, 643), @(430, 824), @(534, 1022)) {
    $w = $t[0]; $h = $t[1]
    $mw = $w * 0.88
    $mh = $mw * $src.Height / $src.Width
    $x = ($w - $mw) / 2
    $y = ($h - $mh) / 2 + $h * 0.05
    Desenhar $w $h $todo (New-Object System.Drawing.RectangleF $x, $y, $mw, $mh) "mumu-lateral-$w.png"
}

# Pequena: rosto e controle (um quadrado de 95% da largura a partir do topo), para caber no canto em 58 px
$lado = $src.Width * 0.95
$rosto = New-Object System.Drawing.RectangleF (($src.Width - $lado) / 2), ($src.Height * 0.01), $lado, $lado
foreach ($s in 58, 77, 97, 124, 159) {
    Desenhar $s $s $rosto (New-Object System.Drawing.RectangleF 0, 0, $s, $s) "mumu-canto-$s.png"
}
$src.Dispose()
