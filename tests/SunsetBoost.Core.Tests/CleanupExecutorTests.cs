using SunsetBoost.Core.Cleanup;
using SunsetBoost.Core.Infrastructure;
using SunsetBoost.Core.Processes;
using static SunsetBoost.Core.Tests.T;

namespace SunsetBoost.Core.Tests;

public class CleanupExecutorTests
{
    private static readonly CleanupOptions Fast = new(TimeSpan.FromMilliseconds(150), PurgeStandby: true);

    [Fact]
    public void Pede_pra_fechar_primeiro_e_so_forca_quem_nao_fechou()
    {
        var snap = new[] { P(10, "msedge"), P(11, "msedge", parent: 10), P(20, "steam") };
        var sys = new FakeSystemActions();
        sys.Stubborn.Add(20); // Steam ignora o pedido e vai pra bandeja

        var report = new CleanupExecutor(sys).Run(CleanupPlanner.Build(snap, Ctx()), Fast);

        Assert.Equal(new[] { 10, 11, 20 }, sys.CloseRequested.OrderBy(x => x));
        Assert.Equal(new[] { 20 }, sys.Killed);
        var edge = report.Groups.Single(r => r.Group.AppId == "edge");
        Assert.Equal(2, edge.ClosedGracefully);
        Assert.Equal(1, report.Groups.Single(r => r.Group.AppId == "steam").Killed);
        Assert.Equal(1, sys.Purges);
    }

    // Tempos de verdade, como no app: 3 s de prazo, 1 s depois que a janela some
    private static readonly CleanupOptions Real = new(TimeSpan.FromSeconds(3), PurgeStandby: false, TrayGrace: TimeSpan.FromSeconds(1));

    private static TimeSpan Time(Action a) { var sw = System.Diagnostics.Stopwatch.StartNew(); a(); return sw.Elapsed; }

    [Fact]
    public void App_sem_janela_e_forcado_na_hora_sem_esperar()
    {
        // Edge em segundo plano (Startup boost): 8 processos, nenhuma janela — como no log real
        var snap = Enumerable.Range(10, 8).Select(pid => P(pid, "msedge")).ToArray();
        var sys = new FakeSystemActions();
        sys.NoWindow.UnionWith(snap.Select(p => p.Pid));

        var elapsed = Time(() => new CleanupExecutor(sys).Run(CleanupPlanner.Build(snap, Ctx()), Real));

        Assert.Equal(8, sys.Killed.Count);
        Assert.True(elapsed < TimeSpan.FromSeconds(0.5), $"demorou {elapsed.TotalSeconds:0.00}s");
    }

    [Fact]
    public void App_que_vai_pra_bandeja_e_forcado_1s_depois_da_janela_sumir()
    {
        // Claude/Spotify: recebem o pedido, escondem a janela e continuam rodando
        var snap = new[] { P(10, "claude"), P(11, "claude", parent: 10) };
        var sys = new FakeSystemActions();
        sys.Stubborn.UnionWith(new[] { 10, 11 });
        sys.NoWindow.Add(11);
        var plan = CleanupPlanner.Build(snap, Ctx(), new Dictionary<string, AppDecision> { ["claude"] = AppDecision.Close });

        var elapsed = Time(() => new CleanupExecutor(sys).Run(plan, Real));

        Assert.Equal(new[] { 10, 11 }, sys.Killed.OrderBy(x => x));
        Assert.InRange(elapsed.TotalSeconds, 0.9, 1.8);
    }

    [Fact]
    public void App_com_janela_que_fecha_sozinho_nao_e_forcado()
    {
        var snap = new[] { P(10, "msedge"), P(11, "msedge", parent: 10) };
        var sys = new FakeSystemActions();
        sys.NoWindow.Add(11); // filho sem janela morre junto com o pai
        sys.Stubborn.Clear();

        new CleanupExecutor(sys).Run(CleanupPlanner.Build(snap, Ctx()), Real);

        // o filho sem janela foi forçado na hora (é o pai que tem janela); o pai fechou sozinho
        Assert.DoesNotContain(10, sys.Killed);
    }

    [Fact]
    public void App_que_mantem_a_janela_ganha_o_prazo_inteiro()
    {
        // Ex.: um "Deseja salvar?" aberto: a janela continua, então espera os 3 s antes de forçar
        var snap = new[] { P(10, "qbittorrent") };
        var sys = new FakeSystemActions();
        sys.Stubborn.Add(10);
        sys.KeepsWindow.Add(10);
        var plan = CleanupPlanner.Build(snap, Ctx(), new Dictionary<string, AppDecision> { ["exe:qbittorrent"] = AppDecision.Close });

        var elapsed = Time(() => new CleanupExecutor(sys).Run(plan, Real));

        Assert.Contains(10, sys.Killed);
        Assert.InRange(elapsed.TotalSeconds, 2.9, 3.8);
    }

    [Fact]
    public void Falha_ao_forcar_vira_erro_no_relatorio_sem_derrubar_o_resto()
    {
        var snap = new[] { P(10, "msedge"), P(20, "steam") };
        var sys = new FakeSystemActions();
        sys.Stubborn.UnionWith(new[] { 10, 20 });
        sys.Unkillable.Add(10);

        var report = new CleanupExecutor(sys).Run(CleanupPlanner.Build(snap, Ctx()), Fast);

        Assert.Single(report.Groups.Single(r => r.Group.AppId == "edge").Errors);
        Assert.Contains(20, sys.Killed);
    }

    [Fact]
    public void Plano_adulterado_com_processo_protegido_e_bloqueado_na_hora_de_executar()
    {
        // Simula um bug/ataque: alguém montou um grupo que diz "fecha" contendo Vanguard, lsass e o jogo
        var evil = new[] { P(101, "vgc"), P(102, "lsass", path: @"C:\Windows\System32\lsass.exe"), P(103, "valorant-win64-shipping"), P(104, "msedge") };
        var ctx = Ctx(GameProfiles.Steam); // perfil "errado": o Valorant tem que ficar protegido mesmo assim
        var plan = new CleanupPlan(ctx.Profile, new[]
        {
            new AppGroup("edge", "Edge", CleanupList.CloseByDefault, "teste", evil, WillClose: true),
            new AppGroup("x", "X", CleanupList.NeverClose, "teste", new[] { P(105, "csrss") }, WillClose: true),
        }, ctx);
        var sys = new FakeSystemActions();

        var report = new CleanupExecutor(sys).Run(plan, Fast);

        Assert.Equal(new[] { 104 }, sys.Touched.Distinct());
        Assert.Equal(3, report.Groups[0].Blocked);
        Assert.Equal(1, report.Groups[1].Blocked);
    }

    [Fact]
    public void Modo_simulacao_nao_toca_em_nada_e_so_registra()
    {
        var snap = new[] { P(10, "msedge"), P(20, "discord") };
        var sim = new SimulatedSystemActions();
        var report = new CleanupExecutor(sim).Run(CleanupPlanner.Build(snap, Ctx()), Fast);

        Assert.True(report.Simulated);
        Assert.Equal(0, report.MeasuredFreedBytes);
        Assert.Contains(sim.Journal, j => j.Contains("msedge.exe"));
        Assert.DoesNotContain(sim.Journal, j => j.Contains("discord"));
    }

    /// <summary>
    /// Teste "bagunçado": 300 snapshots aleatórios misturando processos do sistema, anti-cheat, jogo e apps,
    /// com o usuário mandando fechar TUDO. Nenhum processo protegido pode receber pedido de fechar ou ser forçado.
    /// </summary>
    [Fact]
    public void Nada_da_lista_nunca_fecha_e_tocado_em_cenarios_aleatorios()
    {
        var protectedNames = SafetyRules.CriticalSystem.Concat(SafetyRules.AntiCheat).Concat(SafetyRules.Security)
            .Concat(SafetyRules.Drivers).Concat(new[] { "valorant-win64-shipping", "valorant" }).ToArray();
        var normalNames = AppCatalog.All.SelectMany(a => a.ProcessNames)
            .Concat(new[] { "msedgewebview2", "meuapp", "jogo2", "crashpad_handler" }).ToArray();
        var rng = new Random(42);

        for (int round = 0; round < 300; round++)
        {
            var snap = new List<ProcessInfo>();
            for (int pid = 100; pid < 160; pid++)
            {
                bool prot = rng.NextDouble() < 0.4;
                var name = prot ? protectedNames[rng.Next(protectedNames.Length)] : normalNames[rng.Next(normalNames.Length)];
                int session = rng.NextDouble() < 0.15 ? 0 : 1;
                string? path = rng.Next(10) switch
                {
                    0 => null,
                    1 => $@"C:\Windows\System32\{name}.exe",
                    _ => $@"C:\Program Files\{name}\{name}.exe",
                };
                int parent = rng.NextDouble() < 0.5 ? rng.Next(100, 160) : 4;
                snap.Add(new ProcessInfo(pid, parent, name, path, session, rng.Next(1, 900) * MB, pid));
            }

            var ctx = Ctx(GameProfiles.All[rng.Next(GameProfiles.All.Count)]);
            ctx.ProtectedPids.Add(snap[rng.Next(snap.Count)].Pid); // "o próprio app"
            var closeEverything = snap.Select(p => p.Name).Distinct()
                .SelectMany(n => new[] { "exe:" + n, AppCatalog.FindByProcess(n)?.Id ?? "" })
                .Where(k => k != "").Distinct()
                .ToDictionary(k => k, _ => AppDecision.Close);

            var sys = new FakeSystemActions();
            sys.Stubborn.UnionWith(snap.Where(_ => rng.NextDouble() < 0.5).Select(p => p.Pid));
            new CleanupExecutor(sys).Run(CleanupPlanner.Build(snap, ctx, closeEverything), new(TimeSpan.FromMilliseconds(1), false));

            var byPid = snap.ToDictionary(p => p.Pid);
            foreach (var pid in sys.Touched)
            {
                var p = byPid[pid];
                Assert.True(SafetyRules.Check(p, ctx) is null, $"rodada {round}: tocou em {p.Name} (PID {pid})");
                Assert.DoesNotContain(p.Name, protectedNames);
            }
        }
    }
}
