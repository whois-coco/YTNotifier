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
using YTNotifier.Models;
using YTNotifier.Services;

namespace YTNotifier.Views;

public partial class ChannelDetailWindow : Window
{
    private readonly ChannelInfo _channel;
    private readonly List<FocusTabPanel> _tabPanels = new();
    private int _selectedTab = 0;
    private bool _syncingKind = false;

    public ChannelDetailWindow(ChannelInfo channel, Window owner)
    {
        InitializeComponent();
        Owner    = owner;
        _channel = channel;

        ChannelNameText.Text = channel.ChannelName;
        var cat = SettingsService.Instance.Categories
            .FirstOrDefault(c => c.CategoryId == channel.CategoryId);
        CategoryText.Text = cat?.CategoryName ?? "未設定";

        // 通知種別トグル
        NotifyVideoToggle.IsChecked    = channel.NotifyVideo;
        NotifyShortToggle.IsChecked    = channel.NotifyShort;
        NotifyLiveToggle.IsChecked     = channel.NotifyLive;
        NotifyUpcomingToggle.IsChecked = channel.NotifyUpcoming;

        // リアルタイム連動：トグル変更 → チャンネルデータ更新 → 一覧反映
        NotifyVideoToggle.Checked      += (_, _) => SyncKindToChannel();
        NotifyVideoToggle.Unchecked    += (_, _) => SyncKindToChannel();
        NotifyShortToggle.Checked      += (_, _) => SyncKindToChannel();
        NotifyShortToggle.Unchecked    += (_, _) => SyncKindToChannel();
        NotifyLiveToggle.Checked       += (_, _) => SyncKindToChannel();
        NotifyLiveToggle.Unchecked     += (_, _) => SyncKindToChannel();
        NotifyUpcomingToggle.Checked   += (_, _) => SyncKindToChannel();
        NotifyUpcomingToggle.Unchecked += (_, _) => SyncKindToChannel();

        // 通常モード間隔
        var globalInterval = SettingsService.Instance.Settings.CheckIntervalMinutes;
        foreach (var min in new[] { 3, 5, 10, 15, 20, 30, 60 })
            NormalIntervalBox.Items.Add(new ComboBoxItem
            {
                Content = $"{min}分", Tag = min,
                Style   = (Style)System.Windows.Application.Current.Resources["ModernComboBoxItem"]
            });
        var normalVal = channel.NormalIntervalMinutes == 0 ? globalInterval : channel.NormalIntervalMinutes;
        SelectComboByTag(NormalIntervalBox, normalVal);
        if (NormalIntervalBox.SelectedItem == null) NormalIntervalBox.SelectedIndex = 0;

        // 低頻度
        SelectComboByTag(LowFreqIntervalBox, channel.LowFreqIntervalMinutes);

        // 監視モード
        var modeTag = channel.MonitorMode switch
        {
            MonitorMode.LowFreq => "LowFreq",
            MonitorMode.Focus   => "Focus",
            _                   => "Normal"
        };
        foreach (ComboBoxItem item in MonitorModeCombo.Items)
            if (item.Tag?.ToString() == modeTag) { MonitorModeCombo.SelectedItem = item; break; }
        if (MonitorModeCombo.SelectedItem == null) MonitorModeCombo.SelectedIndex = 0;

        // 時間指定タブ初期化（FocusSlotsから、なければ旧フィールドから移行）
        var slots = channel.FocusSlots.Count > 0
            ? channel.FocusSlots
            : new List<FocusSlot>
            {
                new FocusSlot
                {
                    NotifyKind      = VideoKind.Video,
                    Days            = channel.FocusDays,
                    Hour            = channel.FocusHour,
                    Minute          = channel.FocusMinute,
                    WindowMinutes   = channel.FocusWindowMinutes,
                    IntervalMinutes = channel.FocusIntervalMinutes,
                    IsEnabled       = true
                }
            };

        // 4タブ分作成
        for (int i = 0; i < 4; i++)
        {
            var slot = i < slots.Count ? slots[i] : new FocusSlot();
            _tabPanels.Add(new FocusTabPanel(slot));
        }

        BuildTabUI();
        SelectTab(0);
        UpdatePanels();
        UpdateEstimate();
        UpdateKindToggleStyles();

        // 時間指定モードで開いた場合: アイコンをスロット状態に合わせる
        if (CurrentMode() == MonitorMode.Focus)
        {
            NotifyVideoToggle.IsChecked = false;
            NotifyShortToggle.IsChecked = false;
            NotifyLiveToggle.IsChecked  = false;
            SyncSlotsToKindToggles();
        }
    }

    // ===== タブUI構築 =====
    private void BuildTabUI()
    {
        FocusTabNav.Children.Clear();
        for (int i = 0; i < 4; i++)
        {
            int idx = i;
            var tab = _tabPanels[i];

            // 設定タブと同じ形式: Border + 下線 + TextBlock
            var lbl = new TextBlock
            {
                Text              = $"設定{i + 1}",
                FontSize          = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            var border = new Border
            {
                Padding         = new Thickness(14, 0, 14, 0),
                Cursor          = System.Windows.Input.Cursors.Hand,
                Background      = Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 2),
                Tag             = i,
                Child           = lbl
            };
            border.MouseLeftButtonUp += (_, _) => SelectTab(idx);
            tab.NavBorder = border;
            tab.NavLabel  = lbl;
            SetTabBorderStyle(tab, false);
            FocusTabNav.Children.Add(border);

            tab.OnEnabledChanged = () =>
            {
                SetTabBorderStyle(tab, tab.NavBorder!.Tag is int t && t == _selectedTab);
                UpdateKindToggleStyles();
                UpdateEstimate();
                SyncSlotsToKindToggles();
            };
        }
    }

    private void SelectTab(int idx)
    {
        _selectedTab = idx;
        for (int i = 0; i < 4; i++)
            SetTabBorderStyle(_tabPanels[i], i == idx);

        FocusTabContent.Children.Clear();
        // BuildContentは毎回再生成（データは_slotに保持）
        _tabPanels[idx].ResetContent();
        FocusTabContent.Children.Add(_tabPanels[idx].BuildContent());
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
    }


    // ===== 監視モード・通知種別 =====
    private void UpdateKindToggleStyles()
    {
        var mode = CurrentMode();
        if (mode != MonitorMode.Focus)
        {
            SetToggleActiveBrush(NotifyVideoToggle, "PrimaryBrush");
            SetToggleActiveBrush(NotifyShortToggle, "PrimaryBrush");
            SetToggleActiveBrush(NotifyLiveToggle,  "PrimaryBrush");
        }
    }

    private static void SetToggleActiveBrush(System.Windows.Controls.Primitives.ToggleButton btn, string brushKey)
    {
        var brush = (Brush)System.Windows.Application.Current.Resources[brushKey];
        var style = new Style(typeof(System.Windows.Controls.Primitives.ToggleButton),
            (Style)System.Windows.Application.Current.Resources["KindToggleButtonLarge"]);
        var trigger = new Trigger
        {
            Property = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            Value    = true
        };
        trigger.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty,   brush));
        trigger.Setters.Add(new Setter(System.Windows.Controls.Control.BorderBrushProperty,  brush));
        trigger.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty,   Brushes.White));
        style.Triggers.Add(trigger);
        btn.Style = style;
    }

    private void SyncSlotsToKindToggles()
    {
        if (CurrentMode() != MonitorMode.Focus) return;
        // UI未生成のタブはGetSlot()が正しく動作しないため、_currentSlotDataから取得
        var kinds = new HashSet<VideoKind>();
        for (int i = 0; i < _tabPanels.Count; i++)
        {
            var panel = _tabPanels[i];
            if (!panel.IsEnabled) continue;
            // コンテンツが生成済みならUIから、未生成ならキャッシュデータから
            var kind = panel.HasContent ? panel.GetSlot().NotifyKind : panel.SlotData.NotifyKind;
            kinds.Add(kind);
        }
        _syncingKind = true;
        try
        {
            NotifyVideoToggle.IsChecked = kinds.Contains(VideoKind.Video);
            NotifyShortToggle.IsChecked = kinds.Contains(VideoKind.Short);
            NotifyLiveToggle.IsChecked  = kinds.Contains(VideoKind.Live);
        }
        finally { _syncingKind = false; }
        SyncKindToChannel();
    }

    // ===== イベントハンドラ =====
    private void SyncKindToChannel()
    {
        if (_syncingKind) return;
        _channel.NotifyVideo    = NotifyVideoToggle.IsChecked    == true;
        _channel.NotifyShort    = NotifyShortToggle.IsChecked    == true;
        _channel.NotifyLive     = NotifyLiveToggle.IsChecked     == true;
        _channel.NotifyUpcoming = NotifyUpcomingToggle.IsChecked == true;
        SettingsService.Instance.UpdateChannel(_channel);
        if (Owner is MainWindow mw)
            mw.Dispatcher.BeginInvoke(mw.RefreshChannelList);
    }

    private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }
    private void CloseButton_Click(object sender, RoutedEventArgs e)  => Close();
    private void Cancel_Click(object sender, RoutedEventArgs e)        => Close();

    private void MonitorModeCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdatePanels();
        UpdateEstimate();
        UpdateKindToggleStyles();

        var mode = CurrentMode();
        if (mode == MonitorMode.Focus)
        {
            // 時間指定: スロット状態からアイコンを再構築
            NotifyVideoToggle.IsChecked = false;
            NotifyShortToggle.IsChecked = false;
            NotifyLiveToggle.IsChecked  = false;
            SyncSlotsToKindToggles();
        }
        else
        {
            // 通常・低頻度: 元のチャンネル設定に戻す
            NotifyVideoToggle.IsChecked = _channel.NotifyVideo;
            NotifyShortToggle.IsChecked = _channel.NotifyShort;
            NotifyLiveToggle.IsChecked  = _channel.NotifyLive;
        }
    }

    private void ComboBox_Changed(object sender, SelectionChangedEventArgs e) => UpdateEstimate();

    private MonitorMode CurrentMode()
    {
        var tag = (MonitorModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return tag switch
        {
            "LowFreq" => MonitorMode.LowFreq,
            "Focus"   => MonitorMode.Focus,
            _         => MonitorMode.Normal
        };
    }

    private void UpdatePanels()
    {
        if (FocusSlotsPanel == null || LowFreqPanel == null || NormalPanel == null) return;
        var mode = CurrentMode();
        NormalPanel.Visibility    = mode == MonitorMode.Normal  ? Visibility.Visible : Visibility.Collapsed;
        LowFreqPanel.Visibility   = mode == MonitorMode.LowFreq ? Visibility.Visible : Visibility.Collapsed;
        FocusSlotsPanel.Visibility = mode == MonitorMode.Focus  ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateEstimate()
    {
        if (EstimateText == null || DetailQuotaBar == null || DetailQuotaBarBg == null) return;
        var settings  = SettingsService.Instance.Settings;
        var channels  = SettingsService.Instance.Channels;
        var mode      = CurrentMode();

        var thisUnits = mode == MonitorMode.Focus
            ? ApiQuotaHelper.EstimateDailyUnitsForFocusSlots(_tabPanels.Select(p => p.GetSlot()))
            : ApiQuotaHelper.EstimateDailyUnitsForMode(
                mode, settings.CheckIntervalMinutes, 30, 5,
                GetComboTag(LowFreqIntervalBox, 60));

        var otherUnits = ApiQuotaHelper.EstimateDailyUnitsForChannels(
            settings.CheckIntervalMinutes,
            channels.Where(c => c.IsEnabled && c.ChannelId != _channel.ChannelId));

        var pct        = ApiQuotaHelper.DailyLimit > 0
            ? Math.Min(100.0, (otherUnits + thisUnits) * 100.0 / ApiQuotaHelper.DailyLimit) : 0;
        var pctRounded = (int)Math.Round(pct);

        EstimateText.Text = $"{pctRounded}%";
        var barColor = pctRounded >= 86 ? Color.FromRgb(0xEF, 0x44, 0x44)
                     : pctRounded >= 76 ? Color.FromRgb(0xEA, 0xB3, 0x08)
                     : pctRounded >= 61 ? Color.FromRgb(0xFF, 0xD7, 0x00)
                                        : Color.FromRgb(0x22, 0xC5, 0x5E);
        DetailQuotaBar.Background = new SolidColorBrush(barColor);
        EstimateText.Foreground   = new SolidColorBrush(barColor);
        DetailQuotaBarBg.Tag = pct;

        DetailQuotaBarBg.SizeChanged -= DetailQuotaBarBg_SizeChanged;
        DetailQuotaBarBg.SizeChanged += DetailQuotaBarBg_SizeChanged;
        if (DetailQuotaBarBg.ActualWidth > 0)
            DetailQuotaBar.Width = Math.Max(0, DetailQuotaBarBg.ActualWidth * pct / 100.0);
        else
            Dispatcher.BeginInvoke(() => {
                if (DetailQuotaBarBg.Tag is double d)
                    DetailQuotaBar.Width = Math.Max(0, DetailQuotaBarBg.ActualWidth * d / 100.0);
            }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void DetailQuotaBarBg_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Border bg && bg.Tag is double pct)
            DetailQuotaBar.Width = Math.Max(0, bg.ActualWidth * pct / 100.0);
    }

    // ===== 保存 =====
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _channel.NotifyVideo    = NotifyVideoToggle.IsChecked    == true;
        _channel.NotifyShort    = NotifyShortToggle.IsChecked    == true;
        _channel.NotifyLive     = NotifyLiveToggle.IsChecked     == true;
        _channel.NotifyUpcoming = NotifyUpcomingToggle.IsChecked == true;

        _channel.MonitorMode = CurrentMode();

        if (_channel.MonitorMode == MonitorMode.Normal)
        {
            var g = SettingsService.Instance.Settings.CheckIntervalMinutes;
            var s = GetComboTag(NormalIntervalBox, g);
            _channel.NormalIntervalMinutes = s == g ? 0 : s;
        }
        else if (_channel.MonitorMode == MonitorMode.LowFreq)
        {
            _channel.LowFreqIntervalMinutes = GetComboTag(LowFreqIntervalBox, 60);
        }
        else if (_channel.MonitorMode == MonitorMode.Focus)
        {
            _channel.FocusSlots = _tabPanels.Select(p => p.GetSlot()).ToList();
            // 後方互換: 先頭有効スロットを旧フィールドに反映
            var first = _channel.FocusSlots.FirstOrDefault(s => s.IsEnabled)
                     ?? _channel.FocusSlots.FirstOrDefault();
            if (first != null)
            {
                _channel.FocusHour           = first.Hour;
                _channel.FocusMinute         = first.Minute;
                _channel.FocusWindowMinutes  = first.WindowMinutes;
                _channel.FocusIntervalMinutes = first.IntervalMinutes;
                _channel.FocusDays           = first.Days;
            }
        }

        _channel.NextCheckAt = DateTime.MinValue;
        // UpdateChannel 内で SaveChannels → MarkDirty まで実行される
        SettingsService.Instance.UpdateChannel(_channel);

        DialogResult = true;
        Close();
    }

    // ===== ユーティリティ =====
    private static void SelectComboByTag(ComboBox box, int value)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (item.Tag is int t && t == value) { box.SelectedItem = item; return; }
            if (item.Tag is string s && int.TryParse(s, out var sv) && sv == value) { box.SelectedItem = item; return; }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static int GetComboTag(ComboBox box, int fallback)
    {
        if (box.SelectedItem is ComboBoxItem item)
        {
            if (item.Tag is int t) return t;
            if (item.Tag is string s && int.TryParse(s, out var sv)) return sv;
        }
        return fallback;
    }
}

// ===== 時間指定タブパネル =====
internal class FocusTabPanel
{
    public Border? NavBorder { get; set; }
    public TextBlock? NavLabel { get; set; }
    public Action? OnEnabledChanged { get; set; }
    public bool IsEnabled => _enabledCheck?.IsChecked == true || (!HasContent && _slot.IsEnabled);
    public bool HasContent { get; private set; } = false;
    /// <summary>UIが未生成の間はこのデータを参照する</summary>
    public FocusSlot SlotData => HasContent ? GetSlot() : _slot;

    private readonly FocusSlot _slot;
    private System.Windows.Controls.CheckBox? _enabledCheck;
    private System.Windows.Controls.Primitives.ToggleButton[] _dayBtns = Array.Empty<System.Windows.Controls.Primitives.ToggleButton>();
    private ComboBox? _kindBox, _windowBox, _intervalBox;
    private ComboBox? _hourBox, _minuteBox;
    private StackPanel? _settingsPanel;

    private static readonly (VideoKind kind, string label)[] KindOptions =
    {
        (VideoKind.Video, "動画"),
        (VideoKind.Short, "Short"),
        (VideoKind.Live,  "ライブ配信")
    };
    private static readonly string[] DayLabels = { "Sun","Mon","Tue","Wed","Thu","Fri","Sat" };

    public FocusTabPanel(FocusSlot slot) => _slot = slot;

    public void ResetContent()
    {
        if (HasContent) SaveToSlot();
        HasContent     = false;
        _enabledCheck  = null;
        _dayBtns       = Array.Empty<System.Windows.Controls.Primitives.ToggleButton>();
        _kindBox = _windowBox = _intervalBox = null;
        _hourBox = _minuteBox = null;
        _settingsPanel = null;
    }

    private void SaveToSlot()
    {
        if (_enabledCheck != null) _slot.IsEnabled = _enabledCheck.IsChecked == true;
        if (_kindBox?.SelectedItem is ComboBoxItem ki && ki.Tag is int kt)
            _slot.NotifyKind = (VideoKind)kt;
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
        var res   = System.Windows.Application.Current.Resources;
        var stack = new StackPanel();

        // 有効化チェックボックス
        _enabledCheck = new System.Windows.Controls.CheckBox
        {
            Content    = "この設定を有効にする",
            IsChecked  = _slot.IsEnabled,
            FontSize   = 12,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = (Brush)res["TextPrimaryBrush"],
            Margin     = new Thickness(0, 0, 0, 10)
        };
        _enabledCheck.Checked   += (_, _) => { _settingsPanel!.IsEnabled = true;  _settingsPanel!.Opacity = 1.0; OnEnabledChanged?.Invoke(); };
        _enabledCheck.Unchecked += (_, _) => { _settingsPanel!.IsEnabled = false; _settingsPanel!.Opacity = 0.4; OnEnabledChanged?.Invoke(); };
        stack.Children.Add(_enabledCheck);

        // 設定パネル
        _settingsPanel = new StackPanel
        {
            IsEnabled = _slot.IsEnabled,
            Opacity   = _slot.IsEnabled ? 1.0 : 0.4
        };
        stack.Children.Add(_settingsPanel);

        // 1. 通知種別
        _kindBox = new ComboBox { Style = (Style)res["ModernComboBox"], Width = 130 };
        foreach (var (kind, lbl) in KindOptions)
            _kindBox.Items.Add(new ComboBoxItem { Content = lbl, Tag = (int)kind, Style = (Style)res["ModernComboBoxItem"] });
        _kindBox.SelectedIndex = (int)_slot.NotifyKind;
        _kindBox.SelectionChanged += (_, _) => { SaveToSlot(); OnEnabledChanged?.Invoke(); };
        _settingsPanel.Children.Add(MakeRow("通知種別", _kindBox, res));

        // 2. 曜日指定
        _settingsPanel.Children.Add(new TextBlock
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
                IsChecked = _slot.Days != 0 && (_slot.Days & (1 << i)) != 0
            };
            _dayBtns[i] = btn;
            dayRow.Children.Add(btn);
        }
        _settingsPanel.Children.Add(dayRow);

        // 3. 投稿時刻（時ドロップダウン + 分ドロップダウン）
        _hourBox   = new ComboBox { Style = (Style)res["ModernComboBox"], Width = 76 };
        _minuteBox = new ComboBox { Style = (Style)res["ModernComboBox"], Width = 76 };
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
        timeCtrl.Children.Add(new TextBlock { Text = "時", FontSize = 11, Foreground = (Brush)res["TextSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) });
        timeCtrl.Children.Add(_minuteBox);
        timeCtrl.Children.Add(new TextBlock { Text = "分", FontSize = 11, Foreground = (Brush)res["TextSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
        System.Windows.Controls.Grid.SetColumn(timeCtrl, 1);
        timeGrid.Children.Add(timeLbl);
        timeGrid.Children.Add(timeCtrl);
        _settingsPanel.Children.Add(timeGrid);

        // 4. 前後の幅 / 監視間隔（1行）
        _windowBox   = new ComboBox { Style = (Style)res["ModernComboBox"], Width = 80 };
        _intervalBox = new ComboBox { Style = (Style)res["ModernComboBox"], Width = 70 };
        foreach (var (lbl, tag) in new[] { ("±1分","1"),("±3分","3"),("±5分","5"),("±10分","10"),("±15分","15"),("±30分","30"),("±60分","60"),("±120分","120") })
            _windowBox.Items.Add(new ComboBoxItem { Content = lbl, Tag = tag, Style = (Style)res["ModernComboBoxItem"] });
        foreach (var (lbl, tag) in new[] { ("1分","1"),("3分","3"),("5分","5"),("10分","10"),("15分","15") })
            _intervalBox.Items.Add(new ComboBoxItem { Content = lbl, Tag = tag, Style = (Style)res["ModernComboBoxItem"] });
        SelectComboByTagStr(_windowBox,   _slot.WindowMinutes.ToString());
        SelectComboByTagStr(_intervalBox, _slot.IntervalMinutes.ToString());

        var wiGrid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        wiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        wiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var wiLbl  = new TextBlock { Text = "前後の幅 / 間隔", FontSize = 11, Foreground = (Brush)res["TextSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center };
        var wiCtrl = new StackPanel { Orientation = Orientation.Horizontal };
        wiCtrl.Children.Add(_windowBox);
        wiCtrl.Children.Add(new TextBlock { Text = "/", FontSize = 11, Foreground = (Brush)res["TextMutedBrush"], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) });
        wiCtrl.Children.Add(_intervalBox);
        System.Windows.Controls.Grid.SetColumn(wiCtrl, 1);
        wiGrid.Children.Add(wiLbl);
        wiGrid.Children.Add(wiCtrl);
        _settingsPanel.Children.Add(wiGrid);

        return stack;
    }

    private static Grid MakeRow(string label, FrameworkElement ctrl, ResourceDictionary res)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lbl = new TextBlock { Text = label, FontSize = 11, Foreground = (Brush)res["TextSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center };
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
