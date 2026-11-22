using System.Diagnostics;
using SunsetBoost.Core.Edge;

namespace SunsetBoost.Core.Tests;

/// <summary>
/// Chrome de verdade, mas isolado: perfil temporário próprio (outra instância, não mexe no Chrome do usuário)
/// e o modo leve só pode tocar nas janelas desses processos. Pula se o Chrome não estiver instalado.
/// </summary>
[Trait("Category", "Integration")]
public class ChromeIntegrationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string ChromeExe = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

    [Theory]
    [InlineData(false)] // aperta o "x" (Edge e Chrome)
    [InlineData(true)]  // clique do meio enviado pra janela (o jeito do Opera GX)
    public void Modo_leve_deixa_o_Chrome_com_uma_aba_so(bool middleClick)
    {
        if (!File.Exists(ChromeExe)) { output.WriteLine("PULADO: Chrome ausente"); return; }

        var profile = Path.Combine(Path.GetTempPath(), "sunsetboost-chrome-" + Guid.NewGuid().ToString("N"));
        var pages = Enumerable.Range(1, 4).Select(i => $"data:text/html,<title>aba{i}</title>aba {i}");
        var args = $"--user-data-dir=\"{profile}\" --no-first-run --no-default-browser-check --disable-sync --new-window " +
                   string.Join(" ", pages.Select(p => $"\"{p}\""));
        using var browser = Process.Start(new ProcessStartInfo(ChromeExe, args) { UseShellExecute = false })!;
        try
        {
            HashSet<int> Mine() => Process.GetProcessesByName("chrome")
                .Select(p => { using (p) return p.Id; })
                .Where(pid => ProcessCommandLine.Read(pid)?.Contains(profile, StringComparison.OrdinalIgnoreCase) == true)
                .ToHashSet();

            int before = 0;
            for (int i = 0; i < 40 && before < 4; i++) { Thread.Sleep(500); before = EdgeTabs.CountTabs(Browsers.Chrome, Mine()); }
            Assert.Equal(4, before);

            var r = EdgeTabs.KeepFirstTab(Browsers.Chrome, Mine(), forceMiddleClick: middleClick);
            Assert.Null(r.Error);
            Assert.Equal(3, r.TabsClosed);

            Thread.Sleep(1000);
            var after = EdgeTabs.CountTabs(Browsers.Chrome, Mine());
            output.WriteLine($"RODOU (meio={middleClick}): antes={before} fechadas={r.TabsClosed} depois={after}");
            Assert.Equal(1, after);
        }
        finally
        {
            foreach (var p in Process.GetProcessesByName("chrome"))
                using (p)
                    if (ProcessCommandLine.Read(p.Id)?.Contains(profile, StringComparison.OrdinalIgnoreCase) == true)
                        try { p.Kill(); } catch { }
            Thread.Sleep(1500);
            try { Directory.Delete(profile, true); } catch { }
        }
    }

    [Fact]
    public void Pagina_inicial_fica_e_a_primeira_aba_de_verdade_e_mantida()
    {
        if (!File.Exists(ChromeExe)) { output.WriteLine("PULADO: Chrome ausente"); return; }

        var profile = Path.Combine(Path.GetTempPath(), "sunsetboost-chrome-" + Guid.NewGuid().ToString("N"));
        // Igual ao Opera do usuário: GX Corner na frente e 3 abas dele
        var titles = new[] { "GX Corner", "minha1", "minha2", "minha3" };
        var args = $"--user-data-dir=\"{profile}\" --no-first-run --no-default-browser-check --disable-sync --new-window " +
                   string.Join(" ", titles.Select(t => $"\"data:text/html,<title>{t}</title>{t}\""));
        using var browser = Process.Start(new ProcessStartInfo(ChromeExe, args) { UseShellExecute = false })!;
        try
        {
            HashSet<int> Mine() => Process.GetProcessesByName("chrome")
                .Select(p => { using (p) return p.Id; })
                .Where(pid => ProcessCommandLine.Read(pid)?.Contains(profile, StringComparison.OrdinalIgnoreCase) == true)
                .ToHashSet();

            int before = 0;
            for (int i = 0; i < 40 && before < 4; i++) { Thread.Sleep(500); before = EdgeTabs.CountTabs(Browsers.Chrome, Mine()); }
            Assert.Equal(4, before);

            var r = EdgeTabs.KeepFirstTab(Browsers.Chrome, Mine(), forceMiddleClick: true);
            Thread.Sleep(800);
            var after = EdgeTabs.CountTabs(Browsers.Chrome, Mine());
            output.WriteLine($"RODOU (página inicial): antes={before} fechadas={r.TabsClosed} depois={after}");
            Assert.Equal(2, r.TabsClosed);   // minha2 e minha3
            Assert.Equal(2, after);          // GX Corner + minha1
        }
        finally
        {
            foreach (var p in Process.GetProcessesByName("chrome"))
                using (p)
                    if (ProcessCommandLine.Read(p.Id)?.Contains(profile, StringComparison.OrdinalIgnoreCase) == true)
                        try { p.Kill(); } catch { }
            Thread.Sleep(1500);
            try { Directory.Delete(profile, true); } catch { }
        }
    }
}
