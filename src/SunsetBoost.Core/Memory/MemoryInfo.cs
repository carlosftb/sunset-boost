using System.Runtime.InteropServices;
using SunsetBoost.Core.Infrastructure;
using static SunsetBoost.Core.Native.NativeMethods;

namespace SunsetBoost.Core.Memory;

/// <summary>Foto da memória num instante.</summary>
/// <param name="AvailableBytes">"Disponível" do Gerenciador de Tarefas (livre + standby).</param>
/// <param name="StandbyBytes">Cache standby (null se não deu pra ler — precisa de admin).</param>
/// <param name="FreeBytes">Memória zerada/livre de verdade (null se não deu pra ler).</param>
public sealed record MemorySnapshot(ulong TotalBytes, ulong AvailableBytes, ulong? StandbyBytes, ulong? FreeBytes)
{
    public ulong UsedBytes => TotalBytes - AvailableBytes;
    public double UsedPercent => TotalBytes == 0 ? 0 : 100.0 * UsedBytes / TotalBytes;
}

public static class MemoryInfo
{
    private const ulong PageSize = 4096;

    public static MemorySnapshot Read()
    {
        var st = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref st);

        ulong? standby = null, free = null;
        if (NtQuerySystemInformation(SystemMemoryListInformation, out var list,
                Marshal.SizeOf<SYSTEM_MEMORY_LIST_INFORMATION>(), out _) == 0)
        {
            standby = list.PageCountByPriority.Aggregate(0UL, (s, v) => s + (ulong)v) * PageSize;
            free = ((ulong)list.FreePageCount + (ulong)list.ZeroPageCount) * PageSize;
        }
        return new MemorySnapshot(st.ullTotalPhys, st.ullAvailPhys, standby, free);
    }
}

/// <summary>
/// Esvazia a standby list (a mesma técnica do ISLC / RAMMap "Empty Standby List").
/// Precisa de admin: ativa o privilégio SeProfileSingleProcessPrivilege e chama NtSetSystemInformation.
/// </summary>
public static class StandbyListCleaner
{
    public static bool TryPurge(out string error)
    {
        error = "";
        if (!Elevation.IsElevated) { error = "precisa rodar como administrador"; return false; }
        if (!EnablePrivilege("SeProfileSingleProcessPrivilege")) { error = "não consegui ativar o privilégio"; return false; }

        int command = MemoryPurgeStandbyList;
        int status = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
        if (status != 0) { error = $"NTSTATUS 0x{status:X8}"; return false; }
        return true;
    }

    internal static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token)) return false;
        try
        {
            if (!LookupPrivilegeValueW(null, name, out var luid)) return false;
            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)
                   && Marshal.GetLastWin32Error() == 0;
        }
        finally { CloseHandle(token); }
    }
}
