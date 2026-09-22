using Newtonsoft.Json;

namespace YTNotifier.Models;

public class AppSettings
{
    [Newtonsoft.Json.JsonIgnore]
    public string ApiKey { get; set; } = string.Empty;

    [JsonProperty("isDarkMode")]
    public bool IsDarkMode { get; set; } = false;

    /// <summary>ウィンドウ色（テーマ）。isDarkMode からの移行後はこちらを使用する</summary>
    [JsonProperty("theme")]
    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public AppTheme Theme { get; set; } = AppTheme.Light;

    /// <summary>差し色（テーマの上から適用するアクセント色）。null / 空 はテーマ既定。書式 "#RRGGBB"</summary>
    [JsonProperty("accentColorOverride")]
    public string? AccentColorOverride { get; set; } = null;

    /// <summary>isDarkMode → Theme への移行完了フラグ</summary>
    [JsonProperty("themeMigrated")]
    public bool ThemeMigrated { get; set; } = false;

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

    /// <summary>Premiere/Live 別 UpcomingNotifyMode へのマイグレーション完了フラグ</summary>
    [JsonProperty("upcomingSplitMigrated")]
    public bool UpcomingSplitMigrated { get; set; } = false;

    [JsonProperty("minimizeToTray")]
    public bool MinimizeToTray { get; set; } = false;

    [JsonProperty("startWithWindows")]
    public bool StartWithWindows { get; set; } = false;

    [JsonProperty("traceLogEnabled")]
    public bool TraceLogEnabled { get; set; } = false;

    // ウィンドウサイズ・位置
    [JsonProperty("windowWidth")]
    public double WindowWidth { get; set; } = 429;

    [JsonProperty("windowHeight")]
    public double WindowHeight { get; set; } = 500;

    [JsonProperty("windowLeft")]
    public double WindowLeft { get; set; } = -1;

    [JsonProperty("windowTop")]
    public double WindowTop { get; set; } = -1;

    [JsonProperty("alwaysOnTop")]
    public bool AlwaysOnTop { get; set; } = false;

    [JsonProperty("notificationSound")]
    public bool NotificationSound { get; set; } = true;

    /// <summary>選択中の通知音セット（Sounds フォルダ配下のサブフォルダ名）。空文字は Sounds 直下を使う「デフォルト」</summary>
    [JsonProperty("notificationSoundSet")]
    public string NotificationSoundSet { get; set; } = string.Empty;

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
    public bool SidebarCollapsed { get; set; } = true;

    [JsonProperty("logRetentionDays")]
    public int LogRetentionDays { get; set; } = 30;

    [JsonProperty("autoCleanLogs")]
    public bool AutoCleanLogs { get; set; } = false;

    [JsonProperty("continuousAddMode")]
    public bool ContinuousAddMode { get; set; } = false;  // 連続追加モード（デフォルトOFF）

    [JsonProperty("ngWords")]
    public List<string> NgWords { get; set; } = new();

    [JsonProperty("lastStartupCheckDate")]
    public string LastStartupCheckDate { get; set; } = "";
    [JsonProperty("lastDailyFullScanDate")]
    public string LastDailyFullScanDate { get; set; } = "";

    /// <summary>直近起動時点のアプリバージョン。これより現在バージョンが新しい場合、アップデート後として
    /// リリースノート確認ダイアログを表示する判定に使う</summary>
    [JsonProperty("lastSeenAppVersion")]
    public string LastSeenAppVersion { get; set; } = string.Empty;
}

public enum ToastStyle
{
    Standard,      // デフォルト通知（チャンネルアイコン＋動画情報）
    Thumbnail      // サムネイル通知（サムネイル大表示＋チャンネル名＋種別＋タイトル）
}

/// <summary>ウィンドウ色（テーマ）。Themes/ の12テーマに対応する</summary>
public enum AppTheme { Light, Dark, Blue, Gray, Pink, MatteBlack, Green, Purple, Amber, Teal, Plum, Wine }

/// <summary>設定画面「ウィンドウ色」ドロップダウンの選択肢1件（テーマ・表示ラベル・色見本の塗りブラシ）</summary>
public sealed record WindowColorOption(AppTheme Theme, string Label, System.Windows.Media.Brush SampleBrush);

/// <summary>APIキー有効性テストの結果</summary>
public enum ApiKeyTestResult { Valid, Invalid, NetworkError }
