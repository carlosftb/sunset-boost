using SunsetBoost.Core.Cleanup;
using SunsetBoost.Core.Infrastructure;
using static SunsetBoost.Core.Tests.T;

namespace SunsetBoost.Core.Tests;

public class CleanupPlannerTests
{
    private static AppGroup Group(CleanupPlan plan, string id) => plan.Groups.Single(g => g.AppId == id);

    [Fact]
    public void Edge_com_varios_processos_vira_um_app_que_fecha_por_padrao()
    {
        var snap = new[] { P(10, "msedge", mb: 500), P(11, "msedge", parent: 10, mb: 300), P(12, "msedge", parent: 10, mb: 200) };
        var g = Group(CleanupPlanner.Build(snap, Ctx()), "edge");
        Assert.Equal(CleanupList.CloseByDefault, g.List);
        Assert.True(g.WillClose);
        Assert.Equal(3, g.Processes.Count);
        Assert.Equal(1000 * MB, g.PrivateBytes);
    }

    [Fact]
    public void Steam_fecha_no_perfil_Valorant_e_fica_no_perfil_Steam()
    {
        var snap = new[] { P(10, "steam"), P(11, "steamwebhelper", parent: 10) };
        Assert.True(Group(CleanupPlanner.Build(snap, Ctx(GameProfiles.Valorant)), "steam").WillClose);

        var keep = Group(CleanupPlanner.Build(snap, Ctx(GameProfiles.Steam)), "steam");
        Assert.Equal(CleanupList.NeverClose, keep.List);
        Assert.False(keep.WillClose);
    }

    [Fact]
    public void Riot_Client_fica_no_perfil_Valorant()
    {
        var snap = new[] { P(10, "riotclientservices"), P(11, "riotclientuxrender", parent: 10) };
        var g = Group(CleanupPlanner.Build(snap, Ctx(GameProfiles.Valorant)), "riot");
        Assert.Equal(CleanupList.NeverClose, g.List);
        Assert.False(g.WillClose);
    }

    [Fact]
    public void Discord_fica_aberto_ate_o_usuario_escolher_fechar()
    {
        var snap = new[] { P(10, "discord") };
        Assert.False(Group(CleanupPlanner.Build(snap, Ctx()), "discord").WillClose);

        var decisions = new Dictionary<string, AppDecision> { ["discord"] = AppDecision.Close };
        Assert.True(Group(CleanupPlanner.Build(snap, Ctx(), decisions), "discord").WillClose);
    }

    [Fact]
    public void Usuario_pode_manter_um_app_que_fecharia_por_padrao()
    {
        var decisions = new Dictionary<string, AppDecision> { ["edge"] = AppDecision.Keep };
        Assert.False(Group(CleanupPlanner.Build(new[] { P(10, "msedge") }, Ctx(), decisions), "edge").WillClose);
    }

    [Fact]
    public void Escolha_do_usuario_nao_passa_por_cima_da_protecao()
    {
        var snap = new[] { P(10, "vgtray"), P(11, "explorer", path: @"C:\Windows\explorer.exe") };
        var decisions = new Dictionary<string, AppDecision>
        {
            ["exe:vgtray"] = AppDecision.Close, ["exe:explorer"] = AppDecision.Close,
        };
        var plan = CleanupPlanner.Build(snap, Ctx(), decisions);
        Assert.All(plan.Groups, g => Assert.False(g.WillClose));
        Assert.All(plan.Groups, g => Assert.Equal(CleanupList.NeverClose, g.List));
    }

    [Fact]
    public void App_desconhecido_vai_pra_voce_decide_e_fica_aberto()
    {
        var g = Group(CleanupPlanner.Build(new[] { P(10, "meuprograma") }, Ctx()), "exe:meuprograma");
        Assert.Equal(CleanupList.UserDecides, g.List);
        Assert.False(g.WillClose);
    }

    [Fact]
    public void WebView2_entra_no_grupo_do_app_que_o_abriu()
    {
        var snap = new[] { P(10, "widgets"), P(11, "msedgewebview2", parent: 10), P(12, "msedgewebview2", parent: 11) };
        var g = Group(CleanupPlanner.Build(snap, Ctx()), "widgets");
        Assert.Equal(3, g.Processes.Count);
    }

    [Fact]
    public void WebView2_de_componente_do_Windows_fica_protegido()
    {
        var snap = new[]
        {
            P(10, "searchhost", path: @"C:\Windows\SystemApps\Search\searchhost.exe"),
            P(11, "msedgewebview2", parent: 10),
        };
        var plan = CleanupPlanner.Build(snap, Ctx());
        Assert.Single(plan.Groups);
        Assert.Equal(CleanupList.NeverClose, plan.Groups[0].List);
    }

    [Fact]
    public void Estimativa_soma_so_o_que_vai_fechar()
    {
        var snap = new[] { P(10, "msedge", mb: 1000), P(20, "discord", mb: 400), P(30, "vgtray", mb: 50) };
        Assert.Equal(1000 * MB, CleanupPlanner.Build(snap, Ctx()).EstimatedFreedBytes);
    }
}
