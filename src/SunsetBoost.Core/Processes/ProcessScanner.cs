using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static SunsetBoost.Core.Native.NativeMethods;

namespace SunsetBoost.Core.Processes;

/// <summary>Um processo rodando. <see cref="Name"/> é o nome do .exe em minúsculas, sem extensão.</summary>
public sealed record ProcessInfo(
    int Pid,
    int ParentPid,
    string Name,
    string? ImagePath,
    int SessionId,
    long PrivateBytes,
    long CreationTime)
{
    public string ExeFileName => Name + ".exe";
}

public interface IProcessSource
{
    IReadOnlyList<ProcessInfo> Snapshot();
}

/// <summary>Lê todos os processos com nome, caminho, pai, sessão e RAM privada (a coluna "Memória" do Gerenciador de Tarefas).</summary>
public sealed class ProcessScanner : IProcessSource
{
    public IReadOnlyList<ProcessInfo> Snapshot() => Snapshot(null);

    /// <summary>Só os processos cujo nome passa no filtro. Os outros nem são abertos (o nome vem da lista do Windows).</summary>
    public IReadOnlyList<ProcessInfo> Snapshot(Func<string, bool>? only)
    {
        var result = new List<ProcessInfo>(320);
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == new IntPtr(-1)) return result;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref e)) return result;
            do
            {
                if (only is null || only(NormalizeName(e.szExeFile))) result.Add(Describe(e));
                e.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>();
            } while (Process32NextW(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return result;
    }

    private static ProcessInfo Describe(PROCESSENTRY32W e)
    {
        uint pid = e.th32ProcessID;
        string name = NormalizeName(e.szExeFile);
        ProcessIdToSessionId(pid, out var session);

        string? path = null;
        long priv = 0, created = 0;
        // Jogo, anti-cheat e launcher do jogo: só o nome (que vem da lista do Windows). Nenhum acesso ao processo,
        // nem de leitura: anti-cheats como o Vanguard desconfiam de qualquer programa que abra o processo do jogo.
        if (HandsOff.IsHandsOff(name))
            return new ProcessInfo((int)pid, (int)e.th32ParentProcessID, name, null, (int)session, 0, 0);

        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (QueryFullProcessImageNameW(h, 0, sb, ref size)) path = sb.ToString();

                if (GetProcessMemoryInfo(h, out var mem, (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>()))
                    priv = (long)(ulong)mem.PrivateWorkingSetSize;

                if (GetProcessTimes(h, out var c, out _, out _, out _)) created = c;
            }
            finally { CloseHandle(h); }
        }
        return new ProcessInfo((int)pid, (int)e.th32ParentProcessID, name, path, (int)session, priv, created);
    }

    public static string NormalizeName(string exe)
    {
        var n = exe.Trim().ToLowerInvariant();
        return n.EndsWith(".exe") ? n[..^4] : n;
    }

    /// <summary>Nome amigável do .exe (descrição do arquivo), com cache.</summary>
    public static string FriendlyName(ProcessInfo p)
    {
        if (p.ImagePath is null) return p.ExeFileName;
        lock (FriendlyCache)
        {
            if (FriendlyCache.TryGetValue(p.ImagePath, out var cached)) return cached;
            string name = p.ExeFileName;
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(p.ImagePath);
                // Só a descrição do arquivo; o "ProductName" costuma ser genérico ("Microsoft® Windows® Operating System")
                if (!string.IsNullOrWhiteSpace(vi.FileDescription)) name = vi.FileDescription.Trim();
            }
            catch { }
            FriendlyCache[p.ImagePath] = name;
            return name;
        }
    }

    private static readonly Dictionary<string, string> FriendlyCache = new(StringComparer.OrdinalIgnoreCase);
}
