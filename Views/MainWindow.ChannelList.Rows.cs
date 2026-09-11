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

    /// <summary>「🔄 最新情報取得」の多重実行防止</summary>
    private bool _channelManualCheckInProgress;

    private const int    ChannelRowHeight        = 60;
    private const int    ChannelRowHeightCompact    = 36;

    private const double ChannelRowMarginBottom  = 0;

    /// <summary>ステータス行（種別ピル＋タイトル等）の上マージン</summary>
    private static readonly Thickness TitleRowMargin = new(0, 2, 0, 0);

    // 編集モード：種別トグルの固定色ブラシキー（定義は Themes/CommonStyles.xaml）
    private const string KindToggleOnNormalBrushKey  = "KindToggleOnNormalBrush";
    private const string KindToggleOnLowFreqBrushKey = "KindToggleOnLowFreqBrush";
    private const string KindToggleOnFocusBrushKey   = "KindToggleOnFocusBrush";
    private const string KindToggleOffBrushKey       = "KindToggleOffBrush";
    private const string KindToggleBorderBrushKey    = "KindToggleBorderBrush";
    private const string KindToggleIconOnBrushKey    = "KindToggleIconOnBrush";
    private const string KindToggleIconOffBrushKey   = "KindToggleIconOffBrush";

    private const double RowCornerRadius        = 4;   // チャンネル行の角丸
    private const double RowBorderThickness     = 1;   // チャンネル行の枠線
    private const double RowNewBarWidth         = 4;   // 未読を示す左端のバーの幅
    private const double RowEditIconColWidth    = 18;  // 編集モード：ドラッグハンドル列
    private const double RowEditNameColWidth    = 26;  // 編集モード：アイコン列
    private const double RowNormalHandleColWidth = 20; // 通常モード：ドラッグハンドル列
    private const double RowNormalIconColWidth  = 56;  // 通常モード：アイコン列
    private const double RowEditNameFontSize    = 12;  // 編集モードのチャンネル名
    private const double RowNameFontSize        = 13;  // 通常モードのチャンネル名・状態行
    private const double RowSubTextFontSize     = 11;  // 補足テキスト（種別ピル・通知なし等）
    private const double RowSubTextOpacity      = 0.7; // 補足テキストの薄さ
    private const double RowIconSize            = 44;  // チャンネルアイコンの一辺
    private const double RowIconSizeCompact     = 22;  // コンパクト時のチャンネルアイコンの一辺
    private const double RowDeleteButtonSize    = 28;  // 削除ボタンの一辺
    private const double RowDeleteIconSize      = 15;  // 削除ボタン内のアイコンの一辺
    private const double RowTrashCanvasSize     = 24;  // ゴミ箱アイコンの Canvas 一辺
    private const double RowTrashStrokeThickness = 2;  // ゴミ箱アイコンの線の太さ
    private const double RowKindPillCornerRadius = 3;  // 種別ピルの角丸
    private const double RowKindToggleCornerRadius = 4;// 編集モード：種別トグルの角丸
    private const double RowFavoriteFontSize    = 14;  // お気に入りの★
    private const double RowFavoriteBoxSize     = 24;  // お気に入りの Viewbox 一辺

    private static readonly Thickness RowContentMarginEdit   = new(4, 0, 8, 0);
    private static readonly Thickness RowContentMarginNormal = new(4, 0, 12, 0);
    private static readonly Thickness RowNameMargin          = new(8, 0, 0, 0);
    private static readonly Thickness RowIconMargin          = new(0, 0, 12, 0);
    private static readonly Thickness RowInfoMargin          = new(0, 0, 8, 0);
    private static readonly Thickness RowBulletMargin        = new(0, 0, 6, 0);
    private static readonly Thickness RowKindPillPadding     = new(4, 1, 4, 1);
    private static readonly Thickness RowKindPillMargin      = new(0, 0, 5, 0);
    private static readonly Thickness RowDeleteButtonMargin  = new(4, 0, 0, 0);
    private static readonly Thickness RowFavoriteMargin      = new(12, 0, 0, 0);

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
            CornerRadius        = new CornerRadius(RowCornerRadius),
            BorderThickness     = new Thickness(RowBorderThickness),
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
        outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RowNewBarWidth) });
        outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 新着帯（休眠リストでは常に非表示）
        var newBar = new Border
        {
            CornerRadius = new CornerRadius(RowNewBarWidth, 0, 0, RowNewBarWidth),
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

            var grid = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = RowContentMarginEdit };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RowEditIconColWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RowEditNameColWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(grid, 1);

            var handle   = BuildDragHandle(compact: true,  editMode: editMode);
            var icon     = BuildIconBorderCompact(ch);
            var nameText = new TextBlock
            {
                Text = ch.ChannelName, FontSize = RowEditNameFontSize, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = RowNameMargin
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
            var grid = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = RowContentMarginNormal };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RowNormalHandleColWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RowNormalIconColWidth) });
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
        var trashCanvas = new Canvas { Width = RowTrashCanvasSize, Height = RowTrashCanvasSize };
        foreach (var d in TrashIconPathData)
        {
            var p = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(d), StrokeThickness = RowTrashStrokeThickness,
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
            Width = RowDeleteButtonSize, Height = RowDeleteButtonSize,
            Content = new Viewbox { Width = RowDeleteIconSize, Height = RowDeleteIconSize, Child = BuildTrashIconCanvas() },
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand, Margin = RowDeleteButtonMargin,
            ToolTip = $"{ch.ChannelName} を削除"
        };
        btn.Click += (_, e) => { e.Handled = true; ConfirmAndDeleteChannel(ch); };
        return btn;
    }

    private static Border BuildIconBorderCompact(ChannelInfo ch)
    {
        var b = new Border
        {
            Width = RowIconSizeCompact, Height = RowIconSizeCompact, CornerRadius = new CornerRadius(RowIconSizeCompact / 2),
            VerticalAlignment = VerticalAlignment.Center,
            Clip = new EllipseGeometry(new System.Windows.Point(RowIconSizeCompact / 2.0, RowIconSizeCompact / 2.0), RowIconSizeCompact / 2.0, RowIconSizeCompact / 2.0),
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
            Width = RowIconSize, Height = RowIconSize, CornerRadius = new CornerRadius(RowIconSize / 2),
            Margin = RowIconMargin, VerticalAlignment = VerticalAlignment.Center,
            Clip   = new EllipseGeometry(new System.Windows.Point(RowIconSize / 2, RowIconSize / 2), RowIconSize / 2, RowIconSize / 2),
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
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = RowInfoMargin };

        if (ch.IsBanned)
        {
            var bannedText = new TextBlock
            {
                Text = "チャンネルは利用できません", FontSize = RowNameFontSize, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            SetDynamicBrush(bannedText, TextBlock.ForegroundProperty, "TextMutedBrush");
            info.Children.Add(bannedText);
            return info;
        }

        var nameText = new TextBlock
        {
            Text = ch.ChannelName, FontSize = RowNameFontSize, FontWeight = FontWeights.SemiBold,
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
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = TitleRowMargin };

        var cardStatus = MonitorService.ResolveCardStatus(ch);
        var anyStatusShown = false;

        if (cardStatus.ActiveLiveEntries.Count > 0)
        {
            var bullet = new TextBlock { Margin = RowBulletMargin, FontSize = RowNameFontSize, Cursor = Cursors.Hand };
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
            var bullet = new TextBlock { Margin = RowBulletMargin, FontSize = RowNameFontSize, Cursor = Cursors.Hand };
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
                Margin = RowBulletMargin,
                FontSize = RowNameFontSize, Opacity = RowSubTextOpacity,
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
                Margin = RowBulletMargin,
                FontSize = RowNameFontSize, Opacity = RowSubTextOpacity,
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
            var noVideosText = new TextBlock { Text = "投稿された動画が見つかりません", FontSize = RowSubTextFontSize };
            SetDynamicBrush(noVideosText, TextBlock.ForegroundProperty, "TextMutedBrush");
            row.Children.Add(noVideosText);
            return row;
        }

        if (ch.LatestVideoDeleted)
        {
            var deletedText = new TextBlock { Text = "動画は削除されました", FontSize = RowSubTextFontSize };
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
                CornerRadius = new CornerRadius(RowKindPillCornerRadius),
                Padding = RowKindPillPadding,
                Margin = RowKindPillMargin,
                VerticalAlignment = VerticalAlignment.Center
            };
            SetDynamicBrush(pill, Border.BackgroundProperty, bgKey);
            var pillText = new TextBlock { Text = kindLabel, FontSize = RowSubTextFontSize };
            SetDynamicBrush(pillText, TextBlock.ForegroundProperty, fgKey);
            pill.Child = pillText;

            var titleText = new TextBlock
            {
                Text = ch.LatestTitle,
                FontSize = RowSubTextFontSize,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center,
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
                var publishedAt = ch.RecentUploads.FirstOrDefault(r => r.VideoId == videoId)?.PublishedAt;
                titleText.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    var owner = System.Windows.Window.GetWindow(titleText) as System.Windows.Window;
                    new VideoSummaryPopupWindow(owner!, ch, kind, fullTitle, videoId, duration,
                        publishedAt: publishedAt).Show();
                };
            }

            // ピルを左固定、タイトルは残り幅いっぱい。
            // 水平 StackPanel（row）だと子に無限幅が渡り TextTrimming が効かないため、
            // 幅が確定する DockPanel に入れて自動省略（…）を機能させる。
            var titleRow = new DockPanel { Margin = TitleRowMargin };
            DockPanel.SetDock(pill, Dock.Left);
            titleRow.Children.Add(pill);
            titleRow.Children.Add(titleText); // LastChildFill=true（既定）で残り幅を占有
            return titleRow;
        }

        var noNotify = new TextBlock { Text = "通知なし", FontSize = RowSubTextFontSize };
        SetDynamicBrush(noNotify, TextBlock.ForegroundProperty, "TextMutedBrush");
        row.Children.Add(noNotify);
        return row;
    }

    private static StackPanel BuildActionsPanel(ChannelInfo ch)
    {
        var delBtn = new Button
        {
            Content = new Viewbox { Width = RowDeleteIconSize, Height = RowDeleteIconSize, Child = BuildTrashIconCanvas() },
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

        var recentUploadsItem = new MenuItem { Header = "📜 動画一覧" };
        recentUploadsItem.Click += (_, _) =>
        {
            var filteredUploads = ch.RecentUploads
                .Where(v => IsRecentUploadKindEnabled(ch, v.Kind))
                .Take(RecentUploadsMaxEntries)
                .ToList();
            AppLogger.Log(LogMsg.RecentUploadsPopupOpened, null, ch.ChannelName, filteredUploads.Count);
            new VideoListPopupWindow(this, ch, filteredUploads).Show();
        };

        var manualCheckItem = new MenuItem { Header = "🔄 最新情報取得" };
        manualCheckItem.Click += async (_, _) =>
        {
            if (_channelManualCheckInProgress) return;
            AppLogger.Log(LogMsg.ChannelContextManualCheck, null, ch.ChannelName);
            _channelManualCheckInProgress = true;
            try
            {
                await MonitorService.Instance.ManualCheckChannelAsync(ch);
                RefreshChannelList();
            }
            catch (Exception ex)
            {
                AppLogger.Log(LogMsg.CheckFailed, ch.ChannelName, ex.Message);
            }
            finally
            {
                _channelManualCheckInProgress = false;
            }
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
        menu.Items.Add(manualCheckItem);
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
            manualCheckItem.Visibility = _editMode ? Visibility.Collapsed : Visibility.Visible;

            var appState    = SettingsService.Instance.AppState;
            var quotaKey    = AppConstants.GetQuotaDayKey();
            var actualUnits = appState.TodayApiDate == quotaKey ? appState.TodayApiUnits : 0;
            var actualPct   = actualUnits * 100.0 / ApiQuotaHelper.DailyLimit;
            manualCheckItem.IsEnabled = actualPct <= ApiQuotaHelper.QuotaDisableThresholdPct
                                        && !MonitorService.Instance.QuotaSuspendedUntil.HasValue;
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
        // アクティブ時の背景色（差し色非依存の固定色）: 通常=青 / 低頻度=橙 / 時間指定=緑
        string ActiveBrushKey(bool on)
        {
            if (!on) return KindToggleOffBrushKey;
            return mode switch
            {
                YTNotifier.Models.MonitorMode.LowFreq => KindToggleOnLowFreqBrushKey,
                YTNotifier.Models.MonitorMode.Focus   => KindToggleOnFocusBrushKey,
                _                                     => KindToggleOnNormalBrushKey
            };
        }

        var border = new Border
        {
            CornerRadius = new CornerRadius(RowKindToggleCornerRadius), Padding = new Thickness(4),
            Margin = RowBulletMargin, Cursor = Cursors.Arrow,
            BorderThickness = new Thickness(1), ToolTip = label
        };
        SetDynamicBrush(border, Border.BorderBrushProperty, KindToggleBorderBrushKey);
        SetDynamicBrush(border, Border.BackgroundProperty, ActiveBrushKey(initial));
        border.Child = BuildKindIcon(kind, initial);

        bool current = initial;
        border.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (Application.Current.MainWindow is not MainWindow win || !win._editMode) return;
            e.Handled = true;
            current = !current;
            onChanged(current);
            SetDynamicBrush(border, Border.BackgroundProperty, ActiveBrushKey(current));
            border.Child = BuildKindIcon(kind, current);
        };
        return border;
    }

    private static UIElement BuildKindIcon(VideoKind kind, bool active)
    {
        var color = active
            ? (System.Windows.Media.Brush)Application.Current.Resources[KindToggleIconOnBrushKey]
            : (System.Windows.Media.Brush)Application.Current.Resources[KindToggleIconOffBrushKey];

        double canvasSize = kind == VideoKind.Live ? 24 : 22;
        var (icon, setColor) = KindIconFactory.Build(kind, iconSize: 18, canvasSize: canvasSize);
        setColor(color);
        return icon;
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
            Text = active ? "★" : "☆", FontSize = RowFavoriteFontSize, Foreground = color,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        return new Viewbox { Width = RowFavoriteBoxSize, Height = RowFavoriteBoxSize, Margin = RowFavoriteMargin, Child = text };
    }
}
