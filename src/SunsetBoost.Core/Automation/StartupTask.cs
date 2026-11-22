using System.Diagnostics;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using SunsetBoost.Core.Infrastructure;

namespace SunsetBoost.Core.Automation;

/// <summary>
/// "Abrir junto com o Windows" via Agendador de Tarefas (o único jeito de um app que precisa de admin
/// abrir no logon sem a pergunta do UAC). A tarefa roda com o usuário atual, só quando ele faz logon.
/// </summary>
public static class StartupTask
{
    public const string TaskName = "Sunset Boost";

    /// <summary>
    /// O que os atalhos e a tarefa abrem: o launcher nativo instalado ao lado do app. Nunca o SunsetBoost.App.exe direto:
    /// ele é .NET e, aberto com admin pelo Agendador, receberia as variáveis de ambiente do usuário (DOTNET_ROOT,
    /// CORECLR_PROFILER_PATH...), que qualquer programa sem admin grava e que carregam DLL no processo.
    /// </summary>
    public const string LauncherExe = "SunsetBoost.exe";

    /// <summary>Caminho do launcher na pasta do app (null se não der pra saber onde o app está).</summary>
    public static string? LauncherPathFor(string? appExePath) =>
        string.IsNullOrWhiteSpace(appExePath) ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(appExePath))!, LauncherExe);

    public static bool IsEnabled() => Run($"/Query /TN \"{TaskName}\"", out _) == 0;

    /// <summary>Motivo pra recusar o .exe da tarefa; null = pode.</summary>
    public static string? RejectReason(string exePath) =>
        !IsSafeLocation(exePath) ? "instale o app em Program Files primeiro (instalar.ps1)"
        : !Path.GetFileName(exePath).Equals(LauncherExe, StringComparison.OrdinalIgnoreCase) ? $"a tarefa só pode abrir o launcher ({LauncherExe})"
        : !File.Exists(exePath) ? $"o launcher ({LauncherExe}) não está instalado (instalar.ps1)"
        : null;

    /// <summary>
    /// Por segurança, só aceita um .exe dentro de Program Files: uma tarefa com admin apontando pra uma pasta
    /// que qualquer programa do usuário consegue alterar seria uma porta aberta pra malware.
    /// </summary>
    public static bool IsSafeLocation(string exePath)
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd('\\') + "\\";
        return Path.GetFullPath(exePath).StartsWith(pf, StringComparison.OrdinalIgnoreCase);
    }

    public static bool Enable(string exePath, out string error)
    {
        error = "";
        if (!Elevation.IsElevated) { error = "precisa de administrador"; return false; }
        if (RejectReason(exePath) is { } why) { error = why; return false; }

        // Aberto pelo launcher, o %TEMP% é o do sistema (C:\Windows\Temp). Mesmo assim, se fosse o do usuário,
        // um programa sem admin poderia trocar o XML entre a gravação e o schtasks
        // e registrar uma tarefa com admin rodando outra coisa. Por isso o arquivo nasce só com permissão de
        // Administradores/SYSTEM e fica aberto (sem compartilhar escrita nem exclusão) até o schtasks terminar.
        var xmlFile = Path.Combine(Path.GetTempPath(), $"sunsetboost-task-{Guid.NewGuid():N}.xml");
        try
        {
            var bytes = System.Text.Encoding.Unicode.GetPreamble()
                .Concat(System.Text.Encoding.Unicode.GetBytes(BuildXml(exePath, WindowsIdentity.GetCurrent().Name))).ToArray();
            using (var fs = CreateAdminOnlyFile(xmlFile)) fs.Write(bytes);

            // Reabre só pra leitura, sem compartilhar escrita nem exclusão: daqui até o fim do schtasks ninguém troca o arquivo.
            // E confere que é exatamente o que foi gravado (se alguém apagou e recriou no meio, aborta).
            using (var guard = new FileStream(xmlFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var current = new byte[bytes.Length + 1];
                int read = 0, n;
                while (read < current.Length && (n = guard.Read(current, read, current.Length - read)) > 0) read += n;
                if (read != bytes.Length || !current.AsSpan(0, read).SequenceEqual(bytes))
                {
                    error = "o arquivo temporário da tarefa foi alterado por outro programa; nada foi criado";
                    AppLog.Warn("Início com o Windows: XML temporário alterado antes do schtasks — abortado");
                    return false;
                }
                if (Run($"/Create /TN \"{TaskName}\" /XML \"{xmlFile}\" /F", out var output) != 0) { error = output.Trim(); return false; }
            }
            AppLog.Info($"Início com o Windows: ligado ({exePath})");
            return true;
        }
        finally { try { File.Delete(xmlFile); } catch { } }
    }

    private static FileStream CreateAdminOnlyFile(string path)
    {
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        // CreateNew: se alguém já plantou um arquivo (ou link) com esse nome, falha em vez de usar o dele.
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None,
            4096, FileOptions.None, acl);
    }

    /// <summary>Caminho completo em System32: nunca um schtasks.exe/powercfg.exe da pasta atual ou do PATH.</summary>
    internal static string SystemTool(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    public static bool Disable(out string error)
    {
        error = "";
        if (!IsEnabled()) return true;
        if (Run($"/Delete /TN \"{TaskName}\" /F", out var output) != 0) { error = output.Trim(); return false; }
        AppLog.Info("Início com o Windows: desligado");
        return true;
    }

    internal static string BuildXml(string exePath, string user) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>Abre o Sunset Boost perto do relógio ao entrar no Windows.</Description></RegistrationInfo>
          <Triggers>
            <LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId><Delay>PT15S</Delay></LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author"><UserId>{SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec><Command>{SecurityElement.Escape(exePath)}</Command><Arguments>--minimizado</Arguments></Exec>
          </Actions>
        </Task>
        """;

    private static int Run(string args, out string output)
    {
        var psi = new ProcessStartInfo(SystemTool("schtasks.exe"), args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(10_000);
        return p.ExitCode;
    }
}
