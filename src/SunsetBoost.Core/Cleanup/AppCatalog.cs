namespace SunsetBoost.Core.Cleanup;

public enum CleanupList
{
    /// <summary>🔒 Nunca fecha.</summary>
    NeverClose,
    /// <summary>✅ Fecha por padrão.</summary>
    CloseByDefault,
    /// <summary>✋ Você decide (padrão: mantém aberto).</summary>
    UserDecides,
}

public enum AppCategory { Browser, Launcher, Sync, Communication, Media, Streaming, Assistant, Updater, Widgets, Peripheral, Other }

public sealed record AppDefinition(
    string Id,
    string DisplayName,
    AppCategory Category,
    CleanupList DefaultList,
    string[] ProcessNames,
    string? Note = null);

/// <summary>Apps conhecidos. Processo que não está aqui vira "Você decide" e fica aberto por padrão.</summary>
public static class AppCatalog
{
    public static readonly IReadOnlyList<AppDefinition> All = new AppDefinition[]
    {
        // ✅ Fecha por padrão
        new("steam", "Steam", AppCategory.Launcher, CleanupList.CloseByDefault, new[] { "steam", "steamwebhelper" }),
        new("epic", "Epic Games Launcher", AppCategory.Launcher, CleanupList.CloseByDefault,
            new[] { "epicgameslauncher", "epicwebhelper", "epiconlineservicesuserhelper" }),
        new("riot", "Riot Client", AppCategory.Launcher, CleanupList.CloseByDefault,
            new[] { "riotclientservices", "riotclientux", "riotclientuxrender", "riotclientcrashhandler", "riot client" }),
        new("ea", "EA app", AppCategory.Launcher, CleanupList.CloseByDefault, new[] { "eadesktop", "eabackgroundservice" }),
        new("ubisoft", "Ubisoft Connect", AppCategory.Launcher, CleanupList.CloseByDefault, new[] { "upc", "uplaywebcore", "ubisoftconnect" }),
        new("battlenet", "Battle.net", AppCategory.Launcher, CleanupList.CloseByDefault, new[] { "battle.net" }),
        new("gog", "GOG Galaxy", AppCategory.Launcher, CleanupList.CloseByDefault, new[] { "galaxyclient", "galaxyclient helper" }),
        new("onedrive", "OneDrive", AppCategory.Sync, CleanupList.CloseByDefault, new[] { "onedrive" }),
        new("teams", "Microsoft Teams", AppCategory.Communication, CleanupList.CloseByDefault, new[] { "ms-teams", "teams", "msteams" }),
        new("widgets", "Widgets do Windows", AppCategory.Widgets, CleanupList.CloseByDefault, new[] { "widgets", "widgetservice", "widgetboard" }),
        new("updaters", "Atualizadores", AppCategory.Updater, CleanupList.CloseByDefault,
            new[] { "onedrivestandaloneupdater", "adobearm", "googleupdate", "microsoftedgeupdate", "jusched", "bravecrashhandler", "bravecrashhandler64", "braveupdate" }),

        // ✋ Você decide
        // Navegadores ficam abertos por padrão: no Modo Jogo, a chave "Limitar os navegadores" só segura a memória deles
        new("edge", "Microsoft Edge", AppCategory.Browser, CleanupList.UserDecides, new[] { "msedge" },
            "O Startup boost mantém o Edge na memória mesmo fechado (veja Diagnóstico)."),
        new("chrome", "Google Chrome", AppCategory.Browser, CleanupList.UserDecides, new[] { "chrome" }),
        new("firefox", "Firefox", AppCategory.Browser, CleanupList.UserDecides, new[] { "firefox" }),
        new("opera", "Opera / Opera GX", AppCategory.Browser, CleanupList.UserDecides, new[] { "opera", "opera_crashreporter", "opera_gx_splash" }),
        new("brave", "Brave", AppCategory.Browser, CleanupList.UserDecides, new[] { "brave" }),
        new("vivaldi", "Vivaldi", AppCategory.Browser, CleanupList.UserDecides, new[] { "vivaldi" }),
        new("discord", "Discord", AppCategory.Communication, CleanupList.UserDecides, new[] { "discord" }),
        new("spotify", "Spotify", AppCategory.Media, CleanupList.UserDecides, new[] { "spotify", "spotifylauncher", "spotifyxboxgamebarwebview" }),
        new("obs", "OBS Studio", AppCategory.Streaming, CleanupList.UserDecides, new[] { "obs64", "obs32", "obs-browser-page" }),
        new("claude", "Claude", AppCategory.Assistant, CleanupList.UserDecides, new[] { "claude" }),
        new("whatsapp", "WhatsApp", AppCategory.Communication, CleanupList.UserDecides, new[] { "whatsapp", "whatsapp.root" }),
        new("telegram", "Telegram", AppCategory.Communication, CleanupList.UserDecides, new[] { "telegram" }),
        new("slack", "Slack", AppCategory.Communication, CleanupList.UserDecides, new[] { "slack" }),
        new("zoom", "Zoom", AppCategory.Communication, CleanupList.UserDecides, new[] { "zoom" }),
        new("nvidia-overlay", "Overlay da NVIDIA", AppCategory.Streaming, CleanupList.UserDecides,
            new[] { "nvidia overlay", "nvidia share", "nvidia app", "nvidia web helper" }, "Usado pelo Instant Replay / ShadowPlay."),
        new("ghub", "Logitech G HUB", AppCategory.Peripheral, CleanupList.UserDecides, new[] { "lghub", "lghub_agent", "lghub_system_tray" },
            "Fechar pode desligar macros/DPI configurados no software."),
        new("razer", "Razer Synapse", AppCategory.Peripheral, CleanupList.UserDecides, new[] { "razer synapse 3", "razer synapse service process", "razercentral" }),
        new("icue", "Corsair iCUE", AppCategory.Peripheral, CleanupList.UserDecides, new[] { "icue" }),
        new("phonelink", "Vincular ao Celular", AppCategory.Communication, CleanupList.UserDecides, new[] { "phoneexperiencehost" }),
    };

    private static readonly Dictionary<string, AppDefinition> ByProcess =
        All.SelectMany(a => a.ProcessNames.Select(n => (n, a)))
           .ToDictionary(x => x.n, x => x.a, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, AppDefinition> ById =
        All.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);

    public static AppDefinition? FindByProcess(string name) => ByProcess.GetValueOrDefault(name);
    public static AppDefinition? FindById(string id) => ById.GetValueOrDefault(id);

    /// <summary>Processos auxiliares que pertencem ao app que os abriu (ex.: WebView2 dentro dos Widgets).</summary>
    public static readonly HashSet<string> HelperProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedgewebview2", "crashpad_handler", "cefsharp.browsersubprocess", "qtwebengineprocess",
    };
}
