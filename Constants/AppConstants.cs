namespace YTNotifier.Constants;

internal static class AppConstants
{
    public const string AppName          = "YTNotifier";
    public const string AppVersion       = "0.8.5";
    public const string GitHubReleasesApiUrl = "https://api.github.com/repos/whois-coco/YTNotifier/releases/latest";
    public const string GitHubReleasesPageUrl = "https://github.com/whois-coco/YTNotifier/releases/latest";
    public const string DirLogs          = "logs";
    public const string DirIcons         = "icons";
    public const string DirSounds        = "Sounds";

    /// <summary>トースト通知用画像の一時保存先ディレクトリ名（%TEMP% 配下）</summary>
    public const string DirToastTempImages = "YTNotifier_ToastImages";

    /// <summary>トースト通知用サムネイル一時ファイルの拡張子</summary>
    public const string ToastThumbnailTempExtension = ".jpg";

    /// <summary>トースト通知用アイコン一時ファイルの拡張子</summary>
    public const string ToastIconTempExtension = ".png";

    /// <summary>トースト通知用一時画像ファイルの削除待機時間（ミリ秒）。通知プラットフォームの画像読込猶予</summary>
    public const int ToastTempImageCleanupDelayMs = 10000;
    public const string FileApiKey              = "api_key.dat";
    public const string FileDormantCategories   = "dormant_categories.json";
    public const string FileGeminiApiKey        = "gemini_api_key.dat";
    public const string FileGeminiSummaryCache  = "gemini_summary_cache.json";
    public const string BackupFileFilter     = "YTNotifierバックアップ (*.ytbk)|*.ytbk|ZIPファイル (*.zip)|*.zip";
    public const string BackupFileFilterSave = "YTNotifierバックアップ (*.ytbk)|*.ytbk";
    public const string GoogleCloudConsoleUrl = "https://console.cloud.google.com/";

    /// <summary>初回APIキー設定ウィザードの手順画像取得元URL（step1.png〜step{StepByStepImageCount}.png）</summary>
    public const string StepByStepImageBaseUrl = "https://raw.githubusercontent.com/whois-coco/YTNotifier/main/stepbystep/";

    /// <summary>初回APIキー設定ウィザードの総ステップ数</summary>
    public const int ApiKeySetupStepCount = 20;

    /// <summary>初回APIキー設定ウィザードの手順画像枚数</summary>
    public const int StepByStepImageCount = 18;

    /// <summary>YouTube チャンネル ID の固定長（UC + 22文字）</summary>
    public const int ChannelIdLength = 24;

    /// <summary>ライブ/プレミア配信開始後に継続チェックする猶予回数</summary>
    public const int GracePeriodAttempts = 10;

    /// <summary>待機所通知タイミングのデフォルト（leadMinutes=0 の既存データ向けフォールバック）</summary>
    public const int DefaultUpcomingLeadMinutes = 5;

    /// <summary>チャンネルごとの upcoming キュー上限数</summary>
    public const int MaxPendingQueueSize = 10;

    /// <summary>チャンネルカードに「配信予定」を表示する開始予定時刻までの上限（分）</summary>
    public const int UpcomingDisplayWindowMinutes = 30;

    /// <summary>チャンネルカードの動画タイトル表示最大文字数（超過分は省略）</summary>
    public const int ChannelCardTitleMaxLength = 16;

    /// <summary>pending ライブ/プレミアエントリの破棄閾値（日数）。ScheduledAt がこの日数以上前のエントリは起動時に削除する</summary>
    public const int StalePendingEntryDays = 14;

    /// <summary>ライブ配信中・プレミア公開中の終了検知チェック間隔（分）</summary>
    public const int ActiveLiveCheckIntervalMinutes = 15;

    /// <summary>チェック間隔の推奨候補（分）</summary>
    public static readonly int[] CheckIntervalCandidates = { 1, 5, 10, 30, 60 };

    /// <summary>Gemini 要約に使用するモデル名</summary>
    public const string GeminiModelName = "gemini-2.5-flash";

    /// <summary>Gemini へ動画を渡す際の固定 mime_type</summary>
    public const string GeminiVideoMimeType = "video/mp4";

    /// <summary>Gemini API リクエストのタイムアウト（分）</summary>
    public const int GeminiRequestTimeoutMinutes = 3;

    /// <summary>Gemini 要約リクエストのプロンプト（1行目=主題、2行目以降=詳細要約）</summary>
    public const string GeminiSummaryPromptText =
        "この動画の内容を要約してください。1行目に「ざっくり言うとどんな動画か」が一言でわかる見出しを、" +
        "2行目以降に箇条書き（「・」始まり）で3〜5行程度の要約を記載してください。";

    /// <summary>Gemini 要約リクエストの thinking トークン予算（0=思考無効化で高速化）</summary>
    public const int GeminiThinkingBudget = 0;

    /// <summary>音声中心の要約実験用：動画フレームのサンプリング頻度（fps）を極小化</summary>
    public const double GeminiAudioOnlyFps = 0.1;

    private static readonly TimeZoneInfo _pacificTz =
        TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");

    /// <summary>次回クォータリセット時刻をローカル時刻で返す（DST対応）</summary>
    public static DateTime GetNextQuotaResetTime()
    {
        var nowPt          = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _pacificTz);
        var nextMidnightPt = DateTime.SpecifyKind(nowPt.Date.AddDays(1), DateTimeKind.Unspecified);
        var nextMidnightUtc = TimeZoneInfo.ConvertTimeToUtc(nextMidnightPt, _pacificTz);
        return TimeZoneInfo.ConvertTimeFromUtc(nextMidnightUtc, TimeZoneInfo.Local);
    }

    /// <summary>本日の太平洋時間深夜0時（クォータリセット時刻）をローカル時刻で返す（DST対応）</summary>
    public static DateTime GetTodayQuotaResetTime()
    {
        var nowPt           = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _pacificTz);
        var todayMidnightPt = DateTime.SpecifyKind(nowPt.Date, DateTimeKind.Unspecified);
        var todayMidnightUtc = TimeZoneInfo.ConvertTimeToUtc(todayMidnightPt, _pacificTz);
        return TimeZoneInfo.ConvertTimeFromUtc(todayMidnightUtc, TimeZoneInfo.Local);
    }

    /// <summary>現在のクォータ日付キー（太平洋時間の日付）を返す</summary>
    public static string GetQuotaDayKey()
    {
        var nowPt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _pacificTz);
        return nowPt.Date.ToString("yyyy-MM-dd");
    }
}