using System.Text;
using SunsetBoost.Core.Cleanup;
using SunsetBoost.Core.Infrastructure;
using SunsetBoost.Core.Memory;
using SunsetBoost.Core.Processes;
using SunsetBoost.Core.Tweaks;

// Uso:
//   sunsetboost-cli simular [--perfil valorant] [--fechar discord,spotify] [--manter edge]
//   sunsetboost-cli limpar  [--perfil valorant] [...]   (precisa de admin; pede confirmação)
//   sunsetboost-cli memoria
Console.OutputEncoding = Encoding.UTF8;

// Com admin: %ProgramData%\SunsetBoost (protegida, a mesma do app). Sem admin: %LOCALAPPDATA%\SunsetBoost.
var paths = new AppPaths().EnsureCreated();
AppLog.Configure(paths.LogsDir);
foreach (var note in paths.Notes) AppLog.Warn(note);
var settings = new SettingsStore(paths.SettingsFile).Load();

string command = args.FirstOrDefault(a => !a.StartsWith("--"))?.ToLowerInvariant() ?? "simular";
string Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault() ?? "";

if (command == "memoria")
{
    PrintMemory(MemoryInfo.Read());
    return 0;
}
if (command is "edgeleve" or "navleve")
{
    // Teste manual do modo leve: fecha as outras abas/janelas do SEU navegador, mantendo a 1ª aba da janela usada por último
    //   navleve edge | navleve chrome | navleve opera   (edgeleve = navleve edge)
    var nav = command == "edgeleve" ? SunsetBoost.Core.Edge.Browsers.Edge
        : SunsetBoost.Core.Edge.Browsers.Find(args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? "edge");
    if (nav is null) { Console.WriteLine("Use: navleve edge | chrome | opera"); return 1; }
    Console.WriteLine($"Abas abertas no {nav.Name} agora: {SunsetBoost.Core.Edge.EdgeTabs.CountTabs(nav)}");
    if (SunsetBoost.Core.Edge.EdgeTabs.TabsHidden(nav))
    {
        Console.WriteLine("Atenção: " + SunsetBoost.Core.Edge.EdgeTabs.HiddenMessage(nav) + ".");
        return 0;
    }
    if (!nav.TabsClosable) Console.WriteLine($"(no {nav.Name} as abas fecham com clique do meio; a janela não pode estar minimizada)");
    Console.Write($"Isso fecha todas as abas do {nav.Name} menos a primeira da última janela usada. Digite SIM pra continuar: ");
    if (!string.Equals(Console.ReadLine()?.Trim(), "SIM", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Cancelado."); return 0; }
    var r = SunsetBoost.Core.Edge.EdgeTabs.KeepFirstTab(nav);
    Console.WriteLine(r.Error is null
        ? $"Fechadas: {r.TabsClosed} abas e {r.WindowsClosed} janelas · ficaram {r.TabsKept} aba(s). Ctrl+Shift+T reabre."
        : $"Erro: {r.Error}");
    return r.Error is null ? 0 : 1;
}

if (command == "diagnostico")
{
    // Só lê: mostra o que o verificador acha deste PC (os mesmos itens da aba DIAGNÓSTICO)
    var hw = new SunsetBoost.Core.Diagnostics.RealHardwareInfo();
    foreach (var d in hw.Displays()) Console.WriteLine($"  tela: {d.Name} {d.Width}x{d.Height} {d.CurrentHz} Hz (máx {d.MaxHz})");
    foreach (var m in hw.Memory()) Console.WriteLine($"  RAM: {m.SizeMB} MB {m.Type} {m.ConfiguredSpeed}/{m.RatedSpeed} MT/s {m.PartNumber}");
    foreach (var g in hw.Gpus()) Console.WriteLine($"  vídeo: {g.Name} (driver {g.DriverDate:dd/MM/yyyy}, dedicada: {g.IsDedicated})");
    foreach (var g in SunsetBoost.Core.Tweaks.GameLocator.Find()) Console.WriteLine($"  jogo: {g.Name} → {g.ExePath}");
    Console.WriteLine();
    foreach (var item in SunsetBoost.Core.Diagnostics.DiagnosticsService.Run(hardware: hw))
        Console.WriteLine($"[{item.Status}] {item.Title}{Environment.NewLine}    {item.Detail}");
    return 0;
}
if (command == "atualizacao")
{
    // Procura versão nova no GitHub. Com --baixar-para <pasta>, baixa e confere o SHA-256 (nunca executa).
    // --como 0.0.0 finge ser uma versão antiga (pra testar o download da versão atual).
    var current = Opt("--como") is { Length: > 0 } fake && Version.TryParse(fake, out var fv) ? fv : SunsetBoost.Core.Updates.Updater.Current;
    using var http = SunsetBoost.Core.Updates.Updater.CreateClient();
    var info = await SunsetBoost.Core.Updates.Updater.CheckAsync(http, current);
    Console.WriteLine($"Instalada: {SunsetBoost.Core.Updates.Updater.Display(current)}");
    if (info is null) { Console.WriteLine("Em dia."); return 0; }
    Console.WriteLine($"Nova: {info.Tag} · {info.InstallerName} · {info.InstallerSize} bytes\n  {info.InstallerUrl}\n  {info.PageUrl}");
    if (Opt("--baixar-para") is { Length: > 0 } dir)
    {
        var path = await SunsetBoost.Core.Updates.Updater.DownloadAsync(http, info, dir);
        Console.WriteLine($"Baixado e conferido (SHA-256 da release): {path}");
    }
    return 0;
}
if (command == "modojogo")
    return GameModeCommand(args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"))?.ToLowerInvariant() ?? "status");

if (command is not ("simular" or "limpar"))
{
    Console.WriteLine("Comandos: simular | limpar | memoria | diagnostico | atualizacao | modojogo [status|entrar|sair|testar]");
    return 1;
}

var profile = GameProfiles.Find(Opt("--perfil") is { Length: > 0 } p ? p : settings.ActiveProfileId);
var decisions = new Dictionary<string, AppDecision>(settings.AppDecisions, StringComparer.OrdinalIgnoreCase);
foreach (var id in Opt("--fechar").Split(',', StringSplitOptions.RemoveEmptyEntries)) decisions[id.Trim()] = AppDecision.Close;
foreach (var id in Opt("--manter").Split(',', StringSplitOptions.RemoveEmptyEntries)) decisions[id.Trim()] = AppDecision.Keep;

var snapshot = new ProcessScanner().Snapshot();
var ctx = SafetyContext.ForCurrentProcess(snapshot, profile);
var plan = CleanupPlanner.Build(snapshot, ctx, decisions);

bool simulate = command == "simular" || settings.SimulationMode;
Header($"SUNSET BOOST · {(simulate ? "SIMULAÇÃO" : "LIMPEZA REAL")} · perfil {profile.DisplayName}");
Console.WriteLine($"Admin: {(Elevation.IsElevated ? "sim" : "não")} · {snapshot.Count} processos · {plan.Groups.Count} apps");
PrintMemory(MemoryInfo.Read());
PrintPlan(plan);

if (!simulate)
{
    if (!Elevation.IsElevated)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("\nA limpeza real precisa de administrador. Abra o terminal como admin.");
        Console.ResetColor();
        return 2;
    }
    Console.Write($"\nFechar {plan.ToClose.Count()} apps agora? Digite SIM pra confirmar: ");
    if (!string.Equals(Console.ReadLine()?.Trim(), "SIM", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Cancelado."); return 0; }
}

ISystemActions sys = simulate ? new SimulatedSystemActions() : new RealSystemActions();
var report = new CleanupExecutor(sys).Run(plan, CleanupOptions.FromSettings(settings));

Header(simulate ? "O QUE SERIA FEITO" : "RESULTADO");
foreach (var r in report.Groups)
{
    Console.Write($"  {r.Group.DisplayName,-28} ");
    if (simulate) Console.WriteLine($"{r.Group.Processes.Count,3} processos · pediria pra fechar, força após {settings.GracefulCloseSeconds}s");
    else Console.WriteLine($"{r.ClosedGracefully} fecharam sozinhos · {r.Killed} forçados · {r.Failed} falharam");
    foreach (var e in r.Errors) Console.WriteLine($"      ! {e}");
}
Console.WriteLine();
Color(ConsoleColor.Yellow, simulate
    ? $"  RAM que seria liberada (estimativa): +{Fmt.Bytes(report.EstimatedFreedBytes)}"
    : $"  RAM disponível: {Fmt.Bytes(report.Before.AvailableBytes)} → {Fmt.Bytes(report.After.AvailableBytes)}  (+{Fmt.Bytes(report.MeasuredFreedBytes)})");
Console.WriteLine($"  Standby list: {(report.StandbyPurged ? (simulate ? "seria limpa" : "limpa") : "não limpa (" + report.StandbyError + ")")}");
Console.WriteLine($"  Tempo: {report.Duration.TotalSeconds:0.0}s · log em {paths.LogsDir}");
return 0;

int GameModeCommand(string sub)
{
    var profileGm = GameProfiles.Find(Opt("--perfil") is { Length: > 0 } pp ? pp : settings.ActiveProfileId);
    var gm = new GameModeManager(TweakCatalog.CreateReal(), paths.SnapshotFile);
    var tctx = new TweakContext(profileGm);

    void PrintState(string title, Dictionary<string, TweakState> fp)
    {
        Header(title);
        foreach (var t in gm.Tweaks) Console.WriteLine($"  {t.Title,-58} {fp[t.Id]}");
    }

    if (sub == "status")
    {
        PrintState($"MODO JOGO · {(gm.IsActive ? "ATIVO" : "desligado")}", gm.Fingerprint(tctx));
        if (gm.LoadSnapshot() is { } s) Console.WriteLine($"\n  Cópia salva em {s.CreatedAt:dd/MM HH:mm} com {s.Entries.Count} ajustes ({paths.SnapshotFile})");
        return 0;
    }

    if (!Elevation.IsElevated)
    {
        Color(ConsoleColor.Red, "Esse comando muda configurações do Windows: abra o terminal como administrador.");
        return 2;
    }

    void PrintOutcomes(GameModeResult r)
    {
        foreach (var o in r.Outcomes)
            Color(o.Result.Ok ? ConsoleColor.Green : ConsoleColor.Red, $"  {(o.Result.Ok ? "✓" : "✗")} {o.Tweak.Title}: {o.Result.Message}");
        Color(r.Ok ? ConsoleColor.Yellow : ConsoleColor.Red, $"  {r.Message}");
    }

    var ids = gm.Tweaks.Where(t => settings.Tweaks.TryGetValue(t.Id, out var on) ? on : t.EnabledByDefault).Select(t => t.Id).ToList();

    switch (sub)
    {
        case "entrar":
            Header("ENTRANDO NO MODO JOGO (só ajustes, sem fechar apps)");
            PrintOutcomes(gm.Enter(tctx, ids));
            return 0;
        case "sair":
            Header("RESTAURANDO");
            PrintOutcomes(gm.Restore());
            return 0;
        case "testar":
        {
            // Aplica TODOS os ajustes (inclusive o plano de energia), confere no Windows, restaura e compara
            if (gm.IsActive) { Color(ConsoleColor.Red, "Já existe uma cópia pendente. Rode 'modojogo sair' primeiro."); return 3; }
            var before = gm.Fingerprint(tctx);
            PrintState("1/4 · ANTES", before);

            Header("2/4 · APLICANDO TODOS OS AJUSTES");
            PrintOutcomes(gm.Enter(tctx, gm.Tweaks.Select(t => t.Id)));
            var during = gm.Fingerprint(tctx);
            PrintState("    · COMO FICOU NO WINDOWS", during);

            Header("3/4 · RESTAURANDO");
            PrintOutcomes(gm.Restore());

            var after = gm.Fingerprint(tctx);
            Header("4/4 · COMPARAÇÃO ANTES × DEPOIS");
            bool identical = true;
            foreach (var t in gm.Tweaks)
            {
                bool changed = !before[t.Id].SameAs(during[t.Id]);
                bool same = before[t.Id].SameAs(after[t.Id]);
                identical &= same;
                Color(same ? ConsoleColor.Green : ConsoleColor.Red,
                    $"  {(same ? "✓ idêntico" : "✗ DIFERENTE")}  {(changed ? "(mudou durante)" : "(já estava assim)")}  {t.Title}" +
                    (same ? "" : $"\n      antes: {before[t.Id]}\n      depois: {after[t.Id]}"));
            }
            Console.WriteLine();
            Color(identical ? ConsoleColor.Yellow : ConsoleColor.Red,
                identical ? "  RESULTADO: tudo voltou idêntico ao que era." : "  RESULTADO: algo não voltou. Veja acima e o log.");
            return identical ? 0 : 4;
        }
        default:
            Console.WriteLine("Use: modojogo status | entrar | sair | testar");
            return 1;
    }
}

static void PrintPlan(CleanupPlan plan)
{
    foreach (var list in new[] { CleanupList.CloseByDefault, CleanupList.UserDecides, CleanupList.NeverClose })
    {
        var groups = plan.Groups.Where(g => g.List == list).ToList();
        var (title, color) = list switch
        {
            CleanupList.CloseByDefault => ("✅ FECHA POR PADRÃO", ConsoleColor.Magenta),
            CleanupList.UserDecides => ("✋ VOCÊ DECIDE (fica aberto se você não escolher)", ConsoleColor.Yellow),
            _ => ("🔒 NUNCA FECHA", ConsoleColor.DarkCyan),
        };
        Console.WriteLine();
        Color(color, $"{title} — {groups.Count} apps, {Fmt.Bytes(groups.Sum(g => g.PrivateBytes))}");
        // Nunca fecha: mostra só os maiores pra não poluir
        foreach (var g in list == CleanupList.NeverClose ? groups.Take(12) : groups)
        {
            var mark = g.WillClose ? "[x]" : "[ ]";
            Console.WriteLine($"  {mark} {Trunc(g.DisplayName, 30),-30} {Fmt.Bytes(g.PrivateBytes),9}  {g.Processes.Count,3} proc  · {g.Reason}");
        }
        if (list == CleanupList.NeverClose && groups.Count > 12) Console.WriteLine($"      … e mais {groups.Count - 12} apps do sistema");
    }
}

static void PrintMemory(MemorySnapshot m)
{
    Console.WriteLine($"RAM: {Fmt.Bytes(m.UsedBytes)} em uso de {Fmt.Bytes(m.TotalBytes)} ({m.UsedPercent:0}%) · disponível {Fmt.Bytes(m.AvailableBytes)}" +
        (m.StandbyBytes is { } s ? $" · standby {Fmt.Bytes(s)} · livre de verdade {Fmt.Bytes(m.FreeBytes!.Value)}" : " · standby: precisa de admin pra ler"));
}

static void Header(string t) { Console.WriteLine(); Color(ConsoleColor.Cyan, $"═══ {t} ═══"); }
static void Color(ConsoleColor c, string t) { Console.ForegroundColor = c; Console.WriteLine(t); Console.ResetColor(); }
static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
