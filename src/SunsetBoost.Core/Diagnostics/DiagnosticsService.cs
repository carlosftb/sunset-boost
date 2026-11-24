using Microsoft.Win32;
using SunsetBoost.Core.Infrastructure;
using SunsetBoost.Core.Tweaks;

namespace SunsetBoost.Core.Diagnostics;

public enum DiagStatus { Ok, Recommend, Info }

public sealed record DiagItem(DiagStatus Status, string Title, string Detail);

/// <summary>Recomendações que valem pra sempre (não são revertidas pelo Modo Jogo).</summary>
public static class DiagnosticsService
{
    private static readonly (string Match, string Name)[] HeavyStartup =
    {
        ("steam", "Steam"), ("epicgameslauncher", "Epic Games"), ("discord", "Discord"), ("spotify", "Spotify"),
        ("onedrive", "OneDrive"), ("teams", "Teams"), ("eadesktop", "EA app"), ("upc", "Ubisoft Connect"),
        ("battle.net", "Battle.net"), ("msedge", "Edge"),
    };

    /// <param name="hardware">null = lê o PC de verdade.</param>
    /// <param name="gpuPreferenceOn">Se "Placa de vídeo dedicada nos jogos" está ligada (null = não sei).</param>
    public static IReadOnlyList<DiagItem> Run(IPowerPlans? power = null, IHardwareInfo? hardware = null, bool? gpuPreferenceOn = null)
    {
        // Primeiro o hardware: é onde estão os ganhos grandes (Hz do monitor, XMP, dual channel, placa certa)
        var hw = hardware ?? new RealHardwareInfo();
        var items = HardwareChecks.Run(hw, DateTime.Today, gpuPreferenceOn);

        if (!Elevation.IsElevated)
            items.Add(new(DiagStatus.Recommend, "Abra o Sunset Boost como administrador",
                "Sem admin, ele não consegue pausar serviços, limpar a standby list nem fechar apps elevados."));

        // Edge Startup boost (política de máquina, se existir)
        var boost = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Edge")?.GetValue("StartupBoostEnabled");
        items.Add(boost is 0
            ? new(DiagStatus.Ok, "Startup boost do Edge desligado", "O Edge não fica carregado na memória à toa.")
            : new(DiagStatus.Recommend, "Desligar o \"Startup boost\" do Edge",
                "É ele que mantém o Edge na memória mesmo fechado. No Edge: Configurações › Sistema e desempenho › Aumento de inicialização."));

        // Apps pesados abrindo com o Windows
        var startup = ReadRunKey(Registry.CurrentUser) .Concat(ReadRunKey(Registry.LocalMachine)).ToList();
        var heavy = HeavyStartup.Where(h => startup.Any(s => s.Contains(h.Match, StringComparison.OrdinalIgnoreCase)))
                                .Select(h => h.Name).Distinct().ToList();
        items.Add(heavy.Count == 0
            ? new(DiagStatus.Ok, "Nenhum app pesado abrindo com o Windows", "A inicialização está limpa.")
            : new(DiagStatus.Recommend, $"{string.Join(", ", heavy)} abrindo junto com o Windows",
                "Tire da inicialização em Gerenciador de Tarefas › Aplicativos de inicialização. Economiza memória desde o boot."));

        // Plano de energia (notebook tem regra própria) e dicas de jogo com os números deste PC
        bool? laptop = Safe(hw.HasBattery);
        try { items.Add(HardwareChecks.PowerPlan((power ?? new RealPowerPlans()).GetActive(), laptop)); }
        catch { }
        items.AddRange(HardwareChecks.GameTips(Safe(hw.Displays) ?? Array.Empty<DisplayInfo>(), Safe(hw.Gpus) ?? Array.Empty<GpuInfo>()));
        return items;
    }

    private static T? Safe<T>(Func<T> f)
    {
        try { return f(); } catch { return default; }
    }

    private static IEnumerable<string> ReadRunKey(RegistryKey root)
    {
        using var k = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (k is null) yield break;
        foreach (var name in k.GetValueNames())
            yield return name + " " + k.GetValue(name);
    }
}
