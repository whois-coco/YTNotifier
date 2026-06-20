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
    private const int    SidebarExpandedWidth    = 110;
    private const int    SidebarCollapsedWidth   = 44;
    private const int    ChannelRowHeight        = 60;
    private const int    ChannelRowHeightCompact    = 36;
    private const int    CategoryRowHeight          = 36;
    private const int    CategoryRowHeightCompact   = 24;
    private const double ChannelRowMarginBottom  = 2;
    private const int    ContentWidthNormal      = 360;
    private const int    ContentWidthCompact     = 286;
    private const int    ExpandedTotalWidth      = SidebarExpandedWidth  + ContentWidthNormal;  // 470
    private const int    CollapsedTotalWidth     = SidebarCollapsedWidth + ContentWidthNormal;  // 404
    private const int    CompactTotalWidth       = SidebarCollapsedWidth + ContentWidthCompact; // 340
    private const int    WindowMinWidth          = CompactTotalWidth; // コンパクト幅を下限とする
    private const int    WindowMinHeight         = 500;

    // Short: Lucide zap
    private const string ShortIconPath =
        "M4 14a1 1 0 0 1-.78-1.63l9.9-10.2a.5.5 0 0 1 .86.46l-1.92 6.02A1 1 0 0 0 13 10h7a1 1 0 0 1 .78 1.63l-9.9 10.2a.5.5 0 0 1-.86-.46l1.92-6.02A1 1 0 0 0 11 14z";

    // ===== フィールド =====
    private bool _sidebarCollapsed      = false;
    private bool _loadingSettings       = false;
    private bool _isOffline             = false;
    private System.Windows.Threading.DispatcherTimer? _networkCheckTimer;
    internal bool _editMode             = false;
    private bool _uncategorizedCollapsed = false;

    // アイコンキャッシュ（URL → BitmapImage）。コレクション操作は全て UI スレッドに集約しスレッド安全を担保
    private const int IconCacheMaxEntries = 300;
    private static readonly Dictionary<string, BitmapImage> _iconCache      = new();
    private static readonly HashSet<string>                  _iconDownloading = new();
    // HttpClient はソケット枯渇を避けるため使い回す
    private static readonly System.Net.Http.HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    // D&D
    private ChannelInfo?  _dragSource      = null;
    private bool          _isDragging      = false;
    private Border?       _dropIndicator   = null;
    private int           _dropIndicatorIndex = -1;
    private List<(double y, int childIndex)> _catBoundaries = new();

    // D&Dアニメーション
    private int     _animDropIndex  = -1;
    private Border? _dragSourceRow  = null;

    // ミュート
    private bool _isMuted                    = false;
    private bool _preMuteDesktopNotification = false;
    private bool _preMuteNotificationSound   = false;
    private bool _preMuteFlashTaskbar        = false;

    // コンパクトモード
    private bool   _preCompactSidebarCollapsed = false;
    private bool   _applyingCompactMode        = false;

    // APIキー
    private string _actualApiKey = "";
    // 検索
    private string _searchQuery = "";

    // MonitorService イベントハンドラ（解除用に保持）
    private Action<bool>? _onStatusChanged;
    private Action?       _onChannelUpdated;
    private Action?       _onQuotaUpdated;


    // Win32
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION        = 2;

    // ===== アイコンキャッシュ =====
    private static string GetIconCacheDir() =>
        System.IO.Path.Combine(SettingsService.Instance.AppDataDir, AppConstants.DirIcons);

    private static string GetDiskPath(string url)
    {
        using var sha1 = System.Security.Cryptography.SHA1.Create();
        var hash = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url));
        return System.IO.Path.Combine(
            GetIconCacheDir(),
            BitConverter.ToString(hash).Replace("-", "").ToLower() + ".png");
    }

    private static BitmapImage? LoadBitmapFromFile(string filePath)
    {
        try
        {
            using var stream = System.IO.File.OpenRead(filePath);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = stream;
            bmp.CacheOption  = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static void AddToIconCache(string url, BitmapImage bmp)
    {
        if (_iconCache.Count >= IconCacheMaxEntries)
            _iconCache.Remove(_iconCache.Keys.First());
        _iconCache[url] = bmp;
    }

    private static BitmapImage? GetCachedIcon(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (_iconCache.TryGetValue(url, out var cached)) return cached;

        var cacheDir = GetIconCacheDir();
        try { System.IO.Directory.CreateDirectory(cacheDir); }
        catch { return null; }

        var diskPath = GetDiskPath(url);
        if (System.IO.File.Exists(diskPath))
        {
            var bmp = LoadBitmapFromFile(diskPath);
            if (bmp != null) { AddToIconCache(url, bmp); return bmp; }
        }

        if (_iconDownloading.Contains(url)) return null;
        _iconDownloading.Add(url);

        Task.Run(async () =>
        {
            try
            {
                var bytes = await _httpClient.GetByteArrayAsync(url);
                await System.IO.File.WriteAllBytesAsync(diskPath, bytes);

                var bmp = LoadBitmapFromFile(diskPath);
                if (bmp == null)
                {
                    AppLogger.Log(LogMsg.IconLoadFailed, null, diskPath);
                    await Application.Current.Dispatcher.InvokeAsync(() => _iconDownloading.Remove(url));
                    return;
                }
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    AddToIconCache(url, bmp);
                    _iconDownloading.Remove(url);
                    UpdateIconsInList(url);
                });
            }
            catch (Exception ex)
            {
                AppLogger.Log(LogMsg.IconDownloadFailed, null, ex.Message);
                await Application.Current.Dispatcher.InvokeAsync(() => _iconDownloading.Remove(url));
            }
        });
        return null;
    }

    private static void UpdateIconsInList(string url)
    {
        if (Application.Current.MainWindow is not MainWindow win) return;
        if (!_iconCache.TryGetValue(url, out var bmp)) return;

        foreach (var row in win.ChannelList.Children.OfType<Border>())
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
        MonitorService.Instance.StatusChanged  += _onStatusChanged;
        MonitorService.Instance.ChannelUpdated += _onChannelUpdated;
        MonitorService.Instance.QuotaUpdated   += _onQuotaUpdated;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try { LoadSettings(); }
        catch (Exception ex) { AppLogger.Log(LogMsg.SettingsLoadError, null, ex.Message); }

        UpdateMinWidth();
        RestoreWindowBounds();
        InitChannelListDragDrop();

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

        if (s.CompactMode) ApplyCompactMode(true, skipRefresh: true);
        else               UpdateCompactModeButton(false);

        // コンパクト・非コンパクト共通：SyncWindowWidth で幅を確定しレイアウトを完走させてから
        // RefreshChannelList を呼ぶことで、SCP が再測定され起動直後からスクロールが機能する
        Dispatcher.Invoke(SyncWindowWidth, System.Windows.Threading.DispatcherPriority.Render);
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
    }

    private void InitChannelListDragDrop()
    {
        ChannelList.AllowDrop  = true;
        ChannelList.Background = Brushes.Transparent;
        ChannelList.DragOver  += ChannelList_DragOver;
        ChannelList.Drop      += ChannelList_Drop;
    }

    private void InitMonitor()
    {
        try
        {
            if (!string.IsNullOrEmpty(SettingsService.Instance.Settings.ApiKey))
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
            MonitorService.Instance.StatusChanged  -= _onStatusChanged;
            MonitorService.Instance.ChannelUpdated -= _onChannelUpdated;
            MonitorService.Instance.QuotaUpdated   -= _onQuotaUpdated;
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
        _actualApiKey                     = s.ApiKey;
        ApiKeyBox.Text                    = s.ApiKey;
        UpdateApiKeyState(!string.IsNullOrEmpty(s.ApiKey));
        _loadingSettings = true;
        DarkModeToggle.IsChecked               = s.IsDarkMode;
        NoCategoryModeToggle.IsChecked         = s.NoCategoryMode;
        NotificationToggle.IsChecked           = s.ShowDesktopNotification;
        GlobalNotifyUpcomingToggle.IsChecked   = s.GlobalNotifyUpcoming;
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
            CollapseSidebar();
        }
        else
        {
            ExpandSidebar();
        }
        UpdateMinWidth();
        SyncWindowWidth();

        UpdateQuotaInfo();

        AutoCleanLogsToggle.IsChecked = s.AutoCleanLogs;
        _loadingSettings = true;
        var lvlItems = LogLevelComboBox.Items.Cast<ComboBoxItem>().ToList();
        LogLevelComboBox.SelectedItem =
            lvlItems.FirstOrDefault(i => i.Tag?.ToString() == s.LogLevel) ?? lvlItems[0];
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
