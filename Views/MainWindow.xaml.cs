using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YTNotifier.Models;
using YTNotifier.Services;
using Application   = System.Windows.Application;
using Brush         = System.Windows.Media.Brush;
using Brushes       = System.Windows.Media.Brushes;
using Button        = System.Windows.Controls.Button;
using KeyEventArgs  = System.Windows.Input.KeyEventArgs;
using MessageBox    = System.Windows.MessageBox;
using Orientation   = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment   = System.Windows.VerticalAlignment;
using Cursors             = System.Windows.Input.Cursors;
using TextBox             = System.Windows.Controls.TextBox;
using MouseEventArgs      = System.Windows.Input.MouseEventArgs;
using DragEventArgs       = System.Windows.DragEventArgs;
using DragDropEffects     = System.Windows.DragDropEffects;
using DataObject          = System.Windows.DataObject;

namespace YTNotifier.Views;

public partial class MainWindow : Window
{
    private readonly YouTubeApiClient _youtubeClient = new();
    private ChannelInfo? _previewChannel;
    private bool _addChannelExpanded = false;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
        MonitorService.Instance.StatusChanged   += OnMonitorStatusChanged;
        MonitorService.Instance.ChannelUpdated  += OnChannelUpdated;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // ウィンドウサイズ・位置を復元
        RestoreWindowBounds();

        try { LoadSettings(); } catch (Exception ex) { LoggerService.Instance.Error($"設定読込エラー: {ex.Message}"); }
        try { RefreshChannelList(); } catch (Exception ex) { LoggerService.Instance.Error($"チャンネルリストエラー: {ex.Message}"); }

        // ChannelList に D&D ドロップを登録
        ChannelList.AllowDrop  = true;
        ChannelList.Background = Brushes.Transparent;
        ChannelList.DragOver  += ChannelList_DragOver;
        ChannelList.Drop      += ChannelList_Drop;
        try
        {
            // 起動時にログをクリアして実行中のログのみ表示
            LoggerService.Instance.ClearUiLog();
            LoggerService.Instance.ClearErrorLog();
            LogList.ItemsSource      = LoggerService.Instance.Entries;
            ErrorLogList.ItemsSource = LoggerService.Instance.ErrorEntries;
            LoggerService.Instance.ErrorEntries.CollectionChanged += (_, _) =>
            {
                ErrorLogEmpty.Visibility = LoggerService.Instance.ErrorEntries.Count == 0
                    ? Visibility.Visible : Visibility.Collapsed;
            };
        } catch { }



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

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        // 最大化時にタスクバーにかぶらないようマージンを調整
        if (WindowState == System.Windows.WindowState.Maximized)
            ((Border)Content).Margin = new Thickness(6);
        else
            ((Border)Content).Margin = new Thickness(0);
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // ウィンドウサイズ・位置を保存（最小化中は保存しない）
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
        if (s.WindowWidth > 0)  Width  = s.WindowWidth;
        if (s.WindowHeight > 0) Height = s.WindowHeight;

        // 画面内に収まる位置かチェックしてから適用
        if (s.WindowLeft >= 0 && s.WindowTop >= 0)
        {
            var screenW = SystemParameters.VirtualScreenWidth;
            var screenH = SystemParameters.VirtualScreenHeight;
            if (s.WindowLeft + Width  <= screenW + 100 &&
                s.WindowTop  + Height <= screenH + 100)
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

        // 最大化時は RestoreBounds を使う
        var bounds = s.WindowMaximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            s.WindowWidth  = bounds.Width;
            s.WindowHeight = bounds.Height;
            s.WindowLeft   = bounds.Left;
            s.WindowTop    = bounds.Top;
        }
        SettingsService.Instance.SaveSettings();
    }

    private void LoadSettings()
    {
        var s = SettingsService.Instance.Settings;
        ApiKeyBox.Text = s.ApiKey;
        DarkModeToggle.IsChecked = s.IsDarkMode;
        NotificationToggle.IsChecked = s.ShowDesktopNotification;
        TrayToggle.IsChecked = s.MinimizeToTray;
        StartupToggle.IsChecked = s.StartWithWindows;

        var intervalItems = IntervalComboBox.Items.Cast<ComboBoxItem>().ToList();
        var match = intervalItems.FirstOrDefault(i => i.Tag?.ToString() == s.CheckIntervalMinutes.ToString());
        IntervalComboBox.SelectedItem = match ?? intervalItems[1];

        AlwaysOnTopToggle.IsChecked = s.AlwaysOnTop;
        Topmost = s.AlwaysOnTop;

        // サイドバー折り畳み状態を復元
        if (s.SidebarCollapsed)
        {
            _sidebarCollapsed = false; // SidebarToggle_Click が反転するため false にしておく
            SidebarToggle_Click(this, new RoutedEventArgs());
        }

        // チャンネル追加エリアをデフォルト折り畳み
        AddChannelBody.Visibility = Visibility.Collapsed;
        AddChannelChevron.Text = "▼";
    }

    // ===== DynamicResource をコードから設定するヘルパー =====
    private static void SetDynamicBrush(FrameworkElement el,
        DependencyProperty dp, string resourceKey)
        => el.SetResourceReference(dp, resourceKey);

    // ===== チャンネルリスト（コードビハインドで行生成） =====
    private void RefreshChannelList()
    {
        var channels = SettingsService.Instance.Channels;
        ChannelList.Children.Clear();

        ChannelCountText.Text = $"{channels.Count} チャンネル";
        EmptyState.Visibility = channels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var ch in channels)
            ChannelList.Children.Add(CreateChannelRow(ch));

    }

    private UIElement CreateChannelRow(ChannelInfo ch)
    {
        // 外側 Border（Button を使わない → MouseMove が確実に発火する）
        var btn = new Border
        {
            Height = 60,
            Margin = new Thickness(0, 0, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor = System.Windows.Input.Cursors.SizeAll,
            Tag = ch
        };

        // D&D イベントを Border に直接登録
        // PreviewMouseMove を使って ScrollViewer のイベント消費を回避
        btn.PreviewMouseMove += ChannelRow_MouseMove;
        btn.PreviewMouseLeftButtonDown += ChannelRow_PreviewMouseDown;
        btn.PreviewMouseLeftButtonUp += ChannelRow_MouseUp;

        // 右クリックメニュー
        var ctxMenu = new System.Windows.Controls.ContextMenu();

        var clearBadgeItem = new System.Windows.Controls.MenuItem
        {
            Header = "🔔 NEWバッジを消す",
            Tag = ch
        };
        clearBadgeItem.Click += (s, ev) =>
        {
            if (s is System.Windows.Controls.MenuItem mi && mi.Tag is ChannelInfo c)
            {
                c.HasUnread = false;
                SettingsService.Instance.UpdateChannel(c);
                RefreshChannelList();
            }
        };

        var renameItem = new System.Windows.Controls.MenuItem
        {
            Header = "✏ 名称を変更",
            Tag = ch
        };
        renameItem.Click += (s, ev) =>
        {
            if (s is System.Windows.Controls.MenuItem mi && mi.Tag is ChannelInfo c)
                ShowRenameDialog(c);
        };

        // btn 自体が Border なので直接スタイル設定
        var border = btn; // 同じ参照
        border.CornerRadius = new CornerRadius(4);
        border.Padding = new Thickness(12, 0, 12, 0);
        SetDynamicBrush(border, Border.BackgroundProperty, "SurfaceAltBrush");
        border.MouseEnter += (_, _) => SetDynamicBrush(border, Border.BackgroundProperty, "HoverBrush");
        border.MouseLeave += (_, _) => SetDynamicBrush(border, Border.BackgroundProperty, "SurfaceAltBrush");

        ctxMenu.Items.Add(clearBadgeItem);
        ctxMenu.Items.Add(new System.Windows.Controls.Separator());
        ctxMenu.Items.Add(renameItem);
        border.ContextMenu = ctxMenu;

        // 内部グリッド
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // アイコン（クリックで最新動画を開く）
        var iconBorder = new Border
        {
            Width = 44, Height = 44,
            CornerRadius = new CornerRadius(22),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Clip = new EllipseGeometry(new System.Windows.Point(22, 22), 22, 22),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "クリックして最新動画を開く"
        };
        iconBorder.Tag = ch;
        iconBorder.PreviewMouseLeftButtonDown += (s, e) => e.Handled = true; // ドラッグ防止
        iconBorder.MouseLeftButtonUp += async (s, ev) =>
        {
            if (s is Border ib && ib.Tag is ChannelInfo c)
                await OpenChannelLatestVideo(c);
        };
        if (!string.IsNullOrEmpty(ch.ThumbnailUrl))
        {
            try
            {
                var img = new System.Windows.Controls.Image
                {
                    Source = new BitmapImage(new Uri(ch.ThumbnailUrl)),
                    Stretch = Stretch.UniformToFill
                };
                iconBorder.Child = img;
            }
            catch { }
        }
        Grid.SetColumn(iconBorder, 0);

        // テキスト情報
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        // チャンネル名 + 未読バッジ
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        var nameText = new TextBlock
        {
            Text = ch.ChannelName,
            FontSize = 13, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        SetDynamicBrush(nameText, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        nameRow.Children.Add(nameText);

        // 未読バッジ（NEW）
        var unreadBadge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = ch.HasUnread ? Visibility.Visible : Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "NEW",
                FontSize = 9, FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            }
        };
        SetDynamicBrush(unreadBadge, Border.BackgroundProperty, "AccentBrush");
        nameRow.Children.Add(unreadBadge);
        info.Children.Add(nameRow);

        // 種別トグルを info の下部に追加
        var kindRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 0)
        };
        kindRow.Children.Add(MakeKindToggle("動画", ch.NotifyVideo, v =>
        {
            ch.NotifyVideo = v;
            SettingsService.Instance.UpdateChannel(ch);
        }));
        kindRow.Children.Add(MakeKindToggle("Short", ch.NotifyShort, v =>
        {
            ch.NotifyShort = v;
            SettingsService.Instance.UpdateChannel(ch);
        }));
        kindRow.Children.Add(MakeKindToggle("ライブ", ch.NotifyLive, v =>
        {
            ch.NotifyLive = v;
            SettingsService.Instance.UpdateChannel(ch);
        }));
        info.Children.Add(kindRow);
        Grid.SetColumn(info, 1);

        // 右側アクション（上下移動・削除）
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };



        var delIcon = new TextBlock { Text = "✕", FontSize = 13 };
        SetDynamicBrush(delIcon, TextBlock.ForegroundProperty, "ErrorBrush");

        var delBtn = new Button
        {
            Content = delIcon,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6),
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = ch,
            ToolTip = "削除"
        };
        delBtn.Click += DeleteChannel_Click;

        // 上下移動ボタン
        actions.Children.Add(delBtn);
        Grid.SetColumn(actions, 2);

        grid.Children.Add(iconBorder);
        grid.Children.Add(info);
        grid.Children.Add(actions);

        border.Child = grid;
        return btn;
    }

    private static UIElement MakeKindToggle(string label, bool initial, Action<bool> onChanged)
    {
        var border = new Border
        {
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(7, 3, 7, 3),
            Margin          = new Thickness(0, 0, 6, 0),
            Cursor          = Cursors.Hand,
            BorderThickness = new Thickness(1),
        };
        SetDynamicBrush(border, Border.BorderBrushProperty, "BorderBrush");
        SetDynamicBrush(border, Border.BackgroundProperty,
            initial ? "PrimaryBrush" : "SurfaceElevatedBrush");

        var tb = new TextBlock
        {
            Text       = label,
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
        };
        if (initial)
            tb.Foreground = Brushes.White;
        else
            SetDynamicBrush(tb, TextBlock.ForegroundProperty, "TextMutedBrush");

        border.Child = tb;

        bool current = initial;

        // PreviewMouseLeftButtonDown で外側 Button への伝播を止めてトグルを処理
        border.PreviewMouseLeftButtonDown += (s, e) =>
        {
            e.Handled = true;
            current = !current;
            onChanged(current);
            SetDynamicBrush(border, Border.BackgroundProperty,
                current ? "PrimaryBrush" : "SurfaceElevatedBrush");
            if (current)
                tb.Foreground = Brushes.White;
            else
                SetDynamicBrush(tb, TextBlock.ForegroundProperty, "TextMutedBrush");
        };

        return border;
    }

    private void OnMonitorStatusChanged(bool isRunning)
    {
        Dispatcher.Invoke(() => UpdateMonitorStatus(isRunning));
    }

    private void OnChannelUpdated()
    {
        // 新着検出時にチャンネルリストを再描画（未読バッジ表示）
        Dispatcher.Invoke(() => RefreshChannelList());
    }

    private void UpdateMonitorStatus(bool isRunning)
    {
        if (isRunning)
        {
            StatusText.Text = "監視中";
            StatusText.Foreground = (Brush)Application.Current.Resources["SuccessBrush"];
            MonitorToggleButton.Tag     = "⏸";
            MonitorToggleButton.Content = "監視停止";
        }
        else
        {
            StatusText.Text = "停止中";
            StatusText.Foreground = (Brush)Application.Current.Resources["TextMutedBrush"];
            MonitorToggleButton.Tag     = "▶";
            MonitorToggleButton.Content = "監視開始";
        }
    }

    // ===== サイドバー折り畳み =====
    private bool _sidebarCollapsed = false;

    // D&D 状態管理
    private ChannelInfo? _dragSource = null;
    private int          _dragSourceIndex = -1;

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;

        if (_sidebarCollapsed)
        {
            // 折り畳み: 44px（アイコン列のみ）
            SidebarColumn.Width    = new GridLength(44);

            // セクションラベル・ステータス非表示
            MenuLabelWrap.Visibility    = Visibility.Collapsed;
            ActionsLabelWrap.Visibility = Visibility.Collapsed;
            StatusBadge.Visibility      = Visibility.Collapsed;

            // ラベルテキストを空にする（Content="" トリガーで NavLabel が Collapsed）
            NavWatch.Content            = "";
            NavLog.Content              = "";
            NavSettings.Content         = "";
            ManualCheckButton.Content   = "";
            MonitorToggleButton.Content = "";



            SidebarToggleButton.ToolTip = "メニューを展開する";
            UpdateToggleIcon("▶");
        }
        else
        {
            // 展開: 200px
            SidebarColumn.Width    = new GridLength(120);

            // セクションラベル・ステータス表示
            MenuLabelWrap.Visibility    = Visibility.Visible;
            ActionsLabelWrap.Visibility = Visibility.Visible;
            StatusBadge.Visibility      = Visibility.Visible;

            // ラベルテキスト復元
            NavWatch.Content          = "確認リスト";
            NavLog.Content            = "動作ログ";
            NavSettings.Content       = "基本設定";
            ManualCheckButton.Content = "今すぐチェック";



            // 監視状態に応じてテキストを復元
            UpdateMonitorStatus(MonitorService.Instance.IsRunning);

            SidebarToggleButton.ToolTip = "メニューを折り畳む";
            UpdateToggleIcon("◀");
        }

        // 折り畳み状態を保存
        SettingsService.Instance.Settings.SidebarCollapsed = _sidebarCollapsed;
        SettingsService.Instance.SaveSettings();
    }

    private void UpdateToggleIcon(string icon)
    {
        // ToggleButton 内の TextBlock を探して更新
        if (SidebarToggleButton.Template?.FindName("ToggleIcon", SidebarToggleButton)
            is System.Windows.Controls.TextBlock tb)
            tb.Text = icon;
    }

    // ===== カスタムタイトルバー =====

    // Win32 API: WindowChrome 環境での確実なドラッグ移動
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 2;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            TitleBar_Maximize(sender, e);
            return;
        }

        // WindowChrome 環境では DragMove() が効かないため Win32 API を使用
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SendMessage(hwnd, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
        }
    }

    private void TitleBar_Minimize(object sender, RoutedEventArgs e)
        => WindowState = System.Windows.WindowState.Minimized;

    private void TitleBar_Maximize(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == System.Windows.WindowState.Maximized
            ? System.Windows.WindowState.Normal
            : System.Windows.WindowState.Maximized;
    }

    private void TitleBar_Close(object sender, RoutedEventArgs e)
        => Close();

    // ===== サイドバーナビゲーション =====
    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        PageWatch.Visibility    = Visibility.Collapsed;
        PageLog.Visibility      = Visibility.Collapsed;
        PageSettings.Visibility = Visibility.Collapsed;

        NavWatch.Style    = (Style)FindResource("NavButton");
        NavLog.Style      = (Style)FindResource("NavButton");
        NavSettings.Style = (Style)FindResource("NavButton");

        if (sender == NavWatch)
        {
            PageWatch.Visibility = Visibility.Visible;
            NavWatch.Style = (Style)FindResource("NavButtonActive");
        }
        else if (sender == NavLog)
        {
            PageLog.Visibility      = Visibility.Visible;
            NavLog.Style = (Style)FindResource("NavButtonActive");
        }
        else if (sender == NavSettings)
        {
            PageSettings.Visibility = Visibility.Visible;
            NavSettings.Style = (Style)FindResource("NavButtonActive");
        }


    }

    // ===== ADD CHANNEL 折り畳み =====
    private void AddChannelHeader_Click(object sender, RoutedEventArgs e)
    {
        _addChannelExpanded = !_addChannelExpanded;
        AddChannelBody.Visibility = _addChannelExpanded ? Visibility.Visible : Visibility.Collapsed;
        AddChannelChevron.Text = _addChannelExpanded ? "▲" : "▼";
    }

    // ===== チャンネルアイコンクリック（最新動画を開く・未読クリア） =====
    private async Task OpenChannelLatestVideo(ChannelInfo ch)
    {
        // 未読フラグをクリア
        if (ch.HasUnread)
        {
            ch.HasUnread = false;
            SettingsService.Instance.UpdateChannel(ch);
            RefreshChannelList();
        }

        string url;

        // 有効な種別が1つもない場合はチャンネルページを開く
        if (!ch.NotifyVideo && !ch.NotifyShort && !ch.NotifyLive)
        {
            url = ch.ChannelUrl;
            LoggerService.Instance.Info("チャンネルページを開きます（全種別オフ）", ch.ChannelName);
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return;
        }

        if (!string.IsNullOrEmpty(SettingsService.Instance.Settings.ApiKey))
        {
            LoggerService.Instance.Info("最新動画を検索中...", ch.ChannelName);
            try
            {
                // 有効な種別の中で最新の動画を取得
                var result = await _youtubeClient.FetchLatestAllowedVideoAsync(
                    ch.ChannelId, ch.NotifyVideo, ch.NotifyShort, ch.NotifyLive);

                if (result.HasValue)
                {
                    var (videoId, kind) = result.Value;
                    // 通常動画の場合は LastVideoId に保存
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
                    // 有効種別で見つからなければフォールバック
                    url = !string.IsNullOrEmpty(ch.LastVideoId)
                        ? $"https://www.youtube.com/watch?v={ch.LastVideoId}"
                        : ch.ChannelUrl;
                    LoggerService.Instance.Info("対象動画が見つかりませんでした", ch.ChannelName);
                }
            }
            catch
            {
                url = !string.IsNullOrEmpty(ch.LastVideoId)
                    ? $"https://www.youtube.com/watch?v={ch.LastVideoId}"
                    : ch.ChannelUrl;
                LoggerService.Instance.Warning("API失敗、フォールバック", ch.ChannelName);
            }
        }
        else if (!string.IsNullOrEmpty(ch.LastVideoId))
        {
            url = $"https://www.youtube.com/watch?v={ch.LastVideoId}";
            LoggerService.Instance.Info("最新動画を開きます（APIキー未設定）", ch.ChannelName);
        }
        else
        {
            url = ch.ChannelUrl;
            LoggerService.Instance.Info("チャンネルページを開きます", ch.ChannelName);
        }

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    // 後方互換のため残す（未使用だが削除するとビルドエラーになる可能性）
    private static string KindLabel(VideoKind kind) => kind switch
    {
        VideoKind.Short => "Short",
        VideoKind.Live  => "ライブ",
        _               => "動画"
    };

    // ===== ドラッグアンドドロップ =====
    private System.Windows.Point _dragStartPoint;
    private bool _isDragging = false;

    private void ChannelRow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _dragStartPoint = e.GetPosition(null);
        _isDragging     = false;
        _dragSource     = null;

        if (sender is FrameworkElement fe && fe.Tag is ChannelInfo src)
        {
            _dragSource      = src;
            _dragSourceIndex = SettingsService.Instance.Channels
                .FindIndex(ch2 => ch2.ChannelId == src.ChannelId);
        }
    }

    private void ChannelRow_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragSource == null || _isDragging)
            return;

        var diff = _dragStartPoint - e.GetPosition(null);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _isDragging = true;

        if (sender is Border rowBorder)
        {
            rowBorder.Opacity = 0.5;
            var data = new DataObject("ChannelDrag", _dragSource.ChannelId);
            DragDrop.DoDragDrop(rowBorder, data, DragDropEffects.Move);
            rowBorder.Opacity = 1.0;
        }

        _isDragging = false;
    }

    private void ChannelRow_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragSource      = null;
        _dragSourceIndex = -1;
        _isDragging      = false;
        e.Handled        = false; // 上位に伝播させる
    }

    // ChannelList StackPanel 全体でドロップを受け取る
    private void ChannelList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("ChannelDrag")
            ? DragDropEffects.Move
            : DragDropEffects.None;
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
            // ActualHeight + Margin.Bottom で正確な行の占有高さを計算
            double rowH   = child.ActualHeight + child.Margin.Bottom;
            double rowTop = cumY;
            cumY += rowH;
            if (dropPos.Y <= cumY)
            {
                dstIdx = dropPos.Y > rowTop + rowH / 2
                    ? Math.Min(i + 1, channels.Count - 1)
                    : i;
                break;
            }
        }

        if (dstIdx == srcIdx) return;

        var dragCh = channels[srcIdx];
        channels.RemoveAt(srcIdx);
        if (dstIdx > srcIdx) dstIdx--;
        channels.Insert(dstIdx, dragCh);
        SettingsService.Instance.SaveChannels();
        RefreshChannelList();

        _dragSource      = null;
        _dragSourceIndex = -1;
        e.Handled        = true;
    }

    // ===== チャンネル名称変更 =====
    private void ShowRenameDialog(ChannelInfo ch)
    {
        var dlg = new Window
        {
            Title = "名称を変更",
            Width = 360,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
        };
        SetDynamicBrush(dlg, Window.BackgroundProperty, "SurfaceBrush");

        var root = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
        SetDynamicBrush(root, Border.BorderBrushProperty, "BorderBrush");

        var panel = new StackPanel { Margin = new Thickness(20) };

        var label = new TextBlock { Text = "チャンネルの表示名を入力してください", FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
        SetDynamicBrush(label, TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var input = new TextBox
        {
            Text = ch.ChannelName,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 14),
            Style = (Style)Application.Current.Resources["ModernTextBox"]
        };

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        var cancelBtn = new Button
        {
            Content = "キャンセル",
            Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)Application.Current.Resources["SecondaryButton"],
            Padding = new Thickness(14, 7, 14, 7)
        };
        cancelBtn.Click += (_, _) => dlg.Close();

        var okBtn = new Button
        {
            Content = "変更",
            Style = (Style)Application.Current.Resources["PrimaryButton"],
            Padding = new Thickness(14, 7, 14, 7),
            IsDefault = true
        };
        okBtn.Click += (_, _) =>
        {
            var newName = input.Text.Trim();
            if (!string.IsNullOrEmpty(newName))
            {
                ch.ChannelName = newName;
                SettingsService.Instance.UpdateChannel(ch);
                LoggerService.Instance.Info($"名称を変更しました → {newName}", ch.ChannelName);
                RefreshChannelList();
            }
            dlg.Close();
        };

        input.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter) okBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (e.Key == Key.Escape) dlg.Close();
        };

        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(okBtn);
        panel.Children.Add(label);
        panel.Children.Add(input);
        panel.Children.Add(btnRow);
        root.Child = panel;
        dlg.Content = root;
        dlg.ShowDialog();
        input.Focus();
    }

    // ===== チャンネル削除 =====
    private void DeleteChannel_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button btn && btn.Tag is ChannelInfo ch)
        {
            var result = MessageBox.Show(
                $"「{ch.ChannelName}」を監視リストから削除しますか？",
                "確認", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                SettingsService.Instance.RemoveChannel(ch.ChannelId);
                LoggerService.Instance.Info("チャンネルを削除しました", ch.ChannelName);
                RefreshChannelList();
            }
        }
    }

    // ===== プレビュー =====
    private async void PreviewChannel_Click(object sender, RoutedEventArgs e)
    {
        var input = ChannelInputBox.Text.Trim();
        if (string.IsNullOrEmpty(input)) return;

        PreviewButton.IsEnabled = false;
        PreviewStatusText.Text = "🔍 チャンネル情報を取得中...";
        PreviewContent.Visibility = Visibility.Collapsed;
        PreviewEmptyState.Visibility = Visibility.Visible;

        try
        {
            if (string.IsNullOrEmpty(SettingsService.Instance.Settings.ApiKey))
            {
                PreviewStatusText.Text = "⚠ APIキーが設定されていません。設定タブからAPIキーを入力してください。";
                return;
            }

            _previewChannel = await _youtubeClient.FetchChannelInfoAsync(input);

            if (_previewChannel == null)
            {
                PreviewStatusText.Text = "❌ チャンネルが見つかりませんでした。入力を確認してください。";
                AddChannelButton.IsEnabled = false;
                return;
            }

            PreviewName.Text = _previewChannel.ChannelName;
            PreviewHandle.Text = _previewChannel.ChannelHandle;
            PreviewSubs.Text = $"{_previewChannel.SubscriberCount} 登録者";

            if (!string.IsNullOrEmpty(_previewChannel.ThumbnailUrl))
                PreviewThumbnail.Source = new BitmapImage(new Uri(_previewChannel.ThumbnailUrl));

            PreviewContent.Visibility = Visibility.Visible;
            PreviewEmptyState.Visibility = Visibility.Collapsed;

            if (SettingsService.Instance.Channels.Any(c => c.ChannelId == _previewChannel.ChannelId))
            {
                PreviewStatusText.Text = "⚠ このチャンネルは既に追加されています。";
                AddChannelButton.IsEnabled = false;
            }
            else
            {
                PreviewStatusText.Text = $"✅ 「{_previewChannel.ChannelName}」が見つかりました。";
                AddChannelButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            PreviewStatusText.Text = $"❌ エラー: {ex.Message}";
            AddChannelButton.IsEnabled = false;
        }
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

    // ===== チャンネル追加 =====
    private void AddChannel_Click(object sender, RoutedEventArgs e)
    {
        if (_previewChannel == null) return;
        SettingsService.Instance.AddChannel(_previewChannel);
        LoggerService.Instance.Success("チャンネルを追加しました", _previewChannel.ChannelName);
        RefreshChannelList();
        ChannelInputBox.Text = string.Empty;
        PreviewContent.Visibility = Visibility.Collapsed;
        PreviewEmptyState.Visibility = Visibility.Visible;
        PreviewStatusText.Text = "チャンネルIDまたはハンドル名を入力してプレビューを確認してください";
        AddChannelButton.IsEnabled = false;
        _previewChannel = null;
    }

    // ===== ヘッダーボタン =====
    private async void ManualCheckButton_Click(object sender, RoutedEventArgs e)
    {
        ManualCheckButton.IsEnabled = false;

        // 監視リストページに切り替え
        PageWatch.Visibility    = Visibility.Visible;
        PageLog.Visibility      = Visibility.Collapsed;
        PageSettings.Visibility = Visibility.Collapsed;
        NavWatch.Style    = (Style)FindResource("NavButtonActive");
        NavLog.Style      = (Style)FindResource("NavButton");
        NavSettings.Style = (Style)FindResource("NavButton");

        await MonitorService.Instance.ManualCheckAsync();
        RefreshChannelList();
        ManualCheckButton.IsEnabled = true;
    }

    private void MonitorToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (MonitorService.Instance.IsRunning) MonitorService.Instance.Stop();
        else MonitorService.Instance.Start();
    }

    // ===== 設定 =====
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

        // コードビハインドで生成したチャンネル行を再描画
        RefreshChannelList();

        // UpdateLayout でリソース変更をUIツリー全体に即時反映
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
        if (IntervalComboBox.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out var minutes))
        {
            SettingsService.Instance.Settings.CheckIntervalMinutes = minutes;
            SettingsService.Instance.SaveSettings();
            MonitorService.Instance.RestartWithNewInterval();
        }
    }

    private void TestNotification_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            MonitorService.Instance.SendTestNotification();
            LoggerService.Instance.Info("テスト通知を送信しました");
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Error($"テスト通知失敗: {ex.Message}");
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(SettingsService.Instance.AppDataDir, "logs");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }

    private void ClearErrorLog_Click(object sender, RoutedEventArgs e)
    {
        LoggerService.Instance.ClearErrorLog();
        ErrorLogEmpty.Visibility = Visibility.Visible;
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
        => LoggerService.Instance.ClearUiLog();

    private void ApiKeyLink_Click(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private static void SetStartup(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            var exePath = Environment.ProcessPath
                ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (enable) key.SetValue("YTNotifier", $"\"{exePath}\"");
            else key.DeleteValue("YTNotifier", false);
        }
        catch { }
    }
}
