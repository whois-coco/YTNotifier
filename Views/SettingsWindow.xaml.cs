using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using YTNotifier.Constants;
using YTNotifier.Models;
using YTNotifier.Services;
using Application  = System.Windows.Application;
using Brush        = System.Windows.Media.Brush;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace YTNotifier.Views;

public partial class SettingsWindow : Window
{
    /// <summary>設定コントロールの初期化中はイベントハンドラを黙らせるためのフラグ（MainWindow と同じ考え方の独立フィールド）。</summary>
    private bool _loadingSettings = false;

    public SettingsWindow(Window owner)
    {
        InitializeComponent();
        Owner   = owner;
        WindowCornerHelper.ApplyOnLoaded(this);

        LoadSettings();
        SelectCategory(CategoryNavBasic);
    }

    // ===== タイトルバー =====

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        => WindowTitleBarHelper.DragMoveOnPress(this, e);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void SettingsWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    // ===== カテゴリナビゲーション（縦） =====

    private Dictionary<object, UIElement> CategoryPages => new()
    {
        [CategoryNavBasic]        = CategoryPageBasic,
        [CategoryNavDisplay]      = CategoryPageDisplay,
        [CategoryNavNotification] = CategoryPageNotification,
        [CategoryNavLog]          = CategoryPageLog,
        [CategoryNavData]         = CategoryPageData,
        [CategoryNavPlugins]      = CategoryPagePlugins,
        [CategoryNavAbout]        = CategoryPageAbout,
    };

    private Dictionary<object, string> CategoryNames => new()
    {
        [CategoryNavBasic]        = "基本設定",
        [CategoryNavDisplay]      = "デザイン",
        [CategoryNavNotification] = "通知",
        [CategoryNavLog]          = "ログ",
        [CategoryNavData]         = "データ管理",
        [CategoryNavPlugins]      = "プラグイン",
        [CategoryNavAbout]        = "ABOUT",
    };

    private void CategoryNav_Click(object sender, MouseButtonEventArgs e) => SelectCategory(sender);

    private void SelectCategory(object sender)
    {
        var pages = CategoryPages;
        var names = CategoryNames;

        if (names.TryGetValue(sender, out var categoryName))
            AppLogger.Log(LogMsg.SettingsSubNavSwitched, null, categoryName);

        foreach (var (nav, page) in pages)
        {
            page.Visibility = Visibility.Collapsed;
            if (nav is Border navBorder)
            {
                navBorder.BorderBrush = new SolidColorBrush(Colors.Transparent);
                if (navBorder.Child is StackPanel navPanel)
                    foreach (var child in navPanel.Children.OfType<TextBlock>())
                        MainWindow.SetDynamicBrush(child, TextBlock.ForegroundProperty, "TextMutedBrush");
            }
        }

        if (sender is Border active && pages.TryGetValue(active, out var activePage))
        {
            activePage.Visibility = Visibility.Visible;
            active.BorderBrush    = (Brush)Application.Current.Resources["PrimaryBrush"];
            if (active.Child is StackPanel activeSp)
                foreach (var child in activeSp.Children.OfType<TextBlock>())
                    MainWindow.SetDynamicBrush(child, TextBlock.ForegroundProperty, "PrimaryBrush");

            if (active == CategoryNavPlugins)
                RefreshPluginSettings();
        }
    }

    // ===== 設定読み込み（開いた時点の値を読み直して表示するだけ。リアルタイム同期は行わない） =====

    private void LoadSettings()
    {
        var settings = SettingsService.Instance.Settings;

        InitApiKeySlotComboBox();
        InitNotificationSoundSetComboBox();
        _actualApiKey  = settings.ApiKey;
        ApiKeyBox.Text = _actualApiKey;
        UpdateApiKeyState(!string.IsNullOrEmpty(_actualApiKey));

        _loadingSettings = true;
        var windowColorOptions = BuildWindowColorOptions();
        WindowColorComboBox.ItemsSource  = windowColorOptions;
        WindowColorComboBox.SelectedItem = windowColorOptions.FirstOrDefault(o => o.Theme == settings.Theme);
        BuildAccentColorSwatches();
        NoCategoryModeToggle.IsChecked          = settings.NoCategoryMode;
        NotificationToggle.IsChecked            = settings.ShowDesktopNotification;
        NotificationSoundToggle.IsChecked       = settings.NotificationSound;
        FlashTaskbarToggle.IsChecked            = settings.FlashTaskbar;
        TrayToggle.IsChecked                    = settings.MinimizeToTray;
        StartupToggle.IsChecked                 = settings.StartWithWindows;
        // 通知スタイル（_loadingSettings内で設定しないとSelectionChangedで上書きされる）
        foreach (ComboBoxItem item in ToastStyleComboBox.Items)
            if (item.Tag?.ToString() == settings.ToastStyle.ToString())
            { ToastStyleComboBox.SelectedItem = item; break; }
        if (ToastStyleComboBox.SelectedItem == null) ToastStyleComboBox.SelectedIndex = 0;
        SetControlEnabled(ToastStyleComboBox, settings.ShowDesktopNotification);
        SetControlEnabled(NotificationSoundSetComboBox, settings.NotificationSound);
        _loadingSettings = false;

        _loadingSettings = true;
        var items = IntervalComboBox.Items.Cast<ComboBoxItem>().ToList();
        IntervalComboBox.SelectedItem = items.FirstOrDefault(
            i => i.Tag?.ToString() == settings.CheckIntervalMinutes.ToString()) ?? items[1];
        _loadingSettings = false;

        UpdateIntervalAvailability();

        _loadingSettings = true;
        AutoCleanLogsToggle.IsChecked = settings.AutoCleanLogs;
        TraceLogToggle.IsChecked      = settings.TraceLogEnabled;
        var retItems = LogRetentionComboBox.Items.Cast<ComboBoxItem>().ToList();
        LogRetentionComboBox.SelectedItem =
            retItems.FirstOrDefault(i => i.Tag?.ToString() == settings.LogRetentionDays.ToString())
            ?? retItems[2];
        _loadingSettings = false;
        RefreshLogStats();

        AboutVersionText.Text = $"v{AppConstants.AppVersion}";
        AboutGeminiApiText.Text = $"Gemini API（{AppConstants.GeminiModelName}）使用";

        if (Owner is MainWindow mainWindow && mainWindow.UpdateAvailableBtn.Visibility == Visibility.Visible)
            CheckForUpdateBtn.IsEnabled = false;
    }

    // 設定画面の ON／OFF スイッチ1件分の共通処理。読み込み中は何もしない。
    // 設定へ反映し、未保存の印とログを記録したあと、必要なら続きの処理を実行する。
    private void ApplyToggleSetting(System.Windows.Controls.CheckBox toggle, Action<AppSettings, bool> assign, LogMsg logMessage, Action<bool>? afterApplied = null)
    {
        if (_loadingSettings) return;
        var enabled = toggle.IsChecked == true;
        assign(SettingsService.Instance.Settings, enabled);
        SettingsService.Instance.MarkDirty();
        AppLogger.Log(logMessage, null, AppLogger.OnOffText(enabled));
        afterApplied?.Invoke(enabled);
    }

    // 操作の有効・無効に合わせて、薄さも切り替える。
    private static void SetControlEnabled(UIElement control, bool isEnabled)
    {
        control.IsEnabled = isEnabled;
        control.Opacity   = isEnabled ? 1.0 : AppConstants.DisabledControlOpacity;
    }

    private void NoCategoryModeToggle_Changed(object sender, RoutedEventArgs e)
        => ApplyToggleSetting(NoCategoryModeToggle, (settings, enabled) => settings.NoCategoryMode = enabled, LogMsg.SettingNoCategoryMode);

    private void NotificationToggle_Changed(object sender, RoutedEventArgs e)
        => ApplyToggleSetting(NotificationToggle, (settings, enabled) => settings.ShowDesktopNotification = enabled, LogMsg.SettingDesktopNotification,
            enabled => SetControlEnabled(ToastStyleComboBox, enabled));

    private void ToastStyleComboBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        if (ToastStyleComboBox.SelectedItem is not ComboBoxItem item) return;
        if (Enum.TryParse<YTNotifier.Models.ToastStyle>(item.Tag?.ToString(), out var style))
        {
            SettingsService.Instance.Settings.ToastStyle = style;
            SettingsService.Instance.MarkDirty();
            AppLogger.Log(LogMsg.SettingToastStyle, null, item.Content?.ToString() ?? style.ToString());
        }
    }

    private void NotificationSoundToggle_Changed(object sender, RoutedEventArgs e)
        => ApplyToggleSetting(NotificationSoundToggle, (settings, enabled) => settings.NotificationSound = enabled, LogMsg.SettingNotificationSound,
            enabled => SetControlEnabled(NotificationSoundSetComboBox, enabled));

    private void FlashTaskbarToggle_Changed(object sender, RoutedEventArgs e)
        => ApplyToggleSetting(FlashTaskbarToggle, (settings, enabled) => settings.FlashTaskbar = enabled, LogMsg.SettingFlashTaskbar);

    private void TrayToggle_Changed(object sender, RoutedEventArgs e)
        => ApplyToggleSetting(TrayToggle, (settings, enabled) => settings.MinimizeToTray = enabled, LogMsg.SettingMinimizeToTray);

    private void StartupToggle_Changed(object sender, RoutedEventArgs e)
        => ApplyToggleSetting(StartupToggle, (settings, enabled) => settings.StartWithWindows = enabled, LogMsg.SettingStartWithWindows,
            enabled => SetStartup(enabled));

    private const string StartupRegistryKey = "YTNotifier";

    private static void SetStartup(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            var exe = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (enable) key.SetValue(StartupRegistryKey, $"\"{exe}\"");
            else        key.DeleteValue(StartupRegistryKey, false);
        }
        catch (Exception ex) { AppLogger.Log(LogMsg.StartupRegistrationFailed, null, ex.Message); }
    }

    // ===== チェック間隔 =====
    private void IntervalComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        if (IntervalComboBox.SelectedItem is not ComboBoxItem item) return;
        if (!int.TryParse(item.Tag?.ToString(), out var minutes)) return;

        var channels = SettingsService.Instance.Channels.GetChannelsSnapshot();
        var (safe, recommended) = ApiQuotaHelper.ValidateInterval(minutes, channels);
        if (!safe)
        {
            var recItem = IntervalComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == recommended.ToString());
            if (recItem != null && recItem != item)
            {
                IntervalComboBox.SelectedItem = recItem;
                ConfirmDialog.Show(this, "API クォータ超過防止", $"チャンネル数 {channels.Count} 件では {minutes} 分間隔だと\n1日のAPIクォータ（10,000ユニット）を超過します。\n\n自動的に {recommended} 分間隔に調整しました。", "OK", showCancel: false);
                return;
            }
        }

        SettingsService.Instance.Settings.CheckIntervalMinutes = minutes;
        SettingsService.Instance.MarkDirty();
        AppLogger.Log(LogMsg.SettingCheckInterval, null, minutes);
        MonitorService.Instance.ResetNormalChannels(minutes);
        MonitorService.Instance.RestartWithNewInterval();
        UpdateIntervalAvailability();
    }

    // ===== 通知テスト =====
    private void TestNotification_Click(object sender, RoutedEventArgs e)
    {
        try   { MonitorService.Instance.SendTestNotification(); }
        catch (Exception ex) { AppLogger.Log(LogMsg.TestNotifyFailed, null, ex.Message); }
    }

    // ===== 通知音セット選択 =====

    private const string DefaultSoundSetLabel = "デフォルト";

    private void InitNotificationSoundSetComboBox()
    {
        _loadingSettings = true;
        NotificationSoundSetComboBox.Items.Clear();
        NotificationSoundSetComboBox.Items.Add(DefaultSoundSetLabel);
        foreach (var setName in NotificationService.GetAvailableSoundSets())
            NotificationSoundSetComboBox.Items.Add(setName);

        var current = SettingsService.Instance.Settings.NotificationSoundSet;
        var target = string.IsNullOrEmpty(current) || !NotificationSoundSetComboBox.Items.Contains(current)
            ? DefaultSoundSetLabel
            : current;
        NotificationSoundSetComboBox.SelectedItem = target;
        _loadingSettings = false;
    }

    private void NotificationSoundSetComboBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        var selected = NotificationSoundSetComboBox.SelectedItem as string ?? DefaultSoundSetLabel;
        var value = selected == DefaultSoundSetLabel ? string.Empty : selected;
        SettingsService.Instance.Settings.NotificationSoundSet = value;
        SettingsService.Instance.MarkDirty();
        AppLogger.Log(LogMsg.SettingNotificationSoundSet, null, selected);
    }

    // ===== チェック間隔の選択肢の有効・無効 =====

    private void UpdateIntervalAvailability()
    {
        try
        {
            var channels = SettingsService.Instance.Channels.GetChannelsSnapshot();
            UpdateIntervalComboBoxItems(channels, channels.Count);
        }
        catch (Exception ex) { AppLogger.Log(LogMsg.UiUpdateFailed, null, nameof(UpdateIntervalAvailability), ex.Message); }
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
