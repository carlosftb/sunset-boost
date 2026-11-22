using System.Security.AccessControl;
using System.Security.Principal;
using SunsetBoost.Core.Infrastructure;

namespace SunsetBoost.Core.Tests;

public class ProtectedDirectoryTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData("S-1-5-32-544", true)]  // Administradores
    [InlineData("S-1-5-18", true)]      // SYSTEM
    [InlineData("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", true)] // TrustedInstaller
    [InlineData("S-1-5-32-545", false)] // Usuários
    [InlineData("S-1-5-11", false)]     // Usuários autenticados
    [InlineData("S-1-1-0", false)]      // Todos
    public void So_admin_SYSTEM_e_TrustedInstaller_podem_ser_dono(string sid, bool trusted) =>
        Assert.Equal(trusted, ProtectedDirectory.IsTrustedOwner(new SecurityIdentifier(sid)));

    [Fact]
    public void Usuario_atual_nao_e_dono_confiavel()
    {
        using var me = WindowsIdentity.GetCurrent();
        Assert.False(ProtectedDirectory.IsTrustedOwner(me.User));
        Assert.False(ProtectedDirectory.IsTrustedOwner(null));
    }

    [Fact]
    public void Permissoes_protegidas_so_admin_e_SYSTEM_gravam()
    {
        var s = ProtectedDirectory.BuildSecurity();
        Assert.True(s.AreAccessRulesProtected); // nada herdado do %ProgramData%, onde Usuários podem criar arquivos

        const FileSystemRights write = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
            | FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        var rules = s.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.All(rules, r => Assert.Equal(AccessControlType.Allow, r.AccessControlType));

        var writers = rules.Where(r => (r.FileSystemRights & write) != 0).Select(r => (SecurityIdentifier)r.IdentityReference).ToHashSet();
        Assert.True(writers.SetEquals(new[] { ProtectedDirectory.Administrators, ProtectedDirectory.LocalSystem }));
        Assert.Contains(rules, r => r.IdentityReference.Equals(ProtectedDirectory.Users)
                                    && (r.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute);
    }

    [Fact]
    public void Pasta_criada_sem_admin_nao_e_confiavel()
    {
        if (Elevation.IsElevated) { output.WriteLine("PULADO: com admin, a pasta de teste já nasce com Administradores como dono"); return; }
        var dir = Fs.NewDir();
        try { Assert.Contains("dono", ProtectedDirectory.WhyUntrusted(dir)); }
        finally { Fs.DeleteDir(dir); }
    }

    [Fact]
    public void Arquivo_no_lugar_da_pasta_nao_e_confiavel()
    {
        var file = Fs.NewPath();
        File.WriteAllText(file, "x");
        try { Assert.Contains("arquivo", ProtectedDirectory.WhyUntrusted(file)); }
        finally { File.Delete(file); }
    }
}

/// <summary>Com links de verdade (junction), em pastas temporárias.</summary>
[Trait("Category", "Integration")]
public class ProtectedDirectoryLinkTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Junction_nao_e_confiavel()
    {
        var target = Fs.NewDir();
        var link = Fs.NewPath();
        try
        {
            Fs.Junction(link, target);
            Assert.Contains("link", ProtectedDirectory.WhyUntrusted(link));
        }
        finally
        {
            Fs.RemoveLink(link);
            Fs.DeleteDir(target);
        }
    }

    [Fact]
    public void Log_trocado_por_link_tambem_nao_e_confiavel()
    {
        var dir = Fs.NewDir();
        var target = Fs.NewDir();
        var logs = Path.Combine(dir, "logs");
        try
        {
            Fs.Junction(logs, target);
            Assert.Contains("\"logs\" é um link", ProtectedDirectory.WhyUntrusted(dir));
        }
        finally
        {
            Fs.RemoveLink(logs);
            Fs.DeleteDir(dir);
            Fs.DeleteDir(target);
        }
    }

    [Fact]
    public void Renomear_pro_lado_nao_segue_o_link()
    {
        var target = Fs.NewDir();
        var link = Fs.NewPath();
        File.WriteAllText(Path.Combine(target, "importante.txt"), "não pode sumir");
        Fs.Junction(link, target);
        string? aside = null;
        try
        {
            aside = ProtectedDirectory.MoveAside(link);
            Assert.False(Directory.Exists(link));
            Assert.True(File.GetAttributes(aside).HasFlag(FileAttributes.ReparsePoint)); // quem mudou de nome foi o link
            Assert.True(File.Exists(Path.Combine(target, "importante.txt")));
        }
        finally
        {
            Fs.RemoveLink(aside ?? link);
            Fs.DeleteDir(target);
        }
    }

    [Fact]
    public void Com_admin_troca_pasta_plantada_por_uma_protegida()
    {
        if (!Elevation.IsElevated)
        {
            output.WriteLine("PULADO: precisa de admin (rode o dotnet test num PowerShell como administrador)");
            return;
        }
        var path = Fs.NewPath();
        var planted = Fs.NewDir();
        try
        {
            Fs.Junction(path, planted); // a "pasta" plantada é um link pra outra pasta
            var note = ProtectedDirectory.Ensure(path);
            Assert.Contains("não era confiável", note);
            Assert.Null(ProtectedDirectory.WhyUntrusted(path));
            var security = new DirectoryInfo(path).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            Assert.Equal<IdentityReference>(ProtectedDirectory.Administrators, security.GetOwner(typeof(SecurityIdentifier)));
            Assert.Null(ProtectedDirectory.Ensure(path)); // já está certa: nada a avisar
            output.WriteLine($"RODOU COM ADMIN: {note}. Pasta nova com dono Administradores e permissões protegidas.");
        }
        finally
        {
            foreach (var aside in Directory.GetDirectories(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".nao-confiavel-*"))
                Fs.RemoveLink(aside);
            Fs.DeleteDir(path);
            Fs.DeleteDir(planted);
        }
    }
}

public class AppPathsTests
{
    [Fact]
    public void Com_admin_usa_ProgramData_e_sem_admin_a_pasta_do_usuario()
    {
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SunsetBoost"),
            AppPaths.DefaultRoot(elevated: true));
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SunsetBoost"),
            AppPaths.DefaultRoot(elevated: false));
        Assert.Equal(AppPaths.DefaultRoot(elevated: false), AppPaths.LegacyRoot);
    }

    [Fact]
    public void Pasta_escolhida_nunca_e_tratada_como_protegida()
    {
        var paths = new AppPaths(@"C:\qualquer\pasta");
        Assert.Equal(@"C:\qualquer\pasta", paths.Root);
        Assert.False(paths.IsProtected);
    }

    [Fact]
    public void Pasta_padrao_segue_a_elevacao_do_processo()
    {
        var paths = new AppPaths();
        Assert.Equal(Elevation.IsElevated, paths.IsProtected);
        Assert.Equal(AppPaths.DefaultRoot(Elevation.IsElevated), paths.Root);
    }
}

public class LegacySettingsTests
{
    [Fact]
    public void Copia_as_configuracoes_antigas_uma_vez_so()
    {
        var oldDir = Fs.NewDir();
        var newDir = Fs.NewDir();
        try
        {
            var legacy = Path.Combine(oldDir, "settings.json");
            var target = Path.Combine(newDir, "settings.json");
            var old = new AppSettings { ActiveProfileId = "lol" };
            old.AppDecisions["discord"] = AppDecision.Close;
            new SettingsStore(legacy).Save(old);

            Assert.True(SettingsStore.ImportLegacy(legacy, target));
            var imported = new SettingsStore(target).Load();
            Assert.Equal("lol", imported.ActiveProfileId);
            Assert.Equal(AppDecision.Close, imported.AppDecisions["DISCORD"]);

            // A pasta nova já tem configuração: nunca sobrescreve
            new SettingsStore(legacy).Save(new AppSettings { ActiveProfileId = "steam" });
            Assert.False(SettingsStore.ImportLegacy(legacy, target));
            Assert.Equal("lol", new SettingsStore(target).Load().ActiveProfileId);
        }
        finally
        {
            Fs.DeleteDir(oldDir);
            Fs.DeleteDir(newDir);
        }
    }

    [Fact]
    public void Ignora_arquivo_antigo_ausente_invalido_ou_grande()
    {
        var oldDir = Fs.NewDir();
        var newDir = Fs.NewDir();
        try
        {
            var legacy = Path.Combine(oldDir, "settings.json");
            var target = Path.Combine(newDir, "settings.json");
            Assert.False(SettingsStore.ImportLegacy(legacy, target));
            File.WriteAllText(legacy, "{ isso não é json");
            Assert.False(SettingsStore.ImportLegacy(legacy, target));
            File.WriteAllText(legacy, "{\"ActiveProfileId\":\"" + new string('x', SettingsStore.MaxLegacyBytes) + "\"}");
            Assert.False(SettingsStore.ImportLegacy(legacy, target));
            Assert.False(File.Exists(target));
        }
        finally
        {
            Fs.DeleteDir(oldDir);
            Fs.DeleteDir(newDir);
        }
    }
}

[Trait("Category", "Integration")]
public class LegacySettingsLinkTests
{
    [Fact]
    public void Nao_le_pasta_antiga_que_virou_link()
    {
        var real = Fs.NewDir();
        var link = Fs.NewPath();
        var newDir = Fs.NewDir();
        try
        {
            new SettingsStore(Path.Combine(real, "settings.json")).Save(new AppSettings { ActiveProfileId = "lol" });
            Fs.Junction(link, real);
            var target = Path.Combine(newDir, "settings.json");
            Assert.False(SettingsStore.ImportLegacy(Path.Combine(link, "settings.json"), target));
            Assert.False(File.Exists(target));
        }
        finally
        {
            Fs.RemoveLink(link);
            Fs.DeleteDir(real);
            Fs.DeleteDir(newDir);
        }
    }
}
