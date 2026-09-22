using YTNotifier.Constants;
using Newtonsoft.Json;

namespace YTNotifier.Models;

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

    /// <summary>チャンネルの実行時状態（巡回カーソル・通知済み記録・待機リスト・次回チェック時刻など）。
    /// channels.json には出さず、state.json で管理する。読み込み時に丸ごと差し替えるため set は internal</summary>
    private ChannelState _state = new();
    [JsonIgnore]
    public ChannelState State
    {
        get => _state;
        internal set => _state = value ?? new();
    }

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

    /// <summary>このチャンネルにのみ適用するNGワード（共通NGワードとはOR判定）</summary>
    [JsonProperty("ngWords")]
    public List<string> NgWords { get; set; } = new();

    /// <summary>ライブ/プレミア待機所の通知方法（移行後は UpcomingNotifyMode を使用）</summary>
    [JsonProperty("notifyUpcoming")]
    public bool? NotifyUpcoming { get; set; } = null;

    /// <summary>ライブ/プレミア upcoming の通知方法（移行元として保持。移行後は Premiere/Live 別フィールドを使用）</summary>
    [JsonProperty("upcomingNotifyMode")]
    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public UpcomingNotifyMode UpcomingNotifyMode { get; set; } = UpcomingNotifyMode.WaitingRoomOnly;

    /// <summary>待機所通知リードタイム（分）（移行元として保持。移行後は Premiere/Live 別フィールドを使用）</summary>
    [JsonProperty("upcomingNotifyLeadMinutes")]
    public int UpcomingNotifyLeadMinutes { get; set; } = 10;

    /// <summary>プレミア公開 upcoming の通知方法</summary>
    [JsonProperty("premiereUpcomingNotifyMode")]
    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public UpcomingNotifyMode PremiereUpcomingNotifyMode { get; set; } = UpcomingNotifyMode.Both;

    /// <summary>プレミア待機所通知リードタイム（分）</summary>
    [JsonProperty("premiereUpcomingNotifyLeadMinutes")]
    public int PremiereUpcomingNotifyLeadMinutes { get; set; } = 10;

    /// <summary>ライブ配信 upcoming の通知方法</summary>
    [JsonProperty("liveUpcomingNotifyMode")]
    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public UpcomingNotifyMode LiveUpcomingNotifyMode { get; set; } = UpcomingNotifyMode.Both;

    /// <summary>ライブ待機所通知リードタイム（分）</summary>
    [JsonProperty("liveUpcomingNotifyLeadMinutes")]
    public int LiveUpcomingNotifyLeadMinutes { get; set; } = 10;

    [JsonProperty("testDataPath")]
    public string TestDataPath { get; set; } = string.Empty;

    /// <summary>テストチャンネル用: 現在のテスト状態インデックス</summary>
    [JsonProperty("testStateIndex")]
    public int TestStateIndex { get; set; } = 0;

    // UI専用プロパティ（シリアライズ不要）
    [JsonIgnore]
    public string StatusText => IsEnabled ? "監視中" : "停止中";

    [JsonIgnore]
    public string LastCheckedText => State.LastCheckedAt == DateTime.MinValue
        ? "未確認"
        : State.LastCheckedAt.ToString("MM/dd HH:mm");

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

    [JsonProperty("normalIntervalMinutes")]
    public int NormalIntervalMinutes { get; set; } = 0;

    [JsonIgnore]
    public string ChannelUrl => "https://www.youtube.com/channel/" + ChannelId;

    /// <summary>
    /// 種別ごとの実効監視モードを返す。
    /// スロットベース（MonitorMode.Focus + FocusSlots）の場合は種別（NotifyKind）が一致する最初のスロットの SlotMode を返す。
    /// </summary>
    public MonitorMode GetEffectiveModeForKind(VideoKind kind)
    {
        if (MonitorMode != MonitorMode.Focus || FocusSlots.Count == 0) return MonitorMode;
        var kindSlot = FocusSlots.FirstOrDefault(s => s.NotifyKind == kind);
        return kindSlot?.SlotMode ?? MonitorMode.Normal;
    }

    /// <summary>
    /// このチャンネルの旧来の監視モード設定（MonitorMode / Focus* / LowFreq*）から、
    /// 指定種別の既定 FocusSlot を1つ生成する。FocusSlots 初期化時の変換ルールとして
    /// SettingsService（データ移行）と ChannelDetailWindow（詳細画面）で共用する。
    /// </summary>
    public FocusSlot CreateDefaultFocusSlot(VideoKind kind) => MonitorMode switch
    {
        MonitorMode.LowFreq => new FocusSlot
        {
            SlotMode                   = MonitorMode.LowFreq,
            SlotLowFreqIntervalMinutes = LowFreqIntervalMinutes,
            NotifyKind                 = kind,
            IsEnabled                  = true
        },
        MonitorMode.Focus => new FocusSlot
        {
            SlotMode        = MonitorMode.Focus,
            NotifyKind      = kind,
            Days            = FocusDays,
            Hour            = FocusHour,
            Minute          = FocusMinute,
            WindowMinutes   = FocusWindowMinutes,
            IntervalMinutes = FocusIntervalMinutes,
            IsEnabled       = true
        },
        _ => new FocusSlot { SlotMode = MonitorMode.Normal, NotifyKind = kind, IsEnabled = true }
    };
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
