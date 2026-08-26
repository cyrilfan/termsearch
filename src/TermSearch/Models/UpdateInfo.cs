namespace TermSearch.Models;

/// <summary>从 GitHub Release 查到的、比当前版本新的更新信息。</summary>
public class UpdateInfo
{
    public required string TagName { get; init; }
    public required Version Version { get; init; }
    public string ReleaseNotes { get; init; } = "";
    public required string DownloadUrl { get; init; }
    public required string AssetName { get; init; }
}
