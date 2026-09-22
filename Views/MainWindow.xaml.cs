using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private const int    ContentWidthNormal      = 385;
    private const int    ContentWidthCompact     = 286;
    private const int    CollapsedTotalWidth     = SidebarCollapsedWidth + ContentWidthNormal;  // 429
    private const int    CompactTotalWidth       = SidebarCollapsedWidth + ContentWidthCompact; // 330

    /// <summary>ネットワーク状態のポーリング間隔（秒）</summary>
    private const int NetworkCheckIntervalSeconds = 5;

    /// <summary>最大化時にウィンドウコンテンツへ付与する余白（画面端の切れ防止）</summary>
    private static readonly Thickness WindowContentPadding = new(6);

    /// <summary>アップデートダウンロード中の回転リングが1周するのにかかる秒数</summary>
    private const double UpdateDownloadSpinnerDurationSeconds = 1.0;

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

    // ナビゲーション状態
    private string     _currentNav = "Watch";

    // 自己アップデート
    private string? _pendingUpdateTag = null;

    // MonitorService イベントハンドラ（解除用に保持）
    private Action<bool>? _onStatusChanged;
    private Action?       _onChannelUpdated;
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
        _onNetworkCheckRequested = () => Dispatcher.InvokeAsync(() => CheckNetworkState());
        MonitorService.Instance.StatusChanged         += _onStatusChanged;
        MonitorService.Instance.ChannelUpdated        += _onChannelUpdated;
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
            Interval = TimeSpan.FromSeconds(NetworkCheckIntervalSeconds)
        };
        _networkCheckTimer.Tick += (_, _) => CheckNetworkState();
        _networkCheckTimer.Start();
        CheckNetworkState(); // 初回即時チェック

        // 開発者ツールDLLが存在する場合のみサイドバー項目を表示
        if (MainWindow.IsDebugDllAvailable())
            NavDebugTools.Visibility = Visibility.Visible;

        var settings = SettingsService.Instance.Settings;
        if (settings.IsMuted)
        {
            _isMuted                    = true;
            _preMuteDesktopNotification = settings.PreMuteDesktopNotification;
            _preMuteNotificationSound   = settings.PreMuteNotificationSound;
            _preMuteFlashTaskbar        = settings.PreMuteFlashTaskbar;
        }
        UpdateMuteButton(_isMuted);

        if (settings.CompactMode) ApplyCompactMode(true, skipRefresh: true, skipSave: true);
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
                is System.Windows.Controls.ScrollContentPresenter scrollContentPresenter)
        {
            contentGrid.InvalidateMeasure();
            scrollContentPresenter.InvalidateMeasure();
            ChannelScrollViewer.InvalidateMeasure();
            ChannelScrollViewer.UpdateLayout();
        }

        // 初期ナビセレクターバー（チャンネルがデフォルト選択）
        SetNavSelectorBar(NavWatch,    true);
        SetNavSelectorBar(NavSettings, false);
        UpdateTitleBar(NavWatch, logSwitch: false);
        InitMonitor();

        // 起動時アップデート確認を実行
        _ = CheckForUpdateAsync();
        _ = CheckPostUpdateReleaseNotesAsync();
    }

    private const int UpdateNotifyAutoHideSeconds  = 5;
    private const int UpdateNotifyFadeMilliseconds = 400;

    private async Task CheckForUpdateAsync()
    {
        var latestTag = await UpdateCheckService.CheckAsync();
        if (latestTag == null) return;

        _pendingUpdateTag = latestTag;
        await Dispatcher.InvokeAsync(() =>
        {
            if (FindName("UpdateAvailableBtn") is Button btn)
                btn.Visibility = Visibility.Visible;
            ShowUpdateNotifyBalloon(latestTag);
        });
    }

    private void ShowUpdateNotifyBalloon(string tag)
    {
        if (FindName("UpdateNotifyText") is not TextBlock text) return;
        if (FindName("UpdateNotifyPopup") is not Popup popup) return;
        if (FindName("UpdateNotifyBalloon") is not Border balloon) return;

        text.Text = $"新しいバージョン {tag} が利用可能です";
        balloon.Opacity = 1;
        popup.IsOpen = true;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(UpdateNotifyAutoHideSeconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            FadeOutUpdateNotifyBalloon();
        };
        timer.Start();
    }

    private void FadeOutUpdateNotifyBalloon()
    {
        if (FindName("UpdateNotifyBalloon") is not Border balloon) return;
        if (FindName("UpdateNotifyPopup") is not Popup popup) return;

        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(UpdateNotifyFadeMilliseconds));
        fadeOut.Completed += (_, _) => popup.IsOpen = false;
        balloon.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private void UpdateNotifyCloseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (FindName("UpdateNotifyPopup") is Popup popup)
            popup.IsOpen = false;
    }

    private async Task CheckPostUpdateReleaseNotesAsync()
    {
        var settings = SettingsService.Instance.Settings;
        var lastSeen = settings.LastSeenAppVersion;
        var current  = AppConstants.AppVersion;

        if (!string.IsNullOrEmpty(lastSeen) && UpdateCheckService.IsNewerVersion(current, lastSeen))
        {
            if (ConfirmDialog.Show(this, "アップデート完了",
                    $"バージョン {current} にアップデートしました。リリースノートを確認しますか？", "確認する") == true)
            {
                var notes = await UpdateCheckService.GetLatestReleaseNotesAsync();
                if (notes != null && !string.IsNullOrEmpty(notes.Body))
                    new ReleaseNotesWindow(this, notes.Tag, notes.Body).Show();
                else
                    System.Windows.MessageBox.Show(this, "リリースノートの取得に失敗しました。",
                        "アップデート", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        settings.LastSeenAppVersion = current;
        SettingsService.Instance.SaveSettingsSilent();
    }

    private async void UpdateAvailableBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        btn.IsEnabled = false;
        try
        {
            var assetInfo = await UpdateCheckService.GetLatestAssetAsync();
            if (assetInfo == null)
            {
                System.Windows.MessageBox.Show(this, "更新情報の取得に失敗しました。しばらくしてから再度お試しください。",
                    "アップデート", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string? verifiedPath;
            StartUpdateDownloadSpinner();
            try
            {
                verifiedPath = await SelfUpdateService.DownloadAndVerifyAsync(assetInfo);
            }
            finally
            {
                StopUpdateDownloadSpinner();
            }
            if (verifiedPath == null)
            {
                System.Windows.MessageBox.Show(this, "ダウンロードまたは検証に失敗しました。しばらくしてから再度お試しください。",
                    "アップデート", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SelfUpdateService.ApplyAndRestart(verifiedPath); // 成功時はここでプロセスが終了する
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"アップデートの適用に失敗しました。\n{ex.Message}",
                "アップデート", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    private void StartUpdateDownloadSpinner()
    {
        UpdateDownloadSpinner.Visibility = Visibility.Visible;
        var rotateAnimation = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(UpdateDownloadSpinnerDurationSeconds))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        UpdateDownloadSpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, rotateAnimation);
    }

    private void StopUpdateDownloadSpinner()
    {
        UpdateDownloadSpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        UpdateDownloadSpinner.Visibility = Visibility.Collapsed;
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
            ? WindowContentPadding : new Thickness(0);
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
            MonitorService.Instance.NetworkCheckRequested -= _onNetworkCheckRequested;
            MonitorService.Instance.Stop();
            Application.Current.Shutdown();
        }
    }

    private void RestoreWindowBounds()
    {
        var settings = SettingsService.Instance.Settings;
        Width  = settings.WindowWidth  > 0 ? settings.WindowWidth  : CollapsedTotalWidth;
        Height = settings.WindowHeight > 0 ? settings.WindowHeight : Height;

        if (settings.WindowLeft >= 0 && settings.WindowTop >= 0 &&
            settings.WindowLeft + Width  <= SystemParameters.VirtualScreenWidth  + 100 &&
            settings.WindowTop  + Height <= SystemParameters.VirtualScreenHeight + 100)
        {
            Left = settings.WindowLeft;
            Top  = settings.WindowTop;
        }
    }

    private void SaveWindowBounds()
    {
        var settings = SettingsService.Instance.Settings;
        var windowBounds = new Rect(Left, Top, Width, Height);
        if (windowBounds.Width > 0 && windowBounds.Height > 0)
        {
            settings.WindowWidth = windowBounds.Width; settings.WindowHeight = windowBounds.Height;
            settings.WindowLeft  = windowBounds.Left;  settings.WindowTop    = windowBounds.Top;
        }
        SettingsService.Instance.SaveSettingsSilent();
    }

    // ===== 設定読み込み =====
    private void LoadSettings()
    {
        var settings = SettingsService.Instance.Settings;

        // サイドバー状態を設定から復元
        if (settings.SidebarCollapsed)
        {
            CollapseSidebar(skipSave: true);
        }
        else
        {
            ExpandSidebar(skipSave: true);
        }
        UpdateMinWidth();
        SyncWindowWidth();

        _loadingSettings = true;
        Topmost                     = settings.AlwaysOnTop;
        AlwaysOnTopToggle.IsChecked = settings.AlwaysOnTop;
        CompactModeToggle.IsChecked = settings.CompactMode;
        _loadingSettings = false;
        UpdatePinButton(settings.AlwaysOnTop);

        // ログ自動削除の起動時処理（UIとは無関係な起動時メンテナンス。設定ウィンドウの有無に関わらず必ず実行する）
        if (settings.AutoCleanLogs && settings.LogRetentionDays != -1)
        {
            var (deleted, _) = LoggerService.Instance.CleanOldLogs(settings.LogRetentionDays);
            if (deleted > 0) AppLogger.Log(LogMsg.AutoLogDeleted, null, deleted);
        }
    }

    // ===== 汎用ヘルパー =====
    internal static void SetDynamicBrush(FrameworkElement targetElement, DependencyProperty targetProperty, string key)
        => targetElement.SetResourceReference(targetProperty, key);

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
        for (int childIndex = 0; childIndex < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); childIndex++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, childIndex);
            if (child is T typedChild) yield return typedChild;
            foreach (var sub in FindVisualChildren<T>(child)) yield return sub;
        }
    }

}
