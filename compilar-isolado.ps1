# Sunset Boost · compila o launcher e o app numa pasta isolada. Usado pelo instalar.ps1, que roda como admin.
# Compilar executa código do MSBuild e do NuGet: com admin, nada que um programa sem admin consegue mexer pode entrar.
#   - compila uma cópia dos fontes, sem bin/obj/publish e sem seguir links (junctions)
#   - pacotes NuGet, cache, TEMP e perfil do dotnet ficam dentro da pasta da compilação
#     (nada do %APPDATA%, %LOCALAPPDATA%, .nuget nem dos imports de MSBuild do seu usuário)
#   - só o PATH do sistema e sem variáveis DOTNET_*, MSBUILD*, NUGET_*, CORECLR_*, COR_*, COMPLUS_*
# O que continua dependendo de você: o conteúdo dos fontes no momento da cópia.
# Rode num processo próprio (o instalador faz isso): o script troca variáveis de ambiente do processo.
param(
    [Parameter(Mandatory)] [string]$Root,   # pasta do projeto
    [Parameter(Mandatory)] [string]$RunDir  # pasta da compilação (o instalador cria uma só pra admin)
)
$ErrorActionPreference = 'Stop'

# Caminhos do sistema pela API do Windows, nunca por variável de ambiente
$system32 = [Environment]::SystemDirectory
$programFiles = [Environment]::GetFolderPath('ProgramFiles')
$programFilesX86 = [Environment]::GetFolderPath('ProgramFilesX86')
$windows = [Environment]::GetFolderPath('Windows')

# 1) Cópia dos fontes: /XJ não segue junctions nem symlinks, /XD deixa de fora o que é gerado
$src = Join-Path $RunDir 'src'
& (Join-Path $system32 'robocopy.exe') $Root $src /E /XJ /XD bin obj publish tests design ferramentas .vs .git /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Falha ao copiar os fontes (robocopy $LASTEXITCODE)" }

# 2) Ambiente: o MSBuild transforma QUALQUER variável de ambiente em propriedade (ex.: CustomBeforeMicrosoftCommonTargets
#    importaria um arquivo), então nada do ambiente atual passa. Recria só com as variáveis do sistema (HKLM, lidas sem
#    expandir, porque a expansão usaria o ambiente atual) e com o "perfil" dentro da pasta da compilação.
$machineKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment')
$machine = @{}
foreach ($name in $machineKey.GetValueNames()) {
    $machine[$name] = $machineKey.GetValue($name, '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
}
$machineKey.Close()
foreach ($name in @([Environment]::GetEnvironmentVariables().Keys)) { [Environment]::SetEnvironmentVariable($name, $null) }

$profileDir = Join-Path $RunDir 'perfil'
$tmp = Join-Path $RunDir 'tmp'
$packages = Join-Path $RunDir 'packages'
$set = [ordered]@{
    SystemRoot = $windows; windir = $windows; SystemDrive = [IO.Path]::GetPathRoot($windows).TrimEnd('\')
    ProgramFiles = $programFiles; 'ProgramFiles(x86)' = $programFilesX86; ProgramW6432 = $programFiles
    ProgramData = [Environment]::GetFolderPath('CommonApplicationData'); ComputerName = [Environment]::MachineName
}
foreach ($name in $set.Keys) { [Environment]::SetEnvironmentVariable($name, $set[$name]) }
foreach ($name in $machine.Keys) {
    if ($name -notmatch '^(DOTNET_|CORECLR_|COR_|COMPLUS_|MSBUILD|NUGET_)') {
        [Environment]::SetEnvironmentVariable($name, [Environment]::ExpandEnvironmentVariables($machine[$name]))
    }
}
$set = [ordered]@{
    USERPROFILE = $profileDir                                 # o Windows resolve %APPDATA%/%LOCALAPPDATA% a partir dele
    APPDATA = Join-Path $profileDir 'AppData\Roaming'          # NuGet.Config do usuário
    LOCALAPPDATA = Join-Path $profileDir 'AppData\Local'       # imports de MSBuild do usuário e cache HTTP do NuGet
    DOTNET_CLI_HOME = $profileDir; NUGET_PACKAGES = $packages; TEMP = $tmp; TMP = $tmp
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'; DOTNET_NOLOGO = '1'
    ComSpec = Join-Path $system32 'cmd.exe'
    # PATH do sistema, mais o vswhere: o NativeAOT acha o linker do C++ chamando o vswhere pelo PATH
    Path = [Environment]::GetEnvironmentVariable('Path') + ';' + (Join-Path $programFilesX86 'Microsoft Visual Studio\Installer')
}
foreach ($name in $set.Keys) { [Environment]::SetEnvironmentVariable($name, $set[$name]) }
foreach ($d in (Join-Path $profileDir 'AppData\Roaming'), (Join-Path $profileDir 'AppData\Local'), $tmp, $packages) {
    [void][IO.Directory]::CreateDirectory($d)
}

# 3) Publish com o dotnet de Program Files, sem servidores de build reaproveitados e sem imports do usuário
$dotnet = Join-Path $programFiles 'dotnet\dotnet.exe'
$common = @(
    '-c', 'Release', '-r', 'win-x64', '-p:DebugType=none', '--disable-build-servers', '-nologo', '-v', 'q',
    '-p:ImportUserLocationsByWildcardBeforeMicrosoftCommonProps=false',
    '-p:ImportUserLocationsByWildcardAfterMicrosoftCommonProps=false',
    '-p:ImportUserLocationsByWildcardBeforeMicrosoftCommonTargets=false',
    '-p:ImportUserLocationsByWildcardAfterMicrosoftCommonTargets=false'
)
# Cada projeto publica na própria pasta: o publish apaga da pasta de saída o que o mesmo projeto gerou da última vez
& $dotnet publish (Join-Path $src 'src\SunsetBoost.Launcher') @common -o (Join-Path $RunDir 'out\launcher')
if ($LASTEXITCODE -ne 0) { throw 'Falha ao gerar o launcher' }
& $dotnet publish (Join-Path $src 'src\SunsetBoost.App') @common --self-contained false -p:PublishSingleFile=true -o (Join-Path $RunDir 'out\app')
if ($LASTEXITCODE -ne 0) { throw 'Falha ao gerar o app' }
