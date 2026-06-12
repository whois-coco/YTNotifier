using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using YTNotifier.Models;
using YTNotifier.Services;

namespace YTNotifier.Views;

public partial class AddChannelWindow : Window
{
    private readonly YouTubeApiClient _youtubeClient = new();
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

        // カテゴリコンボボックスを初期化
        PopulateCategoryComboBox();

        ChannelInputBox.Focus();
    }

    private void PopulateCategoryComboBox()
    {
        CategoryComboBox.Items.Clear();
        CategoryComboBox.Items.Add(new System.Windows.Controls.ComboBoxItem
        {
            Content = "（未設定）", Tag = null
        });
        foreach (var cat in SettingsService.Instance.Categories.OrderBy(c => c.SortOrder))
        {
            CategoryComboBox.Items.Add(new System.Windows.Controls.ComboBoxItem
            {
                Content = cat.CategoryName, Tag = cat.CategoryId
            });
        }
        CategoryComboBox.SelectedIndex = 0;
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
            var pid = await new YouTubeApiClient().GetUploadsPlaylistIdAsync(_previewChannel.ChannelId);
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
