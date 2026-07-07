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
    UuFallbackFailed          = 4018,  // {0}=videoId {1}=message
    PlaylistFailed            = 4019,  // {0}=playlistId {1}=message
    ApiFallback               = 4008,
    ChannelBanCheckFailed     = 4022,  // {0}=channelId {1}=message
    // Gemini要約
    GeminiSummaryFailed       = 4021,  // {0}=videoId {1}=message
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
    SettingFlashTaskbar        = 5032,  // {0}=ON/OFF
    SettingMinimizeToTray      = 5033,  // {0}=ON/OFF
    SettingMute                = 5034,  // {0}=ON/OFF
    SettingCompactMode         = 5035,  // {0}=ON/OFF
    SettingAlwaysOnTop         = 5036,  // {0}=ON/OFF
    SettingStartWithWindows    = 5037,  // {0}=ON/OFF
    SettingCheckInterval       = 5038,  // {0}=minutes
    QuotaAutoIntervalAdjusted  = 5079,  // {0}=minutes
    SettingLogLevel            = 5039,  // {0}=level
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
    KindToggleChanged          = 5044,  // {0}=channelName {1}=kind {2}=ON/OFF
    ChannelRowClicked          = 5049,  // {0}=channelName
    ChannelContextClearNew     = 5050,  // {0}=channelName
    ChannelContextOpenDetail   = 5051,  // {0}=channelName
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
    ChannelDetailUpcomingModeChanged  = 5091,  // {0}=modeLabel
    ChannelDetailUpcomingLeadChanged  = 5092,  // {0}=minutes
    ChannelDetailCancelled            = 5093,
    AddChannelDialogOpened            = 5094,
    ChannelSearchExecuted             = 5095,  // {0}=query
    ChannelSearchCleared              = 5096,
    ChannelFilterApplied              = 5107,  // {0}=filterLabel
    ChannelFilterCleared              = 5108,
    FavoriteToggleChanged              = 5118,  // {0}=ON/OFF
    // チャンネルBAN・動画削除検知
    ChannelBanned                      = 5119,
    ChannelBanRecovered                = 5120,
    LatestVideoDeletedDetected         = 5121,  // {0}=videoId
    // 動画要約ポップアップ
    VideoSummaryPopupOpened           = 5109,  // {0}=channelName
    GeminiSummaryRequested            = 5110,  // {0}=videoId
    GeminiSummaryCacheHit             = 5111,  // {0}=videoId
    GeminiSummarySucceeded            = 5112,  // {0}=videoId
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
    // デバッグ・開発
    DebugWindowNotFound       = 5012,
    DevToolError              = 5013,  // {0}=message
    DebugDllFailed            = 5014,  // {0}=message
    UiUpdateFailed            = 5086,  // {0}=methodName {1}=message
}

public static class AppLogger
{
    private record MessageDef(LogLevel Level, string Template);

    private static readonly Dictionary<LogMsg, MessageDef> _messages = new()
    {
        // SYSTEM ─────────────────────────────────────────────────────
        // 監視
        [LogMsg.MonitorStarted]            = new(LogLevel.System,  "監視を開始しました({0})"),
        [LogMsg.MonitorStopped]            = new(LogLevel.System,  "監視を停止しました"),
        [LogMsg.NetworkRestored]           = new(LogLevel.System,  "インターネット接続が回復しました。監視を再開します。"),
        // ログ
        [LogMsg.AutoLogDeleted]            = new(LogLevel.System,  "起動時ログ自動削除: {0}件"),
        // クォータ
        [LogMsg.QuotaResumed]              = new(LogLevel.System,  "APIクォータをリセットしました。監視を再開します。"),
        // 設定
        [LogMsg.SettingLogLevel]           = new(LogLevel.System,  "ログレベル変更: {0}"),
        [LogMsg.SettingBackupExported]      = new(LogLevel.System,  "バックアップをエクスポートしました: {0}"),
        [LogMsg.SettingBackupImported]      = new(LogLevel.System,  "バックアップをインポートしました: {0}"),

        // INFO ────────────────────────────────────────────────────────
        // 監視・通知
        [LogMsg.CheckStarted]              = new(LogLevel.Info,    "{0}開始 ({1}/{2}チャンネル)"),
        [LogMsg.CheckCompleted]            = new(LogLevel.Info,    "{0}完了"),
        [LogMsg.NewVideo]                  = new(LogLevel.Info,    "新着{0}: {1}"),
        // チャンネル
        [LogMsg.ChannelAdded]              = new(LogLevel.Info,    "チャンネルを追加しました"),
        [LogMsg.ChannelRemoved]            = new(LogLevel.Info,    "チャンネルを削除しました"),
        // APIキー
        [LogMsg.ApiKeySaved]               = new(LogLevel.Info,    "APIキーを保存しました"),
        [LogMsg.ApiKeyChanged]              = new(LogLevel.Info,    "APIキーを変更しました"),
        [LogMsg.GeminiApiKeySaved]          = new(LogLevel.Info,    "要約用APIキーを保存しました"),
        [LogMsg.GeminiApiKeyChanged]        = new(LogLevel.Info,    "要約用APIキーを変更しました"),
        // ログ
        [LogMsg.LogManualDeleted]          = new(LogLevel.Info,    "ログ手動削除: {0}件 ({1})"),

        // WARNING ─────────────────────────────────────────────────────
        // APIキー
        [LogMsg.ApiKeyNotSet]              = new(LogLevel.Warning, "APIキーが未設定です。設定タブからAPIキーを入力してください。"),
        [LogMsg.ApiKeyNotSetChannel]       = new(LogLevel.Warning, "APIキーが設定されていません。設定タブからAPIキーを入力してください。"),
        // クォータ
        [LogMsg.QuotaRiskAdjusted]         = new(LogLevel.Warning, "チャンネル数増加によりAPI超過リスク → 監視間隔を{0}分に自動調整"),
        [LogMsg.QuotaAutoIntervalAdjusted]  = new(LogLevel.Warning, "クォータ超過のためチェック間隔を自動調整: {0}分"),
        [LogMsg.QuotaWarningOnSave]        = new(LogLevel.Warning, "詳細設定保存: クォータ使用量警告 {1}%（チャンネル: {0}）"),
        [LogMsg.QuotaExceededOnSave]       = new(LogLevel.Warning, "詳細設定保存: クォータ超過 {1}%（チャンネル: {0}）"),
        // その他
        [LogMsg.AutoRestored]              = new(LogLevel.Warning, "自動復元を実行しました（理由: {0}）"),
        [LogMsg.InvalidChannelId]          = new(LogLevel.Warning, "不正なチャンネルID: '{0}'"),

        // ERROR ───────────────────────────────────────────────────────
        // ネットワーク
        [LogMsg.NetworkDisconnected]       = new(LogLevel.Error,   "インターネット接続が切断されました。監視を停止します。"),
        // API呼び出し
        [LogMsg.QuotaExceeded]             = new(LogLevel.Error,   "APIクォータ上限に達しました。{0} まで監視を停止します。"),
        [LogMsg.CheckFailed]               = new(LogLevel.Error,   "チェック失敗: {0}"),
        [LogMsg.ChannelInfoFailed]         = new(LogLevel.Error,   "チャンネル情報取得失敗: {0}"),
        [LogMsg.LatestVideoFailed]         = new(LogLevel.Error,   "最新動画取得失敗: {0}"),
        [LogMsg.UploadsPlaylistFailed]     = new(LogLevel.Error,   "UploadsPlaylistId 取得失敗({0}): {1}"),
        [LogMsg.VideoKindFailed]           = new(LogLevel.Error,   "動画種別一括取得失敗: {0}"),
        [LogMsg.UuFallbackFailed]          = new(LogLevel.Error,   "UU プレイリスト確認失敗({0}): {1}"),
        [LogMsg.PlaylistFailed]            = new(LogLevel.Error,   "プレイリスト取得失敗({0}): {1}"),
        [LogMsg.ApiFallback]               = new(LogLevel.Error,   "API失敗、フォールバック"),
        [LogMsg.ChannelBanCheckFailed]     = new(LogLevel.Error,   "チャンネルBAN確認失敗({0}): {1}"),
        // Gemini要約
        [LogMsg.GeminiSummaryFailed]       = new(LogLevel.Error,   "Gemini要約失敗({0}): {1}"),
        // 通知
        [LogMsg.NotifyFailed]              = new(LogLevel.Error,   "通知送信失敗: {0}"),
        [LogMsg.TestNotifyFailed]          = new(LogLevel.Error,   "テスト通知失敗: {0}"),
        [LogMsg.TestNotifyFailedNS]        = new(LogLevel.Error,   "テスト通知失敗: {0}"),
        // バックアップ・復元
        [LogMsg.BackupFailed]              = new(LogLevel.Error,   "自動バックアップに失敗しました: {0}"),
        [LogMsg.AutoRestoreFailed]         = new(LogLevel.Error,   "自動復元に失敗しました: {0}"),
        // 設定・起動
        [LogMsg.SettingsLoadError]         = new(LogLevel.Error,   "設定読込エラー: {0}"),
        [LogMsg.ChannelListError]          = new(LogLevel.Error,   "チャンネル一覧エラー: {0}"),
        [LogMsg.MonitorStartError]         = new(LogLevel.Error,   "監視開始エラー: {0}"),
        // UI
        [LogMsg.IconLoadFailed]            = new(LogLevel.Error,   "アイコン読込失敗: {0}"),
        [LogMsg.IconDownloadFailed]        = new(LogLevel.Error,   "アイコンDL失敗: {0}"),
        [LogMsg.TrayIconInitFailed]        = new(LogLevel.Error,   "トレイアイコン初期化失敗: {0}"),

        // DEBUG ───────────────────────────────────────────────────────
        // 監視・通知
        [LogMsg.NotificationSent]          = new(LogLevel.Debug,   "通知送信: {0}"),
        // 監視フロー
        [LogMsg.NoNew]                     = new(LogLevel.Debug,   "新着なし"),
        [LogMsg.GracePeriodStarted]        = new(LogLevel.Debug,   "猶予開始（残{1}回）: {0}"),
        [LogMsg.VideoNotFound]             = new(LogLevel.Debug,   "対象動画が見つかりませんでした"),
        // 動画通知フィルター
        [LogMsg.LiveSkipped]               = new(LogLevel.Debug,   "ライブ待機所スキップ → {0}: {1}"),
        [LogMsg.LiveReSkipped]             = new(LogLevel.Debug,   "ライブ待機所再スキップ（通知済）: {0}"),
        [LogMsg.PremiereSkipped]           = new(LogLevel.Debug,   "プレミア待機所スキップ → {0}: {1}"),
        [LogMsg.PremiereReSkipped]         = new(LogLevel.Debug,   "プレミア待機所再スキップ（通知済）: {0}"),
        [LogMsg.KindFilterSkipped]         = new(LogLevel.Debug,   "種別フィルタースキップ [{0}]: {1}"),
        [LogMsg.LiveStartSkipped]          = new(LogLevel.Debug,   "ライブ開始スキップ（待機所通知済）: {0}"),
        [LogMsg.PremiereStartSkipped]      = new(LogLevel.Debug,   "プレミア開始スキップ（待機所通知済）: {0}"),
        [LogMsg.OldLiveDiscarded]          = new(LogLevel.Debug,   "古いライブ破棄: {0}"),
        [LogMsg.OldLiveDiscardedNew]       = new(LogLevel.Debug,   "古いライブ破棄（新着ライブ優先）: {0}"),
        [LogMsg.OldLiveDiscardedTrans]     = new(LogLevel.Debug,   "古いライブ破棄（遷移済）: {0}"),
        [LogMsg.OldPremiereDiscarded]      = new(LogLevel.Debug,   "古いプレミア破棄: {0}"),
        [LogMsg.OldPremiereDiscardedNew]   = new(LogLevel.Debug,   "古いプレミア破棄（新着プレミア優先）: {0}"),
        [LogMsg.OldPremiereDiscardedTrans] = new(LogLevel.Debug,   "古いプレミア破棄（遷移済）: {0}"),
        [LogMsg.UpcomingQueued]             = new(LogLevel.Debug,   "待機所キュー追加 → {0}: {1}"),
        [LogMsg.UpcomingQueueUpdated]       = new(LogLevel.Debug,   "待機所キュー更新 → {0}: {1}"),
        [LogMsg.UpcomingQueueFull]          = new(LogLevel.Debug,   "待機所キュー満杯スキップ: {0}"),
        [LogMsg.SchedulerWaitingRoomNotify]  = new(LogLevel.Debug,   "スケジューラー待機所通知 [{0}]: {1}"),
        [LogMsg.SchedulerGracePeriodStarted] = new(LogLevel.Debug,   "スケジューラー集中監視起動: {0}"),
        [LogMsg.SchedulerWakeUp]             = new(LogLevel.Debug,   "スケジューラー再計算"),
        [LogMsg.PendingWindowExpired]        = new(LogLevel.Debug,   "監視ウィンドウ終了により予約状態解除: {0}"),
        // 動画検索・表示
        [LogMsg.SearchingVideo]            = new(LogLevel.Debug,   "最新動画を検索中..."),
        [LogMsg.OpenChannelPage]           = new(LogLevel.Debug,   "チャンネルページを開きます（全種別オフ）"),
        [LogMsg.OpenLatestVideo]           = new(LogLevel.Debug,   "最新{0}を開きます"),
        // 通知テスト
        [LogMsg.TestNotifySent]            = new(LogLevel.Debug,   "テスト通知を送信しました"),
        // APIキー
        [LogMsg.ApiKeyMigrated]            = new(LogLevel.Debug,   "APIキーを api_key.dat へ移行しました"),
        // バックアップ
        [LogMsg.BackupSaved]               = new(LogLevel.Debug,   "自動バックアップを保存しました"),
        // 設定変更
        [LogMsg.SettingDarkMode]            = new(LogLevel.Debug,   "ダークモード: {0}"),
        [LogMsg.SettingNoCategoryMode]      = new(LogLevel.Debug,   "カテゴリなし表示: {0}"),
        [LogMsg.SettingDesktopNotification] = new(LogLevel.Debug,   "デスクトップ通知: {0}"),
        [LogMsg.SettingToastStyle]          = new(LogLevel.Debug,   "通知スタイル変更: {0}"),
        [LogMsg.SettingGlobalNotifyUpcoming]= new(LogLevel.Debug,   "プレミア/ライブ待機所通知: {0}"),
        [LogMsg.SettingNotificationSound]   = new(LogLevel.Debug,   "通知音: {0}"),
        [LogMsg.SettingFlashTaskbar]        = new(LogLevel.Debug,   "タスクバー点滅: {0}"),
        [LogMsg.SettingMinimizeToTray]      = new(LogLevel.Debug,   "タスクトレイに格納: {0}"),
        [LogMsg.SettingMute]                = new(LogLevel.Debug,   "通知ミュート: {0}"),
        [LogMsg.SettingCompactMode]         = new(LogLevel.Debug,   "コンパクトモード: {0}"),
        [LogMsg.SettingAlwaysOnTop]         = new(LogLevel.Debug,   "ピン留め: {0}"),
        [LogMsg.SettingStartWithWindows]    = new(LogLevel.Debug,   "スタートアップ起動: {0}"),
        [LogMsg.SettingCheckInterval]       = new(LogLevel.Debug,   "チェック間隔変更: {0}分"),
        [LogMsg.SettingAutoCleanLogs]       = new(LogLevel.Debug,   "自動ログ削除: {0}"),
        [LogMsg.SettingLogRetention]        = new(LogLevel.Debug,   "ログ保持期間変更: {0}日"),
        // チャンネル一覧・編集
        [LogMsg.EditModeOn]                = new(LogLevel.Debug,   "編集モード開始"),
        [LogMsg.EditModeOff]               = new(LogLevel.Debug,   "編集モード終了"),
        [LogMsg.ChannelRenamed]            = new(LogLevel.Debug,   "名称を変更しました → {0}"),
        [LogMsg.ChannelReordered]           = new(LogLevel.Debug,   "チャンネル並び替え: {0}"),
        [LogMsg.CategoryReordered]          = new(LogLevel.Debug,   "カテゴリ並び替え: {0}"),
        [LogMsg.KindToggleChanged]          = new(LogLevel.Debug,   "種別トグル変更 [{0}]: {1}"),
        [LogMsg.ChannelRowClicked]          = new(LogLevel.Debug,   "チャンネル行クリック: {0}"),
        [LogMsg.ChannelContextClearNew]     = new(LogLevel.Debug,   "NEWバッジ消去: {0}"),
        [LogMsg.ChannelContextOpenDetail]   = new(LogLevel.Debug,   "詳細設定を開く: {0}"),
        [LogMsg.ChannelMovedToCategory]     = new(LogLevel.Debug,   "カテゴリ移動: {0} → {1}"),
        // カテゴリ操作
        [LogMsg.CategoryCollapsed]          = new(LogLevel.Debug,   "カテゴリ{1}: {0}"),
        [LogMsg.CategoryContextClearNew]    = new(LogLevel.Debug,   "カテゴリ内NEWバッジ一括消去: {0}"),
        [LogMsg.CategoryContextExpandAll]   = new(LogLevel.Debug,   "全カテゴリ展開"),
        [LogMsg.CategoryContextCollapseAll] = new(LogLevel.Debug,   "全カテゴリ折り畳み"),
        [LogMsg.CategoryDeleted]            = new(LogLevel.Debug,   "カテゴリ削除: {0}"),
        [LogMsg.CategoryRenamed]            = new(LogLevel.Debug,   "カテゴリ名変更: {0} → {1}"),
        [LogMsg.CategoryAdded]                   = new(LogLevel.Debug,   "カテゴリ作成: {0}"),
        [LogMsg.AddChannelPasteClicked]          = new(LogLevel.Debug,   "チャンネル追加: クリップボードからペースト"),
        [LogMsg.AddChannelDetailTabSwitched]     = new(LogLevel.Debug,   "チャンネル追加: 詳細タブ切替: {0}"),
        [LogMsg.AddChannelWindowClosed]          = new(LogLevel.Debug,   "チャンネル追加ウィンドウを閉じた"),
        [LogMsg.AddChannelNewCategoryPanelOpened]= new(LogLevel.Debug,   "チャンネル追加: 新規カテゴリ入力パネルを開いた"),
        [LogMsg.MovedToDormant]                  = new(LogLevel.Debug,   "{0} を休眠リストへ移動しました"),
        [LogMsg.MovedToActive]                   = new(LogLevel.Debug,   "{0} を監視リストへ移動しました"),
        [LogMsg.DormantChannelMovedToCategory]   = new(LogLevel.Debug,   "カテゴリ移動(休眠): {0} → {1}"),
        [LogMsg.DormantSearchExecuted]           = new(LogLevel.Debug,   "休眠検索: {0}"),
        [LogMsg.DormantSearchCleared]            = new(LogLevel.Debug,   "休眠検索クリア"),
        // ナビゲーション・サイドバー
        [LogMsg.NavPageSwitched]            = new(LogLevel.Debug,   "ページ切替: {0}"),
        [LogMsg.SettingsSubNavSwitched]     = new(LogLevel.Debug,   "設定サブナビ切替: {0}"),
        [LogMsg.SidebarToggled]             = new(LogLevel.Debug,   "サイドバー{0}"),
        [LogMsg.ManualCheckTriggered]       = new(LogLevel.Debug,   "手動チェック実行"),
        [LogMsg.MonitorToggleClicked]       = new(LogLevel.Debug,   "監視{0}"),
        // トレイ
        [LogMsg.WindowToTray]              = new(LogLevel.Debug,   "ウィンドウをトレイに格納しました"),
        [LogMsg.TrayWindowOpened]          = new(LogLevel.Debug,   "トレイ: ウィンドウを開く"),
        [LogMsg.TrayManualCheckTriggered]  = new(LogLevel.Debug,   "トレイ: 今すぐチェック"),
        [LogMsg.TrayMonitorStarted]        = new(LogLevel.Debug,   "トレイ: 監視開始"),
        [LogMsg.TrayMonitorStopped]        = new(LogLevel.Debug,   "トレイ: 監視停止"),
        // チャンネル追加ウィンドウ
        [LogMsg.AddChannelPreviewClicked]   = new(LogLevel.Debug,   "チャンネル検索: {0}"),
        [LogMsg.ContinuousAddModeChanged]   = new(LogLevel.Debug,   "連続追加モード: {0}"),
        // チャンネル詳細ウィンドウ
        [LogMsg.ChannelDetailSaved]         = new(LogLevel.Debug,   "チャンネル詳細保存: {0}"),
        [LogMsg.ChannelDetailSlotInterval]         = new(LogLevel.Debug,   "監視間隔設定: {0}: {1}"),
        [LogMsg.ChannelDetailUpcomingModeChanged]  = new(LogLevel.Debug,   "通知方法変更: {0}"),
        [LogMsg.ChannelDetailUpcomingLeadChanged]  = new(LogLevel.Debug,   "通知タイミング変更: {0}分前"),
        [LogMsg.ChannelDetailCancelled]            = new(LogLevel.Debug,   "詳細設定キャンセル"),
        [LogMsg.AddChannelDialogOpened]            = new(LogLevel.Debug,   "チャンネル追加ダイアログを開いた"),
        [LogMsg.ChannelSearchExecuted]             = new(LogLevel.Debug,   "チャンネル検索: {0}"),
        [LogMsg.ChannelSearchCleared]              = new(LogLevel.Debug,   "チャンネル検索クリア"),
        [LogMsg.ChannelFilterApplied]              = new(LogLevel.Debug,   "チャンネルフィルター: {0}"),
        [LogMsg.ChannelFilterCleared]              = new(LogLevel.Debug,   "チャンネルフィルタークリア"),
        [LogMsg.FavoriteToggleChanged]              = new(LogLevel.Debug,   "お気に入り変更: {0}"),
        [LogMsg.ChannelBanned]                       = new(LogLevel.Info,    "チャンネルBAN／削除を検知しました"),
        [LogMsg.ChannelBanRecovered]                 = new(LogLevel.Info,    "チャンネルの利用停止状態が解除されました"),
        [LogMsg.LatestVideoDeletedDetected]          = new(LogLevel.Debug,   "表示中の動画が削除されたことを検知しました: {0}"),
        // 動画要約ポップアップ
        [LogMsg.VideoSummaryPopupOpened]           = new(LogLevel.Debug,   "動画情報ポップアップを開きました: {0}"),
        [LogMsg.GeminiSummaryRequested]            = new(LogLevel.Debug,   "Gemini要約リクエスト送信: {0}"),
        [LogMsg.GeminiSummaryCacheHit]              = new(LogLevel.Debug,   "Gemini要約キャッシュヒット: {0}"),
        [LogMsg.GeminiSummarySucceeded]             = new(LogLevel.Debug,   "Gemini要約成功: {0}"),
        [LogMsg.ChannelDetailTabSwitched]   = new(LogLevel.Debug,   "詳細設定タブ切替: {0}"),
        [LogMsg.ChannelDetailEnabledChanged]= new(LogLevel.Debug,   "詳細設定 有効/無効: {1} → {2}"),
        // APIキーウィンドウ
        [LogMsg.ApiKeyEditStarted]          = new(LogLevel.Debug,   "APIキー変更モード開始"),
        [LogMsg.ApiKeyUnchanged]            = new(LogLevel.Debug,   "APIキー変更なし"),
        // 要約用APIキー
        [LogMsg.GeminiApiKeyEditStarted]    = new(LogLevel.Debug,   "要約用APIキー変更モード開始"),
        [LogMsg.GeminiApiKeyUnchanged]      = new(LogLevel.Debug,   "要約用APIキー変更なし"),
        // アクティビティログウィンドウ
        [LogMsg.ActivityLogWindowOpened]    = new(LogLevel.Debug,   "アクティビティログウィンドウを開きました"),
        [LogMsg.ActivityLogCleared]         = new(LogLevel.Debug,   "アクティビティログをクリアしました"),
        [LogMsg.LogFolderOpened]            = new(LogLevel.Debug,   "ログフォルダを開きました"),
        // デバッグ・開発
        [LogMsg.DebugWindowNotFound]       = new(LogLevel.Debug,   "DebugWindow 型が見つかりません"),
        [LogMsg.DevToolError]              = new(LogLevel.Debug,   "開発者ツール起動エラー: {0}"),
        [LogMsg.DebugDllFailed]            = new(LogLevel.Debug,   "Debug DLL 読み込み失敗: {0}"),
        [LogMsg.UiUpdateFailed]            = new(LogLevel.Debug,   "UI更新エラー ({0}): {1}"),
    };

    public static void Log(LogMsg id, string? channelName = null, params object[] args)
    {
        if (!_messages.TryGetValue(id, out var def)) return;
        var msg = args.Length > 0
            ? string.Format(def.Template, args)
            : def.Template;

        switch (def.Level)
        {
            case LogLevel.System:  LoggerService.Instance.System(msg, channelName);  break;
            case LogLevel.Info:    LoggerService.Instance.Info(msg, channelName);    break;
            case LogLevel.Warning: LoggerService.Instance.Warning(msg, channelName); break;
            case LogLevel.Error:   LoggerService.Instance.Error(msg, channelName);   break;
            case LogLevel.Debug:   LoggerService.Instance.Debug(msg, channelName);   break;
        }
    }
}
