using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using YTNotifier.Plugin;
using YTNotifier.Services;
using Button              = System.Windows.Controls.Button;
using CheckBox            = System.Windows.Controls.CheckBox;
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
    private const string PluginJobsLabel        = "対応する仕事";
    private const string PluginEnabledCheckText = "有効";
    private const string PluginJobOrderHeading  = "仕事ごとの優先順位";
    private const string PluginMoveUpGlyph      = "▲";
    private const string PluginMoveDownGlyph    = "▼";
    private const string PluginMoveUpTip        = "上へ";
    private const string PluginMoveDownTip      = "下へ";
    private const string PluginMetaSeparator    = "  ・  ";

    // レイアウト
    private const double PluginCardCornerRadius = 6;
    private const double PluginCardBorderWidth  = 1;
    private const double PluginNameFontSize     = 12;
    private const double PluginMetaFontSize     = 11;
    private const double PluginJobHeadingFontSize = 10;
    private const double PluginJobNameFontSize  = 11;
    private const double PluginMoveButtonSize   = 22;
    private const int    PluginJobOrderMinPlugins = 2;

    private static readonly Thickness PluginCardMargin    = new(0, 0, 0, 8);
    private static readonly Thickness PluginCardPadding   = new(12, 10, 12, 10);
    private static readonly Thickness PluginMetaMargin    = new(0, 4, 0, 0);
    private static readonly Thickness PluginCheckMargin   = new(0, 8, 0, 0);
    private static readonly Thickness PluginJobBlockMargin = new(0, 0, 0, 12);
    private static readonly Thickness PluginJobRowMargin  = new(0, 3, 0, 0);
    private static readonly Thickness PluginMoveButtonMargin = new(4, 0, 0, 0);

    /// <summary>一覧の再構築中は有効チェックの変更ハンドラを黙らせる。</summary>
    private bool _rebuildingPluginSettings;

    /// <summary>
    /// 現在ホストが検出しているプラグインを読み、有効チェックと優先順位UIを組み立て直す。
    /// 記録だけ残って実体の無いフォルダは表示しない。
    /// </summary>
    private void RefreshPluginSettings()
    {
        if (PluginListPanel == null) return;

        _rebuildingPluginSettings = true;
        try
        {
            var plugins = PluginBridge.Instance.Plugins;

            PluginListPanel.Children.Clear();
            PluginJobOrderPanel.Children.Clear();

            if (plugins.Count == 0)
            {
                PluginEmptyText.Visibility     = Visibility.Visible;
                PluginJobOrderPanel.Visibility = Visibility.Collapsed;
                return;
            }

            PluginEmptyText.Visibility = Visibility.Collapsed;

            foreach (var descriptor in plugins)
                PluginListPanel.Children.Add(BuildPluginCard(descriptor));

            BuildJobOrderSection(plugins);
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

    private Border BuildPluginCard(PluginDescriptor descriptor)
    {
        var stack = new StackPanel();

        var nameText = new TextBlock
        {
            Text       = descriptor.Name,
            FontSize   = PluginNameFontSize,
            FontWeight = FontWeights.SemiBold,
        };
        SetDynamicBrush(nameText, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        stack.Children.Add(nameText);

        var metaParts = new List<string>
        {
            $"{PluginMetaFolderLabel}: {descriptor.Folder}",
        };
        if (!string.IsNullOrWhiteSpace(descriptor.Version))
            metaParts.Add($"{PluginMetaVersionLabel}: {descriptor.Version}");
        metaParts.Add($"{PluginMetaTypeLabel}: {descriptor.Type}");

        var metaText = new TextBlock
        {
            Text         = string.Join(PluginMetaSeparator, metaParts),
            FontSize     = PluginMetaFontSize,
            Margin       = PluginMetaMargin,
            TextWrapping = TextWrapping.Wrap,
        };
        SetDynamicBrush(metaText, TextBlock.ForegroundProperty, "TextMutedBrush");
        stack.Children.Add(metaText);

        var jobsText = new TextBlock
        {
            Text         = $"{PluginJobsLabel}: {string.Join(", ", descriptor.Jobs)}",
            FontSize     = PluginMetaFontSize,
            Margin       = PluginMetaMargin,
            TextWrapping = TextWrapping.Wrap,
        };
        SetDynamicBrush(jobsText, TextBlock.ForegroundProperty, "TextMutedBrush");
        stack.Children.Add(jobsText);

        var enabledCheck = new CheckBox
        {
            Content   = PluginEnabledCheckText,
            FontSize  = PluginMetaFontSize,
            Margin    = PluginCheckMargin,
            IsChecked = PluginBridge.Instance.IsPluginEnabled(descriptor.Folder),
            Tag       = descriptor.Folder,
        };
        SetDynamicBrush(enabledCheck, CheckBox.ForegroundProperty, "TextPrimaryBrush");
        enabledCheck.Checked   += PluginEnabledCheck_Changed;
        enabledCheck.Unchecked += PluginEnabledCheck_Changed;
        stack.Children.Add(enabledCheck);

        var card = new Border
        {
            CornerRadius    = new CornerRadius(PluginCardCornerRadius),
            BorderThickness = new Thickness(PluginCardBorderWidth),
            Padding         = PluginCardPadding,
            Margin          = PluginCardMargin,
            Child           = stack,
        };
        SetDynamicBrush(card, Border.BackgroundProperty, "SurfaceAltBrush");
        SetDynamicBrush(card, Border.BorderBrushProperty, "BorderBrush");
        return card;
    }

    private void BuildJobOrderSection(IReadOnlyList<PluginDescriptor> plugins)
    {
        var jobs = plugins
            .SelectMany(p => p.Jobs)
            .Distinct(StringComparer.Ordinal)
            .Where(job => plugins.Count(p => p.Jobs.Contains(job, StringComparer.Ordinal)) >= PluginJobOrderMinPlugins)
            .ToList();

        if (jobs.Count == 0)
        {
            PluginJobOrderPanel.Visibility = Visibility.Collapsed;
            return;
        }

        PluginJobOrderPanel.Visibility = Visibility.Visible;

        var heading = new TextBlock
        {
            Text       = PluginJobOrderHeading,
            FontSize   = PluginJobHeadingFontSize,
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(2, 0, 0, 6),
        };
        SetDynamicBrush(heading, TextBlock.ForegroundProperty, "TextMutedBrush");
        PluginJobOrderPanel.Children.Add(heading);

        foreach (var job in jobs)
            PluginJobOrderPanel.Children.Add(BuildJobOrderBlock(job, OrderedPluginsForJob(job, plugins)));
    }

    private List<PluginDescriptor> OrderedPluginsForJob(string job, IReadOnlyList<PluginDescriptor> plugins)
    {
        var order = PluginBridge.Instance.GetJobOrder(job);
        var rankByFolder = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < order.Count; i++)
            if (!rankByFolder.ContainsKey(order[i])) rankByFolder[order[i]] = i;

        return plugins
            .Where(p => p.Jobs.Contains(job, StringComparer.Ordinal))
            .OrderBy(p => rankByFolder.TryGetValue(p.Folder, out var rank) ? rank : int.MaxValue)
            .ToList();
    }

    private Border BuildJobOrderBlock(string job, List<PluginDescriptor> orderedPlugins)
    {
        var stack = new StackPanel();

        var jobName = new TextBlock
        {
            Text       = job,
            FontSize   = PluginJobNameFontSize,
            FontWeight = FontWeights.SemiBold,
        };
        SetDynamicBrush(jobName, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        stack.Children.Add(jobName);

        for (var index = 0; index < orderedPlugins.Count; index++)
        {
            var row = new Grid { Margin = PluginJobRowMargin };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var pluginName = new TextBlock
            {
                Text              = orderedPlugins[index].Name,
                FontSize          = PluginJobNameFontSize,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
            };
            SetDynamicBrush(pluginName, TextBlock.ForegroundProperty, "TextMutedBrush");
            Grid.SetColumn(pluginName, 0);
            row.Children.Add(pluginName);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(buttons, 1);

            buttons.Children.Add(BuildMoveButton(PluginMoveUpGlyph, PluginMoveUpTip, job, index, index - 1, index > 0));
            buttons.Children.Add(BuildMoveButton(PluginMoveDownGlyph, PluginMoveDownTip, job, index, index + 1, index < orderedPlugins.Count - 1));

            row.Children.Add(buttons);
            stack.Children.Add(row);
        }

        var block = new Border
        {
            CornerRadius    = new CornerRadius(PluginCardCornerRadius),
            BorderThickness = new Thickness(PluginCardBorderWidth),
            Padding         = PluginCardPadding,
            Margin          = PluginJobBlockMargin,
            Child           = stack,
        };
        SetDynamicBrush(block, Border.BackgroundProperty, "SurfaceAltBrush");
        SetDynamicBrush(block, Border.BorderBrushProperty, "BorderBrush");
        return block;
    }

    private Button BuildMoveButton(string glyph, string tip, string job, int fromIndex, int toIndex, bool enabled)
    {
        var button = new Button
        {
            Content  = glyph,
            ToolTip  = tip,
            Width    = PluginMoveButtonSize,
            Height   = PluginMoveButtonSize,
            FontSize = PluginJobNameFontSize,
            Margin   = PluginMoveButtonMargin,
            IsEnabled = enabled,
            Tag      = new PluginMove(job, fromIndex, toIndex),
        };
        button.Click += PluginMoveButton_Click;
        return button;
    }

    private void PluginEnabledCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_rebuildingPluginSettings) return;
        if (sender is not CheckBox check || check.Tag is not string folder) return;

        PluginBridge.Instance.SetPluginEnabled(folder, check.IsChecked == true);
        RefreshPluginSettings();
    }

    private void PluginMoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not PluginMove move) return;

        var plugins = PluginBridge.Instance.Plugins;
        var ordered = OrderedPluginsForJob(move.Job, plugins);
        if (move.FromIndex < 0 || move.FromIndex >= ordered.Count) return;
        if (move.ToIndex   < 0 || move.ToIndex   >= ordered.Count) return;

        var moved = ordered[move.FromIndex];
        ordered.RemoveAt(move.FromIndex);
        ordered.Insert(move.ToIndex, moved);

        PluginBridge.Instance.SetJobOrder(move.Job, ordered.Select(p => p.Folder).ToList());
        RefreshPluginSettings();
    }

    /// <summary>▲▼ ボタン1個分の並べ替え指示。</summary>
    private readonly record struct PluginMove(string Job, int FromIndex, int ToIndex);
}
