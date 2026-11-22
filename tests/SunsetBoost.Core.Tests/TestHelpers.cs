using System.Diagnostics;
using SunsetBoost.Core.Cleanup;
using SunsetBoost.Core.Memory;
using SunsetBoost.Core.Processes;

namespace SunsetBoost.Core.Tests;

/// <summary>Pastas temporárias e junctions de verdade (mklink /J não precisa de admin).</summary>
internal static class Fs
{
    public static string NewPath() => Path.Combine(Path.GetTempPath(), "sb-teste-" + Guid.NewGuid().ToString("N")[..8]);

    public static string NewDir()
    {
        var path = NewPath();
        Directory.CreateDirectory(path);
        return path;
    }

    public static void Junction(string link, string target)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var error = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit(10_000);
        if (p.ExitCode != 0) throw new InvalidOperationException("mklink /J falhou: " + error);
    }

    /// <summary>Apaga só o link: Directory.Delete sem recursão não entra na pasta de destino.</summary>
    public static void RemoveLink(string link)
    {
        if (Directory.Exists(link)) Directory.Delete(link);
    }

    public static void DeleteDir(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}

internal static class T
{
    public const long MB = 1024 * 1024;

    public static ProcessInfo P(int pid, string name, int session = 1, string? path = "default", int parent = 1000, long mb = 100) =>
        new(pid, parent, name, path == "default" ? $@"C:\Program Files\{name}\{name}.exe" : path, session, mb * MB, pid * 10L);

    public static SafetyContext Ctx(GameProfile? profile = null) =>
        new() { Profile = profile ?? GameProfiles.Valorant, WindowsDir = @"C:\Windows" };
}

/// <summary>Sistema falso: registra cada ação e simula processos que fecham (ou não) sozinhos.</summary>
internal sealed class FakeSystemActions : ISystemActions
{
    public bool IsSimulation => false;
    public List<int> CloseRequested { get; } = new();
    public List<int> Killed { get; } = new();
    public HashSet<int> Stubborn { get; } = new();   // ignoram o pedido de fechar
    public HashSet<int> Unkillable { get; } = new(); // falham ao forçar
    public HashSet<int> NoWindow { get; } = new();   // processo de fundo: não tem janela pra receber o pedido
    public HashSet<int> KeepsWindow { get; } = new(); // teimoso que mantém a janela (ex.: "salvar alterações?")
    private readonly HashSet<int> _dead = new();
    private readonly HashSet<int> _hidden = new();   // teimoso que escondeu a janela (foi pra bandeja)
    public int Purges { get; private set; }

    public IEnumerable<int> Touched => CloseRequested.Concat(Killed);

    public int RequestClose(ProcessInfo p)
    {
        CloseRequested.Add(p.Pid);
        if (NoWindow.Contains(p.Pid)) return 0;
        if (!Stubborn.Contains(p.Pid)) _dead.Add(p.Pid);
        else if (!KeepsWindow.Contains(p.Pid)) _hidden.Add(p.Pid);
        return 1;
    }
    public int CountWindows(ProcessInfo p) =>
        _dead.Contains(p.Pid) || NoWindow.Contains(p.Pid) || _hidden.Contains(p.Pid) ? 0 : 1;
    public bool IsAlive(ProcessInfo p) => !_dead.Contains(p.Pid);
    public bool Kill(ProcessInfo p, out string error)
    {
        error = "";
        if (Unkillable.Contains(p.Pid)) { error = "acesso negado"; return false; }
        Killed.Add(p.Pid); _dead.Add(p.Pid); return true;
    }
    public bool PurgeStandbyList(out string error) { error = ""; Purges++; return true; }
    public MemorySnapshot ReadMemory() => new(16UL << 30, 2UL << 30, null, null);
    public void Sleep(TimeSpan d) => Thread.Sleep(5);
}
