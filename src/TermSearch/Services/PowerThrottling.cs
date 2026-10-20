using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TermSearch.Services;

public static class PowerThrottling
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr hProcess, int informationClass, ref ProcessPowerThrottlingState info, int size);

    private const int ProcessPowerThrottlingClass = 4;
    private const uint CurrentVersion = 1;
    private const uint ExecutionSpeed = 1;

    /// <summary>
    /// 热键工具平时没有可见窗口，Windows 在电池供电时会把这类后台进程降速，导致按热键后迟迟弹不出来。
    /// ControlMask 置位且 StateMask 为 0 即显式声明"不要对我节流"；旧系统上调用失败无害，记日志即可。
    /// </summary>
    public static void OptOut()
    {
        var state = new ProcessPowerThrottlingState { Version = CurrentVersion, ControlMask = ExecutionSpeed, StateMask = 0 };
        if (!SetProcessInformation(Process.GetCurrentProcess().Handle, ProcessPowerThrottlingClass, ref state, Marshal.SizeOf<ProcessPowerThrottlingState>()))
        {
            Logger.Log($"关闭电源节流失败（旧版 Windows 不支持属正常）：{Marshal.GetLastWin32Error()}");
        }
    }
}
