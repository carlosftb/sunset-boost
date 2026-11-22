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

    public static IReadOnlyList<DiagItem> Run(IPowerPlans? power = null)
    {
        var items = new List<DiagItem>();

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

        // Plano de energia
        try
        {
            var active = (power ?? new RealPowerPlans()).GetActive();
            bool good = active == new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c") || active == PowerPlanTweak.UltimatePerformance;
            items.Add(good
                ? new(DiagStatus.Ok, "Plano de energia: Alto desempenho", "Já está certo no seu PC.")
                : new(DiagStatus.Recommend, "Plano de energia não é o de Alto desempenho",
                    "Painel de Controle › Opções de Energia › Alto desempenho. Ou ligue o ajuste no Modo Jogo."));
        }
        catch { }

        items.Add(new(DiagStatus.Info, "Painel de controle da NVIDIA, perfil do Valorant",
            "Modo de baixa latência: Ligado · Gerenciamento de energia: Preferir desempenho máximo · Sincronização vertical: Desligado."));
        items.Add(new(DiagStatus.Info, "Dentro do Valorant",
            "Limite de FPS um pouco acima da taxa do monitor deixa o tempo de quadro mais estável que FPS ilimitado."));
        return items;
    }

    private static IEnumerable<string> ReadRunKey(RegistryKey root)
    {
        using var k = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (k is null) yield break;
        foreach (var name in k.GetValueNames())
            yield return name + " " + k.GetValue(name);
    }
}
