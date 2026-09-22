using System.Globalization;
using YTNotifier.Constants;

namespace YTNotifier.Services;

/// <summary>
/// レポートページ用の集計ヘルパー（画面を呼ばず、結果を返すだけ）。
/// API使用量の日別系列・直近の通知件数と起動時間の合計・投稿時間帯ヒートマップを計算する。
/// </summary>
public static class ReportStatsHelper
{
    /// <summary>日付キーの書式（履歴の日付キーと同じ）</summary>
    private const string DateKeyFormat = "yyyy-MM-dd";

    /// <summary>グラフ用の日付ラベルの書式</summary>
    private const string DateLabelFormat = "M/d";

    /// <summary>曜日1文字の並び。DayOfWeek の数値（日=0〜土=6）でそのまま引く</summary>
    private const string WeekdayLabelChars = "日月火水木金土";

    /// <summary>ヒートマップの曜日の列数（月〜日）</summary>
    private const int HeatmapWeekdayCount = 7;

    /// <summary>ヒートマップの時間の行数（0〜23時）</summary>
    private const int HeatmapHourCount = 24;

    /// <summary>週の並びを月曜始まりにするための、DayOfWeek 数値のずらし量（日=0 → 列6、月=1 → 列0）</summary>
    private const int MondayFirstShift = 6;

    /// <summary>日付を履歴の日付キー（yyyy-MM-dd）へ変換する</summary>
    public static string ToDateKey(DateTime date)
        => date.ToString(DateKeyFormat, CultureInfo.InvariantCulture);

    /// <summary>クォータ日（太平洋時間）の今日の日付を返す</summary>
    public static DateTime GetQuotaToday()
        => DateTime.ParseExact(AppConstants.GetQuotaDayKey(), DateKeyFormat, CultureInfo.InvariantCulture);

    private static string WeekdayLabel(DayOfWeek dayOfWeek)
        => WeekdayLabelChars[(int)dayOfWeek].ToString();

    /// <summary>
    /// 太平洋時間の今日から遡った直近 ReportDays 日分のAPI使用量を、古い順に返す。
    /// 日付が AppState の当日日付と一致する日は AppState のカウンタを、それ以外は履歴を使う。どちらにもなければ記録なし。
    /// 平均は、記録がある日のうち今日を除いた日で求める（該当日がなければ null）。
    /// </summary>
    public static ApiUsageSeries BuildApiUsageSeries(AppState appState, IReadOnlyList<DailyApiUsageRecord> history)
    {
        var quotaToday = GetQuotaToday();
        var series     = new ApiUsageSeries();

        for (var daysAgo = AppConstants.ReportDays - 1; daysAgo >= 0; daysAgo--)
        {
            var date = quotaToday.AddDays(-daysAgo);
            var key  = ToDateKey(date);

            var entry = new ApiUsageDayEntry
            {
                DateLabel    = date.ToString(DateLabelFormat, CultureInfo.InvariantCulture),
                WeekdayLabel = WeekdayLabel(date.DayOfWeek),
                IsToday      = daysAgo == 0,
            };

            if (appState.TodayApiDate == key)
            {
                var total   = appState.TodayApiUnits;
                var pending = appState.TodayApiUnitsPendingTrack;
                var live    = appState.TodayApiUnitsLiveStatus;

                entry.HasRecord       = true;
                entry.NormalUnits     = Math.Max(0, total - pending - live);
                entry.PendingUnits    = pending;
                entry.LiveStatusUnits = live;
            }
            else
            {
                var record = history.FirstOrDefault(r => r.Date == key);
                if (record != null)
                {
                    entry.HasRecord       = true;
                    entry.NormalUnits     = record.NormalUnits;
                    entry.PendingUnits    = record.PendingUnits;
                    entry.LiveStatusUnits = record.LiveStatusUnits;
                }
            }

            series.Days.Add(entry);
        }

        var averageTargets = series.Days.Where(d => d.HasRecord && !d.IsToday).ToList();
        series.AverageUnits = averageTargets.Count > 0
            ? averageTargets.Average(d => (double)d.TotalUnits)
            : null;

        return series;
    }

    /// <summary>ローカル日付で今日を含む直近 ReportDays 日分の、通知件数と起動時間（秒）の合計を返す。記録のない日は0扱い</summary>
    public static (int notificationCount, int uptimeSeconds) SumRecentActivity(IReadOnlyList<DailyActivityRecord> history)
    {
        var localToday = DateTime.Now.Date;
        var targetKeys = new HashSet<string>();
        for (var daysAgo = 0; daysAgo < AppConstants.ReportDays; daysAgo++)
            targetKeys.Add(ToDateKey(localToday.AddDays(-daysAgo)));

        var notificationCount = 0;
        var uptimeSeconds     = 0;
        foreach (var record in history.Where(r => targetKeys.Contains(r.Date)))
        {
            notificationCount += record.NotificationCount;
            uptimeSeconds     += record.UptimeSeconds;
        }
        return (notificationCount, uptimeSeconds);
    }

    /// <summary>
    /// 全チャンネルの最新投稿スナップショットから、曜日（月〜日）×時（0〜23）の投稿件数表を作る。
    /// 投稿日時は UTC として扱いローカル時刻へ変換する（既存の投稿日時表示と同じ変換）。種別は区別せず全て数える。
    /// </summary>
    public static PostHeatmapData BuildPostHeatmap(IEnumerable<ChannelInfo> channels)
    {
        var counts     = new int[HeatmapWeekdayCount, HeatmapHourCount];
        var totalCount = 0;
        var maxCount   = 0;

        foreach (var channel in channels)
        {
            var uploads = channel.State.RecentUploads;
            foreach (var upload in uploads)
            {
                if (upload.PublishedAt == null) continue;

                var local         = DateTime.SpecifyKind(upload.PublishedAt.Value, DateTimeKind.Utc).ToLocalTime();
                var weekdayColumn = ((int)local.DayOfWeek + MondayFirstShift) % HeatmapWeekdayCount;

                counts[weekdayColumn, local.Hour]++;
                totalCount++;
                maxCount = Math.Max(maxCount, counts[weekdayColumn, local.Hour]);
            }
        }

        var weekdayLabels = new string[HeatmapWeekdayCount];
        for (var column = 0; column < HeatmapWeekdayCount; column++)
        {
            var dayOfWeek = (DayOfWeek)((column + 1) % HeatmapWeekdayCount);
            weekdayLabels[column] = WeekdayLabel(dayOfWeek);
        }

        return new PostHeatmapData
        {
            Counts        = counts,
            WeekdayLabels = weekdayLabels,
            TotalCount    = totalCount,
            MaxCount      = maxCount,
        };
    }
}
