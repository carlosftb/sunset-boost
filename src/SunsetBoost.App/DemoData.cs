#if DEBUG
using SunsetBoost.Core.Cleanup;
using SunsetBoost.Core.Diagnostics;
using SunsetBoost.Core.Memory;
using SunsetBoost.Core.Processes;

namespace SunsetBoost.App;

/// <summary>
/// Dados fixos pro modo "--captura" (só no build Debug): as imagens do README não mostram os processos, a memória,
/// a placa de vídeo nem os programas de quem gerou o print.
/// </summary>
internal static class DemoData
{
    private const long MB = 1024 * 1024;

    public static MemorySnapshot Memory => new(16UL * 1024 * MB, 6554UL * MB, 2355UL * MB, 1100UL * MB);
    public const double Cpu = 18, Gpu = 7;

    public static CleanupPlan Plan(GameProfile profile)
    {
        int pid = 1000;
        AppGroup G(string id, string name, CleanupList list, string reason, bool close, params long[] mb) =>
            new(id, name, list, reason, mb.Select(m => new ProcessInfo(pid++, 4, id, null, 1, m * MB, 0)).ToList(), close);

        return new CleanupPlan(profile, new List<AppGroup>
        {
            G("edge", "Microsoft Edge", CleanupList.CloseByDefault, "navegador", true, 420, 310, 260, 180, 140, 90),
            G("steam", "Steam", CleanupList.CloseByDefault, "launcher que não é do jogo atual", true, 160, 120, 90),
            G("onedrive", "OneDrive", CleanupList.CloseByDefault, "sincronização em segundo plano", true, 95),
            G("discord", "Discord", CleanupList.UserDecides, "comunicação", false, 230, 140, 60),
            G("spotify", "Spotify", CleanupList.UserDecides, "música / mídia", false, 180, 85),
            // Jogo e anti-cheat com 0, como no app de verdade: ele nunca abre esses processos, nem pra medir a RAM ("—")
            G("valorant", "Valorant", CleanupList.NeverClose, "o jogo", false, 0),
            G("vgtray", "Vanguard", CleanupList.NeverClose, "anti-cheat", false, 0),
            G("defender", "Microsoft Defender", CleanupList.NeverClose, "antivírus / segurança", false, 180),
            G("sunsetboost", "Sunset Boost", CleanupList.NeverClose, "o próprio Sunset Boost (ou quem o abriu)", false, 95),
        }, new SafetyContext());
    }

    public static List<DiagItem> Diagnostics() =>
    [
        new(DiagStatus.Recommend, "Tela principal a 60 Hz, mas aceita 144 Hz",
            "É o ganho mais visível que existe: o jogo pode mostrar até 144 quadros por segundo em vez de 60. " +
            "Configurações › Sistema › Tela › Vídeo avançado › Escolha uma taxa de atualização. " +
            "Confira também se o cabo é DisplayPort ou HDMI 2.0+ (cabo velho limita os Hz)."),
        new(DiagStatus.Recommend, "Memória RAM a 2133 MT/s, mas o pente é de 3200",
            "O perfil XMP (Intel) ou EXPO/DOCP (AMD) está desligado na BIOS. Ligar costuma dar 10% a 20% mais FPS em jogos " +
            "que dependem do processador (Valorant, LoL, CS2). Entre na BIOS (Del ou F2 ao ligar o PC) e ative o XMP/EXPO."),
        new(DiagStatus.Ok, "Driver da NVIDIA GeForce RTX 3060 em dia", "Versão de 07/11/2026."),
        new(DiagStatus.Recommend, "Abra o Sunset Boost como administrador",
            "Sem admin, ele não consegue pausar serviços, limpar a standby list nem fechar apps elevados."),
        new(DiagStatus.Recommend, "Desligar o \"Startup boost\" do Edge",
            "É ele que mantém o Edge na memória mesmo fechado. No Edge: Configurações › Sistema e desempenho › Aumento de inicialização."),
        new(DiagStatus.Recommend, "Steam e Discord abrindo junto com o Windows",
            "Tire da inicialização em Gerenciador de Tarefas › Aplicativos de inicialização. Economiza memória desde o boot."),
        new(DiagStatus.Ok, "Plano de energia: Alto desempenho", "Já está certo no seu PC."),
        // Mesmos textos que o verificador gera (HardwareChecks.GameTips) pra uma NVIDIA com tela de 144 Hz
        .. HardwareChecks.GameTips(new[] { new DisplayInfo("Tela principal", 1920, 1080, 144, 144) }, new[] { new GpuInfo("NVIDIA GeForce RTX 3060", null) }),
    ];
}
#endif
