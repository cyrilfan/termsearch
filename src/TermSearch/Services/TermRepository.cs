using System.IO;
using System.Text.Json;
using TermSearch.Models;

namespace TermSearch.Services;

/// <summary>匹配档位：全部（精确）匹配 &gt; 前缀匹配 &gt; 模糊匹配，用于结果排序分组。</summary>
public enum MatchTier
{
    Exact = 0,
    Prefix = 1,
    Fuzzy = 2,
}

/// <summary>一条查询结果：命中的缩写、词条、匹配档位，以及（模糊匹配时）命中字符位置和匹配得分。</summary>
public class SearchResult
{
    public required string Key { get; init; }
    public required TermEntry Entry { get; init; }
    public MatchTier Tier { get; init; }
    public int[] MatchedIndices { get; init; } = Array.Empty<int>();
    public int Score { get; init; }
}

/// <summary>
/// 负责 terms.json 的加载、保存、查询（全部/前缀/模糊三档匹配），以及外部编辑后的自动热重载（需求文档第 8.1 节）。
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

    /// <summary>加载失败时触发（文件损坏、解析出错等），不会在首次构造时触发（那时还没人订阅）。
    /// 参数是损坏文件备份后的路径；备份也失败的话是 null。</summary>
    public event Action<string?>? LoadFailed;

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
                // 不重置 _terms：保留调用前那份还能用的旧数据，比直接清空成空术语表更安全——
                // 首次启动时文件本来就是空的，这里"保留旧数据"自然退化成空表，不用特殊处理。
                Logger.Log($"加载 terms.json 失败，继续使用上一次的数据：{ex.Message}");
                var backupPath = TryBackupCorruptedFile();
                LoadFailed?.Invoke(backupPath);
            }
        }
    }

    /// <summary>把解析失败的 terms.json 原样复制一份留档（比如 terms.json.corrupted-20260831-153000），
    /// 方便事后手动抢救内容；不删除/移动原文件，下一次正常保存会自然把它覆盖掉。</summary>
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
            Logger.Log($"备份损坏的 terms.json 失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 大小写不敏感的三档匹配：全部（精确）匹配 &gt; 前缀匹配 &gt; 模糊匹配（子序列匹配，按匹配质量得分排序）。
    /// 每个缩写只归入其中最高的一档，不会在多档重复出现。
    /// </summary>
    public List<SearchResult> Search(string query)
    {
        if (string.IsNullOrEmpty(query)) return new List<SearchResult>();

        lock (_lock)
        {
            var exact = new List<SearchResult>();
            var prefix = new List<SearchResult>();
            var fuzzy = new List<SearchResult>();

            foreach (var kv in _terms)
            {
                var key = kv.Key;
                MatchTier tier;
                int[] matchedIndices;
                int score = 0;

                if (string.Equals(key, query, StringComparison.OrdinalIgnoreCase))
                {
                    tier = MatchTier.Exact;
                    matchedIndices = Enumerable.Range(0, key.Length).ToArray();
                }
                else if (key.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                {
                    tier = MatchTier.Prefix;
                    matchedIndices = Enumerable.Range(0, query.Length).ToArray();
                }
                else if (TryFuzzyMatch(key, query, out matchedIndices, out score))
                {
                    tier = MatchTier.Fuzzy;
                }
                else
                {
                    continue;
                }

                var bucket = tier switch
                {
                    MatchTier.Exact => exact,
                    MatchTier.Prefix => prefix,
                    _ => fuzzy,
                };

                foreach (var entry in kv.Value)
                {
                    bucket.Add(new SearchResult
                    {
                        Key = key,
                        Entry = entry,
                        Tier = tier,
                        MatchedIndices = matchedIndices,
                        Score = score,
                    });
                }
            }

            exact.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
            prefix.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
            fuzzy.Sort((a, b) => b.Score != a.Score
                ? b.Score - a.Score
                : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));

            var results = new List<SearchResult>(exact.Count + prefix.Count + fuzzy.Count);
            results.AddRange(exact);
            results.AddRange(prefix);
            results.AddRange(fuzzy);
            return results;
        }
    }

    /// <summary>
    /// 子序列模糊匹配（类似 fzf/Sublime 命令面板）：query 的每个字符需按顺序（可跳过中间字符）出现在 key 里。
    /// 命中字符越连续、起始位置越靠前、key 本身越短，得分越高，用于同档内排序。
    /// </summary>
    private static bool TryFuzzyMatch(string key, string query, out int[] matchedIndices, out int score)
    {
        matchedIndices = Array.Empty<int>();
        score = 0;

        if (query.Length == 0 || query.Length > key.Length) return false;

        var indices = new int[query.Length];
        int keyIdx = 0;
        for (int qIdx = 0; qIdx < query.Length; qIdx++)
        {
            char qc = char.ToUpperInvariant(query[qIdx]);
            bool found = false;
            while (keyIdx < key.Length)
            {
                if (char.ToUpperInvariant(key[keyIdx]) == qc)
                {
                    indices[qIdx] = keyIdx;
                    keyIdx++;
                    found = true;
                    break;
                }
                keyIdx++;
            }
            if (!found) return false;
        }

        matchedIndices = indices;

        int consecutiveBonus = 0;
        for (int i = 1; i < indices.Length; i++)
        {
            if (indices[i] == indices[i - 1] + 1) consecutiveBonus += 3;
        }

        score = (consecutiveBonus * 10) - indices[0] - key.Length;
        return true;
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

    /// <summary>
    /// 编辑某个已存在词条的全称/解释（原地替换，不新增条目）。用查询结果里那个具体的
    /// <see cref="TermEntry"/> 对象引用来定位要改哪一条，而不是按内容匹配——这样即使同一
    /// 缩写下有两条内容完全一样的记录，改的也一定是用户当时选中的那一条，不会认错。
    /// 如果这期间术语表被外部编辑过（引用已经不在当前数据里了），返回 false，不做任何改动。
    /// </summary>
    public bool UpdateTerm(string key, TermEntry originalEntry, string newFullName, string newDescription)
    {
        lock (_lock)
        {
            var existingKey = _terms.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (existingKey == null || !_terms.TryGetValue(existingKey, out var list))
            {
                return false;
            }

            var index = list.FindIndex(e => ReferenceEquals(e, originalEntry));
            if (index < 0)
            {
                return false;
            }

            list[index] = new TermEntry { FullName = newFullName, Description = newDescription };
            SaveInternal();
        }

        TermsReloaded?.Invoke();
        return true;
    }

    private void SaveInternal()
    {
        try
        {
            _lastSelfWriteUtc = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(_terms, JsonOptions);
            SafeFile.WriteAllTextAtomic(_filePath, json);
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
