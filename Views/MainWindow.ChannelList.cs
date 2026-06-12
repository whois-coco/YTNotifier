using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    // ===== 編集モード =====
    private void EditModeButton_Click(object sender, RoutedEventArgs e)
    {
        _editMode = !_editMode;
        EditModeButton.Content = _editMode ? "✅" : "✏";
        EditModeButton.Style = (Style)Application.Current.Resources[_editMode ? "PrimaryButton" : "SecondaryButton"];

        foreach (var child in ChannelList.Children.OfType<Border>())
        {
            if (child.Tag is not ChannelInfo) continue;
            SetRowInteractive(child, !_editMode);
            SetDeleteButtonVisibility(child, _editMode);
        }

        LoggerService.Instance.Info(_editMode
            ? "編集モード開始：ドラッグでチャンネルを並び替えられます"
            : "編集モード終了");
    }

    private static void SetDeleteButtonVisibility(Border row, bool visible)
    {
        if (row.Child is not Grid outerGrid) return;
        var contentGrid = outerGrid.Children.OfType<Grid>().FirstOrDefault();
        if (contentGrid == null) return;

        // 通常モード: StackPanel内のButton（actionsパネル）
        foreach (var btn in contentGrid.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<Button>()))
            btn.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        // コンパクトモード: Grid直下のButton（削除ボタン）
        foreach (var btn in contentGrid.Children.OfType<Button>())
            btn.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        var handle = contentGrid.Children.OfType<Border>()
            .FirstOrDefault(b => b.Tag is string s && s == "DragHandle");
        if (handle != null)
        {
            handle.Opacity = visible ? 1.0 : 0.25;
            handle.Cursor  = visible ? Cursors.SizeAll : Cursors.Arrow;
        }
    }

    private static void SetRowInteractive(Border row, bool enabled)
    {
        if (row.Child is not Grid outerGrid) return;
        var contentGrid = outerGrid.Children.OfType<Grid>().FirstOrDefault();
        if (contentGrid == null) return;

        foreach (var el in contentGrid.Children.OfType<Border>().Where(b => b.Tag is string s && s == "IconBorder"))
        {
            el.IsHitTestVisible = enabled;
            el.Opacity          = enabled ? 1.0 : 0.5;
        }

        // 種別トグルは編集モード中のみ動作するが常にヒットテスト可能
        foreach (var toggle in contentGrid.Children.OfType<StackPanel>()
            .Where(p => Grid.GetColumn(p) == 2)
            .SelectMany(p => p.Children.OfType<StackPanel>())
            .SelectMany(p => p.Children.OfType<Border>()))
        {
            toggle.IsHitTestVisible = true;
            toggle.Opacity          = 1.0;
        }
    }

    private void ChannelSearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        // フォーカス時：テーマのSurfaceBrush（ライト=白、ダーク=紺）＋枠線をPrimaryBrushに
        ChannelSearchBox.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextPrimaryBrush"];
        if (ChannelSearchBox.Parent is System.Windows.Controls.Grid g &&
            g.Parent is System.Windows.Controls.Grid g2 &&
            g2.Parent is Border b)
        {
            SetDynamicBrush(b, Border.BackgroundProperty, "SurfaceBrush");
            SetDynamicBrush(b, Border.BorderBrushProperty, "PrimaryBrush");
        }
    }

    private void ChannelSearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        ChannelSearchBox.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextPrimaryBrush"];
        if (ChannelSearchBox.Parent is System.Windows.Controls.Grid g &&
            g.Parent is System.Windows.Controls.Grid g2 &&
            g2.Parent is Border b)
        {
            SetDynamicBrush(b, Border.BackgroundProperty, "SurfaceBrush");
            SetDynamicBrush(b, Border.BorderBrushProperty, "BorderBrush");
        }
    }

    private void SetSearchBorderBackground(System.Windows.Media.Brush brush)
    {
        if (ChannelSearchBox.Parent is System.Windows.Controls.Grid g &&
            g.Parent is System.Windows.Controls.Grid g2 &&
            g2.Parent is Border b)
            b.Background = brush;
    }

    private void ChannelSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var hasText = !string.IsNullOrEmpty(ChannelSearchBox.Text);
        SearchPlaceholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ChannelSearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ExecuteSearch();
        if (e.Key == Key.Escape) ClearSearch();
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_searchQuery)) ClearSearch();
        else ExecuteSearch();
    }

    private void SearchClearButton_Click(object sender, RoutedEventArgs e) => ClearSearch();

    private void ExecuteSearch()
    {
        _searchQuery = ChannelSearchBox.Text.Trim();
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(ChannelSearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        UpdateSearchButtonIcon(!string.IsNullOrEmpty(_searchQuery));
        RefreshChannelList();
    }

    private void ClearSearch()
    {
        ChannelSearchBox.Text        = "";
        _searchQuery                 = "";
        SearchPlaceholder.Visibility = Visibility.Visible;
        ChannelSearchBox.Foreground  = (System.Windows.Media.Brush)Application.Current.Resources["TextPrimaryBrush"];
        SetSearchBorderBackground((System.Windows.Media.Brush)Application.Current.Resources["SurfaceBrush"]);
        UpdateSearchButtonIcon(false);
        RefreshChannelList();
    }

    private void UpdateSearchButtonIcon(bool isSearching)
    {
        if (SearchButton.Template?.FindName("SearchIcon", SearchButton) is Viewbox icon &&
            icon.Child is System.Windows.Controls.Canvas canvas)
        {
            canvas.Children.Clear();
            if (isSearching)
            {
                foreach (var d in new[] { "M18 6 6 18", "M6 6l12 12" })
                    canvas.Children.Add(new System.Windows.Shapes.Path
                    {
                        Data = Geometry.Parse(d),
                        Stroke = (System.Windows.Media.Brush)Application.Current.Resources["TextMutedBrush"],
                        StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
                    });
                SearchButton.ToolTip = "クリア";
            }
            else
            {
                foreach (var (d, fill) in new (string, System.Windows.Media.Brush)[]
                {
                    ("M11 3a8 8 0 1 0 0 16A8 8 0 0 0 11 3z", System.Windows.Media.Brushes.Transparent),
                    ("m21 21-4.35-4.35",                      System.Windows.Media.Brushes.Transparent)
                })
                    canvas.Children.Add(new System.Windows.Shapes.Path
                    {
                        Data = Geometry.Parse(d),
                        Stroke = (System.Windows.Media.Brush)Application.Current.Resources["TextMutedBrush"],
                        StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                        Fill = fill
                    });
                SearchButton.ToolTip = "検索";
            }
        }
    }

    // ===== チャンネル一覧 =====

    // 監視中の連続 ChannelUpdated イベントを1回の RefreshChannelList にまとめる
    internal void ScheduleRefreshChannelList()
    {
        if (_refreshScheduled) return;
        _refreshScheduled = true;
        Dispatcher.BeginInvoke(() => { _refreshScheduled = false; RefreshChannelList(); },
            System.Windows.Threading.DispatcherPriority.Render);
    }

    private static int CalcCategoriesHashCode(List<CategoryInfo> cats)
    {
        var h = cats.Count;
        foreach (var c in cats) h = h * 31 + c.SortOrder;
        return h;
    }

    internal void RefreshChannelList()
    {
        var channels   = SettingsService.Instance.Channels;

        var rawCats  = SettingsService.Instance.Categories;
        var catsHash = CalcCategoriesHashCode(rawCats);
        if (_sortedCategories == null || catsHash != _categoriesHashCode)
        {
            _sortedCategories   = rawCats.OrderBy(c => c.SortOrder).ToList();
            _categoriesHashCode = catsHash;
        }
        var categories = _sortedCategories;

        ChannelList.Children.Clear();
        ChannelCountText.Text = $"{channels.Count} チャンネル";
        EmptyState.Visibility = channels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 検索モード: カテゴリなしで部分一致チャンネルのみ表示
        if (!string.IsNullOrEmpty(_searchQuery))
        {
            var matched = channels.Where(c =>
                c.ChannelName.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)).ToList();
            EmptyState.Visibility = matched.Count == 0 && channels.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
            foreach (var ch in matched) ChannelList.Children.Add(CreateChannelRow(ch));
            return;
        }

        foreach (var cat in categories)
        {
            var catChannels = channels.Where(c => c.CategoryId == cat.CategoryId).ToList();
            ChannelList.Children.Add(CreateCategoryRow(cat, catChannels.Count(c => c.HasUnread)));
            if (!cat.IsCollapsed)
                foreach (var ch in catChannels) ChannelList.Children.Add(CreateChannelRow(ch));
        }

        var uncategorized = channels.Where(c => string.IsNullOrEmpty(c.CategoryId)).ToList();
        if (uncategorized.Count > 0)
        {
            ChannelList.Children.Add(CreateUncategorizedRow(uncategorized.Count(c => c.HasUnread), _uncategorizedCollapsed));
            if (!_uncategorizedCollapsed)
                foreach (var ch in uncategorized) ChannelList.Children.Add(CreateChannelRow(ch));
        }

        if (_editMode)
            foreach (var child in ChannelList.Children.OfType<Border>().Where(b => b.Tag is ChannelInfo))
            {
                SetRowInteractive(child, false);
                SetDeleteButtonVisibility(child, true);
            }

        AutoAdjustIntervalForQuota();
    }

    // ===== カテゴリ行の共通ヘッダー生成 =====
    private Border CreateGroupHeaderRow(string label, int unreadCount, bool isCollapsed, object tag)
    {
        var settings = SettingsService.Instance.Settings;
        var rowHeight = settings.CompactMode ? CategoryRowHeightCompact : CategoryRowHeight;
        var row = new Border
        {
            Height              = rowHeight,
            Margin              = new Thickness(0, 3, 0, 1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius        = new CornerRadius(4),
            Cursor              = Cursors.Hand,
            Tag                 = tag
        };
        SetDynamicBrush(row, Border.BackgroundProperty, "SurfaceElevatedBrush");

        var arrow = new TextBlock
        {
            Text              = isCollapsed ? "▶" : "▼",
            FontSize          = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 6, 0)
        };
        SetDynamicBrush(arrow, TextBlock.ForegroundProperty, "TextMutedBrush");

        var nameText = new TextBlock
        {
            Text              = label,
            FontSize          = 11,
            FontWeight        = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        SetDynamicBrush(nameText, TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var badge = new Border
        {
            CornerRadius      = new CornerRadius(6),
            Padding           = new Thickness(5, 0, 5, 0),
            Margin            = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility        = unreadCount > 0 ? Visibility.Visible : Visibility.Collapsed,
            Child             = new TextBlock { Text = unreadCount.ToString(), FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Brushes.White }
        };
        SetDynamicBrush(badge, Border.BackgroundProperty, "AccentBrush");

        row.Child = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(8, 0, 8, 0),
            Children          = { arrow, nameText, badge }
        };
        return row;
    }

    private Border CreateUncategorizedRow(int unreadCount, bool isCollapsed)
    {
        var row = CreateGroupHeaderRow("未分類", unreadCount, isCollapsed, "uncategorized");
        row.MouseLeftButtonUp += (_, _) => { _uncategorizedCollapsed = !_uncategorizedCollapsed; RefreshChannelList(); };
        return row;
    }

    private Border CreateCategoryRow(CategoryInfo cat, int unreadCount)
    {
        var row = CreateGroupHeaderRow(cat.CategoryName, unreadCount, cat.IsCollapsed, cat);
        row.ContextMenu = BuildCategoryContextMenu(cat);

        row.MouseLeftButtonUp += (_, _) =>
        {
            cat.IsCollapsed = !cat.IsCollapsed;
            SettingsService.Instance.SaveCategories();
            RefreshChannelList();
        };

        System.Windows.Point catDragStart = default;
        bool catDragReady = false;

        row.PreviewMouseLeftButtonDown += (_, e) => { if (_editMode) { catDragStart = e.GetPosition(ChannelList); catDragReady = true; } };
        row.PreviewMouseMove += (s, e) =>
        {
            if (!_editMode || !catDragReady) return;
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) { catDragReady = false; return; }
            var diff = e.GetPosition(ChannelList) - catDragStart;
            if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            catDragReady = false;
            CacheCategoryBoundaries();
            if (s is Border b) DragDrop.DoDragDrop(b, new DataObject("CategoryDrag", cat.CategoryId), DragDropEffects.Move);
        };
        row.PreviewMouseLeftButtonUp += (_, _) => { catDragReady = false; };

        return row;
    }

    private void CacheCategoryBoundaries()
    {
        _catBoundaries.Clear();
        double cumY = 0;
        foreach (var child in ChannelList.Children.OfType<Border>())
        {
            double rowH = child.ActualHeight + child.Margin.Top + child.Margin.Bottom;
            if (child.Tag is CategoryInfo)
                _catBoundaries.Add((cumY, ChannelList.Children.IndexOf(child)));
            cumY += rowH;
        }
        if (_catBoundaries.Count > 0)
        {
            var last    = _catBoundaries[^1];
            var lastRow = ChannelList.Children[last.childIndex] as Border;
            if (lastRow != null)
            {
                double lastH = lastRow.ActualHeight + lastRow.Margin.Top + lastRow.Margin.Bottom;
                _catBoundaries.Add((last.y + lastH, last.childIndex + 1));
            }
        }
    }

    // ===== カテゴリ名入力ダイアログ =====
    private string? ShowCategoryNameDialog(string title, string defaultValue = "")
    {
        var dialog = new Window
        {
            Width = 360, Height = 160, MinWidth = 360, MaxWidth = 360, MinHeight = 160, MaxHeight = 160,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Background = System.Windows.Media.Brushes.Transparent, AllowsTransparency = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ShowInTaskbar = false,
        };

        var root = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
        SetDynamicBrush(root, Border.BackgroundProperty,  "SurfaceBrush");
        SetDynamicBrush(root, Border.BorderBrushProperty, "BorderBrush");

        var titleBar = new Border { Height = 38, Cursor = Cursors.SizeAll };
        SetDynamicBrush(titleBar, Border.BackgroundProperty, "SidebarBrush");
        titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) dialog.DragMove(); };
        var titleText = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        SetDynamicBrush(titleText, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        titleBar.Child = titleText;

        var inputBox = new TextBox { Text = defaultValue, Margin = new Thickness(0, 0, 0, 12) };
        inputBox.Style = (Style)Application.Current.Resources["ModernTextBox"];

        var btnRow = new Grid();
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var cancelBtn = new Button { Content = "キャンセル", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0) };
        cancelBtn.Style = (Style)Application.Current.Resources["SecondaryButton"];
        cancelBtn.Click += (_, _) => { dialog.DialogResult = false; dialog.Close(); };
        Grid.SetColumn(cancelBtn, 1);

        var okBtn = new Button { Content = "OK", Padding = new Thickness(14, 7, 14, 7) };
        okBtn.Style = (Style)Application.Current.Resources["PrimaryButton"];
        okBtn.Click += (_, _) => { dialog.DialogResult = true; dialog.Close(); };
        Grid.SetColumn(okBtn, 2);

        inputBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)  { dialog.DialogResult = true;  dialog.Close(); }
            if (e.Key == System.Windows.Input.Key.Escape) { dialog.DialogResult = false; dialog.Close(); }
        };

        btnRow.Children.Add(cancelBtn); btnRow.Children.Add(okBtn);
        var content = new StackPanel { Margin = new Thickness(16, 12, 16, 16), Children = { inputBox, btnRow } };
        root.Child = new StackPanel { Children = { titleBar, content } };
        dialog.Content = root;

        dialog.Loaded += (_, _) => { inputBox.SelectAll(); inputBox.Focus(); };
        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(inputBox.Text)
            ? inputBox.Text.Trim() : null;
    }

    private ContextMenu BuildCategoryContextMenu(CategoryInfo cat)
    {
        var menu = new ContextMenu();

        var clearNewItem = new MenuItem { Header = "🔔 NEWバッジを全て消す" };
        clearNewItem.Click += (_, _) =>
        {
            var channels = SettingsService.Instance.Channels
                .Where(c => c.CategoryId == cat.CategoryId && c.HasUnread).ToList();
            foreach (var c in channels) c.HasUnread = false;
            if (channels.Count > 0)
            {
                SettingsService.Instance.SaveChannels();
                RefreshChannelList();
            }
        };

        var expandAllItem = new MenuItem { Header = "▼ 全てのカテゴリを展開" };
        expandAllItem.Click += (_, _) =>
        {
            foreach (var c in SettingsService.Instance.Categories) c.IsCollapsed = false;
            _uncategorizedCollapsed = false;
            SettingsService.Instance.SaveCategories();
            RefreshChannelList();
        };

        var collapseAllItem = new MenuItem { Header = "▶ 全てのカテゴリを閉じる" };
        collapseAllItem.Click += (_, _) =>
        {
            foreach (var c in SettingsService.Instance.Categories) c.IsCollapsed = true;
            _uncategorizedCollapsed = true;
            SettingsService.Instance.SaveCategories();
            RefreshChannelList();
        };

        var renameItem = new MenuItem { Header = "✏ カテゴリ名を変更" };
        renameItem.Click += (_, _) =>
        {
            var newName = ShowCategoryNameDialog("カテゴリ名を変更", cat.CategoryName);
            if (newName != null) { SettingsService.Instance.RenameCategory(cat.CategoryId, newName); RefreshChannelList(); }
        };

        var deleteItem = new MenuItem { Header = "🗑 カテゴリを削除" };
        deleteItem.Click += (_, _) =>
        {
            if (ConfirmDialog.Show(Application.Current.MainWindow as Window ?? this, "カテゴリ削除", $"「{cat.CategoryName}」を削除しますか？\nチャンネルは未分類に移動されます。", "削除") == true)
            {
                SettingsService.Instance.RemoveCategory(cat.CategoryId);
                RefreshChannelList();
            }
        };

        menu.Items.Add(clearNewItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(expandAllItem);
        menu.Items.Add(collapseAllItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(renameItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(deleteItem);

        menu.Opened += (_, _) =>
        {
            var hasUnread = SettingsService.Instance.Channels
                .Any(c => c.CategoryId == cat.CategoryId && c.HasUnread);
            clearNewItem.IsEnabled = hasUnread; clearNewItem.Opacity = hasUnread ? 1.0 : 0.4;
            expandAllItem.IsEnabled   = true; expandAllItem.Opacity   = 1.0;
            collapseAllItem.IsEnabled = true; collapseAllItem.Opacity = 1.0;
            renameItem.Visibility  = _editMode ? Visibility.Visible : Visibility.Collapsed;
            deleteItem.Visibility  = _editMode ? Visibility.Visible : Visibility.Collapsed;
            // 1つ目のセパレータは常に表示、残りは編集モードのみ
            var seps = menu.Items.OfType<Separator>().ToList();
            for (int i = 0; i < seps.Count; i++)
                seps[i].Visibility = (i == 0 || _editMode) ? Visibility.Visible : Visibility.Collapsed;
        };
        return menu;
    }

    // ===== クォータ =====
    private void AutoAdjustIntervalForQuota()
    {
        var s        = SettingsService.Instance.Settings;
        var channels = SettingsService.Instance.Channels;
        if (channels.Count == 0) return;

        var (safe, recommended) = ApiQuotaHelper.ValidateInterval(s.CheckIntervalMinutes, channels);
        if (!safe)
        {
            var recItem = IntervalComboBox?.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag?.ToString() == recommended.ToString());
            if (recItem != null && IntervalComboBox != null)
                IntervalComboBox.SelectedItem = recItem;
            LoggerService.Instance.Warning($"チャンネル数増加によりAPI超過リスク → 監視間隔を{recommended}分に自動調整");
        }
        UpdateQuotaInfo();
    }

    private void UpdateQuotaInfo()
    {
        try
        {
            if (QuotaInfoText == null) return;
            var channels = SettingsService.Instance.Channels;
            var interval = SettingsService.Instance.Settings.CheckIntervalMinutes;
            var daily    = ApiQuotaHelper.EstimateDailyUnitsForChannels(interval, channels);
            var pct      = Math.Min(daily * 100.0 / ApiQuotaHelper.DailyLimit, 100.0);

            QuotaInfoText.Text    = $"{daily:N0} / {ApiQuotaHelper.DailyLimit:N0} ユニット/日";
            QuotaPercentText.Text = $"{pct:F0}%";

            ApplyQuotaBar(QuotaBar, QuotaBarBg, QuotaInfoText, QuotaPercentText, pct);
            UpdateIntervalComboBoxItems(channels.Count);

            // 当日実使用量バー
            var s          = SettingsService.Instance.Settings;
            var today      = DateTime.Today.ToString("yyyy-MM-dd");
            var actualUnits = s.TodayApiDate == today ? s.TodayApiUnits : 0;
            var actualPct   = Math.Min(actualUnits * 100.0 / ApiQuotaHelper.DailyLimit, 100.0);
            ActualQuotaInfoText.Text = $"{actualUnits:N0} / {ApiQuotaHelper.DailyLimit:N0} ユニット/日";
            ActualQuotaPercentText.Text = $"{(int)Math.Round(actualPct)}%";
            ApplyQuotaBar(ActualQuotaBar, ActualQuotaBarBg, ActualQuotaInfoText, ActualQuotaPercentText, actualPct);
        }
        catch { }
    }

    // プログレスバー表示の共通処理（bar/barBg をキャプチャして SizeChanged にも対応）
    private void ApplyQuotaBar(
        Border bar, Border barBg,
        TextBlock? infoText, TextBlock percentText,
        double pct)
    {
        // 表示と同じ四捨五入した値で色判定
        var pctRounded = (int)Math.Round(pct);
        var barColor = pctRounded >= 86 ? System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44)   // 赤
                     : pctRounded >= 76 ? System.Windows.Media.Color.FromRgb(0xEA, 0xB3, 0x08)   // オレンジ
                     : pctRounded >= 61 ? System.Windows.Media.Color.FromRgb(0xFF, 0xD7, 0x00)   // 黄
                                 : System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E);
        bar.Background = new System.Windows.Media.SolidColorBrush(barColor);
        barBg.Tag = pct;

        void SetBarWidth()
        {
            var maxW = barBg.ActualWidth;
            if (maxW <= 0) return;
            bar.Width = Math.Max(0, maxW * (barBg.Tag is double d ? d : pct) / 100.0);
        }

        // 既存ハンドラを除去してから再登録（多重登録防止）
        if (barBg.Tag is double && _quotaBarHandlers.TryGetValue(barBg, out var prev))
            barBg.SizeChanged -= prev;
        SizeChangedEventHandler handler = (_, _) => SetBarWidth();
        _quotaBarHandlers[barBg] = handler;
        barBg.SizeChanged += handler;

        if (barBg.ActualWidth > 0) SetBarWidth();
        else Dispatcher.BeginInvoke(SetBarWidth, System.Windows.Threading.DispatcherPriority.Render);

        var textColor = pctRounded >= 86 ? "ErrorBrush" : pctRounded >= 76 ? "WarningBrush" : pctRounded >= 61 ? "WarningBrush" : "SuccessBrush";
        if (infoText != null) SetDynamicBrush(infoText, TextBlock.ForegroundProperty, textColor);
        SetDynamicBrush(percentText, TextBlock.ForegroundProperty, textColor);
    }

    // barBg ごとの SizeChanged ハンドラを記録（多重登録防止用）
    private readonly Dictionary<Border, SizeChangedEventHandler> _quotaBarHandlers = new();

    private void UpdateIntervalComboBoxItems(int channelCount)
    {
        if (IntervalComboBox == null) return;
        var channels = SettingsService.Instance.Channels.Where(c => c.IsEnabled).ToList();
        foreach (System.Windows.Controls.ComboBoxItem item in IntervalComboBox.Items)
        {
            if (item.Tag is string tagStr && int.TryParse(tagStr, out int mins))
            {
                var cost = ApiQuotaHelper.EstimateDailyUnitsForChannels(mins, channels);
                var over = cost > ApiQuotaHelper.DailyLimit;
                item.IsEnabled = !over;
                item.ToolTip   = over ? $"クォータ超過（{cost:N0} / {ApiQuotaHelper.DailyLimit:N0} ユニット/日）" : $"{cost:N0} ユニット/日";
                item.Opacity   = over ? 0.4 : 1.0;
            }
        }
    }

    // ===== チャンネル行生成 =====
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
        row.MouseEnter += (_, _) => { if (!_isDragging) SetDynamicBrush(row, Border.BackgroundProperty, "HoverBrush"); };
        row.MouseLeave += (_, _) => SetDynamicBrush(row, Border.BackgroundProperty, "SurfaceAltBrush");
        row.ContextMenu = BuildChannelContextMenu(ch);

        // 共通: [4px新着帯] + コンテンツ列
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

        if (compact)
        {
            row.MouseLeftButtonUp += async (s, e) =>
            {
                if (!_editMode && s is Border b && b.Tag is ChannelInfo c && !c.IsTestChannel) { e.Handled = true; await OpenChannelLatestVideoAsync(c); }
            };

            var grid = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(grid, 1);

            var handle   = BuildDragHandle(compact: true,  editMode: _editMode);
            var icon     = BuildIconBorderCompact(ch);
            var nameText = new TextBlock
            {
                Text = ch.IsTestChannel ? $"⚗ {ch.ChannelName}" : ch.ChannelName,
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            SetDynamicBrush(nameText, TextBlock.ForegroundProperty, "TextPrimaryBrush");

            // 削除ボタン（コンパクト + 編集モードONの時のみ表示）
            var deleteBtn = BuildCompactDeleteButton(ch);
            deleteBtn.Visibility = (_editMode) ? Visibility.Visible : Visibility.Collapsed;

            Grid.SetColumn(handle, 0); Grid.SetColumn(icon, 1); Grid.SetColumn(nameText, 2); Grid.SetColumn(deleteBtn, 3);
            AttachHandleDragEvents(handle, row, ch);
            grid.Children.Add(handle); grid.Children.Add(icon); grid.Children.Add(nameText); grid.Children.Add(deleteBtn);
            outerGrid.Children.Add(newBar); outerGrid.Children.Add(grid);
        }
        else
        {
            var grid = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(grid, 1);

            var handle  = BuildDragHandle(compact: false, editMode: _editMode);
            var icon    = BuildIconBorder(ch);
            var info    = BuildInfoPanel(ch);
            var actions = BuildActionsPanel(ch);

            Grid.SetColumn(handle, 0); Grid.SetColumn(icon, 1); Grid.SetColumn(info, 2); Grid.SetColumn(actions, 3);
            AttachHandleDragEvents(handle, row, ch);
            grid.Children.Add(handle); grid.Children.Add(icon); grid.Children.Add(info); grid.Children.Add(actions);
            outerGrid.Children.Add(newBar); outerGrid.Children.Add(grid);
        }

        row.Child = outerGrid;
        return row;
    }

    private Button BuildCompactDeleteButton(ChannelInfo ch)
    {
        var btn = new Button
        {
            Width = 28, Height = 28,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand, Margin = new Thickness(4, 0, 0, 0),
            ToolTip = $"{ch.ChannelName} を削除"
        };
        var icon = new TextBlock
        {
            Text = "🗑", FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44))
        };
        btn.Content = icon;
        btn.Click += (_, e) =>
        {
            e.Handled = true;
            var result = ConfirmDialog.Show(
                Application.Current.MainWindow as Window ?? this,
                "チャンネル削除",
                $"「{ch.ChannelName}」を削除しますか？",
                "削除");
            if (result != true) return;
            SettingsService.Instance.RemoveChannel(ch.ChannelId);
            RefreshChannelList();
        };
        return btn;
    }

    private static Border BuildDragHandle(bool compact, bool editMode)
    {
        var dots = new TextBlock
        {
            Text = "⠿", FontSize = compact ? 13 : 15,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
        };
        SetDynamicBrush(dots, TextBlock.ForegroundProperty, "TextMutedBrush");
        return new Border
        {
            VerticalAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Center,
            Width = compact ? 18 : 20, Cursor = editMode ? Cursors.SizeAll : Cursors.Arrow, Tag = "DragHandle",
            Background = Brushes.Transparent, Opacity = editMode ? 1.0 : 0.25,
            ToolTip = "ドラッグして並び替え", Child = dots
        };
    }

    private void AttachHandleDragEvents(Border handle, Border row, ChannelInfo ch)
    {
        System.Windows.Point dragStartPos = default;
        bool dragReady = false;

        handle.MouseLeftButtonDown += (_, e) =>
        {
            if (_isDragging || !_editMode) return;
            dragStartPos = e.GetPosition(ChannelList);
            dragReady    = true;
            _dragSource  = ch;
            handle.CaptureMouse();
        };

        handle.MouseMove += (_, e) =>
        {
            if (!dragReady || e.LeftButton != MouseButtonState.Pressed) return;
            var pos  = e.GetPosition(ChannelList);
            var diff = pos - dragStartPos;

            if (!_isDragging)
            {
                if (Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _isDragging    = true;
                _dragSourceRow = row;
                _animDropIndex = -1;
                Mouse.OverrideCursor = Cursors.SizeNS;
                InitRowTransforms();
            }

            if (_dragSourceRow?.RenderTransform is TranslateTransform srcTt)
                srcTt.Y = pos.Y - dragStartPos.Y;

            var catId  = _dragSource?.CategoryId;
            var relIdx = CalcSwapIndex(pos.Y, catId);
            UpdateSwapAnimation(relIdx);
        };

        handle.MouseLeftButtonUp += (_, e) =>
        {
            if (!dragReady && !_isDragging) return;
            var wasDragging = _isDragging;
            dragReady   = false;
            _isDragging = false;
            handle.ReleaseMouseCapture();
            Mouse.OverrideCursor = null;

            if (wasDragging && _dragSourceRow != null)
            {
                var catId      = _dragSource?.CategoryId;
                var currentPos = e.GetPosition(ChannelList);
                var relIdx     = CalcSwapIndex(currentPos.Y, catId);
                CommitSwap(ch, catId, relIdx);
            }
            else
            {
                ResetRowTransforms(commit: false);
                _dragSourceRow = null;
                _dragSource    = null;
                _animDropIndex = -1;
            }
        };

        handle.LostMouseCapture += (_, _) =>
        {
            if (!_isDragging) return;
            ResetRowTransforms(commit: false);
            _dragSourceRow = null; _dragSource = null;
            _animDropIndex = -1; _isDragging = false;
            Mouse.OverrideCursor = null;
        };
    }
    // ===== 入れ替えアニメーション =====

    // ドラッグ開始時に同カテゴリ全行へ TranslateTransform を付与
    private void InitRowTransforms()
    {
        var catId = _dragSource?.CategoryId;
        foreach (var b in ChannelList.Children.OfType<Border>()
            .Where(b => b.Tag is ChannelInfo ch && ch.CategoryId == catId))
        {
            if (b.RenderTransform is TranslateTransform tt) tt.Y = 0;
            else b.RenderTransform = new TranslateTransform(0, 0);
        }
    }

    // マウスY座標から「ドラッグ元が落ち着くべき同カテゴリ内インデックス」を返す
    private int CalcSwapIndex(double mouseY, string? catId)
    {
        var rows = ChannelList.Children.OfType<Border>()
            .Where(b => b.Tag is ChannelInfo ch && ch.CategoryId == catId)
            .ToList();
        if (rows.Count == 0) return -1;

        // 各行の自然な中心Y（TranslateTransform を除いた位置）
        double cumY = 0;
        var centers = new List<double>();
        foreach (var child in ChannelList.Children.OfType<Border>())
        {
            double rowH = child.ActualHeight + child.Margin.Top + child.Margin.Bottom;
            if (rows.Contains(child))
                centers.Add(cumY + rowH / 2);
            cumY += rowH;
        }

        // マウス位置に最も近い中心を持つ行のインデックス
        int best = 0; double bestDist = double.MaxValue;
        for (int i = 0; i < centers.Count; i++)
        {
            double d = Math.Abs(mouseY - centers[i]);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    // ドラッグ元以外の行を「ドラッグ元が best の位置にいる」ように見せるアニメーション
    private void UpdateSwapAnimation(int best)
    {
        if (best < 0 || best == _animDropIndex) return;
        _animDropIndex = best;

        if (_dragSourceRow == null) return;
        var catId = (_dragSourceRow.Tag as ChannelInfo)?.CategoryId;
        var rows  = ChannelList.Children.OfType<Border>()
            .Where(b => b.Tag is ChannelInfo ch && ch.CategoryId == catId)
            .ToList();

        var srcIdx = rows.IndexOf(_dragSourceRow);
        if (srcIdx < 0) return;

        var duration = new Duration(TimeSpan.FromMilliseconds(150));
        double rowH  = _dragSourceRow.ActualHeight + _dragSourceRow.Margin.Bottom;

        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i] == _dragSourceRow) continue;
            if (rows[i].RenderTransform is not TranslateTransform tt)
            {
                tt = new TranslateTransform(0, 0);
                rows[i].RenderTransform = tt;
            }

            // srcIdx → best に動いたとき、間にある行を反対方向へずらす
            double target = 0;
            if (srcIdx < best && i > srcIdx && i <= best) target = -rowH;
            else if (srcIdx > best && i >= best && i < srcIdx) target = rowH;

            if (Math.Abs(tt.Y - target) < 1) continue;
            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(tt.Y, target, duration)
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    // ドロップ確定または キャンセル時に全行リセット
    private void ResetRowTransforms(bool commit)
    {
        var catId = (_dragSourceRow?.Tag as ChannelInfo)?.CategoryId;
        foreach (var b in ChannelList.Children.OfType<Border>()
            .Where(b => b.Tag is ChannelInfo ch && ch.CategoryId == catId))
        {
            if (b.RenderTransform is not TranslateTransform tt) continue;
            // 確定時は即時リセット（アニメーション中の状態でRefreshが走るのを防ぐ）
            tt.BeginAnimation(TranslateTransform.YProperty, null);
            tt.Y = 0;
        }
    }

    // データを実際に並び替えてリストを再描画
    private void CommitSwap(ChannelInfo srcCh, string? catId, int destRelIdx)
    {
        var channels = SettingsService.Instance.Channels;
        var sameCat  = channels
            .Select((c, i) => (c, i))
            .Where(t => t.c.CategoryId == catId)
            .ToList();

        var srcCatIdx = sameCat.FindIndex(t => t.c.ChannelId == srcCh.ChannelId);

        if (srcCatIdx < 0 || destRelIdx < 0 || destRelIdx == srcCatIdx)
        {
            ResetRowTransforms(commit: false);
            _dragSourceRow = null; _dragSource = null;
            _animDropIndex = -1; _isDragging = false;
            return;
        }

        // 同カテゴリ内で移動
        var (moving, movingGlobal) = sameCat[srcCatIdx];
        channels.RemoveAt(movingGlobal);

        // RemoveAt後に同カテゴリのインデックスを再取得
        var updated = channels.Select((c, i) => (c, i)).Where(t => t.c.CategoryId == catId).ToList();

        // destRelIdx は「移動先の行インデックス」（CalcSwapIndex が返す最近傍行）
        // srcより下に移動する場合: destの後ろに挿入（Remove後インデックスは1つずれる）
        // srcより上に移動する場合: destの前に挿入
        int insertIdx;
        if (destRelIdx > srcCatIdx)
        {
            // 下移動: destRelIdx行の後ろ → Remove後は destRelIdx-1 の後ろ
            var afterIdx = destRelIdx - 1;
            insertIdx = afterIdx < updated.Count ? updated[afterIdx].i + 1 : channels.Count;
        }
        else
        {
            // 上移動: destRelIdx行の前
            insertIdx = destRelIdx < updated.Count ? updated[destRelIdx].i : channels.Count;
        }
        channels.Insert(Math.Clamp(insertIdx, 0, channels.Count), moving);

        // Transform を即時リセットしてから再描画
        ResetRowTransforms(commit: true);
        _dragSourceRow = null; _dragSource = null;
        _animDropIndex = -1; _isDragging = false;

        // 確定後にデバッグログを削除してリフレッシュ
        SettingsService.Instance.SaveChannels();
        Dispatcher.Invoke(RefreshChannelList, System.Windows.Threading.DispatcherPriority.Render);
    }

    private static Border BuildIconBorderCompact(ChannelInfo ch)
    {
        const int size = 22;
        var b = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
            VerticalAlignment = VerticalAlignment.Center,
            Clip = new EllipseGeometry(new System.Windows.Point(size / 2.0, size / 2.0), size / 2.0, size / 2.0),
            Tag  = "IconBorder"
        };
        var img = GetCachedIcon(ch.ThumbnailUrl);
        if (img != null) b.Child = new System.Windows.Controls.Image { Source = img, Stretch = Stretch.UniformToFill };
        return b;
    }

    private static Border BuildIconBorder(ChannelInfo ch)
    {
        var b = new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(22),
            Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
            Clip   = new EllipseGeometry(new System.Windows.Point(22, 22), 22, 22),
            Cursor = ch.IsTestChannel ? Cursors.Arrow : Cursors.Hand,
            ToolTip = ch.IsTestChannel ? null : "クリックして最新動画を開く",
            Tag = "IconBorder"
        };
        b.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true;
        if (!ch.IsTestChannel)
            b.MouseLeftButtonUp += async (_, _) => await OpenChannelLatestVideoAsync(ch);
        var img = GetCachedIcon(ch.ThumbnailUrl);
        if (img != null) b.Child = new System.Windows.Controls.Image { Source = img, Stretch = Stretch.UniformToFill };
        return b;
    }

    private static StackPanel BuildInfoPanel(ChannelInfo ch)
    {
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };

        var nameText = new TextBlock
        {
            Text = ch.ChannelName, FontSize = 13, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        SetDynamicBrush(nameText, TextBlock.ForegroundProperty, "TextPrimaryBrush");

        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(nameText);
        if (ch.IsTestChannel)
        {
            var badge = new Border
            {
                Background        = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7C, 0x3A, 0xED)),
                CornerRadius      = new CornerRadius(3),
                Padding           = new Thickness(4, 1, 4, 1),
                Margin            = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            badge.Child = new TextBlock
            {
                Text       = "TEST",
                FontSize   = 9,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            };
            nameRow.Children.Add(badge);
        }
        info.Children.Add(nameRow);

        var kindRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        kindRow.Children.Add(MakeKindToggle("動画",  ch.NotifyVideo, ch.MonitorMode, VideoKind.Video,  v => { ch.NotifyVideo = v; SettingsService.Instance.UpdateChannel(ch); }));
        kindRow.Children.Add(MakeKindToggle("Short", ch.NotifyShort, ch.MonitorMode, VideoKind.Short,  v => { ch.NotifyShort = v; SettingsService.Instance.UpdateChannel(ch); }));
        kindRow.Children.Add(MakeKindToggle("ライブ", ch.NotifyLive,  ch.MonitorMode, VideoKind.Live,   v => { ch.NotifyLive  = v; SettingsService.Instance.UpdateChannel(ch); }));
        info.Children.Add(kindRow);

        return info;
    }

    private static StackPanel BuildActionsPanel(ChannelInfo ch)
    {
        var trashCanvas = new Canvas { Width = 24, Height = 24 };
        foreach (var d in new[] { "M10 11v6", "M14 11v6", "M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6", "M3 6h18", "M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" })
        {
            var p = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(d), StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round, Fill = Brushes.Transparent
            };
            SetDynamicBrush(p, System.Windows.Shapes.Path.StrokeProperty, "ErrorBrush");
            trashCanvas.Children.Add(p);
        }

        var delBtn = new Button
        {
            Content = new Viewbox { Width = 15, Height = 15, Child = trashCanvas },
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(6), Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
            Tag = ch, ToolTip = "削除"
        };
        delBtn.Click += DeleteChannel_Click;
        return new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { delBtn } };
    }

    private ContextMenu BuildChannelContextMenu(ChannelInfo ch)
    {
        var menu = new ContextMenu();

        var clearItem = new MenuItem { Header = "🔔 NEWバッジを消す" };
        clearItem.Click += (_, _) => { ch.HasUnread = false; SettingsService.Instance.UpdateChannel(ch); RefreshChannelList(); };

        // テストチャンネル専用: 状態リセット
        var resetTestItem    = new MenuItem { Header = "⚗ テスト状態をリセット" };
        var sepResetTest     = new Separator();
        resetTestItem.Visibility = ch.IsTestChannel ? Visibility.Visible : Visibility.Collapsed;
        sepResetTest.Visibility  = ch.IsTestChannel ? Visibility.Visible : Visibility.Collapsed;
        resetTestItem.Click += (_, _) =>
        {
            ch.TestStateIndex      = 0;
            ch.LastCheckedVideoId  = string.Empty;
            ch.PendingUpcomingVideoIds.Clear();
            SettingsService.Instance.UpdateChannel(ch);
            RefreshChannelList();
            LoggerService.Instance.Info("テスト状態をリセットしました", ch.ChannelName);
        };

        var renameItem = new MenuItem { Header = "✏ 名称を変更" };
        renameItem.Click += (_, _) => ShowRenameDialog(ch);

        var sepRename = new Separator();

        var newCatItem = new MenuItem { Header = "📁 カテゴリを作成して移動" };
        newCatItem.Click += (_, _) =>
        {
            var catName = ShowCategoryNameDialog("新規カテゴリを作成");
            if (catName != null) { var cat = SettingsService.Instance.AddCategory(catName); SettingsService.Instance.SetChannelCategory(ch.ChannelId, cat.CategoryId); RefreshChannelList(); }
        };

        var moveToCatItem = new MenuItem { Header = "📂 カテゴリを移動" };
        var sep = new Separator();

        var detailItem = new MenuItem { Header = "⚙ 詳細設定" };
        detailItem.Click += (_, _) =>
        {
            var win = new ChannelDetailWindow(ch, this);
            if (win.ShowDialog() == true) { RefreshChannelList(); UpdateQuotaInfo(); }
        };

        menu.Items.Add(clearItem);
        menu.Items.Add(resetTestItem);
        menu.Items.Add(sepResetTest);
        menu.Items.Add(renameItem);
        menu.Items.Add(sepRename);
        menu.Items.Add(newCatItem);
        menu.Items.Add(moveToCatItem);
        menu.Items.Add(sep);
        menu.Items.Add(detailItem);

        menu.Opened += (_, _) =>
        {
            var vis = _editMode ? Visibility.Visible : Visibility.Collapsed;
            clearItem.Visibility     = _editMode ? Visibility.Collapsed : Visibility.Visible;
            renameItem.Visibility    = vis; sepRename.Visibility    = vis;
            newCatItem.Visibility    = vis; moveToCatItem.Visibility = vis;
            sep.Visibility           = vis; detailItem.Visibility   = vis;

            moveToCatItem.Items.Clear();
            var categories = SettingsService.Instance.Categories;
            foreach (var cat in categories.OrderBy(c => c.SortOrder))
            {
                var item = new MenuItem { Header = cat.CategoryName, Tag = (ch, cat) };
                item.Click += (s, _) =>
                {
                    if (s is MenuItem mi && mi.Tag is (ChannelInfo c, CategoryInfo ca))
                    { SettingsService.Instance.SetChannelCategory(c.ChannelId, ca.CategoryId); RefreshChannelList(); }
                };
                moveToCatItem.Items.Add(item);
            }
            var uncatItem = new MenuItem { Header = "（未分類）" };
            uncatItem.Click += (_, _) => { SettingsService.Instance.SetChannelCategory(ch.ChannelId, null); RefreshChannelList(); };
            if (categories.Count > 0) moveToCatItem.Items.Add(new Separator());
            moveToCatItem.Items.Add(uncatItem);
        };

        return menu;
    }

    // ===== 種別トグル =====
    private static UIElement MakeKindToggle(string label, bool initial,
        MonitorMode mode, VideoKind kind,
        Action<bool> onChanged)
    {
        // アクティブ時の背景色: 通常=青、低頻度=黄、時間指定=緑（種別ごと）
        string ActiveBrushKey(bool on)
        {
            if (!on) return "SurfaceElevatedBrush";
            return mode switch
            {
                YTNotifier.Models.MonitorMode.LowFreq => "PrimaryBrush",
                YTNotifier.Models.MonitorMode.Focus   => "SuccessBrush",
                _                                     => "PrimaryBrush"
            };
        }

        var border = new Border
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Arrow,
            BorderThickness = new Thickness(1), ToolTip = label
        };
        SetDynamicBrush(border, Border.BorderBrushProperty, "BorderBrush");
        SetDynamicBrush(border, Border.BackgroundProperty, ActiveBrushKey(initial));
        border.Child = BuildKindIcon(label, initial);

        bool current = initial;
        border.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (Application.Current.MainWindow is not MainWindow win || !win._editMode) return;
            e.Handled = true;
            current = !current;
            onChanged(current);
            SetDynamicBrush(border, Border.BackgroundProperty, ActiveBrushKey(current));
            border.Child = BuildKindIcon(label, current);
        };
        return border;
    }

    private static UIElement BuildKindIcon(string label, bool active)
    {
        var color = active ? Brushes.White : Brushes.Gray;

        if (label == "Short")
            return new System.Windows.Shapes.Path { Data = Geometry.Parse(ShortIconPath), Width = 18, Height = 18, Stretch = Stretch.Uniform, Fill = color };

        if (label == "ライブ")
        {
            var canvas = new Canvas { Width = 24, Height = 24 };
            foreach (var d in new[] { "M4.9 16.1C1 12.2 1 5.8 4.9 1.9", "M7.8 4.7a6.14 6.14 0 0 0-.8 7.5", "M16.2 4.8c2 2 2.26 5.11.8 7.47", "M19.1 1.9a9.96 9.96 0 0 1 0 14.1", "M9.5 18h5", "m8 22 4-11 4 11" })
                canvas.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(d), Stroke = color, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Fill = Brushes.Transparent });
            var circle = new System.Windows.Shapes.Ellipse { Width = 4, Height = 4, Stroke = color, StrokeThickness = 2, Fill = Brushes.Transparent };
            Canvas.SetLeft(circle, 10); Canvas.SetTop(circle, 7);
            canvas.Children.Add(circle);
            return new Viewbox { Width = 18, Height = 18, Child = canvas };
        }

        // 動画
        var vc = new Canvas { Width = 22, Height = 22 };
        vc.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M2 6 L16 6 C17.105 6 18 6.895 18 8 L18 18 C18 19.105 17.105 20 16 20 L2 20 C0.895 20 0 19.105 0 18 L0 8 C0 6.895 0.895 6 2 6 Z"), Fill = color });
        vc.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M16 13 L21.223 16.482 A0.5 0.5 0 0 0 22 16.066 L22 7.87 A0.5 0.5 0 0 0 21.248 7.438 L16 10.5 Z"), Fill = color });
        return new Viewbox { Width = 18, Height = 18, Child = vc };
    }

    // ===== チャンネル操作 =====
    public static async Task OpenChannelLatestVideoFromToastAsync(ChannelInfo ch)
        => await OpenChannelLatestVideoAsync(ch);

    private static async Task OpenChannelLatestVideoAsync(ChannelInfo ch)
    {
        if (ch.HasUnread)
        {
            ch.HasUnread = false;
            SettingsService.Instance.UpdateChannel(ch);
            Application.Current.Dispatcher.Invoke(() => (Application.Current.MainWindow as MainWindow)?.RefreshChannelList());
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
                var result = await YouTubeApiClient.Instance.FetchLatestAllowedVideoAsync(
                    ch.ChannelId, ch.NotifyVideo, ch.NotifyShort, ch.NotifyLive,
                    ch.UploadsPlaylistId);

                if (result.HasValue)
                {
                    var (videoId, kind) = result.Value;
                    if (kind == VideoKind.Video && videoId != null) { ch.LastVideoId = videoId; SettingsService.Instance.UpdateChannel(ch); }
                    url = videoId != null
                        ? $"https://www.youtube.com/watch?v={videoId}"
                        : !string.IsNullOrEmpty(ch.LastVideoId) ? $"https://www.youtube.com/watch?v={ch.LastVideoId}" : ch.ChannelUrl;
                    LoggerService.Instance.Info($"最新{kind.ToLabel()}を開きます", ch.ChannelName);
                }
                else
                {
                    url = !string.IsNullOrEmpty(ch.LastVideoId) ? $"https://www.youtube.com/watch?v={ch.LastVideoId}" : ch.ChannelUrl;
                    LoggerService.Instance.Info("対象動画が見つかりませんでした", ch.ChannelName);
                }
            }
            catch
            {
                url = !string.IsNullOrEmpty(ch.LastVideoId) ? $"https://www.youtube.com/watch?v={ch.LastVideoId}" : ch.ChannelUrl;
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

    private static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void ShowRenameDialog(ChannelInfo ch)
    {
        var name = ShowCategoryNameDialog("名称を変更", ch.ChannelName);
        if (name == null) return;
        ch.ChannelName = name;
        SettingsService.Instance.UpdateChannel(ch);
        LoggerService.Instance.Info($"名称を変更しました → {name}", ch.ChannelName);
        RefreshChannelList();
    }

    private static void DeleteChannel_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button btn || btn.Tag is not ChannelInfo ch) return;
        if (ConfirmDialog.Show(Application.Current.MainWindow as Window, "チャンネルを削除", $"「{ch.ChannelName}」を削除しますか？", "削除") != true) return;
        SettingsService.Instance.RemoveChannel(ch.ChannelId);
        LoggerService.Instance.Info("チャンネルを削除しました", ch.ChannelName);
        (Application.Current.MainWindow as MainWindow)?.RefreshChannelList();
    }

    // ===== ドラッグアンドドロップ =====


    private void ShowDropIndicator(int insertIndex)
    {
        if (_dropIndicatorIndex == insertIndex) return;
        HideDropIndicator();
        _dropIndicatorIndex = insertIndex;
        _dropIndicator = new Border
        {
            Height = 2, HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible = false, Margin = new Thickness(8, 0, 8, 0)
        };
        SetDynamicBrush(_dropIndicator, Border.BackgroundProperty, "PrimaryBrush");
        ChannelList.Children.Insert(Math.Min(insertIndex, ChannelList.Children.Count), _dropIndicator);
    }

    private void HideDropIndicator()
    {
        if (_dropIndicator != null && ChannelList.Children.Contains(_dropIndicator))
            ChannelList.Children.Remove(_dropIndicator);
        _dropIndicator = null; _dropIndicatorIndex = -1;
    }

    private int CalcDropIndex(double posY)
    {
        double cumY = 0; int count = 0;
        foreach (var child in ChannelList.Children.OfType<FrameworkElement>())
        {
            if (child == _dropIndicator) continue;
            double rowH = child.ActualHeight + child.Margin.Bottom;
            double rowTop = cumY; cumY += rowH;
            if (posY <= cumY) return posY > rowTop + rowH / 2 ? count + 1 : count;
            count++;
        }
        return count;
    }


    private void ChannelList_DragOver(object sender, DragEventArgs e)
    {
        // カテゴリD&Dのみ処理（チャンネルD&DはマウスキャプチャーD&Dに移行）
        if (!e.Data.GetDataPresent("CategoryDrag")) { e.Effects = DragDropEffects.None; return; }

        e.Effects = DragDropEffects.Move;
        var posY = e.GetPosition(ChannelList).Y;
        if (_catBoundaries.Count == 0) { e.Handled = true; return; }

        int bestIdx = _catBoundaries[^1].childIndex; double bestDist = double.MaxValue;
        foreach (var (y, idx) in _catBoundaries)
        {
            double dist = Math.Abs(posY - y);
            if (dist < bestDist) { bestDist = dist; bestIdx = idx; }
        }
        ShowDropIndicator(bestIdx);
        e.Handled = true;
    }

    private void ChannelList_Drop(object sender, DragEventArgs e)
    {
        HideDropIndicator();
        if (!e.Data.GetDataPresent("CategoryDrag")) return;

        var catSrcId = e.Data.GetData("CategoryDrag") as string;
        if (string.IsNullOrEmpty(catSrcId)) return;

        var categories = SettingsService.Instance.Categories;
        var catSrcIdx  = categories.FindIndex(c => c.CategoryId == catSrcId);
        if (catSrcIdx < 0) return;

        int dropIdx   = CalcDropIndex(e.GetPosition(ChannelList).Y);
        var rows      = ChannelList.Children.OfType<Border>().Where(b => b != _dropIndicator).ToList();
        int catDstIdx = categories.Count; int scanned = 0;

        foreach (var row in rows)
        {
            if (scanned >= dropIdx && row.Tag is CategoryInfo dropCat)
            { catDstIdx = categories.FindIndex(c => c.CategoryId == dropCat.CategoryId); break; }
            scanned++;
        }

        if (catDstIdx == catSrcIdx || catDstIdx == catSrcIdx + 1) return;
        var movingCat = categories[catSrcIdx];
        categories.RemoveAt(catSrcIdx);
        if (catDstIdx > catSrcIdx) catDstIdx--;
        catDstIdx = Math.Max(0, Math.Min(catDstIdx, categories.Count));
        categories.Insert(catDstIdx, movingCat);
        for (int i = 0; i < categories.Count; i++) categories[i].SortOrder = i;

        SettingsService.Instance.SaveCategories();
        RefreshChannelList();
        e.Handled = true;
    }
}
