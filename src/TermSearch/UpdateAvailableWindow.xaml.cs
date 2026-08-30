using System.Windows;
using TermSearch.Models;
using TermSearch.Services;
using Application = System.Windows.Application;

namespace TermSearch;

/// <summary>
/// 检测到新版本时弹出的确认对话框（非模态，不打断正常查词）：展示版本号和更新说明，
/// 用户点"立即更新"才会真正下载、替换、重启；"以后再说"就单纯关掉，不做任何改动。
/// </summary>
public partial class UpdateAvailableWindow : Window
{
    private readonly UpdateInfo _info;
    private bool _isUpdating;

    public UpdateAvailableWindow(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;

        VersionText.Text = $"发现新版本 {info.TagName}（当前 v{UpdateChecker.CurrentVersion.Major}.{UpdateChecker.CurrentVersion.Minor}）";
        ReleaseNotesText.Text = string.IsNullOrWhiteSpace(info.ReleaseNotes) ? "（没有更新说明）" : info.ReleaseNotes;
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdating) return;
        Close();
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdating) return;
        _isUpdating = true;

        LaterButton.IsEnabled = false;
        UpdateButton.IsEnabled = false;
        StatusText.Visibility = Visibility.Visible;
        StatusText.Text = "正在下载...";

        var progress = new Progress<double>(p => StatusText.Text = $"正在下载... {p:P0}");

        var (newExePath, tempDir) = await UpdateApplier.DownloadAndExtractAsync(_info, progress);
        if (newExePath == null || tempDir == null)
        {
            ShowFailureAndReEnable("下载失败，请稍后再试，或者去 GitHub Release 页面手动下载。");
            return;
        }

        var currentExePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExePath))
        {
            ShowFailureAndReEnable("更新失败：无法确定当前程序的安装路径。");
            return;
        }

        StatusText.Text = "下载完成，即将重启更新...";

        UpdateApplier.LaunchReplaceAndRestart(newExePath, currentExePath, tempDir);

        // 替换脚本要等这个进程完全退出才会覆盖 exe，所以这里直接让整个程序退出；
        // 释放热键、互斥体等收尾工作交给 App.OnExit 统一处理。
        Application.Current.Shutdown();
    }

    private void ShowFailureAndReEnable(string message)
    {
        StatusText.Text = message;
        LaterButton.IsEnabled = true;
        UpdateButton.IsEnabled = true;
        _isUpdating = false;
    }
}
