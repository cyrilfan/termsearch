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
            Logger.Log($"加载 config.json 失败，将使用默认配置：{ex.Message}");
            Config = new AppConfig();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Config, JsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            Logger.Log($"保存 config.json 失败：{ex.Message}");
        }
    }
}
