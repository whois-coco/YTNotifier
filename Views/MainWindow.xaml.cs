using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using YTNotifier.Models;
using YTNotifier.Services;
using Application         = System.Windows.Application;
using Brush               = System.Windows.Media.Brush;
using Brushes             = System.Windows.Media.Brushes;
using Button              = System.Windows.Controls.Button;
using Cursors             = System.Windows.Input.Cursors;
using Cursor              = System.Windows.Input.Cursor;
using DataObject          = System.Windows.DataObject;
using DragDropEffects     = System.Windows.DragDropEffects;
using DragEventArgs       = System.Windows.DragEventArgs;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs        = System.Windows.Input.KeyEventArgs;
using MessageBox          = System.Windows.MessageBox;
using MouseEventArgs      = System.Windows.Input.MouseEventArgs;
using Orientation         = System.Windows.Controls.Orientation;
using Path                = System.IO.Path;
using Rectangle           = System.Windows.Shapes.Rectangle;
using Size                = System.Windows.Size;
using TextBox             = System.Windows.Controls.TextBox;
using VerticalAlignment   = System.Windows.VerticalAlignment;

namespace YTNotifier.Views;

// ドラッグプレビュー用 Adorner
public class DragAdorner : Adorner
{
    private readonly UIElement _child;
    private double _offsetX, _offsetY;

    public DragAdorner(UIElement adornedElement, UIElement child, double offsetX, double offsetY)
        : base(adornedElement)
    {
        _child = child; _offsetX = offsetX; _offsetY = offsetY;
        IsHitTestVisible = false;
        AdornerLayer.GetAdornerLayer(adornedElement)?.Add(this);
    }

    public void UpdatePosition(double x, double y)
    {
        _offsetX = x; _offsetY = y;
        InvalidateArrange();
    }

    public void Detach() =>
        AdornerLayer.GetAdornerLayer(AdornedElement)?.Remove(this);

    protected override Size MeasureOverride(Size constraint)
    {
        _child.Measure(constraint);
        return _child.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _child.Arrange(new Rect(finalSize));
        return finalSize;
    }

    public override GeneralTransform GetDesiredTransform(GeneralTransform transform)
    {
        var g = new GeneralTransformGroup();
        g.Children.Add(base.GetDesiredTransform(transform));
        g.Children.Add(new TranslateTransform(_offsetX, _offsetY));
        return g;
    }

    protected override Visual GetVisualChild(int index) => _child;
    protected override int VisualChildrenCount => 1;
}

public partial class MainWindow : Window
{
    // ===== 定数 =====
    private const int    SidebarExpandedWidth  = 140;
    private const int    SidebarCollapsedWidth = 44;
    private const int    ChannelRowHeight      = 60;
    private const int    ChannelRowHeightCompact = 36;
    private const double ChannelRowMarginBottom = 2;

    // ===== フィールド =====
    private bool _sidebarCollapsed   = false;

    // 編集モード
    internal bool _editMode = false;

    // アイコンキャッシュ（URL → BitmapImage）
    private static readonly Dictionary<string, BitmapImage> _iconCache     = new();
    private static readonly HashSet<string>                  _iconDownloading = new();

    private static string GetIconCacheDir() =>
        System.IO.Path.Combine(SettingsService.Instance.AppDataDir, "icons");

    private static string GetDiskPath(string url)
    {
        using var sha1 = System.Security.Cryptography.SHA1.Create();
        var hash = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url));
        return System.IO.Path.Combine(
            GetIconCacheDir(),
            BitConverter.ToString(hash).Replace("-", "").ToLower() + ".png");
    }

    // ファイルストリームで読み込み（Windowsパスの URI 変換問題を回避）
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

    private static BitmapImage? GetCachedIcon(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;

        // 1. メモリキャッシュ
        if (_iconCache.TryGetValue(url, out var cached)) return cached;

        // キャッシュディレクトリを確実に作成
        var cacheDir = GetIconCacheDir();
        try { System.IO.Directory.CreateDirectory(cacheDir); }
        catch { return null; }

        var diskPath = GetDiskPath(url);

        // 2. ディスクキャッシュ
        if (System.IO.File.Exists(diskPath))
        {
            var bmp = LoadBitmapFromFile(diskPath);
            if (bmp != null)
            {
                _iconCache[url] = bmp;
                return bmp;
            }
        }

        // 3. バックグラウンドでダウンロード（重複防止）
        if (_iconDownloading.Contains(url)) return null;
        _iconDownloading.Add(url);

        Task.Run(async () =>
        {
            try
            {
                using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var bytes = await client.GetByteArrayAsync(url);
                await System.IO.File.WriteAllBytesAsync(diskPath, bytes);

                var bmp = LoadBitmapFromFile(diskPath);
                if (bmp == null)
                {
                    LoggerService.Instance.Warning($"アイコン読込失敗: {diskPath}");
                    await Application.Current.Dispatcher.InvokeAsync(() => _iconDownloading.Remove(url));
                    return;
                }

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    _iconCache[url] = bmp;
                    _iconDownloading.Remove(url);
                    UpdateIconsInList(url);
                });
            }
            catch (Exception ex)
            {
                LoggerService.Instance.Warning($"アイコンDL失敗: {ex.Message}");
                await Application.Current.Dispatcher.InvokeAsync(
                    () => _iconDownloading.Remove(url));
            }
        });

        return null;
    }

    // ダウンロード完了後、該当チャンネルのアイコンのみ差し替え
    private static void UpdateIconsInList(string url)
    {
        var win = Application.Current.MainWindow as MainWindow;
        if (win == null) return;
        if (!_iconCache.TryGetValue(url, out var bmp)) return;

        foreach (var row in win.ChannelList.Children.OfType<Border>())
        {
            if (row.Tag is not ChannelInfo ch || ch.ThumbnailUrl != url) continue;
            if (row.Child is not Grid outer) continue;
            var inner = outer.Children.OfType<Grid>().FirstOrDefault();
            if (inner == null) continue;
            var iconBorder = inner.Children.OfType<Border>()
                .FirstOrDefault(b => Grid.GetColumn(b) == 0);
            if (iconBorder == null) continue;
            iconBorder.Child = new System.Windows.Controls.Image
                { Source = bmp, Stretch = Stretch.UniformToFill };
        }
    }

    // ドロップ位置インジケーター
    private Border? _dropIndicator = null;
    private int     _dropIndicatorIndex = -1;

    // D&D
    private ChannelInfo?  _dragSource      = null;
    private int           _dragSourceIndex = -1;
    private DragAdorner?  _dragAdorner     = null;
    private System.Windows.Point _dragStartPoint;
    private bool          _isDragging      = false;

    // Win32
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION        = 2;

    // ===== 初期化 =====
    public MainWindow()
    {
        InitializeComponent();
        Loaded       += MainWindow_Loaded;
        Closing      += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
        MonitorService.Instance.StatusChanged  += isRunning => Dispatcher.Invoke(() => UpdateMonitorStatus(isRunning));
        MonitorService.Instance.ChannelUpdated += ()        => Dispatcher.Invoke(RefreshChannelList);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try { LoadSettings(); }
        catch (Exception ex) { LoggerService.Instance.Error($"設定読込エラー: {ex.Message}"); }

        try { RefreshChannelList(); }
        catch (Exception ex) { LoggerService.Instance.Error($"チャンネルリストエラー: {ex.Message}"); }

        UpdateMinWidth();
        RestoreWindowBounds();
        InitChannelListDragDrop();
        InitLogBindings();
        InitMonitor();
    }

    private void InitChannelListDragDrop()
    {
        ChannelList.AllowDrop  = true;
        ChannelList.Background = Brushes.Transparent;
        ChannelList.DragOver  += ChannelList_DragOver;
        ChannelList.Drop      += ChannelList_Drop;
    }

    private void InitLogBindings()
    {
        LoggerService.Instance.ClearUiLog();
        LogList.ItemsSource      = LoggerService.Instance.Entries;
    }

    private void InitMonitor()
    {
        try
        {
            if (!string.IsNullOrEmpty(SettingsService.Instance.Settings.ApiKey))
                MonitorService.Instance.Start();
            else
            {
                LoggerService.Instance.Warning("APIキーが未設定です。設定タブからAPIキーを入力してください。");
                UpdateMonitorStatus(false);
            }
        }
        catch (Exception ex) { LoggerService.Instance.Error($"監視開始エラー: {ex.Message}"); }
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
            LoggerService.Instance.Info("ウィンドウをトレイに格納しました");
        }
        else
        {
            MonitorService.Instance.Stop();
            Application.Current.Shutdown();
        }
    }

    private void RestoreWindowBounds()
    {
        var s = SettingsService.Instance.Settings;

        if (s.WindowWidth > 0)
        {
            Width  = s.WindowWidth;
            Height = s.WindowHeight > 0 ? s.WindowHeight : Height;
        }
        else
        {
            // config.json が存在しない初回起動: 横幅のみ最小値で起動
            Width = 405;
        }

        if (s.WindowLeft >= 0 && s.WindowTop >= 0)
        {
            if (s.WindowLeft + Width  <= SystemParameters.VirtualScreenWidth  + 100 &&
                s.WindowTop  + Height <= SystemParameters.VirtualScreenHeight + 100)
            {
                Left = s.WindowLeft;
                Top  = s.WindowTop;
            }
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
        SettingsService.Instance.SaveSettings();
    }

    // ===== 設定読み込み =====
    private void LoadSettings()
    {
        var s = SettingsService.Instance.Settings;
        _actualApiKey                 = s.ApiKey;
        ApiKeyBox.Text                = s.ApiKey;
        UpdateApiKeyState(!string.IsNullOrEmpty(s.ApiKey));
        DarkModeToggle.IsChecked      = s.IsDarkMode;
        NotificationToggle.IsChecked  = s.ShowDesktopNotification;
        TrayToggle.IsChecked          = s.MinimizeToTray;
        StartupToggle.IsChecked       = s.StartWithWindows;
        AlwaysOnTopToggle.IsChecked       = s.AlwaysOnTop;
        Topmost                           = s.AlwaysOnTop;
        NotificationSoundToggle.IsChecked = s.NotificationSound;
        CompactModeToggle.IsChecked        = s.CompactMode;
        UpdatePinButton(s.AlwaysOnTop);

        var items = IntervalComboBox.Items.Cast<ComboBoxItem>().ToList();
        IntervalComboBox.SelectedItem = items.FirstOrDefault(
            i => i.Tag?.ToString() == s.CheckIntervalMinutes.ToString()) ?? items[1];

        if (s.SidebarCollapsed)
        {
            _sidebarCollapsed = false;
            SidebarToggle_Click(this, new RoutedEventArgs());
        }


        // クォータ情報を表示
        UpdateQuotaInfo();

        // ログメンテナンス設定
        AutoCleanLogsToggle.IsChecked = s.AutoCleanLogs;
        var retItems = LogRetentionComboBox.Items.Cast<ComboBoxItem>().ToList();
        LogRetentionComboBox.SelectedItem =
            retItems.FirstOrDefault(i => i.Tag?.ToString() == s.LogRetentionDays.ToString())
            ?? retItems[2]; // デフォルト3日
        RefreshLogStats();

        // 自動削除（0日=当日のみ、-1=無制限は除く）
        if (s.AutoCleanLogs && s.LogRetentionDays != -1)
        {
            var (deleted, _) = LoggerService.Instance.CleanOldLogs(s.LogRetentionDays);
            if (deleted > 0)
                LoggerService.Instance.Info($"起動時ログ自動削除: {deleted}件");
        }
    }

    // ===== ヘルパー =====
    private static void SetDynamicBrush(FrameworkElement el, DependencyProperty dp, string key)
        => el.SetResourceReference(dp, key);

    private static string KindLabel(VideoKind kind) => kind switch
    {
        VideoKind.Short => "Short",
        VideoKind.Live  => "ライブ",
        _               => "動画"
    };

    // ===== 編集モード =====
    private void EditModeButton_Click(object sender, RoutedEventArgs e)
    {
        _editMode = !_editMode;

        EditModeButton.Content = _editMode ? "✅" : "✏";
        if (_editMode)
        {
            EditModeButton.Style = (Style)Application.Current.Resources["PrimaryButton"];
        }
        else
        {
            EditModeButton.Style = (Style)Application.Current.Resources["SecondaryButton"];
        }

        // 全チャンネル行のカーソル・削除ボタン・アイコン・種別トグルの有効状態を切替
        foreach (var child in ChannelList.Children.OfType<Border>())
        {
            child.Cursor = _editMode ? Cursors.Hand : Cursors.Arrow;
            SetRowInteractive(child, !_editMode);
            SetDeleteButtonVisibility(child, _editMode);
        }

        if (_editMode)
            LoggerService.Instance.Info("編集モード開始：ドラッグでチャンネルを並び替えられます");
        else
            LoggerService.Instance.Info("編集モード終了");
    }

    private static void SetDeleteButtonVisibility(Border row, bool visible)
    {
        if (row.Child is not Grid outerGrid) return;
        var contentGrid = outerGrid.Children.OfType<Grid>().FirstOrDefault();
        if (contentGrid == null) return;
        foreach (var panel in contentGrid.Children.OfType<StackPanel>())
        {
            foreach (var btn in panel.Children.OfType<Button>())
                btn.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static void SetRowInteractive(Border row, bool enabled)
    {
        // outerGrid(Col1) の中の contentGrid を取得
        if (row.Child is not Grid outerGrid) return;
        var contentGrid = outerGrid.Children.OfType<Grid>().FirstOrDefault();
        if (contentGrid == null) return;

        // Col0: アイコン Border
        foreach (var el in contentGrid.Children.OfType<Border>())
        {
            if (Grid.GetColumn(el) == 0)
            {
                el.IsHitTestVisible = enabled;
                el.Opacity = enabled ? 1.0 : 0.5;
            }
        }

        // Col1: 情報パネル（StackPanel）内の種別トグル
        // 編集モード中(!enabled)でも種別トグルはクリック可能にする
        foreach (var el in contentGrid.Children.OfType<StackPanel>())
        {
            if (Grid.GetColumn(el) != 1) continue;
            foreach (var child in el.Children.OfType<StackPanel>())
            {
                foreach (var toggle in child.Children.OfType<Border>())
                {
                    toggle.IsHitTestVisible = true; // 常にクリック可能（編集モードのみ実際に動作）
                    toggle.Opacity = 1.0;
                }
            }
        }
    }

    // ===== チャンネルリスト =====
    private void RefreshChannelList()
    {
        var channels = SettingsService.Instance.Channels;
        ChannelList.Children.Clear();
        ChannelCountText.Text = $"{channels.Count} チャンネル";
        EmptyState.Visibility = channels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var ch in channels)
            ChannelList.Children.Add(CreateChannelRow(ch));

        // 編集モード中ならカーソル・インタラクション状態を再適用
        if (_editMode)
            foreach (var child in ChannelList.Children.OfType<Border>())
            {
                child.Cursor = Cursors.Hand;
                SetRowInteractive(child, false);
                SetDeleteButtonVisibility(child, true);
            }

        // チャンネル数変化時に間隔を自動調整
        AutoAdjustIntervalForQuota();
    }

    private void AutoAdjustIntervalForQuota()
    {
        var s        = SettingsService.Instance.Settings;
        var channels = SettingsService.Instance.Channels.Count;
        if (channels == 0) return;

        var (safe, recommended) = ApiQuotaHelper.ValidateInterval(s.CheckIntervalMinutes, channels);
        if (!safe)
        {
            // 推奨値に自動調整
            var recItem = IntervalComboBox?.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag?.ToString() == recommended.ToString());
            if (recItem != null && IntervalComboBox != null)
                IntervalComboBox.SelectedItem = recItem;
            LoggerService.Instance.Warning(
                $"チャンネル数増加によりAPI超過リスク → 監視間隔を{recommended}分に自動調整");
        }
        UpdateQuotaInfo();
    }

    private void UpdateQuotaInfo()
    {
        try
        {
            if (QuotaInfoText == null) return;
            var channels = SettingsService.Instance.Channels.Count;
            var interval = SettingsService.Instance.Settings.CheckIntervalMinutes;
            var daily    = ApiQuotaHelper.EstimateDailyUnits(interval, channels);
            var pct      = daily * 100.0 / ApiQuotaHelper.DailyLimit;
            var color    = pct >= 90 ? "ErrorBrush" : pct >= 70 ? "WarningBrush" : "SuccessBrush";
            QuotaInfoText.Text = $"推定消費量: {daily:N0} / {ApiQuotaHelper.DailyLimit:N0} ユニット/日 ({pct:F0}%)";
            SetDynamicBrush(QuotaInfoText, TextBlock.ForegroundProperty, color);

            // 各間隔項目のコスト計算して超過するものを無効化
            UpdateIntervalComboBoxItems(channels);
        }
        catch { }
    }

    private void UpdateIntervalComboBoxItems(int channels)
    {
        if (IntervalComboBox == null) return;
        foreach (System.Windows.Controls.ComboBoxItem item in IntervalComboBox.Items)
        {
            if (item.Tag is string tagStr && int.TryParse(tagStr, out int mins))
            {
                var cost    = ApiQuotaHelper.EstimateDailyUnits(mins, channels);
                var over    = cost > ApiQuotaHelper.DailyLimit;
                item.IsEnabled = !over;
                item.ToolTip   = over
                    ? $"クォータ超過（{cost:N0} / {ApiQuotaHelper.DailyLimit:N0} ユニット/日）"
                    : $"{cost:N0} ユニット/日";
                item.Opacity   = over ? 0.4 : 1.0;
            }
        }
    }

    private UIElement CreateChannelRow(ChannelInfo ch)
    {
        var compact = SettingsService.Instance.Settings.CompactMode;
        var row = new Border
        {
            Height              = compact ? ChannelRowHeightCompact : ChannelRowHeight,
            Margin              = new Thickness(0, 0, 0, ChannelRowMarginBottom),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor              = Cursors.Arrow,
            CornerRadius        = new CornerRadius(4),
            Tag                 = ch
        };
        SetDynamicBrush(row, Border.BackgroundProperty, "SurfaceAltBrush");
        row.MouseEnter += (_, _) => SetDynamicBrush(row, Border.BackgroundProperty, "HoverBrush");
        row.MouseLeave += (_, _) => SetDynamicBrush(row, Border.BackgroundProperty, "SurfaceAltBrush");

        // D&D
        row.PreviewMouseMove            += ChannelRow_MouseMove;
        row.PreviewMouseLeftButtonDown  += ChannelRow_PreviewMouseDown;
        row.PreviewMouseLeftButtonUp    += ChannelRow_MouseUp;

        // 右クリックメニュー
        row.ContextMenu = BuildChannelContextMenu(ch);

        if (compact)
        {
            // コンパクトモード: 行クリックでリンクを開く
            row.Cursor = Cursors.Hand;
            row.MouseLeftButtonUp += async (s, e) =>
            {
                if (!_editMode && s is Border b && b.Tag is ChannelInfo c)
                {
                    e.Handled = true;
                    await OpenChannelLatestVideoAsync(c);
                }
            };

            // コンパクトモード: [4px帯] [アイコン小] [チャンネル名]
            var outerGrid = new Grid();
            outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var newBar = new Border
            {
                CornerRadius = new CornerRadius(4, 0, 0, 4),
                Visibility   = ch.HasUnread ? Visibility.Visible : Visibility.Hidden,
                Tag          = "NewBar"
            };
            SetDynamicBrush(newBar, Border.BackgroundProperty, "AccentBrush");
            Grid.SetColumn(newBar, 0);

            var grid = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(grid, 1);

            var iconSmall = BuildIconBorderCompact(ch);
            var nameText  = new TextBlock
            {
                Text              = ch.ChannelName,
                FontSize          = 12,
                FontWeight        = FontWeights.SemiBold,
                TextTrimming      = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(8, 0, 0, 0)
            };
            SetDynamicBrush(nameText, TextBlock.ForegroundProperty, "TextPrimaryBrush");

            Grid.SetColumn(iconSmall, 0);
            Grid.SetColumn(nameText,  1);

            grid.Children.Add(iconSmall);
            grid.Children.Add(nameText);
            outerGrid.Children.Add(newBar);
            outerGrid.Children.Add(grid);
            row.Child = outerGrid;
        }
        else
        {
            // 通常モード: [4px帯] [アイコン] [情報] [アクション]
            var outerGrid = new Grid();
            outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var newBar = new Border
            {
                CornerRadius = new CornerRadius(4, 0, 0, 4),
                Visibility   = ch.HasUnread ? Visibility.Visible : Visibility.Hidden,
                Tag          = "NewBar"
            };
            SetDynamicBrush(newBar, Border.BackgroundProperty, "AccentBrush");
            Grid.SetColumn(newBar, 0);

            var grid = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 12, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(grid, 1);

            var iconBorder   = BuildIconBorder(ch);
            var infoPanel    = BuildInfoPanel(ch);
            var actionsPanel = BuildActionsPanel(ch);

            Grid.SetColumn(iconBorder,   0);
            Grid.SetColumn(infoPanel,    1);
            Grid.SetColumn(actionsPanel, 2);

            grid.Children.Add(iconBorder);
            grid.Children.Add(infoPanel);
            grid.Children.Add(actionsPanel);
            outerGrid.Children.Add(newBar);
            outerGrid.Children.Add(grid);
            row.Child = outerGrid;
        }
        return row;
    }

    private static Border BuildIconBorderCompact(ChannelInfo ch)
    {
        const int size = 22; // 通常44の半分
        var iconBorder = new Border
        {
            Width             = size, Height = size,
            CornerRadius      = new CornerRadius(size / 2),
            VerticalAlignment = VerticalAlignment.Center,
            Clip              = new EllipseGeometry(new System.Windows.Point(size / 2, size / 2), size / 2, size / 2),
        };
        var img = GetCachedIcon(ch.ThumbnailUrl);
        if (img != null)
            iconBorder.Child = new System.Windows.Controls.Image
                { Source = img, Stretch = Stretch.UniformToFill };
        return iconBorder;
    }

    private static Border BuildIconBorder(ChannelInfo ch)
    {
        var iconBorder = new Border
        {
            Width             = 44, Height = 44,
            CornerRadius      = new CornerRadius(22),
            Margin            = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Clip              = new EllipseGeometry(new System.Windows.Point(22, 22), 22, 22),
            Cursor            = Cursors.Hand,
            ToolTip           = "クリックして最新動画を開く",
            Tag               = ch
        };
        iconBorder.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true;
        iconBorder.MouseLeftButtonUp += async (s, _) =>
        {
            if (s is Border b && b.Tag is ChannelInfo c)
                await OpenChannelLatestVideoAsync(c);
        };
        var img = GetCachedIcon(ch.ThumbnailUrl);
        if (img != null)
            iconBorder.Child = new System.Windows.Controls.Image
                { Source = img, Stretch = Stretch.UniformToFill };
        return iconBorder;
    }

    private static StackPanel BuildInfoPanel(ChannelInfo ch)
    {
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };

        // チャンネル名 + NEWバッジ
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        var nameText = new TextBlock
        {
            Text          = ch.ChannelName,
            FontSize      = 13,
            FontWeight    = FontWeights.SemiBold,
            TextTrimming  = TextTrimming.CharacterEllipsis,
            TextWrapping  = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        SetDynamicBrush(nameText, TextBlock.ForegroundProperty, "TextPrimaryBrush");

        nameRow.Children.Add(nameText);
        info.Children.Add(nameRow);

        // 種別トグル
        var kindRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        kindRow.Children.Add(MakeKindToggle("動画",  ch.NotifyVideo, v => { ch.NotifyVideo = v; SettingsService.Instance.UpdateChannel(ch); }));
        kindRow.Children.Add(MakeKindToggle("Short", ch.NotifyShort, v => { ch.NotifyShort = v; SettingsService.Instance.UpdateChannel(ch); }));
        kindRow.Children.Add(MakeKindToggle("ライブ", ch.NotifyLive,  v => { ch.NotifyLive  = v; SettingsService.Instance.UpdateChannel(ch); }));
        info.Children.Add(kindRow);

        return info;
    }

    private static StackPanel BuildActionsPanel(ChannelInfo ch)
    {
        var delIcon = new TextBlock { Text = "✕", FontSize = 13 };
        SetDynamicBrush(delIcon, TextBlock.ForegroundProperty, "ErrorBrush");

        var delBtn = new Button
        {
            Content           = delIcon,
            Background        = Brushes.Transparent,
            BorderThickness   = new Thickness(0),
            Padding           = new Thickness(6),
            Cursor            = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility        = Visibility.Collapsed,
            Tag               = ch,
            ToolTip           = "削除"
        };
        delBtn.Click += DeleteChannel_Click;

        return new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children          = { delBtn }
        };
    }

    private static System.Windows.Controls.ContextMenu BuildChannelContextMenu(ChannelInfo ch)
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var clearItem = new System.Windows.Controls.MenuItem { Header = "🔔 NEWバッジを消す", Tag = ch };
        clearItem.Click += (s, _) =>
        {
            if (s is System.Windows.Controls.MenuItem mi && mi.Tag is ChannelInfo c)
            {
                c.HasUnread = false;
                SettingsService.Instance.UpdateChannel(c);
                // RefreshChannelList は static から呼べないため Dispatcher 経由
                Application.Current.Dispatcher.Invoke(
                    () => (Application.Current.MainWindow as MainWindow)?.RefreshChannelList());
            }
        };

        var renameItem = new System.Windows.Controls.MenuItem { Header = "✏ 名称を変更", Tag = ch };
        renameItem.Click += (s, _) =>
        {
            if (s is System.Windows.Controls.MenuItem mi && mi.Tag is ChannelInfo c)
                (Application.Current.MainWindow as MainWindow)?.ShowRenameDialog(c);
        };

        menu.Items.Add(clearItem);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(renameItem);
        return menu;
    }

    private static UIElement MakeKindToggle(string label, bool initial, Action<bool> onChanged)
    {
        var border = new Border
        {
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(7, 3, 7, 3),
            Margin          = new Thickness(0, 0, 6, 0),
            Cursor          = Cursors.Hand,
            BorderThickness = new Thickness(1)
        };
        SetDynamicBrush(border, Border.BorderBrushProperty, "BorderBrush");
        SetDynamicBrush(border, Border.BackgroundProperty, initial ? "PrimaryBrush" : "SurfaceElevatedBrush");

        var tb = new TextBlock { Text = label, FontSize = 10, FontWeight = FontWeights.SemiBold };
        if (initial) tb.Foreground = Brushes.White;
        else SetDynamicBrush(tb, TextBlock.ForegroundProperty, "TextMutedBrush");
        border.Child = tb;

        bool current = initial;
        border.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // 編集モード中のみ変更可能
            var win = Application.Current.MainWindow as MainWindow;
            if (win == null || !win._editMode) return;
            e.Handled = true;
            current = !current;
            onChanged(current);
            SetDynamicBrush(border, Border.BackgroundProperty, current ? "PrimaryBrush" : "SurfaceElevatedBrush");
            if (current) tb.Foreground = Brushes.White;
            else SetDynamicBrush(tb, TextBlock.ForegroundProperty, "TextMutedBrush");
        };
        // 編集モード外はカーソルをデフォルトに
        border.Cursor = Cursors.Arrow;
        return border;
    }

    // ===== チャンネル操作 =====
    // トースト通知クリック時に MonitorService から呼び出すための static ラッパー
    public static async Task OpenChannelLatestVideoFromToastAsync(ChannelInfo ch)
        => await OpenChannelLatestVideoAsync(ch);

    private static async Task OpenChannelLatestVideoAsync(ChannelInfo ch)
    {
        if (ch.HasUnread)
        {
            ch.HasUnread = false;
            SettingsService.Instance.UpdateChannel(ch);
            Application.Current.Dispatcher.Invoke(
                () => (Application.Current.MainWindow as MainWindow)?.RefreshChannelList());
        }

        if (!ch.NotifyVideo && !ch.NotifyShort && !ch.NotifyLive)
        {
            OpenUrl(ch.ChannelUrl);
            LoggerService.Instance.Info("チャンネルページを開きます（全種別オフ）", ch.ChannelName);
            return;
        }

        string url = ch.ChannelUrl;
        var apiKey = SettingsService.Instance.Settings.ApiKey;

        if (!string.IsNullOrEmpty(apiKey))
        {
            LoggerService.Instance.Info("最新動画を検索中...", ch.ChannelName);
            try
            {
                var client = new YouTubeApiClient();
                var result = await client.FetchLatestAllowedVideoAsync(
                    ch.ChannelId, ch.NotifyVideo, ch.NotifyShort, ch.NotifyLive);

                if (result.HasValue)
                {
                    var (videoId, kind) = result.Value;
                    if (kind == VideoKind.Video && videoId != null)
                    {
                        ch.LastVideoId = videoId;
                        SettingsService.Instance.UpdateChannel(ch);
                    }
                    url = $"https://www.youtube.com/watch?v={videoId ?? string.Empty}";
                    LoggerService.Instance.Info($"最新{KindLabel(kind)}を開きます", ch.ChannelName);
                }
                else
                {
                    url = !string.IsNullOrEmpty(ch.LastVideoId)
                        ? $"https://www.youtube.com/watch?v={ch.LastVideoId}" : ch.ChannelUrl;
                    LoggerService.Instance.Info("対象動画が見つかりませんでした", ch.ChannelName);
                }
            }
            catch
            {
                url = !string.IsNullOrEmpty(ch.LastVideoId)
                    ? $"https://www.youtube.com/watch?v={ch.LastVideoId}" : ch.ChannelUrl;
                LoggerService.Instance.Warning("API失敗、フォールバック", ch.ChannelName);
            }
        }
        else if (!string.IsNullOrEmpty(ch.LastVideoId))
        {
            url = $"https://www.youtube.com/watch?v={ch.LastVideoId}";
            LoggerService.Instance.Info("最新動画を開きます（APIキー未設定）", ch.ChannelName);
        }

        OpenUrl(url);
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private void ShowRenameDialog(ChannelInfo ch)
    {
        var dlg = new Window
        {
            Title = "名称を変更", Width = 360, Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this, ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.None
        };
        SetDynamicBrush(dlg, Window.BackgroundProperty, "SurfaceBrush");

        var root = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
        SetDynamicBrush(root, Border.BorderBrushProperty, "BorderBrush");

        var panel  = new StackPanel { Margin = new Thickness(20) };
        var label  = new TextBlock { Text = "チャンネルの表示名を入力してください", FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
        SetDynamicBrush(label, TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var input = new TextBox
        {
            Text = ch.ChannelName, FontSize = 13, Margin = new Thickness(0, 0, 0, 14),
            Style = (Style)Application.Current.Resources["ModernTextBox"]
        };

        var btnRow    = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancelBtn = new Button { Content = "キャンセル", Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)Application.Current.Resources["SecondaryButton"], Padding = new Thickness(14, 7, 14, 7) };
        var okBtn     = new Button { Content = "変更", IsDefault = true,
            Style = (Style)Application.Current.Resources["PrimaryButton"], Padding = new Thickness(14, 7, 14, 7) };

        cancelBtn.Click += (_, _) => dlg.Close();
        okBtn.Click += (_, _) =>
        {
            var name = input.Text.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                ch.ChannelName = name;
                SettingsService.Instance.UpdateChannel(ch);
                LoggerService.Instance.Info($"名称を変更しました → {name}", ch.ChannelName);
                RefreshChannelList();
            }
            dlg.Close();
        };
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)  okBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (e.Key == Key.Escape) dlg.Close();
        };

        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(okBtn);
        panel.Children.Add(label);
        panel.Children.Add(input);
        panel.Children.Add(btnRow);
        root.Child  = panel;
        dlg.Content = root;
        dlg.ShowDialog();
        input.Focus();
    }

    private static void DeleteChannel_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button btn || btn.Tag is not ChannelInfo ch) return;
        if (MessageBox.Show($"「{ch.ChannelName}」を削除しますか？", "確認",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SettingsService.Instance.RemoveChannel(ch.ChannelId);
        LoggerService.Instance.Info("チャンネルを削除しました", ch.ChannelName);
        (Application.Current.MainWindow as MainWindow)?.RefreshChannelList();
    }

    // ===== 監視ステータス =====
    private void UpdateMonitorStatus(bool isRunning)
    {
        // StatusBadge は Button テンプレート内の要素を FindName で取得
        if (StatusBadge.Template?.FindName("StatusText", StatusBadge)
            is System.Windows.Controls.TextBlock st)
        {
            st.Text = isRunning ? "監視中" : "停止中";
            SetDynamicBrush(st, System.Windows.Controls.TextBlock.ForegroundProperty,
                isRunning ? "SuccessBrush" : "SidebarTextBrush");
        }
        if (StatusBadge.Template?.FindName("StatusDot", StatusBadge)
            is SolidColorBrush dot)
        {
            var brush = Application.Current.TryFindResource(
                isRunning ? "SuccessBrush" : "SidebarTextBrush") as SolidColorBrush;
            if (brush != null) dot.Color = brush.Color;
        }
        SetDynamicBrush(StatusBadge, System.Windows.Controls.Button.BackgroundProperty,
            isRunning ? "SidebarStatusBgBrush" : "SidebarStatusBgBrush");
        StatusBadge.ToolTip = isRunning ? "クリックして監視を停止" : "クリックして監視を開始";

        if (_sidebarCollapsed)
            UpdateToggleIconColor(isRunning);
    }

    // ===== サイドバー =====
    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;

        SidebarColumn.Width = new GridLength(_sidebarCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);

        if (_sidebarCollapsed)
        {
            MenuLabel.Text              = " ";
            StatusBadge.Visibility      = Visibility.Collapsed;
            NavWatch.Content            = "";
            NavLog.Content              = "";
            NavSettings.Content         = "";
            SidebarToggleButton.ToolTip = "メニューを展開する";
            UpdateToggleIcon("▶");
            UpdateToggleIconColor(MonitorService.Instance.IsRunning);
        }
        else
        {
            MenuLabelWrap.Visibility  = Visibility.Visible;
            MenuLabel.Text            = "MENU";
            StatusBadge.Visibility    = Visibility.Visible;
            NavWatch.Content          = "チャンネルリスト";
            NavLog.Content            = "動作ログ";
            NavSettings.Content       = "基本設定";
            UpdateMonitorStatus(MonitorService.Instance.IsRunning);
            SidebarToggleButton.ToolTip = "メニューを折り畳む";
            UpdateToggleIcon("◀");
            UpdateToggleIconColor(false); // 展開時は常にデフォルト色
        }

        UpdateMinWidth();
        SettingsService.Instance.Settings.SidebarCollapsed = _sidebarCollapsed;
        SettingsService.Instance.SaveSettings();
    }

    // ===== Ctrl+リサイズで MinWidth を 330px まで縮小可能 =====
    private const double CtrlMinWidth = 330;
    private double _normalMinWidth  = 0;
    private double _normalMinHeight = 0;

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == System.Windows.Input.Key.LeftCtrl ||
            e.Key == System.Windows.Input.Key.RightCtrl)
        {
            _normalMinWidth  = MinWidth;
            _normalMinHeight = MinHeight;
            MinWidth  = CtrlMinWidth;
            // MinHeight はそのまま（縦は制限しない）
        }
    }

    protected override void OnKeyUp(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == System.Windows.Input.Key.LeftCtrl ||
            e.Key == System.Windows.Input.Key.RightCtrl)
        {
            // MinWidth/MinHeight を元に戻す
            MinWidth  = _normalMinWidth;
            MinHeight = _normalMinHeight;

            // Ctrl解放時点でサイズが最小値を下回っていたら即座に修正
            if (_normalMinWidth  > 0 && Width  < _normalMinWidth)  Width  = _normalMinWidth;
            if (_normalMinHeight > 0 && Height < _normalMinHeight) Height = _normalMinHeight;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        // Ctrl非押下中: 最小サイズを下回ったら即座に強制復元
        if (System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LeftCtrl) ||
            System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.RightCtrl))
            return;

        bool changed = false;
        double w = Width, h = Height;
        if (_normalMinWidth  > 0 && w < _normalMinWidth)  { w = _normalMinWidth;  changed = true; }
        if (_normalMinHeight > 0 && h < _normalMinHeight) { h = _normalMinHeight; changed = true; }
        if (changed) { Width = w; Height = h; }
    }

    private void UpdateMinWidth()
    {
        MinWidth = _sidebarCollapsed
            ? 405
            : 410 + SidebarExpandedWidth;

        // _normalMinWidth/_normalMinHeight を常に最新値に同期
        _normalMinWidth  = MinWidth;
        _normalMinHeight = MinHeight > 0 ? MinHeight : 300;

        // 折り畳み時にウィンドウ幅が MinWidth を超えていたら縮める
        if (_sidebarCollapsed && Width > MinWidth)
            Width = MinWidth;
    }

    private void UpdateToggleIcon(string icon)
    {
        if (SidebarToggleButton.Template?.FindName("ToggleIcon", SidebarToggleButton)
            is System.Windows.Controls.TextBlock tb)
            tb.Text = icon;
    }

    private void UpdateToggleIconColor(bool isRunning)
    {
        if (SidebarToggleButton.Template?.FindName("ToggleIcon", SidebarToggleButton)
            is System.Windows.Controls.TextBlock tb)
        {
            if (isRunning)
                SetDynamicBrush(tb, System.Windows.Controls.TextBlock.ForegroundProperty, "SuccessBrush");
            else
                SetDynamicBrush(tb, System.Windows.Controls.TextBlock.ForegroundProperty, "SidebarTextBrush");
        }
    }

    // ===== 設定サブナビゲーション =====
    private void SettingsNav_Click(object sender, RoutedEventArgs e) { }

    private void SettingsNavBorder_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        SettingsPageApp.Visibility     = Visibility.Collapsed;
        SettingsPageMonitor.Visibility = Visibility.Collapsed;
        SettingsPageLog.Visibility     = Visibility.Collapsed;
        SettingsPageApi.Visibility     = Visibility.Collapsed;
        SettingsPageAbout.Visibility   = Visibility.Collapsed;

        var navItems = new[] { SettingsNavApp, SettingsNavMonitor, SettingsNavLog, SettingsNavApi, SettingsNavAbout };
        foreach (var nav in navItems)
        {
            nav.BorderBrush = new SolidColorBrush(Colors.Transparent);
            if (nav.Child is System.Windows.Controls.TextBlock tb)
                SetDynamicBrush(tb, System.Windows.Controls.TextBlock.ForegroundProperty, "TextMutedBrush");
        }

        if (sender is not Border active) return;
        active.BorderBrush = (Brush)Application.Current.Resources["PrimaryBrush"];
        if (active.Child is System.Windows.Controls.TextBlock activeTb)
            SetDynamicBrush(activeTb, System.Windows.Controls.TextBlock.ForegroundProperty, "PrimaryBrush");

        if      (sender == SettingsNavApp)     SettingsPageApp.Visibility     = Visibility.Visible;
        else if (sender == SettingsNavMonitor) SettingsPageMonitor.Visibility = Visibility.Visible;
        else if (sender == SettingsNavLog)     SettingsPageLog.Visibility     = Visibility.Visible;
        else if (sender == SettingsNavApi)     SettingsPageApi.Visibility     = Visibility.Visible;
        else if (sender == SettingsNavAbout)   SettingsPageAbout.Visibility   = Visibility.Visible;
    }

    // ===== ナビゲーション =====
    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        PageWatch.Visibility    = Visibility.Collapsed;
        PageLog.Visibility      = Visibility.Collapsed;
        PageSettings.Visibility = Visibility.Collapsed;
        NavWatch.Style    = (Style)FindResource("NavButton");
        NavLog.Style      = (Style)FindResource("NavButton");
        NavSettings.Style = (Style)FindResource("NavButton");

        if      (sender == NavWatch)    { PageWatch.Visibility    = Visibility.Visible; NavWatch.Style    = (Style)FindResource("NavButtonActive"); }
        else if (sender == NavLog)      { PageLog.Visibility      = Visibility.Visible; NavLog.Style      = (Style)FindResource("NavButtonActive"); }
        else if (sender == NavSettings) { PageSettings.Visibility = Visibility.Visible; NavSettings.Style = (Style)FindResource("NavButtonActive"); }
    }

    // ===== タイトルバー =====
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { TitleBar_Maximize(sender, e); return; }
        if (e.ButtonState == MouseButtonState.Pressed)
            SendMessage(new WindowInteropHelper(this).Handle, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
    }

    private void TitleBar_Minimize(object sender, RoutedEventArgs e) =>
        WindowState = System.Windows.WindowState.Minimized;

    private void TitleBar_Maximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == System.Windows.WindowState.Maximized
            ? System.Windows.WindowState.Normal
            : System.Windows.WindowState.Maximized;

    private void TitleBar_Close(object sender, RoutedEventArgs e) => Close();

    // ===== チャンネル追加 =====
    private void AddChannelHeader_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AddChannelWindow { Owner = this };
        dlg.ShowDialog();
        if (dlg.ChannelAdded)
            RefreshChannelList();
    }


    // ===== アクションボタン =====
    private async void ManualCheckButton_Click(object sender, RoutedEventArgs e)
    {
        InlineCheckButton.IsEnabled = false;
        Nav_Click(NavWatch, e);
        await MonitorService.Instance.ManualCheckAsync();
        RefreshChannelList();
        InlineCheckButton.IsEnabled = true;
    }

    private void MonitorToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (MonitorService.Instance.IsRunning) MonitorService.Instance.Stop();
        else MonitorService.Instance.Start();
        Nav_Click(NavLog, e);
    }

    // ===== 設定ハンドラ =====
    private void SaveApiKey_Click(object sender, RoutedEventArgs e)
    {
        if (SaveApiKeyButton.Content?.ToString() == "変更")
        {
            // 変更ボタン → 編集可能にする
            UpdateApiKeyState(false);
            ApiKeyBox.Focus();
            return;
        }

        // 保存ボタン
        var key = ApiKeyBox.IsReadOnly ? _actualApiKey : ApiKeyBox.Text.Trim();
        if (string.IsNullOrEmpty(key)) return;
        _actualApiKey = key;
        SettingsService.Instance.Settings.ApiKey = key;
        SettingsService.Instance.SaveSettings();
        LoggerService.Instance.Success("APIキーを保存しました");
        UpdateApiKeyState(true);
    }

    private string _actualApiKey = "";

    private void UpdateApiKeyState(bool saved)
    {
        if (saved)
        {
            _actualApiKey     = ApiKeyBox.Text.Trim();
            // マスク文字で中央表示
            ApiKeyBox.Text      = new string('●', Math.Min(_actualApiKey.Length, 32));
            ApiKeyBox.IsReadOnly = true;
            ApiKeyBox.TextAlignment = System.Windows.TextAlignment.Center;
        }
        else
        {
            ApiKeyBox.Text      = _actualApiKey;
            ApiKeyBox.IsReadOnly = false;
            ApiKeyBox.TextAlignment = System.Windows.TextAlignment.Left;
            ApiKeyBox.Focus();
            ApiKeyBox.SelectAll();
        }
        SaveApiKeyButton.Content = saved ? "変更" : "保存";
        SetDynamicBrush(SaveApiKeyButton, Button.BackgroundProperty,
            saved ? "SurfaceElevatedBrush" : "PrimaryBrush");
        if (saved)
            SetDynamicBrush(SaveApiKeyButton, Button.ForegroundProperty, "TextPrimaryBrush");
        else
            SaveApiKeyButton.Foreground = Brushes.White;
    }

    private void DarkModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        var isDark = DarkModeToggle.IsChecked == true;
        SettingsService.Instance.Settings.IsDarkMode = isDark;
        SettingsService.Instance.SaveSettings();
        App.ApplyTheme(isDark);
        RefreshChannelList();
        InvalidateVisual();
        UpdateLayout();
    }

    private void NotificationToggle_Changed(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.Settings.ShowDesktopNotification = NotificationToggle.IsChecked == true;
        SettingsService.Instance.SaveSettings();
    }

    private void CompactModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.Settings.CompactMode = CompactModeToggle.IsChecked == true;
        SettingsService.Instance.SaveSettings();
        RefreshChannelList();
    }

    private void NotificationSoundToggle_Changed(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.Settings.NotificationSound = NotificationSoundToggle.IsChecked == true;
        SettingsService.Instance.SaveSettings();
    }

    private void TrayToggle_Changed(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.Settings.MinimizeToTray = TrayToggle.IsChecked == true;
        SettingsService.Instance.SaveSettings();
    }

    private void AlwaysOnTopToggle_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = AlwaysOnTopToggle.IsChecked == true;
        Topmost = enabled;
        SettingsService.Instance.Settings.AlwaysOnTop = enabled;
        SettingsService.Instance.SaveSettings();
        UpdatePinButton(enabled);
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        var enabled = !SettingsService.Instance.Settings.AlwaysOnTop;
        Topmost = enabled;
        SettingsService.Instance.Settings.AlwaysOnTop = enabled;
        SettingsService.Instance.SaveSettings();
        AlwaysOnTopToggle.IsChecked = enabled;
        UpdatePinButton(enabled);
    }

    private void UpdatePinButton(bool pinned)
    {
        if (PinButton.Template?.FindName("PinIcon", PinButton)
            is System.Windows.Controls.TextBlock icon)
        {
            icon.Opacity = 1.0;
            if (pinned)
                icon.Foreground = (Brush)Application.Current.Resources["ErrorBrush"];
            else
                SetDynamicBrush(icon, System.Windows.Controls.TextBlock.ForegroundProperty, "SidebarTextBrush");
        }
        PinButton.ToolTip = pinned ? "常に前面に表示: ON（クリックで解除）" : "常に前面に表示: OFF";
    }

    private void StartupToggle_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = StartupToggle.IsChecked == true;
        SettingsService.Instance.Settings.StartWithWindows = enabled;
        SettingsService.Instance.SaveSettings();
        SetStartup(enabled);
    }

    private void IntervalComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IntervalComboBox.SelectedItem is not ComboBoxItem item) return;
        if (!int.TryParse(item.Tag?.ToString(), out var minutes)) return;

        var channels = SettingsService.Instance.Channels.Count;
        var (safe, recommended) = ApiQuotaHelper.ValidateInterval(minutes, channels);

        if (!safe)
        {
            // 超過する場合は推奨値に自動修正
            var recItem = IntervalComboBox.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag?.ToString() == recommended.ToString());
            if (recItem != null && recItem != item)
            {
                IntervalComboBox.SelectedItem = recItem;
                MessageBox.Show(
                    $"チャンネル数 {channels} 件では {minutes} 分間隔だと\n1日のAPIクォータ（10,000ユニット）を超過します。\n\n自動的に {recommended} 分間隔に調整しました。",
                    "API クォータ超過防止", MessageBoxButton.OK, MessageBoxImage.Warning);
                return; // 再帰的に SelectionChanged が呼ばれる
            }
        }

        SettingsService.Instance.Settings.CheckIntervalMinutes = minutes;
        SettingsService.Instance.SaveSettings();
        MonitorService.Instance.RestartWithNewInterval();
        UpdateQuotaInfo();
    }

    private void TestNotification_Click(object sender, RoutedEventArgs e)
    {
        try   { MonitorService.Instance.SendTestNotification(); LoggerService.Instance.Info("テスト通知を送信しました"); }
        catch (Exception ex) { LoggerService.Instance.Error($"テスト通知失敗: {ex.Message}"); }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(SettingsService.Instance.AppDataDir, "logs");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)      => LoggerService.Instance.ClearUiLog();

    // ===== ログメンテナンス =====
    private void RefreshLogStats()
    {
        var (count, bytes, oldest, newest) = LoggerService.Instance.GetLogStats();
        if (count == 0)
        {
            LogStatsText.Text = "ログファイルはありません";
            return;
        }
        var mb = bytes / 1024.0 / 1024.0;
        var sizeStr = mb >= 1 ? $"{mb:F1} MB" : $"{bytes / 1024.0:F0} KB";
        LogStatsText.Text = $"ファイル数: {count} 件  合計サイズ: {sizeStr}\n最古: {oldest:yyyy/MM/dd}  最新: {newest:yyyy/MM/dd}";
    }

    private void AutoCleanLogsToggle_Changed(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.Settings.AutoCleanLogs = AutoCleanLogsToggle.IsChecked == true;
        SettingsService.Instance.SaveSettings();
    }

    private void LogRetentionComboBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LogRetentionComboBox.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out var days))
        {
            SettingsService.Instance.Settings.LogRetentionDays = days;
            SettingsService.Instance.SaveSettings();
        }
    }

    private void CleanLogsNow_Click(object sender, RoutedEventArgs e)
    {
        var days = SettingsService.Instance.Settings.LogRetentionDays;
        if (days == -1)
        {
            LogCleanResultText.Text = "保持期間が「無制限」のため削除しません";
            return;
        }
        var (deleted, freed) = LoggerService.Instance.CleanOldLogs(days);
        var mb = freed / 1024.0 / 1024.0;
        var sizeStr = mb >= 1 ? $"{mb:F1} MB" : $"{freed / 1024.0:F0} KB";
        LogCleanResultText.Text = deleted > 0
            ? $"✅ {deleted} 件削除（{sizeStr} 解放）"
            : "削除対象のログファイルはありませんでした";
        LoggerService.Instance.Info($"ログ手動削除: {deleted}件 ({sizeStr})");
        RefreshLogStats();
    }
    private void ApiKeyLink_Click(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        OpenUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private static void SetStartup(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            var exe = Environment.ProcessPath
                   ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (enable) key.SetValue("YTNotifier", $"\"{exe}\"");
            else        key.DeleteValue("YTNotifier", false);
        }
        catch { }
    }

    // ===== ドラッグアンドドロップ =====
    private void ChannelRow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _dragStartPoint = e.GetPosition(null);
        _isDragging     = false;
        _dragSource     = null;
        if (sender is FrameworkElement fe && fe.Tag is ChannelInfo src)
        {
            _dragSource      = src;
            _dragSourceIndex = SettingsService.Instance.Channels.FindIndex(c => c.ChannelId == src.ChannelId);
        }
    }

    private void ChannelRow_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_editMode) return;
        if (e.LeftButton != MouseButtonState.Pressed || _dragSource == null || _isDragging) return;

        var diff = _dragStartPoint - e.GetPosition(null);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _isDragging = true;
        if (sender is Border rowBorder)
        {
            AttachDragAdorner(rowBorder, e.GetPosition(ChannelList));
            rowBorder.Opacity = 0.4;
            // ドラッグ中は手を閉じたカーソル
            Mouse.OverrideCursor = Cursors.ScrollAll;
            ChannelList.PreviewDragOver += OnDragOverUpdateAdorner;
            DragDrop.DoDragDrop(rowBorder, new DataObject("ChannelDrag", _dragSource.ChannelId), DragDropEffects.Move);
            ChannelList.PreviewDragOver -= OnDragOverUpdateAdorner;
            Mouse.OverrideCursor = null;
            rowBorder.Opacity = 1.0;
            DetachDragAdorner();
            HideDropIndicator();
        }
        _isDragging = false;
    }

    private void ChannelRow_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragSource = null; _dragSourceIndex = -1; _isDragging = false;
        e.Handled   = false;
    }

    private void AttachDragAdorner(Border source, System.Windows.Point pos)
    {
        var visual = new Rectangle
        {
            Width  = source.ActualWidth,
            Height = source.ActualHeight,
            Fill   = new VisualBrush(source) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
            Opacity = 0.85,
            Effect  = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 4, Opacity = 0.4, Direction = 270 }
        };
        _dragAdorner = new DragAdorner(ChannelScrollViewer, visual, pos.X, pos.Y - source.ActualHeight / 2);
    }

    private void DetachDragAdorner() { _dragAdorner?.Detach(); _dragAdorner = null; }

    private void OnDragOverUpdateAdorner(object sender, DragEventArgs e)
    {
        if (_dragAdorner == null) return;
        var pos = e.GetPosition(ChannelScrollViewer);
        _dragAdorner.UpdatePosition(pos.X, pos.Y - 30);
    }

    private void ShowDropIndicator(int insertIndex)
    {
        if (_dropIndicatorIndex == insertIndex) return;
        HideDropIndicator();
        _dropIndicatorIndex = insertIndex;
        _dropIndicator = new Border
        {
            Height              = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible    = false,
            Margin              = new Thickness(8, 0, 8, 0)
        };
        SetDynamicBrush(_dropIndicator, Border.BackgroundProperty, "PrimaryBrush");
        var idx = Math.Min(insertIndex, ChannelList.Children.Count);
        ChannelList.Children.Insert(idx, _dropIndicator);
    }

    private void HideDropIndicator()
    {
        if (_dropIndicator != null && ChannelList.Children.Contains(_dropIndicator))
            ChannelList.Children.Remove(_dropIndicator);
        _dropIndicator      = null;
        _dropIndicatorIndex = -1;
    }

    private int CalcDropIndex(double posY)
    {
        double cumY  = 0;
        int    count = 0;
        foreach (var child in ChannelList.Children.OfType<FrameworkElement>())
        {
            if (child == _dropIndicator) continue;
            double rowH   = child.ActualHeight + child.Margin.Bottom;
            double rowTop = cumY;
            cumY += rowH;
            if (posY <= cumY)
                return posY > rowTop + rowH / 2 ? count + 1 : count;
            count++;
        }
        return count;
    }

    private void ChannelList_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("ChannelDrag")) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Move;
        var dropPos = e.GetPosition(ChannelList);
        ShowDropIndicator(CalcDropIndex(dropPos.Y));
        e.Handled = true;
    }

    private void ChannelList_Drop(object sender, DragEventArgs e)
    {
        HideDropIndicator();

        if (!e.Data.GetDataPresent("ChannelDrag")) return;
        var sourceId = e.Data.GetData("ChannelDrag") as string;
        if (string.IsNullOrEmpty(sourceId)) return;

        var channels = SettingsService.Instance.Channels;
        var srcIdx   = channels.FindIndex(c => c.ChannelId == sourceId);
        if (srcIdx < 0) return;

        int dstIdx = CalcDropIndex(e.GetPosition(ChannelList).Y);

        // インジケーター除去後のインデックス調整
        if (dstIdx == srcIdx || dstIdx == srcIdx + 1) return;

        var ch = channels[srcIdx];
        channels.RemoveAt(srcIdx);
        if (dstIdx > srcIdx) dstIdx--;
        channels.Insert(dstIdx, ch);
        SettingsService.Instance.SaveChannels();
        RefreshChannelList();

        _dragSource = null; _dragSourceIndex = -1;
        e.Handled   = true;
    }
}
