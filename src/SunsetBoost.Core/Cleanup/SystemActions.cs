using SunsetBoost.Core.Infrastructure;
using SunsetBoost.Core.Memory;
using SunsetBoost.Core.Processes;
using static SunsetBoost.Core.Native.NativeMethods;

namespace SunsetBoost.Core.Cleanup;

/// <summary>
/// Tudo que a limpeza faz de verdade no Windows passa por aqui.
/// Trocar a implementação = modo simulação (ou testes) sem mudar a lógica.
/// </summary>
public interface ISystemActions
{
    bool IsSimulation { get; }
    /// <summary>Pede educadamente pro programa fechar (WM_CLOSE nas janelas dele). Devolve quantas janelas receberam o pedido.</summary>
    int RequestClose(ProcessInfo p);
    /// <summary>Quantas janelas visíveis (principais) o processo tem agora.</summary>
    int CountWindows(ProcessInfo p);
    bool IsAlive(ProcessInfo p);
    bool Kill(ProcessInfo p, out string error);
    bool PurgeStandbyList(out string error);
    MemorySnapshot ReadMemory();
    void Sleep(TimeSpan duration);
}

public sealed class RealSystemActions : ISystemActions
{
    public bool IsSimulation => false;

    public int RequestClose(ProcessInfo p) =>
        MainWindows(p).Count(hwnd => PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero));

    public int CountWindows(ProcessInfo p) => MainWindows(p).Count;

    /// <summary>Janelas de topo visíveis e sem "dono" (as que aparecem na barra de tarefas).</summary>
    private static List<IntPtr> MainWindows(ProcessInfo p)
    {
        var list = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == p.Pid && IsWindowVisible(hwnd) && GetWindow(hwnd, GW_OWNER) == IntPtr.Zero) list.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public bool IsAlive(ProcessInfo p)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, (uint)p.Pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            if (!IsSameProcess(h, p)) return false; // PID reaproveitado: o original já morreu
            return WaitForSingleObject(h, 0) != WAIT_OBJECT_0;
        }
        finally { CloseHandle(h); }
    }

    public bool Kill(ProcessInfo p, out string error)
    {
        error = "";
        var h = OpenProcess(PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)p.Pid);
        if (h == IntPtr.Zero)
        {
            error = $"sem permissão (erro {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})";
            return false;
        }
        try
        {
            if (!IsSameProcess(h, p)) { error = "PID reaproveitado por outro processo — ignorado"; return false; }
            if (!TerminateProcess(h, 1)) { error = $"TerminateProcess falhou ({System.Runtime.InteropServices.Marshal.GetLastWin32Error()})"; return false; }
            return true;
        }
        finally { CloseHandle(h); }
    }

    private static bool IsSameProcess(IntPtr h, ProcessInfo p) =>
        p.CreationTime == 0 || (GetProcessTimes(h, out var c, out _, out _, out _) && c == p.CreationTime);

    public bool PurgeStandbyList(out string error) => StandbyListCleaner.TryPurge(out error);
    public MemorySnapshot ReadMemory() => MemoryInfo.Read();
    public void Sleep(TimeSpan duration) => Thread.Sleep(duration);
}

/// <summary>Modo simulação: registra o que SERIA feito e não toca em nada. Lê a memória de verdade (isso é inofensivo).</summary>
public sealed class SimulatedSystemActions : ISystemActions
{
    public bool IsSimulation => true;
    public List<string> Journal { get; } = new();

    public int RequestClose(ProcessInfo p) { Journal.Add($"pedir pra fechar {p.ExeFileName} (PID {p.Pid})"); return 1; }
    public int CountWindows(ProcessInfo p) => 0;
    public bool IsAlive(ProcessInfo p) => false;
    public bool Kill(ProcessInfo p, out string error) { error = ""; Journal.Add($"forçar {p.ExeFileName} (PID {p.Pid})"); return true; }
    public bool PurgeStandbyList(out string error) { error = ""; Journal.Add("limpar a standby list"); return true; }
    public MemorySnapshot ReadMemory() => MemoryInfo.Read();
    public void Sleep(TimeSpan duration) { }
}
