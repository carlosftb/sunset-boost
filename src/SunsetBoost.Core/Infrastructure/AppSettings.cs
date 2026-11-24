using System.Text.Json;
using System.Text.Json.Serialization;

namespace SunsetBoost.Core.Infrastructure;

/// <summary>O que o usuário escolheu pra um app da lista "Você decide" (ou pra tirar um app de "Fecha por padrão").</summary>
public enum AppDecision { Default, Close, Keep }

public sealed class AppSettings
{
    /// <summary>Modo simulação: mostra o que seria feito sem tocar em nada.</summary>
    public bool SimulationMode { get; set; } = false;

    /// <summary>Perfil de jogo ativo (valorant, steam, epic, generico).</summary>
    public string ActiveProfileId { get; set; } = "valorant";

    /// <summary>Escolhas por app (chave = id do app, ex.: "discord").</summary>
    public Dictionary<string, AppDecision> AppDecisions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Segundos que o app espera depois de pedir pra um programa fechar, antes de forçar.</summary>
    public int GracefulCloseSeconds { get; set; } = 3;

    public bool PurgeStandbyList { get; set; } = true;

    /// <summary>Fechar os apps da limpeza ao entrar no Modo Jogo.</summary>
    public bool CloseApps { get; set; } = true;

    /// <summary>Ajustes ligados/desligados pelo usuário (chave = id do ajuste). Ausente = padrão do ajuste.</summary>
    public Dictionary<string, bool> Tweaks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string Hotkey { get; set; } = "Ctrl+Shift+B";

    /// <summary>Modo leve dos navegadores (Edge, Chrome, Opera GX): em vez de fechar, mantém só a 1ª aba e segura a memória no limite.
    /// (O nome ficou "Edge" pra não perder a escolha de quem já usava.)</summary>
    public bool EdgeLite { get; set; } = false;

    /// <summary>Limite rígido de memória de cada navegador durante o Modo Jogo (MB).</summary>
    public int EdgeLimitMB { get; set; } = 2048;

    /// <summary>Abriu um jogo conhecido → liga o boost; fechou → restaura.</summary>
    public bool AutoBoost { get; set; } = true;

    /// <summary>Jogos que o usuário adicionou à prioridade alta fixa (nome do .exe), além dos conhecidos.</summary>
    public List<string> PriorityGames { get; set; } = new();

    /// <summary>Jogos que o usuário adicionou à placa de vídeo dedicada (caminho completo do .exe), além dos achados sozinhos.</summary>
    public List<string> GpuGames { get; set; } = new();
}

/// <summary>Lê e grava o settings.json com escrita atômica (nunca deixa o arquivo pela metade).</summary>
public sealed class SettingsStore
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _file;

    public SettingsStore(string file) => _file = file;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_file)) return new AppSettings();
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_file), Json) ?? new AppSettings();
            // Garante comparação sem diferenciar maiúsculas depois de desserializar
            s.AppDecisions = new Dictionary<string, AppDecision>(s.AppDecisions, StringComparer.OrdinalIgnoreCase);
            return s;
        }
        catch (Exception ex)
        {
            AppLog.Error("Configurações corrompidas, usando padrão", ex);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings) => AtomicFile.WriteAllText(_file, JsonSerializer.Serialize(settings, Json));

    internal const int MaxLegacyBytes = 1 << 20;

    /// <summary>
    /// A versão 0.1 guardava o settings.json em %LOCALAPPDATA%, mesmo rodando com admin. Copia uma única vez pra pasta
    /// protegida: só se ainda não houver configuração lá, só se nem a pasta antiga nem o arquivo forem links (senão o app
    /// com admin leria um arquivo escolhido por outro programa) e só os campos conhecidos.
    /// </summary>
    internal static bool ImportLegacy(string legacyFile, string targetFile)
    {
        try
        {
            if (File.Exists(targetFile)) return false;
            var file = new FileInfo(legacyFile);
            if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            if (file.Directory is not { } dir || dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            if (file.Length > MaxLegacyBytes) return false;
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file.FullName), Json);
            if (settings is null) return false;
            new SettingsStore(targetFile).Save(settings);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }
}

internal static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
