using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TermSearch.Services;

/// <summary>
/// 系统托盘图标与右键菜单（需求文档第 9 节）：打开术语表、重新加载、修改热键、开机自启动、退出。
/// </summary>
public class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _startupMenuItem;

    public event Action? OpenTermsRequested;
    public event Action? ReloadRequested;
    public event Action? ChangeHotkeyRequested;
    public event Action? ChangeAddVariantHotkeyRequested;
    public event Action? ChangeEditEntryHotkeyRequested;
    public event Action? CheckForUpdateRequested;
    public event Action? ExitRequested;
    public event Action<bool>? StartWithWindowsToggled;

    public TrayIconManager(bool startWithWindowsEnabled)
    {
        var menu = new ContextMenuStrip();

        var openItem = new ToolStripMenuItem("打开术语表 (terms.json)");
        openItem.Click += (_, _) => OpenTermsRequested?.Invoke();
        menu.Items.Add(openItem);

        var reloadItem = new ToolStripMenuItem("重新加载术语表");
        reloadItem.Click += (_, _) => ReloadRequested?.Invoke();
        menu.Items.Add(reloadItem);

        menu.Items.Add(new ToolStripSeparator());

        var changeHotkeyItem = new ToolStripMenuItem("修改全局热键...");
        changeHotkeyItem.Click += (_, _) => ChangeHotkeyRequested?.Invoke();
        menu.Items.Add(changeHotkeyItem);

        var changeAddVariantHotkeyItem = new ToolStripMenuItem("修改新增全称快捷键...");
        changeAddVariantHotkeyItem.Click += (_, _) => ChangeAddVariantHotkeyRequested?.Invoke();
        menu.Items.Add(changeAddVariantHotkeyItem);

        var changeEditEntryHotkeyItem = new ToolStripMenuItem("修改编辑词条快捷键...");
        changeEditEntryHotkeyItem.Click += (_, _) => ChangeEditEntryHotkeyRequested?.Invoke();
        menu.Items.Add(changeEditEntryHotkeyItem);

        menu.Items.Add(new ToolStripSeparator());

        var checkForUpdateItem = new ToolStripMenuItem("检查更新...");
        checkForUpdateItem.Click += (_, _) => CheckForUpdateRequested?.Invoke();
        menu.Items.Add(checkForUpdateItem);

        menu.Items.Add(new ToolStripSeparator());

        _startupMenuItem = new ToolStripMenuItem("开机自启动") { CheckOnClick = true, Checked = startWithWindowsEnabled };
        _startupMenuItem.Click += (_, _) => StartWithWindowsToggled?.Invoke(_startupMenuItem.Checked);
        menu.Items.Add(_startupMenuItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = CreateAppIcon(),
            Visible = true,
            Text = "术语速查 TermSearch",
            ContextMenuStrip = menu,
        };
    }

    private static Icon CreateAppIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.FromArgb(255, 43, 108, 224));
            g.FillEllipse(brush, 0, 0, 32, 32);
            using var font = new Font("Segoe UI", 16, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("T", font, textBrush, new RectangleF(0, 0, 32, 32), format);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void ShowBalloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(3000);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
