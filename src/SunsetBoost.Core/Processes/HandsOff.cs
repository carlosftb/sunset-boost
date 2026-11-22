using SunsetBoost.Core.Cleanup;

namespace SunsetBoost.Core.Processes;

/// <summary>
/// Processos em que o Sunset Boost NUNCA abre acesso (nem pra ler memória ou caminho):
/// jogos conhecidos, anti-cheats e o Riot Client. Ficam automaticamente em "Nunca fecha"
/// (sem caminho → "processo protegido") e a RAM deles aparece como 0.
/// </summary>
public static class HandsOff
{
    private static readonly HashSet<string> Names = new(
        SafetyRules.AntiCheat
            .Concat(GameProfiles.All.SelectMany(p => p.GameProcessNames))
            .Concat(AppCatalog.FindById("riot")?.ProcessNames ?? Array.Empty<string>()),
        StringComparer.OrdinalIgnoreCase);

    public static bool IsHandsOff(string name) => Names.Contains(name) || SafetyRules.IsAntiCheat(name);
}
