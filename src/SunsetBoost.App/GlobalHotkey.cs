using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SunsetBoost.Core.Automation;

namespace SunsetBoost.App;

/// <summary>Atalho que funciona com qualquer janela em foco (inclusive com o jogo aberto).</summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int Id = 0xB005;
    private readonly HwndSource _source;
    private readonly Action _onPressed;
    public bool IsRegistered { get; }

    public GlobalHotkey(Window window, Hotkey hotkey, Action onPressed)
    {
        _onPressed = onPressed;
        var handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle);
        _source.AddHook(Hook);
        IsRegistered = RegisterHotKey(handle, Id, hotkey.Modifiers, hotkey.VirtualKey);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == Id) { handled = true; _onPressed(); }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (IsRegistered) UnregisterHotKey(_source.Handle, Id);
        _source.RemoveHook(Hook);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
