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

    /// <summary>全チャンネル共通: 待機所（upcoming）通知のグローバルON/OFF</summary>
    [JsonProperty("globalNotifyUpcoming")]
    public bool GlobalNotifyUpcoming { get; set; } = true;

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

    // ===== ログ表示フィルター =====
    // INFO / WARNING / ERROR / DEBUG のいずれかを設定する
    // デフォルトは INFO（SYSTEM + INFO + ERROR を表示）
    [JsonProperty("continuousAddMode")]
    public bool ContinuousAddMode { get; set; } = true;  // 連続追加モード（デフォルトON）

    // 当日のAPI実使用量追跡
    [JsonProperty("todayApiUnits")]
    public int    TodayApiUnits { get; set; } = 0;
    [JsonProperty("todayApiDate")]
    public string TodayApiDate  { get; set; } = "";
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

    /// <summary>プレイリスト走査カーソル（全種別共通）。このIDより新しい動画だけを新着として扱う</summary>
    [JsonProperty("lastCheckedVideoId")]
    public string LastCheckedVideoId { get; set; } = string.Empty;

    /// <summary>UI のクリック機能で「最新動画を開く」ために使用（動画のみ）</summary>
    [JsonProperty("lastVideoId")]
    public string LastVideoId { get; set; } = string.Empty;


    // ===== upcoming 待ちリスト（複数の同時 upcoming ライブ/プレミアに対応）=====
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

    /// <summary>ライブ通知済みVideoId（upcoming 通知ON時の再通知防止用）</summary>
    [JsonProperty("lastLiveNotifiedId")]
    public string LastLiveNotifiedId { get; set; } = string.Empty;

    /// <summary>プレミア通知済みVideoId（upcoming 通知ON時の再通知防止用）</summary>
    [JsonProperty("lastPremiereNotifiedId")]
    public string LastPremiereNotifiedId { get; set; } = string.Empty;

    [JsonProperty("lastCheckedAt")]
    public DateTime LastCheckedAt { get; set; } = DateTime.MinValue;

    // ===== v1→v2 マイグレーション専用フィールド =====
    // 初回チェック時に PendingLives / PendingPremieres へ移行後クリアされる。
    // 新規コードからは参照しないこと。
    [JsonProperty("lastLiveId")]
    public string LastLiveId { get; set; } = string.Empty;

    [JsonProperty("lastPremiereId")]
    public string LastPremiereId { get; set; } = string.Empty;

    [JsonProperty("nextLiveCheckAt")]
    public DateTime? NextLiveCheckAt { get; set; } = null;

    [JsonProperty("liveGraceRemaining")]
    public int LiveGraceRemaining { get; set; } = 0;

    [JsonProperty("nextPremiereCheckAt")]
    public DateTime? NextPremiereCheckAt { get; set; } = null;
    // ===== /v1→v2 マイグレーション専用フィールド =====

    [JsonProperty("isEnabled")]
    public bool IsEnabled { get; set; } = true;

    [JsonProperty("addedAt")]
    public DateTime AddedAt { get; set; } = DateTime.Now;

    // 未読フラグ（新着動画があった場合にtrue、クリックでfalseに戻る）
    [JsonProperty("hasUnread")]
    public bool HasUnread { get; set; } = false;

    // 通知種別フィルター（デフォルトは全て有効）
    [JsonProperty("notifyVideo")]
    public bool NotifyVideo { get; set; } = true;

    [JsonProperty("notifyShort")]
    public bool NotifyShort { get; set; } = true;

    [JsonProperty("notifyLive")]
    public bool NotifyLive { get; set; } = true;

    /// <summary>
    /// null:  グローバル設定に従う（デフォルト）
    /// true:  個別にON（グローバルに関わらず通知）
    /// false: 個別にOFF（グローバルに関わらずスキップ）
    /// </summary>
    [JsonProperty("notifyUpcoming")]
    public bool? NotifyUpcoming { get; set; } = null;

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

    /// <summary>アップロードプレイリストID（channels.list から取得）空の場合はUC→UU変換で代替</summary>
    [JsonProperty("uploadsPlaylistId")]
    public string UploadsPlaylistId { get; set; } = "";
    [JsonProperty("normalIntervalMinutes")]
    public int NormalIntervalMinutes { get; set; } = 0;

    // 次回チェック予定時刻
    [JsonProperty("nextCheckAt")]
    public DateTime NextCheckAt { get; set; } = DateTime.MinValue;

    [JsonIgnore]
    public string ChannelUrl => "https://www.youtube.com/channel/" + ChannelId;

    /// <summary>
    /// 種別ごとの実効監視モードを返す。
    /// スロットベース（MonitorMode.Focus + FocusSlots）の場合はタブ固定順（動画=0/Short=1/ライブ=2）のSlotModeを返す。
    /// </summary>
    public MonitorMode GetEffectiveModeForKind(YTNotifier.Services.VideoKind kind)
    {
        if (MonitorMode != MonitorMode.Focus || FocusSlots.Count == 0) return MonitorMode;
        int idx = kind switch
        {
            YTNotifier.Services.VideoKind.Short => 1,
            YTNotifier.Services.VideoKind.Live  => 2,
            _                                   => 0
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

    /// <summary>開始時刻到達後の猶予チェック残回数</summary>
    [JsonProperty("graceRemaining")]
    public int GraceRemaining { get; set; } = 0;
}

/// <summary>時間指定の1スロット設定</summary>
public class FocusSlot
{
    [JsonProperty("notifyKind")]
    public YTNotifier.Services.VideoKind NotifyKind { get; set; } = YTNotifier.Services.VideoKind.Video;
    /// <summary>曜日ビットマスク (bit0=Sun..bit6=Sat, 0b1111111=全曜日)</summary>
    [JsonProperty("days")]
    public int Days { get; set; } = 0b1111111;
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
