using SunsetBoost.Core.Cleanup;
using SunsetBoost.Core.Infrastructure;
using SunsetBoost.Core.Processes;

namespace SunsetBoost.Core.Edge;

public enum EdgeProcKind { Browser, Tab, Extension, Gpu, Other }

public sealed record EdgeProc(ProcessInfo Info, EdgeProcKind Kind);

/// <param name="StillOver">Mesmo derrubando as abas e extensões, o navegador continua acima do limite (o resto é o "esqueleto").</param>
public sealed record GuardDecision(long TotalBytes, IReadOnlyList<EdgeProc> Kill, bool StillOver)
{
    /// <summary>Nome do navegador (pra tela e log).</summary>
    public string Browser { get; init; } = "Edge";
}

/// <summary>
/// Limite rígido de memória por navegador (Edge, Chrome, Opera GX: todos Chromium, mesmos tipos de processo).
/// O próprio Windows não consegue pôr as abas já abertas num teto só (elas já estão em grupos isolados
/// do navegador), então o app vigia a cada 2 s: passou do limite → derruba as abas que mais gastam;
/// depois extensões. Nunca fecha o navegador inteiro.
/// </summary>
public static class EdgeMemoryGuard
{
    public static EdgeProcKind Classify(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine)) return EdgeProcKind.Other;
        if (!commandLine.Contains("--type=")) return EdgeProcKind.Browser;
        if (commandLine.Contains("--type=renderer"))
            return commandLine.Contains("--extension-process") ? EdgeProcKind.Extension : EdgeProcKind.Tab;
        if (commandLine.Contains("--type=gpu-process")) return EdgeProcKind.Gpu;
        return EdgeProcKind.Other;
    }

    /// <summary>Lógica pura: o que derrubar pra voltar ao limite.</summary>
    public static GuardDecision Decide(IReadOnlyList<EdgeProc> procs, long limitBytes)
    {
        long total = procs.Sum(p => p.Info.PrivateBytes);
        if (total <= limitBytes) return new(total, Array.Empty<EdgeProc>(), false);

        var kill = new List<EdgeProc>();
        long remaining = total;
        foreach (var kind in new[] { EdgeProcKind.Tab, EdgeProcKind.Extension })
            foreach (var p in procs.Where(p => p.Kind == kind).OrderByDescending(p => p.Info.PrivateBytes))
            {
                if (remaining <= limitBytes) break;
                kill.Add(p);
                remaining -= p.Info.PrivateBytes;
            }

        // Só o "esqueleto" (janela principal, GPU, rede...) ainda passa do limite: fica assim, o navegador não é fechado
        return new(total, kill, remaining > limitBytes);
    }

    /// <summary>Uma verificação. Devolve a decisão tomada (pra log/tela).</summary>
    public static GuardDecision Enforce(long limitBytes, ISystemActions sys, IProcessSource? source = null, BrowserDef? browser = null)
    {
        browser ??= Browsers.Edge;
        var proc = browser.Process;
        // Sem fonte de teste: lê SÓ os processos desse navegador; nenhum outro processo é aberto (nem o jogo)
        var all = source?.Snapshot() ?? new ProcessScanner().Snapshot(n => n == proc);
        var edge = all
            .Where(p => p.Name == proc && p.SessionId != 0)
            .Select(p => new EdgeProc(p, Classify(ProcessCommandLine.Read(p.Pid))))
            .ToList();
        var d = Decide(edge, limitBytes) with { Browser = browser.Name };
        if (d.Kill.Count == 0) return d;

        var tag = sys.IsSimulation ? "[SIMULAÇÃO] " : "";
        AppLog.Warn($"{tag}{browser.Name} em {Fmt.Bytes(d.TotalBytes)}, acima do limite de {Fmt.Bytes(limitBytes)}: " +
                    (d.StillOver ? $"derrubando {d.Kill.Count} aba(s)/extensão(ões); o resto do {browser.Name} continua acima do limite, mas ele não é fechado" : $"derrubando {d.Kill.Count} processo(s) ({string.Join(", ", d.Kill.Select(k => $"{k.Kind} {Fmt.Bytes(k.Info.PrivateBytes)}"))})"));
        foreach (var k in d.Kill)
            if (!sys.Kill(k.Info, out var err)) AppLog.Warn($"{browser.Name}: não consegui derrubar PID {k.Info.Pid}: {err}");
        return d;
    }
}
