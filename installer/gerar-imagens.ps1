# Sunset Boost · gera as imagens do instalador (wizard*.bmp) com o pôr do sol do app.
# Só precisa rodar de novo se mudar o visual:  powershell -NoProfile -File .\installer\gerar-imagens.ps1
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
function C($hex) { [Drawing.ColorTranslator]::FromHtml($hex) }

function Sunset([int]$w, [int]$h, [string]$file) {
    $bmp = New-Object Drawing.Bitmap $w, $h, ([Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'

    # Céu: o mesmo degradê da janela do app
    $sky = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.Rectangle 0, 0, $w, $h), (C '#140B2E'), (C '#FFB35C'), 90
    $blend = New-Object Drawing.Drawing2D.ColorBlend
    $blend.Colors = @((C '#140B2E'), (C '#3B1458'), (C '#A12A76'), (C '#FF6F5E'), (C '#FFB35C'))
    $blend.Positions = @(0, 0.38, 0.66, 0.86, 1)
    $sky.InterpolationColors = $blend
    $g.FillRectangle($sky, 0, 0, $w, $h)

    # Sol com faixas (como no app), meio afundado no mar
    $seaTop = [int]($h * 0.78)
    $r = [int]($w * 0.42)
    $cx = [int]($w / 2); $cy = $seaTop - [int]($r * 0.35)
    $sunRect = New-Object Drawing.Rectangle ($cx - $r), ($cy - $r), (2 * $r), (2 * $r)
    $sun = New-Object Drawing.Drawing2D.LinearGradientBrush $sunRect, (C '#FFE38A'), (C '#FF4F8B'), 90
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddEllipse($sunRect)
    $region = New-Object Drawing.Region $path
    $band = [Math]::Max(2, [int]($h / 90))
    for ($y = $cy; $y -lt $cy + $r; $y += $band * 3) { $region.Exclude((New-Object Drawing.Rectangle ($cx - $r), $y, (2 * $r), $band)) }
    $g.FillRegion($sun, $region)

    # Mar com ondas rosadas
    $seaRect = New-Object Drawing.Rectangle 0, $seaTop, $w, ($h - $seaTop)
    $sea = New-Object Drawing.Drawing2D.LinearGradientBrush $seaRect, (C '#5A1F6E'), (C '#2A0F45'), 90
    $g.FillRectangle($sea, $seaRect)
    $alphas = 90, 56, 36
    for ($i = 0; $i -lt 3; $i++) {
        $pen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb($alphas[$i], 255, 143, 192)), ([Math]::Max(1, $w / 160))
        $y = $seaTop + ($h - $seaTop) * (0.18 + 0.28 * $i)
        $pts = for ($x = 0; $x -le $w; $x += 4) { New-Object Drawing.PointF $x, ($y + [Math]::Sin($x / ($w / 6.0)) * ($h / 160.0)) }
        $g.DrawLines($pen, [Drawing.PointF[]]$pts)
    }
    $g.Dispose()
    $bmp.Save((Join-Path $here $file), [Drawing.Imaging.ImageFormat]::Bmp)
    $bmp.Dispose()
}

function Badge([int]$s, [string]$file) {
    $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.Clear((C '#FFFFFF'))   # a faixa de cima do instalador é branca
    $g.InterpolationMode = 'HighQualityBicubic'
    $icon = New-Object Drawing.Icon (Join-Path $here '..\src\SunsetBoost.App\Assets\sunset.ico'), 256, 256
    $g.DrawImage($icon.ToBitmap(), 0, 0, $s, $s)
    $g.Dispose()
    $bmp.Save((Join-Path $here $file), [Drawing.Imaging.ImageFormat]::Bmp)
    $bmp.Dispose()
}

Sunset 164 314 'wizard.bmp'
Sunset 328 628 'wizard-2x.bmp'
Badge 55 'wizard-small.bmp'
Badge 110 'wizard-small-2x.bmp'
Write-Host 'Imagens geradas em installer\' -ForegroundColor Green
