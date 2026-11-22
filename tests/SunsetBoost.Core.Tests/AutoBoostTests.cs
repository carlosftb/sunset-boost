using SunsetBoost.Core.Automation;
using SunsetBoost.Core.Cleanup;

namespace SunsetBoost.Core.Tests;

public class AutoBoostTests
{
    private static readonly DateTime T0 = new(2026, 11, 21, 20, 0, 0);
    private static DateTime S(double seconds) => T0.AddSeconds(seconds);
    private static readonly GameProfile V = GameProfiles.Valorant;

    [Fact]
    public void Liga_so_depois_do_jogo_ficar_aberto_alguns_segundos()
    {
        var c = new AutoBoostController();
        Assert.Equal(AutoAction.None, c.Tick(S(0), V, false).Action);
        Assert.Equal(AutoAction.None, c.Tick(S(2), V, false).Action);
        var d = c.Tick(S(4), V, false);
        Assert.Equal(AutoAction.Enter, d.Action);
        Assert.Equal("valorant", d.Profile!.Id);
        Assert.Equal(AutoAction.None, c.Tick(S(6), V, true).Action);
    }

    [Fact]
    public void Piscada_do_processo_na_abertura_nao_atrasa_o_boost()
    {
        // O Valorant troca de processo ao abrir; um tique sem jogo não zera a contagem
        var c = new AutoBoostController();
        c.Tick(S(0), V, false);
        c.Tick(S(2), null, false);
        Assert.Equal(AutoAction.None, c.Tick(S(3), V, false).Action);
        Assert.Equal(AutoAction.Enter, c.Tick(S(4), V, false).Action);
    }

    [Fact]
    public void Jogo_que_abre_e_fecha_rapido_nao_liga_nada()
    {
        var c = new AutoBoostController();
        c.Tick(S(0), V, false);
        c.Tick(S(2), null, false);
        for (int s = 3; s < 40; s++) Assert.Equal(AutoAction.None, c.Tick(S(s), null, false).Action);
    }

    [Fact]
    public void Restaura_so_depois_do_jogo_ficar_fechado_um_tempo()
    {
        var c = new AutoBoostController();
        c.Tick(S(0), V, false);
        c.Tick(S(4), V, false); // Enter
        Assert.Equal(AutoAction.None, c.Tick(S(60), null, true).Action);
        Assert.Equal(AutoAction.None, c.Tick(S(65), V, true).Action); // voltou (reconexão): cancela
        Assert.Equal(AutoAction.None, c.Tick(S(70), null, true).Action);
        Assert.Equal(AutoAction.None, c.Tick(S(79), null, true).Action);
        Assert.Equal(AutoAction.Exit, c.Tick(S(80), null, true).Action);
    }

    [Fact]
    public void Boost_ligado_na_mao_nao_e_desligado_quando_o_jogo_fecha()
    {
        var c = new AutoBoostController();
        c.Tick(S(0), V, true); // usuário já tinha ligado
        c.Tick(S(10), V, true);
        c.Tick(S(20), null, true);
        Assert.Equal(AutoAction.None, c.Tick(S(40), null, true).Action);
        Assert.False(c.StartedByAuto);
    }

    [Fact]
    public void Se_o_usuario_desliga_durante_a_partida_nao_religa()
    {
        var c = new AutoBoostController();
        c.Tick(S(0), V, false);
        Assert.Equal(AutoAction.Enter, c.Tick(S(4), V, false).Action);
        // usuário restaurou na mão
        Assert.Equal(AutoAction.None, c.Tick(S(30), V, false).Action);
        Assert.Equal(AutoAction.None, c.Tick(S(60), V, false).Action);
        // fechou o jogo: nada a restaurar
        c.Tick(S(70), null, false);
        Assert.Equal(AutoAction.None, c.Tick(S(90), null, false).Action);
    }

    [Fact]
    public void Proxima_partida_liga_de_novo()
    {
        var c = new AutoBoostController();
        c.Tick(S(0), V, false);
        c.Tick(S(4), V, false);           // Enter
        c.Tick(S(100), null, true);
        Assert.Equal(AutoAction.Exit, c.Tick(S(110), null, true).Action);
        c.Tick(S(200), V, false);
        Assert.Equal(AutoAction.Enter, c.Tick(S(204), V, false).Action);
    }
}
