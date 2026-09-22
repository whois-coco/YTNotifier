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
    /// <summary>休眠リストの未分類見出し行の Tag 値（通常側の UncategorizedRowTag に対応）</summary>
    private const string DormantUncategorizedRowTag = "dormant_uncategorized";

    private bool _dormantUncategorizedCollapsed  = false;

    // ===== 休眠チャンネル =====
    internal void RefreshDormantChannelList()
    {
        DormantChannelList.Children.Clear();
        var channels        = SettingsService.Instance.Channels.GetChannelsSnapshot();
        var dormantChannels = channels.Where(c => c.IsDormant).ToList();

        var keyword = _dormantSearchQuery;
        if (!string.IsNullOrEmpty(keyword))
        {
            dormantChannels = dormantChannels
                .Where(c => c.ChannelName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (_currentNav == "Dormant")
                TitleCountText.Text = $"{dormantChannels.Count}{AppConstants.ChannelCountUnitSuffix}";
            foreach (var ch in dormantChannels)
                DormantChannelList.Children.Add(CreateDormantChannelRow(ch));
            return;
        }

        if (_currentNav == "Dormant")
            TitleCountText.Text = $"{dormantChannels.Count}{AppConstants.ChannelCountUnitSuffix}";
        var categories = SettingsService.Instance.Channels.DormantCategories.OrderBy(c => c.SortOrder);
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
        if (_dormantEditMode)
        {
            DormantEditModeButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "PrimaryBrush");
            DormantEditModeButton.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextOnColorBrush");
        }
        else
        {
            DormantEditModeButton.Background = Brushes.Transparent;
            DormantEditModeButton.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextSecondaryBrush");
        }

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

        var activateItem = new MenuItem { Header = "▶ チャンネルリストへ移動" };
        activateItem.Click += (_, _) => MoveChannelToActive(ch);

        var newCatItem = new MenuItem { Header = "📁 カテゴリを作成して移動" };
        newCatItem.Click += (_, _) =>
        {
            var catName = ShowCategoryNameDialog("新規カテゴリを作成");
            if (catName != null)
            {
                var oldCategoryId = ch.CategoryId;
                var cat = SettingsService.Instance.Channels.AddDormantCategory(catName);
                SettingsService.Instance.Channels.SetDormantChannelCategory(ch.ChannelId, cat.CategoryId);
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
            var dormantCategories = SettingsService.Instance.Channels.DormantCategories;
            foreach (var cat in dormantCategories.OrderBy(c => c.SortOrder))
            {
                var item = new MenuItem { Header = cat.CategoryName, Tag = (ch, cat) };
                item.Click += (s, _) =>
                {
                    if (s is MenuItem menuItem && menuItem.Tag is (ChannelInfo targetChannel, CategoryInfo targetCategory))
                    {
                        var oldCategoryId = targetChannel.CategoryId;
                        AppLogger.Log(LogMsg.DormantChannelMovedToCategory, null, targetChannel.ChannelName, targetCategory.CategoryName);
                        SettingsService.Instance.Channels.SetDormantChannelCategory(targetChannel.ChannelId, targetCategory.CategoryId);
                        RefreshDormantChannelList();
                        CheckCategoryAutoDelete(oldCategoryId, true);
                    }
                };
                moveToCatItem.Items.Add(item);
            }
            var uncatItem = new MenuItem { Header = AppConstants.UncategorizedMenuLabel };
            uncatItem.Click += (_, _) =>
            {
                var oldCategoryId = ch.CategoryId;
                AppLogger.Log(LogMsg.DormantChannelMovedToCategory, null, ch.ChannelName, AppConstants.UncategorizedLabel);
                SettingsService.Instance.Channels.SetDormantChannelCategory(ch.ChannelId, null);
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
        var row = CreateGroupHeaderRow(AppConstants.UncategorizedLabel, 0, _dormantUncategorizedCollapsed, DormantUncategorizedRowTag);
        row.MouseLeftButtonUp += (_, _) =>
        {
            _dormantUncategorizedCollapsed = !_dormantUncategorizedCollapsed;
            AppLogger.Log(LogMsg.CategoryCollapsed, null, AppConstants.UncategorizedLabel, _dormantUncategorizedCollapsed ? AppConstants.LogCollapsedText : AppConstants.LogExpandedText);
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
            AppLogger.Log(LogMsg.CategoryCollapsed, null, cat.CategoryName, cat.IsCollapsed ? AppConstants.LogCollapsedText : AppConstants.LogExpandedText);
            SettingsService.Instance.MarkDirty();
            RefreshDormantChannelList();
        };

        AttachCategoryRowDragEvents(row, cat, DormantCategoryDnd);

        return row;
    }

    private ContextMenu BuildDormantCategoryContextMenu(CategoryInfo cat)
    {
        var menu = new ContextMenu();

        var renameItem = new MenuItem { Header = "✏ カテゴリ名を変更" };
        renameItem.Click += (_, _) =>
        {
            var newName = ShowCategoryNameDialog("カテゴリ名を変更", cat.CategoryName);
            if (newName != null) { AppLogger.Log(LogMsg.CategoryRenamed, null, cat.CategoryName, newName); SettingsService.Instance.Channels.RenameDormantCategory(cat.CategoryId, newName); RefreshDormantChannelList(); }
        };

        var deleteItem = new MenuItem { Header = "🗑 カテゴリを削除" };
        deleteItem.Click += (_, _) =>
        {
            if (ConfirmDialog.Show(Application.Current.MainWindow as Window ?? this, "カテゴリ削除", $"「{cat.CategoryName}」を削除しますか？\nチャンネルは未分類に移動されます。", "削除") == true)
            {
                AppLogger.Log(LogMsg.CategoryDeleted, null, cat.CategoryName);
                SettingsService.Instance.Channels.RemoveDormantCategory(cat.CategoryId);
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
            var separators = menu.Items.OfType<Separator>().ToList();
            foreach (var separator in separators) separator.Visibility = _dormantEditMode ? Visibility.Visible : Visibility.Collapsed;
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
            var monCat = SettingsService.Instance.Channels.Categories
                .FirstOrDefault(c => c.CategoryId == ch.CategoryId);
            if (monCat != null)
                ch.CategoryId = SettingsService.Instance.Channels.EnsureDormantCategory(monCat.CategoryId, monCat.CategoryName);
        }
        ch.IsDormant = true;

        // 休眠中は一切のチェックを許容しない（015修正）：通知種別トグル・時間指定スロットを強制OFF
        ch.NotifyVideo = false;
        ch.NotifyShort = false;
        ch.NotifyLive  = false;
        foreach (var slot in ch.FocusSlots) slot.IsEnabled = false;

        // 休眠移動時、これまでの動画チェック情報を全て破棄する（015修正）
        ch.State.LastCheckedVideoId          = string.Empty;
        ch.State.LastCheckedVideoPublishedAt = null;
        MonitorService.RunUnderPendingListLock(() =>
        {
            ch.State.PendingLives.Clear();
            ch.State.PendingPremieres.Clear();
            ch.State.ActiveLives.Clear();
            ch.State.ActivePremieres.Clear();
        });
        ch.State.LastLiveNotifiedId          = string.Empty;
        ch.State.LastPremiereNotifiedId      = string.Empty;
        ch.State.LastLiveId                  = string.Empty;
        ch.State.LastPremiereId              = string.Empty;
        ch.State.NextLiveCheckAt             = null;
        ch.State.LiveGraceRemaining          = 0;
        ch.State.NextPremiereCheckAt         = null;
        ch.State.LastVideoTitle              = string.Empty;
        ch.State.LastVideoNotifiedAt         = null;
        ch.State.LastShortNotifiedId         = string.Empty;
        ch.State.LastShortTitle              = string.Empty;
        ch.State.LastShortNotifiedAt         = null;
        ch.State.LastLiveNotifiedTitle       = string.Empty;
        ch.State.LastLiveNotifiedAt          = null;
        ch.State.LastPremiereNotifiedTitle   = string.Empty;
        ch.State.LastPremiereNotifiedAt      = null;
        ch.State.LatestTitle                 = null;
        ch.State.LatestKind                  = null;
        ch.State.LatestVideoId               = null;
        ch.State.LatestDuration              = null;
        ch.State.LatestThumbnailUrl          = null;
        ch.State.RecentUploads.Clear();
        ch.State.VideoKindCache.Clear();
        ch.State.LatestVideoDeleted          = false;
        ch.State.NoVideosFound               = false;
        ch.HasUnread                         = false;

        SettingsService.Instance.Channels.MoveChannelToCategoryEnd(ch);
        SettingsService.Instance.Channels.UpdateChannel(ch);
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
            var dormantCat = SettingsService.Instance.Channels.DormantCategories
                .FirstOrDefault(c => c.CategoryId == ch.CategoryId);
            if (dormantCat != null)
                ch.CategoryId = SettingsService.Instance.Channels.EnsureCategory(dormantCat.CategoryId, dormantCat.CategoryName);
        }
        ch.IsDormant        = false;
        ch.NotifyVideo      = false;
        ch.NotifyShort      = false;
        ch.NotifyLive       = false;
        foreach (var slot in ch.FocusSlots) slot.IsEnabled = false;

        SettingsService.Instance.Channels.MoveChannelToCategoryEnd(ch);
        SettingsService.Instance.Channels.UpdateChannel(ch);
        AppLogger.Log(LogMsg.MovedToActive, null, ch.ChannelName);
        RefreshChannelList();
        RefreshDormantChannelList();
        CheckCategoryAutoDelete(oldCategoryId, true);
    }
}
