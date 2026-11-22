using System.Diagnostics;
using SunsetBoost.Core.Cleanup;
using SunsetBoost.Core.Processes;

namespace SunsetBoost.Core.Tests;

public class HandsOffTests
{
    [Theory]
    [InlineData("vgc")] [InlineData("vgtray")] [InlineData("valorant-win64-shipping")] [InlineData("valorant")]
    [InlineData("riotclientservices")] [InlineData("riotclientuxrender")] [InlineData("easyanticheat_eos")]
    [InlineData("beservice")] [InlineData("faceitclient")] [InlineData("fortniteclient-win64-shipping")] [InlineData("league of legends")]
    public void Jogo_anti_cheat_e_Riot_ficam_sem_acesso(string name) => Assert.True(HandsOff.IsHandsOff(name));

    [Theory]
    [InlineData("msedge")] [InlineData("steam")] [InlineData("discord")] [InlineData("explorer")]
    public void Apps_comuns_continuam_sendo_lidos(string name) => Assert.False(HandsOff.IsHandsOff(name));

    [Fact]
    public void Processo_sem_acesso_vai_pra_nunca_fecha_mesmo_se_o_usuario_mandar_fechar()
    {
        var riot = new ProcessInfo(10, 1, "riotclientservices", null, 1, 0, 0);
        var ctx = new SafetyContext { Profile = GameProfiles.Steam, WindowsDir = @"C:\Windows" };
        var plan = CleanupPlanner.Build(new[] { riot }, ctx,
            new Dictionary<string, Infrastructure.AppDecision> { ["riot"] = Infrastructure.AppDecision.Close });
        Assert.False(plan.Groups.Single().WillClose);
        Assert.Equal(CleanupList.NeverClose, plan.Groups.Single().List);
    }
}

[Trait("Category", "Integration")]
public class HandsOffIntegrationTests
{
    [Fact]
    public void Scanner_nao_le_nada_de_um_processo_com_nome_de_anti_cheat()
    {
        // Cópia do ping com o nome do ícone do Vanguard: o scanner tem que ver só o nome
        var dir = Path.Combine(Path.GetTempPath(), "sunsetboost-handsoff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var fake = Path.Combine(dir, "vgtray.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), fake);
        using var proc = Process.Start(new ProcessStartInfo(fake, "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            var seen = new ProcessScanner().Snapshot().Single(p => p.Pid == proc.Id);
            Assert.Equal("vgtray", seen.Name);
            Assert.Null(seen.ImagePath);
            Assert.Equal(0, seen.PrivateBytes);
            Assert.Equal(0, seen.CreationTime);
        }
        finally
        {
            proc.Kill();
            proc.WaitForExit(3000);
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
