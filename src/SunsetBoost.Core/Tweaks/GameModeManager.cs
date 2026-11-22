using System.Text.Json;
using SunsetBoost.Core.Infrastructure;

namespace SunsetBoost.Core.Tweaks;

public sealed class SnapshotEntry
{
    public string TweakId { get; set; } = "";
    public TweakState State { get; set; } = new();
    public bool Applied { get; set; }
}

/// <summary>A cópia das configurações de antes do Modo Jogo. Fica no disco enquanto o modo está ativo.</summary>
public sealed class SessionSnapshot
{
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string ProfileId { get; set; } = "";
    public List<SnapshotEntry> Entries { get; set; } = new();
}

public sealed record TweakOutcome(ITweak Tweak, TweakResult Result);

public sealed record GameModeResult(bool Ok, string Message, IReadOnlyList<TweakOutcome> Outcomes)
{
    public IEnumerable<TweakOutcome> Failures => Outcomes.Where(o => !o.Result.Ok);
}

/// <summary>
/// Liga e desliga o Modo Jogo com segurança:
/// 1) lê o estado atual de cada ajuste; 2) grava a cópia no disco; 3) só então aplica.
/// Se o PC travar no meio, a cópia continua no disco e o app oferece restaurar ao abrir.
/// </summary>
public sealed class GameModeManager
{
    private readonly IReadOnlyList<ITweak> _tweaks;
    private readonly string _snapshotFile;

    public GameModeManager(IReadOnlyList<ITweak> tweaks, string snapshotFile)
    {
        _tweaks = tweaks;
        _snapshotFile = snapshotFile;
    }

    public IReadOnlyList<ITweak> Tweaks => _tweaks;

    /// <summary>Tem uma cópia pendente (modo ativo, ou o app caiu sem restaurar).</summary>
    public bool IsActive => File.Exists(_snapshotFile);

    public SessionSnapshot? LoadSnapshot()
    {
        try
        {
            return File.Exists(_snapshotFile)
                ? JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(_snapshotFile), SettingsStore.Json)
                : null;
        }
        catch (Exception ex)
        {
            AppLog.Error("Snapshot ilegível", ex);
            return null;
        }
    }

    public GameModeResult Enter(TweakContext ctx, IEnumerable<string> enabledIds)
    {
        // Nunca sobrescreve uma cópia existente: senão o "estado original" viraria o estado já otimizado
        if (IsActive) return new(false, "O Modo Jogo já está ativo. Restaure antes de aplicar de novo.", Array.Empty<TweakOutcome>());

        var enabled = new HashSet<string>(enabledIds, StringComparer.OrdinalIgnoreCase);
        var selected = _tweaks.Where(t => enabled.Contains(t.Id)).ToList();

        // 1) e 2): captura tudo e grava ANTES de mexer em qualquer coisa
        var snapshot = new SessionSnapshot { ProfileId = ctx.Profile.Id };
        foreach (var t in selected)
        {
            t.Prepare(ctx);
            snapshot.Entries.Add(new SnapshotEntry { TweakId = t.Id, State = t.Capture() });
        }
        Save(snapshot);
        AppLog.Info($"Modo Jogo: cópia das configurações salva ({snapshot.Entries.Count} ajustes)");

        // 3) aplica um por um; falha em um não impede os outros
        var outcomes = new List<TweakOutcome>();
        foreach (var t in selected)
        {
            var entry = snapshot.Entries.First(e => e.TweakId == t.Id);
            TweakResult r;
            try { r = t.Apply(ctx); }
            catch (Exception ex) { r = TweakResult.Fail(ex.Message); }

            entry.Applied = r.Ok;
            if (r.RestoreData is not null)
                foreach (var (k, v) in r.RestoreData) entry.State[k] = v;
            Save(snapshot); // atualiza a cópia a cada passo
            outcomes.Add(new(t, r));
            AppLog.Write(r.Ok ? LogLevel.Info : LogLevel.Warn, $"Modo Jogo: {t.Title} → {(r.Ok ? "ok" : "FALHOU")}: {r.Message}");
        }

        var failures = outcomes.Count(o => !o.Result.Ok);
        return new(failures == 0, failures == 0 ? "Modo Jogo ativo" : $"Modo Jogo ativo com {failures} ajuste(s) que falharam", outcomes);
    }

    public GameModeResult Restore()
    {
        var snapshot = LoadSnapshot();
        if (snapshot is null)
        {
            if (File.Exists(_snapshotFile)) File.Move(_snapshotFile, _snapshotFile + ".corrompido", overwrite: true);
            return new(true, "Nada pra restaurar", Array.Empty<TweakOutcome>());
        }

        var outcomes = new List<TweakOutcome>();
        var remaining = new List<SnapshotEntry>();
        // Ordem inversa: desfaz do último pro primeiro
        foreach (var entry in Enumerable.Reverse(snapshot.Entries))
        {
            var t = _tweaks.FirstOrDefault(x => x.Id == entry.TweakId);
            if (t is null) { AppLog.Warn($"Restaurar: ajuste desconhecido {entry.TweakId}"); continue; }

            TweakResult r;
            try { r = t.Restore(entry.State); }
            catch (Exception ex) { r = TweakResult.Fail(ex.Message); }

            outcomes.Add(new(t, r));
            if (!r.Ok) remaining.Insert(0, entry);
            AppLog.Write(r.Ok ? LogLevel.Info : LogLevel.Warn, $"Restaurar: {t.Title} → {(r.Ok ? "ok" : "FALHOU")}: {r.Message}");
        }

        if (remaining.Count == 0)
        {
            File.Delete(_snapshotFile);
            return new(true, "Tudo restaurado", outcomes);
        }
        // Mantém só o que falhou, pra tentar de novo depois
        snapshot.Entries = remaining;
        Save(snapshot);
        return new(false, $"{remaining.Count} ajuste(s) não voltaram; dá pra tentar de novo", outcomes);
    }

    /// <summary>Estado atual de todos os ajustes (pra conferir se a restauração voltou idêntico).</summary>
    public Dictionary<string, TweakState> Fingerprint(TweakContext ctx) =>
        _tweaks.ToDictionary(t => t.Id, t => { t.Prepare(ctx); return t.Capture(); });

    private void Save(SessionSnapshot s) => AtomicFile.WriteAllText(_snapshotFile, JsonSerializer.Serialize(s, SettingsStore.Json));
}
