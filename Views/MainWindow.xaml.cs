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
    // ===== 定数 =====
    private const int    SidebarExpandedWidth    = 130;
    private const int    SidebarCollapsedWidth   = 44;
    private const int    ContentWidthNormal      = 400;
    private const int    ContentWidthCompact     = 286;
    private const int    CollapsedTotalWidth     = SidebarCollapsedWidth + ContentWidthNormal;  // 424
    private const int    CompactTotalWidth       = SidebarCollapsedWidth + ContentWidthCompact; // 330
    private const string GitHubReleasesPageUrl   = "https://github.com/whois-coco/YTNotifier/releases/latest";

    // ===== フィールド =====
    private Border?    _navWatchUnreadBadge   = null;
    private TextBlock? _navWatchUnreadText    = null;
    private Border?    _navWatchCompactBadge  = null;
    private TextBlock? _navWatchCompactText   = null;
    private bool _sidebarCollapsed      = false;
    private bool _loadingSettings       = false;
    private System.Windows.Threading.DispatcherTimer? _networkCheckTimer;

    // アイコンダウンロード中フラグ（多重ダウンロード防止）。コレクション操作は全て UI スレッドに集約しスレッド安全を担保
    private static readonly HashSet<string> _iconDownloading = new();

    // ミュート
    private bool _isMuted                    = false;
    private bool _preMuteDesktopNotification = false;
    private bool _preMuteNotificationSound   = false;
    private bool _preMuteFlashTaskbar        = false;

    // APIキー
    private string _actualApiKey = "";

    // ナビゲーション状態
    private string     _currentNav = "Watch";

    // MonitorService イベントハンドラ（解除用に保持）
    private Action<bool>? _onStatusChanged;
    private Action?       _onChannelUpdated;
    private Action?       _onQuotaUpdated;
    private Action?       _onNetworkCheckRequested;

    // ===== アイコンキャッシュ =====
    private static BitmapImage? GetCachedIcon(string url, string channelId)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (ImageCacheService.TryGetCachedIcon(url, out var cached)) return cached;

        if (_iconDownloading.Contains(url)) return null;
        _iconDownloading.Add(url);

        Task.Run(async () =>
        {
            var bmp = await ImageCacheService.GetOrDownloadIconAsync(url);
            if (bmp == null)
                AppLogger.Log(LogMsg.IconLoadFailed, null, url);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _iconDownloading.Remove(url);
                if (bmp != null) UpdateIconsInList(url);
            });
        });
        return null;
    }

    private static void UpdateIconsInList(string url)
    {
        if (Application.Current.MainWindow is not MainWindow win) return;
        if (!ImageCacheService.TryGetCachedIcon(url, out var bmp) || bmp == null) return;

        foreach (var list in new[] { win.ChannelList, win.DormantChannelList })
        {
            foreach (var row in list.Children.OfType<Border>())
            {
                if (row.Tag is not ChannelInfo ch || ch.ThumbnailUrl != url) continue;
                if (row.Child is not Grid outer) continue;
                var inner = outer.Children.OfType<Grid>().FirstOrDefault();
                var iconBorder = inner?.Children.OfType<Border>()
                    .FirstOrDefault(b => b.Tag is string s && s == "IconBorder");
                if (iconBorder != null)
                    iconBorder.Child = new System.Windows.Controls.Image { Source = bmp, Stretch = Stretch.UniformToFill };
            }
        }
    }

    // ===== 初期化 =====
    public MainWindow()
    {
        InitializeComponent();
        Loaded       += MainWindow_Loaded;
        Closing      += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
        _onStatusChanged  = isRunning => Dispatcher.Invoke(() => UpdateMonitorStatus(isRunning));
        _onChannelUpdated = ()        => Dispatcher.Invoke(RefreshChannelList);
        _onQuotaUpdated   = ()        => Dispatcher.Invoke(UpdateQuotaInfo);
        _onNetworkCheckRequested = () => Dispatcher.InvokeAsync(() => CheckNetworkState());
        MonitorService.Instance.StatusChanged         += _onStatusChanged;
        MonitorService.Instance.ChannelUpdated        += _onChannelUpdated;
        MonitorService.Instance.QuotaUpdated          += _onQuotaUpdated;
        MonitorService.Instance.NetworkCheckRequested += _onNetworkCheckRequested;
        NotificationService.OpenVideoFromToast = OpenChannelLatestVideoFromToastAsync;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        WindowCornerHelper.Apply(this);
        try { LoadSettings(); }
        catch (Exception ex) { AppLogger.Log(LogMsg.SettingsLoadError, null, ex.Message); }

        UpdateMinWidth();
        RestoreWindowBounds();
        InitChannelListDragDrop();
        InitDormantChannelListDragDrop();

        // ネットワーク状態監視（ポーリング方式・5秒ごと）
        _networkCheckTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _networkCheckTimer.Tick += (_, _) => CheckNetworkState();
        _networkCheckTimer.Start();
        CheckNetworkState(); // 初回即時チェック

        // 開発者ツールDLLが存在する場合のみボタンを表示
        if (MainWindow.IsDebugDllAvailable() &&
            FindName("OpenDebugWindowBtn") is System.Windows.Controls.Button debugBtn)
            debugBtn.Visibility = Visibility.Visible;

        var s = SettingsService.Instance.Settings;
        if (s.IsMuted)
        {
            _isMuted                    = true;
            _preMuteDesktopNotification = s.PreMuteDesktopNotification;
            _preMuteNotificationSound   = s.PreMuteNotificationSound;
            _preMuteFlashTaskbar        = s.PreMuteFlashTaskbar;
        }
        UpdateMuteButton(_isMuted);

        if (s.CompactMode) ApplyCompactMode(true, skipRefresh: true, skipSave: true);
        else               UpdateCompactModeButton(false);

        // コンパクト・非コンパクト共通：SyncWindowWidth で幅を確定しレイアウトを完走させてから
        // RefreshChannelList を呼ぶことで、SCP が再測定され起動直後からスクロールが機能する
        Dispatcher.Invoke(SyncWindowWidth, System.Windows.Threading.DispatcherPriority.Render);
        NavWatch.ApplyTemplate();
        _navWatchUnreadBadge  = NavWatch.Template?.FindName("NavWatchUnreadBadge",  NavWatch) as Border;
        _navWatchUnreadText   = NavWatch.Template?.FindName("NavWatchUnreadText",   NavWatch) as TextBlock;
        _navWatchCompactBadge = NavWatch.Template?.FindName("NavWatchCompactBadge", NavWatch) as Border;
        _navWatchCompactText  = NavWatch.Template?.FindName("NavWatchCompactText",  NavWatch) as TextBlock;
        try { RefreshChannelList(); }
        catch (Exception ex) { AppLogger.Log(LogMsg.ChannelListError, null, ex.Message); }

        // Grid は最初に 0 件の状態で計算され DesiredSize=0 としてキャッシュされる。
        // チャンネル追加後も Grid 自体は dirty にならないため SCP 経由で再計算しても
        // キャッシュヒットで 0 を返し続ける。Grid を明示的に dirty にすることで
        // SCP.MeasureOverride → Grid.MeasureOverride → 正しい高さ の経路を確保する。
        if (ChannelScrollViewer.Content is System.Windows.Controls.Grid contentGrid &&
            ChannelScrollViewer.Template?.FindName("PART_ScrollContentPresenter", ChannelScrollViewer)
                is System.Windows.Controls.ScrollContentPresenter scp)
        {
            contentGrid.InvalidateMeasure();
            scp.InvalidateMeasure();
            ChannelScrollViewer.InvalidateMeasure();
            ChannelScrollViewer.UpdateLayout();
        }

        // 初期ナビセレクターバー（チャンネルがデフォルト選択）
        SetNavSelectorBar(NavWatch,    true);
        SetNavSelectorBar(NavSettings, false);
        InitMonitor();

        // ABOUTページのバージョン表示を設定し、起動時アップデート確認を実行
        if (FindName("AboutVersionText") is TextBlock versionText)
            versionText.Text = $"v{AppConstants.AppVersion}";
        _ = CheckForUpdateAsync();
    }

    private async Task CheckForUpdateAsync()
    {
        var latestTag = await UpdateCheckService.CheckAsync();
        if (latestTag == null) return;

        await Dispatcher.InvokeAsync(() =>
        {
            if (FindName("UpdateBannerText") is TextBlock bannerText)
                bannerText.Text = $"新しいバージョン {latestTag} が公開されています";
            if (FindName("UpdateBanner") is Border banner)
                banner.Visibility = Visibility.Visible;

            if (ConfirmDialog.Show(
                    this,
                    "アップデートがあります",
                    $"新しいバージョン {latestTag} が公開されています。\nリリースページを開きますか？",
                    "開く", "閉じる") == true)
            {
                Process.Start(new ProcessStartInfo(GitHubReleasesPageUrl) { UseShellExecute = true });
            }
        });
    }

    private void UpdateOpenBtn_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(GitHubReleasesPageUrl) { UseShellExecute = true });
    }

    private void InitChannelListDragDrop()
    {
        ChannelList.AllowDrop  = true;
        ChannelList.Background = Brushes.Transparent;
        ChannelList.DragOver  += ChannelList_DragOver;
        ChannelList.Drop      += ChannelList_Drop;
    }

    private void InitDormantChannelListDragDrop()
    {
        DormantChannelList.AllowDrop  = true;
        DormantChannelList.Background = Brushes.Transparent;
        DormantChannelList.DragOver  += DormantChannelList_DragOver;
        DormantChannelList.Drop      += DormantChannelList_Drop;
    }

    private void InitMonitor()
    {
        try
        {
            var initApiKey = SettingsService.Instance.Settings.ApiKey;
            if (!string.IsNullOrEmpty(initApiKey))
                MonitorService.Instance.Start();
            else
            {
                AppLogger.Log(LogMsg.ApiKeyNotSet);
                UpdateMonitorStatus(false);
            }
        }
        catch (Exception ex) { AppLogger.Log(LogMsg.MonitorStartError, null, ex.Message); }
    }

    // ===== ウィンドウ管理 =====
    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        ((Border)Content).Margin = WindowState == System.Windows.WindowState.Maximized
            ? new Thickness(6) : new Thickness(0);
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (WindowState != System.Windows.WindowState.Minimized)
            SaveWindowBounds();

        if (SettingsService.Instance.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            AppLogger.Log(LogMsg.WindowToTray);
        }
        else
        {
            MonitorService.Instance.StatusChanged         -= _onStatusChanged;
            MonitorService.Instance.ChannelUpdated        -= _onChannelUpdated;
            MonitorService.Instance.QuotaUpdated          -= _onQuotaUpdated;
            MonitorService.Instance.NetworkCheckRequested -= _onNetworkCheckRequested;
            MonitorService.Instance.Stop();
            Application.Current.Shutdown();
        }
    }

    private void RestoreWindowBounds()
    {
        var s = SettingsService.Instance.Settings;
        Width  = s.WindowWidth  > 0 ? s.WindowWidth  : CollapsedTotalWidth;
        Height = s.WindowHeight > 0 ? s.WindowHeight : Height;

        if (s.WindowLeft >= 0 && s.WindowTop >= 0 &&
            s.WindowLeft + Width  <= SystemParameters.VirtualScreenWidth  + 100 &&
            s.WindowTop  + Height <= SystemParameters.VirtualScreenHeight + 100)
        {
            Left = s.WindowLeft;
            Top  = s.WindowTop;
        }

        if (s.WindowMaximized)
            WindowState = System.Windows.WindowState.Maximized;
    }

    private void SaveWindowBounds()
    {
        var s = SettingsService.Instance.Settings;
        s.WindowMaximized = WindowState == System.Windows.WindowState.Maximized;
        var b = s.WindowMaximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        if (b.Width > 0 && b.Height > 0)
        {
            s.WindowWidth = b.Width; s.WindowHeight = b.Height;
            s.WindowLeft  = b.Left;  s.WindowTop    = b.Top;
        }
        SettingsService.Instance.SaveSettingsSilent();
    }

    // ===== 設定読み込み =====
    private void LoadSettings()
    {
        var s = SettingsService.Instance.Settings;
        InitApiKeySlotComboBox();
        InitNotificationSoundSetComboBox();
        _actualApiKey                     = s.ApiKey;
        ApiKeyBox.Text                    = _actualApiKey;
        UpdateApiKeyState(!string.IsNullOrEmpty(_actualApiKey));
        _loadingSettings = true;
        DarkModeToggle.IsChecked               = s.IsDarkMode;
        NoCategoryModeToggle.IsChecked         = s.NoCategoryMode;
        NotificationToggle.IsChecked           = s.ShowDesktopNotification;
        TrayToggle.IsChecked                   = s.MinimizeToTray;
        StartupToggle.IsChecked                = s.StartWithWindows;
        AlwaysOnTopToggle.IsChecked            = s.AlwaysOnTop;
        Topmost                                = s.AlwaysOnTop;
        NotificationSoundToggle.IsChecked      = s.NotificationSound;
        FlashTaskbarToggle.IsChecked           = s.FlashTaskbar;
        CompactModeToggle.IsChecked            = s.CompactMode;
        // 通知スタイル（_loadingSettings内で設定しないとSelectionChangedで上書きされる）
        foreach (ComboBoxItem item in ToastStyleComboBox.Items)
            if (item.Tag?.ToString() == s.ToastStyle.ToString())
            { ToastStyleComboBox.SelectedItem = item; break; }
        if (ToastStyleComboBox.SelectedItem == null) ToastStyleComboBox.SelectedIndex = 0;
        ToastStyleComboBox.IsEnabled           = s.ShowDesktopNotification;
        ToastStyleComboBox.Opacity             = s.ShowDesktopNotification ? 1.0 : AppConstants.DisabledControlOpacity;
        NotificationSoundSetComboBox.IsEnabled = s.NotificationSound;
        NotificationSoundSetComboBox.Opacity   = s.NotificationSound ? 1.0 : AppConstants.DisabledControlOpacity;
        _loadingSettings = false;
        UpdatePinButton(s.AlwaysOnTop);

        _loadingSettings = true;
        var items = IntervalComboBox.Items.Cast<ComboBoxItem>().ToList();
        IntervalComboBox.SelectedItem = items.FirstOrDefault(
            i => i.Tag?.ToString() == s.CheckIntervalMinutes.ToString()) ?? items[1];
        _loadingSettings = false;

        // サイドバー状態を設定から復元
        if (s.SidebarCollapsed)
        {
            CollapseSidebar(skipSave: true);
        }
        else
        {
            ExpandSidebar(skipSave: true);
        }
        UpdateMinWidth();
        SyncWindowWidth();

        UpdateQuotaInfo();

        _loadingSettings = true;
        AutoCleanLogsToggle.IsChecked = s.AutoCleanLogs;
        TraceLogToggle.IsChecked = s.TraceLogEnabled;
        _loadingSettings = false;
        _loadingSettings = true;
        var retItems = LogRetentionComboBox.Items.Cast<ComboBoxItem>().ToList();
        LogRetentionComboBox.SelectedItem =
            retItems.FirstOrDefault(i => i.Tag?.ToString() == s.LogRetentionDays.ToString())
            ?? retItems[2];
        _loadingSettings = false;
        RefreshLogStats();

        if (s.AutoCleanLogs && s.LogRetentionDays != -1)
        {
            var (deleted, _) = LoggerService.Instance.CleanOldLogs(s.LogRetentionDays);
            if (deleted > 0) AppLogger.Log(LogMsg.AutoLogDeleted, null, deleted);
        }
    }

    // ===== 汎用ヘルパー =====
    private static void SetDynamicBrush(FrameworkElement el, DependencyProperty dp, string key)
        => el.SetResourceReference(dp, key);

    private static string KindLabel(VideoKind kind) => kind switch
    {
        VideoKind.Short    => "Short",
        VideoKind.Live     => "ライブ",
        VideoKind.Premiere => "プレミア",
        _               => "動画"
    };

    private static IEnumerable<T> FindVisualChildren<T>(System.Windows.DependencyObject parent)
        where T : System.Windows.DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) yield return t;
            foreach (var sub in FindVisualChildren<T>(child)) yield return sub;
        }
    }

}
