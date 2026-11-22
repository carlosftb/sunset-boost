using SunsetBoost.Core.Cleanup;
using SunsetBoost.Core.Edge;
using SunsetBoost.Core.Processes;
using static SunsetBoost.Core.Tests.T;

namespace SunsetBoost.Core.Tests;

internal sealed class FixedSource(params ProcessInfo[] procs) : IProcessSource
{
    public IReadOnlyList<ProcessInfo> Snapshot() => procs;
}

public class BrowsersTests
{
    [Theory]
    [InlineData("edge", "msedge")] [InlineData("chrome", "chrome")] [InlineData("opera", "opera")]
    [InlineData("OperaGX", "opera")] [InlineData("gx", "opera")]
    public void Acha_o_navegador_pelo_nome(string key, string process) => Assert.Equal(process, Browsers.Find(key)!.Process);

    [Fact]
    public void Navegador_desconhecido_nao_tem_modo_leve() => Assert.Null(Browsers.Find("firefox"));

    [Fact]
    public void Cada_navegador_do_modo_leve_existe_no_catalogo_da_limpeza()
    {
        foreach (var b in Browsers.Lite)
        {
            var app = AppCatalog.FindById(b.AppId);
            Assert.NotNull(app);
            Assert.Contains(b.Process, app!.ProcessNames);
        }
    }

    [Fact]
    public void Limite_do_Chrome_so_olha_processos_do_Chrome_e_nunca_fecha_o_Chrome()
    {
        // PIDs que não existem: a linha de comando não é lida, então todos contam como "outro" processo do navegador
        var src = new FixedSource(
            P(4_000_001, "chrome", mb: 1500), P(4_000_002, "chrome", mb: 900),
            P(4_000_003, "msedge", mb: 5000), P(4_000_004, "opera", mb: 5000));
        var sim = new SimulatedSystemActions();

        var d = EdgeMemoryGuard.Enforce(2048 * MB, sim, src, Browsers.Chrome);

        Assert.Equal("Chrome", d.Browser);
        Assert.Equal(2400 * MB, d.TotalBytes);
        Assert.True(d.StillOver); // só o "esqueleto" já passa do limite...
        Assert.Empty(sim.Journal);  // ...e mesmo assim o Chrome não é fechado
    }

    [Fact]
    public void Opera_GX_abaixo_do_limite_nao_derruba_nada()
    {
        var src = new FixedSource(P(4_000_010, "opera", mb: 800), P(4_000_011, "chrome", mb: 9000));
        var sim = new SimulatedSystemActions();
        var d = EdgeMemoryGuard.Enforce(2048 * MB, sim, src, Browsers.Opera);
        Assert.Equal("Opera GX", d.Browser);
        Assert.Empty(d.Kill);
        Assert.Empty(sim.Journal);
    }
}

public class OperaTabsTests
{
    [Fact]
    public void Opera_GX_usa_clique_do_meio_e_sem_janela_nao_faz_nada()
    {
        Assert.False(Browsers.Opera.TabsClosable);
        Assert.True(Browsers.Chrome.TabsClosable && Browsers.Edge.TabsClosable);
        // Nenhum PID permitido: não toca em nenhum Opera aberto
        var r = EdgeTabs.KeepFirstTab(Browsers.Opera, new HashSet<int>());
        Assert.Equal(0, r.TabsClosed);
        Assert.Null(r.Error);
    }
}

public class BrowserRuleTests
{
    private static (BoostService Boost, string Dir) Make(bool lite)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sunsetboost-browser-" + Guid.NewGuid().ToString("N"));
        var settings = new Infrastructure.AppSettings { SimulationMode = true, EdgeLite = lite, PurgeStandbyList = false };
        var src = new FixedSource(P(4_100_001, "msedge", mb: 900), P(4_100_002, "chrome", mb: 500), P(4_100_003, "steam", mb: 300),
                                  P(4_100_004, "opera", mb: 700));
        return (new BoostService(new Infrastructure.AppPaths(dir), settings, Array.Empty<Tweaks.ITweak>(), src), dir);
    }

    [Fact]
    public void Limpar_agora_com_modo_leve_nao_fecha_os_navegadores()
    {
        var (boost, dir) = Make(lite: true);
        try
        {
            var report = boost.CleanNow();
            var closed = report.Groups.Select(g => g.Group.AppId).ToList();
            Assert.DoesNotContain("edge", closed);
            Assert.DoesNotContain("chrome", closed);
            Assert.Contains("steam", closed); // o resto da limpeza continua igual
            Assert.Contains("opera", closed); // Opera GX sempre fecha, mesmo no modo leve
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Limpar_agora_com_a_chave_ligada_fecha_os_navegadores()
    {
        var (boost, dir) = Make(lite: false);
        try
        {
            var closed = boost.CleanNow().Groups.Select(g => g.Group.AppId).ToList();
            Assert.Contains("edge", closed);
            Assert.Contains("chrome", closed);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
