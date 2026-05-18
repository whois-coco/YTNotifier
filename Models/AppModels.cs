using Newtonsoft.Json;

namespace YTNotifier.Models;

public class AppSettings
{
    [JsonProperty("apiKey")]
    public string ApiKey { get; set; } = string.Empty;

    [JsonProperty("isDarkMode")]
    public bool IsDarkMode { get; set; } = false;

    [JsonProperty("checkIntervalMinutes")]
    public int CheckIntervalMinutes { get; set; } = 5;

    [JsonProperty("showDesktopNotification")]
    public bool ShowDesktopNotification { get; set; } = true;

    [JsonProperty("minimizeToTray")]
    public bool MinimizeToTray { get; set; } = true;

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

    // UI専用プロパティ（シリアライズ不要）
    [JsonIgnore]
    public string StatusText => IsEnabled ? "監視中" : "停止中";

    [JsonIgnore]
    public string LastCheckedText => LastCheckedAt == DateTime.MinValue
        ? "未確認"
        : LastCheckedAt.ToString("MM/dd HH:mm");

    [JsonIgnore]
    public string ChannelUrl => $"https://www.youtube.com/channel/{ChannelId}";
}

public class LogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public LogLevel Level { get; set; } = LogLevel.Info;
    public string Message { get; set; } = string.Empty;
    public string? ChannelName { get; set; }

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
