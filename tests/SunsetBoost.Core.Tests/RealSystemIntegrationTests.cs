using System.Diagnostics;
using SunsetBoost.Core.Cleanup;
using SunsetBoost.Core.Memory;
using SunsetBoost.Core.Processes;

namespace SunsetBoost.Core.Tests;

/// <summary>
/// Testes no Windows de verdade, só com processos descartáveis que o próprio teste abre.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Requires", "Desktop")] // abre janelas de verdade: instável na máquina do CI, fica fora dele
public class RealSystemIntegrationTests
{
    private static ProcessInfo Find(int pid) => new ProcessScanner().Snapshot().Single(p => p.Pid == pid);

    [Fact]
    public void Scanner_le_o_proprio_processo_com_caminho_e_memoria()
    {
        var me = Find(Environment.ProcessId);
        Assert.NotNull(me.ImagePath);
        Assert.True(me.PrivateBytes > 0);
        Assert.True(me.CreationTime > 0);
        Assert.True(me.SessionId > 0);
    }

    [Fact]
    public void Ajustes_reais_conseguem_ler_o_estado_atual_sem_mudar_nada()
    {
        var m = new Tweaks.GameModeManager(Tweaks.TweakCatalog.CreateReal(), Path.Combine(Path.GetTempPath(), "nao-usado.json"));
        var fp = m.Fingerprint(new Tweaks.TweakContext(GameProfiles.Valorant));
        Assert.Matches("^[0-9a-f-]{36}$", fp["plano-energia"]["ativo"]);
        Assert.True(fp["servicos"].ContainsKey("WSearch"));
        Assert.Equal(fp["game-dvr"].ToString(), m.Fingerprint(new(GameProfiles.Valorant))["game-dvr"].ToString());
    }

    [Fact]
    public void Memoria_e_lida()
    {
        var m = MemoryInfo.Read();
        Assert.True(m.TotalBytes > 1UL << 30);
        Assert.InRange(m.AvailableBytes, 1UL, m.TotalBytes);
    }

    [Fact]
    public void Processo_sem_janela_nao_fecha_sozinho_e_e_forcado()
    {
        using var proc = Process.Start(new ProcessStartInfo("ping", "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            var info = Find(proc.Id);
            var sys = new RealSystemActions();

            Assert.Equal(0, sys.RequestClose(info)); // não tem janela pra receber o pedido
            Assert.True(sys.IsAlive(info));
            Assert.True(sys.Kill(info, out var err), err);
            Assert.True(proc.WaitForExit(3000));
            Assert.False(sys.IsAlive(info));
        }
        finally { if (!proc.HasExited) proc.Kill(); }
    }

    [Fact]
    public void Processo_com_janela_fecha_sozinho_com_o_pedido_educado()
    {
        // Abre uma janelinha minimizada (Windows Forms via PowerShell) e pede pra ela fechar
        var script = "Add-Type -A System.Windows.Forms; $f = New-Object System.Windows.Forms.Form; " +
                     "$f.Text = 'teste sunset boost'; $f.WindowState = 'Minimized'; [System.Windows.Forms.Application]::Run($f)";
        using var proc = Process.Start(new ProcessStartInfo("powershell", $"-NoProfile -Command \"{script}\"") { UseShellExecute = false })!;
        try
        {
            var sw = Stopwatch.StartNew();
            var sys = new RealSystemActions();
            var info = Find(proc.Id);
            int windows = 0;
            while (windows == 0 && sw.Elapsed < TimeSpan.FromSeconds(15))
            {
                Thread.Sleep(200);
                windows = sys.RequestClose(info);
            }
            Assert.True(windows > 0, "a janela de teste não apareceu");
            Assert.True(proc.WaitForExit(3000), "não fechou em 3 s depois do pedido");
        }
        finally { if (!proc.HasExited) proc.Kill(); }
    }

    [Fact]
    public void Kill_recusa_PID_reaproveitado()
    {
        using var proc = Process.Start(new ProcessStartInfo("ping", "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            var real = Find(proc.Id);
            var fake = real with { CreationTime = real.CreationTime - 1 }; // "outro" processo com o mesmo PID
            var sys = new RealSystemActions();
            Assert.False(sys.Kill(fake, out var err));
            Assert.Contains("reaproveitado", err);
            Assert.False(proc.HasExited);
        }
        finally { if (!proc.HasExited) proc.Kill(); }
    }
}
