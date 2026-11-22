using SunsetBoost.Core.Processes;

namespace SunsetBoost.Core.Cleanup;

/// <summary>Contexto da execução atual pras regras de segurança.</summary>
public sealed class SafetyContext
{
    /// <summary>PIDs que nunca podem ser tocados: o próprio app e quem o abriu (terminal, atalho...).</summary>
    public HashSet<int> ProtectedPids { get; } = new();

    /// <summary>Pasta do Windows (C:\Windows). Tudo dentro dela é componente do sistema.</summary>
    public string WindowsDir { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public GameProfile Profile { get; init; } = GameProfiles.Generic;

    public static SafetyContext ForCurrentProcess(IReadOnlyList<ProcessInfo> snapshot, GameProfile profile)
    {
        var ctx = new SafetyContext { Profile = profile };
        var byPid = snapshot.ToDictionary(p => p.Pid);
        int pid = Environment.ProcessId;
        // Protege o próprio processo e a cadeia de pais (quem nos abriu)
        for (int i = 0; i < 16 && pid > 4 && ctx.ProtectedPids.Add(pid); i++)
            pid = byPid.TryGetValue(pid, out var p) ? p.ParentPid : 0;
        return ctx;
    }
}

/// <summary>
/// Lista "🔒 Nunca fecha". Estas regras são fixas no código: nenhuma configuração do usuário passa por cima delas.
/// </summary>
public static class SafetyRules
{
    public static readonly HashSet<string> CriticalSystem = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "[system process]", "registry", "secure system", "memory compression", "memcompression",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "svchost", "dwm", "fontdrvhost",
        "sihost", "explorer", "ctfmon", "conhost", "runtimebroker", "startmenuexperiencehost", "shellexperiencehost",
        "searchhost", "searchapp", "searchindexer", "textinputhost", "taskhostw", "dllhost", "wmiprvse", "audiodg",
        "spoolsv", "sgrmbroker", "wudfhost", "applicationframehost", "lockapp", "logonui", "consent", "userinit",
        "dashost", "unsecapp", "taskmgr", "systemsettings", "smartscreen", "securityhealthservice",
        "securityhealthsystray", "msmpeng", "nissrv", "mpdefendercoreservice", "mpcmdrun",
    };

    public static readonly HashSet<string> Security = new(StringComparer.OrdinalIgnoreCase)
    {
        "msmpeng", "nissrv", "mpdefendercoreservice", "securityhealthservice", "securityhealthsystray", "smartscreen",
        "avp", "avpui", "avastsvc", "avastui", "afwserv", "avgsvc", "avgui", "bdagent", "bdservicehost", "vsserv",
        "ekrn", "egui", "mcshield", "mfemms", "mcuicnt", "mbamservice", "mbamtray", "mbam", "nortonsecurity", "nsservice",
        "ccsvchst", "sophosui", "savservice", "wrsa", "kavfs", "defendersessionhelper", "defenderaiplatformhost",
        "mssense", "sensecncproxy",
    };

    private static readonly string[] SecurityPathMarkers =
    {
        @"\Windows Defender\", @"\Windows Defender Advanced Threat Protection\", @"\Microsoft\Windows Defender",
    };

    public static readonly HashSet<string> AntiCheat = new(StringComparer.OrdinalIgnoreCase)
    {
        "vgc", "vgtray", "vgk", "easyanticheat", "easyanticheat_eos", "easyanticheat_eos_setup", "beservice",
        "beservice_x64", "battleye", "faceit", "faceitservice", "faceitclient", "pnkbstra", "pnkbstrb",
        "eaanticheat.gameservicelauncher", "eaanticheat.installer", "xigncode", "x3", "gamemon", "gameguard",
        "nprotect", "zakynthos",
    };

    private static readonly string[] AntiCheatPrefixes = { "easyanticheat", "beservice", "faceit", "eaanticheat", "vgc" };

    public static readonly HashSet<string> Drivers = new(StringComparer.OrdinalIgnoreCase)
    {
        "nvdisplay.container", "nvcontainer", "nvsphelper64", "atiesrxx", "atieclxx", "amdrsserv", "radeonsoftware",
        "amdow", "amdfendrsr", "igfxem", "igfxhk", "igfxtray", "igfxcuiservice", "rtkaudioservice64", "rtkauduservice64",
        "rtkngui64", "ravbg64", "ravcpl64", "nahimicsvc64", "nahimicsvc32", "dolbydax2api", "waveshms", "maxxaudiometer",
    };

    /// <summary>Devolve o motivo se o processo NUNCA pode ser fechado; null se pode ser considerado.</summary>
    public static string? Check(ProcessInfo p, SafetyContext ctx)
    {
        if (ctx.ProtectedPids.Contains(p.Pid)) return "o próprio Sunset Boost (ou quem o abriu)";
        if (p.Pid <= 4) return "núcleo do Windows";
        if (p.SessionId == 0) return "serviço do sistema (sessão 0)";
        if (CriticalSystem.Contains(p.Name)) return "processo essencial do Windows";
        if (Security.Contains(p.Name) || p.Name.StartsWith("defender", StringComparison.OrdinalIgnoreCase)
            || (p.ImagePath is { } ip && SecurityPathMarkers.Any(m => ip.Contains(m, StringComparison.OrdinalIgnoreCase))))
            return "antivírus / segurança";
        if (IsAntiCheat(p.Name)) return "anti-cheat";
        if (Drivers.Contains(p.Name)) return "driver de vídeo ou áudio";
        // Qualquer jogo conhecido fica, mesmo que o perfil ativo seja de outro jogo
        if (ctx.Profile.IsGameProcess(p.Name) || GameProfiles.All.Any(g => g.IsGameProcess(p.Name))) return "o jogo";
        if (p.ImagePath is null) return "processo protegido (sem acesso)";
        if (IsUnder(p.ImagePath, ctx.WindowsDir)) return "componente do Windows";
        return null;
    }

    public static bool IsAntiCheat(string name) =>
        AntiCheat.Contains(name) || AntiCheatPrefixes.Any(px => name.StartsWith(px, StringComparison.OrdinalIgnoreCase));

    private static bool IsUnder(string path, string dir)
    {
        if (string.IsNullOrEmpty(dir)) return false;
        var d = dir.TrimEnd('\\') + "\\";
        return path.StartsWith(d, StringComparison.OrdinalIgnoreCase);
    }
}
