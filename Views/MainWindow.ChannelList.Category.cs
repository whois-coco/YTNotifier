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
    private const int    CategoryRowHeight          = 27;
    private const int    CategoryRowHeightCompact   = 24;
    private const int    CategoryBadgeSize          = 18;

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

        var badgeText = new TextBlock
        {
            Text                = unreadCount.ToString(),
            FontSize            = 13,
            FontWeight          = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            TextAlignment       = TextAlignment.Center,
        };
        SetDynamicBrush(badgeText, TextBlock.ForegroundProperty, "TextOnColorBrush");

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
            Child               = badgeText
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
}
