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
    private readonly UpcomingNotifyMode _origPremiereUpcomingMode;
    private readonly int _origPremiereUpcomingLead;
    private readonly UpcomingNotifyMode _origLiveUpcomingMode;
    private readonly int _origLiveUpcomingLead;
    private readonly List<FocusSlot> _origFocusSlots;

    private const double TabLabelFontSize = 12; // タブのラベル
    private static readonly Thickness TabPadding     = new(14, 0, 14, 0);
    private static readonly Thickness TabUnderline   = new(0, 0, 0, 2);
    private static readonly Thickness KindIconMargin = new(0, 0, 5, 0);

    public ChannelDetailWindow(ChannelInfo channel, Window owner)
    {
        InitializeComponent();
        Owner    = owner;
        _channel = channel;
        WindowCornerHelper.ApplyOnLoaded(this);

        ChannelNameText.Text = channel.ChannelName;
        var cat = SettingsService.Instance.Channels.Categories
            .FirstOrDefault(c => c.CategoryId == channel.CategoryId);
        CategoryText.Text = cat?.CategoryName ?? "未設定";

        _origNotifyVideo              = channel.NotifyVideo;
        _origNotifyShort              = channel.NotifyShort;
        _origNotifyLive               = channel.NotifyLive;
        _origPremiereUpcomingMode = channel.PremiereUpcomingNotifyMode;
        _origPremiereUpcomingLead = channel.PremiereUpcomingNotifyLeadMinutes;
        _origLiveUpcomingMode     = channel.LiveUpcomingNotifyMode;
        _origLiveUpcomingLead     = channel.LiveUpcomingNotifyLeadMinutes;
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

        // 監視設定タブ初期化：種別（NotifyKind）ごとにスロットを集める
        // 種別のスロットが1件もない場合はチャンネル本来のモードを引き継ぐ既定値を生成
        bool[] kindEnabled = { channel.NotifyVideo, channel.NotifyShort, channel.NotifyLive };
        for (int kindSlotIndex = 0; kindSlotIndex < AppConstants.KindSlotCount; kindSlotIndex++)
        {
            var tabKind   = AppConstants.KindSlotKinds[kindSlotIndex];
            var kindSlots = channel.FocusSlots.Where(s => s.NotifyKind == tabKind).ToList();
            if (kindSlots.Count == 0)
                kindSlots.Add(channel.CreateDefaultFocusSlot(tabKind));
            foreach (var kindSlot in kindSlots)
                kindSlot.IsEnabled = kindEnabled[kindSlotIndex]; //チャンネル一覧の種別ON/OFFを反映
            _tabPanels.Add(new FocusTabPanel(kindSlots));
        }

        // upcoming（プレミア／ライブ）通知方法をタブ内へ配置：プレミアは動画タブ(0)、ライブはライブ配信タブ(2)
        _tabPanels[0].EnableUpcomingSection(
            channel.PremiereUpcomingNotifyMode, channel.PremiereUpcomingNotifyLeadMinutes);
        _tabPanels[2].EnableUpcomingSection(
            channel.LiveUpcomingNotifyMode, channel.LiveUpcomingNotifyLeadMinutes);

        BuildTabUI();
        SelectTab(0);
        UpdateEstimate();
    }

    // ===== タブUI構築 =====

    private void BuildTabUI()
    {
        FocusTabNav.Children.Clear();
        for (int kindSlotIndex = 0; kindSlotIndex < AppConstants.KindSlotCount; kindSlotIndex++)
        {
            int idx = kindSlotIndex;
            var tab = _tabPanels[kindSlotIndex];
            tab.FixedKind = AppConstants.KindSlotKinds[kindSlotIndex];

            var lbl = new TextBlock
            {
                Text              = AppConstants.KindSlotLabels[kindSlotIndex],
                FontSize          = TabLabelFontSize,
                VerticalAlignment = VerticalAlignment.Center
            };
            var (iconElement, setIconColor) = BuildKindIcon(kindSlotIndex);
            var headerPanel = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            headerPanel.Children.Add(iconElement);
            headerPanel.Children.Add(lbl);
            var border = new Border
            {
                Padding         = TabPadding,
                Cursor          = System.Windows.Input.Cursors.Hand,
                Background      = Brushes.Transparent,
                BorderThickness = TabUnderline,
                Tag             = kindSlotIndex,
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
                SetTabBorderStyle(tab, tab.NavBorder!.Tag is int tabIndexTag && tabIndexTag == _selectedTab);
                UpdateEstimate();
            };
            tab.OnModeChanged = () =>
            {
                SetTabBorderStyle(tab, tab.NavBorder!.Tag is int tabIndexTag && tabIndexTag == _selectedTab);
            };
        }
    }

    private void SelectTab(int idx)
    {
        _selectedTab = idx;
        for (int kindSlotIndex = 0; kindSlotIndex < AppConstants.KindSlotCount; kindSlotIndex++)
            SetTabBorderStyle(_tabPanels[kindSlotIndex], kindSlotIndex == idx);

        FocusTabContent.Children.Clear();
        _tabPanels[idx].ResetContent();
        FocusTabContent.Children.Add(_tabPanels[idx].BuildContent());
        AppLogger.Log(LogMsg.ChannelDetailTabSwitched, _channel.ChannelName, AppConstants.KindSlotLabels[idx]);
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

        var kind = AppConstants.KindSlotKinds[tabIndex];
        var (icon, setColor) = KindIconFactory.Build(kind, IconSize, CanvasSize);
        ((FrameworkElement)icon).Margin = KindIconMargin;
        return (icon, setColor);
    }

    private void RevertAndClose()
    {
        _channel.NotifyVideo               = _origNotifyVideo;
        _channel.NotifyShort               = _origNotifyShort;
        _channel.NotifyLive                = _origNotifyLive;
        _channel.PremiereUpcomingNotifyMode        = _origPremiereUpcomingMode;
        _channel.PremiereUpcomingNotifyLeadMinutes = _origPremiereUpcomingLead;
        _channel.LiveUpcomingNotifyMode            = _origLiveUpcomingMode;
        _channel.LiveUpcomingNotifyLeadMinutes     = _origLiveUpcomingLead;
        _channel.FocusSlots                        = _origFocusSlots;
        SettingsService.Instance.Channels.UpdateChannel(_channel);
        AppLogger.Log(LogMsg.ChannelDetailCancelled, _channel.ChannelName);
        if (Owner is MainWindow mainWindow)
            mainWindow.Dispatcher.BeginInvoke(mainWindow.RefreshChannelList);
        Close();
    }

    private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => WindowTitleBarHelper.DragMoveOnPress(this, e);
    private void CloseButton_Click(object sender, RoutedEventArgs e)  => RevertAndClose();
    private void Cancel_Click(object sender, RoutedEventArgs e) => RevertAndClose();

    private void UpdateEstimate()
    {
        if (EstimateText == null || DetailQuotaBarBg == null) return;
        var settings       = SettingsService.Instance.Settings;
        var channels       = SettingsService.Instance.Channels.GetChannelsSnapshot();
        var globalInterval = settings.CheckIntervalMinutes;
        var otherChannels  = channels.Where(c => c.IsEnabled && c.ChannelId != _channel.ChannelId);

        // % 表示用の合計（設定ページと同じ計算式）
        var currentSlots = _tabPanels.SelectMany(p => p.GetSlots()).ToList();
        var otherTotal   = ApiQuotaHelper.EstimateDailyUnitsForChannels(globalInterval, otherChannels);
        var thisTotal    = ApiQuotaHelper.EstimateDailyUnitsForFocusSlots(currentSlots, globalInterval);
        var banCheckUnits = ApiQuotaHelper.EstimateDailyUnitsForBanCheck(
            channels.Count(c => c.IsEnabled && !c.IsDormant),
            channels.Count(c => c.IsDormant));
        var totalUnits   = otherTotal + thisTotal + banCheckUnits;

        // セグメントバー用の内訳（比率表示のみ。合計は totalUnits に従う）
        var (otherNormal, otherLowFreq, otherFocus) =
            ApiQuotaHelper.EstimateDailyUnitsByMode(globalInterval, otherChannels);

        var (thisNormal, thisLowFreq, thisFocus) =
            ApiQuotaHelper.EstimateDailyUnitsByModeForSlots(currentSlots, globalInterval);

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

            var quotaBarSegments = new[] { (DetailQuotaBarNormal, normalW), (DetailQuotaBarLowFreq, lowFreqW), (DetailQuotaBarFocus, focusW) };
            var nonZero = quotaBarSegments.Where(s => s.Item2 > 0).ToList();
            foreach (var (quotaBar, _) in quotaBarSegments) quotaBar.CornerRadius = new CornerRadius(0);
            if (nonZero.Count == 1)
                nonZero[0].Item1.CornerRadius = new CornerRadius(AppConstants.QuotaBarCornerRadius);
            else if (nonZero.Count > 1)
            {
                nonZero[0].Item1.CornerRadius                 = new CornerRadius(AppConstants.QuotaBarCornerRadius, 0, 0, AppConstants.QuotaBarCornerRadius);
                nonZero[nonZero.Count - 1].Item1.CornerRadius = new CornerRadius(0, AppConstants.QuotaBarCornerRadius, AppConstants.QuotaBarCornerRadius, 0);
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

    // ===== 保存 =====
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // クォータチェック
        var settings   = SettingsService.Instance.Settings;
        var channels   = SettingsService.Instance.Channels.GetChannelsSnapshot();
        var thisUnits  = ApiQuotaHelper.EstimateDailyUnitsForFocusSlots(
            _tabPanels.SelectMany(p => p.GetSlots()), settings.CheckIntervalMinutes);
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

        _channel.NotifyVideo               = _tabPanels[0].GetSlots()[0].IsEnabled;
        _channel.NotifyShort               = _tabPanels[1].GetSlots()[0].IsEnabled;
        _channel.NotifyLive                = _tabPanels[2].GetSlots()[0].IsEnabled;
        _channel.PremiereUpcomingNotifyMode        = _tabPanels[0].GetUpcomingMode();
        _channel.PremiereUpcomingNotifyLeadMinutes = _tabPanels[0].GetUpcomingLead();
        _channel.LiveUpcomingNotifyMode            = _tabPanels[2].GetUpcomingMode();
        _channel.LiveUpcomingNotifyLeadMinutes     = _tabPanels[2].GetUpcomingLead();

        // 常にスロットベースで保存
        _channel.MonitorMode = MonitorMode.Focus;
        _channel.FocusSlots  = _tabPanels.SelectMany(p => p.GetSlots()).ToList();

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

        _channel.State.NextCheckAt = DateTime.MinValue;
        SettingsService.Instance.Channels.UpdateChannel(_channel);
        AppLogger.Log(LogMsg.ChannelDetailSaved, null, _channel.ChannelName);

        var globalInterval = settings.CheckIntervalMinutes;
        bool[] origEnabled = { _origNotifyVideo, _origNotifyShort, _origNotifyLive };
        for (int tabIndex = 0; tabIndex < _tabPanels.Count; tabIndex++)
        {
            var kindSlots         = _tabPanels[tabIndex].GetSlots();
            var representativeSlot = kindSlots[0];
            if (representativeSlot.IsEnabled != origEnabled[tabIndex])
            {
                var statusLabel = representativeSlot.IsEnabled
                    ? $"{AppConstants.LogOnText} ({DescribeSlotInterval(representativeSlot, globalInterval)})"
                    : AppConstants.LogOffText;
                AppLogger.Log(LogMsg.ChannelDetailEnabledChanged, _channel.ChannelName,
                    _channel.ChannelName, AppConstants.KindSlotLabels[tabIndex], statusLabel);
            }
            if (!representativeSlot.IsEnabled) continue;
            foreach (var kindSlot in kindSlots)
            {
                var intervalDesc = DescribeSlotInterval(kindSlot, globalInterval);
                AppLogger.Log(LogMsg.ChannelDetailSlotInterval, _channel.ChannelName, AppConstants.KindSlotLabels[tabIndex], intervalDesc);
            }
        }

        LogUpcomingChange(UpcomingKindLabelPremiere, _origPremiereUpcomingMode, _origPremiereUpcomingLead,
            _channel.PremiereUpcomingNotifyMode, _channel.PremiereUpcomingNotifyLeadMinutes);
        LogUpcomingChange(UpcomingKindLabelLive, _origLiveUpcomingMode, _origLiveUpcomingLead,
            _channel.LiveUpcomingNotifyMode, _channel.LiveUpcomingNotifyLeadMinutes);

        if (needsIntervalAdjust && Owner is MainWindow mainWindow)
            mainWindow.Dispatcher.BeginInvoke(mainWindow.AutoAdjustIntervalForQuota);

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

    // ===== upcoming（プレミア／ライブ）通知方法の変更ログ =====
    private const string UpcomingKindLabelPremiere = "プレミア";
    private const string UpcomingKindLabelLive     = "ライブ";

    private static string UpcomingModeLabel(UpcomingNotifyMode mode) => mode switch
    {
        UpcomingNotifyMode.LiveStartOnly => "開始時のみ",
        UpcomingNotifyMode.Both          => "両方",
        _                                => "待機所のみ",
    };

    private void LogUpcomingChange(string kindLabel, UpcomingNotifyMode origMode, int origLead,
        UpcomingNotifyMode newMode, int newLead)
    {
        if (newMode != origMode)
            AppLogger.Log(LogMsg.ChannelDetailUpcomingModeChanged, _channel.ChannelName, kindLabel, UpcomingModeLabel(newMode));
        if (newLead != origLead)
            AppLogger.Log(LogMsg.ChannelDetailUpcomingLeadChanged, _channel.ChannelName, kindLabel, newLead);
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
    public bool IsEnabled => _enabledCheck?.IsChecked == true || (!HasContent && _slots[0].IsEnabled);
    public bool HasContent { get; private set; } = false;
    public FocusSlot SlotData => HasContent ? GetSlots()[0] : _slots[0];

    private readonly List<FocusSlot> _slots;
    private bool _suppressEnabledEvent = false;
    private System.Windows.Controls.CheckBox? _enabledCheck;
    private ComboBox? _modeBox;
    private ComboBox? _normalIntervalBox, _lowFreqBox;
    private StackPanel? _settingsPanel;
    private StackPanel? _normalPanel, _lowFreqPanel, _focusPanel;

    /// <summary>時間指定1行分（カード）のコントロール一式</summary>
    private sealed class TimeRowControls
    {
        public System.Windows.Controls.Primitives.ToggleButton[] DayBtns = Array.Empty<System.Windows.Controls.Primitives.ToggleButton>();
        public ComboBox? HourBox;
        public ComboBox? MinuteBox;
        public ComboBox? WindowBox;
        public ComboBox? IntervalBox;
    }

    private readonly List<TimeRowControls> _timeRows = new();
    private StackPanel? _timeRowsHost;
    private TextBlock? _timeRowCountText;
    private System.Windows.Controls.Button? _addTimeRowButton;

    /// <summary>種別チェックを外したときの設定パネルの不透明度</summary>
    private const double SettingsPanelDisabledOpacity = 0.4;

    private const double EnabledCheckFontSize = 12; // 種別チェックボックスのラベル
    private const double SectionLabelFontSize = 11; // 各行の見出し
    private const double TimeSeparatorFontSize = 13;// 時刻の「:」
    private const double TimeComboWidth     = 72;  // 時・分のコンボ
    private const double WindowComboWidth   = 78;  // 確認時間・間隔のコンボ

    private static readonly Thickness EnabledCheckMargin = new(0, 0, 0, 10);
    private static readonly Thickness PanelTopMargin   = new(0, 4, 0, 0);
    private static readonly Thickness SectionLabelMargin = new(0, 4, 0, 4);
    private static readonly Thickness SectionRowMargin = new(0, 0, 0, 8);
    private static readonly Thickness TimeSeparatorMargin = new(5, 0, 5, 0);
    private static readonly Thickness SlashSeparatorMargin = new(6, 0, 6, 0);

    // ===== 時間指定の行（カード）関連 =====
    private const int    TimeRowMaxCount               = 5;  // 1種別あたりの上限件数
    private const int    TimeRowMinCount               = 1;  // 1種別あたりの下限件数
    private const int    TimeRowDefaultHour            = 0;  // 追加時の初期 時
    private const int    TimeRowDefaultMinute          = 0;  // 追加時の初期 分
    private const int    TimeRowDefaultWindowMinutes   = 10; // 追加時の初期 投稿確認（分）
    private const int    TimeRowDefaultIntervalMinutes = 1;  // 追加時の初期 間隔（分）
    private const double TimeRowsHostMaxHeight = 260; // 時間カード表示領域の最大高さ（カード約2枚分）

    private const string TimeRowSectionLabel           = "時間指定";
    private const string TimeRowCountFormat            = "{0} / {1} 件";
    private const string TimeRowHeaderFormat           = "時間 {0}";
    private const string TimeRowAddButtonLabel         = "＋ 時間を追加";
    private const string TimeRowRemoveToolTip          = "この時間を削除";
    private const string TimeRowRemoveDisabledToolTip  = "最低1件は必要です";

    private const double TimeRowCardCornerRadius         = 6;
    private const double TimeRowCardBorderThickness      = 1;
    private const double TimeRowRemoveButtonSize         = 22;
    private const double TimeRowRemoveIconSize           = 12; // 削除ボタン内アイコンの一辺
    private const double TimeRowTrashIconCanvasSize      = 24; // ゴミ箱アイコンの Canvas 一辺（元絵柄の座標系）
    private const double TimeRowTrashIconStrokeThickness = 2;  // ゴミ箱アイコンの線の太さ
    private static readonly Thickness TimeRowCardPadding = new(10, 8, 10, 6);
    private static readonly Thickness TimeRowCardMargin  = new(0, 0, 0, 8);

    // ===== upcoming（プレミア／ライブ）通知方法：チャンネル個別設定（スロット非依存） =====
    private const int    DefaultLeadMinutes           = 10;
    private const double UpcomingModeComboWidth       = 160;
    private const double UpcomingLeadComboWidth       = 110;
    private const string UpcomingModeTagWaitingRoom   = "WaitingRoomOnly";
    private const string UpcomingModeTagLiveStart     = "LiveStartOnly";
    private const string UpcomingModeTagBoth          = "Both";
    private const string UpcomingModeRowLabelLive     = "ライブ通知方法";
    private const string UpcomingModeRowLabelPremiere = "プレミア通知方法";
    private const string UpcomingLeadRowLabel         = "待機所通知タイミング";
    private const string EnabledCheckLabelVideo       = "動画チェック";
    private const string EnabledCheckLabelShort       = "Shortチェック";
    private const string EnabledCheckLabelLive        = "ライブチェック";

    private bool _showUpcoming = false;
    private UpcomingNotifyMode _upcomingMode = UpcomingNotifyMode.Both;
    private int _upcomingLead = DefaultLeadMinutes;
    private ComboBox? _upcomingModeBox;
    private ComboBox? _upcomingLeadBox;
    private Grid? _upcomingLeadRow;

    private static readonly string[] DayLabels = { "Sun","Mon","Tue","Wed","Thu","Fri","Sat" };

    public FocusTabPanel(List<FocusSlot> slots)
    {
        // 呼び出し側は1件以上を渡す前提。上限を超える分は切り捨てる
        _slots = slots.Count > 0
            ? slots.Take(TimeRowMaxCount).ToList()
            : new List<FocusSlot> { new FocusSlot { NotifyKind = FixedKind, SlotMode = MonitorMode.Normal } };
    }

    public void SetEnabled(bool enabled)
    {
        foreach (var slot in _slots) slot.IsEnabled = enabled;
        if (_enabledCheck != null)
        {
            _suppressEnabledEvent = true;
            _enabledCheck.IsChecked = enabled;
            _suppressEnabledEvent = false;
        }
        if (_settingsPanel != null)
        {
            _settingsPanel.IsEnabled = enabled;
            _settingsPanel.Opacity   = enabled ? 1.0 : SettingsPanelDisabledOpacity;
        }
    }

    public void ResetContent()
    {
        if (HasContent) { SaveToSlots(); SaveUpcoming(); }
        HasContent    = false;
        _enabledCheck = null;
        _timeRows.Clear();
        _timeRowsHost     = null;
        _timeRowCountText = null;
        _addTimeRowButton = null;
        _modeBox = _normalIntervalBox = _lowFreqBox = null;
        _settingsPanel = _normalPanel = _lowFreqPanel = _focusPanel = null;
        _upcomingModeBox = _upcomingLeadBox = null;
        _upcomingLeadRow = null;
    }

    // ===== upcoming（プレミア／ライブ）通知方法 =====
    public void EnableUpcomingSection(UpcomingNotifyMode mode, int leadMinutes)
    {
        _showUpcoming = true;
        _upcomingMode = mode;
        _upcomingLead = leadMinutes > 0 ? leadMinutes : DefaultLeadMinutes;
    }

    public UpcomingNotifyMode GetUpcomingMode()
    {
        if (HasContent) SaveUpcoming();
        return _upcomingMode;
    }

    public int GetUpcomingLead()
    {
        if (HasContent) SaveUpcoming();
        return _upcomingLead;
    }

    private void SaveUpcoming()
    {
        if (!_showUpcoming) return;
        if (_upcomingModeBox?.SelectedItem is ComboBoxItem upcomingModeItem && upcomingModeItem.Tag is string upcomingModeTagText)
            _upcomingMode = upcomingModeTagText switch
            {
                UpcomingModeTagLiveStart => UpcomingNotifyMode.LiveStartOnly,
                UpcomingModeTagBoth      => UpcomingNotifyMode.Both,
                _                        => UpcomingNotifyMode.WaitingRoomOnly,
            };
        if (_upcomingLeadBox?.SelectedItem is ComboBoxItem upcomingLeadItem && upcomingLeadItem.Tag is string upcomingLeadTagText && int.TryParse(upcomingLeadTagText, out var upcomingLeadValue))
            _upcomingLead = upcomingLeadValue;
    }

    private void UpdateUpcomingLeadRowVisibility()
    {
        if (_upcomingLeadRow == null || _upcomingModeBox == null) return;
        var tag = (_upcomingModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        _upcomingLeadRow.Visibility = tag == UpcomingModeTagLiveStart
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void SaveToSlots()
    {
        // 種別共通の値は全スロットへ書き込む
        foreach (var slot in _slots)
        {
            if (_enabledCheck != null) slot.IsEnabled = _enabledCheck.IsChecked == true;

            // スロットモード
            if (_modeBox?.SelectedItem is ComboBoxItem slotModeItem && slotModeItem.Tag is string slotModeTagText)
                slot.SlotMode = slotModeTagText switch
                {
                    "LowFreq" => MonitorMode.LowFreq,
                    "Focus"   => MonitorMode.Focus,
                    _         => MonitorMode.Normal
                };

            // 通常間隔（0=グローバル）
            if (_normalIntervalBox?.SelectedItem is ComboBoxItem normalIntervalItem && normalIntervalItem.Tag is string normalIntervalTagText && int.TryParse(normalIntervalTagText, out var normalIntervalValue))
                slot.SlotNormalIntervalMinutes = normalIntervalValue;

            // 低頻度間隔
            if (_lowFreqBox?.SelectedItem is ComboBoxItem lowFreqIntervalItem && lowFreqIntervalItem.Tag is string lowFreqIntervalTagText && int.TryParse(lowFreqIntervalTagText, out var lowFreqIntervalValue))
                slot.SlotLowFreqIntervalMinutes = lowFreqIntervalValue;

            // 通知種別はタブ固定
            slot.NotifyKind = FixedKind;
        }

        // 行ごとの値（曜日／時／分／投稿確認／間隔）
        for (int timeRowIndex = 0; timeRowIndex < _timeRows.Count && timeRowIndex < _slots.Count; timeRowIndex++)
        {
            var row     = _timeRows[timeRowIndex];
            var rowSlot = _slots[timeRowIndex];
            int days = 0;
            for (int dayIndex = 0; dayIndex < 7; dayIndex++)
                if (row.DayBtns.Length > dayIndex && row.DayBtns[dayIndex].IsChecked == true) days |= (1 << dayIndex);
            rowSlot.Days   = days;
            rowSlot.Hour   = row.HourBox?.SelectedIndex ?? rowSlot.Hour;
            rowSlot.Minute = (row.MinuteBox?.SelectedIndex ?? 0) * 5;
            if (row.WindowBox?.SelectedItem is ComboBoxItem windowItem && windowItem.Tag is string windowTagText && int.TryParse(windowTagText, out var windowMinutes))
                rowSlot.WindowMinutes = windowMinutes;
            if (row.IntervalBox?.SelectedItem is ComboBoxItem intervalItem && intervalItem.Tag is string intervalTagText && int.TryParse(intervalTagText, out var intervalMinutes))
                rowSlot.IntervalMinutes = intervalMinutes;
        }
    }

    public List<FocusSlot> GetSlots()
    {
        if (HasContent) SaveToSlots();
        // 時間指定以外のモードでは1件目だけを保存対象とする（_slots 自体は切り詰めない）
        return _slots[0].SlotMode == MonitorMode.Focus
            ? new List<FocusSlot>(_slots)
            : new List<FocusSlot> { _slots[0] };
    }

    public UIElement BuildContent()
    {
        HasContent = true;
        _suppressEnabledEvent = true;
        var res   = System.Windows.Application.Current.Resources;
        var stack = new StackPanel();

        // 有効化チェックボックス
        stack.Children.Add(BuildEnabledCheckBox(res));

        // 設定パネル
        _settingsPanel = new StackPanel
        {
            IsEnabled = _slots[0].IsEnabled,
            Opacity   = _slots[0].IsEnabled ? 1.0 : SettingsPanelDisabledOpacity
        };
        stack.Children.Add(_settingsPanel);

        var modeBox = BuildModeComboBox(res);
        _settingsPanel.Children.Add(MakeRow("監視モード", modeBox, res));

        _settingsPanel.Children.Add(BuildNormalIntervalPanel(res));

        _settingsPanel.Children.Add(BuildLowFreqPanel(res));

        _settingsPanel.Children.Add(BuildFocusPanel(res));

        // 5. upcoming（プレミア／ライブ）通知方法 ※チャンネル個別設定（_slots には保存しない）
        //    _settingsPanel の子にすることで、タブのチェック OFF 時に自動でグレーアウトされる
        if (_showUpcoming)
        {
            AddUpcomingSection(_settingsPanel, res);
        }

        // モード切替でパネル表示を切り替え
        UpdateModePanels();
        modeBox.SelectionChanged += (_, _) =>
        {
            UpdateModePanels();
            SaveToSlots();
            if (!_suppressEnabledEvent)
            {
                OnEnabledChanged?.Invoke();
                OnModeChanged?.Invoke();
            }
        };

        stack.Loaded += (_, _) => _suppressEnabledEvent = false;
        return stack;
    }

    /// <summary>有効チェックボックスを生成し、イベントを登録する。</summary>
    private System.Windows.Controls.CheckBox BuildEnabledCheckBox(ResourceDictionary res)
    {
        _enabledCheck = new System.Windows.Controls.CheckBox
        {
            Content    = FixedKind switch
            {
                VideoKind.Short => EnabledCheckLabelShort,
                VideoKind.Live  => EnabledCheckLabelLive,
                _               => EnabledCheckLabelVideo
            },
            IsChecked  = _slots[0].IsEnabled,
            FontSize   = EnabledCheckFontSize,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = (Brush)res["TextPrimaryBrush"],
            Margin     = EnabledCheckMargin
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
        _enabledCheck.Unchecked += (_, _) => { if (_suppressEnabledEvent) return; _settingsPanel!.IsEnabled = false; _settingsPanel!.Opacity = SettingsPanelDisabledOpacity; OnEnabledChanged?.Invoke(); };
        return _enabledCheck;
    }

    /// <summary>監視モードのコンボボックスを生成する。</summary>
    private ComboBox BuildModeComboBox(ResourceDictionary res)
    {
        // 1. 監視モード
        _modeBox = new ComboBox { Style = (Style)res["ModernComboBox"] };
        foreach (var (tag, lbl) in new[] { ("Normal","通常"), ("LowFreq","低頻度"), ("Focus","時間指定") })
            _modeBox.Items.Add(new ComboBoxItem { Content = lbl, Tag = tag, Style = (Style)res["ModernComboBoxItem"] });
        var modeTag = _slots[0].SlotMode switch
        {
            MonitorMode.LowFreq => "LowFreq",
            MonitorMode.Focus   => "Focus",
            _                   => "Normal"
        };
        foreach (ComboBoxItem item in _modeBox.Items)
            if (item.Tag?.ToString() == modeTag) { _modeBox.SelectedItem = item; break; }
        if (_modeBox.SelectedItem == null) _modeBox.SelectedIndex = 0;
        return _modeBox;
    }

    /// <summary>通常モードの間隔選択パネルを生成する。</summary>
    private StackPanel BuildNormalIntervalPanel(ResourceDictionary res)
    {
        // 2. 通常モード（個別間隔、0=グローバル）
        _normalPanel = new StackPanel { Margin = PanelTopMargin };
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
        var savedInterval = _slots[0].SlotNormalIntervalMinutes;
        var selectTag = (savedInterval > 0 && savedInterval < globalInterval) ? "0" : savedInterval.ToString();
        SelectComboByTagStr(_normalIntervalBox, selectTag);
        _normalIntervalBox.SelectionChanged += (_, _) => { if (_suppressEnabledEvent) return; SaveToSlots(); OnEnabledChanged?.Invoke(); };
        _normalPanel.Children.Add(MakeRow("監視間隔", _normalIntervalBox, res));
        return _normalPanel;
    }

    /// <summary>低頻度間隔のパネルを生成する。</summary>
    private StackPanel BuildLowFreqPanel(ResourceDictionary res)
    {
        // 3. 低頻度間隔
        _lowFreqPanel = new StackPanel { Margin = PanelTopMargin };
        _lowFreqBox = new ComboBox { Style = (Style)res["ModernComboBox"] };
        foreach (var (lbl, tag) in new[] { ("1時間","60"),("3時間","180"),("6時間","360"),("12時間","720"),("24時間","1440") })
            _lowFreqBox.Items.Add(new ComboBoxItem { Content = lbl, Tag = tag, Style = (Style)res["ModernComboBoxItem"] });
        SelectComboByTagStr(_lowFreqBox, _slots[0].SlotLowFreqIntervalMinutes.ToString());
        _lowFreqBox.SelectionChanged += (_, _) => { if (_suppressEnabledEvent) return; SaveToSlots(); OnEnabledChanged?.Invoke(); };
        _lowFreqPanel.Children.Add(MakeRow("監視間隔", _lowFreqBox, res));
        return _lowFreqPanel;
    }

    /// <summary>時間指定のパネル（見出し・カード領域・追加ボタン）を生成する。</summary>
    private StackPanel BuildFocusPanel(ResourceDictionary res)
    {
        // 4. 時間指定設定
        _focusPanel = new StackPanel { Margin = PanelTopMargin };

        // 見出し行（「時間指定」／件数表示）
        var focusHeaderGrid = new Grid { Margin = SectionLabelMargin };
        focusHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        focusHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        focusHeaderGrid.Children.Add(new TextBlock
        {
            Text              = TimeRowSectionLabel,
            FontSize          = SectionLabelFontSize,
            Foreground        = (Brush)res["TextSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center
        });
        _timeRowCountText = new TextBlock
        {
            FontSize          = SectionLabelFontSize,
            Foreground        = (Brush)res["TextMutedBrush"],
            VerticalAlignment = VerticalAlignment.Center
        };
        System.Windows.Controls.Grid.SetColumn(_timeRowCountText, 1);
        focusHeaderGrid.Children.Add(_timeRowCountText);
        _focusPanel.Children.Add(focusHeaderGrid);

        // 時間カードを並べる領域（カード2枚分の高さで固定し、超過分は内側でスクロール）
        _timeRowsHost = new StackPanel();
        var timeRowsScroll = new ScrollViewer
        {
            Style                       = (Style)res["SlimScrollViewer"],
            MaxHeight                   = TimeRowsHostMaxHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content                     = _timeRowsHost
        };
        _focusPanel.Children.Add(timeRowsScroll);

        // 時間の追加ボタン
        _addTimeRowButton = new System.Windows.Controls.Button
        {
            Style               = (Style)res["SecondaryButton"],
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _addTimeRowButton.Click += (_, _) => AddTimeRow();
        _focusPanel.Children.Add(_addTimeRowButton);

        RebuildTimeRows();

        return _focusPanel;
    }

    /// <summary>待機所通知設定（通知方法と何分前かの選択）を、設定パネルの子として追加する。</summary>
    private void AddUpcomingSection(StackPanel settingsPanel, ResourceDictionary res)
    {
        settingsPanel.Children.Add(new Separator { Style = (Style)res["HorizontalSeparator"] });

        _upcomingModeBox = new ComboBox { Style = (Style)res["ModernComboBox"], Width = UpcomingModeComboWidth };
        foreach (var (lbl, tag) in new[]
        {
            ("待機所のみ", UpcomingModeTagWaitingRoom),
            ("開始時のみ", UpcomingModeTagLiveStart),
            ("両方",       UpcomingModeTagBoth),
        })
            _upcomingModeBox.Items.Add(new ComboBoxItem { Content = lbl, Tag = tag, Style = (Style)res["ModernComboBoxItem"] });
        var upcomingModeTag = _upcomingMode switch
        {
            UpcomingNotifyMode.LiveStartOnly => UpcomingModeTagLiveStart,
            UpcomingNotifyMode.Both          => UpcomingModeTagBoth,
            _                                => UpcomingModeTagWaitingRoom,
        };
        SelectComboByTagStr(_upcomingModeBox, upcomingModeTag);
        var upcomingModeRowLabel = FixedKind == VideoKind.Live ? UpcomingModeRowLabelLive : UpcomingModeRowLabelPremiere;
        settingsPanel.Children.Add(MakeRow(upcomingModeRowLabel, _upcomingModeBox, res));

        _upcomingLeadBox = new ComboBox { Style = (Style)res["ModernComboBox"], Width = UpcomingLeadComboWidth };
        foreach (var (lbl, tag) in new[] { ("5分前","5"),("10分前","10"),("15分前","15"),("30分前","30"),("60分前","60") })
            _upcomingLeadBox.Items.Add(new ComboBoxItem { Content = lbl, Tag = tag, Style = (Style)res["ModernComboBoxItem"] });
        var upcomingLeadTag = new[] { 5, 10, 15, 30, 60 }.Contains(_upcomingLead)
            ? _upcomingLead.ToString()
            : DefaultLeadMinutes.ToString();
        SelectComboByTagStr(_upcomingLeadBox, upcomingLeadTag);
        _upcomingLeadRow = MakeRow(UpcomingLeadRowLabel, _upcomingLeadBox, res);
        settingsPanel.Children.Add(_upcomingLeadRow);

        _upcomingModeBox.SelectionChanged += (_, _) =>
        {
            UpdateUpcomingLeadRowVisibility();
            if (!_suppressEnabledEvent) OnEnabledChanged?.Invoke();
        };
        UpdateUpcomingLeadRowVisibility();
    }

    /// <summary>時間指定のカード（行）を全件作り直す</summary>
    private void RebuildTimeRows()
    {
        if (_timeRowsHost == null) return;
        var res = System.Windows.Application.Current.Resources;
        _timeRowsHost.Children.Clear();
        _timeRows.Clear();

        for (int slotIndex = 0; slotIndex < _slots.Count; slotIndex++)
        {
            int rowIndex = slotIndex;
            var rowSlot  = _slots[slotIndex];
            var row      = new TimeRowControls();
            var cardStack = new StackPanel();

            // カード見出し＋削除ボタン
            var cardHeaderGrid = new Grid { Margin = SectionRowMargin };
            cardHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cardHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cardHeaderGrid.Children.Add(new TextBlock
            {
                Text              = string.Format(TimeRowHeaderFormat, rowIndex + 1),
                FontSize          = SectionLabelFontSize,
                FontWeight        = System.Windows.FontWeights.SemiBold,
                Foreground        = (Brush)res["TextPrimaryBrush"],
                VerticalAlignment = VerticalAlignment.Center
            });

            var removeButton = new System.Windows.Controls.Button
            {
                Content             = new Viewbox { Width = TimeRowRemoveIconSize, Height = TimeRowRemoveIconSize, Child = BuildTimeRowTrashIconCanvas(res) },
                Width               = TimeRowRemoveButtonSize,
                Height              = TimeRowRemoveButtonSize,
                Background          = Brushes.Transparent,
                BorderThickness     = new Thickness(0),
                Cursor              = System.Windows.Input.Cursors.Hand,
                ToolTip             = TimeRowRemoveToolTip,
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            if (_slots.Count <= TimeRowMinCount)
            {
                removeButton.IsEnabled = false;
                removeButton.Opacity   = AppConstants.DisabledControlOpacity;
                removeButton.ToolTip   = TimeRowRemoveDisabledToolTip;
                ToolTipService.SetShowOnDisabled(removeButton, true);
            }
            removeButton.Click += (_, _) => RemoveTimeRow(rowIndex);
            System.Windows.Controls.Grid.SetColumn(removeButton, 1);
            cardHeaderGrid.Children.Add(removeButton);
            cardStack.Children.Add(cardHeaderGrid);

            // 曜日指定
            var dayRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = SectionRowMargin, HorizontalAlignment = HorizontalAlignment.Center };
            row.DayBtns = new System.Windows.Controls.Primitives.ToggleButton[7];
            for (int dayIndex = 0; dayIndex < 7; dayIndex++)
            {
                var btn = new System.Windows.Controls.Primitives.ToggleButton
                {
                    Content   = DayLabels[dayIndex],
                    Style     = (Style)res["DayToggleButton"],
                    IsChecked = rowSlot.Days == 0 || (rowSlot.Days & (1 << dayIndex)) != 0
                };
                btn.Unchecked += (_, _) =>
                {
                    if (row.DayBtns.All(b => b.IsChecked != true))
                        btn.IsChecked = true;
                };
                row.DayBtns[dayIndex] = btn;
                dayRow.Children.Add(btn);
            }
            cardStack.Children.Add(dayRow);

            // 投稿時刻
            row.HourBox   = new ComboBox { Style = (Style)res["ModernComboBox"], Width = TimeComboWidth };
            row.MinuteBox = new ComboBox { Style = (Style)res["ModernComboBox"], Width = TimeComboWidth };
            for (int hourValue = 0; hourValue < 24; hourValue++)
                row.HourBox.Items.Add(new ComboBoxItem { Content = $"{hourValue:D2}", Style = (Style)res["ModernComboBoxItem"] });
            for (int minuteValue = 0; minuteValue < 60; minuteValue += 5)
                row.MinuteBox.Items.Add(new ComboBoxItem { Content = $"{minuteValue:D2}", Style = (Style)res["ModernComboBoxItem"] });
            row.HourBox.SelectedIndex   = Math.Clamp(rowSlot.Hour, 0, 23);
            row.MinuteBox.SelectedIndex = Math.Clamp(rowSlot.Minute / 5, 0, 11);

            var timeGrid = new Grid { Margin = SectionRowMargin };
            timeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            timeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var timeLbl  = new TextBlock { Text = "投稿時刻", FontSize = SectionLabelFontSize, Foreground = (Brush)res["TextSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center };
            var timeCtrl = new StackPanel { Orientation = Orientation.Horizontal };
            timeCtrl.Children.Add(row.HourBox);
            timeCtrl.Children.Add(new TextBlock { Text = ":", FontSize = TimeSeparatorFontSize, FontWeight = System.Windows.FontWeights.SemiBold, Foreground = (Brush)res["TextSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center, Margin = TimeSeparatorMargin });
            timeCtrl.Children.Add(row.MinuteBox);
            System.Windows.Controls.Grid.SetColumn(timeCtrl, 1);
            timeGrid.Children.Add(timeLbl);
            timeGrid.Children.Add(timeCtrl);
            cardStack.Children.Add(timeGrid);

            // 投稿監視時間 / 監視間隔
            row.WindowBox   = new ComboBox { Style = (Style)res["ModernComboBox"], Width = WindowComboWidth };
            row.IntervalBox = new ComboBox { Style = (Style)res["ModernComboBox"], Width = WindowComboWidth };
            foreach (var (lbl, tag) in new[] { ("3分","3"),("5分","5"),("10分","10"),("15分","15") })
                row.WindowBox.Items.Add(new ComboBoxItem { Content = lbl, Tag = tag, Style = (Style)res["ModernComboBoxItem"] });
            foreach (var (lbl, tag) in new[] { ("30秒","0"),("1分","1"),("5分","5") })
                row.IntervalBox.Items.Add(new ComboBoxItem { Content = lbl, Tag = tag, Style = (Style)res["ModernComboBoxItem"] });
            // デフォルト：投稿確認10分・間隔5分
            var windowTag   = rowSlot.WindowMinutes > 0 ? rowSlot.WindowMinutes.ToString() : "10";
            var intervalTag = rowSlot.IntervalMinutes == 0 ? "0"
                            : new[] { 0, 1, 5 }.Contains(rowSlot.IntervalMinutes) ? rowSlot.IntervalMinutes.ToString() : "5";
            SelectComboByTagStr(row.WindowBox,   windowTag);
            SelectComboByTagStr(row.IntervalBox, intervalTag);
            row.WindowBox.SelectionChanged   += (_, _) => { if (_suppressEnabledEvent) return; OnEnabledChanged?.Invoke(); };
            row.IntervalBox.SelectionChanged += (_, _) => { if (_suppressEnabledEvent) return; OnEnabledChanged?.Invoke(); };

            var wiGrid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            wiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            wiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var wiLbl  = new TextBlock { Text = "投稿確認 / 間隔", FontSize = SectionLabelFontSize, Foreground = (Brush)res["TextSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center };
            var wiCtrl = new StackPanel { Orientation = Orientation.Horizontal };
            wiCtrl.Children.Add(row.WindowBox);
            wiCtrl.Children.Add(new TextBlock { Text = "/", FontSize = SectionLabelFontSize, Foreground = (Brush)res["TextMutedBrush"], VerticalAlignment = VerticalAlignment.Center, Margin = SlashSeparatorMargin });
            wiCtrl.Children.Add(row.IntervalBox);
            System.Windows.Controls.Grid.SetColumn(wiCtrl, 1);
            wiGrid.Children.Add(wiLbl);
            wiGrid.Children.Add(wiCtrl);
            cardStack.Children.Add(wiGrid);

            _timeRowsHost.Children.Add(new Border
            {
                BorderBrush     = (Brush)res["BorderBrush"],
                BorderThickness = new Thickness(TimeRowCardBorderThickness),
                CornerRadius    = new CornerRadius(TimeRowCardCornerRadius),
                Background      = (Brush)res["SurfaceAltBrush"],
                Padding         = TimeRowCardPadding,
                Margin          = TimeRowCardMargin,
                Child           = cardStack
            });
            _timeRows.Add(row);
        }

        UpdateTimeRowCountUI();
    }

    /// <summary>件数表示と追加ボタンの状態を現在の件数に合わせる</summary>
    private void UpdateTimeRowCountUI()
    {
        if (_timeRowCountText != null)
            _timeRowCountText.Text = string.Format(TimeRowCountFormat, _slots.Count, TimeRowMaxCount);
        if (_addTimeRowButton != null)
            _addTimeRowButton.IsEnabled = _slots.Count < TimeRowMaxCount;
    }

    /// <summary>時間指定の行を1件追加する</summary>
    private void AddTimeRow()
    {
        if (_slots.Count >= TimeRowMaxCount) return;
        SaveToSlots();
        _slots.Add(new FocusSlot
        {
            NotifyKind                 = FixedKind,
            SlotMode                   = MonitorMode.Focus,
            IsEnabled                  = _slots[0].IsEnabled,
            SlotNormalIntervalMinutes  = _slots[0].SlotNormalIntervalMinutes,
            SlotLowFreqIntervalMinutes = _slots[0].SlotLowFreqIntervalMinutes,
            Days                       = AppConstants.AllDaysMask,
            Hour                       = TimeRowDefaultHour,
            Minute                     = TimeRowDefaultMinute,
            WindowMinutes              = TimeRowDefaultWindowMinutes,
            IntervalMinutes            = TimeRowDefaultIntervalMinutes
        });
        RebuildTimeRows();
        OnEnabledChanged?.Invoke();
    }

    /// <summary>時間指定の行を1件削除する</summary>
    private void RemoveTimeRow(int rowIndex)
    {
        if (_slots.Count <= TimeRowMinCount || rowIndex < 0 || rowIndex >= _slots.Count) return;
        SaveToSlots();
        _slots.RemoveAt(rowIndex);
        RebuildTimeRows();
        OnEnabledChanged?.Invoke();
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
        var rowGrid = new Grid { Margin = SectionRowMargin };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CtrlColWidth) });
        var lbl = new TextBlock { Text = label, FontSize = SectionLabelFontSize, Foreground = (Brush)res["TextSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center };
        ctrl.HorizontalAlignment = HorizontalAlignment.Stretch;
        System.Windows.Controls.Grid.SetColumn(ctrl, 1);
        rowGrid.Children.Add(lbl);
        rowGrid.Children.Add(ctrl);
        return rowGrid;
    }

    private static void SelectComboByTagStr(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items)
            if (item.Tag?.ToString() == tag) { box.SelectedItem = item; return; }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    /// <summary>時間指定カードの削除ボタン用ゴミ箱アイコン（Lucide 由来。色は目立たない色＝TextMutedBrush）</summary>
    private static readonly string[] TimeRowTrashIconPathData =
        { "M10 11v6", "M14 11v6", "M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6", "M3 6h18", "M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" };

    private static Canvas BuildTimeRowTrashIconCanvas(ResourceDictionary res)
    {
        var canvas = new Canvas { Width = TimeRowTrashIconCanvasSize, Height = TimeRowTrashIconCanvasSize };
        foreach (var pathData in TimeRowTrashIconPathData)
        {
            var path = new System.Windows.Shapes.Path
            {
                Data                = Geometry.Parse(pathData),
                StrokeThickness     = TimeRowTrashIconStrokeThickness,
                StrokeStartLineCap  = PenLineCap.Round,
                StrokeEndLineCap    = PenLineCap.Round,
                StrokeLineJoin      = PenLineJoin.Round,
                Fill                = Brushes.Transparent,
                Stroke              = (Brush)res["TextMutedBrush"]
            };
            canvas.Children.Add(path);
        }
        return canvas;
    }
}
