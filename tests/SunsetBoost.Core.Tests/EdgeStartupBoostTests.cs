using SunsetBoost.Core.Diagnostics;
using SunsetBoost.Core.Edge;
using SunsetBoost.Core.Infrastructure;

namespace SunsetBoost.Core.Tests;

/// <summary>HKLM falso: chaves com seus valores DWORD.</summary>
internal sealed class FakeMachineRegistry : IMachineRegistry
{
    public Dictionary<string, Dictionary<string, int>> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Writes { get; private set; }

    public bool KeyExists(string path) => Keys.ContainsKey(path);
    public int? GetDword(string path, string name) => Keys.TryGetValue(path, out var k) && k.TryGetValue(name, out var v) ? v : null;

    public void SetDword(string path, string name, int value)
    {
        Writes++;
        if (!Keys.TryGetValue(path, out var k)) Keys[path] = k = new(StringComparer.OrdinalIgnoreCase);
        k[name] = value;
    }

    public void DeleteValue(string path, string name)
    {
        Writes++;
        if (Keys.TryGetValue(path, out var k)) k.Remove(name);
    }

    public bool DeleteKeyIfEmpty(string path)
    {
        if (!Keys.TryGetValue(path, out var k) || k.Count > 0) return false;
        Writes++;
        return Keys.Remove(path);
    }
}

public class EdgeStartupBoostTests
{
    private const string P = EdgeStartupBoost.PolicyPath;
    private const string V = EdgeStartupBoost.ValueName;

    [Fact]
    public void TurnOff_creates_key_and_writes_zero_then_restore_removes_key_it_created()
    {
        var reg = new FakeMachineRegistry();
        var settings = new AppSettings();
        var edge = new EdgeStartupBoost(settings, reg);

        Assert.False(edge.IsOff());
        Assert.True(edge.TurnOff().Ok);
        Assert.Equal(0, reg.GetDword(P, V));
        Assert.True(edge.IsOff());
        Assert.True(settings.EdgeStartupBoostUndo!.CreatedKey);

        Assert.True(edge.Restore().Ok);
        Assert.False(edge.IsOff());
        Assert.False(reg.KeyExists(P));
        Assert.Null(settings.EdgeStartupBoostUndo);
    }

    [Fact]
    public void Restore_keeps_other_edge_policies_and_the_existing_key()
    {
        var reg = new FakeMachineRegistry();
        reg.SetDword(P, "HubsSidebarEnabled", 0);
        var settings = new AppSettings();
        var edge = new EdgeStartupBoost(settings, reg);

        edge.TurnOff();
        Assert.False(settings.EdgeStartupBoostUndo!.CreatedKey);
        edge.Restore();

        Assert.True(reg.KeyExists(P));
        Assert.Null(reg.GetDword(P, V));
        Assert.Equal(0, reg.GetDword(P, "HubsSidebarEnabled"));
    }

    [Fact]
    public void Restore_does_not_delete_key_created_by_app_if_someone_added_policies_since()
    {
        var reg = new FakeMachineRegistry();
        var settings = new AppSettings();
        var edge = new EdgeStartupBoost(settings, reg);

        edge.TurnOff();
        reg.SetDword(P, "EfficiencyMode", 1);
        edge.Restore();

        Assert.True(reg.KeyExists(P));
        Assert.Null(reg.GetDword(P, V));
        Assert.Equal(1, reg.GetDword(P, "EfficiencyMode"));
    }

    [Fact]
    public void Restore_puts_back_the_previous_value()
    {
        var reg = new FakeMachineRegistry();
        reg.SetDword(P, V, 1);
        var settings = new AppSettings();
        var edge = new EdgeStartupBoost(settings, reg);

        edge.TurnOff();
        Assert.Equal(0, reg.GetDword(P, V));
        edge.Restore();

        Assert.Equal(1, reg.GetDword(P, V));
        Assert.True(reg.KeyExists(P));
    }

    [Fact]
    public void Restore_without_record_changes_nothing()
    {
        // Desligado por outro programa ou pela empresa (ou registro perdido): o app não apaga política que não criou
        var reg = new FakeMachineRegistry();
        reg.SetDword(P, V, 0);
        int before = reg.Writes;
        var edge = new EdgeStartupBoost(new AppSettings(), reg);

        Assert.True(edge.IsOff());
        Assert.False(edge.TurnedOffByApp);
        Assert.False(edge.Restore().Ok);

        Assert.Equal(before, reg.Writes);
        Assert.Equal(0, reg.GetDword(P, V));
    }

    [Fact]
    public void TurnOff_when_already_off_changes_nothing()
    {
        var reg = new FakeMachineRegistry();
        reg.SetDword(P, V, 0);
        int before = reg.Writes;
        var settings = new AppSettings();

        Assert.True(new EdgeStartupBoost(settings, reg).TurnOff().Ok);
        Assert.Equal(before, reg.Writes);
        Assert.Null(settings.EdgeStartupBoostUndo);
    }

    [Fact]
    public void Simulation_writes_nothing()
    {
        var reg = new FakeMachineRegistry();
        var settings = new AppSettings { SimulationMode = true };
        var edge = new EdgeStartupBoost(settings, reg);

        Assert.True(edge.TurnOff().Ok);
        Assert.Equal(0, reg.Writes);
        Assert.False(reg.KeyExists(P));
        Assert.Null(settings.EdgeStartupBoostUndo);

        reg.SetDword(P, V, 0);
        settings.EdgeStartupBoostUndo = new EdgeStartupBoostUndo { CreatedKey = true };
        int before = reg.Writes;
        Assert.True(edge.Restore().Ok);
        Assert.Equal(before, reg.Writes);
        Assert.Equal(0, reg.GetDword(P, V));
    }

    [Fact]
    public void Diagnose_offers_turn_off_only_with_admin()
    {
        var reg = new FakeMachineRegistry();
        var edge = new EdgeStartupBoost(new AppSettings(), reg);

        var admin = edge.Diagnose(elevated: true);
        Assert.Equal(DiagStatus.Recommend, admin.Status);
        Assert.Equal(EdgeStartupBoost.ActionTurnOff, admin.Action?.Id);
        Assert.Contains("Gerenciado pela sua organização", admin.Detail);

        var noAdmin = edge.Diagnose(elevated: false);
        Assert.Equal(DiagStatus.Recommend, noAdmin.Status);
        Assert.Null(noAdmin.Action);
        Assert.Contains("Aumento de inicialização", noAdmin.Detail);
    }

    [Fact]
    public void Diagnose_when_off_by_app_is_ok_and_offers_restore_with_admin()
    {
        var reg = new FakeMachineRegistry();
        var edge = new EdgeStartupBoost(new AppSettings(), reg);
        Assert.True(edge.TurnOff().Ok);

        var admin = edge.Diagnose(elevated: true);
        Assert.Equal(DiagStatus.Ok, admin.Status);
        Assert.Equal(EdgeStartupBoost.ActionRestore, admin.Action?.Id);
        Assert.Equal("VOLTAR AO PADRÃO", admin.Action?.Label);

        var noAdmin = edge.Diagnose(elevated: false);
        Assert.Equal(DiagStatus.Ok, noAdmin.Status);
        Assert.Null(noAdmin.Action);
    }

    [Fact]
    public void Diagnose_when_off_by_someone_else_has_no_button()
    {
        var reg = new FakeMachineRegistry();
        reg.SetDword(P, V, 0);
        var item = new EdgeStartupBoost(new AppSettings(), reg).Diagnose(elevated: true);

        Assert.Equal(DiagStatus.Ok, item.Status);
        Assert.Null(item.Action);
        Assert.Contains("fora do Sunset Boost", item.Detail);
    }

    [Fact]
    public void Undo_record_survives_settings_round_trip()
    {
        var dir = Fs.NewDir();
        try
        {
            var file = Path.Combine(dir, "settings.json");
            var settings = new AppSettings();
            new EdgeStartupBoost(settings, new FakeMachineRegistry()).TurnOff();
            new SettingsStore(file).Save(settings);

            var loaded = new SettingsStore(file).Load();
            Assert.True(loaded.EdgeStartupBoostUndo!.CreatedKey);
            Assert.Null(loaded.EdgeStartupBoostUndo.PreviousValue);
        }
        finally { Fs.DeleteDir(dir); }
    }
}
