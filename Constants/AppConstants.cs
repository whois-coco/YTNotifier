namespace YTNotifier.Constants;

internal static class AppConstants
{
    public const string AppName          = "YTNotifier";
    public const string DirLogs          = "logs";
    public const string DirIcons         = "icons";
    public const string DirSounds        = "Sounds";
    public const string FileApiKey       = "api_key.dat";
    public const string BackupFileFilter     = "YTNotifierバックアップ (*.ytbk)|*.ytbk|ZIPファイル (*.zip)|*.zip";
    public const string BackupFileFilterSave = "YTNotifierバックアップ (*.ytbk)|*.ytbk";

    /// <summary>YouTube チャンネル ID の固定長（UC + 22文字）</summary>
    public const int ChannelIdLength = 24;

    /// <summary>ライブ/プレミア配信開始後に継続チェックする猶予回数</summary>
    public const int GracePeriodAttempts = 10;

    /// <summary>サムネイルキャッシュの有効期間（日数）</summary>
    public const int ThumbnailCacheDays = 7;

    /// <summary>チェック間隔の推奨候補（分）</summary>
    public static readonly int[] CheckIntervalCandidates = { 1, 5, 10, 30, 60 };

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

    /// <summary>現在のクォータ日付キー（太平洋時間の日付）を返す</summary>
    public static string GetQuotaDayKey()
    {
        var nowPt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _pacificTz);
        return nowPt.Date.ToString("yyyy-MM-dd");
    }
}
