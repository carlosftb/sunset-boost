using System.Runtime.InteropServices;
using static SunsetBoost.Core.Native.NativeMethods;

namespace SunsetBoost.Core.Edge;

/// <summary>Lê a linha de comando de outro processo (pra saber se um msedge.exe é aba, extensão, GPU...).</summary>
public static class ProcessCommandLine
{
    private const int ProcessCommandLineInformation = 60;

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returnLength);

    public static string? Read(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            NtQueryInformationProcess(h, ProcessCommandLineInformation, IntPtr.Zero, 0, out int size);
            if (size <= 0 || size > 1 << 20) return null;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, size, out _) != 0) return null;
                var us = Marshal.PtrToStructure<UNICODE_STRING>(buf);
                return us.Buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseHandle(h); }
    }
}
