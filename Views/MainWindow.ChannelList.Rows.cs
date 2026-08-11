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
    /// <summary>「📜 最新動画一覧」表示時に絞り込む最新投稿件数の上限</summary>
    private const int RecentUploadsMaxEntries = 25;

    private const int    ChannelRowHeight        = 60;
    private const int    ChannelRowHeightCompact    = 36;

    /// <summary>チャンネルカードの動画タイトル表示最大文字数（超過分は省略）</summary>
    private const int ChannelCardTitleMaxLength = 16;
    private const double ChannelRowMarginBottom  = 0;

    // Short: Lucide zap
    private const string ShortIconPath =
        "M4 14a1 1 0 0 1-.78-1.63l9.9-10.2a.5.5 0 0 1 .86.46l-1.92 6.02A1 1 0 0 0 13 10h7a1 1 0 0 1 .78 1.63l-9.9 10.2a.5.5 0 0 1-.86-.46l1.92-6.02A1 1 0 0 0 11 14z";

    // ===== チャンネル行生成（チャンネルリスト・休眠リストで共用） =====
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
            BorderThickness     = new Thickness(1),
            Tag                 = ch
        };
        SetDynamicBrush(row, Border.BackgroundProperty, "SurfaceBrush");
        SetDynamicBrush(row, Border.BorderBrushProperty, "BorderBrush");
        row.MouseEnter += (_, _) => { if (!_isDragging) SetDynamicBrush(row, Border.BackgroundProperty, "HoverBrush"); };
        row.MouseLeave += (_, _) => SetDynamicBrush(row, Border.BackgroundProperty, "SurfaceBrush");
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
            nameText.Cursor = Cursors.Hand;
            nameText.ToolTip = "クリックしてチャンネルページを開く";
            nameText.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true;
            nameText.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                AppLogger.Log(LogMsg.ChannelNameClicked, null, ch.ChannelName);
                OpenUrl(ch.ChannelUrl);
            };

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

        if (!ch.LatestVideoDeleted)
        {
            b.Cursor = Cursors.Hand;
            b.ToolTip = "クリックして最新動画を開く";
            b.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true;
            b.MouseLeftButtonUp += async (_, _) => { AppLogger.Log(LogMsg.ChannelRowClicked, null, ch.ChannelName); await OpenChannelLatestVideoAsync(ch); };
        }
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
        nameText.Cursor = Cursors.Hand;
        nameText.ToolTip = "クリックしてチャンネルページを開く";
        nameText.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true;
        nameText.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            AppLogger.Log(LogMsg.ChannelNameClicked, null, ch.ChannelName);
            OpenUrl(ch.ChannelUrl);
        };
        info.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { nameText } });

        if (editMode)
        {
            // 種別トグルはチャンネルリストのみ表示（休眠リストは編集モード中チャンネル名のみ）
            if (!isDormant)
            {
                var kindRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
                kindRow.Children.Add(MakeKindToggle("動画",  ch.NotifyVideo, ch.GetEffectiveModeForKind(VideoKind.Video),  VideoKind.Video,  v => { ch.NotifyVideo = v; SettingsService.Instance.UpdateChannelSilent(ch); SettingsService.Instance.MarkDirty(); AppLogger.Log(LogMsg.KindToggleChanged, ch.ChannelName, "動画",  v ? "ON" : "OFF"); }));
                kindRow.Children.Add(MakeKindToggle("Short", ch.NotifyShort, ch.GetEffectiveModeForKind(VideoKind.Short),  VideoKind.Short,  v => { ch.NotifyShort = v; SettingsService.Instance.UpdateChannelSilent(ch); SettingsService.Instance.MarkDirty(); AppLogger.Log(LogMsg.KindToggleChanged, ch.ChannelName, "Short", v ? "ON" : "OFF"); }));
                kindRow.Children.Add(MakeKindToggle("ライブ", ch.NotifyLive,  ch.GetEffectiveModeForKind(VideoKind.Live),   VideoKind.Live,   v => { ch.NotifyLive  = v; SettingsService.Instance.UpdateChannelSilent(ch); SettingsService.Instance.MarkDirty(); AppLogger.Log(LogMsg.KindToggleChanged, ch.ChannelName, "ライブ", v ? "ON" : "OFF"); }));
                kindRow.Children.Add(MakeFavoriteToggle(ch));
                info.Children.Add(kindRow);
            }
        }
        else
        {
            // 種別バッジ・動画タイトルプレビューはチャンネルリストのみ表示（休眠リストはチャンネル名のみ）
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

        var cardStatus = MonitorService.ResolveCardStatus(ch);
        var anyStatusShown = false;

        if (cardStatus.ActiveLiveEntries.Count > 0)
        {
            var bullet = new TextBlock { Margin = new Thickness(0, 0, 6, 0), FontSize = 13, Cursor = Cursors.Hand };
            bullet.Text = cardStatus.ActiveLiveEntries.Count == 1 ? "● ライブ配信中" : $"● ライブ配信中 ×{cardStatus.ActiveLiveEntries.Count}";
            SetDynamicBrush(bullet, TextBlock.ForegroundProperty, "ErrorBrush");

            var liveEntries = cardStatus.ActiveLiveEntries;
            if (liveEntries.Count == 1)
            {
                var entry = liveEntries[0];
                bullet.ToolTip = entry.Title;
                bullet.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    var owner = System.Windows.Window.GetWindow(bullet) as System.Windows.Window;
                    new VideoSummaryPopupWindow(owner!, ch, VideoKind.Live, entry.Title, entry.VideoId).Show();
                };
            }
            else
            {
                bullet.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    var owner = System.Windows.Window.GetWindow(bullet) as System.Windows.Window;
                    new VideoListPopupWindow(owner!, ch, VideoKind.Live, liveEntries).Show();
                };
            }

            row.Children.Add(bullet);
            anyStatusShown = true;
        }

        if (cardStatus.ActivePremiereEntries.Count > 0)
        {
            var bullet = new TextBlock { Margin = new Thickness(0, 0, 6, 0), FontSize = 13, Cursor = Cursors.Hand };
            bullet.Text = cardStatus.ActivePremiereEntries.Count == 1 ? "● プレミア公開中" : $"● プレミア公開中 ×{cardStatus.ActivePremiereEntries.Count}";
            SetDynamicBrush(bullet, TextBlock.ForegroundProperty, "WarningBrush");

            var premiereEntries = cardStatus.ActivePremiereEntries;
            if (premiereEntries.Count == 1)
            {
                var entry = premiereEntries[0];
                bullet.ToolTip = entry.Title;
                bullet.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    var owner = System.Windows.Window.GetWindow(bullet) as System.Windows.Window;
                    new VideoSummaryPopupWindow(owner!, ch, VideoKind.Premiere, entry.Title, entry.VideoId, allowSummary: false).Show();
                };
            }
            else
            {
                bullet.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    var owner = System.Windows.Window.GetWindow(bullet) as System.Windows.Window;
                    new VideoListPopupWindow(owner!, ch, VideoKind.Premiere, premiereEntries, allowSummary: false).Show();
                };
            }

            row.Children.Add(bullet);
            anyStatusShown = true;
        }

        if (cardStatus.PendingLiveDisplay != null)
        {
            var pendingLive = cardStatus.PendingLiveDisplay;
            var bullet = new TextBlock
            {
                Text = $"⏲ {pendingLive.ScheduledAt!.Value:HH:mm} から配信予定",
                Margin = new Thickness(0, 0, 6, 0),
                FontSize = 13, Opacity = 0.7,
                Cursor = Cursors.Hand,
                ToolTip = pendingLive.Title
            };
            SetDynamicBrush(bullet, TextBlock.ForegroundProperty, "WarningBrush");
            bullet.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                var owner = System.Windows.Window.GetWindow(bullet) as System.Windows.Window;
                new VideoSummaryPopupWindow(owner!, ch, VideoKind.Live, pendingLive.Title, pendingLive.VideoId, isPending: true).Show();
            };
            row.Children.Add(bullet);
            anyStatusShown = true;
        }

        if (cardStatus.PendingPremiereDisplay != null)
        {
            var pendingPremiere = cardStatus.PendingPremiereDisplay;
            var bullet = new TextBlock
            {
                Text = $"⏲ {pendingPremiere.ScheduledAt!.Value:HH:mm} からプレミア公開予定",
                Margin = new Thickness(0, 0, 6, 0),
                FontSize = 13, Opacity = 0.7,
                Cursor = Cursors.Hand,
                ToolTip = pendingPremiere.Title
            };
            SetDynamicBrush(bullet, TextBlock.ForegroundProperty, "WarningBrush");
            bullet.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                var owner = System.Windows.Window.GetWindow(bullet) as System.Windows.Window;
                new VideoSummaryPopupWindow(owner!, ch, VideoKind.Premiere, pendingPremiere.Title, pendingPremiere.VideoId, allowSummary: false).Show();
            };
            row.Children.Add(bullet);
            anyStatusShown = true;
        }

        if (anyStatusShown)
            return row;

        if (ch.NoVideosFound)
        {
            var noVideosText = new TextBlock { Text = "投稿された動画が見つかりません", FontSize = 11 };
            SetDynamicBrush(noVideosText, TextBlock.ForegroundProperty, "TextMutedBrush");
            row.Children.Add(noVideosText);
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
            var pillText = new TextBlock { Text = kindLabel, FontSize = 11 };
            SetDynamicBrush(pillText, TextBlock.ForegroundProperty, fgKey);
            pill.Child = pillText;

            var titleText = new TextBlock
            {
                Text = ch.LatestTitle != null && ch.LatestTitle.Length > ChannelCardTitleMaxLength ? ch.LatestTitle.Substring(0, ChannelCardTitleMaxLength) + "..." : ch.LatestTitle, FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 180,
                Cursor = Cursors.Hand,
                ToolTip = ch.LatestTitle
            };
            SetDynamicBrush(titleText, TextBlock.ForegroundProperty, "TextPrimaryBrush");
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

        var recentUploadsItem = new MenuItem { Header = "📜 最新動画一覧" };
        recentUploadsItem.Click += (_, _) =>
        {
            var filteredUploads = ch.RecentUploads
                .Where(v => IsRecentUploadKindEnabled(ch, v.Kind))
                .Take(RecentUploadsMaxEntries)
                .ToList();
            AppLogger.Log(LogMsg.RecentUploadsPopupOpened, null, ch.ChannelName, filteredUploads.Count);
            new VideoListPopupWindow(this, ch, filteredUploads).Show();
        };

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

        var sepClearRecent = new Separator();

        menu.Items.Add(clearItem);
        menu.Items.Add(sepClearRecent);
        menu.Items.Add(recentUploadsItem);
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
            sepClearRecent.Visibility    = _editMode ? Visibility.Collapsed : Visibility.Visible;
            recentUploadsItem.Visibility = _editMode ? Visibility.Collapsed : Visibility.Visible;
            recentUploadsItem.IsEnabled  = ch.RecentUploads.Any(v => IsRecentUploadKindEnabled(ch, v.Kind));
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

    /// <summary>RecentUploads（最新動画一覧）表示用に、動画種別が対応する通知トグルでONになっているかを判定する。</summary>
    private static bool IsRecentUploadKindEnabled(ChannelInfo channel, VideoKind kind) => kind switch
    {
        VideoKind.Video or VideoKind.Premiere => channel.NotifyVideo,
        VideoKind.Short                       => channel.NotifyShort,
        VideoKind.Live                         => channel.NotifyLive,
        _                                      => false
    };

    // ===== 種別トグル =====
    private static UIElement MakeKindToggle(string label, bool initial,
        YTNotifier.Models.MonitorMode mode, VideoKind kind,
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
        var color = active ? (System.Windows.Media.Brush)Application.Current.Resources["TextOnColorBrush"] : (System.Windows.Media.Brush)Application.Current.Resources["TextMutedBrush"];

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
}
