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
    private const int    ExpandedTotalWidth      = SidebarExpandedWidth  + ContentWidthNormal;  // 515
    private const int    WindowMinHeight         = 500;
    private bool _isOffline             = false;

    // Win32
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION        = 2;

    // ===== 監視ステータス =====
    private void UpdateMonitorStatus(bool isRunning)
    {
        var quotaUntil   = MonitorService.Instance.QuotaSuspendedUntil;
        var quotaSuspend = quotaUntil.HasValue;

        // クォータ停止中は強制的に停止扱い
        var effectiveRunning = !quotaSuspend && isRunning;

        if (StatusBadge.Template?.FindName("IconRunning", StatusBadge) is Viewbox iconOn)
            iconOn.Visibility  = (!_isOffline && effectiveRunning) ? Visibility.Visible : Visibility.Collapsed;
        if (StatusBadge.Template?.FindName("IconStopped", StatusBadge) is Viewbox iconOff)
            iconOff.Visibility = (!_isOffline && !effectiveRunning) ? Visibility.Visible : Visibility.Collapsed;
        if (StatusBadge.Template?.FindName("IconOffline", StatusBadge) is Viewbox iconOffline)
            iconOffline.Visibility = _isOffline ? Visibility.Visible : Visibility.Collapsed;

        if (StatusBadge.Template?.FindName("StatusText", StatusBadge) is System.Windows.Controls.TextBlock txt)
        {
            txt.Text = _isOffline ? "オフライン"
                : quotaSuspend ? "クォータ超過"
                : effectiveRunning ? "監視中" : "停止中";
            txt.Foreground = _isOffline
                ? (System.Windows.Media.Brush)Application.Current.Resources["ErrorBrush"]
                : quotaSuspend
                    ? (System.Windows.Media.Brush)Application.Current.Resources["WarningBrush"]
                    : effectiveRunning
                        ? (System.Windows.Media.Brush)Application.Current.Resources["SuccessBrush"]
                        : (System.Windows.Media.Brush)Application.Current.Resources["SidebarTextBrush"];
            txt.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        }

        SetDynamicBrush(StatusBadge, Button.BackgroundProperty, "SidebarStatusBgBrush");

        // クォータ停止中はボタンを無効化してトグル操作を封じる
        StatusBadge.IsEnabled = !quotaSuspend && !_isOffline;
        StatusBadge.ToolTip   = _isOffline   ? "オフライン"
            : quotaSuspend ? $"クォータ超過のため停止中（{quotaUntil!.Value:HH:mm} に再開）"
            : effectiveRunning ? "クリックして監視を停止" : "クリックして監視を開始";

        if (_sidebarCollapsed) UpdateToggleIconColor(effectiveRunning);
    }

    /// <summary>NIC状態・HTTP疎通・pingの順でネットワーク接続を確認する</summary>
    internal async void CheckNetworkState()
    {
        var isAvailable = await NetworkStatusService.IsOnlineAsync();

        if (isAvailable == _isOffline)
            UpdateNetworkState(isAvailable);
    }

    internal void UpdateNetworkState(bool isAvailable)
    {
        _isOffline = !isAvailable;

        // チャンネルリスト表示切替
        ChannelList.Visibility   = isAvailable ? Visibility.Visible   : Visibility.Collapsed;
        OfflinePanel.Visibility  = isAvailable ? Visibility.Collapsed : Visibility.Visible;

        // 監視ステータスアイコン更新
        UpdateMonitorStatus(MonitorService.Instance.IsRunning);

        if (_isOffline)
        {
            if (MonitorService.Instance.IsRunning) MonitorService.Instance.Stop();
            AppLogger.Log(LogMsg.NetworkDisconnected);
        }
        else
        {
            AppLogger.Log(LogMsg.NetworkRestored);
            if (!string.IsNullOrEmpty(SettingsService.Instance.Settings.ApiKey))
            {
                MonitorService.Instance.Start();
                UpdateMonitorStatus(true);
            }
        }
    }

    // ===== サイドバー =====
    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_sidebarCollapsed) { AppLogger.Log(LogMsg.SidebarToggled, null, "展開"); ExpandSidebar(); }
        else                   { AppLogger.Log(LogMsg.SidebarToggled, null, "折り畳み"); CollapseSidebar(); }
    }

    private void UpdateMinWidth() { MinHeight = WindowMinHeight; }

    private void SyncWindowWidth()
    {
        if (SettingsService.Instance.Settings.CompactMode)
        {
            ContentColumn.Width    = new GridLength(ContentWidthCompact);
            ContentColumn.MinWidth = ContentWidthCompact;
            MaxWidth = MinWidth = Width = CompactTotalWidth;
            return;
        }
        var target = _sidebarCollapsed ? CollapsedTotalWidth : ExpandedTotalWidth;
        MaxWidth = MinWidth = target;
        if ((int)Width != target) Width = target;
    }

    private void UpdateToggleIcon(string direction)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var collapse = SidebarToggleButton.Template?.FindName("CollapseIcon", SidebarToggleButton)
                as System.Windows.Shapes.Path;
            var expand   = SidebarToggleButton.Template?.FindName("ExpandToggleIcon", SidebarToggleButton)
                as System.Windows.Shapes.Path;
            var lbl      = SidebarToggleButton.Template?.FindName("LblSidebarToggleButton", SidebarToggleButton)
                as System.Windows.Controls.TextBlock;
            bool showCollapse = direction == "◀";
            if (collapse != null) collapse.Visibility = showCollapse ? Visibility.Visible   : Visibility.Collapsed;
            if (expand   != null) expand.Visibility   = showCollapse ? Visibility.Collapsed : Visibility.Visible;
            if (lbl      != null) lbl.Text            = showCollapse ? "折り畳む" : "展開する";
            SidebarToggleButton.ToolTip = showCollapse ? "折り畳む" : "展開する";
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void UpdateToggleIconColor(bool isRunning)
    {
        // 折り畳み時の色設定は不要（展開ボタンは常にSidebarTextBrush）
    }

    // ===== 設定サブナビゲーション =====
    private void SettingsNavBorder_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var pages = new Dictionary<object, UIElement>
        {
            [SettingsNavApp]     = SettingsPageApp,
            [SettingsNavDisplay] = SettingsPageDisplay,
            [SettingsNavMonitor] = SettingsPageMonitor,
            [SettingsNavPlugins] = SettingsPagePlugins,
            [SettingsNavAbout]   = SettingsPageAbout,
        };

        var pageNames = new Dictionary<object, string>
        {
            [SettingsNavApp]     = "動作",
            [SettingsNavDisplay] = "表示",
            [SettingsNavMonitor] = "通知",
            [SettingsNavPlugins] = "プラグイン",
            [SettingsNavAbout]   = "ABOUT",
        };
        if (pageNames.TryGetValue(sender, out var pageName))
            AppLogger.Log(LogMsg.SettingsSubNavSwitched, null, pageName);

        foreach (var (nav, page) in pages)
        {
            page.Visibility = Visibility.Collapsed;
            if (nav is Border b)
            {
                b.BorderBrush = new SolidColorBrush(Colors.Transparent);
                if (b.Child is TextBlock tb) SetDynamicBrush(tb, TextBlock.ForegroundProperty, "TextMutedBrush");
            }
        }

        if (sender is Border active && pages.TryGetValue(active, out var activePage))
        {
            activePage.Visibility = Visibility.Visible;
            active.BorderBrush    = (Brush)Application.Current.Resources["PrimaryBrush"];
            if (active.Child is TextBlock activeTb)
                SetDynamicBrush(activeTb, TextBlock.ForegroundProperty, "PrimaryBrush");

            if (active == SettingsNavMonitor)
                UpdateQuotaInfo();
            else if (active == SettingsNavPlugins)
                RefreshPluginSettings();
        }
    }

    // ===== ナビゲーション =====
    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender == NavWatch)         AppLogger.Log(LogMsg.NavPageSwitched, null, "チャンネル");
        else if (sender == NavDormant)  AppLogger.Log(LogMsg.NavPageSwitched, null, "休眠");
        else if (sender == NavSettings) AppLogger.Log(LogMsg.NavPageSwitched, null, "設定");

        PageWatch.Visibility    = Visibility.Collapsed;
        PageDormant.Visibility  = Visibility.Collapsed;
        PageSettings.Visibility = Visibility.Collapsed;

        if (sender == NavDormant)
        {
            PageDormant.Visibility  = Visibility.Visible;
            _currentNav = "Dormant";
            RefreshDormantChannelList();
        }
        else if (sender == NavWatch)
        {
            PageWatch.Visibility = Visibility.Visible;
            _currentNav          = "Watch";
            RefreshChannelList();
        }
        else if (sender == NavSettings)
        {
            PageSettings.Visibility = Visibility.Visible;
            SettingsNavBorder_Click(SettingsNavDisplay, null!);
        }

        // セレクターバー切り替え
        SetNavSelectorBar(NavWatch,    sender == NavWatch);
        SetNavSelectorBar(NavDormant,  sender == NavDormant);
        SetNavSelectorBar(NavSettings, sender == NavSettings);
    }

    // ===== ナビゲーション・ホットキー =====
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;

        switch (e.Key)
        {
            case Key.D1: e.Handled = true; Nav_Click(NavWatch,    new RoutedEventArgs()); break;
            case Key.D2: e.Handled = true; Nav_Click(NavDormant,  new RoutedEventArgs()); break;
            case Key.D3: e.Handled = true; Nav_Click(NavSettings, new RoutedEventArgs()); break;
            case Key.D4: e.Handled = true; SidebarToggle_Click(SidebarToggleButton, new RoutedEventArgs()); break;
            case Key.D5: e.Handled = true; MuteButton_Click(MuteButton, new RoutedEventArgs()); break;
            case Key.D6: e.Handled = true; CompactModeButton_Click(CompactModeButton, new RoutedEventArgs()); break;
            case Key.D7: e.Handled = true; PinButton_Click(PinButton, new RoutedEventArgs()); break;
        }
    }

    private void NavWatch_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        var clearItem = new MenuItem { Header = "🔔 NEWバッジを全て消す" };
        clearItem.Click += (_, _) =>
        {
            var channels = SettingsService.Instance.Channels.Where(c => c.HasUnread).ToList();
            foreach (var c in channels) c.HasUnread = false;
            if (channels.Count > 0)
            {
                SettingsService.Instance.MarkDirty();
                RefreshChannelList();
            }
            AppLogger.Log(LogMsg.CategoryContextClearNew, null, "全チャンネル");
        };

        var hasUnread = SettingsService.Instance.Channels.Any(c => c.HasUnread);
        clearItem.IsEnabled = hasUnread;
        clearItem.Opacity   = hasUnread ? 1.0 : 0.4;

        var menu = new ContextMenu();
        menu.Items.Add(clearItem);
        menu.IsOpen = true;
    }

    private void SetNavSelectorBar(Button btn, bool active)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (btn.Template?.FindName("SelectorBar", btn) is Border bar)
            {
                if (active)
                    SetDynamicBrush(bar, Border.BackgroundProperty, "SidebarActiveBrush");
                else
                    bar.Background = System.Windows.Media.Brushes.Transparent;
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ===== タイトルバー =====
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { TitleBar_Maximize(sender, e); return; }
        if (e.ButtonState == MouseButtonState.Pressed)
            SendMessage(new WindowInteropHelper(this).Handle, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
    }

    private void TitleBar_Minimize(object sender, RoutedEventArgs e) => WindowState = System.Windows.WindowState.Minimized;

    private void TitleBar_Maximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == System.Windows.WindowState.Maximized
            ? System.Windows.WindowState.Normal : System.Windows.WindowState.Maximized;

    private void TitleBar_Close(object sender, RoutedEventArgs e) => Close();

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

        AppLogger.Log(LogMsg.SettingMute, null, _isMuted ? "ON" : "OFF");
        _loadingSettings = true;
        NotificationToggle.IsChecked      = s.ShowDesktopNotification;
        NotificationSoundToggle.IsChecked = s.NotificationSound;
        FlashTaskbarToggle.IsChecked      = s.FlashTaskbar;
        ToastStyleComboBox.IsEnabled           = s.ShowDesktopNotification;
        ToastStyleComboBox.Opacity             = s.ShowDesktopNotification ? 1.0 : AppConstants.DisabledControlOpacity;
        NotificationSoundSetComboBox.IsEnabled = s.NotificationSound;
        NotificationSoundSetComboBox.Opacity   = s.NotificationSound ? 1.0 : AppConstants.DisabledControlOpacity;
        _loadingSettings = false;
        UpdateMuteButton(_isMuted);
        SettingsService.Instance.MarkDirty();
    }

    private void UpdateMuteButton(bool muted)
    {
        var template = MuteButton.Template;
        if (template == null) return;

        foreach (var n in new[] { "BellIcon", "BellIcon2" })
            if (template.FindName(n, MuteButton) is System.Windows.Shapes.Path p)
                p.Visibility = muted ? Visibility.Collapsed : Visibility.Visible;

        foreach (var n in new[] { "BellOffIcon", "BellOffIcon2", "BellOffIcon3", "BellOffIcon4" })
            if (template.FindName(n, MuteButton) is System.Windows.Shapes.Path p)
                p.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;

        MuteButton.ToolTip = muted ? "通知ミュート: ON（クリックで解除）" : "通知ミュート: OFF";
    }

    // ===== コンパクトモードボタン =====
    private void CompactModeButton_Click(object sender, RoutedEventArgs e)
    {
        var enabled = !SettingsService.Instance.Settings.CompactMode;
        AppLogger.Log(LogMsg.SettingCompactMode, null, enabled ? "ON" : "OFF");
        ApplyCompactMode(enabled);
    }

    private void UpdateCompactModeButton(bool enabled)
    {
        if (CompactModeButton.Template?.FindName("ShrinkIcon", CompactModeButton) is System.Windows.Shapes.Path shrink)
            shrink.Visibility = enabled ? Visibility.Visible   : Visibility.Collapsed;
        if (CompactModeButton.Template?.FindName("ExpandIcon", CompactModeButton) is System.Windows.Shapes.Path expand)
            expand.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        CompactModeButton.ToolTip = enabled ? "コンパクトモード: ON（クリックで解除）" : "コンパクトモード: OFF";
    }

    // ===== ピンボタン =====
    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        var enabled = !SettingsService.Instance.Settings.AlwaysOnTop;
        Topmost = enabled;
        SettingsService.Instance.Settings.AlwaysOnTop = enabled;
        AppLogger.Log(LogMsg.SettingAlwaysOnTop, null, enabled ? "ON" : "OFF");
        _loadingSettings = true;
        AlwaysOnTopToggle.IsChecked = enabled;
        _loadingSettings = false;
        UpdatePinButton(enabled);
        SettingsService.Instance.MarkDirty();
    }

    private void UpdatePinButton(bool pinned)
    {
        if (PinButton.Template?.FindName("PinIcon", PinButton) is System.Windows.Shapes.Path icon)
            icon.Stroke = pinned
                ? (Brush)Application.Current.Resources["ErrorBrush"]
                : (Brush)Application.Current.Resources["SidebarTextBrush"];
        PinButton.ToolTip = pinned ? "常に前面に表示: ON（クリックで解除）" : "常に前面に表示: OFF";
    }

    private void MonitorToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (MonitorService.Instance.QuotaSuspendedUntil.HasValue) return;

        if (MonitorService.Instance.IsRunning)
        {
            AppLogger.Log(LogMsg.MonitorToggleClicked, null, "停止");
            MonitorService.Instance.Stop();
        }
        else
        {
            AppLogger.Log(LogMsg.MonitorToggleClicked, null, "開始");
            LoggerService.Instance.ClearUiLog();
            MonitorService.Instance.Start();
        }
    }

}
