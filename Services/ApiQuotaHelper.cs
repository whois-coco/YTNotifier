namespace YTNotifier.Services;

/// <summary>
/// YouTube Data API v3 クォータ計算ヘルパー
/// 無料枠: 10,000 ユニット/日
/// チェック1回あたりのコスト:
///   activities.list = 1ユニット × チャンネル数
///   videos.list     = 1ユニット × 新着数（最大チャンネル数と仮定）
/// = 最大 2ユニット × チャンネル数 / チェック
/// </summary>
public static class ApiQuotaHelper
{
    public const int DailyLimit      = 10_000; // ユニット/日
    public const int UnitsPerCheck   = 2;      // activities.list + videos.list
    private const int MinutesPerDay  = 1_440;

    /// <summary>1日の推定消費ユニット数を計算する</summary>
    public static int EstimateDailyUnits(int intervalMinutes, int channelCount)
    {
        if (intervalMinutes <= 0 || channelCount <= 0) return 0;
        var checksPerDay = MinutesPerDay / intervalMinutes;
        return checksPerDay * channelCount * UnitsPerCheck;
    }

    /// <summary>
    /// 指定した間隔がクォータ内に収まるか検証し、
    /// 収まらない場合は推奨間隔を返す
    /// </summary>
    public static (bool safe, int recommendedMinutes) ValidateInterval(
        int intervalMinutes, int channelCount)
    {
        if (channelCount <= 0) return (true, intervalMinutes);

        var daily = EstimateDailyUnits(intervalMinutes, channelCount);
        if (daily <= DailyLimit) return (true, intervalMinutes);

        // クォータ内に収まる最小間隔を求める
        // checksPerDay × channels × 2 ≤ 10000
        // checksPerDay ≤ 10000 / (channels × 2)
        // interval ≥ 1440 / checksPerDay
        var maxChecks  = DailyLimit / (channelCount * UnitsPerCheck);
        if (maxChecks <= 0) return (false, 60); // チャンネル数が多すぎる場合は60分

        var minInterval = (int)Math.Ceiling((double)MinutesPerDay / maxChecks);

        // コンボボックスの選択肢に合わせて切り上げ
        int[] candidates = { 1, 5, 10, 30, 60 };
        var recommended  = candidates.FirstOrDefault(c => c >= minInterval);
        if (recommended == 0) recommended = 60;

        return (false, recommended);
    }

    /// <summary>チャンネル数に対して安全に設定できる最小間隔（分）を返す</summary>
    public static int SafeMinInterval(int channelCount)
    {
        var (_, rec) = ValidateInterval(1, channelCount);
        return rec;
    }
}
