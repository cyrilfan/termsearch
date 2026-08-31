using System.IO;

namespace TermSearch.Services;

/// <summary>
/// terms.json / config.json 的原子写入：先写到同目录下的临时文件，再用 File.Move(overwrite) 整体替换正式文件。
/// 保存过程中如果程序崩溃/断电/被杀，正式文件要么完全没被动过，要么已经是替换后的新内容，
/// 不会出现"写到一半的半截 JSON"。
/// </summary>
public static class SafeFile
{
    public static void WriteAllTextAtomic(string path, string content)
    {
        var tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, content);
        File.Move(tmpPath, path, overwrite: true);
    }
}
