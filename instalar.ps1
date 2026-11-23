# Sunset Boost · instalador
# Rode num PowerShell COMO ADMINISTRADOR, dentro da pasta do projeto:
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\instalar.ps1
# (-NoProfile: o perfil do PowerShell fica na pasta do usuário e rodaria junto, com admin)
#
# O que faz:
#   1. Gera o SunsetBoost.exe (launcher nativo, NativeAOT) e o SunsetBoost.App.exe (interface) numa pasta isolada
#      que só admin altera (compilar-isolado.ps1)
#   2. Copia os dois pra C:\Program Files\SunsetBoost (pasta que só administradores alteram)
#   3. Cria atalho na área de trabalho e no menu Iniciar (os atalhos abrem o launcher)
# "Abrir junto com o Windows" é ligado dentro do app, na aba Perfis.
#
# Precisa: .NET 10 SDK, .NET 10 Desktop Runtime e o C++ das Ferramentas de Build do Visual Studio
# (o linker que o NativeAOT usa pra gerar o launcher).

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dest = Join-Path $env:ProgramFiles 'SunsetBoost'
$launcherName = 'SunsetBoost.exe'
$appName = 'SunsetBoost.App.exe'
$launcher = Join-Path $dest $launcherName

function Say($text, $color = 'Gray') { Write-Host $text -ForegroundColor $color }

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Say 'Abra o PowerShell como administrador e rode de novo.' Red
    exit 1
}

# O app usa o .NET 10 já instalado no PC (por isso o .exe é pequeno)
$runtimes = & dotnet --list-runtimes 2>$null
if (-not ($runtimes -match 'Microsoft\.WindowsDesktop\.App 10\.')) {
    Say 'Falta o .NET 10 Desktop Runtime: https://dotnet.microsoft.com/download/dotnet/10.0' Red
    exit 1
}

# O launcher é compilado com NativeAOT, que precisa do linker do C++ (achado pelo vswhere)
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
$vswhere = Join-Path $vsInstaller 'vswhere.exe'
$vc = if (Test-Path $vswhere) { & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath }
if (-not $vc) {
    Say 'Falta o C++ das Ferramentas de Build do Visual Studio (o launcher SunsetBoost.exe é um .exe nativo):' Red
    Say '  https://visualstudio.microsoft.com/visual-cpp-build-tools/ (carga "Desenvolvimento para desktop com C++")' Red
    exit 1
}

# A versão anterior guardava a cópia do Modo Jogo em %LOCALAPPDATA%; esta só lê da pasta protegida em %ProgramData%.
# Com o Modo Jogo ativo, restaurar antes pela versão instalada, senão as configurações de antes ficariam pra trás.
if (Test-Path (Join-Path $env:LOCALAPPDATA 'SunsetBoost\snapshot.json')) {
    Say 'O Modo Jogo está ativo (tem configurações pra restaurar).' Yellow
    Say 'Abra o Sunset Boost, clique em RESTAURAR TUDO e rode o instalador de novo.' Yellow
    exit 1
}

# Clicou em "Sair" agora há pouco? O ícone some antes do programa terminar de fechar: espera até 5 s
$processes = 'SunsetBoost', 'SunsetBoost.App'
for ($i = 0; $i -lt 10 -and (Get-Process -Name $processes -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
if (Get-Process -Name $processes -ErrorAction SilentlyContinue) {
    Say 'O Sunset Boost está aberto. Feche pelo ícone perto do relógio (botão direito › Sair) e rode de novo.' Yellow
    exit 1
}

# Segurança: este script roda como admin e instala o que está nesta pasta (o .exe vai pra Program Files e pode
# virar tarefa com admin). Se outros usuários do PC conseguem alterar a pasta, qualquer um plantaria um programa aqui.
$broad = @(
    'S-1-1-0',      # Todos
    'S-1-5-4',      # INTERATIVO
    'S-1-5-11',     # Usuários autenticados
    'S-1-5-32-545'  # Usuários
)
$writeRights = [Security.AccessControl.FileSystemRights]'WriteData, AppendData, WriteExtendedAttributes, WriteAttributes, Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership'
$unsafe = @()
foreach ($item in @(Get-Item $root) + @(Get-ChildItem $root -Recurse -Force -ErrorAction SilentlyContinue)) {
    $rules = (Get-Acl -LiteralPath $item.FullName).GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])
    foreach ($r in $rules) {
        if ($r.AccessControlType -ne 'Allow') { continue }
        if ($r.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) { continue }
        if ($broad -notcontains $r.IdentityReference.Value) { continue }
        if ($r.FileSystemRights -band $writeRights) { $unsafe += $item.FullName; break }
    }
    if ($unsafe.Count -ge 5) { break }
}
if ($unsafe) {
    Say 'A pasta do projeto pode ser alterada por OUTROS usuários do PC (ex.: "Usuários autenticados"):' Red
    $unsafe | ForEach-Object { Say "  $_" Red }
    Say 'Instalar daqui como administrador seria arriscado. Corrija as permissões (mantém admin, SYSTEM e você) e rode de novo:' Yellow
    Say "  icacls `"$root`" /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' '$($env:USERNAME):(OI)(CI)M' '*S-1-5-32-545:(OI)(CI)RX'" Yellow
    Say "  icacls `"$root\*`" /reset /T /C /Q" Yellow
    exit 1
}

# Compila do zero numa pasta nova que só admin altera (nunca instala um publish\ antigo que pode ter sido trocado).
# Nome imprevisível e criada já com as permissões: um usuário comum não consegue plantar nada nela antes nem durante.
# O compilar-isolado.ps1 roda num PowerShell próprio, sem perfil, porque troca as variáveis de ambiente do processo.
$sec = New-Object Security.AccessControl.DirectorySecurity
$sec.SetAccessRuleProtection($true, $false)
foreach ($sid in 'S-1-5-32-544', 'S-1-5-18') {
    $sec.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        (New-Object Security.Principal.SecurityIdentifier $sid), 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow')))
}
$sec.SetOwner((New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'))
$runDir = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) "SunsetBoost-build-$([guid]::NewGuid().ToString('N'))"
if (Test-Path -LiteralPath $runDir) { Say "A pasta $runDir já existe: alguém a criou antes. Rode de novo." Red; exit 1 }
[void][IO.Directory]::CreateDirectory($runDir, $sec)

try {
    Say 'Gerando o SunsetBoost.exe (launcher nativo) e o SunsetBoost.App.exe numa pasta isolada…' Cyan
    $ps = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
    & $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'compilar-isolado.ps1') -Root $root -RunDir $runDir
    if ($LASTEXITCODE -ne 0) { Say 'Falha ao compilar (veja a mensagem acima)' Red; exit 1 }

    # O app vai antes do launcher: o launcher nunca fica apontando pra um app que ainda não chegou
    $files = [ordered]@{ $appName = (Join-Path $runDir "out\app\$appName"); $launcherName = (Join-Path $runDir "out\launcher\$launcherName") }
    foreach ($f in $files.Keys) {
        if (-not (Test-Path $files[$f])) { Say "O publish não gerou o $f" Red; exit 1 }
        Say "  $f SHA-256: $((Get-FileHash $files[$f] -Algorithm SHA256).Hash)" DarkGray
    }

    Say "Copiando pra $dest…" Cyan
    New-Item -ItemType Directory -Force $dest | Out-Null
    foreach ($f in $files.Keys) {
        # O antivírus ou o Windows às vezes seguram o .exe antigo por alguns segundos: tenta por até 10 s
        $copied = $false
        for ($i = 1; $i -le 10 -and -not $copied; $i++) {
            try { Copy-Item $files[$f] (Join-Path $dest $f) -Force -ErrorAction Stop; $copied = $true }
            catch { if ($i -eq 1) { Say "O $f antigo ainda está em uso, esperando liberar…" Yellow }; Start-Sleep -Seconds 1 }
        }
        if (-not $copied) {
            Say "Não deu pra substituir o $f`: ele continua em uso. Feche o app (ícone do relógio › Sair) ou reinicie o PC e rode de novo." Red
            exit 1
        }
    }
}
finally {
    # A pasta da compilação é só de admin. rmdir /s apaga links sem entrar neles (o Windows cria pastas especiais, como
    # Content.IE5, no perfil falso da compilação, que o Directory.Delete não consegue apagar). Se sobrar algo, só avisa.
    if (Test-Path -LiteralPath $runDir) {
        & (Join-Path ([Environment]::SystemDirectory) 'cmd.exe') /d /c "rmdir /s /q `"$runDir`""
        if (Test-Path -LiteralPath $runDir) { Say "Não deu pra apagar a pasta temporária $runDir (pode apagar depois)." Yellow }
    }
}

$shell = New-Object -ComObject WScript.Shell
foreach ($lnkPath in @(
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Sunset Boost.lnk'),
        (Join-Path ([Environment]::GetFolderPath('Programs')) 'Sunset Boost.lnk'))) {
    $lnk = $shell.CreateShortcut($lnkPath)
    $lnk.TargetPath = $launcher
    $lnk.WorkingDirectory = $dest
    $lnk.Description = 'Sunset Boost · modo jogo com um clique'
    $lnk.Save()
}

Say ''
Say 'Sunset Boost instalado.' Green
Say "  Programa: $launcher (pede admin e abre o $appName com ambiente limpo)"
Say "  Configurações e logs: $(Join-Path $env:ProgramData 'SunsetBoost') (só administradores alteram)"
Say '  Atalhos: área de trabalho e menu Iniciar'
Say '  Pra abrir junto com o Windows: no app, aba PERFIS › "Abrir junto com o Windows".'
