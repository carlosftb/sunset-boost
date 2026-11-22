using SunsetBoost.Core.Cleanup;
using static SunsetBoost.Core.Tests.T;

namespace SunsetBoost.Core.Tests;

public class SafetyRulesTests
{
    public static IEnumerable<object[]> AllProtectedNames() =>
        SafetyRules.CriticalSystem
            .Concat(SafetyRules.Security)
            .Concat(SafetyRules.AntiCheat)
            .Concat(SafetyRules.Drivers)
            .Distinct()
            .Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(AllProtectedNames))]
    public void Nomes_protegidos_nunca_podem_fechar_mesmo_fora_do_Windows(string name)
    {
        // Caminho fora de C:\Windows e sessão do usuário: só o nome já tem que bastar
        var p = P(5000, name, session: 1, path: $@"D:\Qualquer\{name}.exe");
        Assert.NotNull(SafetyRules.Check(p, Ctx()));
    }

    [Theory]
    [InlineData("vgc")] [InlineData("vgtray")] [InlineData("easyanticheat_eos")] [InlineData("beservice_x64")]
    [InlineData("faceitclient")] [InlineData("easyanticheat_v2")] [InlineData("eaanticheat.gameservicelauncher")]
    public void Anti_cheat_e_protegido(string name) =>
        Assert.Equal("anti-cheat", SafetyRules.Check(P(5000, name), Ctx()));

    [Fact]
    public void Defender_e_protegido_pelo_nome_e_pelo_caminho()
    {
        Assert.NotNull(SafetyRules.Check(P(5000, "defendersessionhelper"), Ctx()));
        Assert.NotNull(SafetyRules.Check(P(5001, "novohelper", path: @"C:\ProgramData\Microsoft\Windows Defender\Platform\4.18\novohelper.exe"), Ctx()));
    }

    [Fact]
    public void Servico_em_sessao_0_e_protegido() =>
        Assert.NotNull(SafetyRules.Check(P(5000, "steamservice", session: 0), Ctx()));

    [Fact]
    public void Qualquer_coisa_dentro_da_pasta_do_Windows_e_protegida() =>
        Assert.Equal("componente do Windows",
            SafetyRules.Check(P(5000, "algumacoisa", path: @"C:\WINDOWS\System32\algumacoisa.exe"), Ctx()));

    [Fact]
    public void Processo_sem_acesso_ao_caminho_e_protegido() =>
        Assert.NotNull(SafetyRules.Check(P(5000, "misterioso", path: null), Ctx()));

    [Fact]
    public void Jogo_conhecido_e_protegido_mesmo_com_perfil_de_outro_jogo()
    {
        Assert.Equal("o jogo", SafetyRules.Check(P(5000, "valorant-win64-shipping"), Ctx(GameProfiles.Valorant)));
        Assert.Equal("o jogo", SafetyRules.Check(P(5000, "valorant-win64-shipping"), Ctx(GameProfiles.Steam)));
        Assert.Equal("o jogo", SafetyRules.Check(P(5000, "fortniteclient-win64-shipping"), Ctx(GameProfiles.Generic)));
    }

    [Fact]
    public void O_proprio_app_e_quem_o_abriu_sao_protegidos()
    {
        var snapshot = new[]
        {
            P(Environment.ProcessId, "sunsetboost", parent: 7777),
            P(7777, "terminal", parent: 8888),
            P(8888, "claude", parent: 0),
        };
        var ctx = SafetyContext.ForCurrentProcess(snapshot, GameProfiles.Valorant);
        Assert.Contains(7777, ctx.ProtectedPids);
        Assert.Contains(8888, ctx.ProtectedPids);
        Assert.NotNull(SafetyRules.Check(snapshot[2], ctx));
    }

    [Fact]
    public void Navegador_comum_nao_e_protegido() =>
        Assert.Null(SafetyRules.Check(P(5000, "msedge"), Ctx()));
}
