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
    // ===== 設定ハンドラ =====
    private void SaveApiKey_Click(object sender, RoutedEventArgs e)
    {
        if (SaveApiKeyButton.Content?.ToString() == "変更")
        {
            AppLogger.Log(LogMsg.ApiKeyEditStarted);
            UpdateApiKeyState(false);
            ApiKeyBox.Text = "";
            return;
        }

        var key = ApiKeyBox.IsReadOnly ? _actualApiKey : ApiKeyBox.Text.Trim();
        if (string.IsNullOrEmpty(key))
        {
            // 未入力のまま保存 → APIキーを変更しないオペレーションとして扱う
            if (!string.IsNullOrEmpty(_actualApiKey))
            {
                AppLogger.Log(LogMsg.ApiKeyUnchanged);
                ApiKeyBox.Text          = new string('●', Math.Min(_actualApiKey.Length, 32));
                ApiKeyBox.IsReadOnly    = true;
                ApiKeyBox.TextAlignment = System.Windows.TextAlignment.Center;
                SetDynamicBrush(ApiKeyBox, TextBox.BackgroundProperty, "SurfaceAltBrush");
                SetDynamicBrush(ApiKeyBox, TextBox.ForegroundProperty, "TextMutedBrush");
                SaveApiKeyButton.Content = "変更";
                SetDynamicBrush(SaveApiKeyButton, Button.BackgroundProperty, "SurfaceElevatedBrush");
                SetDynamicBrush(SaveApiKeyButton, Button.ForegroundProperty, "TextPrimaryBrush");
            }
            return;
        }
        _actualApiKey                            = key;
        SettingsService.Instance.Settings.ApiKey = key;
        SettingsService.Instance.SaveSettings();
        AppLogger.Log(LogMsg.ApiKeyChanged);
        UpdateApiKeyState(true);
    }

    private void UpdateApiKeyState(bool saved)
    {
        if (saved)
        {
            _actualApiKey           = ApiKeyBox.Text.Trim();
            ApiKeyBox.Text          = new string('●', Math.Min(_actualApiKey.Length, 32));
            ApiKeyBox.IsReadOnly    = true;
            ApiKeyBox.TextAlignment = System.Windows.TextAlignment.Center;
            SetDynamicBrush(ApiKeyBox, TextBox.BackgroundProperty, "SurfaceAltBrush");
            SetDynamicBrush(ApiKeyBox, TextBox.ForegroundProperty, "TextMutedBrush");
        }
        else
        {
            ApiKeyBox.Text          = "";
            ApiKeyBox.IsReadOnly    = false;
            ApiKeyBox.TextAlignment = System.Windows.TextAlignment.Left;
            SetDynamicBrush(ApiKeyBox, TextBox.BackgroundProperty, "SurfaceBrush");
            SetDynamicBrush(ApiKeyBox, TextBox.ForegroundProperty, "TextPrimaryBrush");
            ApiKeyBox.Focus();
        }
        SaveApiKeyButton.Content = saved ? "変更" : "保存";
        SetDynamicBrush(SaveApiKeyButton, Button.BackgroundProperty, saved ? "SurfaceElevatedBrush" : "PrimaryBrush");
        if (saved) SetDynamicBrush(SaveApiKeyButton, Button.ForegroundProperty, "TextPrimaryBrush");
        else       SaveApiKeyButton.Foreground = Brushes.White;
    }

    private void DarkModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        var isDark = DarkModeToggle.IsChecked == true;
        SettingsService.Instance.Settings.IsDarkMode = isDark;
        SettingsService.Instance.SaveSettings();
        AppLogger.Log(LogMsg.SettingDarkMode, null, isDark ? "ON" : "OFF");
        App.ApplyTheme(isDark);
        RefreshChannelList(); InvalidateVisual(); UpdateLayout();
    }

    private void NoCategoryModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        var enabled = NoCategoryModeToggle.IsChecked == true;
        SettingsService.Instance.Settings.NoCategoryMode = enabled;
        SettingsService.Instance.SaveSettings();
        AppLogger.Log(LogMsg.SettingNoCategoryMode, null, enabled ? "ON" : "OFF");
        RefreshChannelList();
    }

    private void NotificationToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        var enabled = NotificationToggle.IsChecked == true;
        SettingsService.Instance.Settings.ShowDesktopNotification = enabled;
        SettingsService.Instance.SaveSettings();
        AppLogger.Log(LogMsg.SettingDesktopNotification, null, enabled ? "ON" : "OFF");
    }

    private void ToastStyleComboBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        if (ToastStyleComboBox.SelectedItem is not ComboBoxItem item) return;
        if (Enum.TryParse<YTNotifier.Models.ToastStyle>(item.Tag?.ToString(), out var style))
        {
            SettingsService.Instance.Settings.ToastStyle = style;
            SettingsService.Instance.SaveSettings();
            AppLogger.Log(LogMsg.SettingToastStyle, null, item.Content?.ToString() ?? style.ToString());
        }
    }

    private void GlobalNotifyUpcomingToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        var svc  = SettingsService.Instance;
        var val  = GlobalNotifyUpcomingToggle.IsChecked == true;
        svc.Settings.GlobalNotifyUpcoming = val;
        svc.SaveSettings();
        AppLogger.Log(LogMsg.SettingGlobalNotifyUpcoming, null, val ? "ON" : "OFF");

        // ON/OFFどちらも全チャンネルの NotifyUpcoming に一括適用
        bool changed = false;
        foreach (var ch in svc.Channels.Where(c => c.NotifyUpcoming != val))
        {
            ch.NotifyUpcoming = val;
            changed = true;
        }
        if (changed) svc.SaveChannelsSilent();
    }

    private void NotificationSoundToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        var enabled = NotificationSoundToggle.IsChecked == true;
        SettingsService.Instance.Settings.NotificationSound = enabled;
        SettingsService.Instance.SaveSettings();
        AppLogger.Log(LogMsg.SettingNotificationSound, null, enabled ? "ON" : "OFF");
    }

    private void FlashTaskbarToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        var enabled = FlashTaskbarToggle.IsChecked == true;
        SettingsService.Instance.Settings.FlashTaskbar = enabled;
        SettingsService.Instance.SaveSettings();
        AppLogger.Log(LogMsg.SettingFlashTaskbar, null, enabled ? "ON" : "OFF");
    }

    private void TrayToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        var enabled = TrayToggle.IsChecked == true;
        SettingsService.Instance.Settings.MinimizeToTray = enabled;
        SettingsService.Instance.SaveSettings();
        AppLogger.Log(LogMsg.SettingMinimizeToTray, null, enabled ? "ON" : "OFF");
    }

    // ===== ミュートボタン =====
    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        _isMuted = !_isMuted;
        var s = SettingsService.Instance.Settings;

        if (_isMuted)
        {
            _preMuteDesktopNotification      = s.ShowDesktopNotification;
            _preMuteNotificationSound        = s.NotificationSound;
            _preMuteFlashTaskbar             = s.FlashTaskbar;
            s.PreMuteDesktopNotification     = _preMuteDesktopNotification;
            s.PreMuteNotificationSound       = _preMuteNotificationSound;
            s.PreMuteFlashTaskbar            = _preMuteFlashTaskbar;
            s.IsMuted                        = true;
            s.ShowDesktopNotification        = false;
            s.NotificationSound              = false;
            s.FlashTaskbar                   = false;
        }
        else
        {
            s.ShowDesktopNotification = _preMuteDesktopNotification;
            s.NotificationSound       = _preMuteNotificationSound;
            s.FlashTaskbar            = _preMuteFlashTaskbar;
            s.IsMuted                 = false;
        }

        SettingsService.Instance.SaveSettings();
        AppLogger.Log(LogMsg.SettingMute, null, _isMuted ? "ON" : "OFF");
        NotificationToggle.IsChecked      = s.ShowDesktopNotification;
        NotificationSoundToggle.IsChecked = s.NotificationSound;
        FlashTaskbarToggle.IsChecked      = s.FlashTaskbar;
        UpdateMuteButton(_isMuted);
    }

    private void UpdateMuteButton(bool muted)
    {
        var template = MuteButton.Template;
        if (template == null) return;

        // bell（OFF時）表示切替
        foreach (var n in new[] { "BellIcon", "BellIcon2" })
            if (template.FindName(n, MuteButton) is System.Windows.Shapes.Path p)
                p.Visibility = muted ? Visibility.Collapsed : Visibility.Visible;

        // bell-off（ON時・赤）表示切替
        foreach (var n in new[] { "BellOffIcon", "BellOffIcon2", "BellOffIcon3", "BellOffIcon4" })
            if (template.FindName(n, MuteButton) is System.Windows.Shapes.Path p)
                p.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;

        MuteButton.ToolTip = muted ? "通知ミュート: ON（クリックで解除）" : "通知ミュート: OFF";
    }

    // ===== コンパクトモード =====
    private void CompactModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || _applyingCompactMode) return;
        var enabled = CompactModeToggle.IsChecked == true;
        SettingsService.Instance.Settings.CompactMode = enabled;
        SettingsService.Instance.SaveSettings();
        AppLogger.Log(LogMsg.SettingCompactMode, null, enabled ? "ON" : "OFF");
        ApplyCompactMode(enabled);
    }

    private void CompactModeButton_Click(object sender, RoutedEventArgs e)
    {
        var enabled = !SettingsService.Instance.Settings.CompactMode;
        AppLogger.Log(LogMsg.SettingCompactMode, null, enabled ? "ON" : "OFF");
        ApplyCompactMode(enabled);
    }

    private void ApplyCompactMode(bool enabled, bool skipRefresh = false)
    {
        if (_applyingCompactMode) return;
        _applyingCompactMode = true;
        try
        {
            SettingsService.Instance.Settings.CompactMode = enabled;
            SettingsService.Instance.SaveSettings();
            CompactModeToggle.IsChecked = enabled;

            if (enabled)
            {
                _preCompactSidebarCollapsed = _sidebarCollapsed;
                // 強制折り畳み
                if (!_sidebarCollapsed) CollapseSidebar();
                // アイコン20pxに縮小
                SetSidebarIconSize(20);
                // 折り畳みボタンをグレーアウト・無効化
                SidebarToggleButton.IsEnabled = false;
                SidebarToggleButton.Opacity   = 0.3;

                Dispatcher.Invoke(() =>
                {
                    ContentColumn.Width = new GridLength(ContentWidthCompact); ContentColumn.MinWidth = ContentWidthCompact;
                    MaxWidth = MinWidth = Width = CompactTotalWidth;
                }, System.Windows.Threading.DispatcherPriority.Render);
            }
            else
            {
                // アイコン24pxに戻す
                SetSidebarIconSize(24);
                // 折り畳みボタンを有効化
                SidebarToggleButton.IsEnabled = true;
                SidebarToggleButton.Opacity   = 1.0;

                MaxWidth = double.PositiveInfinity; MinWidth = WindowMinWidth;
                ContentColumn.Width = new GridLength(ContentWidthNormal); ContentColumn.MinWidth = ContentWidthNormal;

                if (_sidebarCollapsed != _preCompactSidebarCollapsed)
                {
                    if (_preCompactSidebarCollapsed) CollapseSidebar();
                    else                             ExpandSidebar();
                }
                Dispatcher.Invoke(SyncWindowWidth, System.Windows.Threading.DispatcherPriority.Render);
            }

            UpdateCompactModeButton(enabled);
            if (!skipRefresh) RefreshChannelList();
        }
        finally { _applyingCompactMode = false; }
    }

    private void SetSidebarIconSize(int size)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // NavWatch（x:Name="NavWatchIcon" で直接取得）
            if (NavWatch.Template?.FindName("NavWatchIcon", NavWatch) is Viewbox vw)
            { vw.Width = size; vw.Height = size; }

            // Border(Bd) > StackPanel > Viewbox の構造のボタン
            foreach (var btn in new Button[] { SidebarToggleButton, MuteButton, CompactModeButton, PinButton })
            {
                if (btn.Template?.FindName("Bd", btn) is Border bd &&
                    bd.Child is StackPanel sp &&
                    sp.Children.Count > 0 &&
                    sp.Children[0] is Viewbox vb)
                { vb.Width = size; vb.Height = size; }
            }

            // NavSettings は Grid(Bd) > StackPanel > Viewbox の構造
            if (NavSettings.Template?.FindName("Bd", NavSettings) is System.Windows.Controls.Grid grid)
            {
                foreach (var child in grid.Children)
                    if (child is StackPanel sp2 && sp2.Children.Count > 0 && sp2.Children[0] is Viewbox vb2)
                    { vb2.Width = size; vb2.Height = size; break; }
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // サイドバー折畳の共通処理
    private void SetStatusTextVisibility(Visibility v)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (StatusBadge.Template?.FindName("StatusText", StatusBadge) is System.Windows.Controls.TextBlock txt)
                txt.Visibility = v;
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void SetSidebarLabels(Visibility v)
    {
        // テンプレート適用後に実行（起動直後はまだ適用されていない場合があるため遅延）
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var name in new[] { "LblNavWatch", "LblNavSettings", "LblMuteButton",
                                         "LblCompactModeButton", "LblPinButton", "LblSidebarToggleButton" })
            {
                var btnName = name[3..];
                var btn = FindSidebarButton(btnName);
                if (btn?.Template == null) continue;
                var lbl = btn.Template.FindName(name, btn) as System.Windows.Controls.TextBlock;
                if (lbl != null) lbl.Visibility = v;
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private Button? FindSidebarButton(string name) => name switch
    {
        "NavWatch"            => NavWatch,
        "NavSettings"         => NavSettings,
        "MuteButton"          => MuteButton,
        "CompactModeButton"   => CompactModeButton,
        "PinButton"           => PinButton,
        "SidebarToggleButton" => SidebarToggleButton,
        _                     => null
    };

    private void CollapseSidebar()
    {
        _sidebarCollapsed       = true;
        SidebarColumn.Width     = new GridLength(SidebarCollapsedWidth);
        MenuLabel.Text          = " ";
        StatusBadge.Visibility  = Visibility.Visible;
        StatusBadgeColumn.Width = new GridLength(1, GridUnitType.Star);
        SetSidebarLabels(Visibility.Collapsed);
        SetStatusTextVisibility(Visibility.Collapsed);
        UpdateToggleIcon("▶");
        UpdateToggleIconColor(MonitorService.Instance.IsRunning);
        SettingsService.Instance.Settings.SidebarCollapsed = true;
        SettingsService.Instance.SaveSettingsSilent();
        UpdateMinWidth();
        SyncWindowWidth();
    }

    private void ExpandSidebar()
    {
        _sidebarCollapsed       = false;
        SidebarColumn.Width     = new GridLength(SidebarExpandedWidth);
        MenuLabelWrap.Visibility = Visibility.Visible;
        MenuLabel.Text          = "MENU";
        StatusBadge.Visibility  = Visibility.Visible;
        StatusBadgeColumn.Width = new GridLength(1, GridUnitType.Star);
        SetSidebarLabels(Visibility.Visible);
        SetStatusTextVisibility(Visibility.Visible);
        UpdateToggleIcon("◀");
        UpdateToggleIconColor(false);
        UpdateMonitorStatus(MonitorService.Instance.IsRunning);
        SettingsService.Instance.Settings.SidebarCollapsed = false;
        SettingsService.Instance.SaveSettingsSilent();
        UpdateMinWidth();
        SyncWindowWidth();
    }

    private void UpdateCompactModeButton(bool enabled)
    {
        if (CompactModeButton.Template?.FindName("ShrinkIcon", CompactModeButton) is System.Windows.Shapes.Path shrink)
            shrink.Visibility = enabled ? Visibility.Visible   : Visibility.Collapsed;
        if (CompactModeButton.Template?.FindName("ExpandIcon", CompactModeButton) is System.Windows.Shapes.Path expand)
            expand.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        CompactModeButton.ToolTip = enabled ? "コンパクトモード: ON（クリックで解除）" : "コンパクトモード: OFF";
    }

    // ===== バックアップ / インポート =====
    private void ExportBackup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "バックアップの保存先を選択",
            Filter = BackupFileFilterSingle,
            FileName = $"{BackupFilePrefix}{DateTime.Now:yyyyMMdd}.ytbk",
            DefaultExt = ".ytbk",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var path = SettingsService.Instance.ExportBackup(dlg.FileName);
            BackupStatusText.Text = $"✅ エクスポート完了: {System.IO.Path.GetFileName(path)}";
            SetDynamicBrush(BackupStatusText, TextBlock.ForegroundProperty, "SuccessBrush");
            AppLogger.Log(LogMsg.SettingBackupExported, null, System.IO.Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            BackupStatusText.Text = $"❌ エラー: {ex.Message}";
            SetDynamicBrush(BackupStatusText, TextBlock.ForegroundProperty, "ErrorBrush");
        }
    }

    private void ImportBackup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "復元するバックアップファイルを選択",
            Filter = AppConstants.BackupFileFilter,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dlg.ShowDialog() != true) return;

        if (ConfirmDialog.Show(this, "復元の確認", "現在の設定がバックアップで上書きされます。\n続行しますか？", "上書きする") != true) return;

        var currentLogLevel = SettingsService.Instance.Settings.LogLevel;
        var (success, message) = SettingsService.Instance.ImportBackup(dlg.FileName);
        BackupStatusText.Text = success ? $"✅ {message}" : $"❌ {message}";
        SetDynamicBrush(BackupStatusText, TextBlock.ForegroundProperty, success ? "SuccessBrush" : "ErrorBrush");
        if (success)
        {
            SettingsService.Instance.Settings.LogLevel = currentLogLevel;
            AppLogger.Log(LogMsg.SettingBackupImported, null, System.IO.Path.GetFileName(dlg.FileName));
            LoadSettings(); RefreshChannelList();
        }
    }

    // ===== ピンボタン / 常に前面 =====
    private void AlwaysOnTopToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        var enabled = AlwaysOnTopToggle.IsChecked == true;
        Topmost = enabled;
        SettingsService.Instance.Settings.AlwaysOnTop = enabled;
        SettingsService.Instance.SaveSettings();
        AppLogger.Log(LogMsg.SettingAlwaysOnTop, null, enabled ? "ON" : "OFF");
        UpdatePinButton(enabled);
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        var enabled = !SettingsService.Instance.Settings.AlwaysOnTop;
        Topmost = enabled;
        SettingsService.Instance.Settings.AlwaysOnTop = enabled;
        SettingsService.Instance.SaveSettings();
        AppLogger.Log(LogMsg.SettingAlwaysOnTop, null, enabled ? "ON" : "OFF");
        _loadingSettings = true;
        AlwaysOnTopToggle.IsChecked = enabled;
        _loadingSettings = false;
        UpdatePinButton(enabled);
    }

    private void UpdatePinButton(bool pinned)
    {
        if (PinButton.Template?.FindName("PinIcon", PinButton) is System.Windows.Shapes.Path icon)
            icon.Stroke = pinned
                ? (Brush)Application.Current.Resources["ErrorBrush"]
                : (Brush)Application.Current.Resources["SidebarTextBrush"];
        PinButton.ToolTip = pinned ? "常に前面に表示: ON（クリックで解除）" : "常に前面に表示: OFF";
    }

    private void StartupToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        var enabled = StartupToggle.IsChecked == true;
        SettingsService.Instance.Settings.StartWithWindows = enabled;
        SettingsService.Instance.SaveSettings();
        AppLogger.Log(LogMsg.SettingStartWithWindows, null, enabled ? "ON" : "OFF");
        SetStartup(enabled);
    }

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
        catch { }
    }

    // ===== チェック間隔 =====
    private void IntervalComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        if (IntervalComboBox.SelectedItem is not ComboBoxItem item) return;
        if (!int.TryParse(item.Tag?.ToString(), out var minutes)) return;

        var channels = SettingsService.Instance.Channels;
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
        SettingsService.Instance.SaveSettings();
        AppLogger.Log(LogMsg.SettingCheckInterval, null, minutes);
        MonitorService.Instance.ResetNormalChannels(minutes);
        MonitorService.Instance.RestartWithNewInterval();
        UpdateQuotaInfo();
    }

    // ===== 通知テスト =====
    private void TestNotification_Click(object sender, RoutedEventArgs e)
    {
        try   { MonitorService.Instance.SendTestNotification(); }
        catch (Exception ex) { AppLogger.Log(LogMsg.TestNotifyFailed, null, ex.Message); }
    }

    // ===== ログ =====
    private void ShowActivityLog_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.Log(LogMsg.ActivityLogWindowOpened);
        var win = new ActivityLogWindow { Owner = this };
        win.Show();
    }

    private const string DebugDllName         = "YTNotifier.Debug.dll";
    private const string BackupFileFilterSingle = "YTNotifierバックアップ (*.ytbk)|*.ytbk";
    private const string BackupFilePrefix       = "YTNotifier_";
    private const string StartupRegistryKey     = "YTNotifier";

    private static readonly string DebugDllPath = System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(Environment.ProcessPath
            ?? System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
        DebugDllName);

    internal static bool IsDebugDllAvailable() => System.IO.File.Exists(DebugDllPath);

    private void OpenDebugWindow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var asm  = System.Reflection.Assembly.LoadFrom(DebugDllPath);
            var type = asm.GetType("YTNotifier.Debug.DebugWindow");
            if (type == null) { AppLogger.Log(LogMsg.DebugWindowNotFound); return; }
            var win  = (Window?)Activator.CreateInstance(type, this);
            win?.Show();
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.DevToolError, null, ex.Message);
        }
    }

    private void RefreshLogStats()
    {
        var (count, bytes, oldest, newest) = LoggerService.Instance.GetLogStats();
        if (count == 0) { LogStatsText.Text = "ログファイルはありません"; return; }
        var mb = bytes / 1024.0 / 1024.0;
        var sizeStr = mb >= 1 ? $"{mb:F1} MB" : $"{bytes / 1024.0:F0} KB";
        LogStatsText.Text = $"ファイル数: {count} 件  合計サイズ: {sizeStr}\n最古: {oldest:yyyy/MM/dd}  最新: {newest:yyyy/MM/dd}";
    }

    private void LogLevelComboBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        if (LogLevelComboBox.SelectedItem is ComboBoxItem item)
        {
            var level = item.Tag?.ToString() ?? "Info";
            SettingsService.Instance.Settings.LogLevel = level;
            SettingsService.Instance.SaveSettings();
            AppLogger.Log(LogMsg.SettingLogLevel, null, item.Content?.ToString() ?? level);
        }
    }

    private void AutoCleanLogsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        var enabled = AutoCleanLogsToggle.IsChecked == true;
        SettingsService.Instance.Settings.AutoCleanLogs = enabled;
        SettingsService.Instance.SaveSettings();
        AppLogger.Log(LogMsg.SettingAutoCleanLogs, null, enabled ? "ON" : "OFF");
    }

    private void LogRetentionComboBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        if (LogRetentionComboBox.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out var days))
        {
            SettingsService.Instance.Settings.LogRetentionDays = days;
            SettingsService.Instance.SaveSettings();
            AppLogger.Log(LogMsg.SettingLogRetention, null, days == -1 ? "無制限" : days.ToString());
        }
    }

    private void CleanLogsNow_Click(object sender, RoutedEventArgs e)
    {
        var days = SettingsService.Instance.Settings.LogRetentionDays;
        if (days == -1) { LogCleanResultText.Text = "保持期間が「無制限」のため削除しません"; return; }
        var (deleted, freed) = LoggerService.Instance.CleanOldLogs(days);
        var mb = freed / 1024.0 / 1024.0;
        var sizeStr = mb >= 1 ? $"{mb:F1} MB" : $"{freed / 1024.0:F0} KB";
        LogCleanResultText.Text = deleted > 0 ? $"✅ {deleted} 件削除（{sizeStr} 解放）" : "削除対象のログファイルはありませんでした";
        AppLogger.Log(LogMsg.LogManualDeleted, null, deleted, sizeStr);
        RefreshLogStats();
    }

    private void ApiKeyLink_Click(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        OpenUrl(e.Uri.AbsoluteUri); e.Handled = true;
    }

}
