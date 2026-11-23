# Sunset Boost · gera o instalador (SunsetBoost-Setup-<versão>.exe) em artifacts\
# Rode SEM admin, num PowerShell comum, na pasta do projeto:
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\empacotar.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\empacotar.ps1 -Versao 0.2.0
#
# É o mesmo script que o GitHub Actions usa pra montar o instalador das releases (.github\workflows\release.yml).
# O instalador publicado na página de Releases sai SEMPRE do GitHub Actions, nunca de um PC de desenvolvimento.
#
# Precisa: .NET 10 SDK, o C++ das Ferramentas de Build do Visual Studio (linker do NativeAOT, pro launcher)
# e o Inno Setup 6 (https://jrsoftware.org/isdl.php ou: winget install JRSoftware.InnoSetup).
# O app sai self-contained: quem instala não precisa ter o .NET.

param([string]$Versao)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
function Say($text, $color = 'Gray') { Write-Host $text -ForegroundColor $color }

if (-not $Versao) { $Versao = (Select-Xml -Path (Join-Path $root 'Directory.Build.props') -XPath '//Version').Node.InnerText }
if ($Versao -notmatch '^\d+\.\d+\.\d+$') { throw "Versão inválida: '$Versao' (use 1.2.3)" }

$iscc = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { Say 'Falta o Inno Setup 6: winget install JRSoftware.InnoSetup' Red; exit 1 }

# O NativeAOT acha o linker do C++ chamando o vswhere pelo PATH, e o instalador do VS não o coloca lá
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if ((Test-Path (Join-Path $vsInstaller 'vswhere.exe')) -and ($env:Path -notlike "*$vsInstaller*")) { $env:Path += ";$vsInstaller" }

$artifacts = Join-Path $root 'artifacts'
$out = Join-Path $artifacts 'out'
if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }

$common = @('-c', 'Release', '-r', 'win-x64', '-p:DebugType=none', "-p:Version=$Versao", '-nologo', '-v', 'q')

Say "Sunset Boost $Versao" Magenta
Say 'Gerando o launcher nativo (SunsetBoost.exe)…' Cyan
dotnet publish (Join-Path $root 'src\SunsetBoost.Launcher') @common -o (Join-Path $out 'launcher')
if ($LASTEXITCODE -ne 0) { throw 'Falha ao gerar o launcher' }

# Self-contained: leva o .NET junto. As DLLs nativas do WPF ficam AO LADO do .exe (em Program Files, protegidas),
# nunca extraídas pra %TEMP% (IncludeNativeLibrariesForSelfExtract), que qualquer programa do usuário altera.
Say 'Gerando o app (SunsetBoost.App.exe, com o .NET embutido)…' Cyan
dotnet publish (Join-Path $root 'src\SunsetBoost.App') @common --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=false -o (Join-Path $out 'app')
if ($LASTEXITCODE -ne 0) { throw 'Falha ao gerar o app' }

Say 'Montando o instalador…' Cyan
& $iscc /Q "/DAppVersion=$Versao" (Join-Path $root 'installer\SunsetBoost.iss')
if ($LASTEXITCODE -ne 0) { throw 'Falha no Inno Setup' }

$setup = Join-Path $artifacts "SunsetBoost-Setup-$Versao.exe"
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
# Formato do sha256sum: "<hash>  <arquivo>"
Set-Content -Path "$setup.sha256" -Value "$hash  $(Split-Path $setup -Leaf)" -Encoding ascii -NoNewline

Say ''
Say "Pronto: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)" Green
Say "SHA-256: $hash" DarkGray
