using System.Diagnostics;
using SunsetBoost.Core.Edge;
using SunsetBoost.Core.Processes;
using static SunsetBoost.Core.Tests.T;

namespace SunsetBoost.Core.Tests;

public class EdgeMemoryGuardTests
{
    private const long GB2 = 2048 * MB;
    private static EdgeProc E(int pid, EdgeProcKind kind, long mb) => new(P(pid, "msedge", mb: mb), kind);

    [Theory]
    [InlineData(@"""msedge.exe"" --profile-directory=Default", EdgeProcKind.Browser)]
    [InlineData(@"msedge.exe --type=renderer --lang=pt-BR", EdgeProcKind.Tab)]
    [InlineData(@"msedge.exe --type=renderer --extension-process", EdgeProcKind.Extension)]
    [InlineData(@"msedge.exe --type=gpu-process", EdgeProcKind.Gpu)]
    [InlineData(@"msedge.exe --type=utility --utility-sub-type=network.mojom.NetworkService", EdgeProcKind.Other)]
    [InlineData(null, EdgeProcKind.Other)]
    public void Reconhece_o_tipo_de_cada_processo(string? cmd, EdgeProcKind kind) =>
        Assert.Equal(kind, EdgeMemoryGuard.Classify(cmd));

    [Fact]
    public void Abaixo_do_limite_nao_faz_nada()
    {
        var d = EdgeMemoryGuard.Decide(new[] { E(1, EdgeProcKind.Browser, 300), E(2, EdgeProcKind.Tab, 900) }, GB2);
        Assert.Empty(d.Kill);
        Assert.False(d.StillOver);
    }

    [Fact]
    public void Acima_do_limite_derruba_a_maior_aba_primeiro_e_so_o_necessario()
    {
        var procs = new[]
        {
            E(1, EdgeProcKind.Browser, 300), E(2, EdgeProcKind.Gpu, 400),
            E(3, EdgeProcKind.Tab, 1200), E(4, EdgeProcKind.Tab, 500), E(5, EdgeProcKind.Extension, 200),
        }; // total 2600 MB
        var d = EdgeMemoryGuard.Decide(procs, GB2);
        Assert.Equal(new[] { 3 }, d.Kill.Select(k => k.Info.Pid)); // tirar 1200 já resolve
        Assert.False(d.StillOver);
    }

    [Fact]
    public void Depois_das_abas_vao_as_extensoes()
    {
        var procs = new[]
        {
            E(1, EdgeProcKind.Browser, 900), E(2, EdgeProcKind.Gpu, 900),
            E(3, EdgeProcKind.Tab, 200), E(4, EdgeProcKind.Extension, 300), E(5, EdgeProcKind.Extension, 100),
        }; // total 2400
        var d = EdgeMemoryGuard.Decide(procs, GB2);
        Assert.Equal(new[] { 3, 4 }, d.Kill.Select(k => k.Info.Pid));
    }

    [Fact]
    public void Se_o_esqueleto_ja_passa_do_limite_derruba_as_abas_mas_nunca_fecha_o_navegador()
    {
        var procs = new[] { E(1, EdgeProcKind.Browser, 1500), E(2, EdgeProcKind.Gpu, 800), E(3, EdgeProcKind.Tab, 100) };
        var d = EdgeMemoryGuard.Decide(procs, GB2);
        Assert.True(d.StillOver);
        Assert.Equal(new[] { 3 }, d.Kill.Select(k => k.Info.Pid)); // janela principal e GPU ficam
    }

    [Fact]
    public void Nunca_derruba_o_navegador_nem_a_GPU_isoladamente()
    {
        var procs = new[] { E(1, EdgeProcKind.Browser, 1000), E(2, EdgeProcKind.Gpu, 900), E(3, EdgeProcKind.Tab, 300) };
        var d = EdgeMemoryGuard.Decide(procs, GB2);
        Assert.False(d.StillOver);
        Assert.Equal(new[] { 3 }, d.Kill.Select(k => k.Info.Pid));
    }

    [Theory]
    [InlineData("GX Corner", true)] [InlineData("Speed Dial", true)] [InlineData("Nova guia", true)] [InlineData("New Tab", true)]
    [InlineData("Nova guia - Uso de memória - 50 MB", true)]          // Edge
    [InlineData("Uso da memória em Nova guia: 50 MB", true)]          // Chrome
    [InlineData("YouTube", false)] [InlineData("Nova guia de viagem", false)]
    [InlineData("GitHub - Uso de memória - 174 MB", false)] [InlineData(null, false)]
    public void Pagina_inicial_nao_conta_como_primeira_aba(string? name, bool expected) =>
        Assert.Equal(expected, EdgeTabs.IsStartPage(name));

    [Fact]
    public void So_conta_aba_que_nao_esta_dentro_de_um_site()
    {
        var CT = System.Windows.Automation.ControlType.Tab;
        var Pane = System.Windows.Automation.ControlType.Pane;
        var Doc = System.Windows.Automation.ControlType.Document;
        Assert.True(EdgeTabs.IsNativeTab(new[] { CT, Pane, Pane }));          // barra de abas do navegador
        Assert.False(EdgeTabs.IsNativeTab(new[] { CT, Pane, Doc, Pane }));    // "abas" do GitHub/YouTube
    }

    [Theory]
    [InlineData("Fechar guia", true)] [InlineData("Close tab", true)]
    [InlineData("Silenciar guia", false)] [InlineData("Mute tab", false)] [InlineData(null, false)]
    public void So_aperta_o_botao_de_fechar(string? name, bool expected) =>
        Assert.Equal(expected, EdgeTabs.IsCloseLabel(name));
}
