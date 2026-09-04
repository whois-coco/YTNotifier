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
        Loaded  += (_, _) => WindowCornerHelper.Apply(this);

        ChannelNameText.Text = channel.ChannelName;
        var cat = SettingsService.Instance.Categories
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

        // 監視設定タブ初期化：既存モードをスロット形式に変換
        // 未設定タブ（Short/ライブ配信）もチャンネル本来のモードを引き継ぐための既定値生成
        List<FocusSlot> slots = channel.FocusSlots.Count > 0
            ? channel.FocusSlots
            : new List<FocusSlot> { channel.CreateDefaultFocusSlot(VideoKind.Video) };

        // 3タブ分作成（デフォルト種別: 動画/Short/ライブ配信）
        VideoKind[] defaultKinds = { VideoKind.Video, VideoKind.Short, VideoKind.Live };
        bool[] kindEnabled = { channel.NotifyVideo, channel.NotifyShort, channel.NotifyLive };
        for (int i = 0; i < AppConstants.KindSlotCount; i++)
        {
            var slot = i < slots.Count ? slots[i] : channel.CreateDefaultFocusSlot(defaultKinds[i]);
            slot.IsEnabled = kindEnabled[i]; // チャンネル一覧の種別ON/OFFを反映
            _tabPanels.Add(new FocusTabPanel(slot));
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
        for (int i = 0; i < AppConstants.KindSlotCount; i++)
        {
            int idx = i;
            var tab = _tabPanels[i];
            tab.FixedKind = AppConstants.KindSlotKinds[i];

            var lbl = new TextBlock
            {
                Text              = AppConstants.KindSlotLabels[i],
                FontSize          = TabLabelFontSize,
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
                Padding         = TabPadding,
                Cursor          = System.Windows.Input.Cursors.Hand,
                Background      = Brushes.Transparent,
                BorderThickness = TabUnderline,
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
        for (int i = 0; i < AppConstants.KindSlotCount; i++)
            SetTabBorderStyle(_tabPanels[i], i == idx);

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

            var segs    = new[] { (DetailQuotaBarNormal, normalW), (DetailQuotaBarLowFreq, lowFreqW), (DetailQuotaBarFocus, focusW) };
            var nonZero = segs.Where(s => s.Item2 > 0).ToList();
            foreach (var (seg, _) in segs) seg.CornerRadius = new CornerRadius(0);
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
        _channel.PremiereUpcomingNotifyMode        = _tabPanels[0].GetUpcomingMode();
        _channel.PremiereUpcomingNotifyLeadMinutes = _tabPanels[0].GetUpcomingLead();
        _channel.LiveUpcomingNotifyMode            = _tabPanels[2].GetUpcomingMode();
        _channel.LiveUpcomingNotifyLeadMinutes     = _tabPanels[2].GetUpcomingLead();

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
                    _channel.ChannelName, AppConstants.KindSlotLabels[i], statusLabel);
            }
            if (!slot.IsEnabled) continue;
            var intervalDesc = DescribeSlotInterval(slot, globalInterval);
            AppLogger.Log(LogMsg.ChannelDetailSlotInterval, _channel.ChannelName, AppConstants.KindSlotLabels[i], intervalDesc);
        }

        LogUpcomingChange(UpcomingKindLabelPremiere, _origPremiereUpcomingMode, _origPremiereUpcomingLead,
            _channel.PremiereUpcomingNotifyMode, _channel.PremiereUpcomingNotifyLeadMinutes);
        LogUpcomingChange(UpcomingKindLabelLive, _origLiveUpcomingMode, _origLiveUpcomingLead,
            _channel.LiveUpcomingNotifyMode, _channel.LiveUpcomingNotifyLeadMinutes);

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
        if (HasContent) { SaveToSlot(); SaveUpcoming(); }
        HasContent    = false;
        _enabledCheck = null;
        _dayBtns      = Array.Empty<System.Windows.Controls.Primitives.ToggleButton>();
        _modeBox = _windowBox = _intervalBox = null;
        _hourBox = _minuteBox = _normalIntervalBox = _lowFreqBox = null;
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
        if (_upcomingModeBox?.SelectedItem is ComboBoxItem mi && mi.Tag is string mt)
            _upcomingMode = mt switch
            {
                UpcomingModeTagLiveStart => UpcomingNotifyMode.LiveStartOnly,
                UpcomingModeTagBoth      => UpcomingNotifyMode.Both,
                _                        => UpcomingNotifyMode.WaitingRoomOnly,
            };
        if (_upcomingLeadBox?.SelectedItem is ComboBoxItem li && li.Tag is string ls && int.TryParse(ls, out var lv))
            _upcomingLead = lv;
    }

    private void UpdateUpcomingLeadRowVisibility()
    {
        if (_upcomingLeadRow == null || _upcomingModeBox == null) return;
        var tag = (_upcomingModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        _upcomingLeadRow.Visibility = tag == UpcomingModeTagLiveStart
            ? Visibility.Collapsed
            : Visibility.Visible;
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
                VideoKind.Short => EnabledCheckLabelShort,
                VideoKind.Live  => EnabledCheckLabelLive,
                _               => EnabledCheckLabelVideo
            },
            IsChecked  = _slot.IsEnabled,
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
        var savedInterval = _slot.SlotNormalIntervalMinutes;
        var selectTag = (savedInterval > 0 && savedInterval < globalInterval) ? "0" : savedInterval.ToString();
        SelectComboByTagStr(_normalIntervalBox, selectTag);
        _normalIntervalBox.SelectionChanged += (_, _) => { if (_suppressEnabledEvent) return; SaveToSlot(); OnEnabledChanged?.Invoke(); };
        _normalPanel.Children.Add(MakeRow("監視間隔", _normalIntervalBox, res));
        _settingsPanel.Children.Add(_normalPanel);

        // 3. 低頻度間隔
        _lowFreqPanel = new StackPanel { Margin = PanelTopMargin };
        _lowFreqBox = new ComboBox { Style = (Style)res["ModernComboBox"] };
        foreach (var (lbl, tag) in new[] { ("1時間","60"),("3時間","180"),("6時間","360"),("12時間","720"),("24時間","1440") })
            _lowFreqBox.Items.Add(new ComboBoxItem { Content = lbl, Tag = tag, Style = (Style)res["ModernComboBoxItem"] });
        SelectComboByTagStr(_lowFreqBox, _slot.SlotLowFreqIntervalMinutes.ToString());
        _lowFreqBox.SelectionChanged += (_, _) => { if (_suppressEnabledEvent) return; SaveToSlot(); OnEnabledChanged?.Invoke(); };
        _lowFreqPanel.Children.Add(MakeRow("監視間隔", _lowFreqBox, res));
        _settingsPanel.Children.Add(_lowFreqPanel);

        // 4. 時間指定設定
        _focusPanel = new StackPanel { Margin = PanelTopMargin };

        // 曜日指定
        _focusPanel.Children.Add(new TextBlock
        {
            Text       = "曜日指定",
            FontSize   = SectionLabelFontSize,
            Foreground = (Brush)res["TextSecondaryBrush"],
            Margin     = SectionLabelMargin
        });
        var dayRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = SectionRowMargin, HorizontalAlignment = HorizontalAlignment.Center };
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
        _hourBox   = new ComboBox { Style = (Style)res["ModernComboBox"], Width = TimeComboWidth };
        _minuteBox = new ComboBox { Style = (Style)res["ModernComboBox"], Width = TimeComboWidth };
        for (int h = 0; h < 24; h++)
            _hourBox.Items.Add(new ComboBoxItem { Content = $"{h:D2}", Style = (Style)res["ModernComboBoxItem"] });
        for (int m = 0; m < 60; m += 5)
            _minuteBox.Items.Add(new ComboBoxItem { Content = $"{m:D2}", Style = (Style)res["ModernComboBoxItem"] });
        _hourBox.SelectedIndex   = Math.Clamp(_slot.Hour, 0, 23);
        _minuteBox.SelectedIndex = Math.Clamp(_slot.Minute / 5, 0, 11);

        var timeGrid = new Grid { Margin = SectionRowMargin };
        timeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        timeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var timeLbl  = new TextBlock { Text = "投稿時刻", FontSize = SectionLabelFontSize, Foreground = (Brush)res["TextSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center };
        var timeCtrl = new StackPanel { Orientation = Orientation.Horizontal };
        timeCtrl.Children.Add(_hourBox);
        timeCtrl.Children.Add(new TextBlock { Text = ":", FontSize = TimeSeparatorFontSize, FontWeight = System.Windows.FontWeights.SemiBold, Foreground = (Brush)res["TextSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center, Margin = TimeSeparatorMargin });
        timeCtrl.Children.Add(_minuteBox);
        System.Windows.Controls.Grid.SetColumn(timeCtrl, 1);
        timeGrid.Children.Add(timeLbl);
        timeGrid.Children.Add(timeCtrl);
        _focusPanel.Children.Add(timeGrid);

        // 投稿監視時間 / 監視間隔
        _windowBox   = new ComboBox { Style = (Style)res["ModernComboBox"], Width = WindowComboWidth };
        _intervalBox = new ComboBox { Style = (Style)res["ModernComboBox"], Width = WindowComboWidth };
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
        var wiLbl  = new TextBlock { Text = "投稿確認 / 間隔", FontSize = SectionLabelFontSize, Foreground = (Brush)res["TextSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center };
        var wiCtrl = new StackPanel { Orientation = Orientation.Horizontal };
        wiCtrl.Children.Add(_windowBox);
        wiCtrl.Children.Add(new TextBlock { Text = "/", FontSize = SectionLabelFontSize, Foreground = (Brush)res["TextMutedBrush"], VerticalAlignment = VerticalAlignment.Center, Margin = SlashSeparatorMargin });
        wiCtrl.Children.Add(_intervalBox);
        System.Windows.Controls.Grid.SetColumn(wiCtrl, 1);
        wiGrid.Children.Add(wiLbl);
        wiGrid.Children.Add(wiCtrl);
        _focusPanel.Children.Add(wiGrid);

        _settingsPanel.Children.Add(_focusPanel);

        // 5. upcoming（プレミア／ライブ）通知方法 ※チャンネル個別設定（_slot には保存しない）
        //    _settingsPanel の子にすることで、タブのチェック OFF 時に自動でグレーアウトされる
        if (_showUpcoming)
        {
            _settingsPanel.Children.Add(new Separator { Style = (Style)res["HorizontalSeparator"] });

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
            _settingsPanel.Children.Add(MakeRow(upcomingModeRowLabel, _upcomingModeBox, res));

            _upcomingLeadBox = new ComboBox { Style = (Style)res["ModernComboBox"], Width = UpcomingLeadComboWidth };
            foreach (var (lbl, tag) in new[] { ("5分前","5"),("10分前","10"),("15分前","15"),("30分前","30"),("60分前","60") })
                _upcomingLeadBox.Items.Add(new ComboBoxItem { Content = lbl, Tag = tag, Style = (Style)res["ModernComboBoxItem"] });
            var upcomingLeadTag = new[] { 5, 10, 15, 30, 60 }.Contains(_upcomingLead)
                ? _upcomingLead.ToString()
                : DefaultLeadMinutes.ToString();
            SelectComboByTagStr(_upcomingLeadBox, upcomingLeadTag);
            _upcomingLeadRow = MakeRow(UpcomingLeadRowLabel, _upcomingLeadBox, res);
            _settingsPanel.Children.Add(_upcomingLeadRow);

            _upcomingModeBox.SelectionChanged += (_, _) =>
            {
                UpdateUpcomingLeadRowVisibility();
                if (!_suppressEnabledEvent) OnEnabledChanged?.Invoke();
            };
            UpdateUpcomingLeadRowVisibility();
        }

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
        var g = new Grid { Margin = SectionRowMargin };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CtrlColWidth) });
        var lbl = new TextBlock { Text = label, FontSize = SectionLabelFontSize, Foreground = (Brush)res["TextSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center };
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
