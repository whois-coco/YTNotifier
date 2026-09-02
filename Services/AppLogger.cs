using YTNotifier.Models;

namespace YTNotifier.Services;

public enum LogMsg
{
    // ── SYSTEM (1xxx) ─────────────────────────────────────────────
    // 監視
    MonitorStarted            = 1001,
    MonitorStopped            = 1002,
    CheckStarted              = 1003,  // {0}=label {1}=count {2}=total
    CheckCompleted            = 1004,  // {0}=label
    // ログ
    AutoLogDeleted            = 1005,  // {0}=count

    // ── INFO (2xxx) ───────────────────────────────────────────────
    // 監視・通知
    NewVideo                  = 2008,  // {0}=kind {1}=title
    // チャンネル
    ChannelAdded              = 2005,
    ChannelRemoved            = 2006,
    ChannelBanned             = 2009,
    ChannelBanRecovered       = 2010,
    ChannelListBanCheckStarted   = 2011,
    ChannelListBanCheckCompleted = 2012,
    DormantListBanCheckStarted   = 2013,
    DormantListBanCheckCompleted = 2014,
    NoVideosDetected             = 2015,
    NoVideosRecovered            = 2016,
    // APIキー
    ApiKeyMigrated            = 2002,
    ApiKeySaved               = 2003,
    // ネットワーク
    NetworkRestored           = 2004,
    // バックアップ
    BackupSaved               = 2001,
    // ログ
    LogManualDeleted          = 2007,  // {0}=count {1}=size

    // ── WARNING (3xxx) ────────────────────────────────────────────
    // APIキー
    ApiKeyNotSet              = 3002,
    ApiKeyNotSetChannel       = 3003,
    // ネットワーク
    NetworkDisconnected       = 3004,
    // クォータ
    QuotaRiskAdjusted         = 3005,  // {0}=minutes
    QuotaExceeded             = 3007,  // {0}=resumeTime
    QuotaResumed              = 3008,
    // その他
    AutoRestored              = 3001,  // {0}=reason
    InvalidChannelId          = 3006,  // {0}=channelId

    // ── ERROR (4xxx) ─────────────────────────────────────────────
    // API呼び出し
    CheckFailed               = 4010,  // {0}=message
    ChannelInfoFailed         = 4011,  // {0}=message
    LatestVideoFailed         = 4012,  // {0}=message
    UploadsPlaylistFailed     = 4015,  // {0}=channelId {1}=message
    VideoKindFailed           = 4016,  // {0}=message
    UushFallbackFailed        = 4018,  // {0}=videoId {1}=message
    PlaylistFailed            = 4019,  // {0}=playlistId {1}=message
    ApiFallback               = 4008,
    ChannelBanCheckFailed     = 4022,  // {0}=channelId {1}=message
    // スケジューラー
    SchedulerError            = 4023,  // {0}=message
    // Gemini要約
    GeminiSummaryFailed       = 4021,  // {0}=videoId {1}=message
    // 外部要約DLL（YTS.dll）バイパス
    ExternalSummaryBridgeFailed = 4024,  // {0}=videoId {1}=message
    // 要約スクリプト（Plugins）
    SummaryScriptFailed        = 4025,  // {0}=videoId {1}=message
    // 通知
    NotifyFailed              = 4013,  // {0}=message
    TestNotifyFailed          = 4009,  // {0}=message  (MainWindow.Settings)
    TestNotifyFailedNS        = 4014,  // {0}=message  (NotificationService)
    // バックアップ・復元
    BackupFailed              = 4001,  // {0}=message
    AutoRestoreFailed         = 4020,  // {0}=message
    // 設定・起動
    SettingsLoadError         = 4002,  // {0}=message
    ChannelListError          = 4003,  // {0}=message
    MonitorStartError         = 4004,  // {0}=message
    // UI
    IconLoadFailed            = 4005,  // {0}=path
    IconDownloadFailed        = 4006,  // {0}=message
    TrayIconInitFailed        = 4007,  // {0}=message

    // ── DEBUG (5xxx) ─────────────────────────────────────────────
    // 監視・通知
    NotificationSent          = 5076,  // {0}=title
    // 監視フロー
    NoNew                     = 5008,
    GracePeriodStarted        = 5009,  // {0}=videoId
    VideoNotFound             = 5010,
    // 動画通知フィルター
    LiveSkipped               = 5015,  // {0}=time {1}=title
    LiveReSkipped             = 5016,  // {0}=title
    PremiereSkipped           = 5017,  // {0}=time {1}=title
    PremiereReSkipped         = 5018,  // {0}=title
    KindFilterSkipped         = 5019,  // {0}=kind {1}=title
    LiveStartSkipped          = 5077,  // {0}=title
    PremiereStartSkipped      = 5078,  // {0}=title
    OldLiveDiscarded          = 5020,  // {0}=title
    OldLiveDiscardedNew       = 5021,  // {0}=title
    OldLiveDiscardedTrans     = 5022,  // {0}=title
    OldPremiereDiscarded      = 5023,  // {0}=title
    OldPremiereDiscardedNew   = 5024,  // {0}=title
    OldPremiereDiscardedTrans = 5025,  // {0}=title
    ArchivedLiveNotNotified   = 5127,  // {0}=title
    UpcomingQueued            = 5084,  // {0}=time {1}=title
    UpcomingQueueUpdated      = 5087,  // {0}=time {1}=title
    UpcomingQueueFull         = 5085,  // {0}=title
    SchedulerWaitingRoomNotify  = 5088,  // {0}=kind {1}=title
    SchedulerGracePeriodStarted = 5089,  // {0}=videoId
    SchedulerWakeUp             = 5090,
    PendingWindowExpired        = 5117,  // {0}=videoId
    // 動画検索・表示
    SearchingVideo            = 5005,
    OpenChannelPage           = 5006,
    OpenLatestVideo           = 5007,  // {0}=kind
    // 通知テスト
    TestNotifySent            = 5011,
    // 設定変更
    SettingDarkMode            = 5026,  // {0}=ON/OFF
    SettingNoCategoryMode      = 5027,  // {0}=ON/OFF
    SettingDesktopNotification = 5028,  // {0}=ON/OFF
    SettingToastStyle          = 5029,  // {0}=style
    SettingGlobalNotifyUpcoming= 5030,  // {0}=ON/OFF
    SettingNotificationSound   = 5031,  // {0}=ON/OFF
    SettingNotificationSoundSet = 5131,  // {0}=セット名
    SettingFlashTaskbar        = 5032,  // {0}=ON/OFF
    SettingMinimizeToTray      = 5033,  // {0}=ON/OFF
    SettingMute                = 5034,  // {0}=ON/OFF
    SettingCompactMode         = 5035,  // {0}=ON/OFF
    SettingAlwaysOnTop         = 5036,  // {0}=ON/OFF
    SettingStartWithWindows    = 5037,  // {0}=ON/OFF
    SettingCheckInterval       = 5038,  // {0}=minutes
    QuotaAutoIntervalAdjusted  = 5079,  // {0}=minutes
    SettingTraceLogEnabled     = 5129,  // {0}=ON/OFF
    SettingAutoCleanLogs       = 5040,  // {0}=ON/OFF
    SettingLogRetention        = 5041,  // {0}=days
    SettingBackupExported      = 5042,  // {0}=filename
    SettingBackupImported      = 5043,  // {0}=filename
    // チャンネル一覧・編集
    EditModeOn                = 5001,
    EditModeOff               = 5002,
    ChannelRenamed            = 5004,  // {0}=name
    ChannelReordered           = 5045,  // {0}=channelName
    CategoryReordered          = 5046,  // {0}=categoryName
    KindToggleChanged          = 5044,  // {0}=kind {1}=ON/OFF
    ChannelRowClicked          = 5049,  // {0}=channelName
    ChannelNameClicked         = 5126,  // {0}=channelName
    ChannelContextClearNew     = 5050,  // {0}=channelName
    ChannelContextOpenDetail   = 5051,  // {0}=channelName
    ChannelContextManualCheck  = 5138,  // {0}=channelName
    ChannelMovedToCategory     = 5052,  // {0}=channelName {1}=categoryName
    // カテゴリ操作
    CategoryCollapsed          = 5048,  // {0}=categoryName {1}=折り畳み/展開
    CategoryContextClearNew    = 5053,  // {0}=categoryName
    CategoryContextExpandAll   = 5054,
    CategoryContextCollapseAll = 5055,
    CategoryDeleted            = 5056,  // {0}=categoryName
    CategoryRenamed            = 5072,  // {0}=oldName {1}=newName
    CategoryAdded                   = 5097,  // {0}=categoryName
    AddChannelPasteClicked          = 5098,
    AddChannelDetailTabSwitched     = 5099,  // {0}=tabName
    AddChannelWindowClosed          = 5100,
    AddChannelNewCategoryPanelOpened= 5101,
    MovedToDormant                  = 5102,  // {0}=channelName
    MovedToActive                   = 5103,  // {0}=channelName
    DormantChannelMovedToCategory   = 5104,  // {0}=channelName {1}=categoryName
    DormantSearchExecuted           = 5105,  // {0}=query
    DormantSearchCleared            = 5106,
    // ナビゲーション・サイドバー
    NavPageSwitched            = 5057,  // {0}=page
    SettingsSubNavSwitched     = 5058,  // {0}=page
    SidebarToggled             = 5059,  // {0}=展開/折り畳み
    ManualCheckTriggered       = 5060,
    MonitorToggleClicked       = 5061,  // {0}=開始/停止
    // トレイ
    WindowToTray              = 5003,
    TrayWindowOpened          = 5080,
    TrayManualCheckTriggered  = 5081,
    TrayMonitorStarted        = 5082,
    TrayMonitorStopped        = 5083,
    // チャンネル追加ウィンドウ
    AddChannelPreviewClicked   = 5065,  // {0}=input
    ContinuousAddModeChanged   = 5066,  // {0}=ON/OFF
    // チャンネル詳細ウィンドウ
    ChannelDetailSaved         = 5047,  // {0}=channelName
    ChannelDetailTabSwitched   = 5070,  // {0}=channelName {1}=tabName
    ChannelDetailEnabledChanged= 5071,  // {0}=channelName {1}=tabName {2}=ON/OFF
    QuotaWarningOnSave         = 5073,  // {0}=channelName {1}=pct
    QuotaExceededOnSave        = 5074,  // {0}=channelName {1}=pct
    // チャンネル詳細ウィンドウ（間隔）
    ChannelDetailSlotInterval         = 5075,  // {0}=kindLabel {1}=intervalDesc
    ChannelDetailUpcomingModeChanged  = 5091,  // {0}=種別ラベル {1}=modeLabel
    ChannelDetailUpcomingLeadChanged  = 5092,  // {0}=種別ラベル {1}=minutes
    ChannelDetailCancelled            = 5093,
    AddChannelDialogOpened            = 5094,
    ChannelSearchExecuted             = 5095,  // {0}=query
    ChannelSearchCleared              = 5096,
    ChannelFilterApplied              = 5107,  // {0}=filterLabel
    ChannelFilterCleared              = 5108,
    FavoriteToggleChanged              = 5118,  // {0}=ON/OFF
    // チャンネルBAN・動画削除検知
    LatestVideoDeletedDetected         = 5121,  // {0}=videoId
    LatestVideoRecovered               = 5122,  // {0}=videoId
    LatestVideoTitleChanged            = 5125,  // {0}=videoId {1}=旧タイトル {2}=新タイトル
    ChannelListAllAlive                = 5123,
    DormantListAllAlive                = 5124,
    // 動画要約ポップアップ
    VideoSummaryPopupOpened           = 5109,  // {0}=channelName
    VideoListPopupOpened              = 5130,  // {0}=channelName {1}=件数
    RecentUploadsPopupOpened          = 5132,  // {0}=channelName {1}=件数
    RecentUploadThumbnailOpened       = 5134,  // {0}=channelName
    GeminiSummaryRequested            = 5110,  // {0}=videoId
    GeminiSummarySucceeded            = 5112,  // {0}=videoId
    ExternalSummaryBridgeRequested     = 5133,  // {0}=videoId
    // 要約スクリプト（Plugins）
    SummaryScriptRequested             = 5135,  // {0}=videoId
    SummaryScriptLog                   = 5136,  // {0}=スクリプトから渡されたメッセージ
    // プラグイン
    PluginDetected                     = 5137,  // {0}=プラグインのパス {1}=最終更新日時
    // APIキーウィンドウ
    ApiKeyEditStarted          = 5067,
    ApiKeyChanged              = 5068,
    ApiKeyUnchanged            = 5069,
    // 要約用APIキー
    GeminiApiKeyEditStarted    = 5113,
    GeminiApiKeyChanged        = 5114,
    GeminiApiKeyUnchanged      = 5115,
    GeminiApiKeySaved          = 5116,
    // アクティビティログウィンドウ
    ActivityLogWindowOpened    = 5062,
    ActivityLogCleared         = 5063,
    LogFolderOpened            = 5064,
    ActivityLogSavedToFile     = 5128,  // {0}=count
    // デバッグ・開発
    DebugWindowNotFound       = 5012,
    DevToolError              = 5013,  // {0}=message
    DebugDllFailed            = 5014,  // {0}=message
    UiUpdateFailed            = 5086,  // {0}=methodName {1}=message
}

public static class AppLogger
{
    private record MessageDef(LogLevel Level, LogCategory Category, string Template);

    private static readonly Dictionary<LogMsg, MessageDef> _messages = new()
    {
        // SYSTEM ─────────────────────────────────────────────────────
        // 監視
        [LogMsg.MonitorStarted]            = new(LogLevel.System,  LogCategory.Monitor,      "監視を開始しました({0})"),
        [LogMsg.MonitorStopped]            = new(LogLevel.System,  LogCategory.Monitor,      "監視を停止しました"),
        [LogMsg.NetworkRestored]           = new(LogLevel.System,  LogCategory.Network,      "インターネット接続が回復しました。監視を再開します。"),
        // ログ
        [LogMsg.AutoLogDeleted]            = new(LogLevel.System,  LogCategory.Log,          "起動時ログ自動削除: {0}件"),
        // クォータ
        [LogMsg.QuotaResumed]              = new(LogLevel.System,  LogCategory.Quota,        "APIクォータをリセットしました。監視を再開します。"),
        // 設定
        [LogMsg.SettingTraceLogEnabled]    = new(LogLevel.Debug,   LogCategory.Settings,     "トレースログの取得: {0}"),
        [LogMsg.SettingBackupExported]      = new(LogLevel.System,  LogCategory.Settings,     "バックアップをエクスポートしました: {0}"),
        [LogMsg.SettingBackupImported]      = new(LogLevel.System,  LogCategory.Settings,     "バックアップをインポートしました: {0}"),
        // プラグイン
        [LogMsg.PluginDetected]             = new(LogLevel.System,  LogCategory.Startup,      "プラグインを検出しました: {0}（最終更新 {1}）"),

        // INFO ────────────────────────────────────────────────────────
        // 監視・通知
        [LogMsg.CheckStarted]              = new(LogLevel.Info,    LogCategory.Monitor,      "{0}開始 ({1}/{2}チャンネル)"),
        [LogMsg.CheckCompleted]            = new(LogLevel.Info,    LogCategory.Monitor,      "{0}完了"),
        [LogMsg.NewVideo]                  = new(LogLevel.Info,    LogCategory.Monitor,      "新着{0}: {1}"),
        // チャンネル
        [LogMsg.ChannelAdded]              = new(LogLevel.Info,    LogCategory.Channel,      "チャンネルを追加しました"),
        [LogMsg.ChannelRemoved]            = new(LogLevel.Info,    LogCategory.Channel,      "チャンネルを削除しました"),
        [LogMsg.ChannelBanned]              = new(LogLevel.Info,    LogCategory.Channel,      "チャンネルBAN／削除を検知しました"),
        [LogMsg.ChannelBanRecovered]        = new(LogLevel.Info,    LogCategory.Channel,      "チャンネルの利用停止状態が解除されました"),
        [LogMsg.ChannelListBanCheckStarted]   = new(LogLevel.Info,    LogCategory.Channel,      "チャンネルリスト内の全チャンネル確認開始"),
        [LogMsg.ChannelListBanCheckCompleted] = new(LogLevel.Info,    LogCategory.Channel,      "チャンネルリスト内の全チャンネル確認完了"),
        [LogMsg.DormantListBanCheckStarted]   = new(LogLevel.Info,    LogCategory.Channel,      "休眠リスト内の全チャンネル確認開始"),
        [LogMsg.DormantListBanCheckCompleted] = new(LogLevel.Info,    LogCategory.Channel,      "休眠リスト内の全チャンネル確認完了"),
        [LogMsg.NoVideosDetected]           = new(LogLevel.Info,    LogCategory.Monitor,      "投稿された動画が見つからなくなったことを検知しました"),
        [LogMsg.NoVideosRecovered]          = new(LogLevel.Info,    LogCategory.Monitor,      "投稿された動画が見つかるようになりました（復帰）"),
        // APIキー
        [LogMsg.ApiKeySaved]               = new(LogLevel.Info,    LogCategory.ApiKeyWindow, "APIキーを保存しました"),
        [LogMsg.ApiKeyChanged]              = new(LogLevel.Info,    LogCategory.ApiKeyWindow, "APIキーを変更しました"),
        [LogMsg.GeminiApiKeySaved]          = new(LogLevel.Info,    LogCategory.ApiKeyWindow, "要約用APIキーを保存しました"),
        [LogMsg.GeminiApiKeyChanged]        = new(LogLevel.Info,    LogCategory.ApiKeyWindow, "要約用APIキーを変更しました"),
        // ログ
        [LogMsg.LogManualDeleted]          = new(LogLevel.Info,    LogCategory.Log,          "ログ手動削除: {0}件 ({1})"),

        // WARNING ─────────────────────────────────────────────────────
        // APIキー
        [LogMsg.ApiKeyNotSet]              = new(LogLevel.Warning, LogCategory.ApiKeyWindow, "APIキーが未設定です。設定タブからAPIキーを入力してください。"),
        [LogMsg.ApiKeyNotSetChannel]       = new(LogLevel.Warning, LogCategory.ApiKeyWindow, "APIキーが設定されていません。設定タブからAPIキーを入力してください。"),
        // クォータ
        [LogMsg.QuotaRiskAdjusted]         = new(LogLevel.Warning, LogCategory.Quota,        "チャンネル数増加によりAPI超過リスク → 監視間隔を{0}分に自動調整"),
        [LogMsg.QuotaAutoIntervalAdjusted]  = new(LogLevel.Warning, LogCategory.Quota,        "クォータ超過のためチェック間隔を自動調整: {0}分"),
        [LogMsg.QuotaWarningOnSave]        = new(LogLevel.Warning, LogCategory.ChannelDetailWindow, "詳細設定保存: クォータ使用量警告 {1}%（チャンネル: {0}）"),
        [LogMsg.QuotaExceededOnSave]       = new(LogLevel.Warning, LogCategory.ChannelDetailWindow, "詳細設定保存: クォータ超過 {1}%（チャンネル: {0}）"),
        // その他
        [LogMsg.AutoRestored]              = new(LogLevel.Warning, LogCategory.Backup,       "自動復元を実行しました（理由: {0}）"),
        [LogMsg.InvalidChannelId]          = new(LogLevel.Warning, LogCategory.Channel,       "不正なチャンネルID: '{0}'"),

        // ERROR ───────────────────────────────────────────────────────
        // ネットワーク
        [LogMsg.NetworkDisconnected]       = new(LogLevel.Error,   LogCategory.Network,      "インターネット接続が切断されました。監視を停止します。"),
        // API呼び出し
        [LogMsg.QuotaExceeded]             = new(LogLevel.Error,   LogCategory.Quota,        "APIクォータ上限に達しました。{0} まで監視を停止します。"),
        [LogMsg.CheckFailed]               = new(LogLevel.Error,   LogCategory.Channel,      "チェック失敗: {0}"),
        [LogMsg.ChannelInfoFailed]         = new(LogLevel.Error,   LogCategory.Channel,      "チャンネル情報取得失敗: {0}"),
        [LogMsg.LatestVideoFailed]         = new(LogLevel.Error,   LogCategory.Channel,      "最新動画取得失敗: {0}"),
        [LogMsg.UploadsPlaylistFailed]     = new(LogLevel.Error,   LogCategory.Channel,      "UploadsPlaylistId 取得失敗({0}): {1}"),
        [LogMsg.VideoKindFailed]           = new(LogLevel.Error,   LogCategory.Channel,      "動画種別一括取得失敗: {0}"),
        [LogMsg.UushFallbackFailed]        = new(LogLevel.Error,   LogCategory.Channel,      "UUSH プレイリスト確認失敗({0}): {1}"),
        [LogMsg.PlaylistFailed]            = new(LogLevel.Error,   LogCategory.Channel,      "プレイリスト取得失敗({0}): {1}"),
        [LogMsg.ApiFallback]               = new(LogLevel.Error,   LogCategory.Channel,      "API失敗、フォールバック"),
        [LogMsg.ChannelBanCheckFailed]     = new(LogLevel.Error,   LogCategory.Channel,      "チャンネルBAN確認失敗({0}): {1}"),
        // スケジューラー
        [LogMsg.SchedulerError]            = new(LogLevel.Error,   LogCategory.VideoFilter,  "スケジューラーエラー: {0}"),
        // Gemini要約
        [LogMsg.GeminiSummaryFailed]       = new(LogLevel.Error,   LogCategory.VideoSummaryPopup, "Gemini要約失敗({0}): {1}"),
        // 外部要約DLL（YTS.dll）バイパス
        [LogMsg.ExternalSummaryBridgeFailed] = new(LogLevel.Error, LogCategory.VideoSummaryPopup, "YTS.dll要約失敗、既存ロジックへフォールバック({0}): {1}"),
        // 要約スクリプト（Plugins）
        [LogMsg.SummaryScriptFailed]         = new(LogLevel.Error, LogCategory.VideoSummaryPopup, "Pluginsスクリプト要約失敗、既存ロジックへフォールバック({0}): {1}"),
        // 通知
        [LogMsg.NotifyFailed]              = new(LogLevel.Error,   LogCategory.Notification, "通知送信失敗: {0}"),
        [LogMsg.TestNotifyFailed]          = new(LogLevel.Error,   LogCategory.Notification, "テスト通知失敗: {0}"),
        [LogMsg.TestNotifyFailedNS]        = new(LogLevel.Error,   LogCategory.Notification, "テスト通知失敗: {0}"),
        // バックアップ・復元
        [LogMsg.BackupFailed]              = new(LogLevel.Error,   LogCategory.Backup,       "自動バックアップに失敗しました: {0}"),
        [LogMsg.AutoRestoreFailed]         = new(LogLevel.Error,   LogCategory.Backup,       "自動復元に失敗しました: {0}"),
        // 設定・起動
        [LogMsg.SettingsLoadError]         = new(LogLevel.Error,   LogCategory.Startup,      "設定読込エラー: {0}"),
        [LogMsg.ChannelListError]          = new(LogLevel.Error,   LogCategory.Startup,      "チャンネル一覧エラー: {0}"),
        [LogMsg.MonitorStartError]         = new(LogLevel.Error,   LogCategory.Startup,      "監視開始エラー: {0}"),
        // UI
        [LogMsg.IconLoadFailed]            = new(LogLevel.Error,   LogCategory.Ui,           "アイコン読込失敗: {0}"),
        [LogMsg.IconDownloadFailed]        = new(LogLevel.Error,   LogCategory.Ui,           "アイコンDL失敗: {0}"),
        [LogMsg.TrayIconInitFailed]        = new(LogLevel.Error,   LogCategory.Ui,           "トレイアイコン初期化失敗: {0}"),

        // DEBUG ───────────────────────────────────────────────────────
        // 監視・通知
        [LogMsg.NotificationSent]          = new(LogLevel.Debug,   LogCategory.Notification, "通知送信: {0}"),
        // 監視フロー
        [LogMsg.NoNew]                     = new(LogLevel.Debug,   LogCategory.Monitor,      "新着なし"),
        [LogMsg.GracePeriodStarted]        = new(LogLevel.Debug,   LogCategory.Monitor,      "猶予開始（残{1}回）: {0}"),
        [LogMsg.VideoNotFound]             = new(LogLevel.Debug,   LogCategory.Monitor,      "対象動画が見つかりませんでした"),
        // 動画通知フィルター
        [LogMsg.LiveSkipped]               = new(LogLevel.Debug,   LogCategory.VideoFilter,  "ライブ待機所スキップ → {0}: {1}"),
        [LogMsg.LiveReSkipped]             = new(LogLevel.Debug,   LogCategory.VideoFilter,  "ライブ待機所再スキップ（通知済）: {0}"),
        [LogMsg.PremiereSkipped]           = new(LogLevel.Debug,   LogCategory.VideoFilter,  "プレミア待機所スキップ → {0}: {1}"),
        [LogMsg.PremiereReSkipped]         = new(LogLevel.Debug,   LogCategory.VideoFilter,  "プレミア待機所再スキップ（通知済）: {0}"),
        [LogMsg.KindFilterSkipped]         = new(LogLevel.Debug,   LogCategory.VideoFilter,  "種別フィルタースキップ [{0}]: {1}"),
        [LogMsg.LiveStartSkipped]          = new(LogLevel.Debug,   LogCategory.VideoFilter,  "ライブ開始スキップ（待機所通知済）: {0}"),
        [LogMsg.PremiereStartSkipped]      = new(LogLevel.Debug,   LogCategory.VideoFilter,  "プレミア開始スキップ（待機所通知済）: {0}"),
        [LogMsg.OldLiveDiscarded]          = new(LogLevel.Debug,   LogCategory.VideoFilter,  "古いライブ破棄: {0}"),
        [LogMsg.OldLiveDiscardedNew]       = new(LogLevel.Debug,   LogCategory.VideoFilter,  "古いライブ破棄（新着ライブ優先）: {0}"),
        [LogMsg.OldLiveDiscardedTrans]     = new(LogLevel.Debug,   LogCategory.VideoFilter,  "古いライブ破棄（遷移済）: {0}"),
        [LogMsg.OldPremiereDiscarded]      = new(LogLevel.Debug,   LogCategory.VideoFilter,  "古いプレミア破棄: {0}"),
        [LogMsg.OldPremiereDiscardedNew]   = new(LogLevel.Debug,   LogCategory.VideoFilter,  "古いプレミア破棄（新着プレミア優先）: {0}"),
        [LogMsg.OldPremiereDiscardedTrans] = new(LogLevel.Debug,   LogCategory.VideoFilter,  "古いプレミア破棄（遷移済）: {0}"),
        [LogMsg.ArchivedLiveNotNotified]   = new(LogLevel.Debug,   LogCategory.VideoFilter,  "アーカイブのため通知せず: {0}"),
        [LogMsg.UpcomingQueued]             = new(LogLevel.Debug,   LogCategory.VideoFilter,  "待機所キュー追加 → {0}: {1}"),
        [LogMsg.UpcomingQueueUpdated]       = new(LogLevel.Debug,   LogCategory.VideoFilter,  "待機所キュー更新 → {0}: {1}"),
        [LogMsg.UpcomingQueueFull]          = new(LogLevel.Debug,   LogCategory.VideoFilter,  "待機所キュー満杯スキップ: {0}"),
        [LogMsg.SchedulerWaitingRoomNotify]  = new(LogLevel.Debug,   LogCategory.VideoFilter,  "スケジューラー待機所通知 [{0}]: {1}"),
        [LogMsg.SchedulerGracePeriodStarted] = new(LogLevel.Debug,   LogCategory.VideoFilter,  "スケジューラー集中監視起動: {0}"),
        [LogMsg.SchedulerWakeUp]             = new(LogLevel.Debug,   LogCategory.VideoFilter,  "スケジューラー再計算"),
        [LogMsg.PendingWindowExpired]        = new(LogLevel.Debug,   LogCategory.VideoFilter,  "監視ウィンドウ終了により予約状態解除: {0}"),
        // 動画検索・表示
        [LogMsg.SearchingVideo]            = new(LogLevel.Debug,   LogCategory.Monitor,      "最新動画を検索中..."),
        [LogMsg.OpenChannelPage]           = new(LogLevel.Debug,   LogCategory.Monitor,      "チャンネルページを開きます（全種別オフ）"),
        [LogMsg.OpenLatestVideo]           = new(LogLevel.Debug,   LogCategory.Monitor,      "最新{0}を開きます"),
        // 通知テスト
        [LogMsg.TestNotifySent]            = new(LogLevel.Debug,   LogCategory.Notification, "テスト通知を送信しました"),
        // APIキー
        [LogMsg.ApiKeyMigrated]            = new(LogLevel.Debug,   LogCategory.ApiKeyWindow, "APIキーを api_key.dat へ移行しました"),
        // バックアップ
        [LogMsg.BackupSaved]               = new(LogLevel.Debug,   LogCategory.Backup,       "自動バックアップを保存しました"),
        // 設定変更
        [LogMsg.SettingDarkMode]            = new(LogLevel.Debug,   LogCategory.Settings,     "ダークモード: {0}"),
        [LogMsg.SettingNoCategoryMode]      = new(LogLevel.Debug,   LogCategory.Settings,     "カテゴリなし表示: {0}"),
        [LogMsg.SettingDesktopNotification] = new(LogLevel.Debug,   LogCategory.Settings,     "デスクトップ通知: {0}"),
        [LogMsg.SettingToastStyle]          = new(LogLevel.Debug,   LogCategory.Settings,     "通知スタイル変更: {0}"),
        [LogMsg.SettingGlobalNotifyUpcoming]= new(LogLevel.Debug,   LogCategory.Settings,     "プレミア/ライブ待機所通知: {0}"),
        [LogMsg.SettingNotificationSound]   = new(LogLevel.Debug,   LogCategory.Settings,     "通知音: {0}"),
        [LogMsg.SettingNotificationSoundSet]= new(LogLevel.Debug,   LogCategory.Settings,     "通知音の種類: {0}"),
        [LogMsg.SettingFlashTaskbar]        = new(LogLevel.Debug,   LogCategory.Settings,     "タスクバー点滅: {0}"),
        [LogMsg.SettingMinimizeToTray]      = new(LogLevel.Debug,   LogCategory.Settings,     "タスクトレイに格納: {0}"),
        [LogMsg.SettingMute]                = new(LogLevel.Debug,   LogCategory.Settings,     "通知ミュート: {0}"),
        [LogMsg.SettingCompactMode]         = new(LogLevel.Debug,   LogCategory.Settings,     "コンパクトモード: {0}"),
        [LogMsg.SettingAlwaysOnTop]         = new(LogLevel.Debug,   LogCategory.Settings,     "ピン留め: {0}"),
        [LogMsg.SettingStartWithWindows]    = new(LogLevel.Debug,   LogCategory.Settings,     "スタートアップ起動: {0}"),
        [LogMsg.SettingCheckInterval]       = new(LogLevel.Debug,   LogCategory.Settings,     "チェック間隔変更: {0}分"),
        [LogMsg.SettingAutoCleanLogs]       = new(LogLevel.Debug,   LogCategory.Settings,     "自動ログ削除: {0}"),
        [LogMsg.SettingLogRetention]        = new(LogLevel.Debug,   LogCategory.Settings,     "ログ保持期間変更: {0}日"),
        // チャンネル一覧・編集
        [LogMsg.EditModeOn]                = new(LogLevel.Debug,   LogCategory.ChannelListUi, "編集モード開始"),
        [LogMsg.EditModeOff]               = new(LogLevel.Debug,   LogCategory.ChannelListUi, "編集モード終了"),
        [LogMsg.ChannelRenamed]            = new(LogLevel.Debug,   LogCategory.ChannelListUi, "名称を変更しました → {0}"),
        [LogMsg.ChannelReordered]           = new(LogLevel.Debug,   LogCategory.ChannelListUi, "チャンネル並び替え: {0}"),
        [LogMsg.CategoryReordered]          = new(LogLevel.Debug,   LogCategory.ChannelListUi, "カテゴリ並び替え: {0}"),
        [LogMsg.KindToggleChanged]          = new(LogLevel.Debug,   LogCategory.ChannelListUi, "種別トグル変更 [{0}]: {1}"),
        [LogMsg.ChannelRowClicked]          = new(LogLevel.Debug,   LogCategory.ChannelListUi, "チャンネル行クリック: {0}"),
        [LogMsg.ChannelNameClicked]         = new(LogLevel.Debug,   LogCategory.ChannelListUi, "チャンネル名クリック: {0}"),
        [LogMsg.ChannelContextClearNew]     = new(LogLevel.Debug,   LogCategory.ChannelListUi, "NEWバッジ消去: {0}"),
        [LogMsg.ChannelContextOpenDetail]   = new(LogLevel.Debug,   LogCategory.ChannelListUi, "詳細設定を開く: {0}"),
        [LogMsg.ChannelContextManualCheck]  = new(LogLevel.Debug,   LogCategory.ChannelListUi, "最新情報を取得: {0}"),
        [LogMsg.ChannelMovedToCategory]     = new(LogLevel.Debug,   LogCategory.ChannelListUi, "カテゴリ移動: {0} → {1}"),
        // カテゴリ操作
        [LogMsg.CategoryCollapsed]          = new(LogLevel.Debug,   LogCategory.CategoryUi,    "カテゴリ{1}: {0}"),
        [LogMsg.CategoryContextClearNew]    = new(LogLevel.Debug,   LogCategory.CategoryUi,    "カテゴリ内NEWバッジ一括消去: {0}"),
        [LogMsg.CategoryContextExpandAll]   = new(LogLevel.Debug,   LogCategory.CategoryUi,    "全カテゴリ展開"),
        [LogMsg.CategoryContextCollapseAll] = new(LogLevel.Debug,   LogCategory.CategoryUi,    "全カテゴリ折り畳み"),
        [LogMsg.CategoryDeleted]            = new(LogLevel.Debug,   LogCategory.CategoryUi,    "カテゴリ削除: {0}"),
        [LogMsg.CategoryRenamed]            = new(LogLevel.Debug,   LogCategory.CategoryUi,    "カテゴリ名変更: {0} → {1}"),
        [LogMsg.CategoryAdded]                   = new(LogLevel.Debug,   LogCategory.CategoryUi,      "カテゴリ作成: {0}"),
        [LogMsg.AddChannelPasteClicked]          = new(LogLevel.Debug,   LogCategory.AddChannelWindow, "チャンネル追加: クリップボードからペースト"),
        [LogMsg.AddChannelDetailTabSwitched]     = new(LogLevel.Debug,   LogCategory.AddChannelWindow, "チャンネル追加: 詳細タブ切替: {0}"),
        [LogMsg.AddChannelWindowClosed]          = new(LogLevel.Debug,   LogCategory.AddChannelWindow, "チャンネル追加ウィンドウを閉じた"),
        [LogMsg.AddChannelNewCategoryPanelOpened]= new(LogLevel.Debug,   LogCategory.AddChannelWindow, "チャンネル追加: 新規カテゴリ入力パネルを開いた"),
        [LogMsg.MovedToDormant]                  = new(LogLevel.Debug,   LogCategory.ChannelListUi,   "{0} を休眠リストへ移動しました"),
        [LogMsg.MovedToActive]                   = new(LogLevel.Debug,   LogCategory.ChannelListUi,   "{0} をチャンネルリストへ移動しました"),
        [LogMsg.DormantChannelMovedToCategory]   = new(LogLevel.Debug,   LogCategory.ChannelListUi,   "カテゴリ移動(休眠): {0} → {1}"),
        [LogMsg.DormantSearchExecuted]           = new(LogLevel.Debug,   LogCategory.ChannelListUi,   "休眠検索: {0}"),
        [LogMsg.DormantSearchCleared]            = new(LogLevel.Debug,   LogCategory.ChannelListUi,   "休眠検索クリア"),
        // ナビゲーション・サイドバー
        [LogMsg.NavPageSwitched]            = new(LogLevel.Debug,   LogCategory.Navigation,   "ページ切替: {0}"),
        [LogMsg.SettingsSubNavSwitched]     = new(LogLevel.Debug,   LogCategory.Navigation,   "設定サブナビ切替: {0}"),
        [LogMsg.SidebarToggled]             = new(LogLevel.Debug,   LogCategory.Navigation,   "サイドバー{0}"),
        [LogMsg.ManualCheckTriggered]       = new(LogLevel.Debug,   LogCategory.Monitor,      "手動チェック実行"),
        [LogMsg.MonitorToggleClicked]       = new(LogLevel.Debug,   LogCategory.Monitor,      "監視{0}"),
        // トレイ
        [LogMsg.WindowToTray]              = new(LogLevel.Debug,   LogCategory.Tray,         "ウィンドウをトレイに格納しました"),
        [LogMsg.TrayWindowOpened]          = new(LogLevel.Debug,   LogCategory.Tray,         "トレイ: ウィンドウを開く"),
        [LogMsg.TrayManualCheckTriggered]  = new(LogLevel.Debug,   LogCategory.Tray,         "トレイ: 今すぐチェック"),
        [LogMsg.TrayMonitorStarted]        = new(LogLevel.Debug,   LogCategory.Tray,         "トレイ: 監視開始"),
        [LogMsg.TrayMonitorStopped]        = new(LogLevel.Debug,   LogCategory.Tray,         "トレイ: 監視停止"),
        // チャンネル追加ウィンドウ
        [LogMsg.AddChannelPreviewClicked]   = new(LogLevel.Debug,   LogCategory.AddChannelWindow, "チャンネル検索: {0}"),
        [LogMsg.ContinuousAddModeChanged]   = new(LogLevel.Debug,   LogCategory.AddChannelWindow, "連続追加モード: {0}"),
        // チャンネル詳細ウィンドウ
        [LogMsg.ChannelDetailSaved]         = new(LogLevel.Debug,   LogCategory.ChannelDetailWindow, "チャンネル詳細保存: {0}"),
        [LogMsg.ChannelDetailSlotInterval]         = new(LogLevel.Debug,   LogCategory.ChannelDetailWindow, "監視間隔設定: {0}: {1}"),
        [LogMsg.ChannelDetailUpcomingModeChanged]  = new(LogLevel.Debug,   LogCategory.ChannelDetailWindow, "{0}通知方法変更: {1}"),
        [LogMsg.ChannelDetailUpcomingLeadChanged]  = new(LogLevel.Debug,   LogCategory.ChannelDetailWindow, "{0}通知タイミング変更: {1}分前"),
        [LogMsg.ChannelDetailCancelled]            = new(LogLevel.Debug,   LogCategory.ChannelDetailWindow, "詳細設定キャンセル"),
        [LogMsg.AddChannelDialogOpened]            = new(LogLevel.Debug,   LogCategory.AddChannelWindow,    "チャンネル追加ダイアログを開いた"),
        [LogMsg.ChannelSearchExecuted]             = new(LogLevel.Debug,   LogCategory.ChannelListUi,       "チャンネル検索: {0}"),
        [LogMsg.ChannelSearchCleared]              = new(LogLevel.Debug,   LogCategory.ChannelListUi,       "チャンネル検索クリア"),
        [LogMsg.ChannelFilterApplied]              = new(LogLevel.Debug,   LogCategory.ChannelListUi,       "チャンネルフィルター: {0}"),
        [LogMsg.ChannelFilterCleared]              = new(LogLevel.Debug,   LogCategory.ChannelListUi,       "チャンネルフィルタークリア"),
        [LogMsg.FavoriteToggleChanged]              = new(LogLevel.Debug,   LogCategory.ChannelListUi,       "お気に入り変更: {0}"),
        [LogMsg.LatestVideoDeletedDetected]          = new(LogLevel.Debug,   LogCategory.Channel,           "表示中の動画が削除されたことを検知しました: {0}"),
        [LogMsg.LatestVideoRecovered]                = new(LogLevel.Debug,   LogCategory.Channel,           "表示中の動画が復帰したことを検知しました: {0}"),
        [LogMsg.LatestVideoTitleChanged]             = new(LogLevel.Debug,   LogCategory.Channel,           "表示中の動画のタイトルが変更されたことを検知しました: {0}（{1} → {2}）"),
        [LogMsg.ChannelListAllAlive]                 = new(LogLevel.Debug,   LogCategory.Channel,           "チャンネルリスト内の全てのチャンネルの生存を確認。"),
        [LogMsg.DormantListAllAlive]                 = new(LogLevel.Debug,   LogCategory.Channel,           "休眠リスト内の全てのチャンネルの生存を確認。"),
        [LogMsg.RecentUploadsPopupOpened]            = new(LogLevel.Debug,   LogCategory.Channel,           "最新動画一覧を開きました: {0}（{1}件）"),
        [LogMsg.RecentUploadThumbnailOpened]         = new(LogLevel.Debug,   LogCategory.Channel,           "最新動画一覧のサムネイルから動画を開きました: {0}"),
        // 動画要約ポップアップ
        [LogMsg.VideoSummaryPopupOpened]           = new(LogLevel.Debug,   LogCategory.VideoSummaryPopup,   "動画情報ポップアップを開きました: {0}"),
        [LogMsg.VideoListPopupOpened]              = new(LogLevel.Debug,   LogCategory.VideoSummaryPopup,   "動画一覧ポップアップを開きました: {0}（{1}件）"),
        [LogMsg.GeminiSummaryRequested]            = new(LogLevel.Debug,   LogCategory.VideoSummaryPopup,   "Gemini要約リクエスト送信: {0}"),
        [LogMsg.GeminiSummarySucceeded]             = new(LogLevel.Debug,   LogCategory.VideoSummaryPopup,   "Gemini要約成功: {0}"),
        [LogMsg.ExternalSummaryBridgeRequested]     = new(LogLevel.Debug,   LogCategory.VideoSummaryPopup,   "YTS.dll経由で要約リクエスト送信: {0}"),
        // 要約スクリプト（Plugins）
        [LogMsg.SummaryScriptRequested]              = new(LogLevel.Debug,   LogCategory.VideoSummaryPopup,   "Pluginsスクリプト経由で要約リクエスト送信: {0}"),
        [LogMsg.SummaryScriptLog]                    = new(LogLevel.Debug,   LogCategory.VideoSummaryPopup,   "Pluginsスクリプトログ: {0}"),
        [LogMsg.ChannelDetailTabSwitched]   = new(LogLevel.Debug,   LogCategory.ChannelDetailWindow, "詳細設定タブ切替: {0}"),
        [LogMsg.ChannelDetailEnabledChanged]= new(LogLevel.Debug,   LogCategory.ChannelDetailWindow, "詳細設定 有効/無効: {1} → {2}"),
        // APIキーウィンドウ
        [LogMsg.ApiKeyEditStarted]          = new(LogLevel.Debug,   LogCategory.ApiKeyWindow, "APIキー変更モード開始"),
        [LogMsg.ApiKeyUnchanged]            = new(LogLevel.Debug,   LogCategory.ApiKeyWindow, "APIキー変更なし"),
        // 要約用APIキー
        [LogMsg.GeminiApiKeyEditStarted]    = new(LogLevel.Debug,   LogCategory.ApiKeyWindow, "要約用APIキー変更モード開始"),
        [LogMsg.GeminiApiKeyUnchanged]      = new(LogLevel.Debug,   LogCategory.ApiKeyWindow, "要約用APIキー変更なし"),
        // アクティビティログウィンドウ
        [LogMsg.ActivityLogWindowOpened]    = new(LogLevel.Debug,   LogCategory.Log,          "アクティビティログウィンドウを開きました"),
        [LogMsg.ActivityLogCleared]         = new(LogLevel.Debug,   LogCategory.Log,          "アクティビティログをクリアしました"),
        [LogMsg.LogFolderOpened]            = new(LogLevel.Debug,   LogCategory.Log,          "ログフォルダを開きました"),
        [LogMsg.ActivityLogSavedToFile]     = new(LogLevel.System,  LogCategory.Log,          "ログをファイルへ保存しました: {0}件"),
        // デバッグ・開発
        [LogMsg.DebugWindowNotFound]       = new(LogLevel.Debug,   LogCategory.Debug,        "DebugWindow 型が見つかりません"),
        [LogMsg.DevToolError]              = new(LogLevel.Debug,   LogCategory.Debug,        "開発者ツール起動エラー: {0}"),
        [LogMsg.DebugDllFailed]            = new(LogLevel.Debug,   LogCategory.Debug,        "Debug DLL 読み込み失敗: {0}"),
        [LogMsg.UiUpdateFailed]            = new(LogLevel.Debug,   LogCategory.Ui,           "UI更新エラー ({0}): {1}"),
    };

    public static void Log(LogMsg id, string? channelName = null, params object[] args)
    {
        if (!_messages.TryGetValue(id, out var def)) return;
        var msg = args.Length > 0
            ? string.Format(def.Template, args)
            : def.Template;

        switch (def.Level)
        {
            case LogLevel.System:  LoggerService.Instance.System(msg, channelName, def.Category);  break;
            case LogLevel.Info:    LoggerService.Instance.Info(msg, channelName, def.Category);    break;
            case LogLevel.Warning: LoggerService.Instance.Warning(msg, channelName, def.Category); break;
            case LogLevel.Error:   LoggerService.Instance.Error(msg, channelName, def.Category);   break;
            case LogLevel.Debug:   LoggerService.Instance.Debug(msg, channelName, def.Category);   break;
        }
    }
}
