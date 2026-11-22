using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SunsetBoost.Launcher;

/// <summary>
/// Abre um programa com um ambiente limpo (variáveis do sistema do HKLM mais a identidade do usuário tirada do token,
/// nada de HKCU\Environment) e só com argumentos permitidos. Também é compilado nos testes (SunsetBoost.Core.Tests).
/// </summary>
internal static class CleanLaunch
{
    public const string AppExe = "SunsetBoost.App.exe";

    /// <summary>Únicos argumentos repassados pro app com admin. O resto (ex.: "--captura arquivo") fica de fora.</summary>
    internal static readonly string[] AllowedArgs = { "--minimizado", "--simular" };

    /// <summary>Prefixos que o .NET usa pra achar o runtime, um profiler ou DLLs extras. Nem vindos do HKLM passam.</summary>
    internal static readonly string[] BlockedPrefixes = { "DOTNET_", "CORECLR_", "COR_", "COMPLUS_" };

    internal static IReadOnlyList<string> FilterArgs(IEnumerable<string> args) =>
        args.Select(a => a.Trim().ToLowerInvariant()).Where(a => AllowedArgs.Contains(a)).Distinct().ToList();

    internal static bool IsBlocked(string name) =>
        BlockedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Ambiente do app com admin: as variáveis do sistema mais as de identidade do usuário (<see cref="UserIdentity"/>).
    /// Nada vem do HKCU\Environment nem do ambiente de quem abriu o launcher.
    /// </summary>
    internal static SortedDictionary<string, string> CleanEnvironment()
    {
        var vars = SystemEnvironment();
        foreach (var (name, value) in UserIdentity()) vars[name] = value;
        return vars;
    }

    /// <summary>
    /// Perfil e nome do usuário. Sem isso o ambiente do sistema traz USERPROFILE=C:\Users\Default e USERNAME=SYSTEM, e o
    /// Windows resolveria %LOCALAPPDATA% e as outras pastas do usuário no perfil errado. Os valores vêm do token e do
    /// ProfileList do HKLM, nunca de variáveis que o usuário (ou um programa dele) consegue mudar.
    /// </summary>
    internal static Dictionary<string, string> UserIdentity()
    {
        var profile = UserProfileDirectory();
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["USERPROFILE"] = profile,
            ["APPDATA"] = Path.Combine(profile, "AppData", "Roaming"),
            ["LOCALAPPDATA"] = Path.Combine(profile, "AppData", "Local"),
            ["USERNAME"] = Environment.UserName,
            ["USERDOMAIN"] = Environment.UserDomainName,
        };
    }

    /// <summary>Pasta do perfil do dono do token (GetUserProfileDirectory lê o ProfileList do HKLM).</summary>
    private static string UserProfileDirectory()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            int size = 0;
            GetUserProfileDirectoryW(token, IntPtr.Zero, ref size); // primeiro pergunta o tamanho
            var buffer = Marshal.AllocHGlobal(Math.Max(size, 1) * sizeof(char));
            try
            {
                if (!GetUserProfileDirectoryW(token, buffer, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return Marshal.PtrToStringUni(buffer) ?? throw new InvalidOperationException("perfil do usuário vazio");
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(token); }
    }

    /// <summary>O launcher está com admin? (TokenElevation)</summary>
    internal static bool IsElevated()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out var token)) return false;
        try { return GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _) && elevated != 0; }
        finally { CloseHandle(token); }
    }

    /// <summary>
    /// Variáveis só do sistema: CreateEnvironmentBlock sem token lê o HKLM e ignora o HKCU\Environment (onde qualquer
    /// programa do usuário grava) e o ambiente de quem abriu o launcher. O PATH também fica só o do sistema.
    /// </summary>
    internal static SortedDictionary<string, string> SystemEnvironment()
    {
        if (!CreateEnvironmentBlock(out var block, IntPtr.Zero, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var p = block; ;)
            {
                var entry = Marshal.PtrToStringUni(p);
                if (string.IsNullOrEmpty(entry)) break;
                p += (entry.Length + 1) * sizeof(char);
                int eq = entry.IndexOf('=', 1); // variáveis como "=C:" começam com '='
                if (eq <= 0) continue;
                var name = entry[..eq];
                if (!IsBlocked(name)) vars[name] = entry[(eq + 1)..];
            }
            return vars;
        }
        finally { DestroyEnvironmentBlock(block); }
    }

    /// <summary>Bloco no formato do CreateProcess: "NOME=valor\0" em ordem alfabética, terminado por mais um "\0".</summary>
    internal static string BuildBlock(IEnumerable<KeyValuePair<string, string>> vars)
    {
        var sb = new StringBuilder();
        foreach (var (name, value) in vars.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append(name).Append('=').Append(value).Append('\0');
        return sb.Append('\0').ToString();
    }

    /// <summary>Abre o programa. Com <paramref name="waitForExit"/> espera e devolve o código de saída (testes).</summary>
    internal static int Start(string exe, string arguments, IEnumerable<KeyValuePair<string, string>> environment,
        string workingDir, bool waitForExit, bool noWindow = false)
    {
        // CreateProcessW pode escrever na linha de comando: precisa de um buffer próprio
        var commandLine = Marshal.StringToHGlobalUni($"\"{exe}\"" + (arguments.Length > 0 ? " " + arguments : ""));
        var env = Marshal.StringToHGlobalUni(BuildBlock(environment));
        try
        {
            var si = new STARTUPINFOW { cb = Marshal.SizeOf<STARTUPINFOW>() };
            uint flags = CREATE_UNICODE_ENVIRONMENT | (noWindow ? CREATE_NO_WINDOW : 0);
            if (!CreateProcessW(exe, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags, env, workingDir, ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!waitForExit) return 0;
                WaitForSingleObject(pi.hProcess, 60_000);
                return GetExitCodeProcess(pi.hProcess, out var code) ? (int)code : -1;
            }
            finally
            {
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(commandLine);
            Marshal.FreeHGlobal(env);
        }
    }

    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("userenv.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("userenv.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetUserProfileDirectoryW(IntPtr token, IntPtr profileDir, ref int size);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CreateProcessW(string applicationName, IntPtr commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
        ref STARTUPINFOW startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CloseHandle(IntPtr handle);
}
