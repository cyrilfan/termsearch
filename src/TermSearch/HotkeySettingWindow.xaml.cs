using System.Windows;
using Key = System.Windows.Input.Key;
using Keyboard = System.Windows.Input.Keyboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ModifierKeys = System.Windows.Input.ModifierKeys;

namespace TermSearch;

/// <summary>
/// 修改全局热键的小对话框（从托盘菜单打开）：录制新的组合键，尝试注册，成功后写回配置文件。
/// </summary>
public partial class HotkeySettingWindow : Window
{
    private readonly Func<string, (bool Success, string? Error)> _tryApply;
    private string? _capturedHotkey;

    public HotkeySettingWindow(string currentHotkey, Func<string, (bool Success, string? Error)> tryApply)
    {
        InitializeComponent();
        _tryApply = tryApply;
        CaptureBox.Text = currentHotkey;
        Loaded += (_, _) =>
        {
            CaptureBox.Focus();
            Keyboard.Focus(CaptureBox);
        };
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void CaptureBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Tab 用于在控件间移动焦点，不作为热键捕获处理。
        if (key == Key.Tab) return;

        e.Handled = true;

        // 只按下功能键本身时，还不构成一个完整组合，先不处理。
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                 or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
        {
            return;
        }

        var mods = Keyboard.Modifiers;
        bool winDown = mods.HasFlag(ModifierKeys.Windows) || Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);

        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (winDown) parts.Add("Win");

        if (parts.Count == 0)
        {
            ShowError("至少需要包含一个 Ctrl / Alt / Shift / Win 组合键。");
            SaveButton.IsEnabled = false;
            return;
        }

        parts.Add(key.ToString());
        _capturedHotkey = string.Join("+", parts);
        CaptureBox.Text = _capturedHotkey;
        HideError();
        SaveButton.IsEnabled = true;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_capturedHotkey)) return;

        var (success, error) = _tryApply(_capturedHotkey);
        if (success)
        {
            Close();
        }
        else
        {
            ShowError(error ?? "保存失败。");
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        ErrorText.Visibility = Visibility.Collapsed;
    }
}
