using System.IO;

namespace TermSearch.Services;

/// <summary>
/// 极简文件日志：用于在“静默失败不允许”的场景下留下可感知的痕迹（见需求文档第 12 节）。
/// 日志记录本身绝不能抛出异常影响主流程。
/// </summary>
public static class Logger
{
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "termsearch.log");
    private static readonly object Lock = new();

    public static void Log(string message)
    {
        try
        {
            lock (Lock)
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败不应影响主流程
        }
    }
}
