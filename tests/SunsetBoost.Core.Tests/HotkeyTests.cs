using SunsetBoost.Core.Automation;

namespace SunsetBoost.Core.Tests;

public class HotkeyTests
{
    [Fact]
    public void Ctrl_Shift_B()
    {
        Assert.True(Hotkey.TryParse("Ctrl+Shift+B", out var h));
        Assert.Equal(Hotkey.ModCtrl | Hotkey.ModShift | Hotkey.ModNoRepeat, h.Modifiers);
        Assert.Equal((uint)'B', h.VirtualKey);
    }

    [Theory]
    [InlineData("alt + f9", 0x78u)]
    [InlineData("Ctrl+Alt+5", (uint)'5')]
    public void Outros_formatos(string text, uint vk)
    {
        Assert.True(Hotkey.TryParse(text, out var h));
        Assert.Equal(vk, h.VirtualKey);
    }

    [Theory]
    [InlineData("B")]          // sem modificador: roubaria a tecla do jogo
    [InlineData("Ctrl+Shift")] // sem tecla
    [InlineData("Ctrl+B+C")]
    [InlineData("Ctrl+F13")]
    [InlineData("")]
    public void Invalidos(string text) => Assert.False(Hotkey.TryParse(text, out _));
}
