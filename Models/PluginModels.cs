using YTNotifier.Services;
using Newtonsoft.Json;

namespace YTNotifier.Models;

/// <summary>プラグイン間口 segments 結果の1項目</summary>
public class PluginSegmentItem
{
    [JsonProperty("start")] public int    Start { get; set; }
    [JsonProperty("title")] public string Title { get; set; } = string.Empty;
    [JsonProperty("body")]  public string Body  { get; set; } = string.Empty;
}

/// <summary>プラグイン間口 segments 結果</summary>
public class PluginSegmentsResult
{
    [JsonProperty("items")] public List<PluginSegmentItem> Items { get; set; } = new();
}

/// <summary>プラグイン間口 text 結果</summary>
public class PluginTextResult
{
    [JsonProperty("headline")] public string? Headline { get; set; }
    [JsonProperty("detail")]   public string  Detail   { get; set; } = string.Empty;
}

/// <summary>手動での更新確認結果</summary>
public enum UpdateCheckStatus { UpdateFound, UpToDate, Failed }

/// <summary>手動での更新確認結果（判定結果と、UpdateFound時の最新タグ）</summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, string? LatestTag);

/// <summary>GitHub Releases 1件分のリリースノート（タグとMarkdown本文）</summary>
public sealed record ReleaseNotes(string Tag, string? Body);

/// <summary>アップデート用アセット情報（ダウンロードURLとSHA256ダイジェスト）</summary>
public sealed record UpdateAssetInfo(string Tag, string DownloadUrl, string? Sha256Digest);

/// <summary><see cref="PluginBridge.InvokeAsync"/> の結果。</summary>
/// <param name="Output">成功時は結果の JSON 文字列、失敗時は <c>null</c>。</param>
/// <param name="HostUnavailable">ホストを起動できなかった（または待ち時間内に接続が完了しなかった）場合に <c>true</c>。</param>
public sealed record PluginInvokeResult(string? Output, bool HostUnavailable);

/// <summary>Gemini 動画要約の結果</summary>
public class GeminiSummaryResult
{
    public bool    Success      { get; init; }
    public string? Headline     { get; init; }
    public string? Detail       { get; init; }
    public string? ErrorMessage { get; init; }
}
