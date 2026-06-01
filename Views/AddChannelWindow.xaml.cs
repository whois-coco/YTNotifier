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

    public bool ChannelAdded { get; private set; } = false;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION        = 2;

    public AddChannelWindow()
    {
        InitializeComponent();
        ChannelInputBox.Focus();
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

    private void AddChannel_Click(object sender, RoutedEventArgs e)
    {
        if (_previewChannel == null) return;
        SettingsService.Instance.AddChannel(_previewChannel);
        LoggerService.Instance.Success("チャンネルを追加しました", _previewChannel.ChannelName);
        ChannelAdded = true;
        Close();
    }
}
