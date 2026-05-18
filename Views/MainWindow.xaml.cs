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
        try
        {
            // 起動時にログをクリアして実行中のログのみ表示
            LoggerService.Instance.ClearUiLog();
            LogList.ItemsSource = LoggerService.Instance.Entries;
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
        // 外側ボタン（行全体がクリッカブル）
        var btn = new Button
        {
            Height = 84,
            Margin = new Thickness(0, 0, 0, 4),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Tag = ch,
            ToolTip = "クリックして最新動画を開く"
        };
        btn.Click += ChannelRow_Click;

        // ホバー背景用 Border
        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 0, 12, 0),
        };
        SetDynamicBrush(border, Border.BackgroundProperty, "SurfaceAltBrush");
        btn.MouseEnter += (_, _) => SetDynamicBrush(border, Border.BackgroundProperty, "HoverBrush");
        btn.MouseLeave += (_, _) => SetDynamicBrush(border, Border.BackgroundProperty, "SurfaceAltBrush");

        // 内部グリッド
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // アイコン
        var iconBorder = new Border
        {
            Width = 44, Height = 44,
            CornerRadius = new CornerRadius(22),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Clip = new EllipseGeometry(new System.Windows.Point(22, 22), 22, 22)
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
        var subText = new TextBlock
        {
            Text = $"{ch.ChannelHandle} · {ch.SubscriberCount} 登録者",
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 2, 0, 0)
        };
        SetDynamicBrush(subText, TextBlock.ForegroundProperty, "TextMutedBrush");
        info.Children.Add(subText);

        var lastText = new TextBlock
        {
            Text = $"最終確認: {ch.LastCheckedText}",
            FontSize = 11,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 2, 0, 0)
        };
        SetDynamicBrush(lastText, TextBlock.ForegroundProperty, "TextMutedBrush");
        info.Children.Add(lastText);
        // 種別トグルを info の下部に追加
        var kindRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 0)
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
        var upIcon = new TextBlock { Text = "▲", FontSize = 10 };
        SetDynamicBrush(upIcon, TextBlock.ForegroundProperty, "TextMutedBrush");
        var upBtn = new Button
        {
            Content = upIcon,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2, 4, 2),
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = ch,
            ToolTip = "上へ移動"
        };
        upBtn.Click += MoveChannelUp_Click;

        var downIcon = new TextBlock { Text = "▼", FontSize = 10 };
        SetDynamicBrush(downIcon, TextBlock.ForegroundProperty, "TextMutedBrush");
        var downBtn = new Button
        {
            Content = downIcon,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2, 4, 2),
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = ch,
            ToolTip = "下へ移動"
        };
        downBtn.Click += MoveChannelDown_Click;

        var movePanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        movePanel.Children.Add(upBtn);
        movePanel.Children.Add(downBtn);

        actions.Children.Add(movePanel);
        actions.Children.Add(delBtn);
        Grid.SetColumn(actions, 2);

        grid.Children.Add(iconBorder);
        grid.Children.Add(info);
        grid.Children.Add(actions);

        border.Child = grid;
        btn.Content = border;
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
            PageLog.Visibility = Visibility.Visible;
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

    // ===== チャンネル行クリック（最新の通常動画を開く・未読クリア） =====
    private async void ChannelRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ChannelInfo ch) return;

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

    private static string KindLabel(VideoKind kind) => kind switch
    {
        VideoKind.Short => "Short",
        VideoKind.Live  => "ライブ",
        _               => "動画"
    };

    // ===== チャンネル上下移動 =====
    private void MoveChannelUp_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button btn && btn.Tag is ChannelInfo ch)
        {
            SettingsService.Instance.MoveChannelUp(ch.ChannelId);
            RefreshChannelList();
        }
    }

    private void MoveChannelDown_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button btn && btn.Tag is ChannelInfo ch)
        {
            SettingsService.Instance.MoveChannelDown(ch.ChannelId);
            RefreshChannelList();
        }
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
