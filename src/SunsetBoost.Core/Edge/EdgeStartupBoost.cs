using Microsoft.Win32;
using SunsetBoost.Core.Diagnostics;
using SunsetBoost.Core.Infrastructure;

namespace SunsetBoost.Core.Edge;

/// <summary>Registro da máquina (HKLM, visão 64 bits). Nos testes entra uma versão falsa.</summary>
public interface IMachineRegistry
{
    bool KeyExists(string path);
    /// <summary>null se a chave ou o valor não existir, ou se o valor não for DWORD.</summary>
    int? GetDword(string path, string name);
    /// <summary>Cria a chave se precisar.</summary>
    void SetDword(string path, string name, int value);
    void DeleteValue(string path, string name);
    /// <summary>Apaga a chave só se ela não tiver nenhum valor nem subchave. true se apagou.</summary>
    bool DeleteKeyIfEmpty(string path);
}

public sealed class RealMachineRegistry : IMachineRegistry
{
    private static RegistryKey Hklm() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    public bool KeyExists(string path)
    {
        using var hklm = Hklm();
        using var k = hklm.OpenSubKey(path);
        return k is not null;
    }

    public int? GetDword(string path, string name)
    {
        using var hklm = Hklm();
        using var k = hklm.OpenSubKey(path);
        return k?.GetValue(name) is int v ? v : null;
    }

    public void SetDword(string path, string name, int value)
    {
        using var hklm = Hklm();
        using var k = hklm.CreateSubKey(path, writable: true);
        k.SetValue(name, value, RegistryValueKind.DWord);
    }

    public void DeleteValue(string path, string name)
    {
        using var hklm = Hklm();
        using var k = hklm.OpenSubKey(path, writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }

    public bool DeleteKeyIfEmpty(string path)
    {
        using var hklm = Hklm();
        using (var k = hklm.OpenSubKey(path))
        {
            if (k is null || k.ValueCount > 0 || k.SubKeyCount > 0) return false;
        }
        hklm.DeleteSubKey(path, throwOnMissingSubKey: false);
        return true;
    }
}

/// <summary>O que havia antes de o app desligar o Startup boost, pra VOLTAR AO PADRÃO deixar igual.</summary>
public sealed class EdgeStartupBoostUndo
{
    /// <summary>A chave das políticas do Edge não existia: o app criou.</summary>
    public bool CreatedKey { get; set; }
    /// <summary>Valor de StartupBoostEnabled antes (null = não existia).</summary>
    public int? PreviousValue { get; set; }
}

public sealed record EdgeBoostResult(bool Ok, string Message);

/// <summary>
/// "Startup boost" (Aumento de inicialização) do Edge: deixa o Edge carregado na memória mesmo fechado.
/// O app desliga pela política oficial (HKLM\SOFTWARE\Policies\Microsoft\Edge, StartupBoostEnabled = 0), que o Edge lê
/// ao abrir. Por ser política, o Edge mostra "Gerenciado pela sua organização" e trava a opção até voltar ao padrão.
/// </summary>
public sealed class EdgeStartupBoost
{
    public const string PolicyPath = @"SOFTWARE\Policies\Microsoft\Edge";
    public const string ValueName = "StartupBoostEnabled";

    public const string ActionTurnOff = "edge-startup-boost-off";
    public const string ActionRestore = "edge-startup-boost-restore";

    public const string ManagedNote =
        "Por ser uma política do Windows, o Edge passa a mostrar \"Gerenciado pela sua organização\" e a opção fica travada " +
        "nas configurações dele até você voltar ao padrão. Vale na próxima vez que o Edge abrir.";

    private readonly IMachineRegistry _reg;
    private readonly AppSettings _settings;

    /// <param name="settings">Onde fica guardado o que havia antes (quem chama salva depois de TurnOff/Restore).</param>
    public EdgeStartupBoost(AppSettings settings, IMachineRegistry? registry = null)
    {
        _settings = settings;
        _reg = registry ?? new RealMachineRegistry();
    }

    /// <summary>A política está desligando o Startup boost.</summary>
    public bool IsOff() => _reg.GetDword(PolicyPath, ValueName) == 0;

    public EdgeBoostResult TurnOff()
    {
        if (IsOff()) return new(true, "O Startup boost do Edge já está desligado");
        if (_settings.SimulationMode)
        {
            AppLog.Info($"[simulação] Startup boost do Edge: gravaria HKLM\\{PolicyPath}\\{ValueName} = 0");
            return new(true, "Simulação: o Startup boost do Edge seria desligado (nada foi alterado)");
        }
        try
        {
            var undo = new EdgeStartupBoostUndo
            {
                CreatedKey = !_reg.KeyExists(PolicyPath),
                PreviousValue = _reg.GetDword(PolicyPath, ValueName),
            };
            _reg.SetDword(PolicyPath, ValueName, 0);
            // Já tinha um registro de antes (o app desligou, alguém religou): o estado original é o mais antigo
            _settings.EdgeStartupBoostUndo ??= undo;
            AppLog.Info($"Startup boost do Edge desligado: HKLM\\{PolicyPath}\\{ValueName} = 0 " +
                        $"(antes: {(undo.PreviousValue?.ToString() ?? "ausente")}{(undo.CreatedKey ? ", chave criada pelo app" : "")})");
            return new(true, "Startup boost do Edge desligado: vale na próxima vez que o Edge abrir");
        }
        catch (Exception ex)
        {
            AppLog.Error("Startup boost do Edge: não consegui desligar", ex);
            return new(false, $"Não consegui desligar o Startup boost do Edge: {ex.Message}");
        }
    }

    /// <summary>O próprio app desligou (tem o registro do que havia antes). Sem isso, a política é de outro programa ou da empresa.</summary>
    public bool TurnedOffByApp => _settings.EdgeStartupBoostUndo is not null;

    /// <summary>
    /// Volta ao padrão: põe de volta o valor que havia antes ou apaga só StartupBoostEnabled. A chave das políticas do Edge
    /// só sai se o app criou ela e ela ficou vazia; outras políticas do Edge nunca são tocadas.
    /// Só age se foi o app que desligou: política de outro programa ou da empresa nunca é apagada.
    /// </summary>
    public EdgeBoostResult Restore()
    {
        var undo = _settings.EdgeStartupBoostUndo;
        if (undo is null)
        {
            AppLog.Info("Startup boost do Edge: voltar ao padrão ignorado, quem desligou não foi o app");
            return new(false, "O Startup boost do Edge não foi desligado pelo Sunset Boost: nada foi alterado");
        }
        if (_settings.SimulationMode)
        {
            AppLog.Info($"[simulação] Startup boost do Edge: {(undo.PreviousValue is { } p ? $"gravaria {ValueName} = {p}" : $"apagaria HKLM\\{PolicyPath}\\{ValueName}")}");
            return new(true, "Simulação: o Startup boost do Edge voltaria ao padrão (nada foi alterado)");
        }
        try
        {
            if (undo.PreviousValue is { } previous and not 0)
                _reg.SetDword(PolicyPath, ValueName, previous);
            else
                _reg.DeleteValue(PolicyPath, ValueName);

            bool keyDeleted = undo.CreatedKey && _reg.DeleteKeyIfEmpty(PolicyPath);
            _settings.EdgeStartupBoostUndo = null;
            AppLog.Info($"Startup boost do Edge voltou ao padrão: " +
                        (undo.PreviousValue is { } v and not 0 ? $"{ValueName} = {v} de novo" : $"{ValueName} apagado") +
                        (keyDeleted ? ", chave vazia criada pelo app apagada" : ""));
            return new(true, "Startup boost do Edge voltou ao padrão: a opção volta a valer nas configurações do Edge");
        }
        catch (Exception ex)
        {
            AppLog.Error("Startup boost do Edge: não consegui voltar ao padrão", ex);
            return new(false, $"Não consegui voltar o Startup boost do Edge ao padrão: {ex.Message}");
        }
    }

    /// <summary>Item da aba Diagnóstico. Com admin, ganha o botão DESLIGAR, ou VOLTAR AO PADRÃO se foi o app que desligou.</summary>
    public DiagItem Diagnose(bool elevated)
    {
        bool off;
        try { off = IsOff(); }
        catch (Exception ex) { AppLog.Warn($"Startup boost do Edge: não consegui ler o registro: {ex.Message}"); off = false; }

        // Sem admin, o settings.json é outro (o do usuário), então não dá pra saber quem desligou: cai no texto simples abaixo
        if (off && elevated && !TurnedOffByApp)
            return new(DiagStatus.Ok, "Startup boost do Edge desligado",
                "O Edge não fica carregado na memória à toa. Quem desligou foi uma política de fora do Sunset Boost " +
                "(outro programa ou a empresa), então o app não mexe nela.");

        if (off)
            return elevated
                ? new(DiagStatus.Ok, "Startup boost do Edge desligado",
                    "O Edge não fica carregado na memória à toa. Está desligado por política: o Edge mostra \"Gerenciado pela sua " +
                    "organização\" e a opção fica travada nas configurações dele até você voltar ao padrão.",
                    new DiagAction(ActionRestore, "VOLTAR AO PADRÃO"))
                : new(DiagStatus.Ok, "Startup boost do Edge desligado", "O Edge não fica carregado na memória à toa.");

        return elevated
            ? new(DiagStatus.Recommend, "Desligar o \"Startup boost\" do Edge",
                "É ele que mantém o Edge na memória mesmo fechado. O app desliga com um clique e dá pra voltar ao padrão quando quiser. " + ManagedNote,
                new DiagAction(ActionTurnOff, "DESLIGAR"))
            : new(DiagStatus.Recommend, "Desligar o \"Startup boost\" do Edge",
                "É ele que mantém o Edge na memória mesmo fechado. No Edge: Configurações › Sistema e desempenho › Aumento de inicialização.");
    }
}
