using System.Diagnostics;
using System.IO;
using System.Windows;
using SunsetBoost.Core;
using SunsetBoost.Core.Automation;
using SunsetBoost.Core.Infrastructure;

namespace SunsetBoost.App;

public partial class App : Application
{
    public static AppPaths Paths { get; private set; } = null!;
    public static SettingsStore SettingsStore { get; private set; } = null!;
    public static AppSettings Settings { get; private set; } = null!;
    public static BoostService Boost { get; private set; } = null!;

    /// <summary>"--captura aba arquivo.png": abre, tira um print da aba e fecha (usado pra conferir o visual). Só no build Debug.</summary>
    public static (string Tab, string File)? Capture { get; private set; }

    /// <summary>"--minimizado": abre direto perto do relógio (usado ao iniciar com o Windows).</summary>
    public static bool StartMinimized { get; private set; }

    private static bool _persistedSimulation;
    private static Mutex? _singleInstance;
    private static EventWaitHandle? _showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        var args = e.Args;
        bool Has(string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
#if DEBUG
        // Só no build de desenvolvimento: grava um arquivo no caminho recebido, o que não pode existir num .exe que roda com admin
        int i = Array.FindIndex(args, a => a.Equals("--captura", StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 2 < args.Length) Capture = (args[i + 1], args[i + 2]);
#endif
        StartMinimized = Has("--minimizado");

        // Instalado e aberto sem admin (ex.: duplo clique no SunsetBoost.App.exe): passa pelo launcher, que pede o UAC
        // e abre o app com admin e um ambiente limpo
        if (Capture is null && !Elevation.IsElevated && RelaunchThroughLauncher(args))
        {
            Shutdown();
            return;
        }

        // Instância única: se já está aberto, só pede pra janela existente aparecer
        if (Capture is null)
        {
            try
            {
                _singleInstance = new Mutex(true, @"Local\SunsetBoost.SingleInstance", out bool first);
                _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SunsetBoost.Show");
                if (!first)
                {
                    if (!StartMinimized) _showSignal.Set();
                    Shutdown();
                    return;
                }
            }
            catch (UnauthorizedAccessException)
            {
                // A instância aberta tem admin e esta não: não dá nem pra pedir pra janela dela aparecer
                MessageBox.Show("O Sunset Boost já está aberto (ícone perto do relógio).", "Sunset Boost",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
            var listener = new Thread(() =>
            {
                while (_showSignal.WaitOne())
                    Dispatcher.Invoke(() => (MainWindow as MainWindow)?.BringToFront());
            }) { IsBackground = true, Name = "SunsetBoost.Show" };
            listener.Start();
        }

        // Com admin: %ProgramData%\SunsetBoost, protegida. Se não der pra deixar a pasta segura, não abre.
        try { Paths = new AppPaths().EnsureCreated(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Sunset Boost", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }
        AppLog.Configure(Paths.LogsDir);
        foreach (var note in Paths.Notes) AppLog.Warn(note);

        SettingsStore = new SettingsStore(Paths.SettingsFile);
        Settings = SettingsStore.Load();
        _persistedSimulation = Settings.SimulationMode;
        if (Has("--simular")) Settings.SimulationMode = true; // só nesta execução: SaveSettings não grava isso


        Boost = new BoostService(Paths, Settings);

        AppLog.Info($"Sunset Boost iniciado | admin={Elevation.IsElevated} | simulação={Settings.SimulationMode} | perfil={Settings.ActiveProfileId} | dados={Paths.Root}");

        DispatcherUnhandledException += (_, ex) =>
        {
            AppLog.Error("Erro não tratado na interface", ex.Exception);
            MessageBox.Show(ex.Exception.Message, "Sunset Boost", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            AppLog.Error("Erro fatal", ex.ExceptionObject as Exception);

        base.OnStartup(e);
        new MainWindow().Show();
    }

    /// <summary>
    /// Sem admin, de dentro da instalação: abre o launcher (SunsetBoost.exe, na mesma pasta), que pede o UAC e abre o app
    /// com admin e um ambiente limpo. Devolve false fora da instalação (desenvolvimento) ou com o UAC negado: aí o app
    /// segue sem admin, no modo limitado.
    /// </summary>
    private static bool RelaunchThroughLauncher(string[] args)
    {
        var launcher = StartupTask.LauncherPathFor(Environment.ProcessPath);
        if (launcher is null || !StartupTask.IsSafeLocation(launcher) || !File.Exists(launcher)) return false;
        var passThrough = args.Where(a => a.Equals("--minimizado", StringComparison.OrdinalIgnoreCase)
                                          || a.Equals("--simular", StringComparison.OrdinalIgnoreCase));
        try
        {
            // UseShellExecute: o manifesto do launcher pede admin e o Windows mostra o UAC
            Process.Start(new ProcessStartInfo(launcher, string.Join(" ", passThrough))
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(launcher)!,
            });
            return true;
        }
        catch (System.ComponentModel.Win32Exception) { return false; } // UAC negado
    }

    public static void SaveSettings()
    {
        var runtimeSimulation = Settings.SimulationMode;
        try
        {
            Settings.SimulationMode = _persistedSimulation; // "--simular" vale só pra esta execução
            SettingsStore.Save(Settings);
        }
        catch (Exception ex) { AppLog.Error("Não consegui salvar as configurações", ex); }
        finally { Settings.SimulationMode = runtimeSimulation; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info("Sunset Boost encerrado");
        base.OnExit(e);
    }
}
