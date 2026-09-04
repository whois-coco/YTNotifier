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

    /// <summary>未分類見出し行の Tag 値（開閉状態の識別に使う）</summary>
    private const string UncategorizedRowTag = "uncategorized";

    /// <summary>
    /// カテゴリ開閉アニメーションの所要時間（ミリ秒）。
    /// 検索ボックスの開閉アニメーション（SearchAnimateDurationMs = 200）と同じ体感に揃える。
    /// </summary>
    private const int CategoryToggleAnimDurationMs = 200;

    /// <summary>カテゴリ開閉アニメーションの実行中フラグ（多重クリック抑止）</summary>
    private bool _categoryToggleAnimating = false;

    private const double CategoryHeaderCornerRadius = 4;   // カテゴリ見出しの角丸
    private const double CategoryArrowFontSize      = 9;   // 開閉の▶▼
    private const double CategoryNameFontSize       = 11;  // カテゴリ名
    private const double CategoryBadgeFontSize      = 13;  // 未読バッジの件数
    private const double CategoryDimmedOpacity      = 0.4; // 対象が無いメニュー項目の薄さ

    // カテゴリ名入力ダイアログ
    private const double CategoryDialogWidth        = 360;
    private const double CategoryDialogHeight       = 160;
    private const double CategoryDialogCornerRadius = 6;
    private const double CategoryDialogBorderThickness = 1;
    private const double CategoryDialogTitleBarHeight  = 38;
    private const double CategoryDialogTitleFontSize   = 13;

    private static readonly Thickness CategoryHeaderMargin     = new(0, 3, 0, 1);
    private static readonly Thickness CategoryArrowMargin      = new(0, 0, 6, 0);
    private static readonly Thickness CategoryBadgePadding     = new(5, 0, 5, 0);
    private static readonly Thickness CategoryBadgeMargin      = new(6, 0, 0, 0);
    private static readonly Thickness CategoryHeaderPadding    = new(8, 0, 8, 0);
    private static readonly Thickness CategoryDialogTitleMargin = new(16, 0, 0, 0);
    private static readonly Thickness CategoryDialogInputMargin = new(0, 0, 0, 12);
    private static readonly Thickness CategoryDialogButtonPadding = new(14, 7, 14, 7);
    private static readonly Thickness CategoryDialogCancelMargin  = new(0, 0, 8, 0);
    private static readonly Thickness CategoryDialogContentMargin = new(16, 12, 16, 16);

    // ===== カテゴリ行の共通ヘッダー生成 =====
    private Border CreateGroupHeaderRow(string label, int unreadCount, bool isCollapsed, object tag)
    {
        var settings = SettingsService.Instance.Settings;
        var rowHeight = settings.CompactMode ? CategoryRowHeightCompact : CategoryRowHeight;
        var row = new Border
        {
            Height              = rowHeight,
            Margin              = CategoryHeaderMargin,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius        = new CornerRadius(CategoryHeaderCornerRadius),
            Cursor              = Cursors.Hand,
            Tag                 = tag
        };
        SetDynamicBrush(row, Border.BackgroundProperty, "SurfaceElevatedBrush");

        var arrow = new TextBlock
        {
            Text              = isCollapsed ? "▶" : "▼",
            FontSize          = CategoryArrowFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = CategoryArrowMargin
        };
        SetDynamicBrush(arrow, TextBlock.ForegroundProperty, "TextMutedBrush");

        var nameText = new TextBlock
        {
            Text              = label,
            FontSize          = CategoryNameFontSize,
            FontWeight        = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        SetDynamicBrush(nameText, TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var badgeText = new TextBlock
        {
            Text                = unreadCount.ToString(),
            FontSize            = CategoryBadgeFontSize,
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
            Padding             = CategoryBadgePadding,
            Margin              = CategoryBadgeMargin,
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
            Margin            = CategoryHeaderPadding,
            Children          = { arrow, nameText, badge }
        };
        return row;
    }

    private Border CreateUncategorizedRow(int unreadCount, bool isCollapsed)
    {
        var row = CreateGroupHeaderRow("未分類", unreadCount, isCollapsed, UncategorizedRowTag);
        row.MouseLeftButtonUp += (_, _) =>
        {
            if (_categoryToggleAnimating) return;
            AppLogger.Log(LogMsg.CategoryCollapsed, null, "未分類", !_uncategorizedCollapsed ? "折り畳み" : "展開");
            AnimateCategoryToggle(UncategorizedRowTag, _uncategorizedCollapsed, () =>
            {
                _uncategorizedCollapsed = !_uncategorizedCollapsed;
            });
        };
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
            clearNewItem.Opacity   = hasUnread ? 1.0 : CategoryDimmedOpacity;
        };

        return menu;
    }

    private Border CreateCategoryRow(CategoryInfo cat, int unreadCount)
    {
        var row = CreateGroupHeaderRow(cat.CategoryName, unreadCount, cat.IsCollapsed, cat);
        row.ContextMenu = BuildCategoryContextMenu(cat);

        row.MouseLeftButtonUp += (_, _) =>
        {
            if (_categoryToggleAnimating) return;
            // ログは従来どおり「変更後の状態」を出す（従来は反転後に評価していたため !cat.IsCollapsed と等価）
            AppLogger.Log(LogMsg.CategoryCollapsed, null, cat.CategoryName, !cat.IsCollapsed ? "折り畳み" : "展開");
            AnimateCategoryToggle(cat, cat.IsCollapsed, () =>
            {
                cat.IsCollapsed = !cat.IsCollapsed;
                SettingsService.Instance.MarkDirty();
            });
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
            Width = CategoryDialogWidth, Height = CategoryDialogHeight, MinWidth = CategoryDialogWidth, MaxWidth = CategoryDialogWidth, MinHeight = CategoryDialogHeight, MaxHeight = CategoryDialogHeight,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Background = System.Windows.Media.Brushes.Transparent, AllowsTransparency = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ShowInTaskbar = false,
        };

        var root = new Border { CornerRadius = new CornerRadius(CategoryDialogCornerRadius), BorderThickness = new Thickness(CategoryDialogBorderThickness) };
        SetDynamicBrush(root, Border.BackgroundProperty,  "SurfaceBrush");
        SetDynamicBrush(root, Border.BorderBrushProperty, "BorderBrush");

        var titleBar = new Border { Height = CategoryDialogTitleBarHeight, Cursor = Cursors.SizeAll };
        SetDynamicBrush(titleBar, Border.BackgroundProperty, "SidebarBrush");
        titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) dialog.DragMove(); };
        var titleText = new TextBlock { Text = title, FontSize = CategoryDialogTitleFontSize, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = CategoryDialogTitleMargin };
        SetDynamicBrush(titleText, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        titleBar.Child = titleText;

        var inputBox = new TextBox { Text = defaultValue, Margin = CategoryDialogInputMargin };
        inputBox.Style = (Style)Application.Current.Resources["ModernTextBox"];

        var btnRow = new Grid();
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var cancelBtn = new Button { Content = "キャンセル", Padding = CategoryDialogButtonPadding, Margin = CategoryDialogCancelMargin };
        cancelBtn.Style = (Style)Application.Current.Resources["SecondaryButton"];
        cancelBtn.Click += (_, _) => { dialog.DialogResult = false; dialog.Close(); };
        Grid.SetColumn(cancelBtn, 1);

        var okBtn = new Button { Content = "OK", Padding = CategoryDialogButtonPadding };
        okBtn.Style = (Style)Application.Current.Resources["PrimaryButton"];
        okBtn.Click += (_, _) => { dialog.DialogResult = true; dialog.Close(); };
        Grid.SetColumn(okBtn, 2);

        inputBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)  { dialog.DialogResult = true;  dialog.Close(); }
            if (e.Key == System.Windows.Input.Key.Escape) { dialog.DialogResult = false; dialog.Close(); }
        };

        btnRow.Children.Add(cancelBtn); btnRow.Children.Add(okBtn);
        var content = new StackPanel { Margin = CategoryDialogContentMargin, Children = { inputBox, btnRow } };
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
            clearNewItem.IsEnabled = hasUnread; clearNewItem.Opacity = hasUnread ? 1.0 : CategoryDimmedOpacity;
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

    // ===== カテゴリ開閉アニメーション =====

    /// <summary>
    /// カテゴリ見出し（カテゴリ行 or 未分類行）配下のチャンネル行を、高さ＋透明度のアニメーションで開閉する。
    /// </summary>
    /// <param name="headerTag">見出し行の Tag（CategoryInfo または UncategorizedRowTag）。再構築後の再検索に使う。</param>
    /// <param name="currentlyCollapsed">クリック時点で折り畳み状態か（true = これから展開する）。</param>
    /// <param name="applyNewState">開閉状態を実際に反転する処理（IsCollapsed 反転・必要なら MarkDirty）。</param>
    private void AnimateCategoryToggle(object headerTag, bool currentlyCollapsed, Action applyNewState)
    {
        _categoryToggleAnimating = true;

        if (currentlyCollapsed)
        {
            // ---- 展開: 先に再構築して行を生成 → 畳んだ状態から開く ----
            applyNewState();
            RefreshChannelList();

            var rows = CollectCategoryChildRows(headerTag);
            if (rows.Count == 0) { _categoryToggleAnimating = false; return; }

            RunCategoryRowsAnimation(rows, expand: true, onCompleted: () =>
            {
                foreach (var r in rows)
                {
                    r.BeginAnimation(FrameworkElement.HeightProperty, null);
                    r.BeginAnimation(UIElement.OpacityProperty, null);
                    r.Height        = GetChannelRowTargetHeight();
                    r.Opacity       = 1.0;
                    r.ClipToBounds  = false;
                }
                _categoryToggleAnimating = false;
            });
        }
        else
        {
            // ---- 折り畳み: 現在の行を畳んでから再構築 ----
            var rows = CollectCategoryChildRows(headerTag);
            if (rows.Count == 0)
            {
                applyNewState();
                RefreshChannelList();
                _categoryToggleAnimating = false;
                return;
            }

            RunCategoryRowsAnimation(rows, expand: false, onCompleted: () =>
            {
                // アニメーションのクロックが Border を参照し続けないよう停止してから破棄
                foreach (var r in rows)
                {
                    r.BeginAnimation(FrameworkElement.HeightProperty, null);
                    r.BeginAnimation(UIElement.OpacityProperty, null);
                }
                applyNewState();
                RefreshChannelList();
                _categoryToggleAnimating = false;
            });
        }
    }

    /// <summary>指定タグの見出し行の直後に連続して並ぶチャンネル行（Tag が ChannelInfo の Border）を集める。</summary>
    private List<Border> CollectCategoryChildRows(object headerTag)
    {
        var result = new List<Border>();
        int headerIdx = -1;
        for (int i = 0; i < ChannelList.Children.Count; i++)
        {
            if (ChannelList.Children[i] is Border b && Equals(b.Tag, headerTag)) { headerIdx = i; break; }
        }
        if (headerIdx < 0) return result;

        for (int i = headerIdx + 1; i < ChannelList.Children.Count; i++)
        {
            if (ChannelList.Children[i] is not Border b) break;
            if (b.Tag is ChannelInfo) result.Add(b);
            else break;   // 次のカテゴリ見出し / 未分類見出しに到達
        }
        return result;
    }

    /// <summary>チャンネル行の本来の高さ（コンパクト設定で変わる）。</summary>
    private static double GetChannelRowTargetHeight()
        => SettingsService.Instance.Settings.CompactMode
            ? ChannelRowHeightCompact
            : ChannelRowHeight;

    /// <summary>チャンネル行群を一括で開閉アニメーションする。最後の行の完了で onCompleted を呼ぶ。</summary>
    private static void RunCategoryRowsAnimation(List<Border> rows, bool expand, Action onCompleted)
    {
        var target   = GetChannelRowTargetHeight();
        var duration = TimeSpan.FromMilliseconds(CategoryToggleAnimDurationMs);
        var ease     = new CubicEase { EasingMode = EasingMode.EaseOut };

        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            r.ClipToBounds = true;

            double fromH = expand ? 0.0    : target;
            double toH   = expand ? target : 0.0;
            double fromO = expand ? 0.0    : 1.0;
            double toO   = expand ? 1.0    : 0.0;

            if (expand) { r.Height = 0.0; r.Opacity = 0.0; }

            var heightAnim = new DoubleAnimation(fromH, toH, duration) { EasingFunction = ease };
            var opacityAnim = new DoubleAnimation(fromO, toO, duration) { EasingFunction = ease };

            if (i == rows.Count - 1)
                heightAnim.Completed += (_, _) => onCompleted();

            r.BeginAnimation(FrameworkElement.HeightProperty, heightAnim);
            r.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        }
    }
}
