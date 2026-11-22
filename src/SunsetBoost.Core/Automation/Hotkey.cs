namespace SunsetBoost.Core.Automation;

/// <summary>Atalho de teclado no formato "Ctrl+Shift+B" convertido pro que a API RegisterHotKey espera.</summary>
public sealed record Hotkey(uint Modifiers, uint VirtualKey, string Text)
{
    public const uint ModAlt = 0x1, ModCtrl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        uint mods = 0;
        uint? vk = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = raw.ToUpperInvariant();
            switch (part)
            {
                case "CTRL" or "CONTROL": mods |= ModCtrl; break;
                case "SHIFT": mods |= ModShift; break;
                case "ALT": mods |= ModAlt; break;
                case "WIN": mods |= ModWin; break;
                default:
                    if (vk is not null) return false;
                    if (part.Length == 1 && (char.IsAsciiLetterUpper(part[0]) || char.IsAsciiDigit(part[0]))) vk = part[0];
                    else if (part.StartsWith('F') && int.TryParse(part[1..], out var f) && f is >= 1 and <= 12) vk = (uint)(0x70 + f - 1);
                    else return false;
                    break;
            }
        }
        // Exige pelo menos um modificador pra não roubar uma tecla comum do jogo
        if (vk is null || mods == 0) return false;
        hotkey = new Hotkey(mods | ModNoRepeat, vk.Value, text.Trim());
        return true;
    }
}
