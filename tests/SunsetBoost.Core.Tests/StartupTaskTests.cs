using System.Xml.Linq;
using SunsetBoost.Core.Automation;

namespace SunsetBoost.Core.Tests;

public class StartupTaskTests
{
    [Fact]
    public void So_aceita_exe_dentro_de_Program_Files()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.True(StartupTask.IsSafeLocation(Path.Combine(pf, "SunsetBoost", "SunsetBoost.exe")));
        Assert.False(StartupTask.IsSafeLocation(@"C:\sunset-boost\publish\SunsetBoost.exe"));
        Assert.False(StartupTask.IsSafeLocation(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SunsetBoost.exe")));
        Assert.False(StartupTask.IsSafeLocation(pf + @"\..\Users\x\SunsetBoost.exe")); // truque com ".."
    }

    [Fact]
    public void Tarefa_so_abre_o_launcher_instalado()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        // O app .NET direto, com admin pelo Agendador, receberia as variáveis de ambiente do usuário
        Assert.Contains("launcher", StartupTask.RejectReason(Path.Combine(pf, "SunsetBoost", "SunsetBoost.App.exe")));
        Assert.Contains("Program Files", StartupTask.RejectReason(@"C:\sunset-boost\publish\SunsetBoost.exe"));
    }

    [Fact]
    public void Launcher_fica_na_mesma_pasta_do_app()
    {
        Assert.Equal(@"C:\Program Files\SunsetBoost\SunsetBoost.exe",
            StartupTask.LauncherPathFor(@"C:\Program Files\SunsetBoost\SunsetBoost.App.exe"));
        Assert.Null(StartupTask.LauncherPathFor(null));
        Assert.Null(StartupTask.LauncherPathFor(""));
    }

    [Fact]
    public void Xml_da_tarefa_e_valido_e_nao_tem_limite_de_tempo()
    {
        var xml = XDocument.Parse(StartupTask.BuildXml(@"C:\Program Files\SunsetBoost\SunsetBoost.exe", @"PC\usuário & cia"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal("PT0S", xml.Descendants(ns + "ExecutionTimeLimit").Single().Value); // padrão do Windows mataria o app em 72 h
        Assert.Equal("HighestAvailable", xml.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("--minimizado", xml.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal(@"PC\usuário & cia", xml.Descendants(ns + "UserId").First().Value);
    }
}
