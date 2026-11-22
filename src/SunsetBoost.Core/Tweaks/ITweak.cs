using SunsetBoost.Core.Cleanup;

namespace SunsetBoost.Core.Tweaks;

public enum Impact { Alto, Medio, Baixo, Conforto }

/// <summary>Estado bruto de um ajuste (ex.: {"GameDVR_Enabled": "1"}). null = valor não existia.</summary>
public sealed class TweakState : Dictionary<string, string?>
{
    public TweakState() : base(StringComparer.OrdinalIgnoreCase) { }
    public TweakState(IDictionary<string, string?> d) : base(d, StringComparer.OrdinalIgnoreCase) { }

    public bool SameAs(TweakState other) =>
        Count == other.Count && this.All(kv => other.TryGetValue(kv.Key, out var v) && v == kv.Value);

    public override string ToString() => string.Join(", ", this.Select(kv => $"{kv.Key}={kv.Value ?? "(ausente)"}"));
}

public sealed record TweakContext(GameProfile Profile);

public sealed record TweakResult(bool Ok, string Message)
{
    /// <summary>Informação extra que só existe depois de aplicar (ex.: GUID do plano criado). Vai pro snapshot.</summary>
    public TweakState? RestoreData { get; init; }

    public static TweakResult Done(string msg) => new(true, msg);
    public static TweakResult Skip(string msg) => new(true, msg);
    public static TweakResult Fail(string msg) => new(false, msg);
}

/// <summary>
/// Um ajuste do Modo Jogo. Três ações: ver como está (Capture), aplicar e desfazer.
/// Restore recebe exatamente o que Capture devolveu antes do Apply.
/// </summary>
public interface ITweak
{
    string Id { get; }
    string Title { get; }
    string Detail { get; }
    Impact Impact { get; }
    bool EnabledByDefault => true;

    /// <summary>Chamado antes do Capture (ex.: pra saber qual é o jogo do perfil).</summary>
    void Prepare(TweakContext ctx) { }

    /// <summary>true se o Windows já está do jeito que o ajuste deixaria (aplicar não muda nada). null = não dá pra saber.</summary>
    bool? AlreadyOptimal() => null;

    TweakState Capture();
    TweakResult Apply(TweakContext ctx);
    TweakResult Restore(TweakState saved);
}
