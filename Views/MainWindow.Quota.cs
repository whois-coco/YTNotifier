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

    // API使用量ドーナツのタイトル・内訳ラベル
    private const string QuotaDonutTitleEstimate       = "API使用量（見積り）";
    private const string QuotaDonutTitleActual         = "本日の実使用量";
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

    private void UpdateQuotaInfo()
    {
        try
        {
            if (QuotaDonutEstimate == null || QuotaDonutActual == null) return;
            var svc      = SettingsService.Instance;
            var settings = svc.Settings;
            var channels = svc.GetChannelsSnapshot();
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
            UpdateIntervalComboBoxItems(channels, channels.Count);

            // 当日実使用量ドーナツ（クォータ期間 = 太平洋時間0:00リセット）。カテゴリ別に3セグメント表示。
            var appState              = SettingsService.Instance.AppState;
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
        }
        catch (Exception ex) { AppLogger.Log(LogMsg.UiUpdateFailed, null, nameof(UpdateQuotaInfo), ex.Message); }
    }

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
