using System.Windows;
using TermSearch.Models;
using TermSearch.Services;
using Keyboard = System.Windows.Input.Keyboard;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
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
    private bool _isEditMode;
    private string _editingKey = "";
    private TermEntry? _editingEntry;

    // 拖拽记住的位置只存在内存里，进程重启后（新的 PopupWindow 实例）自动恢复默认居中位置，
    // 不写 config.json（需求确认：不需要跨重启持久化）。
    private bool _hasDraggedPosition;
    private double _draggedLeft;
    private double _draggedTop;

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
        if (_hasDraggedPosition)
        {
            Left = _draggedLeft;
            Top = _draggedTop;
            ClampToVirtualScreen();
            return;
        }

        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + workArea.Height * 0.18;
    }

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove 只能在鼠标左键按下期间调用，理论上这里的调用时机必然满足；防御性忽略。
        }

        ClampToVirtualScreen();
        _hasDraggedPosition = true;
        _draggedLeft = Left;
        _draggedTop = Top;
    }

    /// <summary>
    /// 把窗口夹紧到当前所有显示器组成的虚拟桌面范围内，避免拖到不存在的屏幕区域后（比如后续
    /// 拔掉了外接显示器）再也找不到/弹不出窗口。
    /// </summary>
    private void ClampToVirtualScreen()
    {
        double virtualLeft = SystemParameters.VirtualScreenLeft;
        double virtualTop = SystemParameters.VirtualScreenTop;
        double virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        double virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : w;

        double maxLeft = Math.Max(virtualLeft, virtualRight - w);
        double maxTop = Math.Max(virtualTop, virtualBottom - h);

        Left = Math.Clamp(Left, virtualLeft, maxLeft);
        Top = Math.Clamp(Top, virtualTop, maxTop);
    }

    private void ResetState()
    {
        _addMode = false;
        _isEditMode = false;
        _editingKey = "";
        _editingEntry = null;
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

        // 可配置的"编辑当前选中词条"快捷键（默认 Ctrl+Shift+Enter）：只有当前确实有
        // 选中的匹配结果时才生效，没有匹配时按这个快捷键没有意义（应该走新增流程）。
        if (IsEditEntryHotkey(e.Key, Keyboard.Modifiers))
        {
            if (_currentMatches.Count > 0)
            {
                int idx = ResultsList.SelectedIndex >= 0 ? ResultsList.SelectedIndex : 0;
                EnterEditMode(_currentMatches[idx]);
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

    private bool IsEditEntryHotkey(Key key, System.Windows.Input.ModifierKeys modifiers)
    {
        if (!KeyComboParser.TryParse(_configManager.Config.EditEntryHotkey, out var comboModifiers, out var comboKey))
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
        _isEditMode = false;
        _editingKey = "";
        _editingEntry = null;

        AddPanelTitle.Text = "补充术语";
        AddPanelFooter.Text = "按 Enter 保存，Esc 取消并关闭";
        AddPanelFooter.Foreground = System.Windows.Media.Brushes.Gray;

        AddKeyBox.IsReadOnly = false;
        AddKeyBox.Text = key;
        FullNameBox.Text = "";
        DescriptionBox.Text = "";

        ResultsPanel.Visibility = Visibility.Collapsed;
        AddPanel.Visibility = Visibility.Visible;

        // 缩写默认填入查询词，但允许改（比如查询时输入的是小写 abc，想存成 ABC）。
        // 全选一下，方便直接改大小写或整个重新输入。
        AddKeyBox.Focus();
        Keyboard.Focus(AddKeyBox);
        AddKeyBox.SelectAll();
    }

    /// <summary>
    /// 编辑当前选中词条的全称/解释（缩写本身不允许在这个流程里改，避免和"改缩写=换 key"
    /// 混在一起变复杂——真要改缩写，还是走手动编辑 terms.json）。
    /// </summary>
    private void EnterEditMode(SearchResult result)
    {
        _addMode = true;
        _isEditMode = true;
        _editingKey = result.Key;
        _editingEntry = result.Entry;

        AddPanelTitle.Text = "编辑术语";
        AddPanelFooter.Text = "按 Enter 保存修改，Esc 取消";
        AddPanelFooter.Foreground = System.Windows.Media.Brushes.Gray;

        AddKeyBox.Text = result.Key;
        AddKeyBox.IsReadOnly = true;
        FullNameBox.Text = result.Entry.FullName;
        DescriptionBox.Text = result.Entry.Description;

        ResultsPanel.Visibility = Visibility.Collapsed;
        AddPanel.Visibility = Visibility.Visible;

        // 缩写是只读的，直接把焦点给全称，全选方便整段改掉或者微调。
        FullNameBox.Focus();
        Keyboard.Focus(FullNameBox);
        FullNameBox.SelectAll();
    }

    private void AddKeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FullNameBox.Focus();
            Keyboard.Focus(FullNameBox);
            e.Handled = true;
        }
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

        if (_isEditMode)
        {
            bool updated = _repository.UpdateTerm(_editingKey, _editingEntry!, fullName, description);
            if (!updated)
            {
                // 罕见情况：编辑过程中术语表被外部改动，原条目已经找不到了。留在当前面板，
                // 不假装保存成功，让用户看到提示后自己决定重新查询还是 Esc 放弃。
                AddPanelFooter.Text = "保存失败：该词条可能已被外部修改，请按 Esc 后重新查询再试。";
                AddPanelFooter.Foreground = System.Windows.Media.Brushes.Firebrick;
                return;
            }
        }
        else
        {
            var key = AddKeyBox.Text.Trim();
            if (string.IsNullOrEmpty(key))
            {
                AddKeyBox.Focus();
                return;
            }

            _repository.AddTerm(key, fullName, description);
        }

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
