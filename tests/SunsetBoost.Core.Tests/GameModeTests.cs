using System.Diagnostics;
using SunsetBoost.Core.Cleanup;
using SunsetBoost.Core.Tweaks;

namespace SunsetBoost.Core.Tests;

internal sealed class FakeRegistry : IRegistryAccess
{
    public Dictionary<string, int> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int? GetDword(string path, string name) => Values.TryGetValue(path + "\\" + name, out var v) ? v : null;
    public void SetDword(string path, string name, int value) => Values[path + "\\" + name] = value;
    public void DeleteValue(string path, string name) => Values.Remove(path + "\\" + name);
}

internal sealed class FakeServices : IServiceAccess
{
    public Dictionary<string, bool> Running { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WSearch"] = true, ["SysMain"] = true, ["Spooler"] = false,
    };
    public HashSet<string> FailOnStop { get; } = new();
    public bool Exists(string name) => Running.ContainsKey(name);
    public bool IsRunning(string name) => Running[name];
    public void Stop(string name) { if (FailOnStop.Contains(name)) throw new InvalidOperationException("acesso negado"); Running[name] = false; }
    public void Start(string name) => Running[name] = true;
}

internal sealed class FakePower : IPowerPlans
{
    public static readonly Guid High = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public Guid Active { get; set; } = High;
    public HashSet<Guid> Schemes { get; } = new() { High };
    public Guid GetActive() => Active;
    public void SetActive(Guid s) => Active = s;
    public Guid Duplicate(Guid source) { var g = Guid.NewGuid(); Schemes.Add(g); return g; }
    public void Delete(Guid s) => Schemes.Remove(s);
    public bool Exists(Guid s) => Schemes.Contains(s);
}

internal sealed class ExplodingTweak : ITweak
{
    public string Id => "explode";
    public string Title => "Explode";
    public string Detail => "";
    public Impact Impact => Impact.Baixo;
    public bool FailRestore { get; set; }
    public TweakState Capture() => new() { ["x"] = "1" };
    public TweakResult Apply(TweakContext ctx) => throw new InvalidOperationException("boom");
    public TweakResult Restore(TweakState saved) => FailRestore ? TweakResult.Fail("ainda quebrado") : TweakResult.Done("ok");
}

public class GameModeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sunsetboost-tests-" + Guid.NewGuid().ToString("N"));
    private string SnapFile => Path.Combine(_dir, "snapshot.json");
    private readonly FakeRegistry _reg = new();
    private readonly FakeServices _svc = new();
    private readonly FakePower _power = new();
    private static readonly GameProfile TestGame = new("teste", "Jogo teste", new[] { "jogoteste" }, Array.Empty<string>());
    private static readonly TweakContext Ctx = new(TestGame);

    public GameModeTests()
    {
        Directory.CreateDirectory(_dir);
        _reg.Values[@"System\GameConfigStore\GameDVR_Enabled"] = 1; // DVR ligado; AppCaptureEnabled não existe
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private GameModeManager Manager(params ITweak[] extra) =>
        new(TweakCatalog.Create(_reg, _svc, _power).Concat(extra).ToList(), SnapFile);

    private static IEnumerable<string> AllIds(GameModeManager m) => m.Tweaks.Select(t => t.Id);

    [Fact]
    public void Aplicar_e_restaurar_volta_tudo_identico()
    {
        var m = Manager();
        var before = m.Fingerprint(Ctx);

        var enter = m.Enter(Ctx, AllIds(m));
        Assert.True(enter.Ok, string.Join("; ", enter.Failures.Select(f => f.Result.Message)));
        Assert.Equal(0, _reg.GetDword(@"System\GameConfigStore", "GameDVR_Enabled"));
        Assert.False(_svc.Running["WSearch"]);
        Assert.NotEqual(FakePower.High, _power.Active);

        var exit = m.Restore();
        Assert.True(exit.Ok);
        var after = m.Fingerprint(Ctx);
        foreach (var (id, state) in before) Assert.True(state.SameAs(after[id]), $"{id}: {state} ≠ {after[id]}");

        // Detalhes que o fingerprint não mostra
        Assert.Null(_reg.GetDword(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled")); // não existia → apagado
        Assert.Equal(new[] { FakePower.High }, _power.Schemes); // plano temporário apagado
        Assert.False(_svc.Running["Spooler"]); // estava parado antes: continua parado
        Assert.False(File.Exists(SnapFile));
    }

    [Fact]
    public void Copia_e_gravada_no_disco_antes_de_aplicar()
    {
        var m = Manager(new ExplodingTweak());
        m.Enter(Ctx, new[] { "explode", "game-dvr" });
        var snap = m.LoadSnapshot()!;
        Assert.Contains(snap.Entries, e => e.TweakId == "explode" && !e.Applied);
        Assert.Contains(snap.Entries, e => e.TweakId == "game-dvr" && e.Applied && e.State.Values.Contains("1"));
    }

    [Fact]
    public void Entrar_duas_vezes_nao_sobrescreve_o_estado_original()
    {
        var m = Manager();
        m.Enter(Ctx, new[] { "game-dvr" });
        var second = m.Enter(Ctx, new[] { "game-dvr" });
        Assert.False(second.Ok);

        m.Restore();
        Assert.Equal(1, _reg.GetDword(@"System\GameConfigStore", "GameDVR_Enabled")); // original, não o já otimizado
    }

    [Fact]
    public void Depois_de_um_crash_outra_instancia_encontra_a_copia_e_restaura()
    {
        Manager().Enter(Ctx, new[] { "game-dvr", "servicos" });
        // "PC travou": um manager novo, como se o app tivesse acabado de abrir
        var fresh = Manager();
        Assert.True(fresh.IsActive);
        Assert.True(fresh.Restore().Ok);
        Assert.Equal(1, _reg.GetDword(@"System\GameConfigStore", "GameDVR_Enabled"));
        Assert.True(_svc.Running["WSearch"]);
    }

    [Fact]
    public void Falha_num_ajuste_nao_impede_os_outros()
    {
        _svc.FailOnStop.Add("SysMain");
        var m = Manager();
        var r = m.Enter(Ctx, AllIds(m));
        Assert.False(r.Ok);
        Assert.Single(r.Failures);
        Assert.Equal(0, _reg.GetDword(@"System\GameConfigStore", "GameDVR_Enabled"));
        Assert.True(m.Restore().Ok);
    }

    [Fact]
    public void Restauracao_que_falha_fica_guardada_pra_tentar_de_novo()
    {
        var boom = new ExplodingTweak { FailRestore = true };
        var m = Manager(boom);
        m.Enter(Ctx, new[] { "explode", "game-dvr" });

        Assert.False(m.Restore().Ok);
        Assert.Equal(new[] { "explode" }, m.LoadSnapshot()!.Entries.Select(e => e.TweakId));

        boom.FailRestore = false;
        Assert.True(m.Restore().Ok);
        Assert.False(m.IsActive);
    }

    [Fact]
    public void Plano_de_energia_vem_desligado_por_padrao()
    {
        var m = Manager();
        Assert.False(m.Tweaks.Single(t => t.Id == "plano-energia").EnabledByDefault);
    }

    [Fact]
    public void Modo_jogo_nao_tem_mais_ajuste_que_mexe_no_processo_do_jogo()
    {
        Assert.DoesNotContain("prioridade-jogo", AllIds(Manager()));
    }

    [Fact]
    public void Copia_antiga_com_ajuste_de_prioridade_restaura_sem_travar()
    {
        Manager().Enter(Ctx, new[] { "game-dvr" });
        var snap = File.ReadAllText(SnapFile).Replace("\"game-dvr\"", "\"prioridade-jogo\"");
        File.WriteAllText(SnapFile, snap);
        var r = Manager().Restore();
        Assert.True(r.Ok);
        Assert.False(File.Exists(SnapFile));
    }

    [Fact]
    public void Copia_alterada_nao_liga_servico_fora_da_lista()
    {
        _svc.Running["RemoteRegistry"] = false;
        Manager().Enter(Ctx, new[] { "servicos" });
        // Um programa sem admin edita o snapshot pra o app (admin) ligar outro serviço
        var snap = File.ReadAllText(SnapFile).Replace("\"WSearch\"", "\"RemoteRegistry\"");
        File.WriteAllText(SnapFile, snap);

        var r = Manager().Restore();

        Assert.True(r.Ok);
        Assert.False(_svc.Running["RemoteRegistry"]);
        Assert.True(_svc.Running["SysMain"]);
        Assert.False(File.Exists(SnapFile));
    }
}
