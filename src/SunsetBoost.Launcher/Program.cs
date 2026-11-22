using System.Runtime.InteropServices;

// Toda DLL do Windows usada pelo launcher vem do System32 (nunca da pasta atual nem do PATH)
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace SunsetBoost.Launcher;

/// <summary>
/// SunsetBoost.exe: a única parte do Sunset Boost que pede admin (manifesto). Abre o SunsetBoost.App.exe, que fica na
/// mesma pasta em Program Files, com um ambiente limpo (<see cref="CleanLaunch.CleanEnvironment"/>) e só os argumentos permitidos.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            // Sem admin (ex.: __COMPAT_LAYER=RunAsInvoker) o app abriria sem admin e chamaria o launcher de novo, em loop
            if (!CleanLaunch.IsElevated())
            {
                Error("O Sunset Boost precisa ser aberto como administrador.");
                return 3;
            }

            var dir = Path.GetDirectoryName(Environment.ProcessPath)
                      ?? throw new InvalidOperationException("não deu pra saber onde o launcher está");
            var app = Path.Combine(dir, CleanLaunch.AppExe);
            if (!File.Exists(app))
            {
                Error($"Não achei o {CleanLaunch.AppExe} em {dir}. Instale de novo (instalar.ps1).");
                return 2;
            }

            CleanLaunch.Start(app, string.Join(' ', CleanLaunch.FilterArgs(args)), CleanLaunch.CleanEnvironment(), dir, waitForExit: false);
            return 0;
        }
        catch (Exception ex)
        {
            Error("Não consegui abrir o Sunset Boost: " + ex.Message);
            return 1;
        }
    }

    private static void Error(string text) => MessageBoxW(IntPtr.Zero, text, "Sunset Boost", MB_OK_ICONERROR);

    private const uint MB_OK_ICONERROR = 0x00000010;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
