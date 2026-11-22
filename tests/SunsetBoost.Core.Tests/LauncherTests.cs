using Microsoft.Win32;
using SunsetBoost.Launcher;

namespace SunsetBoost.Core.Tests;

public class LauncherTests
{
    [Fact]
    public void So_repassa_os_argumentos_permitidos()
    {
        var args = new[] { "--minimizado", "--captura", "home", @"C:\Windows\System32\drivers\x.sys", "--SIMULAR", "--minimizado" };
        Assert.Equal(new[] { "--minimizado", "--simular" }, CleanLaunch.FilterArgs(args));
    }

    [Theory]
    [InlineData("DOTNET_ROOT", true)] [InlineData("dotnet_root_x64", true)] [InlineData("DOTNET_STARTUP_HOOKS", true)]
    [InlineData("DOTNET_ADDITIONAL_DEPS", true)] [InlineData("CORECLR_ENABLE_PROFILING", true)] [InlineData("CORECLR_PROFILER_PATH_64", true)]
    [InlineData("COR_ENABLE_PROFILING", true)] [InlineData("COMPlus_EnableDiagnostics", true)]
    [InlineData("PATH", false)] [InlineData("TEMP", false)] [InlineData("SystemRoot", false)] [InlineData("CORE_COUNT", false)]
    public void Bloqueia_variaveis_que_o_dotnet_usa_pra_carregar_codigo(string name, bool blocked) =>
        Assert.Equal(blocked, CleanLaunch.IsBlocked(name));

    [Fact]
    public void Bloco_de_ambiente_em_ordem_e_terminado_por_dois_nulos() =>
        Assert.Equal("A=1\0b=2\0\0", CleanLaunch.BuildBlock(new Dictionary<string, string> { ["b"] = "2", ["A"] = "1" }));
}

/// <summary>Com o Windows de verdade: lê o ambiente do sistema e abre um cmd.exe escondido.</summary>
[Trait("Category", "Integration")]
public class CleanLaunchIntegrationTests
{
    /// <summary>Nomes que o próprio Windows preenche no ambiente (não são lidos do HKCU\Environment).</summary>
    private static readonly HashSet<string> Computed = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALLUSERSPROFILE", "APPDATA", "LOCALAPPDATA", "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432",
        "COMPUTERNAME", "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "PUBLIC", "SystemDrive",
        "SystemRoot", "USERDOMAIN", "USERNAME", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "LOGONSERVER",
    };

    [Fact]
    public void Ambiente_so_tem_variaveis_do_sistema()
    {
        var env = CleanLaunch.SystemEnvironment();
        Assert.True(env.ContainsKey("SystemRoot") || env.ContainsKey("windir"));
        Assert.DoesNotContain(env.Keys, CleanLaunch.IsBlocked);

        // TEMP é o do sistema (C:\Windows\Temp), não o do usuário (que fica dentro do perfil dele)
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (env.TryGetValue("TEMP", out var temp)) Assert.DoesNotContain(profile, temp, StringComparison.OrdinalIgnoreCase);

        // Variável que só existe em HKCU\Environment (onde qualquer programa do usuário grava) não aparece
        using var user = Registry.CurrentUser.OpenSubKey("Environment");
        using var machine = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
        var machineNames = new HashSet<string>(machine?.GetValueNames() ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var name in user?.GetValueNames() ?? Array.Empty<string>())
            if (!machineNames.Contains(name) && !Computed.Contains(name))
                Assert.False(env.ContainsKey(name), $"{name} veio do HKCU\\Environment");
    }

    [Fact]
    public void Ambiente_limpo_tem_o_perfil_e_o_nome_do_usuario_de_verdade()
    {
        // Sem isso o sistema traria USERPROFILE=C:\Users\Default e o app resolveria %LOCALAPPDATA% no perfil errado
        var env = CleanLaunch.CleanEnvironment();
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), env["USERPROFILE"], ignoreCase: true);
        Assert.Equal(Environment.UserName, env["USERNAME"], ignoreCase: true);
        Assert.DoesNotContain(env.Keys, CleanLaunch.IsBlocked);
    }

    [Fact]
    public void Programa_aberto_nao_herda_o_ambiente_de_quem_abriu()
    {
        const string name = "SUNSETBOOST_TESTE_HERANCA";
        var output = Path.Combine(Path.GetTempPath(), $"sb-env-{Guid.NewGuid():N}.txt");
        Environment.SetEnvironmentVariable(name, "veio-do-pai");
        try
        {
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            int code = CleanLaunch.Start(cmd, $"/d /c set > \"{output}\"", CleanLaunch.CleanEnvironment(), Path.GetTempPath(),
                waitForExit: true, noWindow: true);
            Assert.Equal(0, code);
            var text = File.ReadAllText(output);
            Assert.DoesNotContain(name, text);
            Assert.Contains("SystemRoot=", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("USERPROFILE=" + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
            File.Delete(output);
        }
    }
}
