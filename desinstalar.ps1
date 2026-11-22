# Sunset Boost · desinstalador
# Rode num PowerShell COMO ADMINISTRADOR:
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\desinstalar.ps1            (mantém configurações e logs)
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\desinstalar.ps1 -ApagarDados

param([switch]$ApagarDados)
$ErrorActionPreference = 'Stop'
function Say($text, $color = 'Gray') { Write-Host $text -ForegroundColor $color }

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Say 'Abra o PowerShell como administrador e rode de novo.' Red
    exit 1
}

$data = Join-Path $env:ProgramData 'SunsetBoost'     # com admin (protegida)
$legacy = Join-Path $env:LOCALAPPDATA 'SunsetBoost'  # versão anterior e modo sem admin
foreach ($d in $data, $legacy) {
    if (Test-Path (Join-Path $d 'snapshot.json')) {
        Say 'O Modo Jogo ainda está ativo (tem configurações pra restaurar).' Yellow
        Say 'Abra o Sunset Boost e clique em RESTAURAR TUDO antes de desinstalar.' Yellow
        exit 1
    }
}

if (Get-Process -Name 'SunsetBoost', 'SunsetBoost.App' -ErrorAction SilentlyContinue) {
    Say 'Feche o Sunset Boost (ícone perto do relógio › Sair) e rode de novo.' Yellow
    exit 1
}

schtasks /Query /TN 'Sunset Boost' 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) { schtasks /Delete /TN 'Sunset Boost' /F | Out-Null; Say 'Início com o Windows removido.' }

foreach ($lnk in @(
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Sunset Boost.lnk'),
        (Join-Path ([Environment]::GetFolderPath('Programs')) 'Sunset Boost.lnk'))) {
    if (Test-Path $lnk) { Remove-Item $lnk -Force }
}

$dest = Join-Path $env:ProgramFiles 'SunsetBoost'
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }

if ($ApagarDados) {
    # [IO.Directory]::Delete não entra em junction (só apaga o link), então nunca apaga arquivos fora da pasta
    if (Test-Path -LiteralPath $data) { [IO.Directory]::Delete($data, $true); Say 'Configurações e logs apagados.' }
    # A pasta antiga é do seu usuário: apagar como admin daria a outro programa a chance de redirecionar a exclusão
    if (Test-Path -LiteralPath $legacy) { Say "A pasta $legacy (versão anterior / modo sem admin) ficou: apague-a num PowerShell sem admin." DarkGray }
}

Say 'Sunset Boost desinstalado.' Green
