using System.Globalization;
using System.Text.RegularExpressions;

namespace SunsetBoost.Core.Diagnostics;

/// <summary>Uma tela ligada: resolução atual, Hz atual e o maior Hz que ela aceita nessa resolução.</summary>
public sealed record DisplayInfo(string Name, int Width, int Height, int CurrentHz, int MaxHz, bool Primary = true);

/// <summary>Um pente de memória (SMBIOS tipo 17). Velocidades em MT/s; 0 = desconhecida.</summary>
public sealed record MemoryModule(string Type, int RatedSpeed, int ConfiguredSpeed, string PartNumber, int SizeMB);

/// <summary>Uma placa de vídeo do Windows. DriverDate null = desconhecida.</summary>
public sealed record GpuInfo(string Name, DateTime? DriverDate)
{
    public bool IsNvidia => Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
    public bool IsAmd => Name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase);
    public bool IsIntel => Name.Contains("Intel", StringComparison.OrdinalIgnoreCase);

    /// <summary>Placa dedicada: NVIDIA, AMD Radeon RX/Pro ou Intel Arc A/B. O resto da Intel/AMD é integrada ao processador.</summary>
    public bool IsDedicated => IsNvidia
        || (IsAmd && Regex.IsMatch(Name, @"\b(RX|Pro|FirePro)\b", RegexOptions.IgnoreCase))
        || (IsIntel && Regex.IsMatch(Name, @"\bArc\(TM\)\s*[AB]\d|\bArc\s+[AB]\d", RegexOptions.IgnoreCase));

    /// <summary>Adaptadores que não são placa de vídeo de verdade (área de trabalho remota, driver genérico).</summary>
    public bool IsVirtual => Regex.IsMatch(Name, @"Basic Display|Basic Render|Remote|Virtual|Parsec|Hyper-V|Meta Virtual", RegexOptions.IgnoreCase);
}

/// <summary>Tudo o que o verificador lê do PC. Nos testes entra uma versão falsa.</summary>
public interface IHardwareInfo
{
    IReadOnlyList<DisplayInfo> Displays();
    IReadOnlyList<MemoryModule> Memory();
    IReadOnlyList<GpuInfo> Gpus();
    /// <summary>true = tem bateria (notebook). null = não deu pra saber.</summary>
    bool? HasBattery();
    /// <summary>true = rodando na bateria agora.</summary>
    bool OnBattery();
    /// <summary>Espaço livre e total do disco do Windows (bytes).</summary>
    (long Free, long Total)? SystemDrive();
    /// <summary>Integridade da Memória (HVCI) ligada.</summary>
    bool? MemoryIntegrity();
    /// <summary>"Aumentar precisão do ponteiro" (aceleração do mouse) ligada.</summary>
    bool? MouseAcceleration();
    /// <summary>"Otimizações para jogos em janela" ligada. null = o Windows não gravou nada (valor padrão).</summary>
    bool? WindowedGameOptimizations();
}

/// <summary>
/// O verificador: procura configurações do PC que roubam desempenho de verdade (bem mais que qualquer ajuste do registro)
/// e explica como corrigir. Só lê; nunca altera nada.
/// </summary>
public static class HardwareChecks
{
    /// <summary>Driver de vídeo mais velho que isso já perde otimizações dos jogos novos.</summary>
    public static readonly TimeSpan OldDriver = TimeSpan.FromDays(180);

    /// <param name="gpuPreferenceOn">Se o ajuste "Placa de vídeo dedicada nos jogos" está ligado (null = não sei).</param>
    public static List<DiagItem> Run(IHardwareInfo hw, DateTime today, bool? gpuPreferenceOn = null)
    {
        var items = new List<DiagItem>();
        bool? laptop = Try(hw.HasBattery);

        Try(() => CheckDisplays(hw.Displays(), items));
        Try(() => CheckMemory(hw.Memory(), laptop, items));
        Try(() => CheckGpus(hw.Gpus(), today, gpuPreferenceOn, items));
        if (laptop == true && Try(hw.OnBattery))
            items.Add(new(DiagStatus.Recommend, "Notebook na bateria",
                "Na bateria o Windows e a placa de vídeo seguram o desempenho (às vezes pela metade). Pra jogar, ligue na tomada."));
        Try(() => CheckDisk(hw.SystemDrive(), items));

        if (Try(hw.WindowedGameOptimizations) != true)
            items.Add(new(DiagStatus.Recommend, "Ligar \"Otimizações para jogos em janela\"",
                "Menos atraso de imagem em jogos no modo janela sem bordas. Configurações › Sistema › Tela › Elementos gráficos › " +
                "Configurações gráficas avançadas (ou \"Alterar configurações gráficas padrão\") › Otimizações para jogos em janela."));

        if (Try(hw.MouseAcceleration) == true)
            items.Add(new(DiagStatus.Recommend, "Aceleração do mouse ligada",
                "Com ela, a mesma distância do mouse mexe a mira diferente conforme a velocidade. Painel de Controle › Mouse › " +
                "Opções de ponteiro › desmarque \"Aumentar precisão do ponteiro\". Não muda o FPS, deixa a mira consistente."));

        if (Try(hw.MemoryIntegrity) == true)
            items.Add(new(DiagStatus.Info, "Integridade da Memória ligada (proteção do Windows)",
                "Bloqueia drivers maliciosos. Em alguns jogos custa poucos % de FPS, mas o Sunset Boost recomenda deixar ligada: " +
                "desligar abre espaço pra malware no nível do sistema, e anti-cheats como o Vanguard podem exigir ela."));

        return items;
    }

    public static readonly Guid HighPerformancePlan = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid BalancedPlan = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    /// <summary>
    /// Desktop: Alto desempenho. Notebook: Equilibrado está certo (é o plano que os fabricantes ajustam; Alto desempenho
    /// só esquenta e gasta bateria). No notebook o ganho vem do "modo de energia" na tomada.
    /// </summary>
    public static DiagItem PowerPlan(Guid active, bool? laptop)
    {
        bool high = active == HighPerformancePlan || active == Tweaks.PowerPlanTweak.UltimatePerformance;
        if (laptop == true)
            return high || active == BalancedPlan
                ? new(DiagStatus.Ok, high ? "Plano de energia: Alto desempenho (notebook)" : "Plano de energia: Equilibrado (certo pra notebook)",
                    "Pra jogar, ligue na tomada e use Configurações › Sistema › Energia › Modo de energia › Melhor desempenho.")
                : new(DiagStatus.Recommend, "Plano de energia do fabricante ou economia",
                    "Em notebook, use o Equilibrado (Painel de Controle › Opções de Energia) e, na tomada, " +
                    "Configurações › Sistema › Energia › Modo de energia › Melhor desempenho.");
        return high
            ? new(DiagStatus.Ok, "Plano de energia: Alto desempenho", "Já está certo no seu PC.")
            : new(DiagStatus.Recommend, "Plano de energia não é o de Alto desempenho",
                "Painel de Controle › Opções de Energia › Alto desempenho. Ou ligue o ajuste no Modo Jogo.");
    }

    /// <summary>Dicas de configuração do jogo e do driver, com os números deste PC (Hz da tela, marca da placa).</summary>
    public static List<DiagItem> GameTips(IReadOnlyList<DisplayInfo> displays, IReadOnlyList<GpuInfo> gpus)
    {
        var items = new List<DiagItem>();
        var real = gpus.Where(g => !g.IsVirtual).ToList();
        bool nvidia = real.Any(g => g.IsNvidia), amd = real.Any(g => g.IsAmd && g.IsDedicated) || (!nvidia && real.Any(g => g.IsAmd));

        if (nvidia)
            items.Add(new(DiagStatus.Info, "Painel de controle da NVIDIA › Gerenciar configurações 3D",
                "Tamanho do cache do sombreador: 10 GB (menos travadinhas em jogos novos e de mundo aberto; o padrão do driver costuma ser menor) · " +
                "Gerenciamento de energia: Preferir desempenho máximo · Modo de baixa latência: Ligado (em jogo com NVIDIA Reflex, " +
                "use o Reflex do jogo em \"Ligado\") · Sincronização vertical: Desligado, ou Ligado só no painel se usar G-SYNC."));
        else if (amd)
            items.Add(new(DiagStatus.Info, "AMD Software › Jogos › Gráficos",
                "Radeon Anti-Lag: Ligado · Radeon Chill e Boost: Desligados em jogo competitivo (mudam o FPS no meio da partida) · " +
                "Aguardar a atualização vertical: Sempre desligado, a menos que o aplicativo especifique."));

        var main = displays.FirstOrDefault(d => d.Primary) ?? displays.FirstOrDefault();
        if (main is { CurrentHz: > 0 } m)
        {
            int cap = Math.Max(30, m.CurrentHz - 3);
            var vrr = nvidia ? "G-SYNC" : amd ? "FreeSync" : "G-SYNC/FreeSync";
            items.Add(new(DiagStatus.Info, $"Limite de FPS: {cap} (sua tela é de {m.CurrentHz} Hz)",
                $"FPS estável é melhor que alto e oscilando: o tempo entre os quadros fica igual e a mira fica previsível. " +
                $"Com {vrr} ligado, limitar em {cap} (3 abaixo dos Hz) mantém a tela sincronizada sem o atraso do V-Sync. " +
                "Use o limitador do próprio jogo (é o de menor atraso) ou o do driver; o RivaTuner deixa o tempo de quadro mais " +
                $"liso, mas soma um pouco de atraso. Se o PC não segura {cap} o tempo todo, baixe sombras e efeitos ou limite num valor que ele segura."));
        }
        return items;
    }

    private static void CheckDisplays(IReadOnlyList<DisplayInfo> displays, List<DiagItem> items)
    {
        foreach (var d in displays.Where(d => d.CurrentHz > 0))
        {
            // 59 Hz e 60 Hz são a mesma coisa pro monitor (NTSC): só avisa se der pra subir de verdade
            // Tela secundária: o jogo roda na principal, então é só conforto (Info), não desempenho
            if (d.MaxHz > d.CurrentHz + 2 && !d.Primary)
                items.Add(new(DiagStatus.Info, $"{d.Name} a {d.CurrentHz} Hz, mas aceita {d.MaxHz} Hz",
                    "Não muda o jogo na tela principal, mas deixa o mouse e as janelas mais fluidos nela. " +
                    "Configurações › Sistema › Tela › (escolha a tela) › Vídeo avançado › Escolha uma taxa de atualização."));
            else if (d.MaxHz > d.CurrentHz + 2)
                items.Add(new(DiagStatus.Recommend, $"{d.Name} a {d.CurrentHz} Hz, mas aceita {d.MaxHz} Hz",
                    $"É o ganho mais visível que existe: o jogo pode mostrar até {d.MaxHz} quadros por segundo em vez de {d.CurrentHz}. " +
                    "Configurações › Sistema › Tela › Vídeo avançado › Escolha uma taxa de atualização. " +
                    "Confira também se o cabo é DisplayPort ou HDMI 2.0+ (cabo velho limita os Hz)."));
            else
                items.Add(new(DiagStatus.Ok, $"{d.Name}: {d.CurrentHz} Hz ({d.Width}×{d.Height})", "Já está na taxa máxima que o Windows oferece."));
        }
    }

    // 1600 (DDR3) até 8999 (DDR5): quatro dígitos soltos no código do pente
    private static readonly Regex SpeedInPart = new(@"(?<!\d)(1[6-9]\d\d|[2-8]\d\d\d)(?!\d)");

    /// <summary>Velocidade escrita no código do pente (ex.: CMK16GX4M2B3200C16 → 3200). 0 = não achou.</summary>
    internal static int SpeedFromPartNumber(string part)
    {
        var m = SpeedInPart.Match(part ?? "");
        if (m.Success) return int.Parse(m.Value, CultureInfo.InvariantCulture);
        // Kingston Fury: KF + geração (4 = DDR4, 5 = DDR5) + velocidade/100: KF432C16... → 3200, KF560C40... → 6000
        var kf = Regex.Match(part ?? "", @"^KF[45](\d{2})C", RegexOptions.IgnoreCase);
        return kf.Success ? int.Parse(kf.Groups[1].Value, CultureInfo.InvariantCulture) * 100 : 0;
    }

    private static void CheckMemory(IReadOnlyList<MemoryModule> modules, bool? laptop, List<DiagItem> items)
    {
        if (modules.Count == 0) return;
        var running = modules.Select(m => m.ConfiguredSpeed > 0 ? m.ConfiguredSpeed : m.RatedSpeed).Where(s => s > 0).DefaultIfEmpty(0).Min();
        var type = modules[0].Type;
        var totalGB = modules.Sum(m => m.SizeMB) / 1024.0;

        // Velocidade de cada pente: o que o código diz, ou o que o próprio pente informa. Com pentes diferentes,
        // a memória toda roda na velocidade do mais lento: é esse o limite, não o do mais rápido.
        var speeds = modules.Select(m => Math.Max(SpeedFromPartNumber(m.PartNumber), m.RatedSpeed)).ToList();
        var rated = speeds.Min();
        bool soldered = type.StartsWith("LPDDR", StringComparison.OrdinalIgnoreCase);
        bool mixed = !soldered && modules.Select(m => m.PartNumber.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;

        if (mixed)
            items.Add(new(DiagStatus.Info, "Pentes de memória diferentes",
                $"Códigos: {string.Join(", ", modules.Select(m => m.PartNumber.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))}. " +
                $"Pentes misturados rodam todos na velocidade do mais lento" + (speeds.Max() > rated ? $" (aqui, {rated} MT/s mesmo com um de {speeds.Max()})" : "") +
                " e às vezes não aceitam XMP. Dois pentes iguais (um kit) dão o melhor resultado."));

        if (running > 0 && rated > running + 150 && !soldered)
            items.Add(new(DiagStatus.Recommend, $"Memória RAM a {running} MT/s, mas o pente é de {rated}",
                "O perfil XMP (Intel) ou EXPO/DOCP (AMD) está desligado na BIOS. Ligar costuma dar 10% a 20% mais FPS em jogos " +
                "que dependem do processador (Valorant, LoL, CS2). Entre na BIOS (Del ou F2 ao ligar o PC) e ative o XMP/EXPO."));
        // Velocidade padrão e não dá pra saber a do pente pelo código: pede pra conferir (se der pra saber, já está certo)
        else if (running > 0 && laptop == false && !soldered && modules.Any(m => SpeedFromPartNumber(m.PartNumber) == 0)
                 && ((type == "DDR4" && running <= 2666) || (type == "DDR5" && running <= 4800)))
            items.Add(new(DiagStatus.Recommend, $"Memória RAM a {running} MT/s ({type}): confira o XMP/EXPO",
                $"É a velocidade padrão, sem o perfil de fábrica. Se o pente for mais rápido (veja na caixa ou pelo código " +
                $"{modules[0].PartNumber}), ligue o XMP/EXPO na BIOS: 10% a 20% mais FPS em jogos que dependem do processador."));
        else if (running > 0)
            items.Add(new(DiagStatus.Ok, $"Memória RAM: {totalGB:0.#} GB {type} a {running} MT/s", "Rodando na velocidade do pente."));

        // Um pente só = canal simples: metade da banda de memória. Em notebook com memória soldada não dá pra mudar.
        if (modules.Count == 1 && !soldered)
            items.Add(new(DiagStatus.Recommend, "Só um pente de memória (canal simples)",
                "Com dois pentes iguais (dual channel) a memória fica duas vezes mais rápida: em jogos isso dá de 10% a 30% mais FPS, " +
                "principalmente com vídeo integrado. Comprar um segundo pente igual é o upgrade mais barato pra jogo."));

        if (totalGB > 0 && totalGB < 15)
            items.Add(new(DiagStatus.Info, $"{totalGB:0.#} GB de RAM",
                "Pra jogos atuais com Discord e navegador abertos, 16 GB é o recomendado. Feche o que puder antes de jogar (aba LIMPEZA)."));
    }

    private static void CheckGpus(IReadOnlyList<GpuInfo> gpus, DateTime today, bool? gpuPreferenceOn, List<DiagItem> items)
    {
        var real = gpus.Where(g => !g.IsVirtual).ToList();
        foreach (var g in real.Where(g => g.IsDedicated))
        {
            if (g.DriverDate is not { } date) continue;
            var age = today - date;
            var link = g.IsNvidia ? "nvidia.com/drivers (ou o app NVIDIA)" : g.IsAmd ? "amd.com/support (ou o AMD Software)" : "intel.com/arc";
            items.Add(age > OldDriver
                ? new(DiagStatus.Recommend, $"Driver da {g.Name} de {date:MM/yyyy}",
                    $"Tem {(int)(age.TotalDays / 30)} meses. Driver novo traz otimizações pros jogos recentes e correções. Baixe em {link}.")
                : new(DiagStatus.Ok, $"Driver da {g.Name} em dia", $"Versão de {date:dd/MM/yyyy}."));
        }

        bool hybrid = real.Any(g => g.IsDedicated) && real.Any(g => !g.IsDedicated);
        if (hybrid)
        {
            var dedicated = real.First(g => g.IsDedicated).Name;
            items.Add(gpuPreferenceOn == true
                ? new(DiagStatus.Ok, "Jogos usando a placa dedicada", $"Os jogos da lista abrem na {dedicated}.")
                : new(DiagStatus.Recommend, "Seu PC tem duas placas de vídeo: garanta a dedicada nos jogos",
                    $"Se o jogo abrir na integrada em vez da {dedicated}, o FPS cai pela metade ou mais. " +
                    "Ligue PERFIS › PLACA DE VÍDEO DEDICADA. Em notebook, ligue também o monitor/cabo na saída da placa dedicada quando houver."));
        }
        else if (real.Count > 0 && real.All(g => !g.IsDedicated))
            items.Add(new(DiagStatus.Info, $"Vídeo integrado ({real[0].Name})",
                "Sem placa dedicada, o que mais ajuda é memória em dual channel e na velocidade certa, e gráficos no baixo com resolução de renderização menor."));
    }

    private static void CheckDisk((long Free, long Total)? drive, List<DiagItem> items)
    {
        if (drive is not { Total: > 0 } d) return;
        var freeGB = d.Free / 1024.0 / 1024 / 1024;
        var pct = 100.0 * d.Free / d.Total;
        if (freeGB < 20 || pct < 10)
            items.Add(new(DiagStatus.Recommend, $"Disco do Windows quase cheio ({freeGB:0} GB livres)",
                "Com pouco espaço, o Windows não tem onde pôr o arquivo de paginação e as atualizações dos jogos falham. " +
                "Deixe pelo menos 20 GB livres: Configurações › Sistema › Armazenamento › Recomendações de limpeza."));
    }

    private static T? Try<T>(Func<T> f)
    {
        try { return f(); }
        catch (Exception ex) { Infrastructure.AppLog.Warn($"Verificador: {ex.Message}"); return default; }
    }

    private static void Try(Action a)
    {
        try { a(); }
        catch (Exception ex) { Infrastructure.AppLog.Warn($"Verificador: {ex.Message}"); }
    }
}
