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
    private const int    SidebarExpandedWidth  = 120;
    private const int    SidebarCollapsedWidth = 44;
    private const int    ChannelRowHeight      = 60;
    private const double ChannelRowMarginBottom = 2;

    // ===== フィールド =====
    private readonly YouTubeApiClient _youtubeClient = new();
    private ChannelInfo? _previewChannel;
    private bool _addChannelExpanded = false;
    private bool _sidebarCollapsed   = false;

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
        RestoreWindowBounds();

        try { LoadSettings(); }
        catch (Exception ex) { LoggerService.Instance.Error($"設定読込エラー: {ex.Message}"); }

        try { RefreshChannelList(); }
        catch (Exception ex) { LoggerService.Instance.Error($"チャンネルリストエラー: {ex.Message}"); }

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
        LoggerService.Instance.ClearErrorLog();
        LogList.ItemsSource      = LoggerService.Instance.Entries;
        ErrorLogList.ItemsSource = LoggerService.Instance.ErrorEntries;
        LoggerService.Instance.ErrorEntries.CollectionChanged += (_, _) =>
            ErrorLogEmpty.Visibility = LoggerService.Instance.ErrorEntries.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
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
        if (s.WindowWidth  > 0) Width  = s.WindowWidth;
        if (s.WindowHeight > 0) Height = s.WindowHeight;

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
        ApiKeyBox.Text                = s.ApiKey;
        DarkModeToggle.IsChecked      = s.IsDarkMode;
        NotificationToggle.IsChecked  = s.ShowDesktopNotification;
        TrayToggle.IsChecked          = s.MinimizeToTray;
        StartupToggle.IsChecked       = s.StartWithWindows;
        AlwaysOnTopToggle.IsChecked   = s.AlwaysOnTop;
        Topmost                       = s.AlwaysOnTop;

        var items = IntervalComboBox.Items.Cast<ComboBoxItem>().ToList();
        IntervalComboBox.SelectedItem = items.FirstOrDefault(
            i => i.Tag?.ToString() == s.CheckIntervalMinutes.ToString()) ?? items[1];

        if (s.SidebarCollapsed)
        {
            _sidebarCollapsed = false;
            SidebarToggle_Click(this, new RoutedEventArgs());
        }

        AddChannelBody.Visibility = Visibility.Collapsed;
        AddChannelChevron.Text    = "▼";

        // クォータ情報を表示
        UpdateQuotaInfo();

        // ログメンテナンス設定
        AutoCleanLogsToggle.IsChecked = s.AutoCleanLogs;
        var retItems = LogRetentionComboBox.Items.Cast<ComboBoxItem>().ToList();
        LogRetentionComboBox.SelectedItem =
            retItems.FirstOrDefault(i => i.Tag?.ToString() == s.LogRetentionDays.ToString())
            ?? retItems[2]; // デフォルト30日
        RefreshLogStats();

        // 自動削除
        if (s.AutoCleanLogs && s.LogRetentionDays > 0)
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

    // ===== チャンネルリスト =====
    private void RefreshChannelList()
    {
        var channels = SettingsService.Instance.Channels;
        ChannelList.Children.Clear();
        ChannelCountText.Text = $"{channels.Count} チャンネル";
        EmptyState.Visibility = channels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var ch in channels)
            ChannelList.Children.Add(CreateChannelRow(ch));

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
        }
        catch { }
    }

    private UIElement CreateChannelRow(ChannelInfo ch)
    {
        var row = new Border
        {
            Height              = ChannelRowHeight,
            Margin              = new Thickness(0, 0, 0, ChannelRowMarginBottom),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor              = Cursors.SizeAll,
            CornerRadius        = new CornerRadius(4),
            Padding             = new Thickness(12, 0, 12, 0),
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

        // レイアウト
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconBorder   = BuildIconBorder(ch);
        var infoPanel    = BuildInfoPanel(ch);
        var actionsPanel = BuildActionsPanel(ch);

        Grid.SetColumn(iconBorder,   0);
        Grid.SetColumn(infoPanel,    1);
        Grid.SetColumn(actionsPanel, 2);

        grid.Children.Add(iconBorder);
        grid.Children.Add(infoPanel);
        grid.Children.Add(actionsPanel);

        row.Child = grid;
        return row;
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
        if (!string.IsNullOrEmpty(ch.ThumbnailUrl))
        {
            try
            {
                iconBorder.Child = new System.Windows.Controls.Image
                {
                    Source  = new BitmapImage(new Uri(ch.ThumbnailUrl)),
                    Stretch = Stretch.UniformToFill
                };
            }
            catch { }
        }
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

        var badge = new Border
        {
            CornerRadius      = new CornerRadius(4),
            Padding           = new Thickness(5, 1, 5, 1),
            Margin            = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility        = ch.HasUnread ? Visibility.Visible : Visibility.Collapsed,
            Child             = new TextBlock { Text = "NEW", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Brushes.White }
        };
        SetDynamicBrush(badge, Border.BackgroundProperty, "AccentBrush");

        nameRow.Children.Add(nameText);
        nameRow.Children.Add(badge);
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
            Content         = delIcon,
            Background      = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding         = new Thickness(6),
            Cursor          = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Tag             = ch,
            ToolTip         = "削除"
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
            e.Handled = true;
            current = !current;
            onChanged(current);
            SetDynamicBrush(border, Border.BackgroundProperty, current ? "PrimaryBrush" : "SurfaceElevatedBrush");
            if (current) tb.Foreground = Brushes.White;
            else SetDynamicBrush(tb, TextBlock.ForegroundProperty, "TextMutedBrush");
        };
        return border;
    }

    // ===== チャンネル操作 =====
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
        StatusText.Text = isRunning ? "監視中" : "停止中";
        SetDynamicBrush(StatusText, System.Windows.Controls.TextBlock.ForegroundProperty,
            isRunning ? "SuccessBrush" : "SidebarTextBrush");
        StatusDot.Color = ((SolidColorBrush)Application.Current.Resources[
            isRunning ? "SuccessBrush" : "SidebarTextBrush"]).Color;
        MonitorToggleButton.Tag     = isRunning ? "⏸" : "▶";
        MonitorToggleButton.Content = isRunning ? "監視停止" : "監視開始";
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
            ManualCheckButton.Content   = "";
            MonitorToggleButton.Content = "";
            SidebarToggleButton.ToolTip = "メニューを展開する";
            UpdateToggleIcon("▶");
            UpdateToggleIconColor(MonitorService.Instance.IsRunning);
        }
        else
        {
            MenuLabelWrap.Visibility  = Visibility.Visible;
            MenuLabel.Text            = "MENU";
            StatusBadge.Visibility    = Visibility.Visible;
            NavWatch.Content          = "確認リスト";
            NavLog.Content            = "動作ログ";
            NavSettings.Content       = "基本設定";
            ManualCheckButton.Content = "今すぐチェック";
            UpdateMonitorStatus(MonitorService.Instance.IsRunning);
            SidebarToggleButton.ToolTip = "メニューを折り畳む";
            UpdateToggleIcon("◀");
            UpdateToggleIconColor(false); // 展開時は常にデフォルト色
        }

        SettingsService.Instance.Settings.SidebarCollapsed = _sidebarCollapsed;
        SettingsService.Instance.SaveSettings();
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
        _addChannelExpanded       = !_addChannelExpanded;
        AddChannelBody.Visibility = _addChannelExpanded ? Visibility.Visible : Visibility.Collapsed;
        AddChannelChevron.Text    = _addChannelExpanded ? "▲" : "▼";
    }

    private async void PreviewChannel_Click(object sender, RoutedEventArgs e)
    {
        var input = ChannelInputBox.Text.Trim();
        if (string.IsNullOrEmpty(input)) return;

        PreviewButton.IsEnabled      = false;
        PreviewStatusText.Text       = "🔍 チャンネル情報を取得中...";
        PreviewContent.Visibility    = Visibility.Collapsed;
        PreviewEmptyState.Visibility = Visibility.Visible;

        try
        {
            if (string.IsNullOrEmpty(SettingsService.Instance.Settings.ApiKey))
            {
                PreviewStatusText.Text = "⚠ APIキーが設定されていません。";
                return;
            }

            _previewChannel = await _youtubeClient.FetchChannelInfoAsync(input);
            if (_previewChannel == null)
            {
                PreviewStatusText.Text     = "❌ チャンネルが見つかりませんでした。";
                AddChannelButton.IsEnabled = false;
                return;
            }

            PreviewName.Text   = _previewChannel.ChannelName;
            PreviewHandle.Text = _previewChannel.ChannelHandle;
            PreviewSubs.Text   = $"{_previewChannel.SubscriberCount} 登録者";
            if (!string.IsNullOrEmpty(_previewChannel.ThumbnailUrl))
                PreviewThumbnail.Source = new BitmapImage(new Uri(_previewChannel.ThumbnailUrl));

            PreviewContent.Visibility    = Visibility.Visible;
            PreviewEmptyState.Visibility = Visibility.Collapsed;

            bool exists = SettingsService.Instance.Channels.Any(c => c.ChannelId == _previewChannel.ChannelId);
            PreviewStatusText.Text     = exists ? "⚠ このチャンネルは既に追加されています。" : $"✅ 「{_previewChannel.ChannelName}」が見つかりました。";
            AddChannelButton.IsEnabled = !exists;
        }
        catch (Exception ex) { PreviewStatusText.Text = $"❌ エラー: {ex.Message}"; AddChannelButton.IsEnabled = false; }
        finally { PreviewButton.IsEnabled = true; }
    }

    private void ChannelInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) PreviewChannel_Click(sender, e);
    }

    private void ChannelInputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ChannelInputPlaceholder != null)
            ChannelInputPlaceholder.Visibility =
                string.IsNullOrEmpty(ChannelInputBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddChannel_Click(object sender, RoutedEventArgs e)
    {
        if (_previewChannel == null) return;
        SettingsService.Instance.AddChannel(_previewChannel);
        LoggerService.Instance.Success("チャンネルを追加しました", _previewChannel.ChannelName);
        RefreshChannelList();
        ChannelInputBox.Text         = string.Empty;
        PreviewContent.Visibility    = Visibility.Collapsed;
        PreviewEmptyState.Visibility = Visibility.Visible;
        PreviewStatusText.Text       = "チャンネルIDまたはハンドル名を入力してプレビューを確認してください";
        AddChannelButton.IsEnabled   = false;
        _previewChannel              = null;
    }

    // ===== アクションボタン =====
    private async void ManualCheckButton_Click(object sender, RoutedEventArgs e)
    {
        ManualCheckButton.IsEnabled = false;
        Nav_Click(NavWatch, e);
        await MonitorService.Instance.ManualCheckAsync();
        RefreshChannelList();
        ManualCheckButton.IsEnabled = true;
    }

    private void MonitorToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (MonitorService.Instance.IsRunning) MonitorService.Instance.Stop();
        else MonitorService.Instance.Start();
    }

    // ===== 設定ハンドラ =====
    private void SaveApiKey_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.Settings.ApiKey = ApiKeyBox.Text.Trim();
        SettingsService.Instance.SaveSettings();
        LoggerService.Instance.Success("APIキーを保存しました");
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
        if (days == 0)
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
    private void ClearErrorLog_Click(object sender, RoutedEventArgs e)
    {
        LoggerService.Instance.ClearErrorLog();
        ErrorLogEmpty.Visibility = Visibility.Visible;
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
        if (e.LeftButton != MouseButtonState.Pressed || _dragSource == null || _isDragging) return;

        var diff = _dragStartPoint - e.GetPosition(null);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _isDragging = true;
        if (sender is Border rowBorder)
        {
            AttachDragAdorner(rowBorder, e.GetPosition(ChannelList));
            rowBorder.Opacity = 0.4;
            ChannelList.PreviewDragOver += OnDragOverUpdateAdorner;
            DragDrop.DoDragDrop(rowBorder, new DataObject("ChannelDrag", _dragSource.ChannelId), DragDropEffects.Move);
            ChannelList.PreviewDragOver -= OnDragOverUpdateAdorner;
            rowBorder.Opacity = 1.0;
            DetachDragAdorner();
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

    private void ChannelList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("ChannelDrag") ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void ChannelList_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("ChannelDrag")) return;
        var sourceId = e.Data.GetData("ChannelDrag") as string;
        if (string.IsNullOrEmpty(sourceId)) return;

        var channels = SettingsService.Instance.Channels;
        var srcIdx   = channels.FindIndex(c => c.ChannelId == sourceId);
        if (srcIdx < 0) return;

        var dropPos = e.GetPosition(ChannelList);
        int dstIdx  = channels.Count - 1;
        double cumY = 0;

        for (int i = 0; i < ChannelList.Children.Count; i++)
        {
            if (ChannelList.Children[i] is not FrameworkElement child) continue;
            double rowH   = child.ActualHeight + child.Margin.Bottom;
            double rowTop = cumY;
            cumY += rowH;
            if (dropPos.Y <= cumY)
            {
                dstIdx = dropPos.Y > rowTop + rowH / 2 ? Math.Min(i + 1, channels.Count - 1) : i;
                break;
            }
        }

        if (dstIdx == srcIdx) return;

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
