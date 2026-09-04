using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using YTNotifier.Constants;
using YTNotifier.Models;
using YTNotifier.Services;
using Application      = System.Windows.Application;
using Brush            = System.Windows.Media.Brush;
using Brushes          = System.Windows.Media.Brushes;
using Button           = System.Windows.Controls.Button;
using Cursors          = System.Windows.Input.Cursors;
using DataObject       = System.Windows.DataObject;
using DragDropEffects  = System.Windows.DragDropEffects;
using DragEventArgs    = System.Windows.DragEventArgs;
using Geometry         = System.Windows.Media.Geometry;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs     = System.Windows.Input.KeyEventArgs;
using Orientation      = System.Windows.Controls.Orientation;
using Path             = System.Windows.Shapes.Path;
using TextBox          = System.Windows.Controls.TextBox;

namespace YTNotifier.Views;

public partial class MainWindow : System.Windows.Window
{
    // ===== クォータ =====
    internal void AutoAdjustIntervalForQuota()
    {
        var svc      = SettingsService.Instance;
        var s        = svc.Settings;
        var channels = svc.Channels;
        if (channels.Count == 0) return;

        var (safe, recommended) = ApiQuotaHelper.ValidateInterval(s.CheckIntervalMinutes, channels);
        if (!safe && s.CheckIntervalMinutes != recommended)
        {
            var recItem = IntervalComboBox?.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag?.ToString() == recommended.ToString());
            if (recItem != null && IntervalComboBox != null)
                IntervalComboBox.SelectedItem = recItem;
            AppLogger.Log(LogMsg.QuotaRiskAdjusted, null, recommended);
        }
        UpdateQuotaInfo();
    }

    private void UpdateQuotaInfo()
    {
        try
        {
            if (QuotaInfoText == null) return;
            var svc      = SettingsService.Instance;
            var settings = svc.Settings;
            var channels = svc.GetChannelsSnapshot();
            var interval = settings.CheckIntervalMinutes;
            var banCheckUnits = ApiQuotaHelper.EstimateDailyUnitsForBanCheck(
                channels.Count(c => c.IsEnabled && !c.IsDormant),
                channels.Count(c => c.IsDormant));
            var daily    = ApiQuotaHelper.EstimateDailyUnitsForChannels(interval, channels) + banCheckUnits;
            var pct      = Math.Min(daily * 100.0 / ApiQuotaHelper.DailyLimit, 100.0);

            QuotaInfoText.Text    = $"{daily:N0} / {ApiQuotaHelper.DailyLimit:N0} ユニット/日";
            QuotaPercentText.Text = $"{pct:F0}%";

            var (normalUnits, lowFreqUnits, focusUnits) =
                ApiQuotaHelper.EstimateDailyUnitsByMode(interval, channels);
            ApplySegmentedQuotaBar(
                QuotaBarBg, QuotaBarNormal, QuotaBarLowFreq, QuotaBarFocus,
                QuotaInfoText, QuotaPercentText,
                pct, normalUnits, lowFreqUnits, focusUnits,
                QuotaSegLabelEstimateNormal, QuotaSegLabelEstimateLowFreq, QuotaSegLabelEstimateFocus);
            UpdateIntervalComboBoxItems(channels, channels.Count);

            // 当日実使用量バー（クォータ期間 = 太平洋時間0:00リセット）。カテゴリ別に3セグメント表示。
            var appState              = SettingsService.Instance.AppState;
            var quotaKey              = AppConstants.GetQuotaDayKey();
            var isTodayQuota          = appState.TodayApiDate == quotaKey;
            var actualUnits           = isTodayQuota ? appState.TodayApiUnits             : 0;
            var actualPendingUnits    = isTodayQuota ? appState.TodayApiUnitsPendingTrack : 0;
            var actualLiveStatusUnits = isTodayQuota ? appState.TodayApiUnitsLiveStatus   : 0;
            var actualNormalUnits     = Math.Max(0, actualUnits - actualPendingUnits - actualLiveStatusUnits);
            var actualPct             = Math.Min(actualUnits * 100.0 / ApiQuotaHelper.DailyLimit, 100.0);
            ActualQuotaInfoText.Text = $"{actualUnits:N0} / {ApiQuotaHelper.DailyLimit:N0} ユニット/日"
                                     + $"（通常 {actualNormalUnits:N0} / 配信予定 {actualPendingUnits:N0} / 配信中 {actualLiveStatusUnits:N0}）";
            ActualQuotaPercentText.Text = $"{(int)Math.Round(actualPct)}%";
            ApplySegmentedQuotaBar(
                ActualQuotaBarBg, ActualQuotaBarNormal, ActualQuotaBarPending, ActualQuotaBarLiveStatus,
                ActualQuotaInfoText, ActualQuotaPercentText,
                actualPct, actualNormalUnits, actualPendingUnits, actualLiveStatusUnits,
                QuotaSegLabelActualNormal, QuotaSegLabelActualPending, QuotaSegLabelActualLive);

        }
        catch (Exception ex) { AppLogger.Log(LogMsg.UiUpdateFailed, null, nameof(UpdateQuotaInfo), ex.Message); }
    }

    // セグメントバー（モード別色分け）表示処理
    private void ApplySegmentedQuotaBar(
        Border barBg,
        Border barNormal, Border barLowFreq, Border barFocus,
        TextBlock? infoText, TextBlock percentText,
        double pct, int normalUnits, int lowFreqUnits, int focusUnits,
        string segLabelNormal, string segLabelLowFreq, string segLabelFocus)
    {
        var pctRounded = (int)Math.Round(pct);
        var textColor  = pctRounded >= ApiQuotaHelper.QuotaWarnHighThresholdPct ? "QuotaWarnHighBrush"
                       : pctRounded >= ApiQuotaHelper.QuotaWarnLowThresholdPct  ? "QuotaWarnLowBrush"
                                                                                 : "SuccessBrush";
        if (infoText != null) SetDynamicBrush(infoText, TextBlock.ForegroundProperty, textColor);
        SetDynamicBrush(percentText, TextBlock.ForegroundProperty, textColor);

        barNormal.ToolTip  = string.Format(QuotaSegTooltipFormat, segLabelNormal,
                                           normalUnits,  normalUnits  * 100.0 / ApiQuotaHelper.DailyLimit);
        barLowFreq.ToolTip = string.Format(QuotaSegTooltipFormat, segLabelLowFreq,
                                           lowFreqUnits, lowFreqUnits * 100.0 / ApiQuotaHelper.DailyLimit);
        barFocus.ToolTip   = string.Format(QuotaSegTooltipFormat, segLabelFocus,
                                           focusUnits,   focusUnits   * 100.0 / ApiQuotaHelper.DailyLimit);

        var total = normalUnits + lowFreqUnits + focusUnits;

        void SetSegmentWidths()
        {
            var maxW = barBg.ActualWidth;
            if (maxW <= 0) return;
            var totalBarW = Math.Max(0, maxW * pct / 100.0);

            double normalW  = 0, lowFreqW = 0, focusW = 0;
            if (total > 0)
            {
                normalW  = Math.Floor(totalBarW * normalUnits  / (double)total);
                lowFreqW = Math.Floor(totalBarW * lowFreqUnits / (double)total);
                focusW   = totalBarW - normalW - lowFreqW;
            }
            else if (totalBarW > 0)
            {
                normalW = totalBarW;
            }

            barNormal.Width  = Math.Max(0, normalW);
            barLowFreq.Width = Math.Max(0, lowFreqW);
            barFocus.Width   = Math.Max(0, focusW);

            // 端のセグメントのみ角丸を付ける
            var segs = new[] { (barNormal, normalW), (barLowFreq, lowFreqW), (barFocus, focusW) };
            var nonZero = segs.Where(s => s.Item2 > 0).ToList();
            foreach (var (seg, _) in segs)
                seg.CornerRadius = new CornerRadius(0);
            if (nonZero.Count == 1)
            {
                nonZero[0].Item1.CornerRadius = new CornerRadius(AppConstants.QuotaBarCornerRadius);
            }
            else if (nonZero.Count > 1)
            {
                nonZero[0].Item1.CornerRadius                       = new CornerRadius(AppConstants.QuotaBarCornerRadius, 0, 0, AppConstants.QuotaBarCornerRadius);
                nonZero[nonZero.Count - 1].Item1.CornerRadius       = new CornerRadius(0, AppConstants.QuotaBarCornerRadius, AppConstants.QuotaBarCornerRadius, 0);
            }
        }

        // 既存ハンドラを除去してから再登録（多重登録防止）
        if (_quotaBarHandlers.TryGetValue(barBg, out var prev))
            barBg.SizeChanged -= prev;
        SizeChangedEventHandler handler = (_, _) => SetSegmentWidths();
        _quotaBarHandlers[barBg] = handler;
        barBg.SizeChanged += handler;

        if (barBg.ActualWidth > 0) SetSegmentWidths();
        else Dispatcher.BeginInvoke(SetSegmentWidths, System.Windows.Threading.DispatcherPriority.Render);
    }

    // barBg ごとの SizeChanged ハンドラを記録（多重登録防止用）
    private readonly Dictionary<Border, SizeChangedEventHandler> _quotaBarHandlers = new();

    // 使用量バーのセグメント別ツールチップ用ラベル（Views/MainWindow.xaml の凡例 Text= と一致させる）
    private const string QuotaSegLabelEstimateNormal  = "通常";
    private const string QuotaSegLabelEstimateLowFreq = "低頻度";
    private const string QuotaSegLabelEstimateFocus   = "時間指定";
    private const string QuotaSegLabelActualNormal    = "通常巡回";
    private const string QuotaSegLabelActualPending   = "配信予定の追跡";
    private const string QuotaSegLabelActualLive      = "配信中の状態確認";

    // {0}=カテゴリ名 {1}=消費ユニット数 {2}=日次上限に対する割合(%)
    private const string QuotaSegTooltipFormat = "{0}: {1:N0} ユニット（{2:F1}%）";

    private void UpdateIntervalComboBoxItems(List<ChannelInfo> channels, int channelCount)
    {
        if (IntervalComboBox == null) return;
        foreach (System.Windows.Controls.ComboBoxItem item in IntervalComboBox.Items)
        {
            if (item.Tag is string tagStr && int.TryParse(tagStr, out int mins))
            {
                var cost = ApiQuotaHelper.EstimateDailyUnitsForChannels(mins, channels);
                var over = cost > ApiQuotaHelper.DailyLimit;
                item.IsEnabled = !over;
                item.ToolTip   = over ? $"クォータ超過（{cost:N0} / {ApiQuotaHelper.DailyLimit:N0} ユニット/日）" : $"{cost:N0} ユニット/日";
                item.Opacity   = over ? 0.4 : 1.0;
            }
        }

        // 現在の選択がクォータ超過なら最小有効間隔へ自動調整
        if (IntervalComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem currentItem && !currentItem.IsEnabled)
        {
            var firstEnabled = IntervalComboBox.Items.OfType<System.Windows.Controls.ComboBoxItem>()
                .FirstOrDefault(i => i.IsEnabled && i.Tag is string t && int.TryParse(t, out _));
            if (firstEnabled != null && int.TryParse(firstEnabled.Tag?.ToString(), out var newMins))
            {
                _loadingSettings = true;
                IntervalComboBox.SelectedItem = firstEnabled;
                _loadingSettings = false;
                SettingsService.Instance.Settings.CheckIntervalMinutes = newMins;
                SettingsService.Instance.SaveSettings();
                MonitorService.Instance.ResetNormalChannels(newMins);
                MonitorService.Instance.RestartWithNewInterval();
                AppLogger.Log(LogMsg.QuotaAutoIntervalAdjusted, null, newMins);
            }
        }
    }
}
