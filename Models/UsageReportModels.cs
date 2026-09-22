using Newtonsoft.Json;

namespace YTNotifier.Models;

/// <summary>API使用ユニットの計上カテゴリ（実使用量バーの色分け用）。既定値 Normal を 0 に置く。</summary>
public enum ApiUnitCategory { Normal, PendingTrack, LiveStatus }

/// <summary>API使用量ドーナツ（QuotaDonut）の内訳セグメント1件分。ラベル・消費ユニット数・固定色ブラシキーを束ねる</summary>
public class QuotaDonutSegment
{
    /// <summary>内訳ラベル（例「通常巡回」）</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>このセグメントの消費ユニット数</summary>
    public int Units { get; set; }

    /// <summary>セグメント色の固定ブラシキー（Themes/ のキー名。差し色非依存）</summary>
    public string BrushKey { get; set; } = string.Empty;
}

/// <summary>日別のAPI使用量の履歴1件（日付は太平洋時間の日付キー）</summary>
public class DailyApiUsageRecord
{
    /// <summary>太平洋日の日付キー（yyyy-MM-dd）</summary>
    [JsonProperty("date")]
    public string Date { get; set; } = string.Empty;

    /// <summary>通常巡回のユニット数</summary>
    [JsonProperty("normalUnits")]
    public int NormalUnits { get; set; } = 0;

    /// <summary>配信予定の追跡のユニット数</summary>
    [JsonProperty("pendingUnits")]
    public int PendingUnits { get; set; } = 0;

    /// <summary>配信中の状態確認のユニット数</summary>
    [JsonProperty("liveStatusUnits")]
    public int LiveStatusUnits { get; set; } = 0;
}

/// <summary>日別の通知件数・起動時間の履歴1件（日付はローカル日の日付キー）</summary>
public class DailyActivityRecord
{
    /// <summary>ローカル日の日付キー（yyyy-MM-dd）</summary>
    [JsonProperty("date")]
    public string Date { get; set; } = string.Empty;

    /// <summary>その日に表示した通知の件数</summary>
    [JsonProperty("notificationCount")]
    public int NotificationCount { get; set; } = 0;

    /// <summary>その日の起動時間（秒）</summary>
    [JsonProperty("uptimeSeconds")]
    public int UptimeSeconds { get; set; } = 0;
}

/// <summary>レポート用の履歴ルート（report_history.json で管理）</summary>
public class ReportHistory
{
    [JsonProperty("apiUsage")]
    public List<DailyApiUsageRecord> ApiUsage { get; set; } = new();

    [JsonProperty("activity")]
    public List<DailyActivityRecord> Activity { get; set; } = new();
}

/// <summary>API使用量の推移グラフ（ApiUsageBarChart）の日別1件分。日付・曜日は太平洋日付のもの</summary>
public class ApiUsageDayEntry
{
    /// <summary>「M/d」形式の日付ラベル</summary>
    public string DateLabel { get; set; } = string.Empty;

    /// <summary>曜日1文字（日〜土）</summary>
    public string WeekdayLabel { get; set; } = string.Empty;

    /// <summary>今日（太平洋日付）の日か</summary>
    public bool IsToday { get; set; }

    /// <summary>その日の記録があるか（false の日は棒を描かない）</summary>
    public bool HasRecord { get; set; }

    /// <summary>通常巡回のユニット数</summary>
    public int NormalUnits { get; set; }

    /// <summary>配信予定の追跡のユニット数</summary>
    public int PendingUnits { get; set; }

    /// <summary>配信中の状態確認のユニット数</summary>
    public int LiveStatusUnits { get; set; }

    /// <summary>合計ユニット数</summary>
    public int TotalUnits => NormalUnits + PendingUnits + LiveStatusUnits;
}

/// <summary>API使用量の推移グラフに渡す直近の日別系列（古い順）と平均</summary>
public class ApiUsageSeries
{
    public List<ApiUsageDayEntry> Days { get; set; } = new();

    /// <summary>今日を除く記録あり日の平均ユニット数。該当日がなければ null（平均線を引かない）</summary>
    public double? AverageUnits { get; set; }
}

/// <summary>投稿時間帯ヒートマップ（PostHeatmap）に渡す集計結果</summary>
public class PostHeatmapData
{
    /// <summary>件数表。[曜日の列（月〜日）, 時（0〜23）]</summary>
    public int[,] Counts { get; set; } = new int[0, 0];

    /// <summary>曜日の列ラベル（月〜日の順・1文字）</summary>
    public string[] WeekdayLabels { get; set; } = Array.Empty<string>();

    /// <summary>集計対象の総件数</summary>
    public int TotalCount { get; set; }

    /// <summary>表の最大値</summary>
    public int MaxCount { get; set; }
}
