using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using YTNotifier.Constants;
using YTNotifier.Plugin;
using YTNotifier.Services;
using Brushes             = System.Windows.Media.Brushes;
using Button              = System.Windows.Controls.Button;
using CheckBox            = System.Windows.Controls.CheckBox;
using Cursors             = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation         = System.Windows.Controls.Orientation;

namespace YTNotifier.Views;

public partial class MainWindow : System.Windows.Window
{
    // ===== プラグイン設定ページ =====

    // 表示テキスト
    private const string PluginMetaFolderLabel  = "フォルダ";
    private const string PluginMetaVersionLabel = "版";
    private const string PluginMetaTypeLabel    = "種類";
    private const string PluginMetaAuthorLabel  = "製作者";
    private const string PluginMetaAuthorFallback = "-";
    private const string PluginJobsLabel        = "対応する仕事";
    // 「動画詳細ポップアップに「チャプター」ボタンを追加（仕事: chapters）」を組み立てる区切り
    private const string PluginContributionSepOpen  = "に「";
    private const string PluginContributionSepMid   = "」ボタンを追加（仕事: ";
    private const string PluginContributionSepClose = "）";
    private const string PluginDetailToggleGlyphCollapsed = "▼";
    private const string PluginDetailToggleGlyphExpanded  = "▲";
    private const string PluginDetailToggleTipShow = "詳細を表示";
    private const string PluginDetailToggleTipHide  = "詳細を隠す";
    private const string PluginMetaSeparator    = "  ・  ";
    private const string PluginDragHandleGlyph  = "⠿";
    private const string PluginDragHandleTip    = "ドラッグして並び替え";

    // レイアウト
    private const double PluginCardCornerRadius = 6;
    private const double PluginCardBorderWidth  = 1;
    private const double PluginNameFontSize     = 12;
    private const double PluginMetaFontSize     = 11;
    private const double PluginJobNameFontSize  = 11;
    private const double PluginDetailToggleButtonSize = 22;
    private const double PluginDragHandleWidth  = 20;
    private const double PluginDragHandleFontSize = 14;

    /// <summary>ドラッグ入れ替えアニメーションの時間（ミリ秒）。チャンネル一覧のD&amp;Dと同じ値。</summary>
    private const int PluginDndReorderAnimDurationMs = 150;

    private static readonly Thickness PluginCardMargin    = new(0, 0, 0, 8);
    private static readonly Thickness PluginCardPadding   = new(12, 10, 12, 10);
    private static readonly Thickness PluginMetaMargin    = new(0, 4, 0, 0);

    /// <summary>一覧の再構築中は有効チェックの変更ハンドラを黙らせる。</summary>
    private bool _rebuildingPluginSettings;

    /// <summary>詳細を展開中のプラグインのフォルダ名。一覧再構築をまたいで状態を保つ。</summary>
    private readonly HashSet<string> _expandedPluginFolders = new(StringComparer.Ordinal);

    // D&D（プラグイン一覧の並び替え。Views/MainWindow.ChannelList.Dnd.cs のD&Dと同方式・状態は独立）
    private PluginDescriptor? _pluginDragSource     = null;
    private bool              _pluginIsDragging     = false;
    private int               _pluginAnimDropIndex  = -1;
    private Border?           _pluginDragSourceRow  = null;

    /// <summary>
    /// 現在ホストが検出しているプラグインを読み、有効チェック・詳細折りたたみ・並び順UIを組み立て直す。
    /// 記録だけ残って実体の無いフォルダは表示しない。
    /// </summary>
    private void RefreshPluginSettings()
    {
        if (PluginListPanel == null) return;

        _rebuildingPluginSettings = true;
        try
        {
            var plugins = OrderedPlugins(PluginBridge.Instance.Plugins);

            PluginListPanel.Children.Clear();

            if (plugins.Count == 0)
            {
                PluginEmptyText.Visibility = Visibility.Visible;
                return;
            }

            PluginEmptyText.Visibility = Visibility.Collapsed;

            foreach (var descriptor in plugins)
                PluginListPanel.Children.Add(BuildPluginCard(descriptor));
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.UiUpdateFailed, null, nameof(RefreshPluginSettings), ex.Message);
        }
        finally
        {
            _rebuildingPluginSettings = false;
        }
    }

    /// <summary>
    /// プラグインを表示・優先順（<see cref="PluginBridge.GetPluginOrder"/>）で並べる。
    /// 記録に無いプラグインは末尾（発見順を保つ安定ソート）。
    /// </summary>
    private static List<PluginDescriptor> OrderedPlugins(IReadOnlyList<PluginDescriptor> plugins)
    {
        var order        = PluginBridge.Instance.GetPluginOrder();
        var rankByFolder = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < order.Count; i++)
            if (!rankByFolder.ContainsKey(order[i])) rankByFolder[order[i]] = i;

        return plugins
            .OrderBy(p => rankByFolder.TryGetValue(p.Folder, out var rank) ? rank : int.MaxValue)
            .ToList();
    }

    private Border BuildPluginCard(PluginDescriptor descriptor)
    {
        var stack = new StackPanel();
        var isExpanded = _expandedPluginFolders.Contains(descriptor.Folder);

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dragHandle = BuildPluginDragHandle();
        Grid.SetColumn(dragHandle, 0);
        headerGrid.Children.Add(dragHandle);

        var enabledCheck = new CheckBox
        {
            Content    = descriptor.Name,
            Style      = (Style)FindResource("ModernCheckBox"),
            FontSize   = PluginNameFontSize,
            FontWeight = FontWeights.SemiBold,
            IsChecked  = PluginBridge.Instance.IsPluginEnabled(descriptor.Folder),
            Tag        = descriptor.Folder,
        };
        SetDynamicBrush(enabledCheck, CheckBox.ForegroundProperty, "TextPrimaryBrush");
        enabledCheck.Checked   += PluginEnabledCheck_Changed;
        enabledCheck.Unchecked += PluginEnabledCheck_Changed;
        Grid.SetColumn(enabledCheck, 1);
        headerGrid.Children.Add(enabledCheck);

        var toggleButton = new Button
        {
            Style     = (Style)FindResource("IconButton"),
            Content   = isExpanded ? PluginDetailToggleGlyphExpanded : PluginDetailToggleGlyphCollapsed,
            ToolTip   = isExpanded ? PluginDetailToggleTipHide : PluginDetailToggleTipShow,
            Width     = PluginDetailToggleButtonSize,
            Height    = PluginDetailToggleButtonSize,
            FontSize  = PluginJobNameFontSize,
            Tag       = descriptor.Folder,
        };
        SetDynamicBrush(toggleButton, Button.ForegroundProperty, "AccentBrush");
        toggleButton.Click += PluginDetailToggleButton_Click;
        Grid.SetColumn(toggleButton, 2);
        headerGrid.Children.Add(toggleButton);

        stack.Children.Add(headerGrid);

        var detailsPanel = new StackPanel
        {
            Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed,
        };

        var metaParts = new List<string>
        {
            $"{PluginMetaFolderLabel}: {descriptor.Folder}",
        };
        if (!string.IsNullOrWhiteSpace(descriptor.Version))
            metaParts.Add($"{PluginMetaVersionLabel}: {descriptor.Version}");
        metaParts.Add($"{PluginMetaTypeLabel}: {descriptor.Type}");
        var authorName = string.IsNullOrWhiteSpace(descriptor.Author) ? PluginMetaAuthorFallback : descriptor.Author;
        metaParts.Add($"{PluginMetaAuthorLabel}: {authorName}");

        var metaText = new TextBlock
        {
            Text         = string.Join(PluginMetaSeparator, metaParts),
            FontSize     = PluginMetaFontSize,
            Margin       = PluginMetaMargin,
            TextWrapping = TextWrapping.Wrap,
        };
        SetDynamicBrush(metaText, TextBlock.ForegroundProperty, "TextMutedBrush");
        detailsPanel.Children.Add(metaText);

        var jobsText = new TextBlock
        {
            Text         = $"{PluginJobsLabel}: {string.Join(", ", descriptor.Jobs)}",
            FontSize     = PluginMetaFontSize,
            Margin       = PluginMetaMargin,
            TextWrapping = TextWrapping.Wrap,
        };
        SetDynamicBrush(jobsText, TextBlock.ForegroundProperty, "TextMutedBrush");
        detailsPanel.Children.Add(jobsText);

        var contributionLines = BuildContributionLines(descriptor);
        if (contributionLines.Count > 0)
        {
            var contributesText = new TextBlock
            {
                Text         = string.Join(Environment.NewLine, contributionLines),
                FontSize     = PluginMetaFontSize,
                Margin       = PluginMetaMargin,
                TextWrapping = TextWrapping.Wrap,
            };
            SetDynamicBrush(contributesText, TextBlock.ForegroundProperty, "TextMutedBrush");
            detailsPanel.Children.Add(contributesText);
        }

        stack.Children.Add(detailsPanel);

        var card = new Border
        {
            CornerRadius    = new CornerRadius(PluginCardCornerRadius),
            BorderThickness = new Thickness(PluginCardBorderWidth),
            Padding         = PluginCardPadding,
            Margin          = PluginCardMargin,
            Child           = stack,
            Tag             = descriptor,
        };
        SetDynamicBrush(card, Border.BackgroundProperty, "SurfaceAltBrush");
        SetDynamicBrush(card, Border.BorderBrushProperty, "BorderBrush");

        AttachPluginDragEvents(dragHandle, card, descriptor);
        return card;
    }

    private void PluginEnabledCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_rebuildingPluginSettings) return;
        if (sender is not CheckBox check || check.Tag is not string folder) return;

        PluginBridge.Instance.SetPluginEnabled(folder, check.IsChecked == true);
        RefreshPluginSettings();
    }

    private void PluginDetailToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string folder) return;

        if (!_expandedPluginFolders.Remove(folder))
            _expandedPluginFolders.Add(folder);

        RefreshPluginSettings();
    }

    // ===== 画面に追加される項目の列挙 =====

    /// <summary>
    /// このプラグインが画面へ差し込む項目を1コマンド1行で列挙する。
    /// 本体が知らないリージョンID宛の行はスキップする。
    /// </summary>
    private static List<string> BuildContributionLines(PluginDescriptor descriptor)
    {
        var lines = new List<string>();
        var commands = descriptor.Contributes?.Commands;
        if (commands == null) return lines;

        foreach (var command in commands)
        {
            foreach (var regionId in command.Regions)
            {
                if (!AppConstants.PluginRegionDisplayNames.TryGetValue(regionId, out var regionName)) continue;
                lines.Add(regionName + PluginContributionSepOpen + command.Title +
                          PluginContributionSepMid + command.Job + PluginContributionSepClose);
            }
        }
        return lines;
    }

    // ===== ドラッグ＆ドロップによる並び替え =====

    private static Border BuildPluginDragHandle()
    {
        var dots = new TextBlock
        {
            Text = PluginDragHandleGlyph, FontSize = PluginDragHandleFontSize,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
        SetDynamicBrush(dots, TextBlock.ForegroundProperty, "TextMutedBrush");
        return new Border
        {
            VerticalAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Center,
            Width = PluginDragHandleWidth, Cursor = Cursors.SizeAll,
            Background = Brushes.Transparent,
            ToolTip = PluginDragHandleTip, Child = dots,
        };
    }

    private void AttachPluginDragEvents(Border handle, Border row, PluginDescriptor descriptor)
    {
        System.Windows.Point dragStartPos = default;
        bool dragReady = false;

        handle.MouseLeftButtonDown += (_, e) =>
        {
            if (_pluginIsDragging) return;
            dragStartPos      = e.GetPosition(PluginListPanel);
            dragReady         = true;
            _pluginDragSource = descriptor;
            handle.CaptureMouse();
        };

        handle.MouseMove += (_, e) =>
        {
            if (!dragReady || e.LeftButton != MouseButtonState.Pressed) return;
            var pos  = e.GetPosition(PluginListPanel);
            var diff = pos - dragStartPos;

            if (!_pluginIsDragging)
            {
                if (Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _pluginIsDragging    = true;
                _pluginDragSourceRow = row;
                _pluginAnimDropIndex = -1;
                Mouse.OverrideCursor = Cursors.SizeNS;
                InitPluginRowTransforms();
            }

            if (_pluginDragSourceRow?.RenderTransform is TranslateTransform srcTt)
                srcTt.Y = pos.Y - dragStartPos.Y;

            UpdatePluginSwapAnimation(CalcPluginSwapIndex(pos.Y));
        };

        handle.MouseLeftButtonUp += (_, e) =>
        {
            if (!dragReady && !_pluginIsDragging) return;
            var wasDragging   = _pluginIsDragging;
            dragReady         = false;
            _pluginIsDragging = false;
            handle.ReleaseMouseCapture();
            Mouse.OverrideCursor = null;

            if (wasDragging && _pluginDragSourceRow != null)
            {
                var relIdx = CalcPluginSwapIndex(e.GetPosition(PluginListPanel).Y);
                CommitPluginSwap(descriptor, relIdx);
            }
            else
            {
                ResetPluginRowTransforms();
                _pluginDragSourceRow = null;
                _pluginDragSource    = null;
                _pluginAnimDropIndex = -1;
            }
        };

        handle.LostMouseCapture += (_, _) =>
        {
            if (!_pluginIsDragging) return;
            ResetPluginRowTransforms();
            _pluginDragSourceRow = null; _pluginDragSource = null;
            _pluginAnimDropIndex = -1;   _pluginIsDragging  = false;
            Mouse.OverrideCursor = null;
        };
    }

    // ドラッグ開始時に全行へ TranslateTransform を付与
    private void InitPluginRowTransforms()
    {
        foreach (var row in PluginListPanel.Children.OfType<Border>())
        {
            if (row.RenderTransform is TranslateTransform tt) tt.Y = 0;
            else row.RenderTransform = new TranslateTransform(0, 0);
        }
    }

    // マウスY座標から「ドラッグ元が落ち着くべきインデックス」を返す
    private int CalcPluginSwapIndex(double mouseY)
    {
        var rows = PluginListPanel.Children.OfType<Border>().ToList();
        if (rows.Count == 0) return -1;

        double cumY = 0;
        var centers = new List<double>();
        foreach (var row in rows)
        {
            double rowH = row.ActualHeight + row.Margin.Top + row.Margin.Bottom;
            centers.Add(cumY + rowH / 2);
            cumY += rowH;
        }

        int best = 0; double bestDist = double.MaxValue;
        for (int i = 0; i < centers.Count; i++)
        {
            double d = Math.Abs(mouseY - centers[i]);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    // ドラッグ元以外の行を「ドラッグ元が best の位置にいる」ように見せるアニメーション
    private void UpdatePluginSwapAnimation(int best)
    {
        if (best < 0 || best == _pluginAnimDropIndex) return;
        _pluginAnimDropIndex = best;
        if (_pluginDragSourceRow == null) return;

        var rows   = PluginListPanel.Children.OfType<Border>().ToList();
        var srcIdx = rows.IndexOf(_pluginDragSourceRow);
        if (srcIdx < 0) return;

        var duration = new Duration(TimeSpan.FromMilliseconds(PluginDndReorderAnimDurationMs));
        double rowH  = _pluginDragSourceRow.ActualHeight + _pluginDragSourceRow.Margin.Bottom;

        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i] == _pluginDragSourceRow) continue;
            if (rows[i].RenderTransform is not TranslateTransform tt)
            {
                tt = new TranslateTransform(0, 0);
                rows[i].RenderTransform = tt;
            }

            double target = 0;
            if (srcIdx < best && i > srcIdx && i <= best) target = -rowH;
            else if (srcIdx > best && i >= best && i < srcIdx) target = rowH;

            if (Math.Abs(tt.Y - target) < 1) continue;
            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(tt.Y, target, duration)
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    // ドロップ確定・キャンセルいずれでも全行の Transform をリセット
    private void ResetPluginRowTransforms()
    {
        foreach (var row in PluginListPanel.Children.OfType<Border>())
        {
            if (row.RenderTransform is not TranslateTransform tt) continue;
            tt.BeginAnimation(TranslateTransform.YProperty, null);
            tt.Y = 0;
        }
    }

    // データを実際に並び替えて conf\plugins.json へ保存し、一覧を再描画
    private void CommitPluginSwap(PluginDescriptor srcDescriptor, int destRelIdx)
    {
        var ordered = OrderedPlugins(PluginBridge.Instance.Plugins);
        var srcIdx  = ordered.FindIndex(p => p.Folder == srcDescriptor.Folder);

        if (srcIdx < 0 || destRelIdx < 0 || destRelIdx == srcIdx)
        {
            ResetPluginRowTransforms();
            _pluginDragSourceRow = null; _pluginDragSource = null;
            _pluginAnimDropIndex = -1;   _pluginIsDragging  = false;
            return;
        }

        var moving = ordered[srcIdx];
        ordered.RemoveAt(srcIdx);
        var insertIdx = destRelIdx > srcIdx ? destRelIdx - 1 : destRelIdx;
        ordered.Insert(Math.Clamp(insertIdx, 0, ordered.Count), moving);

        ResetPluginRowTransforms();
        _pluginDragSourceRow = null; _pluginDragSource = null;
        _pluginAnimDropIndex = -1;   _pluginIsDragging  = false;

        PluginBridge.Instance.SetPluginOrder(ordered.Select(p => p.Folder).ToList());
        AppLogger.Log(LogMsg.PluginOrderChanged, null, moving.Name);
        RefreshPluginSettings();
    }
}
