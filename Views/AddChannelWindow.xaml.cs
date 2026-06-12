using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Brush      = System.Windows.Media.Brush;
using Brushes    = System.Windows.Media.Brushes;
using Application = System.Windows.Application;

namespace YTNotifier.Views;

public partial class AddChannelWindow : Window
{
    private readonly IYouTubeApiClient _youtubeClient = YouTubeApiClient.Instance;
    private ChannelInfo? _previewChannel;
    private readonly Action? _onChannelAdded;

    public bool ChannelAdded { get; private set; } = false;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION        = 2;

    public AddChannelWindow(Action? onChannelAdded = null)
    {
        InitializeComponent();
        _onChannelAdded = onChannelAdded;

        // 連続追加モードの前回値を復元（デフォルトON）
        ContinuousAddCheckBox.IsChecked = SettingsService.Instance.Settings.ContinuousAddMode;

        PopulateCategoryComboBox();
        SwitchToNormalMode();

        if (!MainWindow.IsDebugDllAvailable())
            TestTabBorder.Visibility = Visibility.Collapsed;

        ChannelInputBox.Focus();
    }

    private void PopulateCategoryComboBox()
    {
        foreach (var combo in new[] { CategoryComboBox, TestCategoryComboBox })
        {
            combo.Items.Clear();
            combo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "（未設定）", Tag = null });
            foreach (var cat in SettingsService.Instance.Categories.OrderBy(c => c.SortOrder))
                combo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = cat.CategoryName, Tag = cat.CategoryId });
            combo.SelectedIndex = 0;
        }
    }

    // ===== モード切替 =====

    private void SwitchToNormalMode()
    {
        NormalModePanel.Visibility = Visibility.Visible;
        TestModePanel.Visibility   = Visibility.Collapsed;
        NormalTabBorder.Background = (Brush)Application.Current.Resources["AccentBrush"];
        NormalTabText.Foreground   = Brushes.White;
        TestTabBorder.Background   = (Brush)Application.Current.Resources["SurfaceAltBrush"];
        TestTabText.Foreground     = (Brush)Application.Current.Resources["TextSecondaryBrush"];
    }

    private void SwitchToTestMode()
    {
        NormalModePanel.Visibility = Visibility.Collapsed;
        TestModePanel.Visibility   = Visibility.Visible;
        TestTabBorder.Background   = (Brush)Application.Current.Resources["AccentBrush"];
        TestTabText.Foreground     = Brushes.White;
        NormalTabBorder.Background = (Brush)Application.Current.Resources["SurfaceAltBrush"];
        NormalTabText.Foreground   = (Brush)Application.Current.Resources["TextSecondaryBrush"];
        TestNameBox.Focus();
    }

    private void NormalTab_Click(object sender, MouseButtonEventArgs e) => SwitchToNormalMode();
    private void TestTab_Click(object sender, MouseButtonEventArgs e)   => SwitchToTestMode();

    // ===== テストチャンネル =====

    private void BrowseTestFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title           = "テストデータファイルを選択",
            Filter          = "JSONファイル (*.json)|*.json|すべてのファイル (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) == true)
        {
            TestDataPathBox.Text               = dlg.FileName;
            TestDataPathPlaceholder.Visibility = Visibility.Collapsed;
            TestStatusText.Text                = string.Empty;
        }
    }

    private void AddTestChannel_Click(object sender, RoutedEventArgs e)
    {
        var name = TestNameBox.Text.Trim();
        var path = TestDataPathBox.Text.Trim();

        if (string.IsNullOrEmpty(name))
        { TestStatusText.Text = "⚠ チャンネル名を入力してください。"; return; }
        if (string.IsNullOrEmpty(path))
        { TestStatusText.Text = "⚠ JSONファイルを選択してください。"; return; }
        if (!System.IO.File.Exists(path))
        { TestStatusText.Text = "⚠ 指定されたファイルが見つかりません。"; return; }

        var testData = TestChannelService.LoadTestData(path);
        if (testData == null)
        { TestStatusText.Text = "⚠ JSONの読み込みに失敗しました。"; return; }
        if (testData.States.Count == 0)
        { TestStatusText.Text = "⚠ テストデータが空です（states が0件）。"; return; }

        var channelId   = "TEST_" + Guid.NewGuid().ToString("N")[..8].ToUpper();
        var selectedCat = TestCategoryComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem;

        var channel = new ChannelInfo
        {
            ChannelId      = channelId,
            ChannelName    = name,
            IsTestChannel  = true,
            TestDataPath   = path,
            TestStateIndex = 0,
            IsEnabled      = true,
            CategoryId     = selectedCat?.Tag as string,
            AddedAt        = DateTime.Now,
            NotifyVideo    = true,
            NotifyShort    = true,
            NotifyLive     = true,
        };

        SettingsService.Instance.AddChannel(channel);
        LoggerService.Instance.Success(
            $"テストチャンネルを追加しました（{testData.States.Count} ステート）", name);
        ChannelAdded = true;
        _onChannelAdded?.Invoke();
        TestStatusText.Text = $"✅ 追加しました（{testData.States.Count} ステート）";

        TestNameBox.Text                   = string.Empty;
        TestDataPathBox.Text               = string.Empty;
        TestDataPathPlaceholder.Visibility = Visibility.Visible;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            SendMessage(new WindowInteropHelper(this).Handle, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ChannelInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) PreviewChannel_Click(sender, e);
        if (e.Key == Key.Escape) Close();
    }

    private void ChannelInputBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (ChannelInputPlaceholder != null)
            ChannelInputPlaceholder.Visibility =
                string.IsNullOrEmpty(ChannelInputBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PasteButton_Click(object sender, RoutedEventArgs e)
    {
        var text = System.Windows.Clipboard.GetText()?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            ChannelInputBox.Text = text;
            ChannelInputBox.CaretIndex = text.Length;
        }
    }

    private void ContinuousAddCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.Settings.ContinuousAddMode = ContinuousAddCheckBox.IsChecked == true;
        SettingsService.Instance.SaveSettings();
    }

    private async void PreviewChannel_Click(object sender, RoutedEventArgs e)
    {
        var input = ChannelInputBox.Text.Trim();
        if (string.IsNullOrEmpty(input)) return;

        PreviewButton.IsEnabled      = false;
        PreviewStatusText.Text       = "🔍 チャンネル情報を取得中...";
        PreviewThumbnail.Visibility  = Visibility.Collapsed;
        PreviewEmptyState.Visibility = Visibility.Visible;
        ExpandedArea.Visibility      = Visibility.Collapsed;

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

            if (!string.IsNullOrEmpty(_previewChannel.ThumbnailUrl))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource        = new Uri(_previewChannel.ThumbnailUrl);
                    bmp.CacheOption      = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 76;
                    bmp.EndInit();
                    PreviewThumbnail.Source      = bmp;
                    PreviewThumbnail.Visibility  = Visibility.Visible;
                    PreviewEmptyState.Visibility = Visibility.Collapsed;
                }
                catch { }
            }

            bool exists = SettingsService.Instance.Channels.Any(c => c.ChannelId == _previewChannel.ChannelId);
            PreviewStatusText.Text     = exists
                ? "⚠ このチャンネルは既に追加されています。"
                : $"✅ 「{_previewChannel.ChannelName}」が見つかりました。";
            AddChannelButton.IsEnabled = !exists;

            // チャンネル発見時に拡張エリアを表示
            if (!exists)
            {
                PopulateCategoryComboBox();
                CheckVideo.IsChecked = true;
                CheckShort.IsChecked = true;
                CheckLive.IsChecked  = true;
                ExpandedArea.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            PreviewStatusText.Text     = $"❌ エラー: {ex.Message}";
            AddChannelButton.IsEnabled = false;
        }
        finally { PreviewButton.IsEnabled = true; }
    }

    private async void AddChannel_Click(object sender, RoutedEventArgs e)
    {
        if (_previewChannel == null) return;

        // カテゴリ設定（未選択または未設定 → null）
        var selectedCat = CategoryComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem;
        _previewChannel.CategoryId = selectedCat?.Tag as string;

        // チェック対象設定（デフォルト全て）
        _previewChannel.NotifyVideo = CheckVideo.IsChecked != false;
        _previewChannel.NotifyShort = CheckShort.IsChecked != false;
        _previewChannel.NotifyLive  = CheckLive.IsChecked  != false;

        // クォータ事前シミュレーション
        var settings    = SettingsService.Instance.Settings;
        var allChannels = SettingsService.Instance.Channels.ToList();
        allChannels.Add(_previewChannel);

        var daily = ApiQuotaHelper.EstimateDailyUnitsForChannels(settings.CheckIntervalMinutes, allChannels);
        var pct   = daily * 100.0 / ApiQuotaHelper.DailyLimit;

        if (daily > ApiQuotaHelper.DailyLimit)
        {
            var (_, recommended) = ApiQuotaHelper.ValidateInterval(settings.CheckIntervalMinutes, allChannels);
            var msg = $"このチャンネルを追加すると、現在の監視間隔（{settings.CheckIntervalMinutes}分）では\n" +
                      $"1日のAPIクォータ（{ApiQuotaHelper.DailyLimit:N0}ユニット）を超過します。\n\n" +
                      $"推定使用量: {daily:N0} ユニット/日（{pct:F0}%）\n" +
                      $"推奨監視間隔: {recommended}分\n\n" +
                      "それでも追加しますか？（設定で間隔を調整してください）";
            if (ConfirmDialog.Show(this, "クォータ超過の警告", msg, "追加する") != true)
                return;
        }
        else if (pct >= 85)
        {
            PreviewStatusText.Text = $"⚠ 追加後のAPI使用量が {pct:F0}% になります。";
        }

        // UploadsPlaylistIdをAPIから取得（トピックチャンネル対応）
        try
        {
            var pid = await YouTubeApiClient.Instance.GetUploadsPlaylistIdAsync(_previewChannel.ChannelId);
            if (!string.IsNullOrEmpty(pid))
                _previewChannel.UploadsPlaylistId = pid;
        }
        catch { /* 取得失敗時はUC→UU変換でフォールバック */ }

        SettingsService.Instance.AddChannel(_previewChannel);
        LoggerService.Instance.Success("チャンネルを追加しました", _previewChannel.ChannelName);
        ChannelAdded = true;
        _onChannelAdded?.Invoke();

        if (ContinuousAddCheckBox.IsChecked == true)
        {
            // 連続追加: リセットして次の入力を待つ
            _previewChannel              = null;
            ChannelInputBox.Text         = "";
            PreviewStatusText.Text       = "✅ 追加しました。次のチャンネルを入力してください。";
            PreviewThumbnail.Visibility  = Visibility.Collapsed;
            PreviewEmptyState.Visibility = Visibility.Visible;
            AddChannelButton.IsEnabled   = false;
            ExpandedArea.Visibility      = Visibility.Collapsed;
            ChannelInputBox.Focus();
        }
        else
        {
            Close();
        }
    }
}
