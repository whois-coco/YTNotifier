namespace YTNotifier.Constants;

internal static class AppConstants
{
    public const string AppName          = "YTNotifier";
    public const string AppVersion       = "0.8.8";
    public const string DirLogs          = "logs";
    public const string DirSounds        = "Sounds";
    public const string FileApiKey              = "api_key.dat";
    public const string FileGeminiApiKey        = "gemini_api_key.dat";
    public const string BackupFileFilter     = "YTNotifierバックアップ (*.ytbk)|*.ytbk|ZIPファイル (*.zip)|*.zip";

    /// <summary>全曜日ビットマスク（bit0=日〜bit6=土）</summary>
    public const int AllDaysMask = 0b1111111;

    /// <summary>無効化されたコントロールの不透明度</summary>
    public const double DisabledControlOpacity = 0.3;

    private static readonly TimeZoneInfo _pacificTz =
        TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");

    /// <summary>現在のクォータ日付キー（太平洋時間の日付）を返す</summary>
    public static string GetQuotaDayKey()
    {
        var nowPt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _pacificTz);
        return nowPt.Date.ToString("yyyy-MM-dd");
    }
}
