using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SunsetBoost.Core.Tweaks;

/// <summary>Onde a preferência de placa fica: HKCU\Software\Microsoft\DirectX\UserGpuPreferences (a mesma tela do Windows).</summary>
public interface IGpuPreferenceStore
{
    /// <summary>Texto gravado pra esse .exe (ex.: "GpuPreference=2;"). null = nada gravado.</summary>
    string? Get(string exePath);
    /// <summary>null apaga o valor.</summary>
    void Set(string exePath, string? value);
}

/// <summary>
/// "Placa de vídeo dedicada nos jogos": o mesmo que Configurações › Sistema › Tela › Elementos gráficos › Alto desempenho,
/// feito pra cada jogo da lista. Quem aplica é o próprio Windows quando o jogo abre (o app nunca toca no jogo).
/// Em notebook com duas placas pode dobrar o FPS; com uma placa só, não muda nada.
/// </summary>
public static class GpuPreferences
{
    public const string RegistryPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    public const string GlobalValue = "DirectXUserGlobalSettings";
    private const string Key = "GpuPreference";
    /// <summary>0 = o Windows decide, 1 = economia de energia (integrada), 2 = alto desempenho (dedicada).</summary>
    public const string HighPerformance = "2";

    /// <summary>"GpuPreference=2;SwapEffectUpgradeEnable=1;" → pares em ordem.</summary>
    public static Dictionary<string, string> ParseTokens(string? value)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0) d[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }
        return d;
    }

    private static string? Join(Dictionary<string, string> d) => d.Count == 0 ? null : string.Concat(d.Select(kv => $"{kv.Key}={kv.Value};"));

    public static bool IsOn(IGpuPreferenceStore store, IEnumerable<string> exePaths)
    {
        var list = exePaths.ToList();
        return list.Count > 0 && list.All(p => ParseTokens(store.Get(p)).GetValueOrDefault(Key) == HighPerformance);
    }

    /// <summary>Marca cada jogo como "Alto desempenho", mantendo outras opções que o Windows já tinha gravado pra ele.</summary>
    public static List<string> Apply(IGpuPreferenceStore store, IEnumerable<string> exePaths)
    {
        var errors = new List<string>();
        foreach (var exe in exePaths)
        {
            if (!IsAllowed(exe, out var why)) { errors.Add($"{Path.GetFileName(exe)}: {why}"); continue; }
            try
            {
                var tokens = ParseTokens(store.Get(exe));
                tokens[Key] = HighPerformance;
                store.Set(exe, Join(tokens));
            }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(exe)}: {ex.Message}"); }
        }
        return errors;
    }

    /// <summary>Desliga: só tira o "Alto desempenho" (o que o app põe). Escolha diferente feita pelo usuário fica.</summary>
    public static List<string> Remove(IGpuPreferenceStore store, IEnumerable<string> exePaths)
    {
        var errors = new List<string>();
        foreach (var exe in exePaths)
        {
            try
            {
                var tokens = ParseTokens(store.Get(exe));
                if (tokens.GetValueOrDefault(Key) != HighPerformance) continue;
                tokens.Remove(Key);
                store.Set(exe, Join(tokens));
            }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(exe)}: {ex.Message}"); }
        }
        return errors;
    }

    /// <summary>Só caminho completo de um .exe que existe, fora da pasta do Windows e que não seja o próprio app.</summary>
    public static bool IsAllowed(string exePath, out string why)
    {
        why = "";
        if (string.IsNullOrWhiteSpace(exePath) || !Path.IsPathFullyQualified(exePath) || exePath.Contains(';')
            || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        { why = "caminho inválido"; return false; }
        var full = Path.GetFullPath(exePath);
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        if (full.StartsWith(win, StringComparison.OrdinalIgnoreCase)) { why = "programa do Windows"; return false; }
        if (Path.GetFileNameWithoutExtension(full).StartsWith("SunsetBoost", StringComparison.OrdinalIgnoreCase)) { why = "o próprio app"; return false; }
        if (!File.Exists(full)) { why = "o arquivo não existe"; return false; }
        return true;
    }
}

/// <summary>HKCU de quem está usando o PC (o app roda com admin, mas como o próprio usuário: é o mesmo HKCU).</summary>
public sealed class RealGpuPreferenceStore : IGpuPreferenceStore
{
    public string? Get(string exePath)
    {
        using var k = Registry.CurrentUser.OpenSubKey(GpuPreferences.RegistryPath);
        return k?.GetValue(exePath) as string;
    }

    public void Set(string exePath, string? value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(GpuPreferences.RegistryPath, writable: true);
        if (value is null) k.DeleteValue(exePath, throwOnMissingValue: false);
        else k.SetValue(exePath, value, RegistryValueKind.String);
    }
}

/// <summary>
/// Acha onde os jogos conhecidos estão instalados, pelos arquivos dos launchers (Riot e Epic). A preferência de placa
/// precisa do caminho completo do .exe; o app nunca abre o processo do jogo pra descobrir.
/// </summary>
public static class GameLocator
{
    private sealed record Known(string Name, string RiotProduct, string EpicApp, string RelativeExe, string DefaultRoot);

    private static readonly Known[] Games =
    {
        new("Valorant", "valorant.live", "", @"ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe", @"C:\Riot Games\VALORANT\live"),
        new("LoL", "league_of_legends.live", "", @"Game\League of Legends.exe", @"C:\Riot Games\League of Legends"),
        new("Fortnite", "", "Fortnite", @"FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe", @"C:\Program Files\Epic Games\Fortnite"),
    };

    private static readonly Regex RiotInstallPath = new(@"^\s*product_install_full_path:\s*""?(?<p>[^""\r\n]+)""?\s*$", RegexOptions.Multiline);

    /// <summary>(nome, caminho do .exe) de cada jogo conhecido que está instalado.</summary>
    /// <param name="programData">Pasta ProgramData (nos testes, uma pasta temporária).</param>
    public static List<(string Name, string ExePath)> Find(string? programData = null, bool useDefaults = true)
    {
        programData ??= Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var found = new List<(string, string)>();
        foreach (var g in Games)
        {
            var roots = new List<string>();
            if (g.RiotProduct != "") roots.AddRange(RiotRoots(programData, g.RiotProduct));
            if (g.EpicApp != "") roots.AddRange(EpicRoots(programData, g.EpicApp));
            if (useDefaults) roots.Add(g.DefaultRoot);

            var exe = roots.Select(r => SafeCombine(r, g.RelativeExe)).FirstOrDefault(p => p is not null && File.Exists(p));
            if (exe is not null) found.Add((g.Name, exe));
        }
        return found;
    }

    private static IEnumerable<string> RiotRoots(string programData, string product)
    {
        var file = Path.Combine(programData, "Riot Games", "Metadata", product, product + ".product_settings.yaml");
        string text;
        try { if (!File.Exists(file) || new FileInfo(file).Length > 1 << 20) yield break; text = File.ReadAllText(file); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }
        var m = RiotInstallPath.Match(text);
        if (m.Success) yield return m.Groups["p"].Value.Trim().Replace('/', '\\');
    }

    private static IEnumerable<string> EpicRoots(string programData, string appName)
    {
        var dir = Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        string[] files;
        try { files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.item") : Array.Empty<string>(); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }
        foreach (var f in files)
        {
            string? root = null;
            try
            {
                if (new FileInfo(f).Length > 1 << 20) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                if (doc.RootElement.TryGetProperty("AppName", out var a) && a.GetString() == appName
                    && doc.RootElement.TryGetProperty("InstallLocation", out var loc))
                    root = loc.GetString();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            if (!string.IsNullOrWhiteSpace(root)) yield return root;
        }
    }

    /// <summary>Junta pasta + caminho relativo; null se a pasta não for um caminho completo válido.</summary>
    private static string? SafeCombine(string root, string relative)
    {
        try { return Path.IsPathFullyQualified(root) ? Path.GetFullPath(Path.Combine(root, relative)) : null; }
        catch (ArgumentException) { return null; }
    }
}
