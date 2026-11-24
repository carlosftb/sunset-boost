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
    public void Navegador_desconhecido_nao_tem_limite() => Assert.Null(Browsers.Find("firefox"));

    [Fact]
    public void Cada_navegador_com_limite_existe_no_catalogo_da_limpeza()
    {
        foreach (var b in Browsers.Limited)
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
    private static (BoostService Boost, string Dir) Make(bool limit)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sunsetboost-browser-" + Guid.NewGuid().ToString("N"));
        var settings = new Infrastructure.AppSettings { SimulationMode = true, EdgeLite = limit, PurgeStandbyList = false };
        var src = new FixedSource(P(4_100_001, "msedge", mb: 900), P(4_100_002, "chrome", mb: 500), P(4_100_003, "steam", mb: 300),
                                  P(4_100_004, "opera", mb: 700), P(4_100_005, "firefox", mb: 600));
        return (new BoostService(new Infrastructure.AppPaths(dir), settings, Array.Empty<Tweaks.ITweak>(), src), dir);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Com_a_chave_ligada_ou_desligada_nenhum_navegador_e_fechado(bool limit)
    {
        var (boost, dir) = Make(limit);
        try
        {
            var closed = boost.CleanNow().Groups.Select(g => g.Group.AppId).ToList();
            foreach (var id in new[] { "edge", "chrome", "opera", "firefox" }) Assert.DoesNotContain(id, closed);
            Assert.Contains("steam", closed); // o resto da limpeza continua igual
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Limite_vale_pro_Edge_Chrome_e_Opera_GX_com_2_GB()
    {
        Assert.Equal(new[] { "msedge", "chrome", "opera" }, Browsers.Limited.Select(b => b.Process));
        Assert.Equal("Edge, Chrome e Opera GX", Browsers.Names);
        Assert.Equal(2048, new Infrastructure.AppSettings().EdgeLimitMB);
    }
}
