using System.Runtime.InteropServices;

namespace SunsetBoost.Core.Monitoring;

/// <summary>
/// Uso de CPU (GetSystemTimes) e GPU (contador "GPU Engine" do Windows, o mesmo do Gerenciador de Tarefas).
/// Chamar <see cref="Sample"/> uma vez por segundo.
/// </summary>
public sealed class SystemMonitor : IDisposable
{
    private long _idle, _kernel, _user;
    private IntPtr _query, _gpuCounter;
    private bool _gpuOk;

    public SystemMonitor()
    {
        GetSystemTimes(out _idle, out _kernel, out _user);
        try
        {
            _gpuOk = PdhOpenQueryW(null, IntPtr.Zero, out _query) == 0
                     && PdhAddEnglishCounterW(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _gpuCounter) == 0
                     && PdhCollectQueryData(_query) == 0;
        }
        catch { _gpuOk = false; }
    }

    /// <summary>(CPU %, GPU % ou null se não deu pra ler). Sem GPU: o contador da GPU é por processo, então com jogo aberto o app pula.</summary>
    public (double Cpu, double? Gpu) Sample(bool withGpu = true) => (ReadCpu(), withGpu ? ReadGpu() : null);

    private double ReadCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        long dIdle = idle - _idle, dTotal = (kernel - _kernel) + (user - _user); // kernel já inclui idle
        (_idle, _kernel, _user) = (idle, kernel, user);
        return dTotal <= 0 ? 0 : Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
    }

    private double? ReadGpu()
    {
        if (!_gpuOk) return null;
        try
        {
            if (PdhCollectQueryData(_query) != 0) return null;
            uint size = 0, count = 0;
            int status = PdhGetFormattedCounterArrayW(_gpuCounter, PDH_FMT_DOUBLE, ref size, ref count, IntPtr.Zero);
            if (status != PDH_MORE_DATA || size == 0) return 0;
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(_gpuCounter, PDH_FMT_DOUBLE, ref size, ref count, buf) != 0) return null;
                double sum = 0;
                int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buf + i * itemSize);
                    if (item.CStatus == 0) sum += item.DoubleValue;
                }
                return Math.Clamp(sum, 0, 100);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr Name;
        public uint CStatus;
        private uint _pad;
        public double DoubleValue;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr items);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);
}
