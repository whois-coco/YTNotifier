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
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44))
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

    /// <summary>実際にDNS解決を試みてネットワーク疎通を確認する</summary>
    internal async void CheckNetworkState()
    {
        var isAvailable = false;
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = await ping.SendPingAsync("8.8.8.8", 1000);
            isAvailable = reply.Status == System.Net.NetworkInformation.IPStatus.Success;
        }
        catch { isAvailable = false; }

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
            MonitorService.Instance.Start();
            UpdateMonitorStatus(true);
            AppLogger.Log(LogMsg.NetworkRestored);
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
            [SettingsNavAbout]   = SettingsPageAbout,
        };

        var pageNames = new Dictionary<object, string>
        {
            [SettingsNavApp]     = "動作",
            [SettingsNavDisplay] = "表示",
            [SettingsNavMonitor] = "通知",
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
        }
    }

    // ===== ナビゲーション =====
    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        var pageMap = new Dictionary<object, UIElement>
        {
            [NavWatch]    = PageWatch,
            [NavSettings] = PageSettings,
        };

        if (sender == NavWatch)         AppLogger.Log(LogMsg.NavPageSwitched, null, "チャンネル");
        else if (sender == NavSettings) AppLogger.Log(LogMsg.NavPageSwitched, null, "設定");

        foreach (var (_, page) in pageMap)
            page.Visibility = Visibility.Collapsed;

        if (pageMap.TryGetValue(sender, out var targetPage))
            targetPage.Visibility = Visibility.Visible;

        if (sender == NavSettings)
            SettingsNavBorder_Click(SettingsNavDisplay, null!);

        // セレクターバー切り替え
        SetNavSelectorBar(NavWatch,    sender == NavWatch);
        SetNavSelectorBar(NavSettings, sender == NavSettings);
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

    // ===== チャンネル追加 =====
    private void AddChannelHeader_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AddChannelWindow(onChannelAdded: () =>
        {
            Dispatcher.Invoke(RefreshChannelList);
        })
        { Owner = this };
        dlg.ShowDialog();
    }

    // ===== アクションボタン =====
    private async void ManualCheckButton_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.Log(LogMsg.ManualCheckTriggered);
        InlineCheckButton.IsEnabled = false;
        Nav_Click(NavWatch, e);
        await MonitorService.Instance.ManualCheckAsync();
        RefreshChannelList();
        InlineCheckButton.IsEnabled = true;
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
