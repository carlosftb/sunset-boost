using System.Diagnostics;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SunsetBoost.Core.Tweaks;

// Interfaces pequenas pra cada parte do Windows que os ajustes mexem.
// Nos testes entram versões falsas; no app, as reais abaixo.

public interface IRegistryAccess
{
    /// <summary>Lê um DWORD do HKCU. null se não existir.</summary>
    int? GetDword(string path, string name);
    void SetDword(string path, string name, int value);
    void DeleteValue(string path, string name);
}

public interface IServiceAccess
{
    bool Exists(string name);
    bool IsRunning(string name);
    void Stop(string name);
    void Start(string name);
}

public interface IPowerPlans
{
    Guid GetActive();
    void SetActive(Guid scheme);
    Guid Duplicate(Guid source);
    void Delete(Guid scheme);
    bool Exists(Guid scheme);
}

public sealed class RealRegistry : IRegistryAccess
{
    public int? GetDword(string path, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(path);
        return k?.GetValue(name) is int v ? v : null;
    }

    public void SetDword(string path, string name, int value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(path, writable: true);
        k.SetValue(name, value, RegistryValueKind.DWord);
    }

    public void DeleteValue(string path, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(path, writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }
}

public sealed class RealServices : IServiceAccess
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public bool Exists(string name) => ServiceController.GetServices().Any(s => s.ServiceName.Equals(name, StringComparison.OrdinalIgnoreCase));

    public bool IsRunning(string name)
    {
        using var sc = new ServiceController(name);
        return sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
    }

    public void Stop(string name)
    {
        using var sc = new ServiceController(name);
        if (sc.Status == ServiceControllerStatus.Stopped) return;
        sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, Timeout);
    }

    public void Start(string name)
    {
        using var sc = new ServiceController(name);
        if (sc.Status == ServiceControllerStatus.Running) return;
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, Timeout);
    }
}

/// <summary>Planos de energia via powercfg (ferramenta oficial do Windows).</summary>
public sealed class RealPowerPlans : IPowerPlans
{
    private static readonly Regex GuidRx = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

    public Guid GetActive() => Guid.Parse(GuidRx.Match(Run("/getactivescheme")).Value);
    public void SetActive(Guid scheme) => Run($"/setactive {scheme}");
    public Guid Duplicate(Guid source) => Guid.Parse(GuidRx.Match(Run($"-duplicatescheme {source}")).Value);
    public void Delete(Guid scheme) => Run($"/delete {scheme}");
    public bool Exists(Guid scheme) => Run("/list").Contains(scheme.ToString(), StringComparison.OrdinalIgnoreCase);

    private static string Run(string args)
    {
        var psi = new ProcessStartInfo(Automation.StartupTask.SystemTool("powercfg.exe"), args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(10_000);
        if (p.ExitCode != 0) throw new InvalidOperationException($"powercfg {args} falhou: {output.Trim()}");
        return output;
    }
}
