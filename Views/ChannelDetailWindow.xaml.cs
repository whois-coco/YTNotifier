using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ComboBox    = System.Windows.Controls.ComboBox;
using Brush       = System.Windows.Media.Brush;
using Brushes     = System.Windows.Media.Brushes;
using Color       = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment   = System.Windows.VerticalAlignment;
using YTNotifier.Constants;
using YTNotifier.Models;
using YTNotifier.Services;

namespace YTNotifier.Views;

public partial class ChannelDetailWindow : Window
{
    private readonly ChannelInfo _channel;
    private readonly List<FocusTabPanel> _tabPanels = new();
    private int _selectedTab = 0;
    private readonly bool _origNotifyVideo;
    private readonly bool _origNotifyShort;
    private readonly bool _origNotifyLive;
    private readonly UpcomingNotifyMode _origUpcomingNotifyMode;
    private readonly int _origUpcomingNotifyLeadMinutes;
    private readonly List<FocusSlot> _origFocusSlots;

    public ChannelDetailWindow(ChannelInfo channel, Window owner)
    {
        InitializeComponent();
        Owner    = owner;
        _channel = channel;
        Loaded  += (_, _) => WindowCornerHelper.Apply(this);

        ChannelNameText.Text = channel.ChannelName;
        var cat = SettingsService.Instance.Categories
            .FirstOrDefault(c => c.CategoryId == channel.CategoryId);
        CategoryText.Text = cat?.CategoryName ?? "未設定";

        _origNotifyVideo              = channel.NotifyVideo;
        _origNotifyShort              = channel.NotifyShort;
        _origNotifyLive               = channel.NotifyLive;
        _origUpcomingNotifyMode       = channel.UpcomingNotifyMode;
        _origUpcomingNotifyLeadMinutes = channel.UpcomingNotifyLeadMinutes;
        _origFocusSlots     = channel.FocusSlots.Select(s => new FocusSlot
        {
            NotifyKind                 = s.NotifyKind,
            Days                       = s.Days,
            Hour                       = s.Hour,
            Minute                     = s.Minute,
            WindowMinutes              = s.WindowMinutes,
            IntervalMinutes            = s.IntervalMinutes,
            IsEnabled                  = s.IsEnabled,
            SlotMode                   = s.SlotMode,
            SlotNormalIntervalMinutes  = s.SlotNormalIntervalMinutes,
            SlotLowFreqIntervalMinutes = s.SlotLowFreqIntervalMinutes,
        }).ToList();

        // 通知方法ドロップダウン初期選択
        SelectComboByTag(UpcomingNotifyModeCombo, channel.UpcomingNotifyMode switch
        {
            UpcomingNotifyMode.LiveStartOnly => "LiveStartOnly",
            UpcomingNotifyMode.Both          => "Both",
            _                                => "WaitingRoomOnly",
        });
        SelectComboByTag(UpcomingLeadCombo, channel.UpcomingNotifyLeadMinutes > 0 ? channel.UpcomingNotifyLeadMinutes.ToString() : "10");
        UpdateLeadRowVisibility();

        // 監視設定タブ初期化：既存モードをスロット形式に変換
        // 未設定タブ（Short/ライブ配信）もチャンネル本来のモードを引き継ぐための既定値生成
        FocusSlot MakeDefaultSlot(VideoKind kind) => channel.MonitorMode switch
        {
            MonitorMode.LowFreq => new FocusSlot
            {
                SlotMode                   = MonitorMode.LowFreq,
                SlotLowFreqIntervalMinutes = channel.LowFreqIntervalMinutes,
                NotifyKind                 = kind,
                IsEnabled                  = true
            },
            MonitorMode.Focus => new FocusSlot
            {
                SlotMode        = MonitorMode.Focus,
                NotifyKind      = kind,
                Days            = channel.FocusDays,
                Hour            = channel.FocusHour,
                Minute          = channel.FocusMinute,
                WindowMinutes   = channel.FocusWindowMinutes,
                IntervalMinutes = channel.FocusIntervalMinutes,
                IsEnabled       = true
            },
            _ => new FocusSlot { SlotMode = MonitorMode.Normal, NotifyKind = kind, IsEnabled = true }
        };

        List<FocusSlot> slots = channel.FocusSlots.Count > 0
            ? channel.FocusSlots
            : new List<FocusSlot> { MakeDefaultSlot(VideoKind.Video) };

        // 3タブ分作成（デフォルト種別: 動画/Short/ライブ配信）
        VideoKind[] defaultKinds = { VideoKind.Video, VideoKind.Short, VideoKind.Live };
        bool[] kindEnabled = { channel.NotifyVideo, channel.NotifyShort, channel.NotifyLive };
        for (int i = 0; i < 3; i++)
        {
            var slot = i < slots.Count ? slots[i] : MakeDefaultSlot(defaultKinds[i]);
            slot.IsEnabled = kindEnabled[i]; // チャンネル一覧の種別ON/OFFを反映
            _tabPanels.Add(new FocusTabPanel(slot));
        }

        BuildTabUI();
        SelectTab(0);
        UpdateEstimate();
    }

    // ===== タブUI構築 =====
    private static readonly string[] TabKindLabels = { "動画", "Short", "ライブ配信" };
    private static readonly VideoKind[] TabKinds = { VideoKind.Video, VideoKind.Short, VideoKind.Live };

    private void BuildTabUI()
    {
        FocusTabNav.Children.Clear();
        for (int i = 0; i < 3; i++)
        {
            int idx = i;
            var tab = _tabPanels[i];
            tab.FixedKind = TabKinds[i];

            var lbl = new TextBlock
            {
                Text              = TabKindLabels[i],
                FontSize          = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            var (iconElement, setIconColor) = BuildKindIcon(i);
            var headerPanel = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            headerPanel.Children.Add(iconElement);
            headerPanel.Children.Add(lbl);
            var border = new Border
            {
                Padding         = new Thickness(14, 0, 14, 0),
                Cursor          = System.Windows.Input.Cursors.Hand,
                Background      = Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 2),
                Tag             = i,
                Child           = headerPanel
            };
            border.MouseLeftButtonUp += (_, _) => SelectTab(idx);
            tab.NavBorder       = border;
            tab.NavLabel        = lbl;
            tab.SetNavIconColor = setIconColor;
            SetTabBorderStyle(tab, false);
            FocusTabNav.Children.Add(border);

            tab.OnEnabledChanged = () =>
            {
                SetTabBorderStyle(tab, tab.NavBorder!.Tag is int t && t == _selectedTab);
                UpdateEstimate();
            };
            tab.OnModeChanged = () =>
            {
                SetTabBorderStyle(tab, tab.NavBorder!.Tag is int t && t == _selectedTab);
            };
        }
    }

    private void SelectTab(int idx)
    {
        _selectedTab = idx;
        for (int i = 0; i < 3; i++)
            SetTabBorderStyle(_tabPanels[i], i == idx);

        FocusTabContent.Children.Clear();
        _tabPanels[idx].ResetContent();
        FocusTabContent.Children.Add(_tabPanels[idx].BuildContent());
        AppLogger.Log(LogMsg.ChannelDetailTabSwitched, _channel.ChannelName, TabKindLabels[idx]);
    }

    private void SetTabBorderStyle(FocusTabPanel tab, bool selected)
    {
        var res = System.Windows.Application.Current.Resources;
        if (tab.NavBorder == null || tab.NavLabel == null) return;

        tab.NavBorder.BorderBrush = selected
            ? (Brush)res["PrimaryBrush"]
            : Brushes.Transparent;
        tab.NavLabel.Foreground = selected
            ? (Brush)res["PrimaryBrush"]
            : tab.IsEnabled
                ? (Brush)res["TextSecondaryBrush"]
                : (Brush)res["TextMutedBrush"];
        tab.NavLabel.FontWeight = selected
            ? System.Windows.FontWeights.SemiBold
            : System.Windows.FontWeights.Normal;

        if (tab.SetNavIconColor != null)
        {
            var iconBrushKey = !tab.IsEnabled ? "TextMutedBrush"
                : tab.SlotData.SlotMode switch
                {
                    MonitorMode.LowFreq => "WarningBrush",
                    MonitorMode.Focus   => "SuccessBrush",
                    _                   => "PrimaryBrush"
                };
            tab.SetNavIconColor((Brush)res[iconBrushKey]);
        }
    }

    private static (System.Windows.UIElement icon, Action<Brush> setColor) BuildKindIcon(int tabIndex)
    {
        const double IconSize = 14;
        const double CanvasSize = 24;

        switch (tabIndex)
        {
            case 0: // 動画
            {
                var p1 = new System.Windows.Shapes.Path { Data = System.Windows.Media.Geometry.Parse("M16 13 L21.223 16.482 A0.5 0.5 0 0 0 22 16.066 L22 7.87 A0.5 0.5 0 0 0 21.248 7.438 L16 10.5 Z") };
                var p2 = new System.Windows.Shapes.Path { Data = System.Windows.Media.Geometry.Parse("M2 6 L16 6 C17.105 6 18 6.895 18 8 L18 18 C18 19.105 17.105 20 16 20 L2 20 C0.895 20 0 19.105 0 18 L0 8 C0 6.895 0.895 6 2 6 Z") };
                var canvas = new System.Windows.Controls.Canvas { Width = CanvasSize, Height = CanvasSize };
                canvas.Children.Add(p1);
                canvas.Children.Add(p2);
                var vb = new System.Windows.Controls.Viewbox { Width = IconSize, Height = IconSize, Margin = new Thickness(0, 0, 5, 0), Child = canvas };
                return (vb, b => { p1.Fill = b; p2.Fill = b; });
            }
            case 1: // Short
            {
                var p = new System.Windows.Shapes.Path
                {
                    Data    = System.Windows.Media.Geometry.Parse("M4 14a1 1 0 0 1-.78-1.63l9.9-10.2a.5.5 0 0 1 .86.46l-1.92 6.02A1 1 0 0 0 13 10h7a1 1 0 0 1 .78 1.63l-9.9 10.2a.5.5 0 0 1-.86-.46l1.92-6.02A1 1 0 0 0 11 14z"),
                    Stretch = System.Windows.Media.Stretch.Uniform
                };
                var vb = new System.Windows.Controls.Viewbox { Width = IconSize, Height = IconSize, Margin = new Thickness(0, 0, 5, 0), Child = p };
                return (vb, b => p.Fill = b);
            }
            default: // ライブ配信
            {
                var strokePaths = new[]
                {
                    new System.Windows.Shapes.Path { Data = System.Windows.Media.Geometry.Parse("M4.9 16.1C1 12.2 1 5.8 4.9 1.9"),   Fill = Brushes.Transparent, StrokeThickness = 2, StrokeStartLineCap = System.Windows.Media.PenLineCap.Round, StrokeEndLineCap = System.Windows.Media.PenLineCap.Round },
                    new System.Windows.Shapes.Path { Data = System.Windows.Media.Geometry.Parse("M7.8 4.7a6.14 6.14 0 0 0-.8 7.5"), Fill = Brushes.Transparent, StrokeThickness = 2, StrokeStartLineCap = System.Windows.Media.PenLineCap.Round, StrokeEndLineCap = System.Windows.Media.PenLineCap.Round },
                    new System.Windows.Shapes.Path { Data = System.Windows.Media.Geometry.Parse("M16.2 4.8c2 2 2.26 5.11.8 7.47"),   Fill = Brushes.Transparent, StrokeThickness = 2, StrokeStartLineCap = System.Windows.Media.PenLineCap.Round, StrokeEndLineCap = System.Windows.Media.PenLineCap.Round },
                    new System.Windows.Shapes.Path { Data = System.Windows.Media.Geometry.Parse("M19.1 1.9a9.96 9.96 0 0 1 0 14.1"),Fill = Brushes.Transparent, StrokeThickness = 2, StrokeStartLineCap = System.Windows.Media.PenLineCap.Round, StrokeEndLineCap = System.Windows.Media.PenLineCap.Round },
                    new System.Windows.Shapes.Path { Data = System.Windows.Media.Geometry.Parse("M9.5 18h5"),                        Fill = Brushes.Transparent, StrokeThickness = 2, StrokeStartLineCap = System.Windows.Media.PenLineCap.Round, StrokeEndLineCap = System.Windows.Media.PenLineCap.Round },
                    new System.Windows.Shapes.Path { Data = System.Windows.Media.Geometry.Parse("m8 22 4-11 4 11"),                  Fill = Brushes.Transparent, StrokeThickness = 2, StrokeStartLineCap = System.Windows.Media.PenLineCap.Round, StrokeEndLineCap = System.Windows.Media.PenLineCap.Round, StrokeLineJoin = System.Windows.Media.PenLineJoin.Round },
                };
                var ellipse = new System.Windows.Shapes.Ellipse
                {
                    Width           = 4,
                    Height          = 4,
                    Fill            = Brushes.Transparent,
                    StrokeThickness = 2
                };
                System.Windows.Controls.Canvas.SetLeft(ellipse, 10);
                System.Windows.Controls.Canvas.SetTop(ellipse, 7);

                var canvas = new System.Windows.Controls.Canvas { Width = CanvasSize, Height = CanvasSize };
                foreach (var sp in strokePaths) canvas.Children.Add(sp);
                canvas.Children.Add(ellipse);
                var vb = new System.Windows.Controls.Viewbox { Width = IconSize, Height = IconSize, Margin = new Thickness(0, 0, 5, 0), Child = canvas };
                return (vb, b => { foreach (var sp in strokePaths) sp.Stroke = b; ellipse.Stroke = b; });
            }
        }
    }

    // ===== ドロップダウン操作ヘルパー =====
    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (item.Tag?.ToString() == tag)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private UpcomingNotifyMode GetSelectedUpcomingMode()
    {
        var tag = (UpcomingNotifyModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return tag switch
        {
            "LiveStartOnly" => UpcomingNotifyMode.LiveStartOnly,
            "Both"          => UpcomingNotifyMode.Both,
            _               => UpcomingNotifyMode.WaitingRoomOnly,
        };
    }

    private int GetSelectedLeadMinutes()
    {
        var tag = (UpcomingLeadCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return int.TryParse(tag, out var v) ? v : 0;
    }

    private void UpdateLeadRowVisibility()
    {
        var mode = GetSelectedUpcomingMode();
        UpcomingLeadRow.Visibility = mode == UpcomingNotifyMode.LiveStartOnly
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    // ===== イベントハンドラ =====
    private void UpcomingNotifyModeCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateLeadRowVisibility();
    }

    private void RevertAndClose()
    {
        _channel.NotifyVideo               = _origNotifyVideo;
        _channel.NotifyShort               = _origNotifyShort;
        _channel.NotifyLive                = _origNotifyLive;
        _channel.UpcomingNotifyMode        = _origUpcomingNotifyMode;
        _channel.UpcomingNotifyLeadMinutes = _origUpcomingNotifyLeadMinutes;
        _channel.FocusSlots                = _origFocusSlots;
        SettingsService.Instance.UpdateChannel(_channel);
        AppLogger.Log(LogMsg.ChannelDetailCancelled, _channel.ChannelName);
        if (Owner is MainWindow mw)
            mw.Dispatcher.BeginInvoke(mw.RefreshChannelList);
        Close();
    }

    private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }
    private void CloseButton_Click(object sender, RoutedEventArgs e)  => RevertAndClose();
    private void Cancel_Click(object sender, RoutedEventArgs e) => RevertAndClose();

    private void UpdateEstimate()
    {
        if (EstimateText == null || DetailQuotaBarBg == null) return;
        var settings       = SettingsService.Instance.Settings;
        var channels       = SettingsService.Instance.Channels;
        var globalInterval = settings.CheckIntervalMinutes;
        var otherChannels  = channels.Where(c => c.IsEnabled && c.ChannelId != _channel.ChannelId);

        // % 表示用の合計（設定ページと同じ計算式）
        var currentSlots = _tabPanels.Select(p => p.GetSlot()).ToList();
        var otherTotal   = ApiQuotaHelper.EstimateDailyUnitsForChannels(globalInterval, otherChannels);
        var thisTotal    = ApiQuotaHelper.EstimateDailyUnitsForFocusSlots(currentSlots, globalInterval);
        var banCheckUnits = ApiQuotaHelper.EstimateDailyUnitsForBanCheck(
            channels.Count(c => c.IsEnabled && !c.IsDormant),
            channels.Count(c => c.IsDormant));
        var totalUnits   = otherTotal + thisTotal + banCheckUnits;

        // セグメントバー用の内訳（比率表示のみ。合計は totalUnits に従う）
        var (otherNormal, otherLowFreq, otherFocus) =
            ApiQuotaHelper.EstimateDailyUnitsByMode(globalInterval, otherChannels);

        int thisNormal = 0, thisLowFreq = 0, thisFocus = 0;
        foreach (var slot in currentSlots.Where(s => s.IsEnabled))
        {
            switch (slot.SlotMode)
            {
                case MonitorMode.Normal:
                    var ni = slot.SlotNormalIntervalMinutes > 0
                        ? slot.SlotNormalIntervalMinutes : Math.Max(1, globalInterval);
                    var normalChecks = ApiQuotaHelper.MinutesPerDay / Math.Max(1, ni);
                    thisNormal += normalChecks + Math.Min(normalChecks, ApiQuotaHelper.DailyUploadLimitPerChannel);
                    break;
                case MonitorMode.LowFreq:
                    var lowFreqChecks = ApiQuotaHelper.MinutesPerDay / Math.Max(1, slot.SlotLowFreqIntervalMinutes);
                    thisLowFreq += lowFreqChecks + Math.Min(lowFreqChecks, ApiQuotaHelper.DailyUploadLimitPerChannel);
                    break;
                case MonitorMode.Focus:
                    // IntervalMinutes == 0 は30秒（0.5分）を意味する
                    var fi        = slot.IntervalMinutes == 0 ? 0.5 : Math.Max(1, slot.IntervalMinutes);
                    int activeDays = slot.Days == 0 ? 7 : CountBitsSet(slot.Days);
                    var focusChecks = (int)Math.Round((slot.WindowMinutes / (double)fi) * activeDays / 7.0);
                    thisFocus += focusChecks + Math.Min(focusChecks, ApiQuotaHelper.DailyUploadLimitPerChannel);
                    break;
            }
        }

        var normalUnits  = otherNormal  + thisNormal;
        var lowFreqUnits = otherLowFreq + thisLowFreq;
        var focusUnits   = otherFocus   + thisFocus;
        var pct          = ApiQuotaHelper.DailyLimit > 0
            ? Math.Min(100.0, totalUnits * 100.0 / ApiQuotaHelper.DailyLimit) : 0;
        var pctRounded   = (int)Math.Round(pct);

        EstimateText.Text = $"{pctRounded}%";
        var textColor = pctRounded >= ApiQuotaHelper.QuotaWarnHighThresholdPct ? "QuotaWarnHighBrush"
                      : pctRounded >= ApiQuotaHelper.QuotaWarnLowThresholdPct  ? "QuotaWarnLowBrush"
                                                                                : "SuccessBrush";
        EstimateText.Foreground = (Brush)System.Windows.Application.Current.Resources[textColor];

        void SetSegmentWidths()
        {
            var maxW      = DetailQuotaBarBg.ActualWidth;
            if (maxW <= 0) return;
            var totalBarW = Math.Max(0, maxW * pct / 100.0);

            double normalW = 0, lowFreqW = 0, focusW = 0;
            var breakdownTotal = normalUnits + lowFreqUnits + focusUnits;
            if (breakdownTotal > 0)
            {
                normalW  = Math.Floor(totalBarW * normalUnits  / (double)breakdownTotal);
                lowFreqW = Math.Floor(totalBarW * lowFreqUnits / (double)breakdownTotal);
                focusW   = totalBarW - normalW - lowFreqW;
            }
            else if (totalBarW > 0)
            {
                normalW = totalBarW;
            }

            DetailQuotaBarNormal.Width  = Math.Max(0, normalW);
            DetailQuotaBarLowFreq.Width = Math.Max(0, lowFreqW);
            DetailQuotaBarFocus.Width   = Math.Max(0, focusW);

            var segs    = new[] { (DetailQuotaBarNormal, normalW), (DetailQuotaBarLowFreq, lowFreqW), (DetailQuotaBarFocus, focusW) };
            var nonZero = segs.Where(s => s.Item2 > 0).ToList();
            foreach (var (seg, _) in segs) seg.CornerRadius = new CornerRadius(0);
            if (nonZero.Count == 1)
                nonZero[0].Item1.CornerRadius = new CornerRadius(4);
            else if (nonZero.Count > 1)
            {
                nonZero[0].Item1.CornerRadius                 = new CornerRadius(4, 0, 0, 4);
                nonZero[nonZero.Count - 1].Item1.CornerRadius = new CornerRadius(0, 4, 4, 0);
            }
        }

        if (_detailQuotaBarHandler != null)
            DetailQuotaBarBg.SizeChanged -= _detailQuotaBarHandler;
        _detailQuotaBarHandler = (_, _) => SetSegmentWidths();
        DetailQuotaBarBg.SizeChanged += _detailQuotaBarHandler;

        if (DetailQuotaBarBg.ActualWidth > 0) SetSegmentWidths();
        else Dispatcher.BeginInvoke(SetSegmentWidths, System.Windows.Threading.DispatcherPriority.Render);
    }

    private SizeChangedEventHandler? _detailQuotaBarHandler;

    private static int CountBitsSet(int v)
    {
        int c = 0;
        for (int i = 0; i < 7; i++) if ((v & (1 << i)) != 0) c++;
        return c;
    }

    // ===== 保存 =====
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // クォータチェック
        var settings   = SettingsService.Instance.Settings;
        var channels   = SettingsService.Instance.Channels;
        var thisUnits  = ApiQuotaHelper.EstimateDailyUnitsForFocusSlots(
            _tabPanels.Select(p => p.GetSlot()), settings.CheckIntervalMinutes);
        var otherUnits = ApiQuotaHelper.EstimateDailyUnitsForChannels(
            settings.CheckIntervalMinutes,
            channels.Where(c => c.IsEnabled && c.ChannelId != _channel.ChannelId));
        var daily = otherUnits + thisUnits;
        var pct   = daily * 100.0 / ApiQuotaHelper.DailyLimit;

        if (daily > ApiQuotaHelper.DailyLimit)
        {
            var msg = $"この設定では1日のAPIクォータ（{ApiQuotaHelper.DailyLimit:N0}ユニット）を超過します。\n\n" +
                      $"推定使用量: {daily:N0} ユニット/日（{pct:F0}%）\n\n" +
                      "それでも保存しますか？（設定で間隔を調整してください）";
            if (ConfirmDialog.Show(this, "クォータ超過の警告", msg, "保存する") != true)
                return;
            AppLogger.Log(LogMsg.QuotaExceededOnSave, _channel.ChannelName, _channel.ChannelName, (int)Math.Round(pct));
        }
        else if (pct >= ApiQuotaHelper.QuotaWarnLowThresholdPct)
        {
            var msg = $"この設定後のAPI使用量が {pct:F0}% になります。\nクォータの消費にご注意ください。";
            if (ConfirmDialog.Show(this, "クォータ使用量の警告", msg, "保存する") != true)
                return;
            AppLogger.Log(LogMsg.QuotaWarningOnSave, _channel.ChannelName, _channel.ChannelName, (int)Math.Round(pct));
        }

        _channel.NotifyVideo               = _tabPanels[0].GetSlot().IsEnabled;
        _channel.NotifyShort               = _tabPanels[1].GetSlot().IsEnabled;
        _channel.NotifyLive                = _tabPanels[2].GetSlot().IsEnabled;
        _channel.UpcomingNotifyMode        = GetSelectedUpcomingMode();
        _channel.UpcomingNotifyLeadMinutes = GetSelectedLeadMinutes();

        // 常にスロットベースで保存
        _channel.MonitorMode = MonitorMode.Focus;
        _channel.FocusSlots  = _tabPanels.Select(p => p.GetSlot()).ToList();

        // 後方互換: 先頭有効スロットのうちFocusモードのものを旧フィールドに反映
        var first = _channel.FocusSlots
            .FirstOrDefault(s => s.IsEnabled && s.SlotMode == MonitorMode.Focus)
            ?? _channel.FocusSlots.FirstOrDefault(s => s.IsEnabled);
        if (first != null && first.SlotMode == MonitorMode.Focus)
        {
            _channel.FocusHour            = first.Hour;
            _channel.FocusMinute          = first.Minute;
            _channel.FocusWindowMinutes   = first.WindowMinutes;
            _channel.FocusIntervalMinutes = first.IntervalMinutes;
            _channel.FocusDays            = first.Days;
        }

        var needsIntervalAdjust = daily > ApiQuotaHelper.DailyLimit;

        _channel.NextCheckAt = DateTime.MinValue;
        SettingsService.Instance.UpdateChannel(_channel);
        AppLogger.Log(LogMsg.ChannelDetailSaved, null, _channel.ChannelName);

        var globalInterval = settings.CheckIntervalMinutes;
        bool[] origEnabled = { _origNotifyVideo, _origNotifyShort, _origNotifyLive };
        for (int i = 0; i < _tabPanels.Count; i++)
        {
            var slot = _tabPanels[i].GetSlot();
            if (slot.IsEnabled != origEnabled[i])
            {
                var statusLabel = slot.IsEnabled
                    ? $"ON ({DescribeSlotInterval(slot, globalInterval)})"
                    : "OFF";
                AppLogger.Log(LogMsg.ChannelDetailEnabledChanged, _channel.ChannelName,
                    _channel.ChannelName, TabKindLabels[i], statusLabel);
            }
            if (!slot.IsEnabled) continue;
            var intervalDesc = DescribeSlotInterval(slot, globalInterval);
            AppLogger.Log(LogMsg.ChannelDetailSlotInterval, _channel.ChannelName, TabKindLabels[i], intervalDesc);
        }

        if (_channel.UpcomingNotifyMode != _origUpcomingNotifyMode)
        {
            var modeLabel = _channel.UpcomingNotifyMode switch
            {
                UpcomingNotifyMode.LiveStartOnly => "開始時のみ",
                UpcomingNotifyMode.Both          => "両方",
                _                                => "待機所のみ",
            };
            AppLogger.Log(LogMsg.ChannelDetailUpcomingModeChanged, _channel.ChannelName, modeLabel);
        }

        if (_channel.UpcomingNotifyLeadMinutes != _origUpcomingNotifyLeadMinutes)
            AppLogger.Log(LogMsg.ChannelDetailUpcomingLeadChanged, _channel.ChannelName, _channel.UpcomingNotifyLeadMinutes);

        if (needsIntervalAdjust && Owner is MainWindow mw)
            mw.Dispatcher.BeginInvoke(mw.AutoAdjustIntervalForQuota);

        DialogResult = true;
        Close();
    }

    // ===== ユーティリティ =====
    private static string DescribeSlotInterval(FocusSlot slot, int globalIntervalMinutes)
    {
        return slot.SlotMode switch
        {
            MonitorMode.LowFreq => $"低頻度 {slot.SlotLowFreqIntervalMinutes}分",
            MonitorMode.Focus   => BuildFocusDesc(slot),
            _ => slot.SlotNormalIntervalMinutes > 0
                ? $"通常 {slot.SlotNormalIntervalMinutes}分"
                : $"通常 {globalIntervalMinutes}分（グローバル）",
        };
    }

    private static string BuildFocusDesc(FocusSlot slot)
    {
        const string dayChars = "日月火水木金土";
        var daysStr = slot.Days == AppConstants.AllDaysMask
            ? "全曜日"
            : string.Concat(dayChars.Where((_, i) => (slot.Days & (1 << i)) != 0));
        var intervalLabel = slot.IntervalMinutes == 0 ? "30秒" : $"{slot.IntervalMinutes}分";
        return $"時間指定 [{daysStr}] {slot.Hour:D2}:{slot.Minute:D2}〜{slot.WindowMinutes}分 / {intervalLabel}間隔";
    }

}

// ===== 監視設定タブパネル =====
internal class FocusTabPanel
{
    public Border? NavBorder { get; set; }
    public TextBlock? NavLabel { get; set; }
    public Action<Brush>? SetNavIconColor { get; set; }
    public Action? OnEnabledChanged { get; set; }
    public Action? OnModeChanged { get; set; }
    public VideoKind FixedKind { get; set; } = VideoKind.Video;
    public bool IsEnabled => _enabledCheck?.IsChecked == true || (!HasContent && _slot.IsEnabled);
    public bool HasContent { get; private set; } = false;
    public FocusSlot SlotData => HasContent ? GetSlot() : _slot;

    private readonly FocusSlot _slot;
    private bool _suppressEnabledEvent = false;
    private System.Windows.Controls.CheckBox? _enabledCheck;
    private System.Windows.Controls.Primitives.ToggleButton[] _dayBtns = Array.Empty<System.Windows.Controls.Primitives.ToggleButton>();
    private ComboBox? _modeBox;
    private ComboBox? _windowBox, _intervalBox;
    private ComboBox? _hourBox, _minuteBox;
    private ComboBox? _normalIntervalBox, _lowFreqBox;
    private StackPanel? _settingsPanel;
    private StackPanel? _normalPanel, _lowFreqPanel, _focusPanel;

    private static readonly string[] DayLabels = { "Sun","Mon","Tue","Wed","Thu","Fri","Sat" };

    public FocusTabPanel(FocusSlot slot) => _slot = slot;

    public void SetEnabled(bool enabled)
    {
        _slot.IsEnabled = enabled;
        if (_enabledCheck != null)
        {
            _suppressEnabledEvent = true;
            _enabledCheck.IsChecked = enabled;
            _suppressEnabledEvent = false;
        }
        if (_settingsPanel != null)
        {
            _settingsPanel.IsEnabled = enabled;
            _settingsPanel.Opacity   = enabled ? 1.0 : 0.4;
        }
    }

    public void ResetContent()
    {
        if (HasContent) SaveToSlot();
        HasContent    = false;
        _enabledCheck = null;
        _dayBtns      = Array.Empty<System.Windows.Controls.Primitives.ToggleButton>();
        _modeBox = _windowBox = _intervalBox = null;
        _hourBox = _minuteBox = _normalIntervalBox = _lowFreqBox = null;
        _settingsPanel = _normalPanel = _lowFreqPanel = _focusPanel = null;
    }

    private void SaveToSlot()
    {
        if (_enabledCheck != null) _slot.IsEnabled = _enabledCheck.IsChecked == true;

        // スロットモード
        if (_modeBox?.SelectedItem is ComboBoxItem mi && mi.Tag is string mt)
            _slot.SlotMode = mt switch
            {
                "LowFreq" => MonitorMode.LowFreq,
                "Focus"   => MonitorMode.Focus,
                _         => MonitorMode.Normal
            };

        // 通常間隔（0=グローバル）
        if (_normalIntervalBox?.SelectedItem is ComboBoxItem ni && ni.Tag is string ns && int.TryParse(ns, out var nv))
            _slot.SlotNormalIntervalMinutes = nv;

        // 低頻度間隔
        if (_lowFreqBox?.SelectedItem is ComboBoxItem li && li.Tag is string ls && int.TryParse(ls, out var lv))
            _slot.SlotLowFreqIntervalMinutes = lv;

        // 通知種別はタブ固定
        _slot.NotifyKind = FixedKind;
        int days = 0;
        for (int i = 0; i < 7; i++)
            if (_dayBtns.Length > i && _dayBtns[i].IsChecked == true) days |= (1 << i);
        _slot.Days   = days;
        _slot.Hour   = _hourBox?.SelectedIndex ?? _slot.Hour;
        _slot.Minute = (_minuteBox?.SelectedIndex ?? 0) * 5;
        if (_windowBox?.SelectedItem is ComboBoxItem wi && wi.Tag is string ws && int.TryParse(ws, out var w))
            _slot.WindowMinutes = w;
        if (_intervalBox?.SelectedItem is ComboBoxItem ii && ii.Tag is string ivs && int.TryParse(ivs, out var iv))
            _slot.IntervalMinutes = iv;
    }

    public FocusSlot GetSlot()
    {
        if (HasContent) SaveToSlot();
        return _slot;
    }

    public UIElement BuildContent()
    {
        HasContent = true;
        _suppressEnabledEvent = true;
        var res   = System.Windows.Application.Current.Resources;
        var stack = new StackPanel();

        // 有効化チェックボックス
        _enabledCheck = new System.Windows.Controls.CheckBox
        {
            Content    = FixedKind switch
            {
                VideoKind.Short => "Shortチェック",
                VideoKind.Live  => "ライブ配信チェック",
                _               => "動画チェック"
            },
            IsChecked  = _slot.IsEnabled,
            FontSize   = 12,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = (Brush)res["TextPrimaryBrush"],
            Margin     = new Thickness(0, 0, 0, 10)
        };
        _enabledCheck.Checked   += (_, _) =>
        {
            if (_suppressEnabledEvent) return;
            _settingsPanel!.IsEnabled = true;
            _settingsPanel!.Opacity   = 1.0;
            // 有効化時は監視モードをデフォルト「通常」に設定（SelectionChanged の連鎖を抑制）
            if (_modeBox != null)
            {
                _suppressEnabledEvent = true;
                try
                {
                    foreach (ComboBoxItem item in _modeBox.Items)
                        if (item.Tag?.ToString() == "Normal") { _modeBox.SelectedItem = item; break; }
                }
                finally { _suppressEnabledEvent = false; }
            }
            OnEnabledChanged?.Invoke();
        };
        _enabledCheck.Unchecked += (_, _) => { if (_suppressEnabledEvent) return; _settingsPanel!.IsEnabled = false; _settingsPanel!.Opacity = 0.4; OnEnabledChanged?.Invoke(); };
        stack.Children.Add(_enabledCheck);

        // 設定パネル
        _settingsPanel = new StackPanel
        {
            IsEnabled = _slot.IsEnabled,
            Opacity   = _slot.IsEnabled ? 1.0 : 0.4
        };
        stack.Children.Add(_settingsPanel);

        // 1. 監視モード
        _modeBox = new ComboBox { Style = (Style)res["ModernComboBox"] };
        foreach (var (tag, lbl) in new[] { ("Normal","通常"), ("LowFreq","低頻度"), ("Focus","時間指定") })
            _modeBox.Items.Add(new ComboBoxItem { Content = lbl, Tag = tag, Style = (Style)res["ModernComboBoxItem"] });
        var modeTag = _slot.SlotMode switch
        {
            MonitorMode.LowFreq => "LowFreq",
            MonitorMode.Focus   => "Focus",
            _                   => "Normal"
        };
        foreach (ComboBoxItem item in _modeBox.Items)
            if (item.Tag?.ToString() == modeTag) { _modeBox.SelectedItem = item; break; }
        if (_modeBox.SelectedItem == null) _modeBox.SelectedIndex = 0;
        _settingsPanel.Children.Add(MakeRow("監視モード", _modeBox, res));

        // 2. 通常モード（個別間隔、0=グローバル）
        _normalPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        _normalIntervalBox = new ComboBox { Style = (Style)res["ModernComboBox"] };
        _normalIntervalBox.Items.Add(new ComboBoxItem { Content = "一括で設定に従う", Tag = "0", Style = (Style)res["ModernComboBoxItem"] });
        var globalInterval = SettingsService.Instance.Settings.CheckIntervalMinutes;
        foreach (var (lbl, tag) in new[] { ("3分","3"),("5分","5"),("10分","10"),("15分","15"),("20分","20"),("30分","30") })
        {
            var tooShort = int.TryParse(tag, out var tagMins) && tagMins < globalInterval;
            _normalIntervalBox.Items.Add(new ComboBoxItem
            {
                Content    = lbl,
                Tag        = tag,
                Style      = (Style)res["ModernComboBoxItem"],
                IsEnabled  = !tooShort,
                Opacity    = tooShort ? 0.4 : 1.0,
                ToolTip    = tooShort ? $"グローバル設定（{globalInterval}分）より短い間隔は設定できません" : null,
            });
        }
        // 保存済み値がグローバルより短い場合は「一括で設定に従う」へフォールバック
        var savedInterval = _slot.SlotNormalIntervalMinutes;
        var selectTag = (savedInterval > 0 && savedInterval < globalInterval) ? "0" : savedInterval.ToString();
        SelectComboByTagStr(_normalIntervalBox, selectTag);
        _normalIntervalBox.SelectionChanged += (_, _) => { if (_suppressEnabledEvent) return; SaveToSlot(); OnEnabledChanged?.Invoke(); };
        _normalPanel.Children.Add(MakeRow("監視間隔", _normalIntervalBox, res));
        _settingsPanel.Children.Add(_normalPanel);

        // 3. 低頻度間隔
        _lowFreqPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        _lowFreqBox = new ComboBox { Style = (Style)res["ModernComboBox"] };
        foreach (var (lbl, tag) in new[] { ("1時間","60"),("3時間","180"),("6時間","360"),("12時間","720"),("24時間","1440") })
            _lowFreqBox.Items.Add(new ComboBoxItem { Content = lbl, Tag = tag, Style = (Style)res["ModernComboBoxItem"] });
        SelectComboByTagStr(_lowFreqBox, _slot.SlotLowFreqIntervalMinutes.ToString());
        _lowFreqBox.SelectionChanged += (_, _) => { if (_suppressEnabledEvent) return; SaveToSlot(); OnEnabledChanged?.Invoke(); };
        _lowFreqPanel.Children.Add(MakeRow("監視間隔", _lowFreqBox, res));
        _settingsPanel.Children.Add(_lowFreqPanel);

        // 4. 時間指定設定
        _focusPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

        // 曜日指定
        _focusPanel.Children.Add(new TextBlock
        {
            Text       = "曜日指定",
            FontSize   = 11,
            Foreground = (Brush)res["TextSecondaryBrush"],
            Margin     = new Thickness(0, 4, 0, 4)
        });
        var dayRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Center };
        _dayBtns = new System.Windows.Controls.Primitives.ToggleButton[7];
        for (int i = 0; i < 7; i++)
        {
            var btn = new System.Windows.Controls.Primitives.ToggleButton
            {
                Content   = DayLabels[i],
                Style     = (Style)res["DayToggleButton"],
                IsChecked = _slot.Days == 0 || (_slot.Days & (1 << i)) != 0
            };
            btn.Unchecked += (_, _) =>
            {
                if (_dayBtns.All(b => b.IsChecked != true))
                    btn.IsChecked = true;
            };
            _dayBtns[i] = btn;
            dayRow.Children.Add(btn);
        }
        _focusPanel.Children.Add(dayRow);

        // 投稿時刻
        _hourBox   = new ComboBox { Style = (Style)res["ModernComboBox"], Width = 72 };
        _minuteBox = new ComboBox { Style = (Style)res["ModernComboBox"], Width = 72 };
        for (int h = 0; h < 24; h++)
            _hourBox.Items.Add(new ComboBoxItem { Content = $"{h:D2}", Style = (Style)res["ModernComboBoxItem"] });
        for (int m = 0; m < 60; m += 5)
            _minuteBox.Items.Add(new ComboBoxItem { Content = $"{m:D2}", Style = (Style)res["ModernComboBoxItem"] });
        _hourBox.SelectedIndex   = Math.Clamp(_slot.Hour, 0, 23);
        _minuteBox.SelectedIndex = Math.Clamp(_slot.Minute / 5, 0, 11);

        var timeGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        timeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        timeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var timeLbl  = new TextBlock { Text = "投稿時刻", FontSize = 11, Foreground = (Brush)res["TextSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center };
        var timeCtrl = new StackPanel { Orientation = Orientation.Horizontal };
        timeCtrl.Children.Add(_hourBox);
        timeCtrl.Children.Add(new TextBlock { Text = ":", FontSize = 13, FontWeight = System.Windows.FontWeights.SemiBold, Foreground = (Brush)res["TextSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 5, 0) });
        timeCtrl.Children.Add(_minuteBox);
        System.Windows.Controls.Grid.SetColumn(timeCtrl, 1);
        timeGrid.Children.Add(timeLbl);
        timeGrid.Children.Add(timeCtrl);
        _focusPanel.Children.Add(timeGrid);

        // 投稿監視時間 / 監視間隔
        _windowBox   = new ComboBox { Style = (Style)res["ModernComboBox"], Width = 78 };
        _intervalBox = new ComboBox { Style = (Style)res["ModernComboBox"], Width = 78 };
        foreach (var (lbl, tag) in new[] { ("3分","3"),("5分","5"),("10分","10"),("15分","15") })
            _windowBox.Items.Add(new ComboBoxItem { Content = lbl, Tag = tag, Style = (Style)res["ModernComboBoxItem"] });
        foreach (var (lbl, tag) in new[] { ("30秒","0"),("1分","1"),("5分","5") })
            _intervalBox.Items.Add(new ComboBoxItem { Content = lbl, Tag = tag, Style = (Style)res["ModernComboBoxItem"] });
        // デフォルト：投稿確認10分・間隔5分
        var windowTag   = _slot.WindowMinutes > 0 ? _slot.WindowMinutes.ToString() : "10";
        var intervalTag = _slot.IntervalMinutes == 0 ? "0"
                        : new[] { 0, 1, 5 }.Contains(_slot.IntervalMinutes) ? _slot.IntervalMinutes.ToString() : "5";
        SelectComboByTagStr(_windowBox,   windowTag);
        SelectComboByTagStr(_intervalBox, intervalTag);
        _windowBox.SelectionChanged   += (_, _) => { if (_suppressEnabledEvent) return; OnEnabledChanged?.Invoke(); };
        _intervalBox.SelectionChanged += (_, _) => { if (_suppressEnabledEvent) return; OnEnabledChanged?.Invoke(); };

        var wiGrid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        wiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        wiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var wiLbl  = new TextBlock { Text = "投稿確認 / 間隔", FontSize = 11, Foreground = (Brush)res["TextSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center };
        var wiCtrl = new StackPanel { Orientation = Orientation.Horizontal };
        wiCtrl.Children.Add(_windowBox);
        wiCtrl.Children.Add(new TextBlock { Text = "/", FontSize = 11, Foreground = (Brush)res["TextMutedBrush"], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) });
        wiCtrl.Children.Add(_intervalBox);
        System.Windows.Controls.Grid.SetColumn(wiCtrl, 1);
        wiGrid.Children.Add(wiLbl);
        wiGrid.Children.Add(wiCtrl);
        _focusPanel.Children.Add(wiGrid);

        _settingsPanel.Children.Add(_focusPanel);

        // モード切替でパネル表示を切り替え
        UpdateModePanels();
        _modeBox.SelectionChanged += (_, _) =>
        {
            UpdateModePanels();
            SaveToSlot();
            if (!_suppressEnabledEvent)
            {
                OnEnabledChanged?.Invoke();
                OnModeChanged?.Invoke();
            }
        };

        stack.Loaded += (_, _) => _suppressEnabledEvent = false;
        return stack;
    }

    private void UpdateModePanels()
    {
        if (_normalPanel == null || _lowFreqPanel == null || _focusPanel == null || _modeBox == null) return;
        var tag = (_modeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        _normalPanel.Visibility  = tag == "Normal"  ? Visibility.Visible : Visibility.Collapsed;
        _lowFreqPanel.Visibility = tag == "LowFreq" ? Visibility.Visible : Visibility.Collapsed;
        _focusPanel.Visibility   = tag == "Focus"   ? Visibility.Visible : Visibility.Collapsed;
    }

    private const double CtrlColWidth = 150;

    private static Grid MakeRow(string label, FrameworkElement ctrl, ResourceDictionary res)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CtrlColWidth) });
        var lbl = new TextBlock { Text = label, FontSize = 11, Foreground = (Brush)res["TextSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center };
        ctrl.HorizontalAlignment = HorizontalAlignment.Stretch;
        System.Windows.Controls.Grid.SetColumn(ctrl, 1);
        g.Children.Add(lbl);
        g.Children.Add(ctrl);
        return g;
    }

    private static void SelectComboByTagStr(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items)
            if (item.Tag?.ToString() == tag) { box.SelectedItem = item; return; }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }
}
