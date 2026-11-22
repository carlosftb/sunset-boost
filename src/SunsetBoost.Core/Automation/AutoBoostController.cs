using System.Diagnostics;
using SunsetBoost.Core.Cleanup;

namespace SunsetBoost.Core.Automation;

public enum AutoAction { None, Enter, Exit }

public sealed record AutoDecision(AutoAction Action, GameProfile? Profile);

/// <summary>
/// Decide quando ligar/desligar o boost sozinho. Lógica pura (sem tocar no Windows) pra dar pra testar:
/// - o jogo precisa estar aberto por <see cref="StartDelay"/> antes de ligar (o Valorant abre e fecha processos no início);
/// - precisa ficar fechado por <see cref="StopDelay"/> antes de restaurar (evita desfazer tudo numa reconexão);
/// - só restaura sozinho o que ele mesmo ligou. Boost ligado na mão continua até você desligar.
/// </summary>
public sealed class AutoBoostController
{
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(4);
    public TimeSpan StopDelay { get; init; } = TimeSpan.FromSeconds(10);

    private GameProfile? _seen;
    private DateTime _seenSince;
    private DateTime? _goneSince;
    private bool _weStarted;
    private bool _handledThisRun; // já ligou (ou o usuário desligou) nesta partida: não liga de novo

    public bool StartedByAuto => _weStarted;

    /// <param name="running">Perfil do jogo aberto agora (null = nenhum jogo).</param>
    /// <param name="boostActive">O Modo Jogo está ativo (por qualquer motivo).</param>
    public AutoDecision Tick(DateTime now, GameProfile? running, bool boostActive)
    {
        if (running is not null)
        {
            _goneSince = null;
            if (_seen?.Id != running.Id) { _seen = running; _seenSince = now; _handledThisRun = false; }

            if (!boostActive && !_handledThisRun && now - _seenSince >= StartDelay)
            {
                _handledThisRun = true;
                _weStarted = true;
                return new(AutoAction.Enter, running);
            }
            // O usuário desligou na mão durante a partida: respeita e não religa
            if (!boostActive && _weStarted) _weStarted = false;
            if (boostActive) _handledThisRun = true;
            return new(AutoAction.None, null);
        }

        // Nenhum jogo aberto
        if (_seen is not null)
        {
            _goneSince ??= now;
            if (now - _goneSince.Value >= StopDelay)
            {
                var finished = _seen;
                _seen = null;
                _handledThisRun = false;
                _goneSince = null;
                if (_weStarted && boostActive)
                {
                    _weStarted = false;
                    return new(AutoAction.Exit, finished);
                }
                _weStarted = false;
            }
        }
        return new(AutoAction.None, null);
    }
}

/// <summary>
/// Descobre se algum jogo conhecido está aberto. Usa só a lista de nomes do Windows (a mesma do Gerenciador
/// de Tarefas): não abre nenhum processo, nem o do jogo.
/// </summary>
public static class GameDetector
{
    public static GameProfile? FindRunning() => FindIn(RunningNames());

    internal static GameProfile? FindIn(ISet<string> running) =>
        GameProfiles.All.FirstOrDefault(p => p.GameProcessNames.Any(running.Contains));

    private static HashSet<string> RunningNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
            using (p) names.Add(p.ProcessName);
        return names;
    }
}
