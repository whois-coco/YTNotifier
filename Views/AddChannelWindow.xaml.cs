using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brush   = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using YTNotifier.Constants;
using YTNotifier.Models;
using YTNotifier.Services;

namespace YTNotifier.Views;

public partial class AddChannelWindow : Window
{
    private readonly YouTubeApiClient _youtubeClient = new();
    private ChannelInfo? _previewChannel;
    private readonly Action? _onChannelAdded;

    private readonly List<FocusTabPanel> _detailTabPanels = new();
    private int _selectedDetailTab = 0;

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
        var svc          = SettingsService.Instance;
        var noCategoryMode = svc.Settings.NoCategoryMode;

        CategoryComboBox.Items.Clear();
        CategoryComboBox.Items.Add(new System.Windows.Controls.ComboBoxItem
        {
            Content = "（未設定）", Tag = null
        });
        foreach (var cat in svc.Categories.OrderBy(c => c.SortOrder))
        {
            CategoryComboBox.Items.Add(new System.Windows.Controls.ComboBoxItem
            {
                Content = cat.CategoryName, Tag = cat.CategoryId
            });
        }
        CategoryComboBox.SelectedIndex = 0;

        // カテゴリなし表示モードでは選択を無効化
        CategoryComboBox.IsEnabled = !noCategoryMode;
        CategoryComboBox.Opacity   = noCategoryMode ? 0.4 : 1.0;
        CategoryComboBox.ToolTip   = noCategoryMode ? "カテゴリなし表示モードが有効なため選択できません" : null;
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
        var enabled = ContinuousAddCheckBox.IsChecked == true;
        SettingsService.Instance.Settings.ContinuousAddMode = enabled;
        SettingsService.Instance.SaveSettings();
        AppLogger.Log(LogMsg.ContinuousAddModeChanged, null, enabled ? "ON" : "OFF");
    }

    private async void PreviewChannel_Click(object sender, RoutedEventArgs e)
    {
        var input = ChannelInputBox.Text.Trim();
        if (string.IsNullOrEmpty(input)) return;
        AppLogger.Log(LogMsg.AddChannelPreviewClicked, null, input);

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
            PreviewStatusText.Text = exists
                ? "⚠ このチャンネルは既に追加されています。"
                : $"✅ 「{_previewChannel.ChannelName}」が見つかりました。";
            var brushKey = exists ? "WarningBrush" : "SuccessBrush";
            PreviewStatusText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            AddChannelButton.IsEnabled = !exists;

            // チャンネル発見時に拡張エリアを表示
            if (!exists)
            {
                PopulateCategoryComboBox();
                CheckVideo.IsChecked = true;
                CheckShort.IsChecked = true;
                CheckLive.IsChecked  = true;
                BuildDetailTabUI();
                SelectDetailTab(0);
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

        // スロット設定を適用（Expander 開閉にかかわらず常に設定）
        if (_detailTabPanels.Count == 3)
        {
            _previewChannel.MonitorMode = MonitorMode.Focus;
            _previewChannel.FocusSlots  = _detailTabPanels.Select(p => p.GetSlot()).ToList();
            // タブの IsEnabled をチェック対象フラグと同期
            _previewChannel.FocusSlots[0].IsEnabled = _previewChannel.NotifyVideo;
            _previewChannel.FocusSlots[1].IsEnabled = _previewChannel.NotifyShort;
            _previewChannel.FocusSlots[2].IsEnabled = _previewChannel.NotifyLive;
        }

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
                      $"追加すると監視間隔が {recommended}分 に変更されます";
            if (ConfirmDialog.Show(this, "クォータ超過の警告", msg, "追加する") != true)
                return;
        }
        else if (pct >= 85)
        {
            var msg = $"追加後のAPI推定使用量が {pct:F0}% になります。\n追加しますか？";
            if (ConfirmDialog.Show(this, "クォータ使用量の警告", msg, "追加する") != true)
                return;
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
        AppLogger.Log(LogMsg.ChannelAdded, _previewChannel.ChannelName);
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
            DetailExpander.IsExpanded    = false;
            _detailTabPanels.Clear();
            DetailFocusTabNav.Children.Clear();
            DetailFocusTabContent.Children.Clear();
            ChannelInputBox.Focus();
        }
        else
        {
            Close();
        }
    }

    // ===== 詳細設定タブ =====
    private static readonly string[] DetailTabKindLabels = { "動画", "Short", "ライブ配信" };
    private static readonly VideoKind[] DetailTabKinds   = { VideoKind.Video, VideoKind.Short, VideoKind.Live };

    private void CheckKind_Changed(object sender, RoutedEventArgs e)
    {
        if (_detailTabPanels.Count != 3) return;
        _detailTabPanels[0].SetEnabled(CheckVideo.IsChecked != false);
        _detailTabPanels[1].SetEnabled(CheckShort.IsChecked != false);
        _detailTabPanels[2].SetEnabled(CheckLive.IsChecked  != false);
        for (int i = 0; i < _detailTabPanels.Count; i++)
            SetDetailTabBorderStyle(_detailTabPanels[i], i == _selectedDetailTab);
    }

    private void BuildDetailTabUI()
    {
        _detailTabPanels.Clear();
        DetailFocusTabNav.Children.Clear();
        DetailFocusTabContent.Children.Clear();
        _selectedDetailTab = 0;

        var res = System.Windows.Application.Current.Resources;
        bool[] kindEnabled = { CheckVideo.IsChecked != false, CheckShort.IsChecked != false, CheckLive.IsChecked != false };
        System.Windows.Controls.CheckBox[] kindChecks = { CheckVideo, CheckShort, CheckLive };

        for (int i = 0; i < 3; i++)
        {
            var slot = new FocusSlot
            {
                NotifyKind = DetailTabKinds[i],
                SlotMode   = MonitorMode.Normal,
                IsEnabled  = kindEnabled[i]
            };
            var panel = new FocusTabPanel(slot);
            panel.FixedKind = DetailTabKinds[i];
            _detailTabPanels.Add(panel);

            // 「この設定を有効にする」→ CheckVideo/Short/Live を同期
            int capturedIdx = i;
            panel.OnEnabledChanged = () =>
            {
                kindChecks[capturedIdx].IsChecked = _detailTabPanels[capturedIdx].IsEnabled;
                SetDetailTabBorderStyle(_detailTabPanels[capturedIdx], capturedIdx == _selectedDetailTab);
            };

            int idx    = i;
            var lbl    = new TextBlock { Text = DetailTabKindLabels[i], FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var border = new Border
            {
                Padding         = new Thickness(14, 0, 14, 0),
                Cursor          = System.Windows.Input.Cursors.Hand,
                Background      = Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 2),
                Tag             = i,
                Child           = lbl
            };
            border.MouseLeftButtonUp += (_, _) => SelectDetailTab(idx);
            panel.NavBorder = border;
            panel.NavLabel  = lbl;
            SetDetailTabBorderStyle(panel, false);
            DetailFocusTabNav.Children.Add(border);
        }
    }

    private void SelectDetailTab(int idx)
    {
        _selectedDetailTab = idx;
        for (int i = 0; i < _detailTabPanels.Count; i++)
            SetDetailTabBorderStyle(_detailTabPanels[i], i == idx);

        DetailFocusTabContent.Children.Clear();
        _detailTabPanels[idx].ResetContent();
        DetailFocusTabContent.Children.Add(_detailTabPanels[idx].BuildContent());
    }

    private static void SetDetailTabBorderStyle(FocusTabPanel tab, bool selected)
    {
        var res = System.Windows.Application.Current.Resources;
        if (tab.NavBorder == null || tab.NavLabel == null) return;
        tab.NavBorder.BorderBrush = selected
            ? (Brush)res["PrimaryBrush"]
            : Brushes.Transparent;
        tab.NavLabel.Foreground = selected
            ? (Brush)res["PrimaryBrush"]
            : tab.IsEnabled
                ? (Brush)res["TextSecondaryBrush"]
                : (Brush)res["TextMutedBrush"];
        tab.NavLabel.FontWeight = selected
            ? FontWeights.SemiBold
            : FontWeights.Normal;
    }
}
