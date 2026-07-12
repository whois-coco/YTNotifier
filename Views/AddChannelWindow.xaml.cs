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
    private readonly bool _isDormant;

    private readonly List<FocusTabPanel> _detailTabPanels = new();
    private int _selectedDetailTab = 0;

    private bool _notifyVideo = true;
    private bool _notifyShort = true;
    private bool _notifyLive  = true;
    private readonly Border[] _kindToggleBorders = new Border[3];

    private const string AddWindowShortIconPath =
        "M4 14a1 1 0 0 1-.78-1.63l9.9-10.2a.5.5 0 0 1 .86.46l-1.92 6.02A1 1 0 0 0 13 10h7a1 1 0 0 1 .78 1.63l-9.9 10.2a.5.5 0 0 1-.86-.46l1.92-6.02A1 1 0 0 0 11 14z";

    public bool ChannelAdded { get; private set; } = false;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION        = 2;

    public AddChannelWindow(Action? onChannelAdded = null, bool isDormant = false)
    {
        InitializeComponent();
        Loaded         += (_, _) => WindowCornerHelper.Apply(this);
        _onChannelAdded = onChannelAdded;
        _isDormant      = isDormant;

        // 連続追加モードの前回値を復元（デフォルトON）
        ContinuousAddCheckBox.IsChecked = SettingsService.Instance.Settings.ContinuousAddMode;

        // カテゴリコンボボックスを初期化
        PopulateCategoryComboBox();

        // 種別トグルアイコンを構築
        BuildKindToggles();

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
        var categories = _isDormant ? svc.DormantCategories : svc.Categories;
        foreach (var cat in categories.OrderBy(c => c.SortOrder))
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

    private void NewCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.Log(LogMsg.AddChannelNewCategoryPanelOpened);
        NewCategoryPanel.Visibility = Visibility.Visible;
        NewCategoryInput.Text       = "";
        NewCategoryInput.Focus();
    }

    private void NewCategoryCancel_Click(object sender, RoutedEventArgs e)
    {
        NewCategoryPanel.Visibility = Visibility.Collapsed;
        NewCategoryInput.Text       = "";
    }

    private void NewCategoryConfirm_Click(object sender, RoutedEventArgs e)
    {
        CreateNewCategory();
    }

    private void NewCategoryInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)  CreateNewCategory();
        if (e.Key == System.Windows.Input.Key.Escape) NewCategoryCancel_Click(sender, e);
    }

    private void CreateNewCategory()
    {
        var name = NewCategoryInput.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        var svc        = SettingsService.Instance;
        var categories = _isDormant ? svc.DormantCategories : svc.Categories;
        if (categories.Any(c => c.CategoryName.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            NewCategoryInput.SelectAll();
            NewCategoryInput.Focus();
            return;
        }

        var cat = _isDormant ? svc.AddDormantCategory(name) : svc.AddCategory(name);
        AppLogger.Log(LogMsg.CategoryAdded, null, name);
        NewCategoryPanel.Visibility = Visibility.Collapsed;
        NewCategoryInput.Text       = "";

        PopulateCategoryComboBox();
        var item = CategoryComboBox.Items
            .OfType<System.Windows.Controls.ComboBoxItem>()
            .FirstOrDefault(i => i.Tag as string == cat.CategoryId);
        if (item != null) CategoryComboBox.SelectedItem = item;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            SendMessage(new WindowInteropHelper(this).Handle, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.Log(LogMsg.AddChannelWindowClosed);
        Close();
    }

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
        AppLogger.Log(LogMsg.AddChannelPasteClicked);
    }

    private void ContinuousAddCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = ContinuousAddCheckBox.IsChecked == true;
        SettingsService.Instance.Settings.ContinuousAddMode = enabled;
        SettingsService.Instance.MarkDirty();
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
                _notifyVideo = true;
                _notifyShort = true;
                _notifyLive  = true;
                RefreshKindToggleIcon(0);
                RefreshKindToggleIcon(1);
                RefreshKindToggleIcon(2);
                BuildDetailTabUI();
                SelectDetailTab(0);
                ExpandedArea.Visibility = Visibility.Visible;

                CheckTargetPanel.IsEnabled = !_isDormant;
                CheckTargetPanel.Opacity   = _isDormant ? 0.4 : 1.0;
                CheckTargetPanel.ToolTip   = _isDormant ? "休眠リストでは種別ごとの監視を行わないため設定できません" : null;

                DetailExpander.IsEnabled = !_isDormant;
                DetailExpander.Opacity   = _isDormant ? 0.4 : 1.0;
                DetailExpander.ToolTip   = _isDormant ? "休眠リストでは種別ごとの監視を行わないため設定できません" : null;
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

        // チェック対象設定
        if (_isDormant)
        {
            _previewChannel.NotifyVideo = false;
            _previewChannel.NotifyShort = false;
            _previewChannel.NotifyLive  = false;
        }
        else
        {
            _previewChannel.NotifyVideo = _notifyVideo;
            _previewChannel.NotifyShort = _notifyShort;
            _previewChannel.NotifyLive  = _notifyLive;
        }

        // スロット設定を適用（Expander 開閉にかかわらず常に設定）
        if (_detailTabPanels.Count == 3)
        {
            _previewChannel.MonitorMode = MonitorMode.Focus;
            _previewChannel.FocusSlots  = _detailTabPanels.Select(p => p.GetSlot()).ToList();
            if (_isDormant)
            {
                foreach (var slot in _previewChannel.FocusSlots) slot.IsEnabled = false;
            }
            else
            {
                // タブの IsEnabled をチェック対象フラグと同期
                _previewChannel.FocusSlots[0].IsEnabled = _previewChannel.NotifyVideo;
                _previewChannel.FocusSlots[1].IsEnabled = _previewChannel.NotifyShort;
                _previewChannel.FocusSlots[2].IsEnabled = _previewChannel.NotifyLive;
            }
        }

        // クォータ事前シミュレーション（休眠チャンネルはスキップ）
        if (!_isDormant)
        {
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
        }

        if (_isDormant) _previewChannel.IsDormant = true;

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

    // ===== 種別トグルアイコン =====
    private static readonly string[] KindToggleLabels = { "動画", "Short", "ライブ" };

    private void BuildKindToggles()
    {
        KindToggleRow.Children.Clear();
        for (int i = 0; i < 3; i++)
        {
            int capturedIdx = i;
            var border = new Border
            {
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(4),
                Cursor          = System.Windows.Input.Cursors.Hand,
                BorderThickness = new Thickness(1),
                ToolTip         = KindToggleLabels[i]
            };
            border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            _kindToggleBorders[i] = border;
            RefreshKindToggleIcon(i);
            border.MouseLeftButtonDown += (_, _) =>
            {
                SetKindToggleValue(capturedIdx, !GetKindToggleValue(capturedIdx));
            };

            var lbl = new TextBlock
            {
                Text              = KindToggleLabels[i],
                FontSize          = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(4, 0, 12, 0)
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

            KindToggleRow.Children.Add(border);
            KindToggleRow.Children.Add(lbl);
        }
    }

    private bool GetKindToggleValue(int idx) => idx switch
    {
        0 => _notifyVideo,
        1 => _notifyShort,
        _ => _notifyLive
    };

    private void SetKindToggleValue(int idx, bool value)
    {
        switch (idx)
        {
            case 0: _notifyVideo = value; break;
            case 1: _notifyShort = value; break;
            default: _notifyLive = value; break;
        }
        RefreshKindToggleIcon(idx);
        if (_detailTabPanels.Count == 3)
        {
            _detailTabPanels[idx].SetEnabled(value);
            SetDetailTabBorderStyle(_detailTabPanels[idx], idx == _selectedDetailTab);
        }
    }

    private void RefreshKindToggleIcon(int idx)
    {
        var border = _kindToggleBorders[idx];
        if (border == null) return;
        bool active = GetKindToggleValue(idx);
        border.SetResourceReference(Border.BackgroundProperty, active ? "PrimaryBrush" : "SurfaceElevatedBrush");
        border.Child = BuildAddWindowKindIcon(KindToggleLabels[idx], active);
    }

    private static UIElement BuildAddWindowKindIcon(string label, bool active)
    {
        var color = active
            ? (Brush)System.Windows.Application.Current.Resources["TextOnColorBrush"]
            : (Brush)System.Windows.Application.Current.Resources["TextMutedBrush"];

        if (label == "Short")
            return new System.Windows.Shapes.Path
            {
                Data    = Geometry.Parse(AddWindowShortIconPath),
                Width   = 18, Height = 18,
                Stretch = Stretch.Uniform,
                Fill    = color
            };

        if (label == "ライブ")
        {
            var canvas = new Canvas { Width = 24, Height = 24 };
            foreach (var d in new[] {
                "M4.9 16.1C1 12.2 1 5.8 4.9 1.9",
                "M7.8 4.7a6.14 6.14 0 0 0-.8 7.5",
                "M16.2 4.8c2 2 2.26 5.11.8 7.47",
                "M19.1 1.9a9.96 9.96 0 0 1 0 14.1",
                "M9.5 18h5",
                "m8 22 4-11 4 11"
            })
                canvas.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse(d), Stroke = color,
                    StrokeThickness = 2,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap   = PenLineCap.Round,
                    StrokeLineJoin     = PenLineJoin.Round,
                    Fill = Brushes.Transparent
                });
            var circle = new System.Windows.Shapes.Ellipse
            {
                Width = 4, Height = 4,
                Stroke = color, StrokeThickness = 2,
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(circle, 10);
            Canvas.SetTop(circle, 7);
            canvas.Children.Add(circle);
            return new Viewbox { Width = 18, Height = 18, Child = canvas };
        }

        // 動画
        var vc = new Canvas { Width = 22, Height = 22 };
        vc.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M2 6 L16 6 C17.105 6 18 6.895 18 8 L18 18 C18 19.105 17.105 20 16 20 L2 20 C0.895 20 0 19.105 0 18 L0 8 C0 6.895 0.895 6 2 6 Z"),
            Fill = color
        });
        vc.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M16 13 L21.223 16.482 A0.5 0.5 0 0 0 22 16.066 L22 7.87 A0.5 0.5 0 0 0 21.248 7.438 L16 10.5 Z"),
            Fill = color
        });
        return new Viewbox { Width = 18, Height = 18, Child = vc };
    }

    // ===== 詳細設定タブ =====
    private static readonly string[] DetailTabKindLabels = { "動画", "Short", "ライブ配信" };
    private static readonly VideoKind[] DetailTabKinds   = { VideoKind.Video, VideoKind.Short, VideoKind.Live };

    private void BuildDetailTabUI()
    {
        _detailTabPanels.Clear();
        DetailFocusTabNav.Children.Clear();
        DetailFocusTabContent.Children.Clear();
        _selectedDetailTab = 0;

        bool[] kindEnabled = { _notifyVideo, _notifyShort, _notifyLive };

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

            // 「この設定を有効にする」→ トグルアイコンに同期
            int capturedIdx = i;
            panel.OnEnabledChanged = () =>
            {
                SetKindToggleValue(capturedIdx, _detailTabPanels[capturedIdx].IsEnabled);
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
        AppLogger.Log(LogMsg.AddChannelDetailTabSwitched, null, DetailTabKindLabels[idx]);
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
