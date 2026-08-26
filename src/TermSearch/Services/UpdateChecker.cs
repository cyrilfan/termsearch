using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using TermSearch.Models;

namespace TermSearch.Services;

/// <summary>
/// 查询 GitHub 上最新 Release，判断是否有比当前版本更新的版本可用（需求文档"软件更新"相关功能）。
/// 只负责"检测"，下载和替换见 <see cref="UpdateApplier"/>。发布本身仍然是手动流程
/// （改代码 → dotnet publish → 打 zip → gh release create），这里只读取已发布的结果。
/// </summary>
public static class UpdateChecker
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/cyrilfan/termsearch/releases/latest";

    private static readonly HttpClient s_http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub API 要求请求带 User-Agent，否则会被拒绝（403）。
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TermSearch-UpdateChecker");
        return client;
    }

    /// <summary>当前程序版本，取自 csproj 里的 &lt;Version&gt;（发布新 Release 前需要同步改这个号）。</summary>
    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    /// <summary>
    /// 查询是否有新版本。网络失败、解析失败、没有可下载的 zip 附件等任何异常情况都当作
    /// "没有更新"处理（返回 null），不能因为检查更新失败就影响程序正常使用。
    /// </summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            using var response = await s_http.GetAsync(ReleasesApiUrl);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            var tagName = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
            if (!TryParseVersion(tagName, out var latestVersion)) return null;
            if (!IsNewer(latestVersion, CurrentVersion)) return null;

            string? downloadUrl = null;
            string? assetName = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
                        assetName = name;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(downloadUrl) || string.IsNullOrEmpty(assetName))
            {
                Logger.Log($"最新 Release {tagName} 没有找到可下载的 zip 附件，跳过更新提示。");
                return null;
            }

            var releaseNotes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "";

            return new UpdateInfo
            {
                TagName = tagName,
                Version = latestVersion,
                ReleaseNotes = releaseNotes,
                DownloadUrl = downloadUrl,
                AssetName = assetName,
            };
        }
        catch (Exception ex)
        {
            Logger.Log($"检查更新失败：{ex.Message}");
            return null;
        }
    }

    private static bool TryParseVersion(string tagName, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(tagName)) return false;
        var trimmed = tagName.TrimStart('v', 'V');
        return Version.TryParse(trimmed, out version!);
    }

    /// <summary>
    /// 只比较主版本号和次版本号（tag 格式是 "v0.1" "v0.2" 这种两段式，不带补丁号），
    /// 避免跟 AssemblyVersion 固定 4 段（自动补 0）之间的比较产生误判。
    /// </summary>
    private static bool IsNewer(Version latest, Version current)
    {
        if (latest.Major != current.Major) return latest.Major > current.Major;
        return latest.Minor > current.Minor;
    }
}
