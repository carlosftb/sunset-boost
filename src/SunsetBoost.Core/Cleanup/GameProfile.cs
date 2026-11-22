namespace SunsetBoost.Core.Cleanup;

/// <summary>Perfil de jogo: quais processos são o jogo e quais apps ele precisa manter abertos.</summary>
/// <param name="AntiCheat">Jogo com anti-cheat: o app não mexe em nada do processo do jogo (nem prioridade).</param>
public sealed record GameProfile(string Id, string DisplayName, string[] GameProcessNames, string[] KeepAppIds, bool AntiCheat = false)
{
    public bool IsGameProcess(string name) => GameProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    public bool KeepsApp(string appId) => KeepAppIds.Contains(appId, StringComparer.OrdinalIgnoreCase);
}

public static class GameProfiles
{
    public static readonly GameProfile Valorant = new("valorant", "Valorant",
        new[] { "valorant-win64-shipping", "valorant" }, new[] { "riot" }, AntiCheat: true);

    public static readonly GameProfile LeagueOfLegends = new("lol", "League of Legends",
        new[] { "league of legends", "leagueclient", "leagueclientux", "leagueclientuxrender" }, new[] { "riot" }, AntiCheat: true);

    public static readonly GameProfile Steam = new("steam", "Jogos da Steam", Array.Empty<string>(), new[] { "steam" });

    public static readonly GameProfile Epic = new("epic", "Jogos da Epic",
        new[] { "fortnitelauncher", "fortniteclient-win64-shipping" }, new[] { "epic" }, AntiCheat: true);

    public static readonly GameProfile Generic = new("generico", "Sem perfil", Array.Empty<string>(), Array.Empty<string>());

    public static readonly IReadOnlyList<GameProfile> All = new[] { Valorant, LeagueOfLegends, Steam, Epic, Generic };

    public static GameProfile Find(string? id) =>
        All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? Generic;
}
