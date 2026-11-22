namespace SunsetBoost.Core.Infrastructure;

/// <summary>
/// Onde o app guarda configurações, logs e a cópia de segurança dos ajustes.
/// Com admin: %ProgramData%\SunsetBoost, que só administradores alteram (<see cref="ProtectedDirectory"/>).
/// Sem admin (desenvolvimento, CLI): %LOCALAPPDATA%\SunsetBoost.
/// O processo com admin nunca usa a pasta do usuário: qualquer programa sem admin consegue trocar o que tem lá por links
/// e fazer o app gravar ou apagar arquivos do sistema.
/// </summary>
public sealed class AppPaths
{
    private readonly List<string> _notes = new();

    public string Root { get; }

    /// <summary>Pasta protegida (só admin grava). Falso quando o app roda sem admin ou com pasta escolhida (testes).</summary>
    public bool IsProtected { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string LogsDir => Path.Combine(Root, "logs");
    public string SnapshotFile => Path.Combine(Root, "snapshot.json");

    /// <summary>Avisos de quando a pasta foi preparada. O log só abre depois: quem chama manda pra ele.</summary>
    public IReadOnlyList<string> Notes => _notes;

    public AppPaths(string? root = null)
    {
        IsProtected = root is null && Elevation.IsElevated;
        Root = root ?? DefaultRoot(IsProtected);
    }

    /// <summary>Pasta padrão: %ProgramData%\SunsetBoost com admin, %LOCALAPPDATA%\SunsetBoost sem.</summary>
    public static string DefaultRoot(bool elevated) => Path.Combine(
        Environment.GetFolderPath(elevated ? Environment.SpecialFolder.CommonApplicationData : Environment.SpecialFolder.LocalApplicationData),
        "SunsetBoost");

    /// <summary>Onde a versão 0.1 guardava tudo, inclusive com admin.</summary>
    public static string LegacyRoot => DefaultRoot(elevated: false);

    public AppPaths EnsureCreated()
    {
        if (!IsProtected)
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(LogsDir);
            return this;
        }

        if (ProtectedDirectory.Ensure(Root) is { } note) _notes.Add(note);
        Directory.CreateDirectory(LogsDir); // herda as permissões protegidas da pasta

        var legacySettings = Path.Combine(LegacyRoot, "settings.json");
        if (SettingsStore.ImportLegacy(legacySettings, SettingsFile))
            _notes.Add($"Configurações da versão anterior copiadas de {legacySettings}");
        // A cópia do Modo Jogo da versão anterior não é lida com admin (o instalador pede pra restaurar antes de atualizar)
        if (File.Exists(Path.Combine(LegacyRoot, "snapshot.json")) && !File.Exists(SnapshotFile))
            _notes.Add($"Há uma cópia do Modo Jogo da versão anterior em {LegacyRoot} que não é usada com admin");
        return this;
    }
}
