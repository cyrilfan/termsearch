using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using TermSearch.Services;
using Application = System.Windows.Application;

namespace TermSearch;

public partial class App : Application
{
    // 命名带上一个固定后缀，避免和其他程序的同名内核对象冲突。
    private const string MutexName = "Local\\TermSearch_SingleInstance_9F3D2E7B";
    private const string ShowPopupEventName = "Local\\TermSearch_ShowPopup_9F3D2E7B";

    private static string AppDir => AppContext.BaseDirectory;
    private static string TermsPath => Path.Combine(AppDir, "terms.json");
    private static string ConfigPath => Path.Combine(AppDir, "config.json");

    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private EventWaitHandle? _showPopupEvent;

    private TermRepository? _termRepository;
    private ConfigManager? _configManager;
    private HotkeyManager? _hotkeyManager;
    private TrayIconManager? _trayIconManager;
    private PopupWindow? _popupWindow;
    private UpdateAvailableWindow? _updateWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 作为常驻后台工具，UI 线程上的意外异常不应导致整个程序退出（需求文档第 12 节）。
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Log($"未处理的界面异常：{args.Exception}");
            args.Handled = true;
        };

        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (!createdNew)
        {
            NotifyExistingInstance();
            Shutdown();
            return;
        }

        _showPopupEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowPopupEventName);
        StartSecondInstanceListener();

        _termRepository = new TermRepository(TermsPath);
        _configManager = new ConfigManager(ConfigPath);
        _popupWindow = new PopupWindow(_termRepository, _configManager);

        _hotkeyManager = new HotkeyManager();
        _hotkeyManager.HotkeyPressed += () => Dispatcher.Invoke(ShowPopup);
        RegisterHotkeyFromConfig();

        _trayIconManager = new TrayIconManager(_configManager.Config.StartWithWindows);
        _trayIconManager.OpenTermsRequested += OpenTermsFile;
        _trayIconManager.ReloadRequested += ReloadTerms;
        _trayIconManager.ChangeHotkeyRequested += OpenHotkeySettings;
        _trayIconManager.ChangeAddVariantHotkeyRequested += OpenAddVariantHotkeySettings;
        _trayIconManager.ChangeEditEntryHotkeyRequested += OpenEditEntryHotkeySettings;
        _trayIconManager.CheckForUpdateRequested += () => _ = CheckForUpdateAsync(showUpToDateMessage: true);
        _trayIconManager.StartWithWindowsToggled += OnStartWithWindowsToggled;
        _trayIconManager.ExitRequested += () => Shutdown();

        _termRepository.LoadFailed += backupPath => OnDataLoadFailed("术语表", backupPath);
        _configManager.LoadFailed += backupPath => OnDataLoadFailed("配置", backupPath);

        CheckPendingUpdateFailureMarker();

        // 启动时顺手异步查一下有没有新版本，不阻塞热键注册/弹窗响应速度；查不到/查失败都静默处理。
        _ = CheckForUpdateAsync(showUpToDateMessage: false);
    }

    private static void NotifyExistingInstance()
    {
        try
        {
            using var existingEvent = EventWaitHandle.OpenExisting(ShowPopupEventName);
            existingEvent.Set();
        }
        catch (Exception ex)
        {
            Logger.Log($"唤醒已运行实例失败：{ex.Message}");
        }
    }

    private void StartSecondInstanceListener()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                _showPopupEvent!.WaitOne();
                try
                {
                    Dispatcher.Invoke(ShowPopup);
                }
                catch (Exception ex)
                {
                    Logger.Log($"响应第二次启动请求失败：{ex.Message}");
                }
            }
        })
        {
            IsBackground = true,
            Name = "TermSearch-SecondInstanceListener",
        };
        thread.Start();
    }

    private void RegisterHotkeyFromConfig()
    {
        var hotkey = _configManager!.Config.Hotkey;
        _hotkeyManager!.Register(hotkey);
    }

    private void ShowPopup()
    {
        _popupWindow?.ShowAtFixedPosition();
    }

    private void OpenTermsFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo(TermsPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Log($"打开 terms.json 失败：{ex.Message}");
        }
    }

    private void ReloadTerms()
    {
        _termRepository?.Load();
        _trayIconManager?.ShowBalloon("术语速查", "术语表已重新加载");
    }

    private void OpenHotkeySettings()
    {
        var current = _configManager!.Config.Hotkey;
        var window = new HotkeySettingWindow(current, TryChangeHotkey, "修改全局热键");
        window.ShowDialog();
    }

    /// <summary>
    /// 尝试切换为新热键：先注册新组合，成功才落盘；若被占用则回滚到原热键，保证程序始终有一个可用的热键。
    /// </summary>
    private (bool Success, string? Error) TryChangeHotkey(string newHotkey)
    {
        var oldHotkey = _configManager!.Config.Hotkey;
        if (string.Equals(newHotkey, oldHotkey, StringComparison.OrdinalIgnoreCase))
        {
            return (true, null);
        }

        if (string.Equals(newHotkey, _configManager.Config.AddVariantHotkey, StringComparison.OrdinalIgnoreCase))
        {
            return (false, "不能和「新增全称」快捷键相同，请更换一个。");
        }

        if (string.Equals(newHotkey, _configManager.Config.EditEntryHotkey, StringComparison.OrdinalIgnoreCase))
        {
            return (false, "不能和「编辑词条」快捷键相同，请更换一个。");
        }

        bool ok = _hotkeyManager!.Register(newHotkey);
        if (!ok)
        {
            _hotkeyManager.Register(oldHotkey);
            return (false, "注册失败，该组合键可能已被其他程序占用，请更换一个。");
        }

        _configManager.Config.Hotkey = newHotkey;
        _configManager.Save();
        _trayIconManager?.ShowBalloon("术语速查", $"全局热键已更新为 {newHotkey}");
        return (true, null);
    }

    private void OpenAddVariantHotkeySettings()
    {
        var current = _configManager!.Config.AddVariantHotkey;
        var window = new HotkeySettingWindow(current, TryChangeAddVariantHotkey, "修改新增全称快捷键");
        window.ShowDialog();
    }

    /// <summary>
    /// 与全局热键不同，这是弹窗内的局部按键，不需要向系统注册，只需保证和全局热键不撞车即可。
    /// </summary>
    private (bool Success, string? Error) TryChangeAddVariantHotkey(string newHotkey)
    {
        if (string.Equals(newHotkey, _configManager!.Config.AddVariantHotkey, StringComparison.OrdinalIgnoreCase))
        {
            return (true, null);
        }

        if (string.Equals(newHotkey, _configManager.Config.Hotkey, StringComparison.OrdinalIgnoreCase))
        {
            return (false, "不能和全局热键相同，请更换一个。");
        }

        if (string.Equals(newHotkey, _configManager.Config.EditEntryHotkey, StringComparison.OrdinalIgnoreCase))
        {
            return (false, "不能和「编辑词条」快捷键相同，请更换一个。");
        }

        _configManager.Config.AddVariantHotkey = newHotkey;
        _configManager.Save();
        _trayIconManager?.ShowBalloon("术语速查", $"新增全称快捷键已更新为 {newHotkey}");
        return (true, null);
    }

    private void OpenEditEntryHotkeySettings()
    {
        var current = _configManager!.Config.EditEntryHotkey;
        var window = new HotkeySettingWindow(current, TryChangeEditEntryHotkey, "修改编辑词条快捷键");
        window.ShowDialog();
    }

    /// <summary>
    /// 同样是弹窗内的局部按键，不需要向系统注册，只需要保证和另外两个快捷键（全局热键、
    /// 新增全称快捷键）都不撞车即可。
    /// </summary>
    private (bool Success, string? Error) TryChangeEditEntryHotkey(string newHotkey)
    {
        if (string.Equals(newHotkey, _configManager!.Config.EditEntryHotkey, StringComparison.OrdinalIgnoreCase))
        {
            return (true, null);
        }

        if (string.Equals(newHotkey, _configManager.Config.Hotkey, StringComparison.OrdinalIgnoreCase))
        {
            return (false, "不能和全局热键相同，请更换一个。");
        }

        if (string.Equals(newHotkey, _configManager.Config.AddVariantHotkey, StringComparison.OrdinalIgnoreCase))
        {
            return (false, "不能和「新增全称」快捷键相同，请更换一个。");
        }

        _configManager.Config.EditEntryHotkey = newHotkey;
        _configManager.Save();
        _trayIconManager?.ShowBalloon("术语速查", $"编辑词条快捷键已更新为 {newHotkey}");
        return (true, null);
    }

    /// <summary>
    /// 查询 GitHub 上是否有新版本。<paramref name="showUpToDateMessage"/> 区分是启动时的静默自动检查
    /// （没有更新就什么都不说）还是用户手动点"检查更新..."（没有更新也要给个反馈，不能像没反应一样）。
    /// 已经有更新提示窗口开着时不会重复弹一个。
    /// </summary>
    private async Task CheckForUpdateAsync(bool showUpToDateMessage)
    {
        if (showUpToDateMessage)
        {
            _trayIconManager?.ShowBalloon("术语速查", "正在检查更新...");
        }

        var info = await UpdateChecker.CheckForUpdateAsync();

        await Dispatcher.InvokeAsync(() =>
        {
            if (info == null)
            {
                if (showUpToDateMessage)
                {
                    _trayIconManager?.ShowBalloon("术语速查", "当前已是最新版本。");
                }
                return;
            }

            if (_updateWindow != null)
            {
                _updateWindow.Activate();
                return;
            }

            _updateWindow = new UpdateAvailableWindow(info);
            _updateWindow.Closed += (_, _) => _updateWindow = null;
            _updateWindow.Show();
        });
    }

    /// <summary>
    /// terms.json / config.json 加载失败时的提醒（文件损坏、解析出错等）：数据本身已经在
    /// TermRepository/ConfigManager 里保留了上一次的旧数据继续用，这里只是让用户知道
    /// "现在用的是旧数据，文件已经坏了，需要去看一眼"，避免这种沉默失败被长期忽略。
    /// </summary>
    private void OnDataLoadFailed(string dataName, string? backupPath)
    {
        var message = backupPath != null
            ? $"{dataName}加载失败，正在使用上一次的数据。已将损坏文件备份为：{Path.GetFileName(backupPath)}"
            : $"{dataName}加载失败，正在使用上一次的数据，请检查文件是否损坏。";
        _trayIconManager?.ShowBalloon("术语速查", message, ToolTipIcon.Error);
    }

    /// <summary>
    /// 如果上次自动更新是在替换脚本里失败的（比如拷贝新 exe 时被占用/权限不足），脚本会保留
    /// 下载的安装包并在程序目录下留一个标记文件，同时把旧版本重新拉起来——这里在启动时检测
    /// 这个标记，提醒一次然后删掉，避免每次启动都重复提示。
    /// </summary>
    private void CheckPendingUpdateFailureMarker()
    {
        var markerPath = Path.Combine(AppDir, UpdateApplier.UpdateFailedMarkerFileName);
        if (!File.Exists(markerPath)) return;

        try
        {
            var tempDir = File.ReadAllText(markerPath).Trim();
            Logger.Log($"上次自动更新失败，安装包保留在：{tempDir}");
            _trayIconManager?.ShowBalloon("术语速查", $"上次自动更新失败，已恢复到当前版本。安装包保留在：{tempDir}", ToolTipIcon.Error);
        }
        catch (Exception ex)
        {
            Logger.Log($"读取更新失败标记文件出错：{ex.Message}");
        }
        finally
        {
            try { File.Delete(markerPath); } catch (Exception ex) { Logger.Log($"删除更新失败标记文件失败：{ex.Message}"); }
        }
    }

    private void OnStartWithWindowsToggled(bool enabled)
    {
        try
        {
            StartupManager.SetEnabled(enabled);
            _configManager!.Config.StartWithWindows = enabled;
            _configManager.Save();
        }
        catch (Exception ex)
        {
            Logger.Log($"设置开机自启动失败：{ex.Message}");
            _trayIconManager?.ShowBalloon("术语速查", "设置开机自启动失败，详见 termsearch.log", ToolTipIcon.Error);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeyManager?.Dispose();
        _termRepository?.Dispose();
        _trayIconManager?.Dispose();

        if (_singleInstanceMutex != null && _ownsSingleInstanceMutex)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch (Exception ex) { Logger.Log($"释放互斥体失败：{ex.Message}"); }
        }

        base.OnExit(e);
    }
}
