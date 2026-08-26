using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using TermSearch.Models;

namespace TermSearch.Services;

/// <summary>
/// 下载新版本并完成"替换正在运行的 exe"这个自更新场景里最棘手的部分：Windows 下正在运行的
/// exe 不能覆盖自己。做法是下载解压出新 exe 后，生成一个独立的批处理脚本，脚本等本进程退出
/// 后再把新 exe 覆盖到当前安装位置、重新启动、然后自我删除——本进程自己只需要负责启动这个
/// 脚本，然后正常退出。全程只碰 exe 本身，绝不动 terms.json / config.json（用户数据）。
/// </summary>
public static class UpdateApplier
{
    /// <summary>下载 Release 里的 zip 附件并解压出其中的 TermSearch.exe，返回解压出的路径；失败返回 null。</summary>
    public static async Task<string?> DownloadAndExtractAsync(UpdateInfo info, IProgress<double>? progress = null)
    {
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "TermSearchUpdate_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var zipPath = Path.Combine(tempDir, info.AssetName);

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("TermSearch-UpdateChecker");

                using var response = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? -1L;
                await using var contentStream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = File.Create(zipPath);

                var buffer = new byte[81920];
                long readTotal = 0;
                int read;
                while ((read = await contentStream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read));
                    readTotal += read;
                    if (total > 0)
                    {
                        progress?.Report((double)readTotal / total);
                    }
                }
            }

            var extractDir = Path.Combine(tempDir, "extracted");
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            var exePath = Directory.GetFiles(extractDir, "TermSearch.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exePath == null)
            {
                Logger.Log($"下载的更新包 {info.AssetName} 里没有找到 TermSearch.exe。");
            }
            return exePath;
        }
        catch (Exception ex)
        {
            Logger.Log($"下载/解压新版本失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 启动一个独立的替换脚本并让它在后台等待，调用方随后应自行退出程序（比如
    /// Application.Current.Shutdown()），退出后脚本会接手完成覆盖、重启。
    /// </summary>
    public static void LaunchReplaceAndRestart(string newExePath, string currentExePath)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), "TermSearchUpdate_" + Guid.NewGuid().ToString("N") + ".bat");
        int pid = Environment.ProcessId;

        // chcp 65001 切到 UTF-8 代码页，保证路径里如果有中文（比如装在带中文的文件夹下）不会乱码。
        var script =
$"""
@echo off
chcp 65001 >nul
:wait
tasklist /FI "PID eq {pid}" 2>NUL | find "{pid}" >NUL
if not errorlevel 1 (
  timeout /t 1 /nobreak >nul
  goto wait
)
copy /y "{newExePath}" "{currentExePath}" >nul
start "" "{currentExePath}"
del "%~f0"
""";

        File.WriteAllText(scriptPath, script, new System.Text.UTF8Encoding(false));

        Process.Start(new ProcessStartInfo
        {
            FileName = scriptPath,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
        });
    }
}
