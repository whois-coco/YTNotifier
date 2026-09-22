namespace YTNotifier.Models;

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
    Monitor,              // 監視・チェック実行、動画検索
    VideoFilter,          // 待機所（ライブ/プレミア）フィルター・スケジューラー
    Notification,         // 通知送信・テスト通知
    Channel,              // チャンネルBAN検知・API取得失敗
    ChannelListUi,        // チャンネル一覧画面の操作
    CategoryUi,           // カテゴリの追加・削除・並び替え
    AddChannelWindow,      // チャンネル追加ウィンドウ
    ChannelDetailWindow,   // チャンネル詳細ウィンドウ
    VideoSummaryPopup,    // 動画情報・Gemini要約ポップアップ
    ApiKeyWindow,          // APIキー・要約用APIキーウィンドウ
    Navigation,           // ページ・サイドバー切替
    Tray,                 // タスクトレイ操作
    Settings,             // 設定タブでの変更
    Quota,                // APIクォータ関連
    Network,              // ネットワーク切断・復帰
    Backup,               // バックアップ・自動復元
    Log,                  // ログ削除・アクティビティログウィンドウ
    Startup,              // 起動時エラー（設定読込・チャンネル一覧・監視開始）
    Ui,                   // アイコン・トレイ初期化等の汎用UIエラー
    Debug,                // デバッグ・開発者ツール
}
