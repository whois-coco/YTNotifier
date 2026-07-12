using YTNotifier.Constants;
using YTNotifier.Services;
using Newtonsoft.Json;

namespace YTNotifier.Models;

public class AppSettings
{
    [Newtonsoft.Json.JsonIgnore]
    public string ApiKey { get; set; } = string.Empty;

    [JsonProperty("isDarkMode")]
    public bool IsDarkMode { get; set; } = false;

    /// <summary>ONにするとカテゴリヘッダーを非表示にして全チャンネルをフラット表示する</summary>
    [JsonProperty("noCategoryMode")]
    public bool NoCategoryMode { get; set; } = false;

    [JsonProperty("checkIntervalMinutes")]
    public int CheckIntervalMinutes { get; set; } = 5;

    [JsonProperty("showDesktopNotification")]
    public bool ShowDesktopNotification { get; set; } = true;

    /// <summary>トースト通知スタイル</summary>
    [JsonProperty("toastStyle")]
    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public ToastStyle ToastStyle { get; set; } = ToastStyle.Standard;

    /// <summary>全チャンネル共通: 待機所（upcoming）通知のグローバルON/OFF（廃止・移行用に読み取りのみ）</summary>
    [JsonProperty("globalNotifyUpcoming")]
    public bool GlobalNotifyUpcoming { get; set; } = true;

    /// <summary>UpcomingNotifyMode へのマイグレーション完了フラグ</summary>
    [JsonProperty("upcomingMigrated")]
    public bool UpcomingMigrated { get; set; } = false;

    [JsonProperty("minimizeToTray")]
    public bool MinimizeToTray { get; set; } = false;

    [JsonProperty("startWithWindows")]
    public bool StartWithWindows { get; set; } = false;

    [JsonProperty("logLevel")]
    public string LogLevel { get; set; } = "Info";

    // ウィンドウサイズ・位置
    [JsonProperty("windowWidth")]
    public double WindowWidth { get; set; } = 1000;

    [JsonProperty("windowHeight")]
    public double WindowHeight { get; set; } = 740;

    [JsonProperty("windowLeft")]
    public double WindowLeft { get; set; } = -1;

    [JsonProperty("windowTop")]
    public double WindowTop { get; set; } = -1;

    [JsonProperty("windowMaximized")]
    public bool WindowMaximized { get; set; } = false;

    [JsonProperty("alwaysOnTop")]
    public bool AlwaysOnTop { get; set; } = false;

    [JsonProperty("notificationSound")]
    public bool NotificationSound { get; set; } = true;

    [JsonProperty("isMuted")]
    public bool IsMuted { get; set; } = false;

    [JsonProperty("flashTaskbar")]
    public bool FlashTaskbar { get; set; } = true;

    [JsonProperty("preMuteDesktopNotification")]
    public bool PreMuteDesktopNotification { get; set; } = false;

    [JsonProperty("preMuteNotificationSound")]
    public bool PreMuteNotificationSound { get; set; } = false;

    [JsonProperty("preMuteFlashTaskbar")]
    public bool PreMuteFlashTaskbar { get; set; } = false;

    [JsonProperty("compactMode")]
    public bool CompactMode { get; set; } = false;

    [JsonProperty("sidebarCollapsed")]
    public bool SidebarCollapsed { get; set; } = false;

    [JsonProperty("logRetentionDays")]
    public int LogRetentionDays { get; set; } = 30;

    [JsonProperty("autoCleanLogs")]
    public bool AutoCleanLogs { get; set; } = false;

    [JsonProperty("continuousAddMode")]
    public bool ContinuousAddMode { get; set; } = true;  // 連続追加モード（デフォルトON）

    // 当日のAPI実使用量追跡（state.json で管理）
    [Newtonsoft.Json.JsonIgnore]
    public int    TodayApiUnits { get; set; } = 0;
    [Newtonsoft.Json.JsonIgnore]
    public string TodayApiDate  { get; set; } = "";
    [JsonProperty("lastStartupCheckDate")]
    public string LastStartupCheckDate { get; set; } = "";
    [JsonProperty("lastDailyFullScanDate")]
    public string LastDailyFullScanDate { get; set; } = "";
}

public class ChannelInfo
{
    [JsonProperty("channelId")]
    public string ChannelId { get; set; } = string.Empty;

    [JsonProperty("channelName")]
    public string ChannelName { get; set; } = string.Empty;

    [JsonProperty("channelHandle")]
    public string ChannelHandle { get; set; } = string.Empty;

    [JsonProperty("thumbnailUrl")]
    public string ThumbnailUrl { get; set; } = string.Empty;

    [JsonProperty("subscriberCount")]
    public string SubscriberCount { get; set; } = string.Empty;

    [JsonProperty("isFavorite")]
    public bool IsFavorite { get; set; } = false;

    /// <summary>プレイリスト走査カーソル（全種別共通）。state.json で管理</summary>
    [JsonIgnore]
    public string LastCheckedVideoId { get; set; } = string.Empty;

    /// <summary>LastCheckedVideoId が指す動画自身の投稿日時（新着検出の基準線）。state.json で管理</summary>
    [JsonIgnore]
    public DateTime? LastCheckedVideoPublishedAt { get; set; } = null;

    // ===== upcoming 待ちリスト（state.json で管理）=====
    private List<PendingVideoEntry> _pendingLives = new();
    [JsonIgnore]
    public List<PendingVideoEntry> PendingLives
    {
        get => _pendingLives;
        set => _pendingLives = value ?? new();
    }

    private List<PendingVideoEntry> _pendingPremieres = new();
    [JsonIgnore]
    public List<PendingVideoEntry> PendingPremieres
    {
        get => _pendingPremieres;
        set => _pendingPremieres = value ?? new();
    }

    /// <summary>配信中ライブ。state.json で管理</summary>
    [JsonIgnore]
    public List<PendingVideoEntry> ActiveLives { get; set; } = new();

    /// <summary>公開中プレミア。state.json で管理</summary>
    [JsonIgnore]
    public List<PendingVideoEntry> ActivePremieres { get; set; } = new();

    /// <summary>ライブ通知済みVideoId。state.json で管理</summary>
    [JsonIgnore]
    public string LastLiveNotifiedId { get; set; } = string.Empty;

    /// <summary>プレミア通知済みVideoId。state.json で管理</summary>
    [JsonIgnore]
    public string LastPremiereNotifiedId { get; set; } = string.Empty;

    [JsonIgnore]
    public DateTime LastCheckedAt { get; set; } = DateTime.MinValue;

    // ===== v1→v2 マイグレーション専用フィールド（state.json で管理）=====
    [JsonIgnore]
    public string LastLiveId { get; set; } = string.Empty;

    [JsonIgnore]
    public string LastPremiereId { get; set; } = string.Empty;

    [JsonIgnore]
    public DateTime? NextLiveCheckAt { get; set; } = null;

    [JsonIgnore]
    public int LiveGraceRemaining { get; set; } = 0;

    [JsonIgnore]
    public DateTime? NextPremiereCheckAt { get; set; } = null;
    // ===== /v1→v2 マイグレーション専用フィールド =====

    /// <summary>最後に通知した動画タイトル。state.json で管理</summary>
    [JsonIgnore]
    public string LastVideoTitle { get; set; } = string.Empty;

    /// <summary>動画通知日時。state.json で管理</summary>
    [JsonIgnore]
    public DateTime? LastVideoNotifiedAt { get; set; }

    /// <summary>最後に通知したShortのVideoId。state.json で管理</summary>
    [JsonIgnore]
    public string LastShortNotifiedId { get; set; } = string.Empty;

    /// <summary>最後に通知したShortタイトル。state.json で管理</summary>
    [JsonIgnore]
    public string LastShortTitle { get; set; } = string.Empty;

    /// <summary>Short通知日時。state.json で管理</summary>
    [JsonIgnore]
    public DateTime? LastShortNotifiedAt { get; set; }

    /// <summary>最後に通知したライブタイトル。state.json で管理</summary>
    [JsonIgnore]
    public string LastLiveNotifiedTitle { get; set; } = string.Empty;

    /// <summary>ライブ通知日時。state.json で管理</summary>
    [JsonIgnore]
    public DateTime? LastLiveNotifiedAt { get; set; }

    /// <summary>最後に通知したプレミアタイトル。state.json で管理</summary>
    [JsonIgnore]
    public string LastPremiereNotifiedTitle { get; set; } = string.Empty;

    /// <summary>プレミア通知日時。state.json で管理</summary>
    [JsonIgnore]
    public DateTime? LastPremiereNotifiedAt { get; set; }

    [JsonIgnore]
    public string?    LatestTitle { get; set; }

    [JsonIgnore]
    public VideoKind? LatestKind  { get; set; }

    [JsonIgnore]
    public string?    LatestVideoId { get; set; }

    [JsonIgnore]
    public TimeSpan?  LatestDuration { get; set; }

    [JsonIgnore]
    public string?    LatestThumbnailUrl { get; set; }

    /// <summary>チャンネルBAN／自主削除が確定した場合 true。state.json で管理</summary>
    [JsonIgnore]
    public bool IsBanned { get; set; } = false;

    /// <summary>表示中の最新動画（LatestVideoId）が削除されたことが確定した場合 true。state.json で管理</summary>
    [JsonIgnore]
    public bool LatestVideoDeleted { get; set; } = false;

    /// <summary>過去に動画が存在したチャンネルの投稿がすべて確認できなくなった場合 true。state.json で管理</summary>
    [JsonIgnore]
    public bool NoVideosFound { get; set; } = false;

    [JsonProperty("isEnabled")]
    public bool IsEnabled { get; set; } = true;

    [JsonProperty("addedAt")]
    public DateTime AddedAt { get; set; } = DateTime.Now;

    // 未読フラグ（新着動画があった場合にtrue、クリックでfalseに戻る）
    [JsonProperty("hasUnread")]
    public bool HasUnread { get; set; } = false;

    [JsonProperty("isDormant")]
    public bool IsDormant { get; set; } = false;

    // 通知種別フィルター（デフォルトは全て有効）
    [JsonProperty("notifyVideo")]
    public bool NotifyVideo { get; set; } = true;

    [JsonProperty("notifyShort")]
    public bool NotifyShort { get; set; } = true;

    [JsonProperty("notifyLive")]
    public bool NotifyLive { get; set; } = true;

    /// <summary>ライブ/プレミア待機所の通知方法（移行後は UpcomingNotifyMode を使用）</summary>
    [JsonProperty("notifyUpcoming")]
    public bool? NotifyUpcoming { get; set; } = null;

    /// <summary>ライブ/プレミア upcoming の通知方法</summary>
    [JsonProperty("upcomingNotifyMode")]
    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public UpcomingNotifyMode UpcomingNotifyMode { get; set; } = UpcomingNotifyMode.WaitingRoomOnly;

    /// <summary>待機所通知リードタイム（分）</summary>
    [JsonProperty("upcomingNotifyLeadMinutes")]
    public int UpcomingNotifyLeadMinutes { get; set; } = 10;

    [JsonProperty("testDataPath")]
    public string TestDataPath { get; set; } = string.Empty;

    /// <summary>テストチャンネル用: 現在のテスト状態インデックス</summary>
    [JsonProperty("testStateIndex")]
    public int TestStateIndex { get; set; } = 0;

    // UI専用プロパティ（シリアライズ不要）
    [JsonIgnore]
    public string StatusText => IsEnabled ? "監視中" : "停止中";

    [JsonIgnore]
    public string LastCheckedText => LastCheckedAt == DateTime.MinValue
        ? "未確認"
        : LastCheckedAt.ToString("MM/dd HH:mm");

    [JsonProperty("categoryId")]
    public string? CategoryId { get; set; }

    // ===== 監視モード =====
    [JsonProperty("monitorMode")]
    public MonitorMode MonitorMode { get; set; } = MonitorMode.Normal;

    // 集中監視（後方互換のため残す）
    [JsonProperty("focusHour")]
    public int FocusHour { get; set; } = 18;

    [JsonProperty("focusMinute")]
    public int FocusMinute { get; set; } = 0;

    [JsonProperty("focusWindowMinutes")]
    public int FocusWindowMinutes { get; set; } = 15;

    [JsonProperty("focusIntervalMinutes")]
    public int FocusIntervalMinutes { get; set; } = 5;
    /// <summary>曜日ビットマスク: bit0=Sun, bit1=Mon, ..., bit6=Sat。0=全曜日</summary>
    [JsonProperty("focusDays")]
    public int FocusDays { get; set; } = 0;

    /// <summary>時間指定スロットリスト（複数スロット対応）</summary>
    [JsonProperty("focusSlots")]
    public List<FocusSlot> FocusSlots { get; set; } = new();

    // 低頻度監視
    [JsonProperty("lowFreqIntervalMinutes")]
    public int LowFreqIntervalMinutes { get; set; } = 60;

    /// <summary>アップロードプレイリストID（channels.list から取得）。state.json で管理</summary>
    [JsonIgnore]
    public string UploadsPlaylistId { get; set; } = "";
    [JsonProperty("normalIntervalMinutes")]
    public int NormalIntervalMinutes { get; set; } = 0;

    // 次回チェック予定時刻（state.json で管理）
    [JsonIgnore]
    public DateTime NextCheckAt { get; set; } = DateTime.MinValue;

    [JsonIgnore]
    public string ChannelUrl => "https://www.youtube.com/channel/" + ChannelId;

    /// <summary>
    /// 種別ごとの実効監視モードを返す。
    /// スロットベース（MonitorMode.Focus + FocusSlots）の場合はタブ固定順（動画=0/Short=1/ライブ=2）のSlotModeを返す。
    /// </summary>
    public MonitorMode GetEffectiveModeForKind(VideoKind kind)
    {
        if (MonitorMode != MonitorMode.Focus || FocusSlots.Count == 0) return MonitorMode;
        int idx = kind switch
        {
            VideoKind.Short => 1,
            VideoKind.Live  => 2,
            _               => 0
        };
        return idx < FocusSlots.Count ? FocusSlots[idx].SlotMode : MonitorMode.Normal;
    }
}

public enum MonitorMode
{
    Normal   = 0,  // 全体設定に従う
    Focus    = 1,  // 集中監視のみ
    LowFreq  = 2,  // 低頻度監視
}

public enum UpcomingNotifyMode
{
    WaitingRoomOnly = 0,  // 待機所のみ（開始時通知なし）
    LiveStartOnly   = 1,  // ライブ配信開始時のみ（待機所通知なし）
    Both            = 2,  // 両方通知
}

public class CategoryInfo
{
    [JsonProperty("categoryId")]
    public string CategoryId { get; set; } = Guid.NewGuid().ToString();

    [JsonProperty("categoryName")]
    public string CategoryName { get; set; } = string.Empty;

    [JsonProperty("isCollapsed")]
    public bool IsCollapsed { get; set; } = false;

    [JsonProperty("sortOrder")]
    public int SortOrder { get; set; } = 0;
}

public class LogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public LogLevel Level { get; set; } = LogLevel.Info;
    public string Message { get; set; } = string.Empty;
    public string? ChannelName { get; set; }
    public string ChannelNameFormatted =>
        string.IsNullOrEmpty(ChannelName) ? string.Empty : $"[{ChannelName}] ";

    public string FormattedTime => Timestamp.ToString("HH:mm:ss");
    public string LevelText => Level.ToString().ToUpper();

    public string LevelColor => Level switch
    {
        LogLevel.System  => "#60A5FA",
        LogLevel.Warning => "#F59E0B",
        LogLevel.Error   => "#EF4444",
        LogLevel.Debug   => "#6B7280",
        _                => "#94A3B8"
    };
}

public enum LogLevel
{
    System,
    Info,
    Warning,
    Error,
    Debug
}

public enum LogCategory
{
    System,
    Info,
    Warning,
    Error,
    Debug
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

/// <summary>チャンネルの「今表示・クリックで開くべき対象」の判定結果</summary>
public class ChannelCardStatus
{
    public List<PendingVideoEntry> ActiveLiveEntries { get; set; } = new();
    public List<PendingVideoEntry> ActivePremiereEntries { get; set; } = new();
    public PendingVideoEntry? PendingLiveDisplay { get; set; }
    public PendingVideoEntry? PendingPremiereDisplay { get; set; }
    public string? ClickTargetVideoId { get; set; }
    public VideoKind? ClickTargetKind { get; set; }
}

/// <summary>時間指定の1スロット設定</summary>
public class FocusSlot
{
    [JsonProperty("notifyKind")]
    public VideoKind NotifyKind { get; set; } = VideoKind.Video;
    /// <summary>曜日ビットマスク (bit0=Sun..bit6=Sat, 0b1111111=全曜日)</summary>
    [JsonProperty("days")]
    public int Days { get; set; } = AppConstants.AllDaysMask;
    [JsonProperty("hour")]
    public int Hour { get; set; } = 18;
    [JsonProperty("minute")]
    public int Minute { get; set; } = 0;
    [JsonProperty("windowMinutes")]
    public int WindowMinutes { get; set; } = 15;
    [JsonProperty("intervalMinutes")]
    public int IntervalMinutes { get; set; } = 5;
    [JsonProperty("isEnabled")]
    public bool IsEnabled { get; set; } = false;
    [JsonProperty("slotMode")]
    public MonitorMode SlotMode { get; set; } = MonitorMode.Focus;
    [JsonProperty("slotNormalIntervalMinutes")]
    public int SlotNormalIntervalMinutes { get; set; } = 0; // 0 = グローバル設定に従う
    [JsonProperty("slotLowFreqIntervalMinutes")]
    public int SlotLowFreqIntervalMinutes { get; set; } = 60;
}

public enum ToastStyle
{
    Standard,      // デフォルト通知（チャンネルアイコン＋動画情報）
    Thumbnail      // サムネイル通知（サムネイル大表示＋チャンネル名＋種別＋タイトル）
}

/// <summary>チャンネルごとの実行状態（state.json で管理）</summary>
public class ChannelState
{
    [JsonProperty("lastCheckedVideoId")]
    public string LastCheckedVideoId { get; set; } = string.Empty;

    [JsonProperty("lastCheckedVideoPublishedAt")]
    public DateTime? LastCheckedVideoPublishedAt { get; set; } = null;

    [JsonProperty("nextCheckAt")]
    public DateTime NextCheckAt { get; set; } = DateTime.MinValue;

    [JsonProperty("lastCheckedAt")]
    public DateTime LastCheckedAt { get; set; } = DateTime.MinValue;

    [JsonProperty("uploadsPlaylistId")]
    public string UploadsPlaylistId { get; set; } = string.Empty;

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

    [JsonProperty("activeLives")]
    public List<PendingVideoEntry> ActiveLives { get; set; } = new();

    [JsonProperty("activePremieres")]
    public List<PendingVideoEntry> ActivePremieres { get; set; } = new();

    [JsonProperty("lastLiveNotifiedId")]
    public string LastLiveNotifiedId { get; set; } = string.Empty;

    [JsonProperty("lastPremiereNotifiedId")]
    public string LastPremiereNotifiedId { get; set; } = string.Empty;

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

    [JsonProperty("lastVideoTitle")]
    public string LastVideoTitle { get; set; } = string.Empty;

    [JsonProperty("lastVideoNotifiedAt")]
    public DateTime? LastVideoNotifiedAt { get; set; }

    [JsonProperty("lastShortNotifiedId")]
    public string LastShortNotifiedId { get; set; } = string.Empty;

    [JsonProperty("lastShortTitle")]
    public string LastShortTitle { get; set; } = string.Empty;

    [JsonProperty("lastShortNotifiedAt")]
    public DateTime? LastShortNotifiedAt { get; set; }

    [JsonProperty("lastLiveNotifiedTitle")]
    public string LastLiveNotifiedTitle { get; set; } = string.Empty;

    [JsonProperty("lastLiveNotifiedAt")]
    public DateTime? LastLiveNotifiedAt { get; set; }

    [JsonProperty("lastPremiereNotifiedTitle")]
    public string LastPremiereNotifiedTitle { get; set; } = string.Empty;

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

    [JsonProperty("isBanned")]
    public bool IsBanned { get; set; } = false;

    [JsonProperty("latestVideoDeleted")]
    public bool LatestVideoDeleted { get; set; } = false;

    [JsonProperty("noVideosFound")]
    public bool NoVideosFound { get; set; } = false;
}

/// <summary>Gemini 要約結果1件分（gemini_summary_cache.json で管理）</summary>
public class GeminiSummaryEntry
{
    [JsonProperty("headline")]
    public string Headline { get; set; } = string.Empty;

    [JsonProperty("detail")]
    public string Detail { get; set; } = string.Empty;
}

/// <summary>アプリ全体の実行状態（state.json で管理）</summary>
public class AppState
{
    [JsonProperty("todayApiUnits")]
    public int TodayApiUnits { get; set; } = 0;

    [JsonProperty("todayApiDate")]
    public string TodayApiDate { get; set; } = string.Empty;

    [JsonProperty("channels")]
    public Dictionary<string, ChannelState> Channels { get; set; } = new();
}

public enum VideoKind { Video, Short, Live, Premiere }

/// <summary>APIキー有効性テストの結果</summary>
public enum ApiKeyTestResult { Valid, Invalid, NetworkError }

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
    public string KindLabel => Kind switch
    {
        VideoKind.Short    => "Short",
        VideoKind.Live     => "ライブ",
        VideoKind.Premiere => "プレミア",
        _                  => "動画"
    };
}

/// <summary>Gemini 動画要約の結果</summary>
public class GeminiSummaryResult
{
    public bool    Success      { get; init; }
    public string? Headline     { get; init; }
    public string? Detail       { get; init; }
    public string? ErrorMessage { get; init; }
}
