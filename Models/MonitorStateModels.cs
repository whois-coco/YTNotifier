using Newtonsoft.Json;

namespace YTNotifier.Models;

/// <summary>チャンネルの最新投稿スナップショット1件分の情報（右クリック一覧表示用）</summary>
public class RecentUploadEntry
{
    [JsonProperty("videoId")]
    public string    VideoId      { get; set; } = string.Empty;

    [JsonProperty("title")]
    public string    Title        { get; set; } = string.Empty;

    [JsonProperty("thumbnailUrl")]
    public string?   ThumbnailUrl { get; set; }

    [JsonProperty("kind")]
    public VideoKind Kind         { get; set; } = VideoKind.Video;

    [JsonProperty("duration")]
    public TimeSpan? Duration     { get; set; } = null;

    [JsonProperty("publishedAt")]
    public DateTime? PublishedAt  { get; set; } = null;
}

/// <summary>upcoming 待ち中のライブ/プレミア1件分の情報</summary>
public class PendingVideoEntry
{
    [JsonProperty("videoId")]
    public string VideoId { get; set; } = string.Empty;

    [JsonProperty("scheduledAt")]
    public DateTime? ScheduledAt { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("thumbnailUrl")]
    public string? ThumbnailUrl { get; set; }

    /// <summary>待機所通知送信済みフラグ（X分前通知の二重送信防止）</summary>
    [JsonProperty("upcomingNotified")]
    public bool UpcomingNotified { get; set; } = false;

    /// <summary>開始時刻到達後の猶予チェック残回数</summary>
    [JsonProperty("graceRemaining")]
    public int GraceRemaining { get; set; } = 0;

    /// <summary>配信・公開の実際の開始時刻（ActiveLives/ActivePremieres でのみ使用）</summary>
    [JsonProperty("actualStartTime")]
    public DateTime? ActualStartTime { get; set; }
}

/// <summary>チャンネルごとの実行状態（巡回カーソル・通知済み記録・待機リスト・次回チェック時刻など）。
/// ChannelInfo.State として実行時に保持し、state.json で管理する（channels.json には出さない）。
/// 項目名・宣言順は state.json の出力形式そのものなので変更しない</summary>
public class ChannelState
{
    /// <summary>プレイリスト走査カーソル（全種別共通）</summary>
    [JsonProperty("lastCheckedVideoId")]
    public string LastCheckedVideoId { get; set; } = string.Empty;

    /// <summary>LastCheckedVideoId が指す動画自身の投稿日時（新着検出の基準線）</summary>
    [JsonProperty("lastCheckedVideoPublishedAt")]
    public DateTime? LastCheckedVideoPublishedAt { get; set; } = null;

    /// <summary>次回チェック予定時刻</summary>
    [JsonProperty("nextCheckAt")]
    public DateTime NextCheckAt { get; set; } = DateTime.MinValue;

    [JsonProperty("lastCheckedAt")]
    public DateTime LastCheckedAt { get; set; } = DateTime.MinValue;

    /// <summary>アップロードプレイリストID（channels.list から取得）</summary>
    [JsonProperty("uploadsPlaylistId")]
    public string UploadsPlaylistId { get; set; } = string.Empty;

    // upcoming 待ちリスト
    private List<PendingVideoEntry> _pendingLives = new();
    [JsonProperty("pendingLives")]
    public List<PendingVideoEntry> PendingLives
    {
        get => _pendingLives;
        set => _pendingLives = value ?? new();
    }

    private List<PendingVideoEntry> _pendingPremieres = new();
    [JsonProperty("pendingPremieres")]
    public List<PendingVideoEntry> PendingPremieres
    {
        get => _pendingPremieres;
        set => _pendingPremieres = value ?? new();
    }

    // 配信中ライブ
    private List<PendingVideoEntry> _activeLives = new();
    [JsonProperty("activeLives")]
    public List<PendingVideoEntry> ActiveLives
    {
        get => _activeLives;
        set => _activeLives = value ?? new();
    }

    // 公開中プレミア
    private List<PendingVideoEntry> _activePremieres = new();
    [JsonProperty("activePremieres")]
    public List<PendingVideoEntry> ActivePremieres
    {
        get => _activePremieres;
        set => _activePremieres = value ?? new();
    }

    /// <summary>ライブ通知済みVideoId</summary>
    [JsonProperty("lastLiveNotifiedId")]
    public string LastLiveNotifiedId { get; set; } = string.Empty;

    /// <summary>プレミア通知済みVideoId</summary>
    [JsonProperty("lastPremiereNotifiedId")]
    public string LastPremiereNotifiedId { get; set; } = string.Empty;

    // ===== v1→v2 マイグレーション専用フィールド =====
    [JsonProperty("lastLiveId")]
    public string LastLiveId { get; set; } = string.Empty;

    [JsonProperty("lastPremiereId")]
    public string LastPremiereId { get; set; } = string.Empty;

    [JsonProperty("nextLiveCheckAt")]
    public DateTime? NextLiveCheckAt { get; set; }

    [JsonProperty("nextPremiereCheckAt")]
    public DateTime? NextPremiereCheckAt { get; set; }

    [JsonProperty("liveGraceRemaining")]
    public int LiveGraceRemaining { get; set; } = 0;
    // ===== /v1→v2 マイグレーション専用フィールド =====

    /// <summary>最後に通知した動画タイトル</summary>
    [JsonProperty("lastVideoTitle")]
    public string LastVideoTitle { get; set; } = string.Empty;

    /// <summary>動画通知日時</summary>
    [JsonProperty("lastVideoNotifiedAt")]
    public DateTime? LastVideoNotifiedAt { get; set; }

    /// <summary>最後に通知したShortのVideoId</summary>
    [JsonProperty("lastShortNotifiedId")]
    public string LastShortNotifiedId { get; set; } = string.Empty;

    /// <summary>最後に通知したShortタイトル</summary>
    [JsonProperty("lastShortTitle")]
    public string LastShortTitle { get; set; } = string.Empty;

    /// <summary>Short通知日時</summary>
    [JsonProperty("lastShortNotifiedAt")]
    public DateTime? LastShortNotifiedAt { get; set; }

    /// <summary>最後に通知したライブタイトル</summary>
    [JsonProperty("lastLiveNotifiedTitle")]
    public string LastLiveNotifiedTitle { get; set; } = string.Empty;

    /// <summary>ライブ通知日時</summary>
    [JsonProperty("lastLiveNotifiedAt")]
    public DateTime? LastLiveNotifiedAt { get; set; }

    /// <summary>最後に通知したプレミアタイトル</summary>
    [JsonProperty("lastPremiereNotifiedTitle")]
    public string LastPremiereNotifiedTitle { get; set; } = string.Empty;

    /// <summary>プレミア通知日時</summary>
    [JsonProperty("lastPremiereNotifiedAt")]
    public DateTime? LastPremiereNotifiedAt { get; set; }

    [JsonProperty("latestTitle")]
    public string?    LatestTitle { get; set; }

    [JsonProperty("latestKind")]
    public VideoKind? LatestKind  { get; set; }

    [JsonProperty("latestVideoId")]
    public string?    LatestVideoId { get; set; }

    [JsonProperty("latestDuration")]
    public TimeSpan?  LatestDuration { get; set; }

    [JsonProperty("latestThumbnailUrl")]
    public string?    LatestThumbnailUrl { get; set; }

    private List<RecentUploadEntry> _recentUploads = new();
    /// <summary>チャンネルの最新投稿スナップショット（右クリック一覧表示用）。
    /// state.json には出さず、別ファイル（recent_uploads.json.gz）で管理する</summary>
    [JsonIgnore]
    public List<RecentUploadEntry> RecentUploads
    {
        get => _recentUploads;
        set => _recentUploads = value ?? new();
    }

    private System.Collections.Concurrent.ConcurrentDictionary<string, VideoKind> _videoKindCache = new();
    /// <summary>動画種別判定（フェーズ4＝ショート判定）の確定済み結果。動画ID → 種別。
    /// 種別は一度確定すれば変わらないため再問い合わせを省略する。並列判定から書き込むため ConcurrentDictionary を使う</summary>
    [JsonProperty("videoKindCache")]
    public System.Collections.Concurrent.ConcurrentDictionary<string, VideoKind> VideoKindCache
    {
        get => _videoKindCache;
        set => _videoKindCache = value ?? new();
    }

    /// <summary>チャンネルBAN／自主削除が確定した場合 true</summary>
    [JsonProperty("isBanned")]
    public bool IsBanned { get; set; } = false;

    /// <summary>表示中の最新動画（LatestVideoId）が削除されたことが確定した場合 true</summary>
    [JsonProperty("latestVideoDeleted")]
    public bool LatestVideoDeleted { get; set; } = false;

    /// <summary>過去に動画が存在したチャンネルの投稿がすべて確認できなくなった場合 true</summary>
    [JsonProperty("noVideosFound")]
    public bool NoVideosFound { get; set; } = false;
}

/// <summary>アプリ全体の実行状態（state.json で管理）</summary>
public class AppState
{
    [JsonProperty("todayApiUnits")]
    public int TodayApiUnits { get; set; } = 0;

    [JsonProperty("todayApiDate")]
    public string TodayApiDate { get; set; } = string.Empty;

    [JsonProperty("todayApiUnitsPendingTrack")]
    public int TodayApiUnitsPendingTrack { get; set; } = 0;

    [JsonProperty("todayApiUnitsLiveStatus")]
    public int TodayApiUnitsLiveStatus { get; set; } = 0;

    [JsonProperty("todayGeminiRequests")]
    public int TodayGeminiRequests { get; set; } = 0;

    [JsonProperty("todayGeminiRequestDate")]
    public string TodayGeminiRequestDate { get; set; } = string.Empty;

    [JsonProperty("channels")]
    public Dictionary<string, ChannelState> Channels { get; set; } = new();

    [JsonProperty("quotaSuspendedUntil")]
    public DateTime? QuotaSuspendedUntil { get; set; } = null;
}
