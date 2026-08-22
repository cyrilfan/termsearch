using System.Windows;
using TermSearch.Models;
using TermSearch.Services;
using Keyboard = System.Windows.Input.Keyboard;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Clipboard = System.Windows.Clipboard;

namespace TermSearch;

/// <summary>
/// 核心交互窗口（需求文档第 5、6、7 节）：输入前缀查询、Enter 复制全称并关闭、
/// Esc/点击窗口外部关闭，未匹配时支持在同一窗口内快捷补充词条。
/// </summary>
public partial class PopupWindow : Window
{
    private readonly TermRepository _repository;
    private List<(string Key, TermEntry Entry)> _currentMatches = new();
    private bool _addMode;
    private string _pendingKey = "";

    public PopupWindow(TermRepository repository)
    {
        InitializeComponent();
        _repository = repository;
    }

    public void ShowAtFixedPosition()
    {
        ResetState();
        PositionWindow();
        Show();
        Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    private void PositionWindow()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + workArea.Height * 0.18;
    }

    private void ResetState()
    {
        _addMode = false;
        _pendingKey = "";
        _currentMatches = new List<(string, TermEntry)>();

        SearchBox.Text = "";
        AddPanel.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Visible;
        ResultsList.ItemsSource = null;
        ResultsList.Visibility = Visibility.Collapsed;
        HintText.Visibility = Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_addMode) return;
        UpdateResults(SearchBox.Text.Trim());
    }

    private void UpdateResults(string query)
    {
        _currentMatches = string.IsNullOrEmpty(query)
            ? new List<(string, TermEntry)>()
            : _repository.FindByPrefix(query);

        if (_currentMatches.Count > 0)
        {
            ResultsList.ItemsSource = _currentMatches.Select(m => new ResultRow(m.Key, m.Entry)).ToList();
            ResultsList.SelectedIndex = 0;
            ResultsList.Visibility = Visibility.Visible;
            HintText.Visibility = Visibility.Collapsed;
        }
        else
        {
            ResultsList.ItemsSource = null;
            ResultsList.Visibility = Visibility.Collapsed;
            HintText.Visibility = string.IsNullOrEmpty(query) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Esc 在任何状态下都直接关闭且不产生副作用（含补充模式下取消补充）。
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }

        if (_addMode) return; // 补充模式下的 Enter 由各输入框自己的 KeyDown 处理

        if (e.Key == Key.Down)
        {
            MoveSelection(1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            MoveSelection(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            HandleEnterInSearch();
            e.Handled = true;
        }
    }

    private void MoveSelection(int delta)
    {
        if (_currentMatches.Count == 0) return;

        int next = ResultsList.SelectedIndex + delta;
        next = Math.Clamp(next, 0, _currentMatches.Count - 1);
        ResultsList.SelectedIndex = next;
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
    }

    private void HandleEnterInSearch()
    {
        var query = SearchBox.Text.Trim();

        if (_currentMatches.Count > 0)
        {
            int idx = ResultsList.SelectedIndex >= 0 ? ResultsList.SelectedIndex : 0;
            var chosen = _currentMatches[idx];
            TryCopyToClipboard(chosen.Entry.FullName);
            Hide();
        }
        else if (!string.IsNullOrEmpty(query))
        {
            EnterAddMode(query);
        }
    }

    private static void TryCopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            Logger.Log($"复制到剪贴板失败：{ex.Message}");
        }
    }

    private void EnterAddMode(string key)
    {
        _addMode = true;
        _pendingKey = key;

        AddKeyLabel.Text = key;
        FullNameBox.Text = "";
        DescriptionBox.Text = "";

        ResultsPanel.Visibility = Visibility.Collapsed;
        AddPanel.Visibility = Visibility.Visible;

        FullNameBox.Focus();
        Keyboard.Focus(FullNameBox);
    }

    private void FullNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DescriptionBox.Focus();
            Keyboard.Focus(DescriptionBox);
            e.Handled = true;
        }
    }

    private void DescriptionBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitAdd();
            e.Handled = true;
        }
    }

    private void CommitAdd()
    {
        var fullName = FullNameBox.Text.Trim();
        var description = DescriptionBox.Text.Trim();

        if (string.IsNullOrEmpty(fullName))
        {
            FullNameBox.Focus();
            return;
        }

        _repository.AddTerm(_pendingKey, fullName, description);
        Hide();
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        Hide();
    }
}

/// <summary>结果列表的展示行，供 XAML 数据绑定使用。</summary>
public class ResultRow
{
    public ResultRow(string key, TermEntry entry)
    {
        Key = key;
        Entry = entry;
    }

    public string Key { get; }
    public TermEntry Entry { get; }

    public Visibility DescriptionVisibility =>
        string.IsNullOrEmpty(Entry.Description) ? Visibility.Collapsed : Visibility.Visible;
}
