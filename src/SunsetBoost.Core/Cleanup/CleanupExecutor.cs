using System.Diagnostics;
using SunsetBoost.Core.Infrastructure;
using SunsetBoost.Core.Memory;
using SunsetBoost.Core.Processes;

namespace SunsetBoost.Core.Cleanup;

/// <param name="GracePeriod">Tempo máximo esperando um app com janela fechar sozinho.</param>
/// <param name="TrayGrace">Depois que a janela some e o processo continua vivo (foi pra bandeja), quanto esperar antes de forçar.</param>
public sealed record CleanupOptions(TimeSpan GracePeriod, bool PurgeStandby, TimeSpan? TrayGrace = null)
{
    public TimeSpan TrayWait => TrayGrace ?? TimeSpan.FromSeconds(1);

    public static CleanupOptions FromSettings(AppSettings s) =>
        new(TimeSpan.FromSeconds(Math.Clamp(s.GracefulCloseSeconds, 1, 15)), s.PurgeStandbyList);
}

public sealed record GroupResult(AppGroup Group, int ClosedGracefully, int Killed, int Failed, int Blocked, List<string> Errors);

public sealed record CleanupReport(
    bool Simulated,
    MemorySnapshot Before,
    MemorySnapshot After,
    long EstimatedFreedBytes,
    bool StandbyPurged,
    string? StandbyError,
    IReadOnlyList<GroupResult> Groups,
    TimeSpan Duration)
{
    /// <summary>Quanto o "Disponível" subiu de verdade (0 em simulação).</summary>
    public long MeasuredFreedBytes => Simulated ? 0 : (long)After.AvailableBytes - (long)Before.AvailableBytes;
}

/// <summary>
/// Executa o plano: 1) pede pra todos os apps fecharem; 2) espera até 3 s; 3) força só quem sobrou;
/// 4) limpa a standby list; 5) mede antes/depois.
/// </summary>
public sealed class CleanupExecutor
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private readonly ISystemActions _sys;

    public CleanupExecutor(ISystemActions sys) => _sys = sys;

    public CleanupReport Run(CleanupPlan plan, CleanupOptions options)
    {
        var sw = Stopwatch.StartNew();
        var tag = _sys.IsSimulation ? "[SIMULAÇÃO] " : "";
        var before = _sys.ReadMemory();
        AppLog.Info($"{tag}Limpeza iniciada | perfil={plan.Profile.Id} | disponível={Fmt.Bytes(before.AvailableBytes)}");

        // Monta os alvos, revalidando a segurança de CADA processo (defesa em profundidade:
        // mesmo que o plano venha errado, nada da lista "Nunca fecha" é tocado).
        var work = new List<(AppGroup group, List<ProcessInfo> targets, GroupResult result)>();
        foreach (var g in plan.Groups.Where(g => g.WillClose))
        {
            var result = new GroupResult(g, 0, 0, 0, 0, new List<string>());
            var targets = new List<ProcessInfo>();
            if (g.List == CleanupList.NeverClose)
            {
                AppLog.Warn($"{tag}BLOQUEADO: {g.DisplayName} está em 'Nunca fecha'");
                result = result with { Blocked = g.Processes.Count };
            }
            else
            {
                int blocked = 0;
                foreach (var p in g.Processes)
                {
                    var why = SafetyRules.Check(p, plan.Context);
                    if (why is null) targets.Add(p);
                    else { blocked++; AppLog.Warn($"{tag}BLOQUEADO: {p.ExeFileName} (PID {p.Pid}) — {why}"); }
                }
                result = result with { Blocked = blocked };
            }
            work.Add((g, targets, result));
        }

        // 1) Pedido educado pra todo mundo de uma vez (assim a espera total é de 3 s, não 3 s por app)
        var waiting = new List<List<ProcessInfo>>();
        var noWindow = new List<ProcessInfo>();
        foreach (var (g, targets, _) in work)
        {
            int windows = targets.Sum(_sys.RequestClose);
            AppLog.Info($"{tag}Pedindo pra fechar {g.DisplayName}: {targets.Count} processos, {windows} janelas, {Fmt.Bytes(g.PrivateBytes)}");
            // Sem janela não tem como receber o pedido: esperar seria só perder tempo
            if (windows > 0) waiting.Add(targets); else noWindow.AddRange(targets);
        }

        // 2) Quem não tem janela é forçado já (o passo 3 cuida disso); só os apps com janela ganham tempo.
        //    Um app é dado como "não vai fechar" quando: saiu (ótimo), estourou o prazo, ou a janela sumiu
        //    mas o processo continuou vivo por TrayWait (foi pra bandeja, como Claude, Spotify, Steam).
        var deadline = Stopwatch.StartNew();
        var windowGoneAt = new Dictionary<List<ProcessInfo>, TimeSpan>();
        while (deadline.Elapsed < options.GracePeriod)
        {
            waiting.RemoveAll(group =>
            {
                var alive = group.Where(_sys.IsAlive).ToList();
                if (alive.Count == 0) return true;
                if (alive.Any(p => _sys.CountWindows(p) > 0)) { windowGoneAt.Remove(group); return false; }
                if (!windowGoneAt.TryGetValue(group, out var since)) { windowGoneAt[group] = deadline.Elapsed; return false; }
                return deadline.Elapsed - since >= options.TrayWait;
            });
            if (waiting.Count == 0) break;
            _sys.Sleep(PollInterval);
        }

        // 3) Força quem não fechou sozinho
        var results = new List<GroupResult>();
        foreach (var (g, targets, r0) in work)
        {
            int graceful = 0, killed = 0, failed = 0;
            foreach (var p in targets)
            {
                if (!_sys.IsAlive(p)) { graceful++; continue; }
                if (_sys.Kill(p, out var err)) killed++;
                else { failed++; r0.Errors.Add($"{p.ExeFileName} (PID {p.Pid}): {err}"); }
            }
            if (killed + failed > 0 || _sys.IsSimulation)
                AppLog.Info($"{tag}{g.DisplayName}: {graceful} fecharam sozinhos, {killed} forçados, {failed} falharam");
            results.Add(r0 with { ClosedGracefully = graceful, Killed = killed, Failed = failed });
        }

        // 4) Standby list
        bool purged = false; string? purgeErr = null;
        if (options.PurgeStandby)
        {
            if (!_sys.IsSimulation) _sys.Sleep(TimeSpan.FromMilliseconds(400)); // deixa o Windows devolver a memória
            purged = _sys.PurgeStandbyList(out var e);
            purgeErr = purged ? null : e;
            AppLog.Info(purged ? $"{tag}Standby list limpa" : $"{tag}Standby list NÃO limpa: {e}");
        }

        var after = _sys.IsSimulation ? before : _sys.ReadMemory();
        var report = new CleanupReport(_sys.IsSimulation, before, after, plan.EstimatedFreedBytes, purged, purgeErr, results, sw.Elapsed);
        AppLog.Info($"{tag}Limpeza concluída em {sw.Elapsed.TotalSeconds:0.0}s | estimado={Fmt.Bytes(report.EstimatedFreedBytes)} | medido={Fmt.Bytes(report.MeasuredFreedBytes)}");
        return report;
    }
}

public static class Fmt
{
    private static readonly System.Globalization.CultureInfo PtBr = new("pt-BR");

    public static string Bytes(long bytes) => Bytes((double)bytes);
    public static string Bytes(ulong bytes) => Bytes((double)bytes);
    public static string Bytes(double b)
    {
        var sign = b < 0 ? "-" : "";
        b = Math.Abs(b);
        return b >= 1024d * 1024 * 1024
            ? sign + (b / (1024d * 1024 * 1024)).ToString("0.0", PtBr) + " GB"
            : sign + (b / (1024d * 1024)).ToString("0", PtBr) + " MB";
    }
}
