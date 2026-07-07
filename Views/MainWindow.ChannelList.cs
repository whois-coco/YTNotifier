using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
using YTNotifier.Constants;
using YTNotifier.Models;
using YTNotifier.Services;
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
        if (_editMode)
        {
            EditModeButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "PrimaryBrush");
            EditModeButton.Foreground = Brushes.White;
        }
        else
        {
            EditModeButton.Background = Brushes.Transparent;
            EditModeButton.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextSecondaryBrush");
        }

        AppLogger.Log(_editMode ? LogMsg.EditModeOn : LogMsg.EditModeOff);
        RefreshChannelList();
        RefreshDormantChannelList();
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
            handle.Opacity = visible ? 1.0 : 0.0;
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

    // ===== 検索UI共通処理（監視リスト・休眠リストで共用） =====

    /// <summary>検索ボックス縮小時の幅</summary>
    private const double SearchBoxCollapsedWidth = 32;
    /// <summary>検索ボックス開閉アニメーションの時間（ミリ秒）</summary>
    private const int SearchAnimateDurationMs = 200;

    // 検索UIを構成するコントロール・状態・ログIDの組（監視リスト用と休眠リスト用の2セット）
    private sealed class SearchUi
    {
        public required TextBox   Box;
        public required Border    Border;
        public required TextBlock Placeholder;
        public required Button    Button;
        public required Grid      AreaGrid;
        public required Button    CancelButton;
        public required Func<string>   GetQuery;
        public required Action<string> SetQuery;
        public required Func<bool>     GetExpanded;
        public required Action<bool>   SetExpanded;
        public required LogMsg ExecutedLog;
        public required LogMsg ClearedLog;
        public required Action Refresh;
    }

    private SearchUi? _mainSearchUi;
    private SearchUi? _dormantSearchUi;

    private SearchUi MainSearchUi => _mainSearchUi ??= new SearchUi
    {
        Box          = ChannelSearchBox,
        Border       = SearchBorder,
        Placeholder  = SearchPlaceholder,
        Button       = SearchButton,
        AreaGrid     = SearchAreaGrid,
        CancelButton = SearchCancelButton,
        GetQuery     = () => _searchQuery,
        SetQuery     = v => _searchQuery = v,
        GetExpanded  = () => _searchExpanded,
        SetExpanded  = v => _searchExpanded = v,
        ExecutedLog  = LogMsg.ChannelSearchExecuted,
        ClearedLog   = LogMsg.ChannelSearchCleared,
        Refresh      = RefreshChannelList,
    };

    private SearchUi DormantSearchUi => _dormantSearchUi ??= new SearchUi
    {
        Box          = DormantChannelSearchBox,
        Border       = DormantSearchBorder,
        Placeholder  = DormantSearchPlaceholder,
        Button       = DormantSearchButton,
        AreaGrid     = DormantSearchAreaGrid,
        CancelButton = DormantSearchCancelButton,
        GetQuery     = () => _dormantSearchQuery,
        SetQuery     = v => _dormantSearchQuery = v,
        GetExpanded  = () => _dormantSearchExpanded,
        SetExpanded  = v => _dormantSearchExpanded = v,
        ExecutedLog  = LogMsg.DormantSearchExecuted,
        ClearedLog   = LogMsg.DormantSearchCleared,
        Refresh      = RefreshDormantChannelList,
    };

    private static void SearchBoxGotFocusCore(SearchUi ui)
    {
        ui.Box.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextPrimaryBrush"];
        SetDynamicBrush(ui.Border, Border.BackgroundProperty, "SurfaceBrush");
        SetDynamicBrush(ui.Border, Border.BorderBrushProperty, "PrimaryBrush");
    }

    private void SearchBoxLostFocusCore(SearchUi ui)
    {
        ui.Box.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextPrimaryBrush"];
        SetDynamicBrush(ui.Border, Border.BackgroundProperty, "SurfaceBrush");
        SetDynamicBrush(ui.Border, Border.BorderBrushProperty, "BorderBrush");
        Dispatcher.BeginInvoke(() =>
        {
            if (string.IsNullOrEmpty(ui.Box.Text) &&
                string.IsNullOrEmpty(ui.GetQuery()) &&
                !ui.Button.IsKeyboardFocused)
                AnimateSearchCore(ui, false);
        }, DispatcherPriority.Input);
    }

    private static void SearchBoxTextChangedCore(SearchUi ui)
    {
        var hasText = !string.IsNullOrEmpty(ui.Box.Text);
        ui.Placeholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SearchBoxKeyDownCore(SearchUi ui, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)  ExecuteSearchCore(ui);
        if (e.Key == Key.Escape) ClearSearchCore(ui);
    }

    private void SearchButtonClickCore(SearchUi ui)
    {
        if (!ui.GetExpanded())
        {
            AnimateSearchCore(ui, true);
        }
        else if (!string.IsNullOrEmpty(ui.GetQuery()))
        {
            ClearSearchCore(ui);
        }
        else
        {
            ExecuteSearchCore(ui);
        }
    }

    private static void AnimateSearchCore(SearchUi ui, bool expand)
    {
        var targetWidth = expand ? ui.AreaGrid.ActualWidth : SearchBoxCollapsedWidth;
        var anim = new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(SearchAnimateDurationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        ui.Border.BeginAnimation(FrameworkElement.WidthProperty, anim);
        ui.Border.BorderThickness = expand ? new Thickness(1) : new Thickness(0);
        if (expand)
            SetDynamicBrush(ui.Border, Border.BackgroundProperty, "SurfaceBrush");
        else
            ui.Border.Background = System.Windows.Media.Brushes.Transparent;
        ui.SetExpanded(expand);
        ui.CancelButton.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        if (expand) ui.Box.Focus();
    }

    private void ExecuteSearchCore(SearchUi ui)
    {
        ui.SetQuery(ui.Box.Text.Trim());
        ui.Placeholder.Visibility = string.IsNullOrEmpty(ui.Box.Text) ? Visibility.Visible : Visibility.Collapsed;
        AppLogger.Log(ui.ExecutedLog, null, ui.GetQuery());
        if (ui == MainSearchUi && _channelStatusFilter.HasValue)
        {
            _channelStatusFilter = null;
            SetFilterButtonActive(false);
            AppLogger.Log(LogMsg.ChannelFilterCleared);
        }
        ui.Refresh();
    }

    private static void ClearSearchCore(SearchUi ui)
    {
        ui.Box.Text               = "";
        ui.SetQuery("");
        ui.Placeholder.Visibility = Visibility.Visible;
        ui.Box.Foreground         = (System.Windows.Media.Brush)Application.Current.Resources["TextPrimaryBrush"];
        SetDynamicBrush(ui.Border, Border.BackgroundProperty, "SurfaceBrush");
        AnimateSearchCore(ui, false);
        AppLogger.Log(ui.ClearedLog);
        ui.Refresh();
    }

    // ===== 検索UIイベントハンドラ（監視リスト） =====
    private void ChannelSearchBox_GotFocus(object sender, RoutedEventArgs e)  => SearchBoxGotFocusCore(MainSearchUi);

    private void ChannelSearchBox_LostFocus(object sender, RoutedEventArgs e) => SearchBoxLostFocusCore(MainSearchUi);

    private void ChannelSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => SearchBoxTextChangedCore(MainSearchUi);

    private void ChannelSearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        => SearchBoxKeyDownCore(MainSearchUi, e);

    private void SearchButton_Click(object sender, RoutedEventArgs e)         => SearchButtonClickCore(MainSearchUi);

    private void SearchCancelButton_Click(object sender, RoutedEventArgs e)   => ClearSearchCore(MainSearchUi);

    // ===== ステータス絞り込み（監視リストのみ） =====

    // BuildStatusRow がカードに表示するステータスと1:1で対応する絞り込み項目
    private enum ChannelStatusFilter
    {
        Favorite,
        LiveNow,
        PremiereNow,
        LiveScheduled,
        PremiereScheduled,
        Video,
        Short,
        Archive,
    }

    private ChannelStatusFilter? _channelStatusFilter;

    private static string GetChannelStatusFilterLabel(ChannelStatusFilter filter) => filter switch
    {
        ChannelStatusFilter.LiveNow           => "ライブ配信中",
        ChannelStatusFilter.PremiereNow       => "プレミア公開中",
        ChannelStatusFilter.LiveScheduled     => "ライブ予約",
        ChannelStatusFilter.PremiereScheduled => "プレミア公開予約",
        ChannelStatusFilter.Video             => "動画",
        ChannelStatusFilter.Short             => "Short",
        ChannelStatusFilter.Archive           => "アーカイブ",
        ChannelStatusFilter.Favorite          => "お気に入り",
        _                                     => string.Empty,
    };

    // BuildStatusRow と同じ優先順位・条件でチャンネルの該当ステータスを判定する
    private static ChannelStatusFilter? GetChannelStatusFilter(ChannelInfo ch)
    {
        if (ch.NotifyLive && ch.ActiveLives.Count > 0) return ChannelStatusFilter.LiveNow;
        if (ch.ActivePremieres.Count > 0) return ChannelStatusFilter.PremiereNow;

        var statusWindow = DateTime.Now.AddMinutes(AppConstants.UpcomingDisplayWindowMinutes);
        if (ch.NotifyLive && ch.PendingLives.Any(p => p.ScheduledAt.HasValue && p.ScheduledAt.Value <= statusWindow))
            return ChannelStatusFilter.LiveScheduled;
        if (ch.PendingPremieres.Any(p => p.ScheduledAt.HasValue && p.ScheduledAt.Value <= statusWindow))
            return ChannelStatusFilter.PremiereScheduled;

        return ch.LatestKind switch
        {
            VideoKind.Video    => ChannelStatusFilter.Video,
            VideoKind.Premiere => ChannelStatusFilter.Video,
            VideoKind.Short    => ChannelStatusFilter.Short,
            VideoKind.Live     => ch.ActiveLives.Count == 0 ? ChannelStatusFilter.Archive : ChannelStatusFilter.LiveNow,
            _                  => null,
        };
    }

    private void FilterButton_Click(object sender, RoutedEventArgs e)
    {
        FilterButton.ContextMenu ??= BuildChannelFilterMenu();
        FilterButton.ContextMenu.PlacementTarget = FilterButton;
        FilterButton.ContextMenu.Placement       = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        FilterButton.ContextMenu.IsOpen          = true;
    }

    private ContextMenu BuildChannelFilterMenu()
    {
        var menu  = new ContextMenu();
        var items = new List<MenuItem>();

        foreach (ChannelStatusFilter filter in Enum.GetValues<ChannelStatusFilter>())
        {
            var item = new MenuItem { Header = GetChannelStatusFilterLabel(filter), IsCheckable = true, Tag = filter };
            item.Click += (_, _) => ApplyChannelStatusFilter(filter);
            items.Add(item);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var clearItem = new MenuItem { Header = "フィルターを解除" };
        clearItem.Click += (_, _) => ClearChannelStatusFilter();
        menu.Items.Add(clearItem);

        menu.Opened += (_, _) =>
        {
            foreach (var item in items)
                item.IsChecked = item.Tag is ChannelStatusFilter f && _channelStatusFilter == f;
            clearItem.IsEnabled = _channelStatusFilter.HasValue;
            clearItem.Opacity   = _channelStatusFilter.HasValue ? 1.0 : 0.4;
        };

        return menu;
    }

    private void ApplyChannelStatusFilter(ChannelStatusFilter filter)
    {
        _channelStatusFilter = filter;

        // 検索との排他: 検索クエリをクリアし、検索ボックスも閉じる
        if (_searchExpanded) AnimateSearchCore(MainSearchUi, false);
        if (!string.IsNullOrEmpty(_searchQuery) || !string.IsNullOrEmpty(ChannelSearchBox.Text))
        {
            ChannelSearchBox.Text        = "";
            _searchQuery                 = "";
            SearchPlaceholder.Visibility = Visibility.Visible;
        }

        SetFilterButtonActive(true);
        AppLogger.Log(LogMsg.ChannelFilterApplied, null, GetChannelStatusFilterLabel(filter));
        RefreshChannelList();
    }

    private void ClearChannelStatusFilter()
    {
        if (!_channelStatusFilter.HasValue) return;
        _channelStatusFilter = null;
        SetFilterButtonActive(false);
        AppLogger.Log(LogMsg.ChannelFilterCleared);
        RefreshChannelList();
    }

    private void SetFilterButtonActive(bool active)
    {
        if (active)
        {
            FilterButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "PrimaryBrush");
            FilterButton.Foreground = Brushes.White;
        }
        else
        {
            FilterButton.Background = Brushes.Transparent;
            FilterButton.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextSecondaryBrush");
        }
    }

    // カテゴリなし表示モードでドラッグ対象を全チャンネルに広げるためのセンチネル値
    private const string AllChannelsCatSentinel = "\x00all";

    // ===== チャンネル一覧 =====
    internal void RefreshChannelList()
    {
        var settings   = SettingsService.Instance.Settings;
        var channels   = SettingsService.Instance.Channels;
        var categories = SettingsService.Instance.Categories.OrderBy(c => c.SortOrder);

        ChannelList.Children.Clear();
        var activeCount  = channels.Count(c => !c.IsDormant);
        ChannelCountText.Text = $"{activeCount} チャンネル";
        EmptyState.Visibility = activeCount == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 検索モード: カテゴリなしで部分一致チャンネルのみ表示
        if (!string.IsNullOrEmpty(_searchQuery))
        {
            var matched = channels.Where(c =>
                !c.IsDormant &&
                c.ChannelName.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)).ToList();
            EmptyState.Visibility = matched.Count == 0 && channels.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
            foreach (var ch in matched) ChannelList.Children.Add(CreateChannelRow(ch));
            UpdateQuotaInfo();
            return;
        }

        // フィルターモード: カテゴリなしで該当ステータスのチャンネルのみ表示
        if (_channelStatusFilter.HasValue)
        {
            var matched = _channelStatusFilter.Value == ChannelStatusFilter.Favorite
                ? channels.Where(c => !c.IsDormant && c.IsFavorite == true).ToList()
                : channels.Where(c =>
                    !c.IsDormant &&
                    GetChannelStatusFilter(c) == _channelStatusFilter.Value).ToList();
            EmptyState.Visibility = matched.Count == 0 && channels.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
            foreach (var ch in matched) ChannelList.Children.Add(CreateChannelRow(ch));
            UpdateQuotaInfo();
            return;
        }

        if (settings.NoCategoryMode)
        {
            // カテゴリなし表示: ヘッダーを省略して全チャンネルをフラットに並べる
            foreach (var ch in channels.Where(c => !c.IsDormant)) ChannelList.Children.Add(CreateChannelRow(ch));
        }
        else
        {
            foreach (var cat in categories)
            {
                var catChannels = channels.Where(c => !c.IsDormant && c.CategoryId == cat.CategoryId).ToList();
                ChannelList.Children.Add(CreateCategoryRow(cat, catChannels.Count(c => c.HasUnread)));
                if (!cat.IsCollapsed)
                    foreach (var ch in catChannels) ChannelList.Children.Add(CreateChannelRow(ch));
            }

            var monCatIds     = new HashSet<string>(categories.Select(c => c.CategoryId));
            var uncategorized = channels.Where(c => !c.IsDormant && (string.IsNullOrEmpty(c.CategoryId) || !monCatIds.Contains(c.CategoryId))).ToList();
            if (uncategorized.Count > 0)
            {
                ChannelList.Children.Add(CreateUncategorizedRow(uncategorized.Count(c => c.HasUnread), _uncategorizedCollapsed));
                if (!_uncategorizedCollapsed)
                    foreach (var ch in uncategorized) ChannelList.Children.Add(CreateChannelRow(ch));
            }
        }

        if (_editMode)
            foreach (var child in ChannelList.Children.OfType<Border>().Where(b => b.Tag is ChannelInfo))
            {
                SetRowInteractive(child, false);
                SetDeleteButtonVisibility(child, true);
            }

        UpdateQuotaInfo();
        UpdateNavWatchBadge();
    }

    internal void UpdateNavWatchBadge()
    {
        var count = SettingsService.Instance.Channels.Count(c => c.HasUnread);
        if (_navWatchUnreadText   != null) _navWatchUnreadText.Text   = count.ToString();
        if (_navWatchCompactText  != null) _navWatchCompactText.Text  = count.ToString();
        if (_navWatchUnreadBadge  != null)
            _navWatchUnreadBadge.Visibility  = (!_sidebarCollapsed && count > 0)
                ? Visibility.Visible : Visibility.Collapsed;
        if (_navWatchCompactBadge != null)
            _navWatchCompactBadge.Visibility = (_sidebarCollapsed && count > 0)
                ? Visibility.Visible : Visibility.Collapsed;
    }

    // カテゴリなし表示モードかどうか
    private static bool IsNoCategoryMode
        => SettingsService.Instance.Settings.NoCategoryMode;

    // ドラッグ時のカテゴリIDを返す（カテゴリなし表示モードでは全チャンネル対象のセンチネルを返す）
    private static string? GetDragCatId(ChannelInfo ch)
        => IsNoCategoryMode ? AllChannelsCatSentinel : ch.CategoryId;

    // catId がセンチネルの場合は全チャンネル行にマッチする
    private static bool MatchesCatId(ChannelInfo ch, string? catId)
        => catId == AllChannelsCatSentinel || ch.CategoryId == catId;

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
            FontWeight        = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        SetDynamicBrush(nameText, TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var badge = new Border
        {
            CornerRadius        = new CornerRadius(CategoryBadgeSize / 2),
            Height              = CategoryBadgeSize,
            MinWidth            = CategoryBadgeSize,
            Padding             = new Thickness(5, 0, 5, 0),
            Margin              = new Thickness(6, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Visibility          = unreadCount > 0 ? Visibility.Visible : Visibility.Collapsed,
            Child               = new TextBlock
            {
                Text                = unreadCount.ToString(),
                FontSize            = 13,
                FontWeight          = FontWeights.Bold,
                Foreground          = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                TextAlignment       = TextAlignment.Center,
                Margin              = new Thickness(0, 0, 0, 5)
            }
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
        row.MouseLeftButtonUp += (_, _) => { _uncategorizedCollapsed = !_uncategorizedCollapsed; AppLogger.Log(LogMsg.CategoryCollapsed, null, "未分類", _uncategorizedCollapsed ? "折り畳み" : "展開"); RefreshChannelList(); };
        row.ContextMenu = BuildUncategorizedContextMenu();
        return row;
    }

    private ContextMenu BuildUncategorizedContextMenu()
    {
        var menu = new ContextMenu();

        var clearNewItem = new MenuItem { Header = "🔔 NEWバッジを全て消す" };
        clearNewItem.Click += (_, _) =>
        {
            var channels = SettingsService.Instance.Channels
                .Where(c => string.IsNullOrEmpty(c.CategoryId) && c.HasUnread).ToList();
            foreach (var c in channels) c.HasUnread = false;
            if (channels.Count > 0)
            {
                SettingsService.Instance.MarkDirty();
                RefreshChannelList();
            }
            AppLogger.Log(LogMsg.CategoryContextClearNew, null, "未分類");
        };

        menu.Items.Add(clearNewItem);

        menu.Opened += (_, _) =>
        {
            var hasUnread = SettingsService.Instance.Channels
                .Any(c => string.IsNullOrEmpty(c.CategoryId) && c.HasUnread);
            clearNewItem.IsEnabled = hasUnread;
            clearNewItem.Opacity   = hasUnread ? 1.0 : 0.4;
        };

        return menu;
    }

    private Border CreateCategoryRow(CategoryInfo cat, int unreadCount)
    {
        var row = CreateGroupHeaderRow(cat.CategoryName, unreadCount, cat.IsCollapsed, cat);
        row.ContextMenu = BuildCategoryContextMenu(cat);

        row.MouseLeftButtonUp += (_, _) =>
        {
            cat.IsCollapsed = !cat.IsCollapsed;
            AppLogger.Log(LogMsg.CategoryCollapsed, null, cat.CategoryName, cat.IsCollapsed ? "折り畳み" : "展開");
            SettingsService.Instance.MarkDirty();
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
            CacheCategoryBoundariesCore(MainCategoryDnd);
            if (s is Border b) DragDrop.DoDragDrop(b, new DataObject(CategoryDragFormat, cat.CategoryId), DragDropEffects.Move);
        };
        row.PreviewMouseLeftButtonUp += (_, _) => { catDragReady = false; };

        return row;
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
                SettingsService.Instance.MarkDirty();
                RefreshChannelList();
            }
            AppLogger.Log(LogMsg.CategoryContextClearNew, null, cat.CategoryName);
        };

        var expandAllItem = new MenuItem { Header = "▼ 全てのカテゴリを展開" };
        expandAllItem.Click += (_, _) =>
        {
            foreach (var c in SettingsService.Instance.Categories) c.IsCollapsed = false;
            _uncategorizedCollapsed = false;
            SettingsService.Instance.MarkDirty();
            AppLogger.Log(LogMsg.CategoryContextExpandAll);
            RefreshChannelList();
        };

        var collapseAllItem = new MenuItem { Header = "▶ 全てのカテゴリを閉じる" };
        collapseAllItem.Click += (_, _) =>
        {
            foreach (var c in SettingsService.Instance.Categories) c.IsCollapsed = true;
            _uncategorizedCollapsed = true;
            SettingsService.Instance.MarkDirty();
            AppLogger.Log(LogMsg.CategoryContextCollapseAll);
            RefreshChannelList();
        };

        var renameItem = new MenuItem { Header = "✏ カテゴリ名を変更" };
        renameItem.Click += (_, _) =>
        {
            var newName = ShowCategoryNameDialog("カテゴリ名を変更", cat.CategoryName);
            if (newName != null) { AppLogger.Log(LogMsg.CategoryRenamed, null, cat.CategoryName, newName); SettingsService.Instance.RenameCategory(cat.CategoryId, newName); RefreshChannelList(); }
        };

        var deleteItem = new MenuItem { Header = "🗑 カテゴリを削除" };
        deleteItem.Click += (_, _) =>
        {
            if (ConfirmDialog.Show(Application.Current.MainWindow as Window ?? this, "カテゴリ削除", $"「{cat.CategoryName}」を削除しますか？\nチャンネルは未分類に移動されます。", "削除") == true)
            {
                AppLogger.Log(LogMsg.CategoryDeleted, null, cat.CategoryName);
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
    internal void AutoAdjustIntervalForQuota()
    {
        var svc      = SettingsService.Instance;
        var s        = svc.Settings;
        var channels = svc.Channels;
        if (channels.Count == 0) return;

        var (safe, recommended) = ApiQuotaHelper.ValidateInterval(s.CheckIntervalMinutes, channels);
        if (!safe && s.CheckIntervalMinutes != recommended)
        {
            var recItem = IntervalComboBox?.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag?.ToString() == recommended.ToString());
            if (recItem != null && IntervalComboBox != null)
                IntervalComboBox.SelectedItem = recItem;
            AppLogger.Log(LogMsg.QuotaRiskAdjusted, null, recommended);
        }
        UpdateQuotaInfo();
    }

    private void UpdateQuotaInfo()
    {
        try
        {
            if (QuotaInfoText == null) return;
            var svc      = SettingsService.Instance;
            var settings = svc.Settings;
            var channels = svc.GetChannelsSnapshot();
            var interval = settings.CheckIntervalMinutes;
            var daily    = ApiQuotaHelper.EstimateDailyUnitsForChannels(interval, channels);
            var pct      = Math.Min(daily * 100.0 / ApiQuotaHelper.DailyLimit, 100.0);

            QuotaInfoText.Text    = $"{daily:N0} / {ApiQuotaHelper.DailyLimit:N0} ユニット/日";
            QuotaPercentText.Text = $"{pct:F0}%";

            var (normalUnits, lowFreqUnits, focusUnits) =
                ApiQuotaHelper.EstimateDailyUnitsByMode(interval, channels);
            ApplySegmentedQuotaBar(
                QuotaBarBg, QuotaBarNormal, QuotaBarLowFreq, QuotaBarFocus,
                QuotaInfoText, QuotaPercentText,
                pct, normalUnits, lowFreqUnits, focusUnits);
            UpdateIntervalComboBoxItems(channels, channels.Count);

            // 当日実使用量バー（クォータ期間 = 太平洋時間0:00リセット）
            var appState    = SettingsService.Instance.AppState;
            var quotaKey    = AppConstants.GetQuotaDayKey();
            var actualUnits = appState.TodayApiDate == quotaKey ? appState.TodayApiUnits : 0;
            var actualPct   = Math.Min(actualUnits * 100.0 / ApiQuotaHelper.DailyLimit, 100.0);
            ActualQuotaInfoText.Text = $"{actualUnits:N0} / {ApiQuotaHelper.DailyLimit:N0} ユニット/日";
            ActualQuotaPercentText.Text = $"{(int)Math.Round(actualPct)}%";
            ApplyQuotaBar(ActualQuotaBar, ActualQuotaBarBg, ActualQuotaInfoText, ActualQuotaPercentText, actualPct);

        }
        catch (Exception ex) { AppLogger.Log(LogMsg.UiUpdateFailed, null, nameof(UpdateQuotaInfo), ex.Message); }
    }

    // プログレスバー表示の共通処理（bar/barBg をキャプチャして SizeChanged にも対応）
    private void ApplyQuotaBar(
        Border bar, Border barBg,
        TextBlock? infoText, TextBlock percentText,
        double pct)
    {
        // 表示と同じ四捨五入した値で色判定
        var pctRounded = (int)Math.Round(pct);
        var res = Application.Current.Resources;
        bar.Background = pctRounded >= ApiQuotaHelper.QuotaWarnHighThresholdPct ? (System.Windows.Media.Brush)res["QuotaWarnHighBrush"]
                       : pctRounded >= ApiQuotaHelper.QuotaWarnLowThresholdPct  ? (System.Windows.Media.Brush)res["QuotaWarnLowBrush"]
                                                                                 : (System.Windows.Media.Brush)res["QuotaOkBrush"];
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

        var textColor = pctRounded >= ApiQuotaHelper.QuotaWarnHighThresholdPct ? "QuotaWarnHighBrush"
                     : pctRounded >= ApiQuotaHelper.QuotaWarnLowThresholdPct  ? "QuotaWarnLowBrush"
                                                                               : "SuccessBrush";
        if (infoText != null) SetDynamicBrush(infoText, TextBlock.ForegroundProperty, textColor);
        SetDynamicBrush(percentText, TextBlock.ForegroundProperty, textColor);
    }

    // セグメントバー（モード別色分け）表示処理
    private void ApplySegmentedQuotaBar(
        Border barBg,
        Border barNormal, Border barLowFreq, Border barFocus,
        TextBlock? infoText, TextBlock percentText,
        double pct, int normalUnits, int lowFreqUnits, int focusUnits)
    {
        var pctRounded = (int)Math.Round(pct);
        var textColor  = pctRounded >= ApiQuotaHelper.QuotaWarnHighThresholdPct ? "QuotaWarnHighBrush"
                       : pctRounded >= ApiQuotaHelper.QuotaWarnLowThresholdPct  ? "QuotaWarnLowBrush"
                                                                                 : "SuccessBrush";
        if (infoText != null) SetDynamicBrush(infoText, TextBlock.ForegroundProperty, textColor);
        SetDynamicBrush(percentText, TextBlock.ForegroundProperty, textColor);

        var total = normalUnits + lowFreqUnits + focusUnits;

        void SetSegmentWidths()
        {
            var maxW = barBg.ActualWidth;
            if (maxW <= 0) return;
            var totalBarW = Math.Max(0, maxW * pct / 100.0);

            double normalW  = 0, lowFreqW = 0, focusW = 0;
            if (total > 0)
            {
                normalW  = Math.Floor(totalBarW * normalUnits  / (double)total);
                lowFreqW = Math.Floor(totalBarW * lowFreqUnits / (double)total);
                focusW   = totalBarW - normalW - lowFreqW;
            }
            else if (totalBarW > 0)
            {
                normalW = totalBarW;
            }

            barNormal.Width  = Math.Max(0, normalW);
            barLowFreq.Width = Math.Max(0, lowFreqW);
            barFocus.Width   = Math.Max(0, focusW);

            // 端のセグメントのみ角丸を付ける
            var segs = new[] { (barNormal, normalW), (barLowFreq, lowFreqW), (barFocus, focusW) };
            var nonZero = segs.Where(s => s.Item2 > 0).ToList();
            foreach (var (seg, _) in segs)
                seg.CornerRadius = new CornerRadius(0);
            if (nonZero.Count == 1)
            {
                nonZero[0].Item1.CornerRadius = new CornerRadius(4);
            }
            else if (nonZero.Count > 1)
            {
                nonZero[0].Item1.CornerRadius                       = new CornerRadius(4, 0, 0, 4);
                nonZero[nonZero.Count - 1].Item1.CornerRadius       = new CornerRadius(0, 4, 4, 0);
            }
        }

        // 既存ハンドラを除去してから再登録（多重登録防止）
        if (_quotaBarHandlers.TryGetValue(barBg, out var prev))
            barBg.SizeChanged -= prev;
        SizeChangedEventHandler handler = (_, _) => SetSegmentWidths();
        _quotaBarHandlers[barBg] = handler;
        barBg.SizeChanged += handler;

        if (barBg.ActualWidth > 0) SetSegmentWidths();
        else Dispatcher.BeginInvoke(SetSegmentWidths, System.Windows.Threading.DispatcherPriority.Render);
    }

    // barBg ごとの SizeChanged ハンドラを記録（多重登録防止用）
    private readonly Dictionary<Border, SizeChangedEventHandler> _quotaBarHandlers = new();

    private void UpdateIntervalComboBoxItems(List<ChannelInfo> channels, int channelCount)
    {
        if (IntervalComboBox == null) return;
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

        // 現在の選択がクォータ超過なら最小有効間隔へ自動調整
        if (IntervalComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem currentItem && !currentItem.IsEnabled)
        {
            var firstEnabled = IntervalComboBox.Items.OfType<System.Windows.Controls.ComboBoxItem>()
                .FirstOrDefault(i => i.IsEnabled && i.Tag is string t && int.TryParse(t, out _));
            if (firstEnabled != null && int.TryParse(firstEnabled.Tag?.ToString(), out var newMins))
            {
                _loadingSettings = true;
                IntervalComboBox.SelectedItem = firstEnabled;
                _loadingSettings = false;
                SettingsService.Instance.Settings.CheckIntervalMinutes = newMins;
                SettingsService.Instance.SaveSettings();
                MonitorService.Instance.ResetNormalChannels(newMins);
                MonitorService.Instance.RestartWithNewInterval();
                LoggerService.Instance.UpdateFlushInterval();
                AppLogger.Log(LogMsg.QuotaAutoIntervalAdjusted, null, newMins);
            }
        }
    }

    // ===== チャンネル行生成（監視リスト・休眠リストで共用） =====
    private UIElement CreateChannelRow(ChannelInfo ch) => CreateChannelRowCore(ch, isDormant: false);

    private Border CreateChannelRowCore(ChannelInfo ch, bool isDormant)
    {
        var editMode = isDormant ? _dormantEditMode : _editMode;
        var panel    = isDormant ? DormantChannelList : ChannelList;
        var compact  = SettingsService.Instance.Settings.CompactMode;
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
        if (isDormant)
        {
            row.ContextMenu = BuildDormantChannelContextMenu(ch);
            row.ContextMenuOpening += (_, e) => { if (!_dormantEditMode) e.Handled = true; };
        }
        else
        {
            row.ContextMenu = BuildChannelContextMenu(ch);
        }

        // 共通: [4px新着帯] + コンテンツ列
        var outerGrid = new Grid();
        outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 新着帯（休眠リストでは常に非表示）
        var newBar = new Border
        {
            CornerRadius = new CornerRadius(4, 0, 0, 4),
            Visibility   = !isDormant && ch.HasUnread ? Visibility.Visible : Visibility.Hidden,
            Tag          = "NewBar"
        };
        SetDynamicBrush(newBar, Border.BackgroundProperty, "AccentBrush");
        Grid.SetColumn(newBar, 0);

        if (compact)
        {
            row.MouseLeftButtonUp += async (s, e) =>
            {
                var nowEditMode = isDormant ? _dormantEditMode : _editMode;
                if (!nowEditMode && s is Border b && b.Tag is ChannelInfo c) { e.Handled = true; AppLogger.Log(LogMsg.ChannelRowClicked, null, c.ChannelName); await OpenChannelLatestVideoAsync(c); }
            };

            var grid = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(grid, 1);

            var handle   = BuildDragHandle(compact: true,  editMode: editMode);
            var icon     = BuildIconBorderCompact(ch);
            var nameText = new TextBlock
            {
                Text = ch.ChannelName, FontSize = 12, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            SetDynamicBrush(nameText, TextBlock.ForegroundProperty, "TextPrimaryBrush");

            // 削除ボタン（コンパクト + 編集モードONの時のみ表示）
            var deleteBtn = BuildCompactDeleteButton(ch);
            deleteBtn.Visibility = editMode ? Visibility.Visible : Visibility.Collapsed;

            Grid.SetColumn(handle, 0); Grid.SetColumn(icon, 1); Grid.SetColumn(nameText, 2); Grid.SetColumn(deleteBtn, 3);
            AttachHandleDragEvents(handle, row, ch, panel);
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

            var handle  = BuildDragHandle(compact: false, editMode: editMode);
            var icon    = BuildIconBorder(ch);
            var info    = BuildInfoPanelCore(ch, editMode, isDormant);
            var actions = BuildActionsPanel(ch);

            Grid.SetColumn(handle, 0); Grid.SetColumn(icon, 1); Grid.SetColumn(info, 2); Grid.SetColumn(actions, 3);
            AttachHandleDragEvents(handle, row, ch, panel);
            grid.Children.Add(handle); grid.Children.Add(icon); grid.Children.Add(info); grid.Children.Add(actions);
            outerGrid.Children.Add(newBar); outerGrid.Children.Add(grid);
        }

        row.Child = outerGrid;
        return row;
    }

    // ゴミ箱アイコンの図形データ（コンパクト用・通常用の削除ボタンで共用）
    private static readonly string[] TrashIconPathData =
        { "M10 11v6", "M14 11v6", "M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6", "M3 6h18", "M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" };

    private static Canvas BuildTrashIconCanvas()
    {
        var trashCanvas = new Canvas { Width = 24, Height = 24 };
        foreach (var d in TrashIconPathData)
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
        return trashCanvas;
    }

    private Button BuildCompactDeleteButton(ChannelInfo ch)
    {
        var btn = new Button
        {
            Width = 28, Height = 28,
            Content = new Viewbox { Width = 15, Height = 15, Child = BuildTrashIconCanvas() },
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand, Margin = new Thickness(4, 0, 0, 0),
            ToolTip = $"{ch.ChannelName} を削除"
        };
        btn.Click += (_, e) => { e.Handled = true; ConfirmAndDeleteChannel(ch); };
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
            Background = Brushes.Transparent, Opacity = editMode ? 1.0 : 0.0,
            ToolTip = "ドラッグして並び替え", Child = dots
        };
    }

    private void AttachHandleDragEvents(Border handle, Border row, ChannelInfo ch, StackPanel panel)
    {
        System.Windows.Point dragStartPos = default;
        bool dragReady = false;

        handle.MouseLeftButtonDown += (_, e) =>
        {
            if (_isDragging || !(panel == ChannelList ? _editMode : _dormantEditMode)) return;
            dragStartPos = e.GetPosition(panel);
            dragReady    = true;
            _dragSource  = ch;
            handle.CaptureMouse();
        };

        handle.MouseMove += (_, e) =>
        {
            if (!dragReady || e.LeftButton != MouseButtonState.Pressed) return;
            var pos  = e.GetPosition(panel);
            var diff = pos - dragStartPos;

            if (!_isDragging)
            {
                if (Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _isDragging    = true;
                _dragSourceRow = row;
                _animDropIndex = -1;
                Mouse.OverrideCursor = Cursors.SizeNS;
                InitRowTransforms(panel);
            }

            if (_dragSourceRow?.RenderTransform is TranslateTransform srcTt)
                srcTt.Y = pos.Y - dragStartPos.Y;

            var catId  = GetDragCatId(_dragSource!);
            var relIdx = CalcSwapIndex(pos.Y, catId, panel);
            UpdateSwapAnimation(relIdx, panel);
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
                var catId      = GetDragCatId(ch);
                var currentPos = e.GetPosition(panel);
                var relIdx     = CalcSwapIndex(currentPos.Y, catId, panel);
                CommitSwap(ch, catId, relIdx, panel);
            }
            else
            {
                ResetRowTransforms(false, panel);
                _dragSourceRow = null;
                _dragSource    = null;
                _animDropIndex = -1;
            }
        };

        handle.LostMouseCapture += (_, _) =>
        {
            if (!_isDragging) return;
            ResetRowTransforms(false, panel);
            _dragSourceRow = null; _dragSource = null;
            _animDropIndex = -1; _isDragging = false;
            Mouse.OverrideCursor = null;
        };
    }
    // ===== 入れ替えアニメーション =====

    // ドラッグ開始時に対象チャンネル全行へ TranslateTransform を付与
    private void InitRowTransforms(StackPanel panel)
    {
        var catId = GetDragCatId(_dragSource!);
        foreach (var b in panel.Children.OfType<Border>()
            .Where(b => b.Tag is ChannelInfo ch && MatchesCatId(ch, catId)))
        {
            if (b.RenderTransform is TranslateTransform tt) tt.Y = 0;
            else b.RenderTransform = new TranslateTransform(0, 0);
        }
    }

    // マウスY座標から「ドラッグ元が落ち着くべきインデックス」を返す
    private int CalcSwapIndex(double mouseY, string? catId, StackPanel panel)
    {
        var rows = panel.Children.OfType<Border>()
            .Where(b => b.Tag is ChannelInfo ch && MatchesCatId(ch, catId))
            .ToList();
        if (rows.Count == 0) return -1;

        // 各行の自然な中心Y（TranslateTransform を除いた位置）
        double cumY = 0;
        var centers = new List<double>();
        foreach (var child in panel.Children.OfType<Border>())
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
    private void UpdateSwapAnimation(int best, StackPanel panel)
    {
        if (best < 0 || best == _animDropIndex) return;
        _animDropIndex = best;

        if (_dragSourceRow == null) return;
        var catId = GetDragCatId((_dragSourceRow.Tag as ChannelInfo)!);
        var rows  = panel.Children.OfType<Border>()
            .Where(b => b.Tag is ChannelInfo ch && MatchesCatId(ch, catId))
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
    private void ResetRowTransforms(bool commit, StackPanel panel)
    {
        var src   = _dragSourceRow?.Tag as ChannelInfo;
        var catId = src != null ? GetDragCatId(src) : null;
        foreach (var b in panel.Children.OfType<Border>()
            .Where(b => b.Tag is ChannelInfo ch && MatchesCatId(ch, catId)))
        {
            if (b.RenderTransform is not TranslateTransform tt) continue;
            // 確定時は即時リセット（アニメーション中の状態でRefreshが走るのを防ぐ）
            tt.BeginAnimation(TranslateTransform.YProperty, null);
            tt.Y = 0;
        }
    }

    // データを実際に並び替えてリストを再描画
    private void CommitSwap(ChannelInfo srcCh, string? catId, int destRelIdx, StackPanel panel)
    {
        var channels  = SettingsService.Instance.Channels;
        var isDormant = panel == DormantChannelList;
        var sameCat   = channels
            .Select((c, i) => (c, i))
            .Where(t => MatchesCatId(t.c, catId) && t.c.IsDormant == isDormant)
            .ToList();

        var srcCatIdx = sameCat.FindIndex(t => t.c.ChannelId == srcCh.ChannelId);

        if (srcCatIdx < 0 || destRelIdx < 0 || destRelIdx == srcCatIdx)
        {
            ResetRowTransforms(false, panel);
            _dragSourceRow = null; _dragSource = null;
            _animDropIndex = -1; _isDragging = false;
            return;
        }

        var (moving, movingGlobal) = sameCat[srcCatIdx];
        channels.RemoveAt(movingGlobal);

        // RemoveAt後にインデックスを再取得
        var updated = channels.Select((c, i) => (c, i)).Where(t => MatchesCatId(t.c, catId) && t.c.IsDormant == isDormant).ToList();

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
        ResetRowTransforms(true, panel);
        _dragSourceRow = null; _dragSource = null;
        _animDropIndex = -1; _isDragging = false;

        // 確定後にデバッグログを削除してリフレッシュ
        SettingsService.Instance.MarkDirty();
        AppLogger.Log(LogMsg.ChannelReordered, null, srcCh.ChannelName);
        Action refresh = _currentNav == "Dormant" ? RefreshDormantChannelList : RefreshChannelList;
        Dispatcher.Invoke(refresh, System.Windows.Threading.DispatcherPriority.Render);
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
        var img = GetCachedIcon(ch.ThumbnailUrl, ch.ChannelId);
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
            Tag = "IconBorder"
        };

        if (ch.IsBanned)
        {
            SetDynamicBrush(b, Border.BackgroundProperty, "BorderBrush");
            return b;
        }

        b.Cursor = Cursors.Hand;
        b.ToolTip = "クリックして最新動画を開く";
        b.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += async (_, _) => { AppLogger.Log(LogMsg.ChannelRowClicked, null, ch.ChannelName); await OpenChannelLatestVideoAsync(ch); };
        var img = GetCachedIcon(ch.ThumbnailUrl, ch.ChannelId);
        if (img != null) b.Child = new System.Windows.Controls.Image { Source = img, Stretch = Stretch.UniformToFill };
        return b;
    }

    private StackPanel BuildInfoPanelCore(ChannelInfo ch, bool editMode, bool isDormant)
    {
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };

        if (ch.IsBanned)
        {
            var bannedText = new TextBlock
            {
                Text = "チャンネルは利用できません", FontSize = 13, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            SetDynamicBrush(bannedText, TextBlock.ForegroundProperty, "TextMutedBrush");
            info.Children.Add(bannedText);
            return info;
        }

        var nameText = new TextBlock
        {
            Text = ch.ChannelName, FontSize = 13, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        SetDynamicBrush(nameText, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        info.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { nameText } });

        if (editMode)
        {
            // 種別トグルは監視リストのみ表示（休眠リストは編集モード中チャンネル名のみ）
            if (!isDormant)
            {
                var kindRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
                kindRow.Children.Add(MakeKindToggle("動画",  ch.NotifyVideo, ch.GetEffectiveModeForKind(YTNotifier.Services.VideoKind.Video),  YTNotifier.Services.VideoKind.Video,  v => { ch.NotifyVideo = v; SettingsService.Instance.UpdateChannelSilent(ch); SettingsService.Instance.MarkDirty(); AppLogger.Log(LogMsg.KindToggleChanged, ch.ChannelName, "動画",  v ? "ON" : "OFF"); }));
                kindRow.Children.Add(MakeKindToggle("Short", ch.NotifyShort, ch.GetEffectiveModeForKind(YTNotifier.Services.VideoKind.Short),  YTNotifier.Services.VideoKind.Short,  v => { ch.NotifyShort = v; SettingsService.Instance.UpdateChannelSilent(ch); SettingsService.Instance.MarkDirty(); AppLogger.Log(LogMsg.KindToggleChanged, ch.ChannelName, "Short", v ? "ON" : "OFF"); }));
                kindRow.Children.Add(MakeKindToggle("ライブ", ch.NotifyLive,  ch.GetEffectiveModeForKind(YTNotifier.Services.VideoKind.Live),   YTNotifier.Services.VideoKind.Live,   v => { ch.NotifyLive  = v; SettingsService.Instance.UpdateChannelSilent(ch); SettingsService.Instance.MarkDirty(); AppLogger.Log(LogMsg.KindToggleChanged, ch.ChannelName, "ライブ", v ? "ON" : "OFF"); }));
                kindRow.Children.Add(MakeFavoriteToggle(ch));
                info.Children.Add(kindRow);
            }
        }
        else
        {
            // 種別バッジ・動画タイトルプレビューは監視リストのみ表示（休眠リストはチャンネル名のみ）
            if (!isDormant)
            {
                info.Children.Add(BuildStatusRow(ch));
            }
        }

        return info;
    }

    private static UIElement BuildStatusRow(ChannelInfo ch)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };

        if (ch.NotifyLive && ch.ActiveLives.Count > 0)
        {
            var bullet = new TextBlock { Margin = new Thickness(0, 0, 6, 0), FontSize = 13 };
            bullet.Text = ch.ActiveLives.Count == 1 ? "● ライブ配信中" : $"● ライブ配信中 ×{ch.ActiveLives.Count}";
            SetDynamicBrush(bullet, TextBlock.ForegroundProperty, "ErrorBrush");
            row.Children.Add(bullet);

            return row;
        }

        if (ch.ActivePremieres.Count > 0)
        {
            var bullet = new TextBlock { Margin = new Thickness(0, 0, 6, 0), FontSize = 13 };
            bullet.Text = ch.ActivePremieres.Count == 1 ? "● プレミア公開中" : $"● プレミア公開中 ×{ch.ActivePremieres.Count}";
            SetDynamicBrush(bullet, TextBlock.ForegroundProperty, "WarningBrush");
            row.Children.Add(bullet);

            return row;
        }

        var _statusWindow = DateTime.Now.AddMinutes(AppConstants.UpcomingDisplayWindowMinutes);
        if (ch.NotifyLive)
        {
            var liveScheduled = ch.PendingLives
                .Where(p => p.ScheduledAt.HasValue && p.ScheduledAt.Value <= _statusWindow)
                .OrderBy(p => p.ScheduledAt)
                .FirstOrDefault();
            if (liveScheduled != null)
            {
                var bullet = new TextBlock
                {
                    Text = $"⏲ {liveScheduled.ScheduledAt!.Value:HH:mm} から配信予定",
                    FontSize = 13, Opacity = 0.7
                };
                SetDynamicBrush(bullet, TextBlock.ForegroundProperty, "WarningBrush");
                row.Children.Add(bullet);
                return row;
            }
        }

        var premiereScheduled = ch.PendingPremieres
            .Where(p => p.ScheduledAt.HasValue && p.ScheduledAt.Value <= _statusWindow)
            .OrderBy(p => p.ScheduledAt)
            .FirstOrDefault();
        if (premiereScheduled != null)
        {
            var bullet = new TextBlock
            {
                Text = $"⏲ {premiereScheduled.ScheduledAt!.Value:HH:mm} からプレミア公開予定",
                FontSize = 13, Opacity = 0.7
            };
            SetDynamicBrush(bullet, TextBlock.ForegroundProperty, "WarningBrush");
            row.Children.Add(bullet);
            return row;
        }

        if (ch.LatestVideoDeleted)
        {
            var deletedText = new TextBlock { Text = "動画は削除されました", FontSize = 11 };
            SetDynamicBrush(deletedText, TextBlock.ForegroundProperty, "TextMutedBrush");
            row.Children.Add(deletedText);
            return row;
        }

        if (!string.IsNullOrEmpty(ch.LatestTitle) && ch.LatestKind.HasValue)
        {
            var kindLabel = ch.LatestKind.Value switch
            {
                VideoKind.Video    => "動画",
                VideoKind.Short    => "Short",
                VideoKind.Live     => ch.ActiveLives.Count == 0 ? "アーカイブ" : "ライブ",
                VideoKind.Premiere => "プレミア",
                _                  => string.Empty,
            };

            var (bgKey, fgKey) = ch.LatestKind.Value switch
            {
                VideoKind.Video    => ("KindPillVideoBgBrush",    "KindPillVideoFgBrush"),
                VideoKind.Short    => ("KindPillShortBgBrush",    "KindPillShortFgBrush"),
                VideoKind.Live     => ("KindPillLiveBgBrush",     "KindPillLiveFgBrush"),
                VideoKind.Premiere => ("KindPillPremiereBgBrush", "KindPillPremiereFgBrush"),
                _                  => ("KindPillVideoBgBrush",    "KindPillVideoFgBrush"),
            };

            var pill = new Border
            {
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            SetDynamicBrush(pill, Border.BackgroundProperty, bgKey);
            var pillText = new TextBlock { Text = kindLabel, FontSize = 13 };
            SetDynamicBrush(pillText, TextBlock.ForegroundProperty, fgKey);
            pill.Child = pillText;

            var titleText = new TextBlock
            {
                Text = ch.LatestTitle != null && ch.LatestTitle.Length > AppConstants.ChannelCardTitleMaxLength ? ch.LatestTitle.Substring(0, AppConstants.ChannelCardTitleMaxLength) + "..." : ch.LatestTitle, FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 180,
                Cursor = Cursors.Hand
            };
            SetDynamicBrush(titleText, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            if (!string.IsNullOrEmpty(ch.LatestVideoId))
            {
                var kind     = ch.LatestKind.Value;
                var videoId  = ch.LatestVideoId;
                var fullTitle = ch.LatestTitle ?? string.Empty;
                var duration = ch.LatestDuration;
                titleText.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    var owner = System.Windows.Window.GetWindow(titleText) as System.Windows.Window;
                    new VideoSummaryPopupWindow(owner!, ch, kind, fullTitle, videoId, duration).Show();
                };
            }

            row.Children.Add(pill);
            row.Children.Add(titleText);
        }
        else
        {
            var noNotify = new TextBlock { Text = "通知なし", FontSize = 11 };
            SetDynamicBrush(noNotify, TextBlock.ForegroundProperty, "TextMutedBrush");
            row.Children.Add(noNotify);
        }

        return row;
    }

    private static StackPanel BuildActionsPanel(ChannelInfo ch)
    {
        var delBtn = new Button
        {
            Content = new Viewbox { Width = 15, Height = 15, Child = BuildTrashIconCanvas() },
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
        clearItem.Click += (_, _) => { AppLogger.Log(LogMsg.ChannelContextClearNew, null, ch.ChannelName); ch.HasUnread = false; SettingsService.Instance.UpdateChannelSilent(ch); RefreshChannelList(); };

        var renameItem = new MenuItem { Header = "✏ 名称を変更" };
        renameItem.Click += (_, _) => ShowRenameDialog(ch);

        var sepRename = new Separator();

        var newCatItem = new MenuItem { Header = "📁 カテゴリを作成して移動" };
        newCatItem.Click += (_, _) =>
        {
            var catName = ShowCategoryNameDialog("新規カテゴリを作成");
            if (catName != null)
            {
                var oldCategoryId = ch.CategoryId;
                var cat = SettingsService.Instance.AddCategory(catName);
                SettingsService.Instance.SetChannelCategory(ch.ChannelId, cat.CategoryId);
                AppLogger.Log(LogMsg.ChannelMovedToCategory, null, ch.ChannelName, catName);
                RefreshChannelList();
                CheckCategoryAutoDelete(oldCategoryId, false);
            }
        };

        var moveToCatItem = new MenuItem { Header = "📂 カテゴリを移動" };
        var sep = new Separator();

        var detailItem = new MenuItem { Header = "⚙ 詳細設定" };
        detailItem.Click += (_, _) =>
        {
            AppLogger.Log(LogMsg.ChannelContextOpenDetail, null, ch.ChannelName);
            var win = new ChannelDetailWindow(ch, this);
            if (win.ShowDialog() == true) { RefreshChannelList(); UpdateQuotaInfo(); }
        };

        var dormantItem = new MenuItem { Header = "💤 休眠リストへ移動" };
        dormantItem.Click += (_, _) => MoveChannelToDormant(ch);
        var sepDormant = new Separator();

        menu.Items.Add(clearItem);
        menu.Items.Add(detailItem);
        menu.Items.Add(sepDormant);
        menu.Items.Add(renameItem);
        menu.Items.Add(sepRename);
        menu.Items.Add(newCatItem);
        menu.Items.Add(moveToCatItem);
        menu.Items.Add(sep);
        menu.Items.Add(dormantItem);

        menu.Opened += (_, _) =>
        {
            var vis = _editMode ? Visibility.Visible : Visibility.Collapsed;
            clearItem.Visibility     = _editMode ? Visibility.Collapsed : Visibility.Visible;
            clearItem.IsEnabled      = ch.HasUnread;
            clearItem.Opacity        = ch.HasUnread ? 1.0 : 0.4;
            renameItem.Visibility    = vis; sepRename.Visibility    = vis;
            newCatItem.Visibility    = vis; moveToCatItem.Visibility = vis;
            sep.Visibility           = vis; detailItem.Visibility   = vis;
            sepDormant.Visibility    = vis; dormantItem.Visibility  = vis;

            moveToCatItem.Items.Clear();
            var categories = SettingsService.Instance.Categories;
            foreach (var cat in categories.OrderBy(c => c.SortOrder))
            {
                var item = new MenuItem { Header = cat.CategoryName, Tag = (ch, cat) };
                item.Click += (s, _) =>
                {
                    if (s is MenuItem mi && mi.Tag is (ChannelInfo c, CategoryInfo ca))
                    {
                        var oldCategoryId = c.CategoryId;
                        AppLogger.Log(LogMsg.ChannelMovedToCategory, null, c.ChannelName, ca.CategoryName);
                        SettingsService.Instance.SetChannelCategory(c.ChannelId, ca.CategoryId);
                        RefreshChannelList();
                        CheckCategoryAutoDelete(oldCategoryId, false);
                    }
                };
                moveToCatItem.Items.Add(item);
            }
            var uncatItem = new MenuItem { Header = "（未分類）" };
            uncatItem.Click += (_, _) =>
            {
                var oldCategoryId = ch.CategoryId;
                AppLogger.Log(LogMsg.ChannelMovedToCategory, null, ch.ChannelName, "未分類");
                SettingsService.Instance.SetChannelCategory(ch.ChannelId, null);
                RefreshChannelList();
                CheckCategoryAutoDelete(oldCategoryId, false);
            };
            if (categories.Count > 0) moveToCatItem.Items.Add(new Separator());
            moveToCatItem.Items.Add(uncatItem);
        };

        return menu;
    }

    // ===== 種別トグル =====
    private static UIElement MakeKindToggle(string label, bool initial,
        YTNotifier.Models.MonitorMode mode, YTNotifier.Services.VideoKind kind,
        Action<bool> onChanged)
    {
        // アクティブ時の背景色: 通常=青、低頻度=黄、時間指定=緑（種別ごと）
        string ActiveBrushKey(bool on)
        {
            if (!on) return "SurfaceElevatedBrush";
            return mode switch
            {
                YTNotifier.Models.MonitorMode.LowFreq => "WarningBrush",
                YTNotifier.Models.MonitorMode.Focus   => "SuccessBrush",
                _                                     => "PrimaryBrush"
            };
        }

        var border = new Border
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(4),
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
        var color = active ? Brushes.White : (System.Windows.Media.Brush)Application.Current.Resources["TextMutedBrush"];

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

    private static UIElement MakeFavoriteToggle(ChannelInfo ch)
    {
        var border = new Border
        {
            Padding = new Thickness(4), Cursor = Cursors.Arrow, ToolTip = "お気に入り"
        };
        border.Child = BuildFavoriteIcon(ch.IsFavorite);

        border.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (Application.Current.MainWindow is not MainWindow win || !win._editMode) return;
            e.Handled = true;
            ch.IsFavorite = !ch.IsFavorite;
            SettingsService.Instance.UpdateChannelSilent(ch);
            SettingsService.Instance.MarkDirty();
            AppLogger.Log(LogMsg.FavoriteToggleChanged, ch.ChannelName, ch.IsFavorite ? "ON" : "OFF");
            border.Child = BuildFavoriteIcon(ch.IsFavorite);
        };
        return border;
    }

    private static UIElement BuildFavoriteIcon(bool active)
    {
        var color = active
            ? (System.Windows.Media.Brush)Application.Current.Resources["QuotaWarnLowBrush"]
            : (System.Windows.Media.Brush)Application.Current.Resources["TextMutedBrush"];
        var text = new TextBlock
        {
            Text = active ? "★" : "☆", FontSize = 14, Foreground = color,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        return new Viewbox { Width = 24, Height = 24, Margin = new Thickness(12, 0, 0, 0), Child = text };
    }

    // ===== チャンネル操作 =====
    public static async Task OpenChannelLatestVideoFromToastAsync(ChannelInfo ch, string? toastUrl = null)
        => await OpenChannelLatestVideoAsync(ch, toastUrl);

    private static async Task OpenChannelLatestVideoAsync(ChannelInfo ch, string? toastUrl = null)
    {
        if (ch.HasUnread)
        {
            ch.HasUnread = false;
            SettingsService.Instance.UpdateChannelSilent(ch);
            SettingsService.Instance.MarkDirty();
            Application.Current.Dispatcher.Invoke(() => (Application.Current.MainWindow as MainWindow)?.RefreshChannelList());
        }

        if (!ch.NotifyVideo && !ch.NotifyShort && !ch.NotifyLive)
        {
            OpenUrl(ch.ChannelUrl);
            AppLogger.Log(LogMsg.OpenChannelPage, ch.ChannelName);
            return;
        }

        // toast に埋め込まれた URL（通知対象の動画）を直接開く
        if (!string.IsNullOrEmpty(toastUrl))
        {
            OpenUrl(toastUrl);
            AppLogger.Log(LogMsg.OpenLatestVideo, ch.ChannelName, "通知動画");
            return;
        }

        // toastUrl がない場合（チャンネル行クリック等）は API で最新動画を取得
        string url = ch.ChannelUrl;
        var apiKey = SettingsService.Instance.Settings.ApiKey;

        if (!string.IsNullOrEmpty(apiKey))
        {
            AppLogger.Log(LogMsg.SearchingVideo, ch.ChannelName);
            try
            {
                var result = await new YouTubeApiClient().FetchLatestAllowedVideoAsync(
                    ch.ChannelId, ch.NotifyVideo, ch.NotifyShort, ch.NotifyLive,
                    ch.UploadsPlaylistId);

                if (result.HasValue)
                {
                    var (videoId, kind) = result.Value;
                    if (kind == VideoKind.Video && videoId != null) { ch.LastVideoId = videoId; SettingsService.Instance.UpdateChannelSilent(ch); }
                    url = YouTubeConstants.WatchUrlBase + (videoId ?? string.Empty);
                    AppLogger.Log(LogMsg.OpenLatestVideo, ch.ChannelName, KindLabel(kind));
                }
                else
                {
                    url = !string.IsNullOrEmpty(ch.LastVideoId) ? YouTubeConstants.WatchUrlBase + ch.LastVideoId : ch.ChannelUrl;
                    AppLogger.Log(LogMsg.VideoNotFound, ch.ChannelName);
                }
            }
            catch
            {
                url = !string.IsNullOrEmpty(ch.LastVideoId) ? YouTubeConstants.WatchUrlBase + ch.LastVideoId : ch.ChannelUrl;
                AppLogger.Log(LogMsg.ApiFallback, ch.ChannelName);
            }
        }
        else
        {
            AppLogger.Log(LogMsg.ApiKeyNotSetChannel, ch.ChannelName);
            System.Windows.MessageBox.Show(
                "APIキーが設定されていないため、最新動画を取得できません。\n設定タブからAPIキーを入力してください。",
                "APIキー未設定",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        OpenUrl(url);
    }

    private static void OpenUrl(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("http://",  StringComparison.OrdinalIgnoreCase))
            return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void ShowRenameDialog(ChannelInfo ch)
    {
        var name = ShowCategoryNameDialog("名称を変更", ch.ChannelName);
        if (name == null) return;
        ch.ChannelName = name;
        SettingsService.Instance.UpdateChannel(ch);
        AppLogger.Log(LogMsg.ChannelRenamed, null, name);
        RefreshChannelList();
    }

    private void ConfirmAndDeleteChannel(ChannelInfo ch)
    {
        if (ConfirmDialog.Show(this, "チャンネルを削除", $"「{ch.ChannelName}」を削除しますか？", "削除") != true) return;
        var oldCategoryId = ch.CategoryId;
        var oldIsDormant  = ch.IsDormant;
        SettingsService.Instance.RemoveChannel(ch.ChannelId);
        AppLogger.Log(LogMsg.ChannelRemoved, ch.ChannelName);
        RefreshChannelList();
        RefreshDormantChannelList();
        CheckCategoryAutoDelete(oldCategoryId, oldIsDormant);
    }

    // ===== カテゴリ自動削除確認 =====
    // 指定カテゴリの所属チャンネルが0件になった場合、削除するかどうかを確認する
    private void CheckCategoryAutoDelete(string? categoryId, bool isDormant)
    {
        if (string.IsNullOrEmpty(categoryId)) return;
        if (SettingsService.Instance.Channels.Any(c => c.CategoryId == categoryId && c.IsDormant == isDormant)) return;

        var categories = isDormant ? SettingsService.Instance.DormantCategories : SettingsService.Instance.Categories;
        var cat = categories.FirstOrDefault(c => c.CategoryId == categoryId);
        if (cat == null) return;

        if (ConfirmDialog.Show(this, "カテゴリ削除の確認", $"「{cat.CategoryName}」にはチャンネルがなくなりました。カテゴリを削除しますか？", "削除") != true) return;

        AppLogger.Log(LogMsg.CategoryDeleted, null, cat.CategoryName);
        if (isDormant) SettingsService.Instance.RemoveDormantCategory(cat.CategoryId);
        else           SettingsService.Instance.RemoveCategory(cat.CategoryId);

        if (isDormant) RefreshDormantChannelList();
        else           RefreshChannelList();
    }

    private static void DeleteChannel_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button btn || btn.Tag is not ChannelInfo ch) return;
        var mainWindow = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
        mainWindow?.ConfirmAndDeleteChannel(ch);
    }

    // ===== カテゴリのドラッグアンドドロップ共通処理（監視リスト・休眠リストで共用） =====

    /// <summary>カテゴリD&Dのドラッグデータ形式（監視リスト）</summary>
    private const string CategoryDragFormat = "CategoryDrag";
    /// <summary>カテゴリD&Dのドラッグデータ形式（休眠リスト）</summary>
    private const string DormantCategoryDragFormat = "DormantCategoryDrag";

    // カテゴリD&Dを構成するパネル・状態・対象リストの組（監視リスト用と休眠リスト用の2セット）
    private sealed class CategoryDndUi
    {
        public required StackPanel Panel;
        public required string DragFormat;
        public required Func<List<CategoryInfo>> GetCategories;
        public required Action Refresh;
        public List<(double y, int childIndex)> Boundaries = new();
        public Border? DropIndicator;
        public int DropIndicatorIndex = -1;
    }

    private CategoryDndUi? _mainCategoryDnd;
    private CategoryDndUi? _dormantCategoryDnd;

    private CategoryDndUi MainCategoryDnd => _mainCategoryDnd ??= new CategoryDndUi
    {
        Panel         = ChannelList,
        DragFormat    = CategoryDragFormat,
        GetCategories = () => SettingsService.Instance.Categories,
        Refresh       = RefreshChannelList,
    };

    private CategoryDndUi DormantCategoryDnd => _dormantCategoryDnd ??= new CategoryDndUi
    {
        Panel         = DormantChannelList,
        DragFormat    = DormantCategoryDragFormat,
        GetCategories = () => SettingsService.Instance.DormantCategories,
        Refresh       = RefreshDormantChannelList,
    };

    // ドラッグ開始時にカテゴリ行の境界位置（Y座標と挿入先インデックス）を記録する
    private static void CacheCategoryBoundariesCore(CategoryDndUi ui)
    {
        ui.Boundaries.Clear();
        double cumY = 0;
        foreach (var child in ui.Panel.Children.OfType<Border>())
        {
            double rowH = child.ActualHeight + child.Margin.Top + child.Margin.Bottom;
            if (child.Tag is CategoryInfo)
                ui.Boundaries.Add((cumY, ui.Panel.Children.IndexOf(child)));
            cumY += rowH;
        }
        if (ui.Boundaries.Count > 0)
        {
            var last    = ui.Boundaries[^1];
            var lastRow = ui.Panel.Children[last.childIndex] as Border;
            if (lastRow != null)
            {
                double lastH = lastRow.ActualHeight + lastRow.Margin.Top + lastRow.Margin.Bottom;
                ui.Boundaries.Add((last.y + lastH, last.childIndex + 1));
            }
        }
    }

    private static void ShowDropIndicatorCore(CategoryDndUi ui, int insertIndex)
    {
        if (ui.DropIndicatorIndex == insertIndex) return;
        HideDropIndicatorCore(ui);
        ui.DropIndicatorIndex = insertIndex;
        ui.DropIndicator = new Border
        {
            Height = 2, HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible = false, Margin = new Thickness(8, 0, 8, 0)
        };
        SetDynamicBrush(ui.DropIndicator, Border.BackgroundProperty, "PrimaryBrush");
        ui.Panel.Children.Insert(Math.Min(insertIndex, ui.Panel.Children.Count), ui.DropIndicator);
    }

    private static void HideDropIndicatorCore(CategoryDndUi ui)
    {
        if (ui.DropIndicator != null && ui.Panel.Children.Contains(ui.DropIndicator))
            ui.Panel.Children.Remove(ui.DropIndicator);
        ui.DropIndicator = null; ui.DropIndicatorIndex = -1;
    }

    private static int CalcCategoryDropIndexCore(CategoryDndUi ui, double posY)
    {
        double cumY = 0; int count = 0;
        foreach (var child in ui.Panel.Children.OfType<FrameworkElement>())
        {
            if (child == ui.DropIndicator) continue;
            double rowH   = child.ActualHeight + child.Margin.Bottom;
            double rowTop = cumY; cumY += rowH;
            if (posY <= cumY) return posY > rowTop + rowH / 2 ? count + 1 : count;
            count++;
        }
        return count;
    }

    private static void CategoryListDragOverCore(CategoryDndUi ui, DragEventArgs e)
    {
        // カテゴリD&Dのみ処理（チャンネルD&DはマウスキャプチャーD&Dに移行）
        if (!e.Data.GetDataPresent(ui.DragFormat)) { e.Effects = DragDropEffects.None; return; }

        e.Effects = DragDropEffects.Move;
        var posY = e.GetPosition(ui.Panel).Y;
        if (ui.Boundaries.Count == 0) { e.Handled = true; return; }

        int bestIdx = ui.Boundaries[^1].childIndex; double bestDist = double.MaxValue;
        foreach (var (y, idx) in ui.Boundaries)
        {
            double dist = Math.Abs(posY - y);
            if (dist < bestDist) { bestDist = dist; bestIdx = idx; }
        }
        ShowDropIndicatorCore(ui, bestIdx);
        e.Handled = true;
    }

    private static void CategoryListDropCore(CategoryDndUi ui, DragEventArgs e)
    {
        HideDropIndicatorCore(ui);
        if (!e.Data.GetDataPresent(ui.DragFormat)) return;

        var catSrcId = e.Data.GetData(ui.DragFormat) as string;
        if (string.IsNullOrEmpty(catSrcId)) return;

        var categories = ui.GetCategories();
        var catSrcIdx  = categories.FindIndex(c => c.CategoryId == catSrcId);
        if (catSrcIdx < 0) return;

        int dropIdx   = CalcCategoryDropIndexCore(ui, e.GetPosition(ui.Panel).Y);
        var rows      = ui.Panel.Children.OfType<Border>().Where(b => b != ui.DropIndicator).ToList();
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

        SettingsService.Instance.MarkDirty();
        AppLogger.Log(LogMsg.CategoryReordered, null, movingCat.CategoryName);
        ui.Refresh();
        e.Handled = true;
    }

    // ===== カテゴリD&Dイベントハンドラ（監視リスト） =====
    private void ChannelList_DragOver(object sender, DragEventArgs e) => CategoryListDragOverCore(MainCategoryDnd, e);

    private void ChannelList_Drop(object sender, DragEventArgs e)     => CategoryListDropCore(MainCategoryDnd, e);

    // ===== チャンネル追加 =====
    private void AddChannelHeader_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.Log(LogMsg.AddChannelDialogOpened);
        if (_currentNav == "Dormant")
        {
            var dlg = new AddChannelWindow(
                isDormant: true,
                onChannelAdded: () =>
                {
                    Dispatcher.Invoke(RefreshChannelList);
                    Dispatcher.Invoke(RefreshDormantChannelList);
                })
            { Owner = this };
            dlg.ShowDialog();
        }
        else
        {
            var dlg = new AddChannelWindow(onChannelAdded: () =>
            {
                Dispatcher.Invoke(RefreshChannelList);
                Dispatcher.Invoke(UpdateQuotaInfo);
            })
            { Owner = this };
            dlg.ShowDialog();
        }
    }

    // ===== 休眠チャンネル =====
    internal void RefreshDormantChannelList()
    {
        DormantChannelList.Children.Clear();
        var channels        = SettingsService.Instance.Channels;
        var dormantChannels = channels.Where(c => c.IsDormant).ToList();

        var keyword = _dormantSearchQuery;
        if (!string.IsNullOrEmpty(keyword))
        {
            dormantChannels = dormantChannels
                .Where(c => c.ChannelName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToList();
            DormantChannelCountText.Text = $"{dormantChannels.Count} チャンネル";
            foreach (var ch in dormantChannels)
                DormantChannelList.Children.Add(CreateDormantChannelRow(ch));
            return;
        }

        DormantChannelCountText.Text = $"{dormantChannels.Count} チャンネル";
        var categories = SettingsService.Instance.DormantCategories.OrderBy(c => c.SortOrder);
        var settings = SettingsService.Instance.Settings;

        if (settings.NoCategoryMode || !categories.Any())
        {
            foreach (var ch in dormantChannels)
                DormantChannelList.Children.Add(CreateDormantChannelRow(ch));
        }
        else
        {
            foreach (var cat in categories)
            {
                var catChannels = dormantChannels.Where(c => c.CategoryId == cat.CategoryId).ToList();
                DormantChannelList.Children.Add(CreateDormantCategoryRow(cat));
                if (!cat.IsCollapsed)
                    foreach (var ch in catChannels) DormantChannelList.Children.Add(CreateDormantChannelRow(ch));
            }
            var dormantCatIds = new HashSet<string>(categories.Select(c => c.CategoryId));
            var uncategorized = dormantChannels.Where(c => string.IsNullOrEmpty(c.CategoryId) || !dormantCatIds.Contains(c.CategoryId)).ToList();
            if (uncategorized.Count > 0)
            {
                DormantChannelList.Children.Add(CreateDormantUncategorizedRow());
                if (!_dormantUncategorizedCollapsed)
                    foreach (var ch in uncategorized) DormantChannelList.Children.Add(CreateDormantChannelRow(ch));
            }
        }

        if (_dormantEditMode)
            foreach (var child in DormantChannelList.Children.OfType<Border>().Where(b => b.Tag is ChannelInfo))
            {
                SetRowInteractive(child, false);
                SetDeleteButtonVisibility(child, true);
            }
    }

    private void DormantEditModeButton_Click(object sender, RoutedEventArgs e)
    {
        _dormantEditMode = !_dormantEditMode;
        AppLogger.Log(_dormantEditMode ? LogMsg.EditModeOn : LogMsg.EditModeOff);
        RefreshChannelList();
        RefreshDormantChannelList();
    }

    // ===== 検索UIイベントハンドラ（休眠リスト） =====
    private void DormantSearchButton_Click(object sender, RoutedEventArgs e)  => SearchButtonClickCore(DormantSearchUi);

    private void DormantChannelSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => SearchBoxTextChangedCore(DormantSearchUi);

    private void DormantChannelSearchBox_GotFocus(object sender, RoutedEventArgs e)  => SearchBoxGotFocusCore(DormantSearchUi);

    private void DormantChannelSearchBox_LostFocus(object sender, RoutedEventArgs e) => SearchBoxLostFocusCore(DormantSearchUi);

    private void DormantChannelSearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        => SearchBoxKeyDownCore(DormantSearchUi, e);

    private void DormantSearchCancelButton_Click(object sender, RoutedEventArgs e)   => ClearSearchCore(DormantSearchUi);

    private Border CreateDormantChannelRow(ChannelInfo ch) => CreateChannelRowCore(ch, isDormant: true);

    private ContextMenu BuildDormantChannelContextMenu(ChannelInfo ch)
    {
        var menu = new ContextMenu();

        var activateItem = new MenuItem { Header = "▶ 監視リストへ移動" };
        activateItem.Click += (_, _) => MoveChannelToActive(ch);

        var newCatItem = new MenuItem { Header = "📁 カテゴリを作成して移動" };
        newCatItem.Click += (_, _) =>
        {
            var catName = ShowCategoryNameDialog("新規カテゴリを作成");
            if (catName != null)
            {
                var oldCategoryId = ch.CategoryId;
                var cat = SettingsService.Instance.AddDormantCategory(catName);
                SettingsService.Instance.SetDormantChannelCategory(ch.ChannelId, cat.CategoryId);
                AppLogger.Log(LogMsg.DormantChannelMovedToCategory, null, ch.ChannelName, catName);
                RefreshDormantChannelList();
                CheckCategoryAutoDelete(oldCategoryId, true);
            }
        };

        var moveToCatItem = new MenuItem { Header = "📂 カテゴリを移動" };
        var sepActivate = new Separator();

        menu.Items.Add(newCatItem);
        menu.Items.Add(moveToCatItem);
        menu.Items.Add(sepActivate);
        menu.Items.Add(activateItem);

        menu.Opened += (_, _) =>
        {
            if (!_dormantEditMode) { menu.IsOpen = false; return; }

            moveToCatItem.Items.Clear();
            var dormantCategories = SettingsService.Instance.DormantCategories;
            foreach (var cat in dormantCategories.OrderBy(c => c.SortOrder))
            {
                var item = new MenuItem { Header = cat.CategoryName, Tag = (ch, cat) };
                item.Click += (s, _) =>
                {
                    if (s is MenuItem mi && mi.Tag is (ChannelInfo c, CategoryInfo ca))
                    {
                        var oldCategoryId = c.CategoryId;
                        AppLogger.Log(LogMsg.DormantChannelMovedToCategory, null, c.ChannelName, ca.CategoryName);
                        SettingsService.Instance.SetDormantChannelCategory(c.ChannelId, ca.CategoryId);
                        RefreshDormantChannelList();
                        CheckCategoryAutoDelete(oldCategoryId, true);
                    }
                };
                moveToCatItem.Items.Add(item);
            }
            var uncatItem = new MenuItem { Header = "（未分類）" };
            uncatItem.Click += (_, _) =>
            {
                var oldCategoryId = ch.CategoryId;
                AppLogger.Log(LogMsg.DormantChannelMovedToCategory, null, ch.ChannelName, "未分類");
                SettingsService.Instance.SetDormantChannelCategory(ch.ChannelId, null);
                RefreshDormantChannelList();
                CheckCategoryAutoDelete(oldCategoryId, true);
            };
            if (dormantCategories.Count > 0) moveToCatItem.Items.Add(new Separator());
            moveToCatItem.Items.Add(uncatItem);
        };

        return menu;
    }

    private Border CreateDormantUncategorizedRow()
    {
        var row = CreateGroupHeaderRow("未分類", 0, _dormantUncategorizedCollapsed, "dormant_uncategorized");
        row.MouseLeftButtonUp += (_, _) =>
        {
            _dormantUncategorizedCollapsed = !_dormantUncategorizedCollapsed;
            AppLogger.Log(LogMsg.CategoryCollapsed, null, "未分類", _dormantUncategorizedCollapsed ? "折り畳み" : "展開");
            RefreshDormantChannelList();
        };
        return row;
    }

    private Border CreateDormantCategoryRow(CategoryInfo cat)
    {
        var row = CreateGroupHeaderRow(cat.CategoryName, 0, cat.IsCollapsed, cat);
        row.ContextMenu = BuildDormantCategoryContextMenu(cat);

        row.MouseLeftButtonUp += (_, _) =>
        {
            cat.IsCollapsed = !cat.IsCollapsed;
            AppLogger.Log(LogMsg.CategoryCollapsed, null, cat.CategoryName, cat.IsCollapsed ? "折り畳み" : "展開");
            SettingsService.Instance.MarkDirty();
            RefreshDormantChannelList();
        };

        System.Windows.Point catDragStart = default;
        bool catDragReady = false;

        row.PreviewMouseLeftButtonDown += (_, e) => { if (_dormantEditMode) { catDragStart = e.GetPosition(DormantChannelList); catDragReady = true; } };
        row.PreviewMouseMove += (s, e) =>
        {
            if (!_dormantEditMode || !catDragReady) return;
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) { catDragReady = false; return; }
            var diff = e.GetPosition(DormantChannelList) - catDragStart;
            if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            catDragReady = false;
            CacheCategoryBoundariesCore(DormantCategoryDnd);
            if (s is Border b) DragDrop.DoDragDrop(b, new DataObject(DormantCategoryDragFormat, cat.CategoryId), DragDropEffects.Move);
        };
        row.PreviewMouseLeftButtonUp += (_, _) => { catDragReady = false; };

        return row;
    }

    private ContextMenu BuildDormantCategoryContextMenu(CategoryInfo cat)
    {
        var menu = new ContextMenu();

        var renameItem = new MenuItem { Header = "✏ カテゴリ名を変更" };
        renameItem.Click += (_, _) =>
        {
            var newName = ShowCategoryNameDialog("カテゴリ名を変更", cat.CategoryName);
            if (newName != null) { AppLogger.Log(LogMsg.CategoryRenamed, null, cat.CategoryName, newName); SettingsService.Instance.RenameDormantCategory(cat.CategoryId, newName); RefreshDormantChannelList(); }
        };

        var deleteItem = new MenuItem { Header = "🗑 カテゴリを削除" };
        deleteItem.Click += (_, _) =>
        {
            if (ConfirmDialog.Show(Application.Current.MainWindow as Window ?? this, "カテゴリ削除", $"「{cat.CategoryName}」を削除しますか？\nチャンネルは未分類に移動されます。", "削除") == true)
            {
                AppLogger.Log(LogMsg.CategoryDeleted, null, cat.CategoryName);
                SettingsService.Instance.RemoveDormantCategory(cat.CategoryId);
                RefreshDormantChannelList();
            }
        };

        menu.Items.Add(renameItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(deleteItem);

        menu.Opened += (_, _) =>
        {
            renameItem.Visibility = _dormantEditMode ? Visibility.Visible : Visibility.Collapsed;
            deleteItem.Visibility = _dormantEditMode ? Visibility.Visible : Visibility.Collapsed;
            var seps = menu.Items.OfType<Separator>().ToList();
            foreach (var s in seps) s.Visibility = _dormantEditMode ? Visibility.Visible : Visibility.Collapsed;
        };

        return menu;
    }

    // ===== カテゴリD&Dイベントハンドラ（休眠リスト） =====
    private void DormantChannelList_DragOver(object sender, DragEventArgs e) => CategoryListDragOverCore(DormantCategoryDnd, e);

    private void DormantChannelList_Drop(object sender, DragEventArgs e)     => CategoryListDropCore(DormantCategoryDnd, e);

    private void MoveChannelToDormant(ChannelInfo ch)
    {
        var oldCategoryId = ch.CategoryId;
        if (!string.IsNullOrEmpty(ch.CategoryId))
        {
            var monCat = SettingsService.Instance.Categories
                .FirstOrDefault(c => c.CategoryId == ch.CategoryId);
            if (monCat != null)
                SettingsService.Instance.EnsureDormantCategory(monCat.CategoryId, monCat.CategoryName);
        }
        ch.IsDormant = true;
        SettingsService.Instance.UpdateChannel(ch);
        AppLogger.Log(LogMsg.MovedToDormant, null, ch.ChannelName);
        RefreshChannelList();
        RefreshDormantChannelList();
        CheckCategoryAutoDelete(oldCategoryId, false);
    }

    private void MoveChannelToActive(ChannelInfo ch)
    {
        var oldCategoryId = ch.CategoryId;
        if (!string.IsNullOrEmpty(ch.CategoryId))
        {
            var dormantCat = SettingsService.Instance.DormantCategories
                .FirstOrDefault(c => c.CategoryId == ch.CategoryId);
            if (dormantCat != null)
                SettingsService.Instance.EnsureCategory(dormantCat.CategoryId, dormantCat.CategoryName);
        }
        ch.IsDormant        = false;
        ch.NotifyVideo      = false;
        ch.NotifyShort      = false;
        ch.NotifyLive       = false;
        foreach (var slot in ch.FocusSlots) slot.IsEnabled = false;
        SettingsService.Instance.UpdateChannel(ch);
        AppLogger.Log(LogMsg.MovedToActive, null, ch.ChannelName);
        RefreshChannelList();
        RefreshDormantChannelList();
        CheckCategoryAutoDelete(oldCategoryId, true);
    }

    // ===== 手動チェック =====
    private async void ManualCheckButton_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.Log(LogMsg.ManualCheckTriggered);
        InlineCheckButton.IsEnabled = false;
        Nav_Click(NavWatch, e);
        try
        {
            await MonitorService.Instance.ManualCheckAsync();
            RefreshChannelList();
        }
        finally
        {
            InlineCheckButton.IsEnabled = true;
        }
    }
}
