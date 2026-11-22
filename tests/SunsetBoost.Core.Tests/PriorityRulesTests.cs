using SunsetBoost.Core.Automation;
using SunsetBoost.Core.Cleanup;
using SunsetBoost.Core.Processes;
using SunsetBoost.Core.Tweaks;

namespace SunsetBoost.Core.Tests;

internal sealed class FakePriorityStore : IPriorityStore
{
    public Dictionary<string, int> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Writes { get; private set; }
    public int? GetCpuPriority(string exe) => Values.TryGetValue(exe, out var v) ? v : null;
    public void SetCpuPriority(string exe, int value) { Values[exe] = value; Writes++; }
    public void ClearCpuPriority(string exe) { Values.Remove(exe); Writes++; }
}

public class PriorityRulesTests
{
    [Fact]
    public void Ligar_poe_alta_em_todos_os_jogos_e_desligar_tira()
    {
        var store = new FakePriorityStore();
        var targets = PriorityRules.Targets(new[] { "cs2.exe" });

        Assert.Empty(PriorityRules.Apply(store, targets));
        Assert.True(PriorityRules.IsOn(store, targets));
        Assert.Equal(PriorityRules.High, store.GetCpuPriority("VALORANT-Win64-Shipping.exe"));
        Assert.Equal(PriorityRules.High, store.GetCpuPriority("cs2.exe"));

        Assert.Empty(PriorityRules.Remove(store, targets));
        Assert.False(PriorityRules.IsOn(store, targets));
        Assert.Empty(store.Values);
    }

    [Fact]
    public void Nunca_usa_tempo_real()
    {
        var store = new FakePriorityStore();
        PriorityRules.Apply(store, PriorityRules.KnownGames);
        Assert.All(store.Values.Values, v => Assert.Equal(3, v));
        Assert.DoesNotContain(4, store.Values.Values);
    }

    [Fact]
    public void Desligar_nao_apaga_valor_que_outro_programa_pos()
    {
        var store = new FakePriorityStore();
        store.Values["League of Legends.exe"] = 6; // "Acima do normal", posto por outro programa
        PriorityRules.Remove(store, PriorityRules.KnownGames);
        Assert.Equal(6, store.GetCpuPriority("League of Legends.exe"));
    }

    [Fact]
    public void Lista_nao_repete_jogo_ja_conhecido()
    {
        var t = PriorityRules.Targets(new[] { "valorant-win64-shipping.exe", "cs2.exe" });
        Assert.Equal(PriorityRules.KnownGames.Length + 1, t.Count);
    }

    [Theory]
    [InlineData("svchost.exe")] [InlineData("csrss.exe")] [InlineData("explorer.exe")] [InlineData("vgc.exe")]
    [InlineData("SunsetBoost.exe")] [InlineData("SunsetBoost.App.exe")] [InlineData(@"C:\Jogos\cs2.exe")] [InlineData("jogo.bat")] [InlineData("")]
    public void Recusa_processo_do_windows_anti_cheat_e_nome_invalido(string exe)
    {
        Assert.False(PriorityRules.IsAllowed(exe, out _));
        var store = new FakePriorityStore();
        Assert.NotEmpty(PriorityRules.Apply(store, new[] { exe }));
        Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData("cs2.exe")] [InlineData("RainbowSix.exe")] [InlineData("eldenring.exe")]
    public void Aceita_jogo_comum(string exe) => Assert.True(PriorityRules.IsAllowed(exe, out _));
}

public class QuietModeTests
{
    [Fact]
    public void Detector_acha_o_jogo_so_pelo_nome()
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "explorer", "VALORANT-Win64-Shipping" };
        Assert.Equal("valorant", GameDetector.FindIn(running)?.Id);
        Assert.Null(GameDetector.FindIn(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "explorer", "msedge" }));
    }

    [Fact]
    public void Scanner_com_filtro_so_devolve_os_processos_pedidos()
    {
        var me = ProcessScanner.NormalizeName(System.Diagnostics.Process.GetCurrentProcess().ProcessName + ".exe");
        var only = new ProcessScanner().Snapshot(n => n == me);
        Assert.NotEmpty(only);
        Assert.All(only, p => Assert.Equal(me, p.Name));
    }
}
