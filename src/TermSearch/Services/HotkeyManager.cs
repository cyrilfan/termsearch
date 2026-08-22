using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace TermSearch.Services;

/// <summary>
/// 封装 Win32 全局热键的注册/注销（需求文档第 4 节）。
/// 用一个不可见窗口承接 WM_HOTKEY 消息。
/// </summary>
public class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 0x4A51;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private Window? _messageWindow;
    private HwndSource? _source;
    private bool _registered;

    public event Action? HotkeyPressed;

    /// <summary>尝试注册热键，返回是否成功。失败（多半是被其他程序占用）不会抛异常。</summary>
    public bool Register(string hotkeyString)
    {
        Unregister();

        if (!TryParse(hotkeyString, out uint modifiers, out uint vk))
        {
            Logger.Log($"热键配置无法解析：\"{hotkeyString}\"");
            return false;
        }

        _messageWindow = new Window
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Width = 0,
            Height = 0,
            Left = -10000,
            Top = -10000,
            Visibility = Visibility.Hidden,
        };

        var helper = new WindowInteropHelper(_messageWindow);
        helper.EnsureHandle();
        _source = HwndSource.FromHwnd(helper.Handle);
        _source!.AddHook(WndProc);

        _registered = RegisterHotKey(helper.Handle, HOTKEY_ID, modifiers | MOD_NOREPEAT, vk);
        if (!_registered)
        {
            Logger.Log($"注册全局热键失败（可能已被其他程序占用）：{hotkeyString}");
        }
        return _registered;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>解析形如 "Alt+Space"、"Ctrl+Alt+T" 的热键字符串。</summary>
    public static bool TryParse(string hotkeyString, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(hotkeyString)) return false;

        var parts = hotkeyString.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 1) return false;

        string keyPart = parts[^1];
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= MOD_CONTROL;
                    break;
                case "alt":
                    modifiers |= MOD_ALT;
                    break;
                case "shift":
                    modifiers |= MOD_SHIFT;
                    break;
                case "win":
                case "windows":
                    modifiers |= MOD_WIN;
                    break;
                default:
                    return false;
            }
        }

        if (!Enum.TryParse<Key>(keyPart, true, out var key) || key == Key.None)
        {
            return false;
        }

        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return vk != 0;
    }

    public void Unregister()
    {
        if (_source != null)
        {
            if (_registered)
            {
                UnregisterHotKey(_source.Handle, HOTKEY_ID);
                _registered = false;
            }
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }

        _messageWindow?.Close();
        _messageWindow = null;
    }

    public void Dispose() => Unregister();
}
