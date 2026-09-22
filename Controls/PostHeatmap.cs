using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using YTNotifier.Models;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using UserControl         = System.Windows.Controls.UserControl;
using VerticalAlignment   = System.Windows.VerticalAlignment;

namespace YTNotifier.Controls;

/// <summary>
/// 投稿時間帯を「時間（縦・24行）× 曜日（横・7列）」のヒートマップで表示する部品。
/// 件数0のセルは薄い面色、1件以上のセルは主色を最大値に対する割合の不透明度で塗る。
/// 計算ロジックは持たず、<see cref="SetData"/> で受け取った集計結果をそのまま描画する（呼び出しのたびに内部を再構築する）。
/// </summary>
public class PostHeatmap : UserControl
{
    // ===== 寸法・書式（この部品でしか参照しないためローカル定数）=====
    private const double CellHeight            = 12;
    private const double CellGap               = 1;      // セルの上下左右に空ける余白
    private const double CellCornerRadius      = 2;
    private const double MinCellOpacity        = 0.25;   // 1件以上のセルの最小の不透明度（1件でも識別できる値）
    private const double WeekdayFontSize       = 10;
    private const double WeekdayBottomMargin   = 2;
    private const double HourLabelFontSize     = 9;
    private const double HourLabelRightMargin  = 4;
    private const double EmptyMessageFontSize  = 11;
    private const double EmptyMessageMargin    = 12;
    private const int    HourLabelInterval     = 3;      // 時間ラベルを出す間隔（時）

    // 書式文字列・文言
    // {0}=時
    private const string HourLabelFormat = "{0}時";
    // {0}=曜日 {1}=時 {2}=件数
    private const string CellTooltipFormat = "{0}曜 {1}時台: {2}件";
    private const string EmptyMessage      = "集計できる投稿がありません";

    // 固定ブラシキー
    private const string ZeroCellBrushKey   = "SurfaceElevatedBrush";
    private const string FilledCellBrushKey = "PrimaryBrush";
    private const string LabelBrushKey      = "TextSecondaryBrush";

    /// <summary>
    /// 表示データを投入し、内部表示を再構築する。
    /// </summary>
    /// <param name="data">曜日×時の件数表と最大値・総件数</param>
    public void SetData(PostHeatmapData data)
    {
        var weekdayCount = data.Counts.GetLength(0);
        var hourCount    = data.Counts.GetLength(1);

        if (data.TotalCount <= 0 || data.MaxCount <= 0 || weekdayCount == 0 || hourCount == 0)
        {
            Content = BuildEmptyMessage();
            return;
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var column = 0; column < weekdayCount; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var hour = 0; hour < hourCount; hour++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CellHeight) });

        // 曜日ラベル（上）
        for (var column = 0; column < weekdayCount; column++)
        {
            var weekdayText = BuildLabel(data.WeekdayLabels[column], WeekdayFontSize);
            weekdayText.HorizontalAlignment = HorizontalAlignment.Center;
            weekdayText.Margin              = new Thickness(0, 0, 0, WeekdayBottomMargin);
            Grid.SetRow(weekdayText, 0);
            Grid.SetColumn(weekdayText, column + 1);
            grid.Children.Add(weekdayText);
        }

        for (var hour = 0; hour < hourCount; hour++)
        {
            // 時間ラベル（左・3時間ごと）
            if (hour % HourLabelInterval == 0)
            {
                var hourText = BuildLabel(string.Format(CultureInfo.CurrentCulture, HourLabelFormat, hour), HourLabelFontSize);
                hourText.HorizontalAlignment = HorizontalAlignment.Right;
                hourText.VerticalAlignment   = VerticalAlignment.Center;
                hourText.Margin              = new Thickness(0, 0, HourLabelRightMargin, 0);
                Grid.SetRow(hourText, hour + 1);
                Grid.SetColumn(hourText, 0);
                grid.Children.Add(hourText);
            }

            for (var column = 0; column < weekdayCount; column++)
            {
                var cell = BuildCell(data, column, hour);
                Grid.SetRow(cell, hour + 1);
                Grid.SetColumn(cell, column + 1);
                grid.Children.Add(cell);
            }
        }

        Content = grid;
    }

    private static Border BuildCell(PostHeatmapData data, int weekdayColumn, int hour)
    {
        var count = data.Counts[weekdayColumn, hour];

        var cell = new Border
        {
            Margin       = new Thickness(CellGap),
            CornerRadius = new CornerRadius(CellCornerRadius),
            ToolTip      = string.Format(CultureInfo.CurrentCulture, CellTooltipFormat,
                               data.WeekdayLabels[weekdayColumn], hour, count),
        };

        if (count > 0)
        {
            SetResourceRef(cell, Border.BackgroundProperty, FilledCellBrushKey);
            cell.Opacity = MinCellOpacity + (1.0 - MinCellOpacity) * count / data.MaxCount;
        }
        else
        {
            SetResourceRef(cell, Border.BackgroundProperty, ZeroCellBrushKey);
        }
        return cell;
    }

    private static TextBlock BuildLabel(string text, double fontSize)
    {
        var label = new TextBlock
        {
            Text     = text,
            FontSize = fontSize,
        };
        SetResourceRef(label, TextBlock.ForegroundProperty, LabelBrushKey);
        return label;
    }

    private static TextBlock BuildEmptyMessage()
    {
        var message = BuildLabel(EmptyMessage, EmptyMessageFontSize);
        message.HorizontalAlignment = HorizontalAlignment.Center;
        message.Margin              = new Thickness(0, EmptyMessageMargin, 0, EmptyMessageMargin);
        return message;
    }

    private static void SetResourceRef(FrameworkElement element, DependencyProperty property, string resourceKey)
        => element.SetResourceReference(property, resourceKey);
}
