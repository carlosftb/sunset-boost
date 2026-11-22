using SunsetBoost.Core.Cleanup;
using SunsetBoost.Core.Edge;
using SunsetBoost.Core.Infrastructure;
using SunsetBoost.Core.Processes;
using SunsetBoost.Core.Tweaks;

namespace SunsetBoost.Core;

public sealed record BoostResult(CleanupReport? Cleanup, GameModeResult? Tweaks, IReadOnlyList<string> SimulatedTweaks)
{
    public bool Simulated => Cleanup?.Simulated ?? SimulatedTweaks.Count > 0;
}

/// <summary>O botão "ENTRAR NO MODO JOGO": limpeza + ajustes. E o "RESTAURAR".</summary>
public sealed class BoostService
{
    private readonly AppSettings _settings;
    private readonly IProcessSource _processes;
    private readonly Func<bool, ISystemActions> _actions;

    public GameModeManager GameMode { get; }

    public BoostService(AppPaths paths, AppSettings settings,
        IReadOnlyList<ITweak>? tweaks = null, IProcessSource? processes = null, Func<bool, ISystemActions>? actions = null)
    {
        _settings = settings;
        _processes = processes ?? new ProcessScanner();
        _actions = actions ?? (sim => sim ? new SimulatedSystemActions() : new RealSystemActions());
        GameMode = new GameModeManager(tweaks ?? TweakCatalog.CreateReal(), paths.SnapshotFile);
    }

    public GameProfile Profile => GameProfiles.Find(_settings.ActiveProfileId);

    public bool IsTweakEnabled(ITweak t) => _settings.Tweaks.TryGetValue(t.Id, out var on) ? on : t.EnabledByDefault;

    public CleanupPlan BuildPlan()
    {
        var snap = _processes.Snapshot();
        return CleanupPlanner.Build(snap, SafetyContext.ForCurrentProcess(snap, Profile), _settings.AppDecisions);
    }

    public BoostResult Enter()
    {
        bool sim = _settings.SimulationMode;
        CleanupReport? cleanup = null;
        if (_settings.CloseApps || _settings.PurgeStandbyList)
        {
            var plan = BuildPlan();
            if (!_settings.CloseApps) plan = plan with { Groups = plan.Groups.Select(g => g with { WillClose = false }).ToList() };
            plan = ApplyBrowserRule(plan, sim);
            cleanup = new CleanupExecutor(_actions(sim)).Run(plan, CleanupOptions.FromSettings(_settings));
        }

        var enabled = GameMode.Tweaks.Where(IsTweakEnabled).ToList();
        if (sim)
        {
            foreach (var t in enabled) AppLog.Info($"[SIMULAÇÃO] aplicaria: {t.Title}");
            return new BoostResult(cleanup, null, enabled.Select(t => t.Title).ToList());
        }
        var tweaks = GameMode.Enter(new TweakContext(Profile), enabled.Select(t => t.Id));
        return new BoostResult(cleanup, tweaks, Array.Empty<string>());
    }

    /// <summary>O botão "Limpar agora" da aba Limpeza: mesma regra dos navegadores do Modo Jogo.</summary>
    public CleanupReport CleanNow()
    {
        bool sim = _settings.SimulationMode;
        var plan = ApplyBrowserRule(BuildPlan(), sim);
        return new CleanupExecutor(_actions(sim)).Run(plan, CleanupOptions.FromSettings(_settings));
    }

    /// <summary>
    /// Modo leve: Edge, Chrome e Opera GX nunca são fechados, só enxugados (1ª aba). Roda antes da limpeza,
    /// pra standby list já pegar a memória das abas fechadas.
    /// </summary>
    private CleanupPlan ApplyBrowserRule(CleanupPlan plan, bool sim)
    {
        if (!_settings.EdgeLite) return plan;
        bool trimmed = false;
        foreach (var b in Browsers.Lite.Where(b => plan.Groups.Any(g => g.AppId == b.AppId)))
        {
            plan = plan with { Groups = plan.Groups.Select(g => g.AppId == b.AppId ? g with { WillClose = false } : g).ToList() };
            if (sim) AppLog.Info($"[SIMULAÇÃO] {b.Name} leve: fecharia as abas e janelas extras, mantendo só a 1ª aba");
            else { EdgeTabs.KeepFirstTab(b); trimmed = true; }
        }
        if (trimmed) Thread.Sleep(500);
        return plan;
    }

    /// <summary>Chamado a cada 2 s enquanto o Modo Jogo está ativo: segura cada navegador no limite.</summary>
    public IReadOnlyList<GuardDecision> EnforceBrowserLimits()
    {
        if (!_settings.EdgeLite || !GameMode.IsActive) return Array.Empty<GuardDecision>();
        var limit = (long)_settings.EdgeLimitMB * 1024 * 1024;
        var sys = _actions(_settings.SimulationMode);
        // Fonte de teste só quando foi passada no construtor; no app, cada navegador lê só os próprios processos
        var source = _processes is ProcessScanner ? null : _processes;
        return Browsers.Lite.Select(b => EdgeMemoryGuard.Enforce(limit, sys, source, b)).ToList();
    }

    public GameModeResult Exit() => _settings.SimulationMode && !GameMode.IsActive
        ? new GameModeResult(true, "[SIMULAÇÃO] nada foi alterado", Array.Empty<TweakOutcome>())
        : GameMode.Restore();
}
