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
    private EventWaitHandle? _showPopupEvent;

    private TermRepository? _termRepository;
    private ConfigManager? _configManager;
    private HotkeyManager? _hotkeyManager;
    private TrayIconManager? _trayIconManager;
    private PopupWindow? _popupWindow;

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
        _popupWindow = new PopupWindow(_termRepository);

        _hotkeyManager = new HotkeyManager();
        _hotkeyManager.HotkeyPressed += () => Dispatcher.Invoke(ShowPopup);
        RegisterHotkeyFromConfig();

        _trayIconManager = new TrayIconManager(_configManager.Config.StartWithWindows);
        _trayIconManager.OpenTermsRequested += OpenTermsFile;
        _trayIconManager.ReloadRequested += ReloadTerms;
        _trayIconManager.ChangeHotkeyRequested += OpenHotkeySettings;
        _trayIconManager.StartWithWindowsToggled += OnStartWithWindowsToggled;
        _trayIconManager.ExitRequested += () => Shutdown();
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
        var window = new HotkeySettingWindow(current, TryChangeHotkey);
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

        if (_singleInstanceMutex != null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch (Exception ex) { Logger.Log($"释放互斥体失败：{ex.Message}"); }
        }

        base.OnExit(e);
    }
}
