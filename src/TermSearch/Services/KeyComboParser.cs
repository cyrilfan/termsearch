using System.Windows.Input;

namespace TermSearch.Services;

/// <summary>
/// 解析形如 "Shift+Enter"、"Ctrl+Alt+N" 的组合键字符串为 WPF 的 ModifierKeys/Key。
/// 与 <see cref="HotkeyManager.TryParse"/>（面向 Win32 全局热键注册）分开维护：
/// 这里只用于弹窗内局部按键（PreviewKeyDown）的比对，不涉及系统级注册。
/// </summary>
public static class KeyComboParser
{
    public static bool TryParse(string comboString, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(comboString)) return false;

        var parts = comboString.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 1) return false;

        string keyPart = parts[^1];
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModifierKeys.Control;
                    break;
                case "alt":
                    modifiers |= ModifierKeys.Alt;
                    break;
                case "shift":
                    modifiers |= ModifierKeys.Shift;
                    break;
                case "win":
                case "windows":
                    modifiers |= ModifierKeys.Windows;
                    break;
                default:
                    return false;
            }
        }

        if (!Enum.TryParse(keyPart, true, out key) || key == Key.None) return false;
        return true;
    }
}
