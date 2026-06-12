
namespace YTNotifier.Services;

/// <summary>
/// YouTube Data API v3 クォータ計算ヘルパー
/// 無料枠: 10,000 ユニット/日
/// チェック1回あたりのコスト:
///   playlistItems.list = 1ユニット × チャンネル数
///   videos.list        = 1ユニット × 新着数（最大チャンネル数と仮定）
/// = 最大 2ユニット × チャンネル数 / チェック
/// </summary>
public static class ApiQuotaHelper
{
    public const int DailyLimit      = 10_000; // ユニット/日
    public const int UnitsPerCheck   = 2;      // playlistItems.list + videos.list
    private const int MinutesPerDay  = 1_440;

    /// <summary>1日の推定消費ユニット数を計算する</summary>
    public static int EstimateDailyUnits(int intervalMinutes, int channelCount)
    {
        if (intervalMinutes <= 0 || channelCount <= 0) return 0;
        var checksPerDay = MinutesPerDay / intervalMinutes;
        return checksPerDay * channelCount * UnitsPerCheck;
    }

    /// <summary>
    /// チャンネルリストと間隔指定がクォータ内に収まるか検証し、
    /// 収まらない場合は推奨間隔を返す。
    /// 監視モード（集中/低頻度）を正確に反映する。
    /// </summary>
    public static (bool safe, int recommendedMinutes) ValidateInterval(
        int intervalMinutes,
        IEnumerable<ChannelInfo> channels)
    {
        var channelList = channels.ToList();
        if (channelList.Count == 0) return (true, intervalMinutes);

        var daily = EstimateDailyUnitsForChannels(intervalMinutes, channelList);
        if (daily <= DailyLimit) return (true, intervalMinutes);

        // 推奨間隔を候補から探す（小さい順に試して収まる最小値を返す）
        int[] candidates = { 1, 5, 10, 30, 60 };
        foreach (var candidate in candidates)
        {
            if (EstimateDailyUnitsForChannels(candidate, channelList) <= DailyLimit)
                return (false, candidate);
        }
        return (false, 60);
    }

    /// <summary>後方互換：チャンネル数だけ分かる場合の簡易検証（全チャンネル通常モード仮定）</summary>
    public static (bool safe, int recommendedMinutes) ValidateInterval(
        int intervalMinutes, int channelCount)
    {
        if (channelCount <= 0) return (true, intervalMinutes);

        var daily = EstimateDailyUnits(intervalMinutes, channelCount);
        if (daily <= DailyLimit) return (true, intervalMinutes);

        var maxChecks = DailyLimit / (channelCount * UnitsPerCheck);
        if (maxChecks <= 0) return (false, 60);

        var minInterval = (int)Math.Ceiling((double)MinutesPerDay / maxChecks);
        int[] candidates = { 1, 5, 10, 30, 60 };
        var recommended  = candidates.FirstOrDefault(c => c >= minInterval);
        return (false, recommended == 0 ? 60 : recommended);
    }

    /// <summary>
    /// 1チャンネル分の1日推定消費ユニット数を、監視モード別パラメータから計算する。
    /// 全チャンネル合算（EstimateDailyUnitsForChannels）と上級設定のプレビューで
    /// 同じ計算式を共有するための単一ソース。
    /// </summary>
    public static int EstimateDailyUnitsForMode(
        MonitorMode mode,
        int globalIntervalMinutes,
        int focusWindowMinutes,
        int focusIntervalMinutes,
        int lowFreqIntervalMinutes)
    {
        switch (mode)
        {
            case MonitorMode.LowFreq:
                var lowInterval = Math.Max(1, lowFreqIntervalMinutes);
                return (MinutesPerDay / lowInterval) * UnitsPerCheck;

            case MonitorMode.Focus:
                var window   = focusWindowMinutes * 2; // 前後の合計分
                var interval = Math.Max(1, focusIntervalMinutes);
                return (window / interval) * UnitsPerCheck;

            default: // Normal
                var globalInterval = Math.Max(1, globalIntervalMinutes);
                return (MinutesPerDay / globalInterval) * UnitsPerCheck;
        }
    }

    /// <summary>時間指定モード（複数スロット）の1日推定ユニット数。有効スロットを合算する</summary>
    public static int EstimateDailyUnitsForFocusSlots(
        IEnumerable<FocusSlot> slots)
    {
        int total = 0;
        foreach (var slot in slots.Where(s => s.IsEnabled))
        {
            var window   = slot.WindowMinutes * 2;
            var interval = Math.Max(1, slot.IntervalMinutes);
            // 曜日指定がある場合は稼働日割合で按分（0=全曜日）
            int activeDays = slot.Days == 0 ? 7 : CountBits(slot.Days);
            total += (int)Math.Round((window / (double)interval) * UnitsPerCheck * activeDays / 7.0);
        }
        return total;
    }

    private static int CountBits(int v)
    {
        int c = 0;
        for (int i = 0; i < 7; i++) if ((v & (1 << i)) != 0) c++;
        return c;
    }

    /// <summary>全チャンネルの監視モードを考慮した1日の推定消費ユニット数を計算する</summary>
    public static int EstimateDailyUnitsForChannels(
        int globalIntervalMinutes,
        IEnumerable<ChannelInfo> channels)
    {
        int total = 0;
        foreach (var ch in channels.Where(c => c.IsEnabled && !c.IsTestChannel))
        {
            // 時間指定モードでFocusSlotsがあれば複数スロット計算を使用
            if (ch.MonitorMode == MonitorMode.Focus
                && ch.FocusSlots.Any(s => s.IsEnabled))
            {
                total += EstimateDailyUnitsForFocusSlots(ch.FocusSlots);
                continue;
            }
            total += EstimateDailyUnitsForMode(
                ch.MonitorMode, globalIntervalMinutes,
                ch.FocusWindowMinutes, ch.FocusIntervalMinutes, ch.LowFreqIntervalMinutes);
        }
        return total;
    }
}
