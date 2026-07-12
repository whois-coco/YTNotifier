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
    internal bool _editMode             = false;
    private bool _dormantEditMode       = false;
    private bool _uncategorizedCollapsed         = false;

    // ===== 編集モード =====
    private void EditModeButton_Click(object sender, RoutedEventArgs e)
    {
        _editMode = !_editMode;
        if (_editMode)
        {
            EditModeButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "PrimaryBrush");
            EditModeButton.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextOnColorBrush");
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

    // ===== ステータス絞り込み（チャンネルリストのみ） =====

    // BuildStatusRow がカードに表示するステータスに対応する絞り込み項目
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

    // カード表示（BuildStatusRow）と同じ判定元（ResolveCardStatus）を使い、フィルター結果とカード表示を常に一致させる。
    // ライブとプレミアの同時進行のようにカードへ複数の表示が出るチャンネルは、対応する複数のフィルターすべてに一致する。
    private static bool MatchesChannelStatusFilter(ChannelInfo ch, ChannelStatusFilter filter)
    {
        var cardStatus     = MonitorService.ResolveCardStatus(ch);
        var anyStatusShown = cardStatus.ActiveLiveEntries.Count > 0
                          || cardStatus.ActivePremiereEntries.Count > 0
                          || cardStatus.PendingLiveDisplay != null
                          || cardStatus.PendingPremiereDisplay != null;

        // BuildStatusRow の fallback（種別ピル）が表示される条件と同一
        var kindPillShown = !anyStatusShown
                         && !ch.LatestVideoDeleted
                         && !string.IsNullOrEmpty(ch.LatestTitle)
                         && ch.LatestKind.HasValue;

        return filter switch
        {
            ChannelStatusFilter.Favorite          => ch.IsFavorite,
            ChannelStatusFilter.LiveNow           => cardStatus.ActiveLiveEntries.Count > 0
                                                     || (kindPillShown && ch.LatestKind == VideoKind.Live && ch.ActiveLives.Count > 0),
            ChannelStatusFilter.PremiereNow       => cardStatus.ActivePremiereEntries.Count > 0,
            ChannelStatusFilter.LiveScheduled     => cardStatus.PendingLiveDisplay != null,
            ChannelStatusFilter.PremiereScheduled => cardStatus.PendingPremiereDisplay != null,
            ChannelStatusFilter.Video             => kindPillShown && (ch.LatestKind == VideoKind.Video || ch.LatestKind == VideoKind.Premiere),
            ChannelStatusFilter.Short             => kindPillShown && ch.LatestKind == VideoKind.Short,
            ChannelStatusFilter.Archive           => kindPillShown && ch.LatestKind == VideoKind.Live && ch.ActiveLives.Count == 0,
            _                                     => false,
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
            FilterButton.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextOnColorBrush");
        }
        else
        {
            FilterButton.Background = Brushes.Transparent;
            FilterButton.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextSecondaryBrush");
        }
    }

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
            var matched = channels.Where(c =>
                !c.IsDormant &&
                MatchesChannelStatusFilter(c, _channelStatusFilter.Value)).ToList();
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

    // ===== チャンネル操作 =====
    public static async Task OpenChannelLatestVideoFromToastAsync(ChannelInfo ch, string? toastUrl = null)
        => await OpenChannelLatestVideoAsync(ch, toastUrl);

    private static Task OpenChannelLatestVideoAsync(ChannelInfo ch, string? toastUrl = null)
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
            return Task.CompletedTask;
        }

        // toast に埋め込まれた URL（通知対象の動画）を直接開く
        if (!string.IsNullOrEmpty(toastUrl))
        {
            OpenUrl(toastUrl);
            AppLogger.Log(LogMsg.OpenLatestVideo, ch.ChannelName, "通知動画");
            return Task.CompletedTask;
        }

        // toastUrl がない場合（チャンネル行クリック等）は保存済みの状態から遷移先を判定する
        string url;
        var cardStatus = MonitorService.ResolveCardStatus(ch);
        if (!string.IsNullOrEmpty(cardStatus.ClickTargetVideoId))
        {
            url = YouTubeConstants.WatchUrlBase + cardStatus.ClickTargetVideoId;
            AppLogger.Log(LogMsg.OpenLatestVideo, ch.ChannelName, KindLabel(cardStatus.ClickTargetKind!.Value));
        }
        else
        {
            url = ch.ChannelUrl;
            AppLogger.Log(LogMsg.OpenChannelPage, ch.ChannelName);
        }

        OpenUrl(url);
        return Task.CompletedTask;
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
