using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using SunsetBoost.Core;
using SunsetBoost.Core.Automation;
using SunsetBoost.Core.Cleanup;
using SunsetBoost.Core.Diagnostics;
using SunsetBoost.Core.Edge;
using SunsetBoost.Core.Infrastructure;
using SunsetBoost.Core.Memory;
using SunsetBoost.Core.Monitoring;
using SunsetBoost.Core.Tweaks;
using SunsetBoost.Core.Updates;

namespace SunsetBoost.App;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> BoringReasons = new()
    {
        "componente do Windows", "serviço do sistema (sessão 0)", "processo essencial do Windows",
        "núcleo do Windows", "processo protegido (sem acesso)",
    };

    private readonly BoostService _boost = App.Boost;
    private readonly AppSettings _settings = App.Settings;
    private readonly SystemMonitor _monitor = new();
    private readonly DispatcherTimer _fast = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _slow = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Dictionary<string, long> _cardBytes = new();
    private readonly DispatcherTimer _autoTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly AutoBoostController _auto = new();
    private readonly IPriorityStore _priority = new RealPriorityStore();
    private readonly IGpuPreferenceStore _gpu = new RealGpuPreferenceStore();
    /// <summary>Jogo conhecido aberto agora (só pelo nome). Enquanto tiver, o app fica em silêncio: não lê processos.</summary>
    private GameProfile? _gameOpen;
    private GlobalHotkey? _hotkey;
    private TrayIcon? _tray;
    private CleanupPlan? _plan;
    private bool _busy;
    private bool _exiting;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        Sun.OpacityMask = BuildSunStripes();
        if (App.StartMinimized) { ShowActivated = false; ShowInTaskbar = false; Opacity = 0; }

        // Janela escondida ou minimizada: nada pra mostrar, então nada é lido
        _fast.Tick += (_, _) => { if (OnScreen) RefreshLive(); };
        _slow.Tick += async (_, _) => { if (OnScreen) await RefreshPlanAsync(rebuildCards: false); };
        _autoTimer.Tick += async (_, _) => await AutoTickAsync();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    // ===================================================================== ciclo de vida

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (App.Capture is null)
            _tray = new TrayIcon(ShowFromTray, () => Dispatcher.Invoke(ToggleBoost), () => Dispatcher.Invoke(ExitApp));

        RefreshHeader();
        RefreshProfiles();
        RefreshTweaks();
        RefreshDiagnostics();
        RefreshLive();
        await RefreshPlanAsync(rebuildCards: true);
        _fast.Start();
        _slow.Start();

        AutoSwitch.IsChecked = _settings.AutoBoost;
        UpdateAutoCheck.IsChecked = _settings.CheckUpdates;
        RefreshUpdateUi();
        RefreshStartup();
        RefreshPriority();
        RefreshGpu();
        if (App.Capture is { } cap) { HotkeyText.Text = $"{_settings.Hotkey} liga e desliga o Modo Jogo de qualquer lugar, até com o jogo aberto."; await CaptureAndExit(cap.Tab, cap.File); return; }
        SetupHotkey();
        _autoTimer.Start();
        if (_settings.CheckUpdates) _ = CheckForUpdateAsync(quiet: true, delay: TimeSpan.FromSeconds(8));
        if (App.StartMinimized) { Hide(); Opacity = 1; ShowInTaskbar = true; AppLog.Info("Iniciado minimizado perto do relógio"); }

        // Recuperação: se ficou uma cópia pendente (PC travou ou app caiu no meio do Modo Jogo)
        if (_boost.GameMode.LoadSnapshot() is { } snap)
        {
            var answer = MessageBox.Show(
                $"O Modo Jogo ficou ativo desde {snap.CreatedAt:dd/MM 'às' HH:mm} (o PC pode ter desligado no meio).\n\nRestaurar as configurações de antes agora?",
                "Sunset Boost", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes) await RunRestoreAsync();
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting) return;
        // O X esconde a janela; o app continua perto do relógio
        e.Cancel = true;
        Hide();
        if (!_trayHintShown) { _tray?.Notify("Sunset Boost continua aqui", "Clique duas vezes no ícone pra abrir. Pra sair de vez: botão direito › Sair."); _trayHintShown = true; }
    }

    public void BringToFront() => ShowFromTray();

    private void ShowFromTray() => Dispatcher.Invoke(() =>
    {
        Opacity = 1;
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    });

    private async void ExitApp()
    {
        if (_boost.GameMode.IsActive)
        {
            var r = MessageBox.Show("O Modo Jogo está ativo. Restaurar as configurações antes de sair?\n\n(Se escolher Não, o app oferece restaurar na próxima vez que abrir.)",
                "Sunset Boost", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            if (r == MessageBoxResult.Yes) await RunRestoreAsync();
        }
        _exiting = true;
        _fast.Stop(); _slow.Stop(); _autoTimer.Stop();
        _hotkey?.Dispose();
        _monitor.Dispose();
        _tray?.Dispose();
        Close();
        Application.Current.Shutdown();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ===================================================================== abas

    private async void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not RadioButton { Tag: string tab }) return;
        ShowTab(tab);
        if (tab == "clean") await RefreshPlanAsync(rebuildCards: true);
        if (tab == "game") RefreshTweaks();
        if (tab == "diag") RefreshDiagnostics();
    }

    private void ShowTab(string tab)
    {
        ScreenHome.Visibility = tab == "home" ? Visibility.Visible : Visibility.Collapsed;
        ScreenClean.Visibility = tab == "clean" ? Visibility.Visible : Visibility.Collapsed;
        ScreenGame.Visibility = tab == "game" ? Visibility.Visible : Visibility.Collapsed;
        ScreenProf.Visibility = tab == "prof" ? Visibility.Visible : Visibility.Collapsed;
        ScreenDiag.Visibility = tab == "diag" ? Visibility.Visible : Visibility.Collapsed;
        var target = (FrameworkElement)(tab switch { "clean" => ScreenClean, "game" => ScreenGame, "prof" => ScreenProf, "diag" => ScreenDiag, _ => ScreenHome });
        target.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
    }

    // ===================================================================== dados ao vivo

    private bool OnScreen => IsVisible && WindowState != WindowState.Minimized;

    private void RefreshLive()
    {
        var mem = MemoryInfo.Read();
        // RAM e CPU são números do Windows inteiro; a GPU vem de um contador por processo, então pausa com jogo aberto
        var (cpu, gpu) = _monitor.Sample(withGpu: _gameOpen is null);
#if DEBUG
        if (App.Capture is not null) (mem, cpu, gpu) = (DemoData.Memory, DemoData.Cpu, DemoData.Gpu);
#endif

        FreeRam.Text = Fmt.Bytes(mem.AvailableBytes);
        TotalRam.Text = $"de {Fmt.Bytes(mem.TotalBytes)}";
        SetArc(ArcRam, mem.UsedPercent);
        SetArc(ArcCpu, cpu);
        SetArc(ArcGpu, gpu ?? 0);
        LRam.Text = $"{mem.UsedPercent:0}%";
        LCpu.Text = $"{cpu:0}%";
        LGpu.Text = gpu is { } g ? $"{g:0}%" : _gameOpen is not null ? "pausa" : "n/d";

        if (mem.StandbyBytes is { } sb)
        {
            StatStandby.Text = Fmt.Bytes(sb);
            StatStandbyInfo.Text = mem.FreeBytes is { } f ? $"cache · livre de verdade: {Fmt.Bytes(f)}" : "cache do Windows";
        }
        else StatStandby.Text = "n/d";
    }

    /// <summary>Arco do radar: comprimento do traço proporcional à porcentagem.</summary>
    private static void SetArc(Ellipse e, double percent)
    {
        double t = e.StrokeThickness, circ = Math.PI * (e.Width - t);
        double len = circ * Math.Clamp(percent, 0, 100) / 100 / t;
        e.StrokeDashArray = new DoubleCollection { Math.Max(len, 0.001), circ / t + 1 };
    }

    private async Task RefreshPlanAsync(bool rebuildCards)
    {
        if (_busy) return;
        // Jogo aberto: a lista de apps não é atualizada (ela abre os outros processos pra medir a RAM)
        if (_gameOpen is not null) { StatFreeWho.Text = $"{_gameOpen.DisplayName} aberto: lista pausada até o jogo fechar"; return; }
        try
        {
            _plan = await Task.Run(_boost.BuildPlan);
#if DEBUG
            if (App.Capture is not null) _plan = DemoData.Plan(_boost.Profile);
#endif
        }
        catch (Exception ex) { AppLog.Error("Falha ao ler processos", ex); return; }

        int procs = _plan.Groups.Sum(g => g.Processes.Count);
        StatProc.Text = procs.ToString();
        var closing = _plan.ToClose.ToList();
        StatFree.Text = "+" + Fmt.Bytes(_settings.CloseApps ? _plan.EstimatedFreedBytes : 0);
        StatFreeWho.Text = closing.Count == 0 ? "nada pra fechar agora" : string.Join(", ", closing.Take(4).Select(g => g.DisplayName));

        // Jogo e anti-cheat ficam de fora: o app não abre esses processos, então não sabe quanto eles usam
        TopApps.ItemsSource = _plan.Groups.Where(g => !BoringReasons.Contains(g.Reason) && !g.RamUnknown)
            .OrderByDescending(g => g.PrivateBytes).Take(4)
            .Select(g => new TopAppVm(g.DisplayName, Fmt.Bytes(g.PrivateBytes), ListLabel(g), RamBrush(g))).ToList();

        var tweaksOn = _boost.GameMode.Tweaks.Count(_boost.IsTweakEnabled);
        CtaSub.Text = _boost.GameMode.IsActive
            ? "Devolve todas as configurações de antes"
            : $"Fecha {(_settings.CloseApps ? closing.Count : 0)} apps, {(_settings.PurgeStandbyList ? "limpa a standby list" : "mantém a standby list")} e ativa {tweaksOn} ajustes";

        if (rebuildCards) RebuildCards();
        UpdateCleanTotal();
    }

    private void RebuildCards()
    {
        if (_plan is null) return;
        _cardBytes.Clear();
        var cards = new List<AppCardVm>();
        var mb5 = 5L * 1024 * 1024;

        foreach (var g in _plan.Groups.Where(g => g.List == CleanupList.CloseByDefault))
            cards.Add(Card(g, Res("Pink")));
        foreach (var g in _plan.Groups.Where(g => g.List == CleanupList.UserDecides && (g.PrivateBytes >= mb5 || g.WillClose)))
            cards.Add(Card(g, Res("Gold")));
        foreach (var g in _plan.Groups.Where(g => g.List == CleanupList.NeverClose && !BoringReasons.Contains(g.Reason)).OrderByDescending(g => g.PrivateBytes).Take(6))
            cards.Add(new AppCardVm(g.AppId, g.DisplayName, g.RamUnknown ? "—" : Fmt.Bytes(g.PrivateBytes), g.Reason, Res("Lavender"), true, false, null)
            {
                RamHint = g.RamUnknown ? "O Sunset Boost nunca abre o processo de jogos e anti-cheats, nem pra medir a RAM." : null,
            });

        Cards.ItemsSource = cards;

        AppCardVm Card(AppGroup g, Brush accent)
        {
            _cardBytes[g.AppId] = g.PrivateBytes;
            var info = $"{g.Processes.Count} processo{(g.Processes.Count == 1 ? "" : "s")} · {g.Reason}";
            return new AppCardVm(g.AppId, g.DisplayName, Fmt.Bytes(g.PrivateBytes), info, accent, false, g.WillClose, c => OnCardToggled(c, g.List));
        }
    }

    private void OnCardToggled(AppCardVm card, CleanupList list)
    {
        // Fecha por padrão: desligar = "manter". Você decide: ligar = "fechar". Voltar ao padrão apaga a escolha.
        bool isDefault = list == CleanupList.CloseByDefault ? card.IsOn : !card.IsOn;
        if (isDefault) _settings.AppDecisions.Remove(card.Id);
        else _settings.AppDecisions[card.Id] = card.IsOn ? AppDecision.Close : AppDecision.Keep;
        App.SaveSettings();
        AppLog.Info($"Escolha: {card.Name} → {(card.IsOn ? "fechar" : "manter")}");
        UpdateCleanTotal();
    }

    private void UpdateCleanTotal()
    {
        long total = Cards.ItemsSource is IEnumerable<AppCardVm> cards
            ? cards.Where(c => c.IsOn && _cardBytes.ContainsKey(c.Id)).Sum(c => _cardBytes[c.Id]) : 0;
        CleanTotal.Text = Fmt.Bytes(total);
    }

    // ===================================================================== Modo Jogo (ajustes)

    private void RefreshTweaks()
    {
        bool admin = Elevation.IsElevated;
        var rows = new List<TweakRowVm>
        {
            new("fechar-apps", "Fechar apps pesados", "Os apps marcados na aba Limpeza", "ALTO", Res("Pink"), Hex("#2A0718"),
                _settings.CloseApps, null, r => { _settings.CloseApps = r.IsOn; App.SaveSettings(); }),
            // Ligado = limite de RAM em cada navegador. Desligado = o app não mexe nos navegadores. Nunca fecha nenhum.
            new("limitar-navegadores", $"Limitar os navegadores a {(_settings.EdgeLimitMB / 1024.0):0.#} GB",
                $"{Browsers.Names} · passou do limite, fecha as abas mais pesadas", "MÉDIO", Res("Orange"), Hex("#3A1500"),
                _settings.EdgeLite, null, r => { _settings.EdgeLite = r.IsOn; App.SaveSettings(); AppLog.Info($"Limite dos navegadores: {(r.IsOn ? "ligado" : "desligado")}"); }),
            new("standby", "Limpar standby list", "Como o ISLC · menos travadinhas", "MÉDIO", Res("Orange"), Hex("#3A1500"),
                _settings.PurgeStandbyList, admin ? null : "precisa de admin · ", r => { _settings.PurgeStandbyList = r.IsOn; App.SaveSettings(); }),
        };
        foreach (var t in _boost.GameMode.Tweaks)
        {
            var (label, bg, fg) = t.Impact switch
            {
                Impact.Alto => ("ALTO", Res("Pink"), Hex("#2A0718")),
                Impact.Medio => ("MÉDIO", Res("Orange"), Hex("#3A1500")),
                Impact.Conforto => ("CONFORTO", Res("Cyan"), Hex("#05313A")),
                _ => ("BAIXO", Res("Lavender"), (Brush)System.Windows.Media.Brushes.White),
            };
            string? note = null;
            try { if (!_boost.GameMode.IsActive && t.AlreadyOptimal() == true) note = "✓ já está assim no seu PC · "; } catch { }
            if (!admin && t.Id is "servicos" or "plano-energia") note = "precisa de admin · " + (note ?? "");
            rows.Add(new TweakRowVm(t.Id, ShortTitle(t), t.Detail, label, bg, fg, _boost.IsTweakEnabled(t), note, r =>
            {
                _settings.Tweaks[r.Id] = r.IsOn;
                App.SaveSettings();
            }));
        }
        TweakRows.ItemsSource = rows;
        RefreshHeader();
    }

    private static string ShortTitle(ITweak t) => t.Id switch
    {
        "game-dvr" => "Desligar gravação do Xbox (Game DVR)",
        "servicos" => "Pausar Search, SysMain e Spooler",
        "plano-energia" => "Plano \"Desempenho Máximo\"",
        _ => t.Title,
    };

    // ===================================================================== perfis e diagnóstico

    private void RefreshProfiles()
    {
        var gradients = new Dictionary<string, (string, string)>
        {
            ["valorant"] = ("#FF4D6D", "#7A1238"), ["lol"] = ("#FFD166", "#C2367A"), ["steam"] = ("#3EE6FF", "#1B3A7A"),
            ["epic"] = ("#C0A0FF", "#3B1458"), ["generico"] = ("#7B6CFF", "#140B2E"),
        };
        var details = new Dictionary<string, string>
        {
            ["valorant"] = "Mantém Riot Client e Vanguard. Fecha Steam e Epic.",
            ["lol"] = "Mantém Riot Client e Vanguard. Fecha Steam e Epic.",
            ["steam"] = "Mantém a Steam. Fecha Riot e Epic.",
            ["epic"] = "Mantém Epic e EasyAntiCheat. Fecha Steam e Riot.",
            ["generico"] = "Nenhum launcher é mantido.",
        };
        Profiles.ItemsSource = GameProfiles.All.Select(p =>
        {
            var (a, b) = gradients[p.Id];
            var brush = new LinearGradientBrush(Hex2(a), Hex2(b), 60);
            brush.Freeze();
            return new ProfileVm(p.Id, p.DisplayName.ToUpperInvariant(), details[p.Id], brush, p.Id == _boost.Profile.Id);
        }).ToList();
    }

    private async void Profile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } button) return;
        bool hadKeyboardFocus = button.IsKeyboardFocused;
        _settings.ActiveProfileId = id;
        App.SaveSettings();
        AppLog.Info($"Perfil: {id}");
        RefreshProfiles();
        if (hadKeyboardFocus) FocusProfile(id);
        RefreshHeader();
        await RefreshPlanAsync(rebuildCards: true);
    }

    /// <summary>O RefreshProfiles recria os cards: devolve o foco do teclado ao card escolhido, senão ele some.</summary>
    private void FocusProfile(string id)
    {
        Profiles.UpdateLayout();
        for (int i = 0; i < Profiles.Items.Count; i++)
        {
            if (Profiles.Items[i] is not ProfileVm { Id: var itemId } || itemId != id) continue;
            if (Profiles.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject container
                && VisualTreeHelper.GetChildrenCount(container) > 0
                && VisualTreeHelper.GetChild(container, 0) is Button card)
                Keyboard.Focus(card);
            return;
        }
    }

    private void RefreshDiagnostics()
    {
        bool? gpuOn = null;
        try { gpuOn = GpuPreferences.IsOn(_gpu, GpuTargets.Select(t => t.ExePath)); } catch { }
        IEnumerable<DiagItem> items = DiagnosticsService.Run(gpuPreferenceOn: gpuOn);
#if DEBUG
        if (App.Capture is not null) items = DemoData.Diagnostics();
#endif
        DiagItems.ItemsSource = items.Select(d => d.Status switch
        {
            DiagStatus.Ok => new DiagVm("✓", Res("Mint"), d.Title, d.Detail),
            DiagStatus.Recommend => new DiagVm("!", Res("Gold"), d.Title, d.Detail),
            _ => new DiagVm("i", Res("Cyan"), d.Title, d.Detail),
        }).ToList();
    }

    private void RefreshHeader()
    {
        ProfilePill.Text = $"PERFIL · {_boost.Profile.DisplayName.ToUpperInvariant()}";
        var tags = new List<string>();
        if (_settings.SimulationMode) tags.Add("SIMULAÇÃO: NADA É ALTERADO");
        if (!Elevation.IsElevated) tags.Add("SEM ADMIN · MODO LIMITADO");
        if (_gameOpen is not null) tags.Add($"{_gameOpen.DisplayName.ToUpperInvariant()} ABERTO · APP EM SILÊNCIO");
        ModeLabel.Text = string.Join("  ·  ", tags);

        bool active = _boost.GameMode.IsActive;
        CtaTitle.Text = active ? "SAIR DO MODO JOGO" : "ENTRAR NO MODO JOGO";
        Cta.Background = (Brush)FindResource(active ? "CtaActiveBrush" : "CtaBrush");
        RestoreBtn.IsEnabled = active;
        SnapshotInfo.Text = _boost.GameMode.LoadSnapshot() is { } s
            ? $"Modo Jogo ativo · cópia das configurações salva em {s.CreatedAt:dd/MM HH:mm}"
            : "Antes de aplicar, o app salva uma cópia das configurações atuais no disco.";
        _tray?.SetBoostActive(active);
    }

    // ===================================================================== ações

    private void Cta_Click(object sender, RoutedEventArgs e) => ToggleBoost();

    private async void ToggleBoost()
    {
        if (_busy) return;
        if (_boost.GameMode.IsActive) await RunRestoreAsync();
        else await EnterBoostAsync("botão");
    }

    private async Task EnterBoostAsync(string origin)
    {
        if (_busy) return;
        SetBusy(true, "Entrando no Modo Jogo…");
        AppLog.Info($"Modo Jogo pedido por: {origin} | perfil={_boost.Profile.Id}");
        bool hideToTray = false;
        try
        {
            var result = await Task.Run(_boost.Enter);
            var freed = FreedText(result.Cleanup);
            var failures = result.Tweaks?.Failures.Count() ?? 0;
            Status(result.Simulated
                ? $"Simulação ({origin}): fecharia {result.Cleanup?.Groups.Count ?? 0} apps e aplicaria {result.SimulatedTweaks.Count} ajustes. Nada foi alterado."
                : $"Modo Jogo ativo ({origin}) · {freed} liberados" + (failures > 0 ? $" · {failures} ajuste(s) falharam (veja o log)" : ""));
            PlayBanner(result.Simulated ? "SIMULAÇÃO CONCLUÍDA" : "LIMPEZA CONCLUÍDA", "+" + freed,
                result.Simulated ? "NADA FOI ALTERADO · É SÓ UM ENSAIO" : "MODO JOGO ATIVO · BOA PARTIDA");
            _tray?.Notify("Modo Jogo", result.Simulated ? "Simulação concluída" : $"+{freed} liberados. Boa partida! (o app está aqui no relógio)");
            hideToTray = !result.Simulated && App.Capture is null;
        }
        catch (Exception ex) { AppLog.Error("Falha ao entrar no Modo Jogo", ex); Status("Erro: " + ex.Message); }
        finally { SetBusy(false); await AfterAction(); }
        // Modo Jogo ligado: a janela sai da frente e vai pra bandeja (duplo clique no ícone abre de novo)
        if (hideToTray && _boost.GameMode.IsActive) { Hide(); _trayHintShown = true; AppLog.Info("Modo Jogo ativo: janela escondida na bandeja"); }
    }

    // ===================================================================== automação (Fase 5)

    private async Task AutoTickAsync()
    {
        // Limite rígido dos navegadores: vale enquanto o Modo Jogo estiver ativo (ligado na mão ou sozinho)
        if (!_busy && _settings.EdgeLite && _boost.GameMode.IsActive)
        {
            var guards = await Task.Run(_boost.EnforceBrowserLimits);
            foreach (var guard in guards.Where(g => g.Kill.Count > 0))
                Status($"{guard.Browser} chegou a {Fmt.Bytes(guard.TotalBytes)}: {guard.Kill.Count} aba(s)/extensão(ões) derrubada(s)"
                       + (guard.StillOver ? " (o navegador continua aberto)" : " pra voltar ao limite"));
        }

        // Só nomes, da lista do Windows: não abre nenhum processo
        var running = await Task.Run(GameDetector.FindRunning);
        if (running?.Id != _gameOpen?.Id)
        {
            var wasOpen = _gameOpen;
            _gameOpen = running;
            AppLog.Info(running is not null ? $"{running.DisplayName} aberto: app em silêncio (não lê processos)" : $"{wasOpen?.DisplayName} fechou: app volta a ler os processos");
            RefreshHeader();
            if (running is null && OnScreen) await RefreshPlanAsync(rebuildCards: true);
        }

        if (_busy || !_settings.AutoBoost) return;
        var decision = _auto.Tick(DateTime.Now, running, _boost.GameMode.IsActive);

        if (decision.Action == AutoAction.Enter && decision.Profile is { } p)
        {
            if (!p.Id.Equals(_settings.ActiveProfileId, StringComparison.OrdinalIgnoreCase))
            {
                _settings.ActiveProfileId = p.Id;
                App.SaveSettings();
                RefreshProfiles();
            }
            _tray?.Notify("Sunset Boost", $"{p.DisplayName} aberto: ligando o Modo Jogo");
            await EnterBoostAsync($"{p.DisplayName} abriu");
        }
        else if (decision.Action == AutoAction.Exit)
        {
            AppLog.Info($"Automático: {decision.Profile?.DisplayName} fechou, restaurando");
            await RunRestoreAsync();
        }
    }

    private void SetupHotkey()
    {
        _hotkey?.Dispose();
        _hotkey = null;
        if (!Hotkey.TryParse(_settings.Hotkey, out var hk)) { HotkeyText.Text = $"Atalho inválido: {_settings.Hotkey}"; return; }
        _hotkey = new GlobalHotkey(this, hk, () => Dispatcher.Invoke(ToggleBoost));
        HotkeyText.Text = _hotkey.IsRegistered
            ? $"{hk.Text} liga e desliga o Modo Jogo de qualquer lugar, até com o jogo aberto."
            : $"{hk.Text} já está sendo usado por outro programa.";
        AppLog.Info($"Atalho {hk.Text}: {(_hotkey.IsRegistered ? "registrado" : "ocupado")}");
    }

    // ===================================================================== prioridade alta fixa

    private IReadOnlyList<string> PriorityTargets => PriorityRules.Targets(_settings.PriorityGames);

    private static string GameLabel(string exe) => exe.ToLowerInvariant() switch
    {
        "valorant-win64-shipping.exe" => "Valorant",
        "league of legends.exe" => "LoL",
        "fortniteclient-win64-shipping.exe" => "Fortnite",
        _ => System.IO.Path.GetFileNameWithoutExtension(exe),
    };

    private void RefreshPriority()
    {
        bool on = false;
        try { on = PriorityRules.IsOn(_priority, PriorityTargets); } catch (Exception ex) { AppLog.Warn($"Prioridade fixa: não consegui ler o registro: {ex.Message}"); }
        bool canChange = Elevation.IsElevated && !_settings.SimulationMode;
        PrioritySwitch.IsChecked = on;
        PrioritySwitch.IsEnabled = canChange;
        AddPriorityGame.IsEnabled = canChange;
        var games = string.Join(", ", PriorityTargets.Select(GameLabel));
        PriorityText.Text = !Elevation.IsElevated ? "Precisa abrir o app como administrador."
            : _settings.SimulationMode ? "Desligue a simulação pra mudar."
            : on ? $"Ligada: {games} sempre abrem em prioridade Alta. Fica assim até você desligar."
            : $"O próprio Windows abre {games} em prioridade Alta. O app nunca toca no jogo.";
    }

    private void PrioritySwitch_Click(object sender, RoutedEventArgs e)
    {
        bool want = PrioritySwitch.IsChecked == true;
        var targets = PriorityTargets;
        var errors = want ? PriorityRules.Apply(_priority, targets) : PriorityRules.Remove(_priority, targets);
        AppLog.Info($"Prioridade fixa {(want ? "ligada" : "desligada")}: {string.Join(", ", targets)}" + (errors.Count > 0 ? $" | erros: {string.Join("; ", errors)}" : ""));
        Status(errors.Count > 0 ? "Prioridade fixa: " + string.Join("; ", errors)
            : want ? "Prioridade alta fixa ligada" + (_gameOpen is not null ? ": vale na próxima vez que o jogo abrir" : "")
            : "Prioridade alta fixa desligada: os jogos voltam a abrir em prioridade normal");
        RefreshPriority();
    }

    private void AddPriorityGame_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Escolha o .exe do jogo", Filter = "Jogo (*.exe)|*.exe", CheckFileExists = true };
        if (dlg.ShowDialog(this) != true) return;
        var exe = System.IO.Path.GetFileName(dlg.FileName);

        if (PriorityRules.KnownGames.Contains(exe, StringComparer.OrdinalIgnoreCase)) { Status($"{GameLabel(exe)} já está na lista"); return; }
        bool on = PrioritySwitch.IsChecked == true;
        if (_settings.PriorityGames.Contains(exe, StringComparer.OrdinalIgnoreCase))
        {
            if (MessageBox.Show($"{exe} já está na lista. Tirar ele da prioridade alta?", "Sunset Boost", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            PriorityRules.Remove(_priority, new[] { exe });
            _settings.PriorityGames.RemoveAll(g => g.Equals(exe, StringComparison.OrdinalIgnoreCase));
            App.SaveSettings();
            Status($"{exe} saiu da prioridade alta");
            RefreshPriority();
            return;
        }
        if (!PriorityRules.IsAllowed(exe, out var why)) { Status($"{exe}: {why}"); return; }

        _settings.PriorityGames.Add(exe);
        App.SaveSettings();
        var errors = on ? PriorityRules.Apply(_priority, new[] { exe }) : new List<string>();
        AppLog.Info($"Prioridade fixa: {exe} adicionado" + (errors.Count > 0 ? $" | erro: {errors[0]}" : ""));
        Status(errors.Count > 0 ? errors[0] : on ? $"{exe} adicionado: já abre em prioridade alta" : $"{exe} adicionado: ligue a chave pra valer");
        RefreshPriority();
    }

    // ===================================================================== placa de vídeo dedicada

    /// <summary>Jogos conhecidos achados no PC + os que o usuário adicionou (caminho completo).</summary>
    private List<(string Name, string ExePath)> GpuTargets =>
        GameLocator.Find()
            .Concat(_settings.GpuGames.Where(p => GpuPreferences.IsAllowed(p, out _)).Select(p => (System.IO.Path.GetFileNameWithoutExtension(p), p)))
            .DistinctBy(t => t.Item2, StringComparer.OrdinalIgnoreCase).ToList();

    private void RefreshGpu()
    {
        var targets = GpuTargets;
        bool on = false;
        try { on = GpuPreferences.IsOn(_gpu, targets.Select(t => t.ExePath)); } catch (Exception ex) { AppLog.Warn($"Placa de vídeo: não consegui ler o registro: {ex.Message}"); }
        bool canChange = !_settings.SimulationMode;
        GpuSwitch.IsChecked = on;
        GpuSwitch.IsEnabled = canChange && targets.Count > 0;
        AddGpuGame.IsEnabled = canChange;
        var games = string.Join(", ", targets.Select(t => t.Name));
        bool one = targets.Count == 1;
        GpuText.Text = _settings.SimulationMode ? "Desligue a simulação pra mudar."
            : targets.Count == 0 ? "Nenhum jogo conhecido achado no PC. Use + JOGO pra escolher o .exe do jogo."
            : on ? $"Ligada: {games} {(one ? "abre" : "abrem")} sempre na placa dedicada. Fica assim até você desligar."
            : $"Garante que {games} {(one ? "abra" : "abram")} na placa dedicada, não na integrada. Faz diferença em PC com duas placas (a maioria dos notebooks).";
    }

    private void GpuSwitch_Click(object sender, RoutedEventArgs e)
    {
        bool want = GpuSwitch.IsChecked == true;
        var paths = GpuTargets.Select(t => t.ExePath).ToList();
        var errors = want ? GpuPreferences.Apply(_gpu, paths) : GpuPreferences.Remove(_gpu, paths);
        AppLog.Info($"Placa de vídeo dedicada {(want ? "ligada" : "desligada")}: {string.Join(", ", paths)}" + (errors.Count > 0 ? $" | erros: {string.Join("; ", errors)}" : ""));
        Status(errors.Count > 0 ? "Placa de vídeo: " + string.Join("; ", errors)
            : want ? "Placa de vídeo dedicada ligada" + (_gameOpen is not null ? ": vale na próxima vez que o jogo abrir" : "")
            : "Placa de vídeo dedicada desligada: o Windows volta a decidir");
        RefreshGpu();
        RefreshDiagnostics();
    }

    private void AddGpuGame_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Escolha o .exe do jogo", Filter = "Jogo (*.exe)|*.exe", CheckFileExists = true };
        if (dlg.ShowDialog(this) != true) return;
        var path = System.IO.Path.GetFullPath(dlg.FileName);
        var name = System.IO.Path.GetFileName(path);
        bool on = GpuSwitch.IsChecked == true;

        if (_settings.GpuGames.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            if (MessageBox.Show($"{name} já está na lista. Tirar ele da placa dedicada?", "Sunset Boost", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            GpuPreferences.Remove(_gpu, new[] { path });
            _settings.GpuGames.RemoveAll(g => g.Equals(path, StringComparison.OrdinalIgnoreCase));
            App.SaveSettings();
            Status($"{name} saiu da placa dedicada");
            RefreshGpu();
            return;
        }
        if (GameLocator.Find().Any(t => t.ExePath.Equals(path, StringComparison.OrdinalIgnoreCase))) { Status($"{name} já está na lista"); return; }
        if (!GpuPreferences.IsAllowed(path, out var why)) { Status($"{name}: {why}"); return; }

        _settings.GpuGames.Add(path);
        App.SaveSettings();
        var errors = on ? GpuPreferences.Apply(_gpu, new[] { path }) : new List<string>();
        AppLog.Info($"Placa de vídeo: {path} adicionado" + (errors.Count > 0 ? $" | erro: {errors[0]}" : ""));
        Status(errors.Count > 0 ? errors[0] : on ? $"{name} adicionado: já abre na placa dedicada" : $"{name} adicionado: ligue a chave pra valer");
        RefreshGpu();
    }

    // ===================================================================== atualizações

    private static readonly Lazy<System.Net.Http.HttpClient> Http = new(() => Updater.CreateClient());
    private UpdateInfo? _update;
    private DownloadedInstaller? _installer;
    private bool _updateBusy;

    /// <summary>
    /// Só baixa e instala com o app em admin: aí o download fica na pasta updates dentro do %ProgramData%, que só admin altera.
    /// Sem admin a pasta é a do usuário, e qualquer programa sem admin trocaria o instalador antes do UAC. Então sem admin
    /// o botão só abre a página da versão no navegador.
    /// </summary>
    private static bool CanInstallUpdates => App.Paths.IsProtected;


    private void RefreshUpdateUi()
    {
        var cur = Updater.Display(Updater.Current);
        VersionLabel.Text = $"V I C E   M O D E  ·  v {string.Join(" ", cur.ToCharArray())}";
        if (_updateBusy) return; // o texto do progresso fica até terminar
        UpdateButton.IsEnabled = true;
        if (_installer is not null && _update is not null)
        {
            UpdateButton.Content = $"INSTALAR v{Updater.Display(_update.Version)}";
            UpdateText.Text = $"Instalada: v{cur} · v{Updater.Display(_update.Version)} baixada e conferida";
        }
        else if (_update is not null && !CanInstallUpdates)
        {
            UpdateButton.Content = $"ABRIR PÁGINA DA v{Updater.Display(_update.Version)}";
            UpdateText.Text = $"Instalada: v{cur} · versão nova: v{Updater.Display(_update.Version)} · sem admin, baixe pela página";
        }
        else if (_update is not null)
        {
            UpdateButton.Content = $"BAIXAR v{Updater.Display(_update.Version)}";
            UpdateText.Text = $"Instalada: v{cur} · versão nova: v{Updater.Display(_update.Version)}";
        }
        else
        {
            UpdateButton.Content = "VERIFICAR ATUALIZAÇÕES";
            UpdateText.Text = $"Instalada: v{cur}";
        }
        UpdatePill.Visibility = _update is null ? Visibility.Collapsed : Visibility.Visible;
        UpdatePill.Content = _update is null ? "" : _installer is null ? $"⬆ NOVA VERSÃO v{Updater.Display(_update.Version)}" : $"⬆ INSTALAR v{Updater.Display(_update.Version)}";
    }

    private void UpdateAutoCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.CheckUpdates = UpdateAutoCheck.IsChecked == true;
        App.SaveSettings();
        Status(_settings.CheckUpdates ? "Vai procurar versão nova sempre que abrir" : "Não procura mais versão nova ao abrir (o botão continua funcionando)");
    }

    /// <param name="quiet">Ao abrir: sem versão nova ou sem internet, não fala nada (só no log).</param>
    private async Task CheckForUpdateAsync(bool quiet, TimeSpan delay = default)
    {
        if (_updateBusy) return;
        if (delay > TimeSpan.Zero) await Task.Delay(delay);
        _updateBusy = true;
        UpdateButton.IsEnabled = false;
        UpdateText.Text = "Procurando versão nova…";
        try
        {
            _update = await Updater.CheckAsync(Http.Value, Updater.Current);
            _installer = null;
            AppLog.Info(_update is null ? "Atualizações: em dia" : $"Atualizações: v{Updater.Display(_update.Version)} disponível");
            if (_update is not null) Status($"Versão nova do Sunset Boost: v{Updater.Display(_update.Version)}. Clique em ⬆ NOVA VERSÃO pra baixar.");
            else if (!quiet) Status($"Você já está na versão mais nova (v{Updater.Display(Updater.Current)})");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Atualizações: não deu pra verificar: {ex.Message}");
            if (!quiet) Status("Não deu pra verificar atualizações: " + ex.Message);
        }
        finally
        {
            _updateBusy = false;
            RefreshUpdateUi();
        }
    }

    private async void UpdateAction_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy) return;
        if (_update is null) { await CheckForUpdateAsync(quiet: false); return; }
        if (!CanInstallUpdates) { OpenReleasePage(_update); return; }
        if (_installer is null) { await DownloadUpdateAsync(); if (_installer is null) return; }
        InstallUpdate();
    }

    private async Task DownloadUpdateAsync()
    {
        var u = _update!;
        // Notas da release sem a linha do SHA-256 (vem depois do "---")
        var notes = u.Notes.Split("\n---")[0].Trim();
        if (notes.Length > 700) notes = notes[..700] + "…";
        if (MessageBox.Show($"Baixar o Sunset Boost v{Updater.Display(u.Version)} ({u.InstallerSize / 1024.0 / 1024:0.#} MB) do GitHub?\n\n{notes}",
                "Atualização", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _updateBusy = true;
        UpdateButton.IsEnabled = false;
        var progress = new Progress<double>(p => UpdateText.Text = $"Baixando v{Updater.Display(u.Version)}… {p:P0}");
        try
        {
            _installer = await Updater.DownloadAsync(Http.Value, u, App.Paths.UpdatesDir, progress);
            AppLog.Info($"Atualizações: {u.InstallerName} baixado e conferido (SHA-256 da release)");
        }
        catch (Exception ex)
        {
            _installer = null;
            AppLog.Warn($"Atualizações: download recusado: {ex.Message}");
            MessageBox.Show($"A atualização não foi instalada: {ex.Message}.\n\nSe continuar, baixe pela página: {u.PageUrl}",
                "Atualização", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _updateBusy = false;
            RefreshUpdateUi();
        }
    }

    private void OpenReleasePage(UpdateInfo u)
    {
        try
        {
            // Endereço montado pelo app (github.com/carlosftb/sunset-boost/releases/tag/vX.Y.Z), não vem da resposta
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.PageUrl.ToString()) { UseShellExecute = true });
            Status($"Página da v{Updater.Display(u.Version)} aberta no navegador. Sem admin, o app não baixa nem instala sozinho.");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Atualizações: não deu pra abrir a página: {ex.Message}");
            Status($"Não deu pra abrir o navegador. A versão nova está em {u.PageUrl}");
        }
    }

    private void InstallUpdate()
    {
        if (_boost.GameMode.IsActive)
        {
            MessageBox.Show("O Modo Jogo está ativo. Clique em RESTAURAR TUDO antes de atualizar: a versão nova não pode começar com os ajustes da antiga aplicados.",
                "Atualização", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var v = Updater.Display(_update!.Version);
        if (MessageBox.Show($"Instalar a v{v} agora?\n\nO Windows vai pedir permissão de administrador pro instalador (confira: \"Sunset Boost\"). " +
                "O Sunset Boost fecha agora e o instalador oferece abrir de novo no final.",
                "Atualização", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            InstallerLauncher.Start(_installer!);
            AppLog.Info($"Atualizações: instalador da v{v} aberto; fechando o app");
            ExitApp();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Atualizações: não deu pra abrir o instalador: {ex.Message}");
            Status("Não deu pra abrir o instalador: " + ex.Message);
            if (ex is InvalidDataException) { _installer = null; RefreshUpdateUi(); } // arquivo apagado: volta pra BAIXAR
        }
    }

    /// <summary>A tarefa abre o launcher instalado ao lado (ambiente limpo), nunca este .exe direto.</summary>
    private static string? StartupTarget => StartupTask.LauncherPathFor(Environment.ProcessPath);

    private void RefreshStartup()
    {
        bool enabled = false;
        try { enabled = StartupTask.IsEnabled(); } catch { }
        StartupSwitch.IsChecked = enabled;
        bool installed = StartupTarget is { } target && StartupTask.RejectReason(target) is null;
        bool canChange = Elevation.IsElevated && installed;
        StartupSwitch.IsEnabled = canChange || enabled;
        StartupText.Text = enabled ? "Abre minimizado perto do relógio quando você entra no Windows."
            : !Elevation.IsElevated ? "Precisa abrir o app como administrador."
            : !installed ? "Disponível depois de instalar (instalar.ps1)."
            : "Abre minimizado perto do relógio quando você entra no Windows.";
    }

    private void StartupSwitch_Click(object sender, RoutedEventArgs e)
    {
        bool want = StartupSwitch.IsChecked == true;
        string err = "instale o app primeiro (instalar.ps1)";
        bool ok = want ? StartupTarget is { } target && StartupTask.Enable(target, out err) : StartupTask.Disable(out err);
        if (!ok) Status("Não deu pra mudar o início com o Windows: " + err);
        RefreshStartup();
    }

    private void AutoSwitch_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoBoost = AutoSwitch.IsChecked == true;
        App.SaveSettings();
        AppLog.Info($"Automático: {(_settings.AutoBoost ? "ligado" : "desligado")}");
    }

    private async void CleanNow_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _plan is null) return;
        SetBusy(true, "Limpando…");
        try
        {
            var report = await Task.Run(_boost.CleanNow);
            var freed = FreedText(report);
            var errors = report.Groups.Sum(g => g.Failed);
            Status((report.Simulated ? "Simulação: " : "") + $"{report.Groups.Count} apps · {freed}" + (errors > 0 ? $" · {errors} processo(s) não fecharam" : ""));
            PlayBanner(report.Simulated ? "SIMULAÇÃO CONCLUÍDA" : "LIMPEZA CONCLUÍDA", "+" + freed,
                report.Simulated ? "NADA FOI ALTERADO · É SÓ UM ENSAIO" : $"{report.Groups.Count} APPS FECHADOS");
        }
        catch (Exception ex) { AppLog.Error("Falha na limpeza", ex); Status("Erro: " + ex.Message); }
        finally { SetBusy(false); await AfterAction(); }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e) => await RunRestoreAsync();

    private async Task RunRestoreAsync()
    {
        SetBusy(true, "Restaurando…");
        try
        {
            var r = await Task.Run(_boost.Exit);
            Status(r.Ok ? "Tudo restaurado: o Windows voltou ao que era antes." : r.Message + " (veja o log)");
            _tray?.Notify("Sunset Boost", r.Ok ? "Configurações restauradas" : r.Message);
        }
        catch (Exception ex) { AppLog.Error("Falha ao restaurar", ex); Status("Erro: " + ex.Message); }
        finally { SetBusy(false); await AfterAction(); }
    }

    private async Task AfterAction()
    {
        RefreshHeader();
        RefreshTweaks();
        await RefreshPlanAsync(rebuildCards: true);
    }

    private static string FreedText(CleanupReport? r) =>
        r is null ? "0 MB" : Fmt.Bytes(Math.Max(0, r.Simulated ? r.EstimatedFreedBytes : r.MeasuredFreedBytes));

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        Cta.IsEnabled = CleanBtn.IsEnabled = !busy;
        Mouse.OverrideCursor = busy ? Cursors.AppStarting : null;
        if (message is not null) Status(message);
    }

    private void Status(string text) => StatusText.Text = text;

    private void PlayBanner(string title, string value, string sub)
    {
        BannerTitle.Text = title + " ";
        BannerValue.Text = value;
        BannerSub.Text = string.Join(" ", sub.ToCharArray());
        Banner.Visibility = Visibility.Visible;
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(-1200, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2400))));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1300, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2900)), new CubicEase { EasingMode = EasingMode.EaseIn }));
        anim.Completed += (_, _) => Banner.Visibility = Visibility.Collapsed;
        BannerMove.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    // ===================================================================== utilidades

    private string ListLabel(AppGroup g) => g.List switch
    {
        CleanupList.CloseByDefault => g.WillClose ? "vai fechar" : "você manteve",
        CleanupList.UserDecides => g.WillClose ? "vai fechar" : "você decide",
        _ => "protegido",
    };

    private Brush RamBrush(AppGroup g) => g.List switch
    {
        CleanupList.CloseByDefault => Res("Pink"),
        CleanupList.NeverClose => Res("Cyan"),
        _ => Res("White"),
    };

    private Brush Res(string key) => (Brush)FindResource(key);
    private static Brush Hex(string hex) { var b = new SolidColorBrush(Hex2(hex)); b.Freeze(); return b; }
    private static Color Hex2(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>Faixas horizontais no sol (estilo synthwave): máscara de opacidade com degraus.</summary>
    private static Brush BuildSunStripes()
    {
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        const int stripes = 13;
        for (int i = 0; i < stripes; i++)
        {
            double top = (double)i / stripes, cut = top + (1.0 / stripes) * (0.78 - i * 0.015);
            mask.GradientStops.Add(new GradientStop(Colors.Black, top));
            mask.GradientStops.Add(new GradientStop(Colors.Black, cut));
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, cut));
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, (double)(i + 1) / stripes));
        }
        mask.Freeze();
        return mask;
    }

    /// <summary>Modo "--captura": mostra a aba pedida, salva um PNG da janela e fecha.</summary>
    private async Task CaptureAndExit(string tab, string file)
    {
        var radio = FindTabs().FirstOrDefault(r => (string)r.Tag == tab);
        if (radio is not null) radio.IsChecked = true;
        // "boost": aperta o botão grande de verdade, mas SÓ com a simulação ligada
        if (tab == "boost" && _settings.SimulationMode) ToggleBoost();
        await Task.Delay(tab == "boost" ? 1300 : 1500);
        RefreshLive();
        await Task.Delay(300);

        var rtb = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(Root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        await using (var fs = File.Create(file)) enc.Save(fs);
        _exiting = true;
        Application.Current.Shutdown();
    }

    private IEnumerable<RadioButton> FindTabs()
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is RadioButton rb) yield return rb;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
    }
}
