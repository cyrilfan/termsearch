using System.IO;
using System.Text.Json;
using TermSearch.Models;

namespace TermSearch.Services;

/// <summary>
/// 负责 terms.json 的加载、保存、前缀匹配查询，以及外部编辑后的自动热重载（需求文档第 8.1 节）。
/// </summary>
public class TermRepository : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _filePath;
    private readonly object _lock = new();
    private Dictionary<string, List<TermEntry>> _terms = new();

    private FileSystemWatcher? _watcher;
    private readonly System.Timers.Timer _debounceTimer;
    private DateTime _lastSelfWriteUtc = DateTime.MinValue;

    public event Action? TermsReloaded;

    public TermRepository(string filePath)
    {
        _filePath = filePath;

        _debounceTimer = new System.Timers.Timer(300) { AutoReset = false };
        _debounceTimer.Elapsed += (_, _) => ReloadFromWatcher();

        Load();
        SetupWatcher();
    }

    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    _terms = new Dictionary<string, List<TermEntry>>();
                    SaveInternal();
                    return;
                }

                var json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    _terms = new Dictionary<string, List<TermEntry>>();
                    return;
                }

                var data = JsonSerializer.Deserialize<Dictionary<string, List<TermEntry>>>(json, JsonOptions);
                _terms = data ?? new Dictionary<string, List<TermEntry>>();
            }
            catch (Exception ex)
            {
                Logger.Log($"加载 terms.json 失败，将使用空术语表：{ex.Message}");
                _terms = new Dictionary<string, List<TermEntry>>();
            }
        }
    }

    /// <summary>
    /// 大小写不敏感的前缀匹配。完全匹配的词条排在前面，其余按 key 字母顺序排列。
    /// </summary>
    public List<(string Key, TermEntry Entry)> FindByPrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return new List<(string, TermEntry)>();

        lock (_lock)
        {
            var results = new List<(string Key, TermEntry Entry)>();
            foreach (var kv in _terms)
            {
                if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var entry in kv.Value)
                    {
                        results.Add((kv.Key, entry));
                    }
                }
            }

            results.Sort((a, b) =>
            {
                bool aExact = string.Equals(a.Key, prefix, StringComparison.OrdinalIgnoreCase);
                bool bExact = string.Equals(b.Key, prefix, StringComparison.OrdinalIgnoreCase);
                if (aExact != bExact) return aExact ? -1 : 1;
                return string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });

            return results;
        }
    }

    /// <summary>
    /// 快捷补充流程写入新词条（需求文档第 7 节）。若 key 已存在（忽略大小写），追加到同一 key 下。
    /// </summary>
    public void AddTerm(string key, string fullName, string description)
    {
        lock (_lock)
        {
            var existingKey = _terms.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            var actualKey = existingKey ?? key;

            if (!_terms.TryGetValue(actualKey, out var list))
            {
                list = new List<TermEntry>();
                _terms[actualKey] = list;
            }

            list.Add(new TermEntry { FullName = fullName, Description = description });
            SaveInternal();
        }

        TermsReloaded?.Invoke();
    }

    private void SaveInternal()
    {
        try
        {
            _lastSelfWriteUtc = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(_terms, JsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            Logger.Log($"保存 terms.json 失败：{ex.Message}");
        }
    }

    private void SetupWatcher()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            var name = Path.GetFileName(_filePath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;

            _watcher = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
            };
            _watcher.Changed += (_, _) => RestartDebounceTimer();
            _watcher.Created += (_, _) => RestartDebounceTimer();
            _watcher.Renamed += (_, _) => RestartDebounceTimer();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Logger.Log($"监听 terms.json 变化失败：{ex.Message}");
        }
    }

    private void RestartDebounceTimer()
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void ReloadFromWatcher()
    {
        // 忽略程序自身刚刚写入触发的事件，避免重复加载（无害，但没必要）。
        if ((DateTime.UtcNow - _lastSelfWriteUtc).TotalMilliseconds < 500) return;

        Load();
        TermsReloaded?.Invoke();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounceTimer.Dispose();
    }
}
