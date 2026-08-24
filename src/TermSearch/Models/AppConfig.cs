namespace TermSearch.Models;

public class AppConfig
{
    public string Hotkey { get; set; } = "Alt+Space";
    public bool StartWithWindows { get; set; } = false;

    /// <summary>弹窗内“为当前缩写追加一条新全称”的局部快捷键（默认 Shift+Enter），不同于 <see cref="Hotkey"/> 那个系统级全局热键。</summary>
    public string AddVariantHotkey { get; set; } = "Shift+Enter";
}
