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
            G("valorant", "Valorant", CleanupList.NeverClose, "o jogo", false, 2100),
            G("vgtray", "Vanguard", CleanupList.NeverClose, "anti-cheat", false, 12),
            G("defender", "Microsoft Defender", CleanupList.NeverClose, "antivírus / segurança", false, 180),
            G("sunsetboost", "Sunset Boost", CleanupList.NeverClose, "o próprio Sunset Boost (ou quem o abriu)", false, 95),
        }, new SafetyContext());
    }

    public static List<DiagItem> Diagnostics() => new()
    {
        new(DiagStatus.Recommend, "Abra o Sunset Boost como administrador",
            "Sem admin, ele não consegue pausar serviços, limpar a standby list nem fechar apps elevados."),
        new(DiagStatus.Recommend, "Desligar o \"Startup boost\" do Edge",
            "É ele que mantém o Edge na memória mesmo fechado. No Edge: Configurações › Sistema e desempenho › Aumento de inicialização."),
        new(DiagStatus.Recommend, "Steam e Discord abrindo junto com o Windows",
            "Tire da inicialização em Gerenciador de Tarefas › Aplicativos de inicialização. Economiza memória desde o boot."),
        new(DiagStatus.Ok, "Plano de energia: Alto desempenho", "Já está certo no seu PC."),
        new(DiagStatus.Info, "Painel de controle da NVIDIA, perfil do Valorant",
            "Modo de baixa latência: Ligado · Gerenciamento de energia: Preferir desempenho máximo · Sincronização vertical: Desligado."),
        new(DiagStatus.Info, "Dentro do Valorant",
            "Limite de FPS um pouco acima da taxa do monitor deixa o tempo de quadro mais estável que FPS ilimitado."),
    };
}
#endif
