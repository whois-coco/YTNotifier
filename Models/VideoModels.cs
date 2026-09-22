namespace YTNotifier.Models;

public enum VideoKind { Video, Short, Live, Premiere }

public class VideoInfo
{
    public string    VideoId      { get; set; } = string.Empty;
    public string    Title        { get; set; } = string.Empty;
    public string?   ThumbnailUrl { get; set; }
    public VideoKind Kind         { get; set; } = VideoKind.Video;
    /// <summary>liveBroadcastContent == "upcoming" の時 true（待機所状態）</summary>
    public bool      IsUpcoming      { get; set; } = false;
    /// <summary>liveBroadcastContent == "live" の時 true（今まさに配信中）</summary>
    public bool      IsCurrentlyLive { get; set; } = false;
    /// <summary>配信予定時刻（upcoming の場合のみ設定）</summary>
    public DateTime? ScheduledStartTime { get; set; } = null;
    /// <summary>配信実際の開始時刻（配信中・アーカイブの場合に設定）</summary>
    public DateTime? ActualStartTime    { get; set; } = null;
    /// <summary>動画の再生時間（取得できない場合は null）</summary>
    public TimeSpan? Duration    { get; set; } = null;
    /// <summary>動画の投稿日時（プレイリストアイテムの snippet.publishedAt）</summary>
    public DateTime? PublishedAt { get; set; } = null;
    /// <summary>通知済み記録との照合を行わない（意図的な再通知）場合 true</summary>
    public bool      SkipDuplicateCheck { get; set; } = false;
    public string KindLabel => Kind switch
    {
        VideoKind.Short    => "Short",
        // 配信中・予定（upcoming）は「ライブ」、配信終了済み（録画）は「アーカイブ」
        VideoKind.Live     => (IsCurrentlyLive || IsUpcoming) ? "ライブ" : "アーカイブ",
        VideoKind.Premiere => "プレミア",
        _                  => "動画"
    };
}

/// <summary>
/// チャンネル1件の巡回で取得した結果。Videos は削除された動画の復帰時に後続の新着処理へ差し込むため、変更可能なリストのまま保持する。
/// </summary>
public sealed record ChannelScanResult(
    List<VideoInfo> Videos,
    List<VideoInfo> PendingTransitioned,
    List<VideoInfo> AllScanned,
    List<VideoInfo> AllScannedBasic,
    bool PlaylistEmpty)
{
    /// <summary>
    /// 空の巡回結果を作る。呼ぶたびに4つの空リストを新しく作る（Videos は呼び出し側が要素を追加するため、共有しない）。
    /// </summary>
    public static ChannelScanResult CreateEmpty(bool playlistEmpty = false)
    {
        return new ChannelScanResult(
            new List<VideoInfo>(),
            new List<VideoInfo>(),
            new List<VideoInfo>(),
            new List<VideoInfo>(),
            playlistEmpty);
    }
}

public sealed record SchedulerAction(
    ChannelInfo      Channel,
    PendingVideoEntry Entry,
    DateTime          ActionAt,
    bool              IsNotification);

// 新着ループの通知候補を1つのレコードにまとめる
// Video にはプレミア公開後の動画も含む（NotifyVideo で一元管理）
public sealed record NewVideoNotifyCandidates(
    VideoInfo? Video,
    VideoInfo? Short,
    IReadOnlyList<VideoInfo> Lives,
    bool LatestLiveSeen,
    bool LatestPremiereSeen);
