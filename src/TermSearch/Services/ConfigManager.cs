using System.IO;
using System.Text.Json;
using TermSearch.Models;

namespace TermSearch.Services;

/// <summary>
/// 负责 config.json（热键、开机自启动开关）的加载与保存（需求文档第 8.3 节）。
/// </summary>
public class ConfigManager
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;

    public AppConfig Config { get; private set; } = new();

    /// <summary>加载失败时触发（文件损坏、解析出错等），不会在首次构造时触发（那时还没人订阅）。
    /// 参数是损坏文件备份后的路径；备份也失败的话是 null。</summary>
    public event Action<string?>? LoadFailed;

    public ConfigManager(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                    Config = cfg ?? new AppConfig();
                    return;
                }
            }

            Config = new AppConfig();
            Save();
        }
        catch (Exception ex)
        {
            // 不重置 Config：保留调用前那份还能用的旧配置，比直接换成默认配置更安全——
            // 首次启动时本来就是默认配置，这里"保留旧配置"自然退化成默认值，不用特殊处理。
            Logger.Log($"加载 config.json 失败，继续使用上一次的配置：{ex.Message}");
            var backupPath = TryBackupCorruptedFile();
            LoadFailed?.Invoke(backupPath);
        }
    }

    /// <summary>把解析失败的 config.json 原样复制一份留档，方便事后手动抢救内容；
    /// 不删除/移动原文件，下一次正常保存会自然把它覆盖掉。</summary>
    private string? TryBackupCorruptedFile()
    {
        try
        {
            if (!File.Exists(_filePath)) return null;
            var backupPath = $"{_filePath}.corrupted-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(_filePath, backupPath, overwrite: true);
            return backupPath;
        }
        catch (Exception ex)
        {
            Logger.Log($"备份损坏的 config.json 失败：{ex.Message}");
            return null;
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Config, JsonOptions);
            SafeFile.WriteAllTextAtomic(_filePath, json);
        }
        catch (Exception ex)
        {
            Logger.Log($"保存 config.json 失败：{ex.Message}");
        }
    }
}
