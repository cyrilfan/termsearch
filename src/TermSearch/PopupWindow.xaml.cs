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
    private readonly ConfigManager _configManager;
    private List<SearchResult> _currentMatches = new();
    private bool _addMode;
    private string _pendingKey = "";

    public PopupWindow(TermRepository repository, ConfigManager configManager)
    {
        InitializeComponent();
        _repository = repository;
        _configManager = configManager;
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
        _currentMatches = new List<SearchResult>();

        SearchBox.Text = "";
        AddPanel.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Visible;
        ResultsList.ItemsSource = null;
        ResultsList.Visibility = Visibility.Collapsed;
        HintText.Visibility = Visibility.Collapsed;
        AddVariantHintText.Visibility = Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_addMode) return;
        UpdateResults(SearchBox.Text.Trim());
    }

    private void UpdateResults(string query)
    {
        _currentMatches = string.IsNullOrEmpty(query)
            ? new List<SearchResult>()
            : _repository.Search(query);

        if (_currentMatches.Count > 0)
        {
            ResultsList.ItemsSource = _currentMatches.Select(m => new ResultRow(m)).ToList();
            ResultsList.SelectedIndex = 0;
            ResultsList.Visibility = Visibility.Visible;
            HintText.Visibility = Visibility.Collapsed;
            AddVariantHintText.Text = $"{_configManager.Config.AddVariantHotkey} 追加新全称";
            AddVariantHintText.Visibility = Visibility.Visible;
        }
        else
        {
            ResultsList.ItemsSource = null;
            ResultsList.Visibility = Visibility.Collapsed;
            HintText.Visibility = string.IsNullOrEmpty(query) ? Visibility.Collapsed : Visibility.Visible;
            AddVariantHintText.Visibility = Visibility.Collapsed;
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

        // 可配置的“追加新全称”快捷键（默认 Shift+Enter）：无论当前有没有匹配结果，
        // 都以当前输入框文本作为缩写进入补充模式。放在 Down/Up/Enter 判断之前，
        // 避免用户把它配置成类似 Shift+Down 这种会和内置导航键冲突的组合时被内置逻辑抢先处理。
        if (IsAddVariantHotkey(e.Key, Keyboard.Modifiers))
        {
            var query = SearchBox.Text.Trim();
            if (!string.IsNullOrEmpty(query))
            {
                EnterAddMode(query);
            }
            e.Handled = true;
            return;
        }

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

    private bool IsAddVariantHotkey(Key key, System.Windows.Input.ModifierKeys modifiers)
    {
        if (!KeyComboParser.TryParse(_configManager.Config.AddVariantHotkey, out var comboModifiers, out var comboKey))
        {
            return false;
        }

        return key == comboKey && modifiers == comboModifiers;
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
    public ResultRow(SearchResult result)
    {
        Key = result.Key;
        Entry = result.Entry;
        KeySegments = BuildKeySegments(result);
    }

    public string Key { get; }
    public TermEntry Entry { get; }

    /// <summary>缩写按"是否命中"切分出的片段，用于模糊匹配结果的高亮显示。</summary>
    public List<KeySegment> KeySegments { get; }

    public Visibility DescriptionVisibility =>
        string.IsNullOrEmpty(Entry.Description) ? Visibility.Collapsed : Visibility.Visible;

    // 全部/前缀匹配已经足够直观（命中位置显而易见），只对模糊匹配做高亮，避免界面显得杂乱。
    private static List<KeySegment> BuildKeySegments(SearchResult result)
    {
        if (result.Tier != MatchTier.Fuzzy || result.MatchedIndices.Length == 0)
        {
            return new List<KeySegment> { new KeySegment(result.Key, false) };
        }

        var matched = new HashSet<int>(result.MatchedIndices);
        var segments = new List<KeySegment>();
        var buffer = new System.Text.StringBuilder();
        bool? bufferIsMatch = null;

        for (int i = 0; i < result.Key.Length; i++)
        {
            bool isMatch = matched.Contains(i);
            if (bufferIsMatch is not null && bufferIsMatch != isMatch)
            {
                segments.Add(new KeySegment(buffer.ToString(), bufferIsMatch.Value));
                buffer.Clear();
            }
            buffer.Append(result.Key[i]);
            bufferIsMatch = isMatch;
        }
        if (buffer.Length > 0)
        {
            segments.Add(new KeySegment(buffer.ToString(), bufferIsMatch!.Value));
        }

        return segments;
    }
}

/// <summary>缩写文本中的一段连续片段，标记这段是否命中了模糊匹配的查询字符。</summary>
public class KeySegment
{
    public KeySegment(string text, bool isMatch)
    {
        Text = text;
        IsMatch = isMatch;
    }

    public string Text { get; }
    public bool IsMatch { get; }
}
