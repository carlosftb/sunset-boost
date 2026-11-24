using System.Security.AccessControl;
using System.Security.Principal;

namespace SunsetBoost.Core.Infrastructure;

/// <summary>
/// Pasta de dados do app quando ele roda com admin (%ProgramData%\SunsetBoost): só Administradores e SYSTEM gravam,
/// Usuários só leem. Assim nenhum programa sem admin consegue trocar a pasta, o log ou o settings.json por um link
/// (junction/symlink) e fazer o Sunset Boost criar, sobrescrever ou apagar arquivos do sistema no lugar deles.
/// </summary>
public static class ProtectedDirectory
{
    internal static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    internal static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    internal static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    internal static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    /// <summary>O que o app usa dentro da pasta. Se algum desses virou link, a pasta inteira deixa de ser confiável.</summary>
    internal static readonly string[] KnownEntries =
    {
        "logs", "updates", "settings.json", "settings.json.tmp", "snapshot.json", "snapshot.json.tmp", "snapshot.json.corrompido",
    };

    /// <summary>
    /// Cria a pasta protegida, ou confere e reaplica as permissões se ela já existe (precisa de admin).
    /// Um usuário comum consegue criar pastas em %ProgramData% antes do app: uma pasta que não é confiável (dono que não
    /// é admin, ou link) é renomeada pro lado, sem seguir o link, e uma nova é criada. Devolve um aviso pro log, ou null.
    /// </summary>
    public static string? Ensure(string path)
    {
        path = Path.GetFullPath(path);
        string? note = null;
        if (Exists(path) && WhyUntrusted(path) is { } why)
            note = $"Pasta de dados {path} não era confiável ({why}): renomeada para {MoveAside(path)} e criada de novo";

        var security = BuildSecurity();
        var dir = new DirectoryInfo(path);
        if (dir.Exists) dir.SetAccessControl(security); // já é nossa: só reaplica as permissões
        else
        {
            security.SetOwner(Administrators);
            dir.Create(security); // nasce protegida: não passa nem um instante com as permissões herdadas do %ProgramData%
        }

        // Confere de novo: se outro programa criou a pasta entre o rename e o Create, o Create não fez nada
        if (WhyUntrusted(path) is { } again)
            throw new UnauthorizedAccessException(
                $"A pasta de dados {path} não é segura ({again}). Apague-a como administrador e abra o Sunset Boost de novo.");
        return note;
    }

    /// <summary>Motivo pra não confiar na pasta; null = confiável.</summary>
    internal static string? WhyUntrusted(string path)
    {
        // GetAttributes não segue o último link: uma junction ou symlink aparece como ReparsePoint
        var attrs = File.GetAttributes(path);
        if (attrs.HasFlag(FileAttributes.ReparsePoint)) return "é um link (junction ou symlink)";
        if (!attrs.HasFlag(FileAttributes.Directory)) return "é um arquivo, não uma pasta";

        foreach (var name in KnownEntries)
        {
            try
            {
                if (File.GetAttributes(Path.Combine(path, name)).HasFlag(FileAttributes.ReparsePoint)) return $"\"{name}\" é um link";
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
            catch (UnauthorizedAccessException) { return $"não deu pra ler \"{name}\""; }
        }

        SecurityIdentifier? owner;
        try { owner = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier; }
        catch (UnauthorizedAccessException) { return "não deu pra ler o dono"; }
        return IsTrustedOwner(owner) ? null : $"o dono é {Describe(owner)}, não Administradores";
    }

    internal static bool IsTrustedOwner(SecurityIdentifier? sid) =>
        sid is not null && (sid.Equals(Administrators) || sid.Equals(LocalSystem) || sid.Equals(TrustedInstaller));

    /// <summary>
    /// Permissões da pasta: protegidas (nada herdado do %ProgramData%, onde Usuários podem criar arquivos e pastas),
    /// controle total pra Administradores e SYSTEM, só leitura pra Usuários (dá pra abrir o log sem admin).
    /// </summary>
    internal static DirectorySecurity BuildSecurity()
    {
        var s = new DirectorySecurity();
        s.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        s.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        s.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        s.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, all, PropagationFlags.None, AccessControlType.Allow));
        return s;
    }

    /// <summary>
    /// Renomeia pro lado com um sufixo imprevisível. O rename não segue links: muda só o nome do link, o destino fica intocado.
    /// </summary>
    internal static string MoveAside(string path)
    {
        var aside = $"{path}.nao-confiavel-{Guid.NewGuid():N}";
        if (File.GetAttributes(path).HasFlag(FileAttributes.Directory)) Directory.Move(path, aside);
        else File.Move(path, aside);
        return aside;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string Describe(SecurityIdentifier? sid)
    {
        if (sid is null) return "desconhecido";
        try { return sid.Translate(typeof(NTAccount)).Value; }
        catch { return sid.Value; }
    }
}
