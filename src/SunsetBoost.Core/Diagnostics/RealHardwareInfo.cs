using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SunsetBoost.Core.Diagnostics;

/// <summary>Lê o hardware pela API do Windows e pelo registro. Só leitura; nada aqui altera o PC.</summary>
public sealed class RealHardwareInfo : IHardwareInfo
{
    // ------------------------------------------------------------------ telas

    public IReadOnlyList<DisplayInfo> Displays()
    {
        var list = new List<DisplayInfo>();
        var dev = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dev, 0); i++, dev.cb = Marshal.SizeOf<DISPLAY_DEVICE>())
        {
            if ((dev.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
            var cur = NewDevMode();
            if (!EnumDisplaySettings(dev.DeviceName, ENUM_CURRENT_SETTINGS, ref cur)) continue;

            int max = (int)cur.dmDisplayFrequency;
            var mode = NewDevMode();
            for (int m = 0; EnumDisplaySettings(dev.DeviceName, m, ref mode); m++)
                if (mode.dmPelsWidth == cur.dmPelsWidth && mode.dmPelsHeight == cur.dmPelsHeight && mode.dmDisplayFrequency > max)
                    max = (int)mode.dmDisplayFrequency;

            bool primary = (dev.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;
            var name = primary ? "Tela principal" : $"Tela {list.Count + 1}";
            list.Add(new DisplayInfo(name, (int)cur.dmPelsWidth, (int)cur.dmPelsHeight, (int)cur.dmDisplayFrequency, max, primary));
        }
        return list;
    }

    private static DEVMODE NewDevMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    // ------------------------------------------------------------------ memória (SMBIOS)

    public IReadOnlyList<MemoryModule> Memory()
    {
        const uint RSMB = 0x52534D42; // 'RSMB'
        uint size = GetSystemFirmwareTable(RSMB, 0, null, 0);
        if (size == 0) return Array.Empty<MemoryModule>();
        var buf = new byte[size];
        if (GetSystemFirmwareTable(RSMB, 0, buf, size) == 0) return Array.Empty<MemoryModule>();
        return Smbios.MemoryModules(buf);
    }

    // ------------------------------------------------------------------ placas de vídeo

    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public IReadOnlyList<GpuInfo> Gpus()
    {
        var list = new List<GpuInfo>();
        // Placa que já saiu do PC continua no registro (com driver e tudo): só conta a que o Windows lista como adaptador agora
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dev = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dev, 0); i++, dev.cb = Marshal.SizeOf<DISPLAY_DEVICE>())
            if (!string.IsNullOrWhiteSpace(dev.DeviceString)) present.Add(dev.DeviceString.Trim());

        using var cls = Registry.LocalMachine.OpenSubKey(DisplayClass);
        if (cls is null) return list;
        foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
        {
            try
            {
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is not string name || string.IsNullOrWhiteSpace(name)) continue;
                if (k.GetValue("MatchingDeviceId") is null) continue;
                if (present.Count > 0 && !present.Contains(name.Trim())) continue;
                DateTime? date = k.GetValue("DriverDate") is string s
                    && DateTime.TryParseExact(s, "M-d-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
                if (list.Any(g => g.Name == name)) continue;
                list.Add(new GpuInfo(name.Trim(), date));
            }
            catch (System.Security.SecurityException) { }
        }
        return list;
    }

    // ------------------------------------------------------------------ energia, disco, Windows

    public bool? HasBattery() => GetSystemPowerStatus(out var s) ? (s.BatteryFlag & 128) == 0 && s.BatteryFlag != 255 : null;

    public bool OnBattery() => GetSystemPowerStatus(out var s) && s.ACLineStatus == 0;

    public (long Free, long Total)? SystemDrive()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory);
        if (root is null) return null;
        var d = new DriveInfo(root);
        return (d.AvailableFreeSpace, d.TotalSize);
    }

    public bool? MemoryIntegrity()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
        return k?.GetValue("Enabled") is int v ? v == 1 : null;
    }

    public bool? MouseAcceleration()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse");
        return k?.GetValue("MouseSpeed") is string v ? v.Trim() != "0" : null;
    }

    public bool? WindowedGameOptimizations()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Tweaks.GpuPreferences.RegistryPath);
        if (k?.GetValue(Tweaks.GpuPreferences.GlobalValue) is not string s) return null;
        var v = Tweaks.GpuPreferences.ParseTokens(s).GetValueOrDefault("SwapEffectUpgradeEnable");
        return v is null ? null : v == "1";
    }

    // ------------------------------------------------------------------ API do Windows

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    private const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public int dmDisplayFlags;
        public uint dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string device, int modeNum, ref DEVMODE mode);

    [DllImport("kernel32.dll")]
    private static extern uint GetSystemFirmwareTable(uint provider, uint tableId, byte[]? buffer, uint size);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}

/// <summary>Leitura da tabela SMBIOS (padrão DMTF DSP0134), só a parte dos pentes de memória (tipo 17).</summary>
internal static class Smbios
{
    private static string TypeName(byte t) => t switch
    {
        0x18 => "DDR3", 0x1A => "DDR4", 0x1B => "LPDDR", 0x1C => "LPDDR2", 0x1D => "LPDDR3", 0x1E => "LPDDR4",
        0x22 => "DDR5", 0x23 => "LPDDR5", _ => "RAM",
    };

    /// <param name="raw">O que o GetSystemFirmwareTable('RSMB') devolve: 8 bytes de cabeçalho + a tabela.</param>
    public static List<MemoryModule> MemoryModules(byte[] raw)
    {
        var list = new List<MemoryModule>();
        if (raw.Length < 8) return list;
        int len = (int)Math.Min(BitConverter.ToUInt32(raw, 4), (uint)(raw.Length - 8));
        int i = 8, end = 8 + len;
        while (i + 4 <= end)
        {
            byte type = raw[i], fmtLen = raw[i + 1];
            if (fmtLen < 4 || i + fmtLen > end) break;
            // Os textos ficam logo depois da parte formatada, separados por \0 e terminados por \0\0
            int s = i + fmtLen, strEnd = s;
            while (strEnd + 1 < end && !(raw[strEnd] == 0 && raw[strEnd + 1] == 0)) strEnd++;
            var strings = System.Text.Encoding.ASCII.GetString(raw, s, Math.Max(0, strEnd - s)).Split('\0');

            if (type == 17 && fmtLen >= 0x17)
            {
                int size = U16(raw, i + 0x0C);
                if (size == 0x7FFF && fmtLen >= 0x20) size = (int)BitConverter.ToUInt32(raw, i + 0x1C);
                else if ((size & 0x8000) != 0) size = (size & 0x7FFF) / 1024; // em KB
                if (size is not (0 or 0xFFFF))
                {
                    int rated = U16(raw, i + 0x15);
                    int configured = fmtLen >= 0x22 ? U16(raw, i + 0x20) : 0;
                    string part = fmtLen >= 0x1B ? Str(strings, raw[i + 0x1A]) : "";
                    list.Add(new MemoryModule(TypeName(raw[i + 0x12]), rated is 0xFFFF ? 0 : rated, configured is 0xFFFF ? 0 : configured, part, size));
                }
            }
            if (type == 127) break; // fim da tabela
            i = strEnd + 2;
        }
        return list;
    }

    private static int U16(byte[] b, int at) => BitConverter.ToUInt16(b, at);
    private static string Str(string[] strings, byte index) => index == 0 || index > strings.Length ? "" : strings[index - 1].Trim();
}
