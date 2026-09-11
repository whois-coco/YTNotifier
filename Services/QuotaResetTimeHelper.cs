namespace YTNotifier.Services;

/// <summary>APIクォータの日次リセット時刻（太平洋時間深夜0時）をローカル時刻に変換するヘルパー。YouTube・Gemini双方のクォータが同じ基準でリセットされるため共通化</summary>
public static class QuotaResetTimeHelper
{
    private static readonly TimeZoneInfo _pacificTz =
        TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");

    /// <summary>本日の太平洋時間深夜0時（クォータリセット時刻）をローカル時刻で返す（DST対応）</summary>
    public static DateTime GetTodayQuotaResetTime()
    {
        var nowPt            = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _pacificTz);
        var todayMidnightPt  = DateTime.SpecifyKind(nowPt.Date, DateTimeKind.Unspecified);
        var todayMidnightUtc = TimeZoneInfo.ConvertTimeToUtc(todayMidnightPt, _pacificTz);
        return TimeZoneInfo.ConvertTimeFromUtc(todayMidnightUtc, TimeZoneInfo.Local);
    }

    /// <summary>次回クォータリセット時刻をローカル時刻で返す（DST対応）</summary>
    public static DateTime GetNextQuotaResetTime()
    {
        var nowPt           = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _pacificTz);
        var nextMidnightPt  = DateTime.SpecifyKind(nowPt.Date.AddDays(1), DateTimeKind.Unspecified);
        var nextMidnightUtc = TimeZoneInfo.ConvertTimeToUtc(nextMidnightPt, _pacificTz);
        return TimeZoneInfo.ConvertTimeFromUtc(nextMidnightUtc, TimeZoneInfo.Local);
    }
}
