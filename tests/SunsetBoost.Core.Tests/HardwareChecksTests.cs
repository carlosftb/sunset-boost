using SunsetBoost.Core.Diagnostics;
using SunsetBoost.Core.Tweaks;

namespace SunsetBoost.Core.Tests;

internal sealed class FakeHardware : IHardwareInfo
{
    public List<DisplayInfo> DisplayList { get; set; } = new();
    public List<MemoryModule> Modules { get; set; } = new();
    public List<GpuInfo> GpuList { get; set; } = new();
    public bool? Battery { get; set; } = false;
    public bool Unplugged { get; set; }
    public (long, long)? Drive { get; set; } = (200L << 30, 500L << 30);
    public bool? Hvci { get; set; }
    public bool? MouseAccel { get; set; }
    public bool? Windowed { get; set; } = true;

    public IReadOnlyList<DisplayInfo> Displays() => DisplayList;
    public IReadOnlyList<MemoryModule> Memory() => Modules;
    public IReadOnlyList<GpuInfo> Gpus() => GpuList;
    public bool? HasBattery() => Battery;
    public bool OnBattery() => Unplugged;
    public (long Free, long Total)? SystemDrive() => Drive;
    public bool? MemoryIntegrity() => Hvci;
    public bool? MouseAcceleration() => MouseAccel;
    public bool? WindowedGameOptimizations() => Windowed;
}

public class HardwareChecksTests
{
    private static readonly DateTime Today = new(2026, 11, 23);

    private static List<DiagItem> Run(FakeHardware hw, bool? gpuOn = null) => HardwareChecks.Run(hw, Today, gpuOn);
    private static List<DiagItem> Recs(List<DiagItem> items) => items.Where(i => i.Status == DiagStatus.Recommend).ToList();

    private static MemoryModule Ddr4(int configured, string part = "", int rated = 0) => new("DDR4", rated == 0 ? configured : rated, configured, part, 8192);

    [Fact]
    public void PC_bem_configurado_nao_recebe_recomendacao()
    {
        var hw = new FakeHardware
        {
            DisplayList = { new("Tela principal", 1920, 1080, 144, 144) },
            Modules = { Ddr4(3200, "CMK16GX4M2B3200C16"), Ddr4(3200, "CMK16GX4M2B3200C16") },
            GpuList = { new("NVIDIA GeForce RTX 3060", Today.AddDays(-30)) },
        };
        Assert.Empty(Recs(Run(hw)));
    }

    [Fact]
    public void Monitor_abaixo_do_maximo_e_recomendado()
    {
        var hw = new FakeHardware { DisplayList = { new("Tela principal", 1920, 1080, 60, 165) } };
        var r = Assert.Single(Recs(Run(hw)));
        Assert.Contains("165", r.Title);
    }

    [Fact]
    public void Tela_secundaria_abaixo_do_maximo_so_informa()
    {
        var hw = new FakeHardware { DisplayList = { new("Tela 2", 900, 1600, 59, 75, Primary: false) } };
        var items = Run(hw);
        Assert.Empty(Recs(items));
        Assert.Contains(items, i => i.Status == DiagStatus.Info && i.Title.Contains("75"));
    }

    [Fact]
    public void Monitor_59_contra_60_Hz_nao_e_problema()
    {
        var hw = new FakeHardware { DisplayList = { new("Tela principal", 1920, 1080, 59, 60) } };
        Assert.Empty(Recs(Run(hw)));
    }

    [Fact]
    public void XMP_desligado_aparece_pelo_codigo_do_pente()
    {
        var hw = new FakeHardware { Modules = { Ddr4(2133, "F4-3600C16-8GVKC"), Ddr4(2133, "F4-3600C16-8GVKC") } };
        var r = Assert.Single(Recs(Run(hw)));
        Assert.Contains("2133", r.Title);
        Assert.Contains("3600", r.Title);
    }

    [Fact]
    public void DDR4_lenta_em_desktop_sem_codigo_conhecido_pede_pra_conferir()
    {
        var hw = new FakeHardware { Modules = { Ddr4(2400, "M378A1K43CB2"), Ddr4(2400, "M378A1K43CB2") } };
        Assert.Contains(Recs(Run(hw)), r => r.Title.Contains("XMP"));
    }

    [Fact]
    public void Pentes_diferentes_usam_a_velocidade_do_mais_lento()
    {
        // Caso real: um genérico de 2666 e um HyperX de 2133 rodando a 2133 → não é XMP desligado
        var hw = new FakeHardware { Modules = { Ddr4(2133, "DDR4 2666"), Ddr4(2133, "KHX2133C14/8G") } };
        var items = Run(hw);
        Assert.DoesNotContain(Recs(items), r => r.Title.Contains("XMP") || r.Title.Contains("mas o pente"));
        var info = Assert.Single(items, i => i.Title.Contains("diferentes"));
        Assert.Equal(DiagStatus.Info, info.Status);
        Assert.Contains("2133 MT/s mesmo com um de 2666", info.Detail);
    }

    [Fact]
    public void Notebook_com_memoria_padrao_nao_recebe_alerta_de_XMP()
    {
        var hw = new FakeHardware { Battery = true, Modules = { Ddr4(2666), Ddr4(2666) } };
        Assert.DoesNotContain(Recs(Run(hw)), r => r.Title.Contains("XMP"));
    }

    [Fact]
    public void Um_pente_so_recomenda_dual_channel_mas_nao_em_memoria_soldada()
    {
        Assert.Contains(Recs(Run(new FakeHardware { Modules = { Ddr4(3200) } })), r => r.Title.Contains("canal simples"));
        var soldered = new FakeHardware { Battery = true, Modules = { new("LPDDR5", 6400, 6400, "", 16384) } };
        Assert.DoesNotContain(Recs(Run(soldered)), r => r.Title.Contains("canal simples"));
    }

    [Theory]
    [InlineData("CMK16GX4M2B3200C16", 3200)]
    [InlineData("F4-3600C16-8GVKC", 3600)]
    [InlineData("KF432C16BB/8", 3200)]
    [InlineData("KF560C40BBK2-32", 6000)]
    [InlineData("M378A1K43CB2-CTD", 0)]
    [InlineData("", 0)]
    public void Velocidade_pelo_codigo_do_pente(string part, int speed) =>
        Assert.Equal(speed, HardwareChecks.SpeedFromPartNumber(part));

    [Fact]
    public void Driver_velho_da_placa_dedicada_e_recomendado()
    {
        var hw = new FakeHardware { GpuList = { new("NVIDIA GeForce GTX 1660", Today.AddDays(-400)) } };
        Assert.Contains(Recs(Run(hw)), r => r.Title.Contains("Driver"));
    }

    [Fact]
    public void Duas_placas_recomenda_a_dedicada_ate_ligar()
    {
        var hw = new FakeHardware
        {
            GpuList = { new("Intel(R) UHD Graphics", Today), new("NVIDIA GeForce RTX 3050 Laptop GPU", Today) },
        };
        Assert.Contains(Recs(Run(hw, gpuOn: false)), r => r.Title.Contains("duas placas"));
        Assert.DoesNotContain(Recs(Run(hw, gpuOn: true)), r => r.Title.Contains("duas placas"));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4070", true)]
    [InlineData("AMD Radeon RX 6600", true)]
    [InlineData("AMD Radeon(TM) Graphics", false)]
    [InlineData("Intel(R) UHD Graphics 620", false)]
    [InlineData("Intel(R) Arc(TM) A750 Graphics", true)]
    [InlineData("Intel(R) Arc(TM) Graphics", false)]
    public void Reconhece_placa_dedicada(string name, bool dedicated) => Assert.Equal(dedicated, new GpuInfo(name, null).IsDedicated);

    [Fact]
    public void Notebook_na_bateria_e_disco_cheio_sao_recomendados()
    {
        var hw = new FakeHardware { Battery = true, Unplugged = true, Drive = (5L << 30, 256L << 30) };
        var recs = Recs(Run(hw));
        Assert.Contains(recs, r => r.Title.Contains("bateria"));
        Assert.Contains(recs, r => r.Title.Contains("Disco"));
    }

    [Fact]
    public void Integridade_da_Memoria_so_informa_nunca_recomenda_desligar()
    {
        var item = Assert.Single(Run(new FakeHardware { Hvci = true }), i => i.Title.Contains("Integridade"));
        Assert.Equal(DiagStatus.Info, item.Status);
    }

    [Fact]
    public void Aceleracao_do_mouse_e_jogos_em_janela()
    {
        var recs = Recs(Run(new FakeHardware { MouseAccel = true, Windowed = null }));
        Assert.Contains(recs, r => r.Title.Contains("mouse"));
        Assert.Contains(recs, r => r.Title.Contains("janela"));
    }

    [Fact]
    public void Limite_de_FPS_usa_o_Hz_da_tela_principal()
    {
        var tips = HardwareChecks.GameTips(
            new[] { new DisplayInfo("Tela 2", 900, 1600, 60, 75, Primary: false), new DisplayInfo("Tela principal", 1920, 1080, 144, 144) },
            new[] { new GpuInfo("NVIDIA GeForce GTX 1660", null) });
        Assert.Contains(tips, t => t.Title == "Limite de FPS: 141 (sua tela é de 144 Hz)" && t.Detail.Contains("G-SYNC"));
        Assert.Contains(tips, t => t.Title.Contains("NVIDIA") && t.Detail.Contains("cache do sombreador"));
    }

    [Fact]
    public void Dica_do_driver_segue_a_marca_da_placa()
    {
        var amd = HardwareChecks.GameTips(Array.Empty<DisplayInfo>(), new[] { new GpuInfo("AMD Radeon RX 7600", null) });
        Assert.Single(amd, t => t.Title.Contains("AMD Software"));
        Assert.DoesNotContain(amd, t => t.Title.Contains("NVIDIA"));
        Assert.Empty(HardwareChecks.GameTips(Array.Empty<DisplayInfo>(), new[] { new GpuInfo("Intel(R) UHD Graphics", null) }));
    }

    [Fact]
    public void Plano_de_energia_tem_regra_de_notebook()
    {
        Assert.Equal(DiagStatus.Ok, HardwareChecks.PowerPlan(HardwareChecks.BalancedPlan, laptop: true).Status);
        Assert.Equal(DiagStatus.Recommend, HardwareChecks.PowerPlan(HardwareChecks.BalancedPlan, laptop: false).Status);
        Assert.Equal(DiagStatus.Ok, HardwareChecks.PowerPlan(HardwareChecks.HighPerformancePlan, laptop: false).Status);
        Assert.Equal(DiagStatus.Recommend, HardwareChecks.PowerPlan(Guid.NewGuid(), laptop: true).Status);
    }

    [Fact]
    public void Erro_lendo_uma_parte_nao_derruba_o_resto()
    {
        var hw = new ThrowingDisplays { DisplayList = { }, Modules = { Ddr4(3200) } };
        Assert.Contains(Recs(HardwareChecks.Run(hw, Today)), r => r.Title.Contains("canal simples"));
    }

    private sealed class ThrowingDisplays : IHardwareInfo
    {
        public List<DisplayInfo> DisplayList { get; } = new();
        public List<MemoryModule> Modules { get; } = new();
        public IReadOnlyList<DisplayInfo> Displays() => throw new InvalidOperationException("falhou");
        public IReadOnlyList<MemoryModule> Memory() => Modules;
        public IReadOnlyList<GpuInfo> Gpus() => throw new UnauthorizedAccessException();
        public bool? HasBattery() => false;
        public bool OnBattery() => false;
        public (long Free, long Total)? SystemDrive() => null;
        public bool? MemoryIntegrity() => null;
        public bool? MouseAcceleration() => null;
        public bool? WindowedGameOptimizations() => true;
    }
}

public class SmbiosTests
{
    /// <summary>Monta um pente (tipo 17, versão 3.x, 0x28 bytes) + fim de tabela, no formato do GetSystemFirmwareTable.</summary>
    private static byte[] Table(params (byte Type, ushort SizeMB, ushort Rated, ushort Configured, string Part)[] modules)
    {
        var body = new List<byte>();
        foreach (var m in modules)
        {
            var s = new byte[0x28];
            s[0] = 17; s[1] = 0x28;
            BitConverter.GetBytes(m.SizeMB).CopyTo(s, 0x0C);
            s[0x12] = m.Type;
            BitConverter.GetBytes(m.Rated).CopyTo(s, 0x15);
            s[0x17] = 1; // fabricante = 1º texto
            s[0x1A] = (byte)(m.Part == "" ? 0 : 2); // código = 2º texto (texto vazio não entra na tabela)
            BitConverter.GetBytes(m.Configured).CopyTo(s, 0x20);
            body.AddRange(s);
            body.AddRange(System.Text.Encoding.ASCII.GetBytes(m.Part == "" ? "Fabricante\0\0" : $"Fabricante\0{m.Part}\0\0"));
        }
        body.AddRange(new byte[] { 127, 4, 0, 0, 0, 0 });
        var raw = new byte[8 + body.Count];
        raw[1] = 3;
        BitConverter.GetBytes((uint)body.Count).CopyTo(raw, 4);
        body.CopyTo(raw, 8);
        return raw;
    }

    [Fact]
    public void Le_tipo_velocidades_e_codigo_dos_pentes()
    {
        var mods = Smbios.MemoryModules(Table((0x1A, 8192, 3200, 2133, "F4-3200C16-8GVKB"), (0x1A, 8192, 3200, 2133, "F4-3200C16-8GVKB")));
        Assert.Equal(2, mods.Count);
        Assert.All(mods, m =>
        {
            Assert.Equal("DDR4", m.Type);
            Assert.Equal(3200, m.RatedSpeed);
            Assert.Equal(2133, m.ConfiguredSpeed);
            Assert.Equal("F4-3200C16-8GVKB", m.PartNumber);
            Assert.Equal(8192, m.SizeMB);
        });
    }

    [Fact]
    public void Slot_vazio_e_ignorado_e_tabela_lixo_nao_quebra()
    {
        Assert.Single(Smbios.MemoryModules(Table((0x22, 16384, 6000, 6000, "X"), (0x22, 0, 0, 0, ""))));
        Assert.Empty(Smbios.MemoryModules(new byte[] { 0, 3, 0, 0, 200, 0, 0, 0, 17, 1 }));
        Assert.Empty(Smbios.MemoryModules(Array.Empty<byte>()));
    }
}

internal sealed class FakeGpuStore : IGpuPreferenceStore
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Get(string exePath) => Values.TryGetValue(exePath, out var v) ? v : null;
    public void Set(string exePath, string? value) { if (value is null) Values.Remove(exePath); else Values[exePath] = value; }
}

public class GpuPreferencesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sb-gpu-" + Guid.NewGuid().ToString("N"));
    private readonly string _game;

    public GpuPreferencesTests()
    {
        Directory.CreateDirectory(_dir);
        _game = Path.Combine(_dir, "jogo.exe");
        File.WriteAllText(_game, "");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Ligar_e_desligar_mantem_outras_opcoes_do_Windows()
    {
        var store = new FakeGpuStore();
        store.Values[_game] = "SwapEffectUpgradeEnable=1;";

        Assert.Empty(GpuPreferences.Apply(store, new[] { _game }));
        Assert.True(GpuPreferences.IsOn(store, new[] { _game }));
        Assert.Equal("SwapEffectUpgradeEnable=1;GpuPreference=2;", store.Values[_game]);

        Assert.Empty(GpuPreferences.Remove(store, new[] { _game }));
        Assert.Equal("SwapEffectUpgradeEnable=1;", store.Values[_game]);
    }

    [Fact]
    public void Desligar_apaga_o_valor_quando_so_tinha_o_do_app()
    {
        var store = new FakeGpuStore();
        GpuPreferences.Apply(store, new[] { _game });
        GpuPreferences.Remove(store, new[] { _game });
        Assert.Empty(store.Values);
    }

    [Fact]
    public void Desligar_nao_mexe_na_escolha_do_usuario()
    {
        var store = new FakeGpuStore();
        store.Values[_game] = "GpuPreference=1;";
        GpuPreferences.Remove(store, new[] { _game });
        Assert.Equal("GpuPreference=1;", store.Values[_game]);
    }

    [Fact]
    public void Recusa_caminho_invalido_programa_do_Windows_e_inexistente()
    {
        var store = new FakeGpuStore();
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        var errors = GpuPreferences.Apply(store, new[] { "jogo.exe", notepad, Path.Combine(_dir, "nao-existe.exe"), _game + ";x.exe" });
        Assert.Equal(4, errors.Count);
        Assert.Empty(store.Values);
    }

    [Fact]
    public void Lista_vazia_nao_conta_como_ligado() => Assert.False(GpuPreferences.IsOn(new FakeGpuStore(), Array.Empty<string>()));

    [Fact]
    public void Acha_Valorant_pelo_arquivo_da_Riot_e_Fortnite_pelo_manifesto_da_Epic()
    {
        var pd = Path.Combine(_dir, "ProgramData");
        var valorantRoot = Path.Combine(_dir, "Riot Games", "VALORANT", "live");
        var valorantExe = Path.Combine(valorantRoot, @"ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(valorantExe)!);
        File.WriteAllText(valorantExe, "");
        var meta = Path.Combine(pd, "Riot Games", "Metadata", "valorant.live");
        Directory.CreateDirectory(meta);
        File.WriteAllText(Path.Combine(meta, "valorant.live.product_settings.yaml"),
            $"product_install_full_path: \"{valorantRoot.Replace('\\', '/')}\"\nproduct_install_root: \"C:/\"\n");

        var fnRoot = Path.Combine(_dir, "Epic", "Fortnite");
        var fnExe = Path.Combine(fnRoot, @"FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(fnExe)!);
        File.WriteAllText(fnExe, "");
        var manifests = Path.Combine(pd, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        Directory.CreateDirectory(manifests);
        File.WriteAllText(Path.Combine(manifests, "abc.item"),
            System.Text.Json.JsonSerializer.Serialize(new { AppName = "Fortnite", InstallLocation = fnRoot }));
        File.WriteAllText(Path.Combine(manifests, "lixo.item"), "{ não é json");

        var found = GameLocator.Find(pd, useDefaults: false);
        Assert.Contains(found, f => f.Name == "Valorant" && f.ExePath.Equals(valorantExe, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(found, f => f.Name == "Fortnite" && f.ExePath.Equals(fnExe, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, f => f.Name == "LoL");
    }
}
