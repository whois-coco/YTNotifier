using System;
using System.Collections.Generic;
using System.Linq;
using YTNotifier.Constants;
using YTNotifier.Models;
using YTNotifier.Services;

namespace YTNotifier.Views;

public partial class MainWindow : System.Windows.Window
{
    // ===== レポートページ =====

    // ページ名（サイドバー・タイトルバー・ログで共通）
    private const string ReportPageName = "レポート";

    // 直近7日のまとめカードの表示書式
    // {0}=通知件数
    private const string ReportNotificationCountFormat = "{0:N0} 件";
    // {0}=分
    private const string ReportUptimeMinutesFormat     = "{0}分";
    // {0}=時間 {1}=分
    private const string ReportUptimeHoursFormat       = "{0}時間{1}分";
    private const int    ReportSecondsPerMinute        = 60;
    private const int    ReportMinutesPerHour          = 60;

    // 投稿時間帯の集計対象件数の表示書式。{0}=件数
    private const string ReportHeatmapCountFormat = "集計対象: {0:N0} 件";

    // API使用量ドーナツのタイトル・内訳ラベル
    private const string QuotaDonutTitleEstimate        = "API使用量（見積り）";
    private const string QuotaDonutTitleActual          = "本日の実使用量";
    private const string QuotaDonutLabelEstimateNormal  = "通常";
    private const string QuotaDonutLabelEstimateLowFreq = "低頻度";
    private const string QuotaDonutLabelEstimateFocus   = "時間指定";
    private const string QuotaDonutLabelActualNormal    = "通常巡回";
    private const string QuotaDonutLabelActualPending   = "配信予定";
    private const string QuotaDonutLabelActualLive      = "配信中";

    // API使用量ドーナツの内訳セグメント色（差し色非依存の固定ブラシキー）
    private const string QuotaDonutBrushEstimateNormal   = "QuotaEstimateNormalBrush";
    private const string QuotaDonutBrushEstimateLowFreq  = "QuotaEstimateLowFreqBrush";
    private const string QuotaDonutBrushEstimateFocus    = "QuotaEstimateFocusBrush";
    private const string QuotaDonutBrushActualNormal     = "QuotaActualNormalBrush";
    private const string QuotaDonutBrushActualPending    = "QuotaActualPendingBrush";
    private const string QuotaDonutBrushActualLiveStatus = "QuotaActualLiveStatusBrush";

    // Gemini API使用量ドーナツのタイトル・ラベル・色・単位文言
    private const string QuotaDonutTitleGemini         = "Gemini API使用量（本日）";
    private const string QuotaDonutLabelGeminiRequests = "要約リクエスト";
    private const string QuotaDonutBrushGeminiRequests = "QuotaActualNormalBrush";
    private const string QuotaDonutUnitLabelGemini     = "回/日";

    /// <summary>ページを開いた時・設定ウィンドウを閉じた時に、レポートページ全体を描き直す（表示中のリアルタイム更新は行わない）</summary>
    private void RefreshReportPage()
    {
        try
        {
            var history  = SettingsService.Instance.UsageStats.GetReportHistorySnapshot();
            var channels = SettingsService.Instance.Channels.GetChannelsSnapshot();

            UpdateReportSummary(history);
            UpdateReportQuotaDonuts(channels);
            UpdateReportApiUsageChart(history);
            UpdateReportHeatmap(channels);
        }
        catch (Exception ex) { AppLogger.Log(LogMsg.UiUpdateFailed, null, nameof(RefreshReportPage), ex.Message); }
    }

    /// <summary>直近7日の通知件数・起動時間のカードを更新する</summary>
    private void UpdateReportSummary(ReportHistory history)
    {
        var (notificationCount, uptimeSeconds) = ReportStatsHelper.SumRecentActivity(history.Activity);

        ReportNotificationCountText.Text = string.Format(ReportNotificationCountFormat, notificationCount);
        ReportUptimeText.Text            = FormatReportUptime(uptimeSeconds);
    }

    /// <summary>起動時間を、1時間未満は「N分」、1時間以上は「H時間M分」で返す</summary>
    private static string FormatReportUptime(int uptimeSeconds)
    {
        var totalMinutes = uptimeSeconds / ReportSecondsPerMinute;
        if (totalMinutes < ReportMinutesPerHour)
            return string.Format(ReportUptimeMinutesFormat, totalMinutes);

        return string.Format(ReportUptimeHoursFormat,
            totalMinutes / ReportMinutesPerHour, totalMinutes % ReportMinutesPerHour);
    }

    /// <summary>API使用量ドーナツ3つ（見積り・実使用・Gemini）を更新する</summary>
    private void UpdateReportQuotaDonuts(List<ChannelInfo> channels)
    {
        var settings = SettingsService.Instance.Settings;
        var interval = settings.CheckIntervalMinutes;
        var banCheckUnits = ApiQuotaHelper.EstimateDailyUnitsForBanCheck(
            channels.Count(c => c.IsEnabled && !c.IsDormant),
            channels.Count(c => c.IsDormant));
        var daily    = ApiQuotaHelper.EstimateDailyUnitsForChannels(interval, channels) + banCheckUnits;

        var (normalUnits, lowFreqUnits, focusUnits) =
            ApiQuotaHelper.EstimateDailyUnitsByMode(interval, channels);
        QuotaDonutEstimate.SetData(
            QuotaDonutTitleEstimate, ApiQuotaHelper.DailyLimit, daily,
            new List<QuotaDonutSegment>
            {
                new() { Label = QuotaDonutLabelEstimateNormal,  Units = normalUnits,  BrushKey = QuotaDonutBrushEstimateNormal  },
                new() { Label = QuotaDonutLabelEstimateLowFreq, Units = lowFreqUnits, BrushKey = QuotaDonutBrushEstimateLowFreq },
                new() { Label = QuotaDonutLabelEstimateFocus,   Units = focusUnits,   BrushKey = QuotaDonutBrushEstimateFocus   },
            });

        // 当日実使用量ドーナツ（クォータ期間 = 太平洋時間0:00リセット）。カテゴリ別に3セグメント表示。
        var appState              = SettingsService.Instance.MonitorState.AppState;
        var quotaKey              = AppConstants.GetQuotaDayKey();
        var isTodayQuota          = appState.TodayApiDate == quotaKey;
        var actualUnits           = isTodayQuota ? appState.TodayApiUnits             : 0;
        var actualPendingUnits    = isTodayQuota ? appState.TodayApiUnitsPendingTrack : 0;
        var actualLiveStatusUnits = isTodayQuota ? appState.TodayApiUnitsLiveStatus   : 0;
        var actualNormalUnits     = Math.Max(0, actualUnits - actualPendingUnits - actualLiveStatusUnits);
        QuotaDonutActual.SetData(
            QuotaDonutTitleActual, ApiQuotaHelper.DailyLimit, actualUnits,
            new List<QuotaDonutSegment>
            {
                new() { Label = QuotaDonutLabelActualNormal,  Units = actualNormalUnits,     BrushKey = QuotaDonutBrushActualNormal     },
                new() { Label = QuotaDonutLabelActualPending, Units = actualPendingUnits,    BrushKey = QuotaDonutBrushActualPending    },
                new() { Label = QuotaDonutLabelActualLive,    Units = actualLiveStatusUnits, BrushKey = QuotaDonutBrushActualLiveStatus },
            });

        // Gemini API使用量ドーナツ（クォータ期間 = 太平洋時間0:00リセット）。内訳なしの単一ドーナツ。
        var geminiKey     = AppConstants.GetQuotaDayKey();
        var isTodayGemini = appState.TodayGeminiRequestDate == geminiKey;
        var geminiUnits   = isTodayGemini ? appState.TodayGeminiRequests : 0;
        QuotaDonutGemini.SetData(
            QuotaDonutTitleGemini, GeminiConstants.DailyRequestLimit, geminiUnits,
            new List<QuotaDonutSegment>
            {
                new() { Label = QuotaDonutLabelGeminiRequests, Units = geminiUnits, BrushKey = QuotaDonutBrushGeminiRequests },
            },
            unitLabel: QuotaDonutUnitLabelGemini,
            showBreakdown: false);
    }

    /// <summary>API使用量の推移グラフを更新する</summary>
    private void UpdateReportApiUsageChart(ReportHistory history)
    {
        var series = ReportStatsHelper.BuildApiUsageSeries(SettingsService.Instance.MonitorState.AppState, history.ApiUsage);
        ReportApiUsageChart.SetData(series, ApiQuotaHelper.DailyLimit);
    }

    /// <summary>投稿時間帯ヒートマップと集計対象件数を更新する</summary>
    private void UpdateReportHeatmap(List<ChannelInfo> channels)
    {
        var heatmap = ReportStatsHelper.BuildPostHeatmap(channels);
        ReportHeatmap.SetData(heatmap);
        ReportHeatmapCountText.Text = string.Format(ReportHeatmapCountFormat, heatmap.TotalCount);
    }
}
