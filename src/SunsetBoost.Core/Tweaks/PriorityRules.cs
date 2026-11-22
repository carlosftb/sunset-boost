using System.Text.RegularExpressions;
using Microsoft.Win32;
using SunsetBoost.Core.Cleanup;

namespace SunsetBoost.Core.Tweaks;

/// <summary>Onde a regra de prioridade fica: o registro do Windows (Image File Execution Options).</summary>
public interface IPriorityStore
{
    int? GetCpuPriority(string exe);
    void SetCpuPriority(string exe, int value);
    void ClearCpuPriority(string exe);
}

/// <summary>
/// Prioridade alta fixa: grava no registro "quando este .exe abrir, começa em prioridade Alta".
/// Quem aplica é o próprio Windows na hora que o jogo abre, então o Sunset Boost nunca toca no
/// processo do jogo (nem procura ele). Fica valendo até o usuário desligar, mesmo com o app fechado.
/// </summary>
public static class PriorityRules
{
    /// <summary>Valores do Windows: 1 Ociosa, 2 Normal, 3 Alta, 5 Abaixo do normal, 6 Acima do normal. 4 (Tempo real) nunca.</summary>
    public const int High = 3;

    /// <summary>O executável que é o jogo de verdade (não o launcher).</summary>
    public static readonly string[] KnownGames =
    {
        "VALORANT-Win64-Shipping.exe",
        "League of Legends.exe",
        "FortniteClient-Win64-Shipping.exe",
    };

    public static IReadOnlyList<string> Targets(IEnumerable<string>? extra) =>
        KnownGames.Concat(extra ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Todos os jogos da lista já abrem em prioridade alta?</summary>
    public static bool IsOn(IPriorityStore store, IEnumerable<string> exes) => exes.All(e => store.GetCpuPriority(e) == High);

    /// <summary>Liga pra cada jogo. Devolve os erros (vazio = deu tudo certo).</summary>
    public static List<string> Apply(IPriorityStore store, IEnumerable<string> exes)
    {
        var errors = new List<string>();
        foreach (var exe in exes)
        {
            if (!IsAllowed(exe, out var why)) { errors.Add($"{exe}: {why}"); continue; }
            try { store.SetCpuPriority(exe, High); }
            catch (Exception ex) { errors.Add($"{exe}: {ex.Message}"); }
        }
        return errors;
    }

    /// <summary>Desliga. Só apaga o valor que o app pôs (Alta); se outro programa pôs outro valor, deixa.</summary>
    public static List<string> Remove(IPriorityStore store, IEnumerable<string> exes)
    {
        var errors = new List<string>();
        foreach (var exe in exes)
        {
            try { if (store.GetCpuPriority(exe) == High) store.ClearCpuPriority(exe); }
            catch (Exception ex) { errors.Add($"{exe}: {ex.Message}"); }
        }
        return errors;
    }

    private static readonly Regex ExeName = new(@"^[^\\/:*?""<>|]+\.exe$", RegexOptions.IgnoreCase);

    /// <summary>Só nome de .exe (sem pasta), e nunca processo do Windows, anti-cheat ou o próprio app.</summary>
    public static bool IsAllowed(string exe, out string why)
    {
        why = "";
        if (string.IsNullOrWhiteSpace(exe) || !ExeName.IsMatch(exe) || exe.Trim() != exe) { why = "nome de arquivo inválido"; return false; }
        var name = exe[..^4].ToLowerInvariant();
        if (SafetyRules.CriticalSystem.Contains(name) || name is "explorer" or "sunsetboost" or "sunsetboost.app" or "sunsetboost.cli")
        { why = "processo do Windows ou do próprio app"; return false; }
        if (SafetyRules.IsAntiCheat(name)) { why = "anti-cheat: o app não mexe"; return false; }
        return true;
    }
}

/// <summary>HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\&lt;exe&gt;\PerfOptions (precisa de admin).</summary>
public sealed class RealPriorityStore : IPriorityStore
{
    private const string Ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string Value = "CpuPriorityClass";

    private static RegistryKey Hklm() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    public int? GetCpuPriority(string exe)
    {
        using var root = Hklm();
        using var k = root.OpenSubKey($@"{Ifeo}\{exe}\PerfOptions");
        return k?.GetValue(Value) is int v ? v : null;
    }

    public void SetCpuPriority(string exe, int value)
    {
        using var root = Hklm();
        using var k = root.CreateSubKey($@"{Ifeo}\{exe}\PerfOptions", writable: true)
                      ?? throw new UnauthorizedAccessException("sem permissão no registro (precisa de admin)");
        k.SetValue(Value, value, RegistryValueKind.DWord);
    }

    public void ClearCpuPriority(string exe)
    {
        using var root = Hklm();
        using (var perf = root.OpenSubKey($@"{Ifeo}\{exe}\PerfOptions", writable: true))
        {
            if (perf is null) return;
            perf.DeleteValue(Value, throwOnMissingValue: false);
        }
        // Apaga só as chaves que ficaram vazias; nunca mexe em outras configurações do .exe
        DeleteIfEmpty(root, $@"{Ifeo}\{exe}\PerfOptions");
        DeleteIfEmpty(root, $@"{Ifeo}\{exe}");
    }

    private static void DeleteIfEmpty(RegistryKey root, string path)
    {
        using (var k = root.OpenSubKey(path))
            if (k is null || k.ValueCount > 0 || k.SubKeyCount > 0) return;
        root.DeleteSubKey(path, throwOnMissingSubKey: false);
    }
}
