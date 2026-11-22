namespace SunsetBoost.Core.Tweaks;

/// <summary>Base pros ajustes que só mexem em valores DWORD do HKCU.</summary>
public abstract class RegistryTweak : ITweak
{
    private readonly IRegistryAccess _reg;
    protected RegistryTweak(IRegistryAccess reg) => _reg = reg;

    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Detail { get; }
    public abstract Impact Impact { get; }
    protected abstract (string Path, string Name, int Value)[] Values { get; }

    private static string Key(string path, string name) => path + "\\" + name;

    public bool? AlreadyOptimal() => Values.All(v => _reg.GetDword(v.Path, v.Name) == v.Value);

    public TweakState Capture()
    {
        var s = new TweakState();
        foreach (var (path, name, _) in Values) s[Key(path, name)] = _reg.GetDword(path, name)?.ToString();
        return s;
    }

    public TweakResult Apply(TweakContext ctx)
    {
        foreach (var (path, name, value) in Values) _reg.SetDword(path, name, value);
        return TweakResult.Done("aplicado");
    }

    public TweakResult Restore(TweakState saved)
    {
        foreach (var (path, name, _) in Values)
        {
            if (!saved.TryGetValue(Key(path, name), out var old)) continue;
            if (old is null) _reg.DeleteValue(path, name);
            else _reg.SetDword(path, name, int.Parse(old));
        }
        return TweakResult.Done("restaurado");
    }
}

public sealed class GameDvrTweak(IRegistryAccess reg) : RegistryTweak(reg)
{
    public override string Id => "game-dvr";
    public override string Title => "Desligar gravação em segundo plano do Xbox (Game DVR)";
    public override string Detail => "O Game Bar para de gravar os últimos minutos do jogo o tempo todo.";
    public override Impact Impact => Impact.Medio;
    protected override (string, string, int)[] Values => new[]
    {
        (@"System\GameConfigStore", "GameDVR_Enabled", 0),
        (@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0),
    };
}

public sealed class WindowsGameModeTweak(IRegistryAccess reg) : RegistryTweak(reg)
{
    public override string Id => "modo-jogo-windows";
    public override string Title => "Ativar o Modo de Jogo do Windows";
    public override string Detail => "Configuração nativa: o Windows segura atualizações e dá preferência ao jogo.";
    public override Impact Impact => Impact.Baixo;
    protected override (string, string, int)[] Values => new[]
    {
        (@"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1),
    };
}

public sealed class DoNotDisturbTweak(IRegistryAccess reg) : RegistryTweak(reg)
{
    public override string Id => "nao-perturbe";
    public override string Title => "Não Perturbe";
    public override string Detail => "Desliga os pop-ups de notificação (banners) enquanto você joga.";
    public override Impact Impact => Impact.Conforto;
    protected override (string, string, int)[] Values => new[]
    {
        (@"Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled", 0),
    };
}

/// <summary>Pausa serviços que trabalham em segundo plano. Não muda o tipo de inicialização: reiniciar o PC também volta tudo.</summary>
public sealed class BackgroundServicesTweak(IServiceAccess svc) : ITweak
{
    public static readonly string[] Services = { "WSearch", "SysMain", "Spooler" };

    public string Id => "servicos";
    public string Title => "Pausar Windows Search, SysMain e Spooler";
    public string Detail => "Indexação, pré-carregamento e impressão param só durante o jogo.";
    public Impact Impact => Impact.Baixo;

    public bool? AlreadyOptimal() => Services.Where(svc.Exists).All(s => !svc.IsRunning(s));

    public TweakState Capture()
    {
        var s = new TweakState();
        foreach (var name in Services.Where(svc.Exists)) s[name] = svc.IsRunning(name) ? "rodando" : "parado";
        return s;
    }

    public TweakResult Apply(TweakContext ctx)
    {
        var errors = new List<string>();
        foreach (var name in Services.Where(svc.Exists))
        {
            try { if (svc.IsRunning(name)) svc.Stop(name); }
            catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
        }
        return errors.Count == 0 ? TweakResult.Done("serviços pausados") : TweakResult.Fail(string.Join("; ", errors));
    }

    public TweakResult Restore(TweakState saved)
    {
        var errors = new List<string>();
        foreach (var (name, state) in saved)
        {
            if (name.StartsWith('_') || state != "rodando") continue;
            // O snapshot fica numa pasta que qualquer programa do usuário (sem admin) consegue alterar:
            // só religa os serviços que este ajuste pausa, nunca um nome vindo do arquivo.
            if (!Services.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                Infrastructure.AppLog.Warn($"Restaurar: serviço \"{name}\" não é deste ajuste (cópia alterada?): ignorado");
                continue;
            }
            try { svc.Start(name); }
            catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
        }
        return errors.Count == 0 ? TweakResult.Done("serviços religados") : TweakResult.Fail(string.Join("; ", errors));
    }
}

/// <summary>Plano "Desempenho Máximo" (Ultimate Performance). Cria uma cópia temporária e apaga ao restaurar.</summary>
public sealed class PowerPlanTweak(IPowerPlans power) : ITweak
{
    public static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    internal const string CreatedKey = "_criado";

    public string Id => "plano-energia";
    public string Title => "Plano de energia \"Desempenho Máximo\"";
    public string Detail => "Ganho pequeno se você já usa Alto desempenho.";
    public Impact Impact => Impact.Baixo;
    public bool EnabledByDefault => false;

    public TweakState Capture() => new() { ["ativo"] = power.GetActive().ToString() };

    public TweakResult Apply(TweakContext ctx)
    {
        var created = power.Duplicate(UltimatePerformance);
        power.SetActive(created);
        return new TweakResult(true, "Desempenho Máximo ativo") { RestoreData = new() { [CreatedKey] = created.ToString() } };
    }

    public TweakResult Restore(TweakState saved)
    {
        if (saved.TryGetValue("ativo", out var old) && old is not null) power.SetActive(Guid.Parse(old));
        if (saved.TryGetValue(CreatedKey, out var created) && created is not null && power.Exists(Guid.Parse(created)))
            power.Delete(Guid.Parse(created));
        return TweakResult.Done("plano anterior de volta");
    }
}

public static class TweakCatalog
{
    // Prioridade do jogo não é mais um ajuste do Modo Jogo: virou regra fixa do Windows (PriorityRules),
    // que o próprio Windows aplica quando o jogo abre. Assim o app nunca procura nem abre o processo do jogo.
    public static IReadOnlyList<ITweak> CreateReal() => Create(new RealRegistry(), new RealServices(), new RealPowerPlans());

    public static IReadOnlyList<ITweak> Create(IRegistryAccess reg, IServiceAccess svc, IPowerPlans power) => new ITweak[]
    {
        new GameDvrTweak(reg),
        new BackgroundServicesTweak(svc),
        new WindowsGameModeTweak(reg),
        new PowerPlanTweak(power),
        new DoNotDisturbTweak(reg),
    };
}
