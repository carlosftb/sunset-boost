using SunsetBoost.Core.Infrastructure;
using SunsetBoost.Core.Processes;

namespace SunsetBoost.Core.Cleanup;

/// <summary>Um app (vários processos agrupados) e o que vai acontecer com ele.</summary>
public sealed record AppGroup(
    string AppId,
    string DisplayName,
    CleanupList List,
    string Reason,
    IReadOnlyList<ProcessInfo> Processes,
    bool WillClose,
    string? Note = null)
{
    public long PrivateBytes => Processes.Sum(p => p.PrivateBytes);

    /// <summary>Jogo, anti-cheat ou Riot Client: o app nunca abre esses processos, então a RAM deles é desconhecida (não zero).</summary>
    public bool RamUnknown => Processes.Count > 0 && Processes.All(p => HandsOff.IsHandsOff(p.Name));
}

public sealed record CleanupPlan(GameProfile Profile, IReadOnlyList<AppGroup> Groups, SafetyContext Context)
{
    public IEnumerable<AppGroup> ToClose => Groups.Where(g => g.WillClose);
    public long EstimatedFreedBytes => ToClose.Sum(g => g.PrivateBytes);
}

/// <summary>Agrupa os processos por app e coloca cada app numa das três listas.</summary>
public static class CleanupPlanner
{
    public static CleanupPlan Build(
        IReadOnlyList<ProcessInfo> snapshot,
        SafetyContext ctx,
        IReadOnlyDictionary<string, AppDecision>? decisions = null)
    {
        decisions ??= new Dictionary<string, AppDecision>();
        var byPid = snapshot.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());

        var groups = snapshot
            .GroupBy(p => ResolveAppKey(p, byPid))
            .Select(g => Classify(g.Key, g.ToList(), ctx, decisions))
            .OrderBy(g => g.List)
            .ThenByDescending(g => g.PrivateBytes)
            .ToList();

        return new CleanupPlan(ctx.Profile, groups, ctx);
    }

    /// <summary>Id do app: do catálogo, do app "pai" (pra processos auxiliares) ou "exe:nome".</summary>
    internal static string ResolveAppKey(ProcessInfo p, IReadOnlyDictionary<int, ProcessInfo> byPid)
    {
        var current = p;
        for (int depth = 0; depth < 12; depth++)
        {
            // Processos de sessão 0 (serviços) nunca entram no grupo de um app
            if (current.SessionId == 0 && current != p) break;
            if (!AppCatalog.HelperProcesses.Contains(current.Name))
            {
                var def = AppCatalog.FindByProcess(current.Name);
                return def?.Id ?? "exe:" + current.Name;
            }
            if (!byPid.TryGetValue(current.ParentPid, out var parent) || parent.Pid == current.Pid) break;
            // O pai precisa ter nascido antes do filho; se não, o PID foi reaproveitado
            if (parent.CreationTime > current.CreationTime && current.CreationTime != 0) break;
            current = parent;
        }
        return "exe:" + p.Name;
    }

    private static AppGroup Classify(string key, List<ProcessInfo> procs, SafetyContext ctx,
        IReadOnlyDictionary<string, AppDecision> decisions)
    {
        var def = key.StartsWith("exe:") ? null : AppCatalog.FindById(key);
        var display = def?.DisplayName ?? ProcessScanner.FriendlyName(procs[0]);

        // 1) Segurança primeiro: se QUALQUER processo do grupo é protegido, o app inteiro fica.
        var protectedReason = procs.Select(p => SafetyRules.Check(p, ctx)).FirstOrDefault(r => r is not null);
        if (protectedReason is not null)
        {
            // Mostra "Valorant" em vez de "valorant-win64-shipping.exe"
            var game = GameProfiles.All.FirstOrDefault(g => procs.Any(p => g.IsGameProcess(p.Name)));
            if (game is not null && game.Id is not ("steam" or "epic" or "generico")) display = game.DisplayName;
            return new AppGroup(key, display, CleanupList.NeverClose, protectedReason, procs, false, def?.Note);
        }

        // 2) Apps que o jogo do perfil precisa (ex.: Riot Client pro Valorant)
        if (def is not null && ctx.Profile.KeepsApp(def.Id))
            return new AppGroup(key, display, CleanupList.NeverClose, $"necessário pro {ctx.Profile.DisplayName}", procs, false, def.Note);

        // 3) Catálogo ou desconhecido
        var list = def?.DefaultList ?? CleanupList.UserDecides;
        var reason = def is null ? "app desconhecido" : CategoryReason(def.Category);
        var decision = decisions.TryGetValue(key, out var d) ? d : AppDecision.Default;

        bool willClose = list switch
        {
            CleanupList.CloseByDefault => decision != AppDecision.Keep,
            CleanupList.UserDecides => decision == AppDecision.Close,
            _ => false,
        };
        return new AppGroup(key, display, list, reason, procs, willClose, def?.Note);
    }

    private static string CategoryReason(AppCategory c) => c switch
    {
        AppCategory.Browser => "navegador",
        AppCategory.Launcher => "launcher que não é do jogo atual",
        AppCategory.Sync => "sincronização em segundo plano",
        AppCategory.Communication => "comunicação",
        AppCategory.Media => "música / mídia",
        AppCategory.Streaming => "gravação / transmissão",
        AppCategory.Assistant => "assistente",
        AppCategory.Updater => "atualizador",
        AppCategory.Widgets => "widgets",
        AppCategory.Peripheral => "software de periférico",
        _ => "outro",
    };
}
