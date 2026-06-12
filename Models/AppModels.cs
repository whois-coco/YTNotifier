using YTNotifier.Services;
using Newtonsoft.Json;

namespace YTNotifier.Models;

public class AppSettings
{
    [JsonProperty("apiKey")]
    [Newtonsoft.Json.JsonIgnore]
    public string ApiKey { get; set; } = string.Empty;

    [JsonProperty("isDarkMode")]
    public bool IsDarkMode { get; set; } = false;

    [JsonProperty("checkIntervalMinutes")]
    public int CheckIntervalMinutes { get; set; } = 5;

    [JsonProperty("showDesktopNotification")]
    public bool ShowDesktopNotification { get; set; } = false;

    /// <summary>トースト通知スタイル</summary>
    [JsonProperty("toastStyle")]
    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public ToastStyle ToastStyle { get; set; } = ToastStyle.Standard;

    /// <summary>全チャンネル共通: 待機所（upcoming）通知のグローバルON/OFF</summary>
    [JsonProperty("globalNotifyUpcoming")]
    public bool GlobalNotifyUpcoming { get; set; } = false;

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
    public bool NotificationSound { get; set; } = false;

    [JsonProperty("isMuted")]
    public bool IsMuted { get; set; } = false;

    [JsonProperty("preMuteDesktopNotification")]
    public bool PreMuteDesktopNotification { get; set; } = false;

    [JsonProperty("preMuteNotificationSound")]
    public bool PreMuteNotificationSound { get; set; } = false;

    [JsonProperty("compactMode")]
    public bool CompactMode { get; set; } = false;

    [JsonProperty("sidebarCollapsed")]
    public bool SidebarCollapsed { get; set; } = false;

    [JsonProperty("logRetentionDays")]
    public int LogRetentionDays { get; set; } = 30;

    [JsonProperty("autoCleanLogs")]
    public bool AutoCleanLogs { get; set; } = false;

    // ===== ログ表示フィルター =====
    // システムメッセージ（監視開始/停止・チェック開始/完了）は常に表示
    [JsonProperty("logShowNoNew")]
    public bool LogShowNoNew     { get; set; } = false; // 新着なし
    [JsonProperty("logShowNewFound")]
    public bool LogShowNewFound  { get; set; } = true;  // 新着あり
    [JsonProperty("logShowCheckError")]
    public bool LogShowCheckError{ get; set; } = true;  // チェックエラー
    [JsonProperty("logShowNotify")]
    public bool LogShowNotify    { get; set; } = false; // 通知送信
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

    [JsonProperty("lastCheckedVideoId")]
    public string LastCheckedVideoId { get; set; } = string.Empty;

    // 通常動画のみの最新ID（クリック時に開く用）
    [JsonProperty("lastVideoId")]
    public string LastVideoId { get; set; } = string.Empty;

    // 最新ライブID
    [JsonProperty("lastLiveId")]
    public string LastLiveId { get; set; } = string.Empty;

    // 最新ShortID
    [JsonProperty("lastShortId")]
    public string LastShortId { get; set; } = string.Empty;

    [JsonProperty("lastCheckedAt")]
    public DateTime LastCheckedAt { get; set; } = DateTime.MinValue;

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
    /// ON: upcoming（待機所）段階で通知（ライブ配信前・プレミア公開前）
    /// OFF: live ステータスになった時のみ通知
    /// </summary>
    [JsonProperty("notifyUpcoming")]
    public bool NotifyUpcoming { get; set; } = false;

    /// <summary>upcoming待ちの動画ID（NotifyUpcoming=OFFでliveになるまで監視継続するため）</summary>
    [JsonProperty("pendingUpcomingVideoId")]
    public string PendingUpcomingVideoId { get; set; } = string.Empty;

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
    public int FocusWindowMinutes { get; set; } = 30;

    [JsonProperty("focusIntervalMinutes")]
    public int FocusIntervalMinutes { get; set; } = 5;
    /// <summary>曜日ビットマスク: bit0=Sun, bit1=Mon, ..., bit6=Sat。0=全曜日</summary>
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
    public string ChannelUrl => $"https://www.youtube.com/channel/{ChannelId}";
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
        LogLevel.Error => "#EF4444",
        LogLevel.Warning => "#F59E0B",
        LogLevel.Success => "#22C55E",
        _ => "#94A3B8"
    };
}

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error
}

public enum LogCategory
{
    System,      // 監視開始/停止・チェック開始/完了など（常に表示）
    NoNew,       // 新着なし
    NewFound,    // 新着あり
    CheckError,  // チェックエラー
    Notify       // 通知送信
}

/// <summary>時間指定の1スロット設定</summary>
public class FocusSlot
{
    [JsonProperty("notifyKind")]
    public YTNotifier.Services.VideoKind NotifyKind { get; set; } = YTNotifier.Services.VideoKind.Video;
    /// <summary>曜日ビットマスク (bit0=Sun..bit6=Sat, 0=全曜日)</summary>
    [JsonProperty("days")]
    public int Days { get; set; } = 0;
    [JsonProperty("hour")]
    public int Hour { get; set; } = 18;
    [JsonProperty("minute")]
    public int Minute { get; set; } = 0;
    [JsonProperty("windowMinutes")]
    public int WindowMinutes { get; set; } = 30;
    [JsonProperty("intervalMinutes")]
    public int IntervalMinutes { get; set; } = 5;
    [JsonProperty("isEnabled")]
    public bool IsEnabled { get; set; } = false;
}

public enum ToastStyle
{
    Standard,      // デフォルト通知（チャンネルアイコン＋動画情報）
    Thumbnail      // サムネイル通知（サムネイル大表示＋チャンネル名＋種別＋タイトル）
}
