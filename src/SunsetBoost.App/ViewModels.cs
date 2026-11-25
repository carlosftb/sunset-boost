using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace SunsetBoost.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

/// <summary>Card de um app na aba Limpeza.</summary>
public sealed class AppCardVm : Observable
{
    private readonly Action<AppCardVm>? _onToggle;
    private bool _isOn;

    public AppCardVm(string id, string name, string ram, string info, Brush accent, bool locked, bool isOn, Action<AppCardVm>? onToggle)
    {
        Id = id; Name = name; Ram = ram; Info = info; Accent = accent; Locked = locked; _isOn = isOn; _onToggle = onToggle;
    }

    public string Id { get; }
    public string Name { get; }
    public string Ram { get; }
    /// <summary>Dica do card quando a RAM não é medida (jogo e anti-cheat). Sem dica: null.</summary>
    public string? RamHint { get; init; }
    public string Info { get; }
    public Brush Accent { get; }
    public bool Locked { get; }
    public bool Unlocked => !Locked;
    public double CardOpacity => Locked ? 0.78 : 1;

    public bool IsOn
    {
        get => _isOn;
        set { if (Set(ref _isOn, value)) _onToggle?.Invoke(this); }
    }
}

/// <summary>Linha de ajuste na aba Modo Jogo.</summary>
public sealed class TweakRowVm : Observable
{
    private readonly Action<TweakRowVm> _onToggle;
    private bool _isOn;

    public TweakRowVm(string id, string title, string detail, string impact, Brush impactBg, Brush impactFg, bool isOn, string? note, Action<TweakRowVm> onToggle)
    {
        Id = id; Title = title; Detail = detail; Impact = impact; ImpactBg = impactBg; ImpactFg = impactFg; _isOn = isOn; Note = note; _onToggle = onToggle;
    }

    public string Id { get; }
    public string Title { get; }
    public string Detail { get; }
    public string Impact { get; }
    public Brush ImpactBg { get; }
    public Brush ImpactFg { get; }
    public string? Note { get; }
    public bool HasNote => !string.IsNullOrEmpty(Note);

    public bool IsOn
    {
        get => _isOn;
        set { if (Set(ref _isOn, value)) _onToggle(this); }
    }
}

public sealed record ProfileVm(string Id, string Name, string Detail, Brush Background, bool IsSelected)
{
    /// <summary>Nome lido pelo leitor de tela: o perfil ativo só aparece pela borda branca, então vai no nome.</summary>
    public string A11yName => IsSelected ? $"{Name}, perfil ativo" : Name;
}

public sealed record DiagVm(string Mark, Brush MarkBrush, string Title, string Detail, string? ActionId = null, string? ActionLabel = null)
{
    public System.Windows.Visibility ActionVisibility => ActionId is null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
}

public sealed record TopAppVm(string Name, string Ram, string Info, Brush RamBrush);
