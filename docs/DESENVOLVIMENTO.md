# 🛠️ Desenvolvimento

Tudo que é preciso pra compilar, testar e lançar o Sunset Boost. Pra só usar o app, veja o [README](../README.md).

C# + WPF, .NET 10, só Windows 10 (2004) ou mais novo, 64 bits.

## Estrutura

| Pasta | O que é |
|---|---|
| `src/SunsetBoost.Core` | Motor: leitura de processos, regras de segurança, limpeza, memória. Sem interface. |
| `src/SunsetBoost.Launcher` | `SunsetBoost.exe`: launcher nativo (NativeAOT) que pede admin e abre a interface com ambiente limpo. |
| `src/SunsetBoost.App` | Interface WPF (`SunsetBoost.App.exe`). Abre sem admin; instalada, passa pelo launcher pra ganhar admin. Fora da instalação, roda no modo limitado. |
| `src/SunsetBoost.Cli` | Linha de comando pra testar o motor sem a interface. |
| `tests/SunsetBoost.Core.Tests` | Testes automáticos (xUnit). |
| `installer/` | Roteiro do instalador (Inno Setup) e as imagens dele. |
| `design/mockup.html` | Mockup visual da Fase 1 (abrir no navegador). |
| `ferramentas/` | Scripts avulsos de manutenção do Windows, fora do app. |

Configurações e logs: com admin, em `%ProgramData%\SunsetBoost\` (só administradores alteram; a versão anterior usava a pasta abaixo e as configurações são copiadas uma vez). Sem admin (desenvolvimento, CLI), em `%LOCALAPPDATA%\SunsetBoost\`.

## Comandos

```powershell
dotnet build SunsetBoost.sln
dotnet test SunsetBoost.sln

# App com interface, em modo simulação (nada é alterado)
dotnet run --project src/SunsetBoost.App -- --simular

# Mostra o que seria fechado, sem tocar em nada
dotnet run --project src/SunsetBoost.Cli -- simular --perfil valorant

# Mesma coisa, decidindo apps da lista "Você decide"
dotnet run --project src/SunsetBoost.Cli -- simular --fechar discord,spotify --manter steam

# Limpeza de verdade (terminal como administrador; pede pra digitar SIM)
dotnet run --project src/SunsetBoost.Cli -- limpar --perfil valorant

dotnet run --project src/SunsetBoost.Cli -- memoria

# Modo Jogo (ajustes reversíveis). status não precisa de admin; o resto precisa
dotnet run --project src/SunsetBoost.Cli -- modojogo status
dotnet run --project src/SunsetBoost.Cli -- modojogo testar   # aplica tudo, confere, restaura e compara
dotnet run --project src/SunsetBoost.Cli -- modojogo entrar
dotnet run --project src/SunsetBoost.Cli -- modojogo sair

# Teste manual do Edge leve (fecha as abas do SEU Edge menos a 1ª; pede pra digitar SIM)
dotnet run --project src/SunsetBoost.Cli -- edgeleve
```

Perfis: `valorant`, `lol`, `steam`, `epic`, `generico`.

No build Debug, `--captura <aba> <arquivo.png>` abre o app, tira um print da aba (`home`, `clean`, `game`, `prof`, `diag`) e fecha. É assim que as imagens de `docs/img` são geradas.

## Instalar a partir do código

Num PowerShell **como administrador**, na pasta do projeto:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\instalar.ps1
```

Instala em `C:\Program Files\SunsetBoost` o `SunsetBoost.exe` e o `SunsetBoost.App.exe` e cria atalhos na área de trabalho e no menu Iniciar.
Precisa do .NET 10 SDK e Desktop Runtime e do C++ das [Ferramentas de Build do Visual Studio](https://visualstudio.microsoft.com/visual-cpp-build-tools/) (o NativeAOT usa o linker dele pra gerar o launcher).
A compilação com admin roda no `compilar-isolado.ps1`: numa cópia dos fontes, dentro de uma pasta só de admin, sem os pacotes, imports de MSBuild e variáveis de ambiente do seu usuário (baixa os pacotes NuGet a cada instalação).
Pra remover: `.\desinstalar.ps1` (ou `-ApagarDados` pra apagar configurações e logs).

## Gerar o instalador

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\empacotar.ps1
```

Roda **sem** admin. Gera `artifacts\SunsetBoost-Setup-<versão>.exe` e o `.sha256` dele. Precisa do .NET 10 SDK, do C++ das Ferramentas de Build do Visual Studio e do Inno Setup 6 (`winget install JRSoftware.InnoSetup`).

O app sai self-contained (leva o .NET junto, ~50 MB no instalador). As DLLs nativas do WPF ficam ao lado do `.exe` em Program Files, e não extraídas pra `%TEMP%`, que qualquer programa do usuário altera.

As imagens do instalador saem do `installer\gerar-imagens.ps1`.

## Lançar uma versão

1. Atualize `<Version>` no `Directory.Build.props` e escreva a seção da versão no `CHANGELOG.md`.
2. Faça o commit no `main` e crie a tag:
   ```powershell
   git tag v0.2.0
   git push origin v0.2.0
   ```
3. O workflow **Release** testa, gera o instalador e cria a release como **rascunho**, com o instalador, o `.sha256` e as notas do CHANGELOG. Versões `0.x` saem como pré-lançamento.
4. Revise o rascunho na página de Releases e clique em **Publish release**.

O instalador publicado sai sempre do GitHub Actions, nunca de um PC de desenvolvimento.

## Segurança

### Lista 🔒 Nunca fecha

Fixa no código (`SafetyRules.cs`); nenhuma configuração passa por cima:
processos de sessão 0 (serviços), tudo dentro de `C:\Windows`, processos essenciais, antivírus/Defender,
anti-cheats (Vanguard, EAC, BattlEye, FACEIT…), drivers de vídeo/áudio, qualquer jogo conhecido,
o próprio app e quem o abriu, e processos cujo caminho não dá pra ler.
Jogos conhecidos, anti-cheats e o Riot Client nem são lidos (`HandsOff.cs`): o app não abre acesso a esses processos, nem pra medir memória, e não muda a prioridade de jogos com anti-cheat.
O executor confere tudo de novo, processo por processo, logo antes de agir, e não fecha nada se o PID tiver passado pra outro processo.

### Admin sem brecha pra outros programas

Nenhum programa rodando sem admin consegue usar o Sunset Boost pra ganhar admin:

- **Só o launcher pede admin.** Ele é nativo (NativeAOT) e abre a interface com as variáveis de ambiente do sistema, sem as do usuário (`HKCU\Environment`). Um app .NET aberto com admin direto aceitaria DLL via `DOTNET_ROOT`, `CORECLR_PROFILER_PATH` e afins, que qualquer programa do usuário grava. Os atalhos e a tarefa de logon abrem o launcher; a tarefa recusa qualquer outro .exe.
- **Com admin, os dados ficam numa pasta protegida** (`%ProgramData%\SunsetBoost`, só administradores gravam). Uma pasta plantada ali antes (dono que não é admin, ou link) é renomeada pro lado e recriada. Assim ninguém troca o log ou o `settings.json` por um link pra fazer o app gravar ou apagar arquivos do sistema.
- **"Abrir junto com o Windows"** usa uma tarefa agendada com admin, que só aceita o launcher instalado em Program Files.

## Fases

- [x] 0 · Fundação (solução, admin, configurações, log, modo simulação)
- [x] 1 · Mockup visual (aprovado)
- [x] 2 · Limpeza (motor + testes + simulação no PC real)
- [x] 3 · Modo Jogo e restauração (falta o teste real como admin: `modojogo testar`)
- [x] 4 · Interface completa (fontes do Windows: Impact + Bahnschrift)
- [x] 5 · Perfis e automação (detecção automática do jogo + atalho Ctrl+Shift+B; perfil personalizado ainda não)
- [x] 6 · Instalação (.exe único, instalador, início com o Windows, instância única)
- [x] 7 · Distribuição (instalador Inno Setup, release pelo GitHub Actions)
- [ ] 8 · Teste real com o Valorant
