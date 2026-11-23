; Sunset Boost · instalador (Inno Setup 6)
; Não compile direto: rode o empacotar.ps1, que gera os .exe em artifacts\out e chama o ISCC com a versão.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define RepoUrl "https://github.com/carlosftb/sunset-boost"
#define Out "..\artifacts\out"

[Setup]
AppId={{6F3C2A8E-5B1D-4E7A-9C44-0D8B2F1E7A53}
AppName=Sunset Boost
AppVersion={#AppVersion}
AppVerName=Sunset Boost {#AppVersion}
AppPublisher=Sunset Boost
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Instalador do Sunset Boost
; O app só liga o início com o Windows (tarefa com admin) se estiver em Program Files: a pasta não pode ser trocada
DefaultDirName={autopf}\SunsetBoost
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
; Fechado pela instância única do app: o instalador pede pra fechar antes de trocar os arquivos
AppMutex=Local\SunsetBoost.SingleInstance
OutputDir=..\artifacts
OutputBaseFilename=SunsetBoost-Setup-{#AppVersion}
SetupIconFile=..\src\SunsetBoost.App\Assets\sunset.ico
UninstallDisplayIcon={app}\SunsetBoost.exe
UninstallDisplayName=Sunset Boost
WizardStyle=modern
WizardImageFile=wizard.bmp,wizard-2x.bmp
WizardSmallImageFile=wizard-small.bmp,wizard-small-2x.bmp
Compression=lzma2/ultra64
SolidCompression=yes

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ptbr.WelcomeExtra=Modo jogo com um clique: fecha o que pesa, aplica ajustes reversíveis e desfaz tudo quando você termina.%n%nJogos, anti-cheats e processos do Windows nunca são tocados.
en.WelcomeExtra=One-click game mode: closes what weighs your PC down, applies reversible tweaks and undoes everything when you're done.%n%nGames, anti-cheats and Windows processes are never touched.
ptbr.GameModeActiveSetup=O Modo Jogo de uma versão antiga do Sunset Boost ainda está ativo.%n%nAbra o Sunset Boost, clique em RESTAURAR TUDO e rode o instalador de novo.
en.GameModeActiveSetup=Game Mode from an older Sunset Boost version is still active.%n%nOpen Sunset Boost, click RESTORE ALL and run the installer again.
ptbr.GameModeActiveUninstall=O Modo Jogo ainda está ativo: as configurações de antes ainda não foram restauradas.%n%nAbra o Sunset Boost, clique em RESTAURAR TUDO e desinstale de novo.
en.GameModeActiveUninstall=Game Mode is still active: your previous settings haven't been restored yet.%n%nOpen Sunset Boost, click RESTORE ALL and uninstall again.
ptbr.ShortcutComment=Modo jogo com um clique
en.ShortcutComment=One-click game mode

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[InstallDelete]
; Atalhos por usuário criados pelo instalar.ps1 (instalação a partir do código): agora os atalhos são de todos os usuários
Type: files; Name: "{userdesktop}\Sunset Boost.lnk"
Type: files; Name: "{userprograms}\Sunset Boost.lnk"

[Files]
; O app vai antes do launcher: o launcher nunca fica apontando pra um app que ainda não chegou
Source: "{#Out}\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Out}\launcher\SunsetBoost.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; Os atalhos abrem o launcher (SunsetBoost.exe): é ele que pede admin e abre a interface com ambiente limpo
Name: "{autoprograms}\Sunset Boost"; Filename: "{app}\SunsetBoost.exe"; Comment: "{cm:ShortcutComment}"
Name: "{autodesktop}\Sunset Boost"; Filename: "{app}\SunsetBoost.exe"; Comment: "{cm:ShortcutComment}"; Tasks: desktopicon

[Run]
; O postinstall roda como o usuário original (sem admin) e o launcher exige admin: CreateProcess falharia com o erro 740.
; shellexec usa o ShellExecute, que mostra o UAC como um duplo clique no atalho, e o app abre no usuário certo.
Filename: "{app}\SunsetBoost.exe"; Description: "{cm:LaunchProgram,Sunset Boost}"; Flags: nowait postinstall skipifsilent shellexec

[UninstallRun]
; "Abrir junto com o Windows" (tarefa agendada criada pelo app). Se não existir, o schtasks só falha em silêncio.
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""Sunset Boost"" /F"; Flags: runhidden; RunOnceId: "RemoverTarefaInicio"

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
  // Versões antigas guardavam a cópia do Modo Jogo na pasta do usuário; a atual só lê de %ProgramData%
  if FileExists(ExpandConstant('{localappdata}\SunsetBoost\snapshot.json')) then
  begin
    MsgBox(CustomMessage('GameModeActiveSetup'), mbError, MB_OK);
    Result := False;
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  // Desinstalar com o Modo Jogo ativo deixaria o Windows com os ajustes aplicados e sem quem os desfaça
  if FileExists(ExpandConstant('{commonappdata}\SunsetBoost\snapshot.json')) then
  begin
    MsgBox(CustomMessage('GameModeActiveUninstall'), mbError, MB_OK);
    Result := False;
  end;
end;

procedure InitializeWizard();
begin
  WizardForm.WelcomeLabel2.Caption := WizardForm.WelcomeLabel2.Caption + #13#10#13#10 + CustomMessage('WelcomeExtra');
end;
