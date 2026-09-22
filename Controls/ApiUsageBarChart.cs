using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using YTNotifier.Models;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using UserControl         = System.Windows.Controls.UserControl;
using VerticalAlignment   = System.Windows.VerticalAlignment;

namespace YTNotifier.Controls;

/// <summary>
/// API使用量の直近の日別推移を積み上げ棒グラフで表示する部品。
/// 縦軸は 0 〜 日次上限で固定し、上限の線と7日平均の線を引く。
/// 計算ロジックは持たず、<see cref="SetData"/> で受け取った系列をそのまま描画する（呼び出しのたびに内部を再構築する）。
/// 各日の幅は与えられた領域を等分する。
/// </summary>
public class ApiUsageBarChart : UserControl
{
    // ===== 寸法・書式（この部品でしか参照しないためローカル定数）=====
    private const double ChartHeight              = 110;   // 0 〜 日次上限に対応する棒の最大高さ
    private const double TotalLabelHeight         = 14;    // 棒の上の合計ユニット数の表示領域の高さ
    private const double TotalLabelFontSize       = 9;
    private const double BarSideMargin            = 4;     // 各日の列の左右に空ける余白（棒の幅を決める）
    private const double LineThickness            = 1;
    private const double LimitLabelFontSize       = 9;
    private const double LimitLabelLeftMargin     = 4;
    private const double LimitLabelTopMargin      = 7;     // 上限の線の高さに文字の中心を合わせるための上余白
    private const double DayLabelTopMargin        = 4;
    private const double WeekdayFontSize          = 10;
    private const double DateFontSize             = 9;
    private const double NoRecordOpacity          = 0.4;   // 記録なしの日のラベルの不透明度

    // 破線パターン（線の太さに対する 線の長さ, 間隔）
    private const double DashLength = 3;
    private const double DashGap    = 3;

    // 書式文字列
    private const string TotalLabelFormat     = "{0:N0}";
    private const string LimitLabelFormat     = "{0:N0}";
    // {0}=日付(M/d) {1}=曜日
    private const string TooltipHeaderFormat  = "{0}（{1}）";
    // {0}=ラベル {1}=消費ユニット数 {2}=日次上限に対する割合(%)
    private const string TooltipRowFormat     = "{0}: {1:N0} ユニット（{2:F1}%）";

    // 内訳のラベル
    private const string LabelNormal     = "通常巡回";
    private const string LabelPending    = "配信予定";
    private const string LabelLiveStatus = "配信中";
    private const string LabelTotal      = "合計";

    // 固定ブラシキー（差し色非依存。既存のAPI使用量ドーナツと同じ色）
    private const string BrushKeyNormal     = "QuotaActualNormalBrush";
    private const string BrushKeyPending    = "QuotaActualPendingBrush";
    private const string BrushKeyLiveStatus = "QuotaActualLiveStatusBrush";
    private const string LimitLineBrushKey  = "QuotaWarnHighBrush";
    private const string AverageLineBrushKey = "TextSecondaryBrush";
    private const string LimitLabelBrushKey = "TextSecondaryBrush";
    private const string TotalLabelBrushKey = "TextPrimaryBrush";
    private const string DayLabelBrushKey   = "TextSecondaryBrush";
    private const string TodayLabelBrushKey = "PrimaryBrush";

    /// <summary>
    /// 表示データを投入し、内部表示を再構築する。
    /// </summary>
    /// <param name="series">日別系列（古い順）と7日平均</param>
    /// <param name="limitUnits">日次上限（縦軸の上端）</param>
    public void SetData(ApiUsageSeries series, int limitUnits)
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var plot = BuildPlot(series, limitUnits);
        Grid.SetRow(plot, 0);
        Grid.SetColumn(plot, 0);
        root.Children.Add(plot);

        var limitLabel = BuildLimitLabel(limitUnits);
        Grid.SetRow(limitLabel, 0);
        Grid.SetColumn(limitLabel, 1);
        root.Children.Add(limitLabel);

        var dayLabels = BuildDayLabels(series);
        Grid.SetRow(dayLabels, 1);
        Grid.SetColumn(dayLabels, 0);
        root.Children.Add(dayLabels);

        Content = root;
    }

    private static Grid BuildPlot(ApiUsageSeries series, int limitUnits)
    {
        var plot = new Grid { Height = TotalLabelHeight + ChartHeight };

        for (var index = 0; index < series.Days.Count; index++)
        {
            plot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var dayColumn = BuildDayColumn(series.Days[index], limitUnits);
            Grid.SetColumn(dayColumn, index);
            plot.Children.Add(dayColumn);
        }

        if (series.Days.Count > 0)
        {
            var limitLine = BuildHorizontalLine(LimitLineBrushKey);
            limitLine.VerticalAlignment = VerticalAlignment.Top;
            limitLine.Margin            = new Thickness(0, TotalLabelHeight, 0, 0);
            Grid.SetColumnSpan(limitLine, series.Days.Count);
            plot.Children.Add(limitLine);

            if (series.AverageUnits is double averageUnits && limitUnits > 0)
            {
                var averageRatio = Math.Min(averageUnits / limitUnits, 1.0);
                var averageLine  = BuildHorizontalLine(AverageLineBrushKey);
                averageLine.VerticalAlignment = VerticalAlignment.Bottom;
                averageLine.Margin            = new Thickness(0, 0, 0, ChartHeight * averageRatio);
                Grid.SetColumnSpan(averageLine, series.Days.Count);
                plot.Children.Add(averageLine);
            }
        }

        return plot;
    }

    private static Grid BuildDayColumn(ApiUsageDayEntry day, int limitUnits)
    {
        // 背景を透明で塗ることで、列のどこにカーソルを合わせてもツールチップが出る
        var column = new Grid { Background = System.Windows.Media.Brushes.Transparent };
        if (!day.HasRecord) return column;

        var totalUnits = day.TotalUnits;
        // 合計が上限を超える場合は、上限の高さに収まるよう各セグメントを縮める
        var scale = limitUnits > 0 && totalUnits > limitUnits ? limitUnits / (double)totalUnits : 1.0;

        double HeightOf(int units) => limitUnits > 0 ? units / (double)limitUnits * ChartHeight * scale : 0.0;

        // 上から順に「配信中」「配信予定」「通常巡回」を積む（通常巡回が最下段）
        var bar = new StackPanel { Margin = new Thickness(BarSideMargin, 0, BarSideMargin, 0) };
        bar.Children.Add(BuildSegment(HeightOf(day.LiveStatusUnits), BrushKeyLiveStatus));
        bar.Children.Add(BuildSegment(HeightOf(day.PendingUnits),    BrushKeyPending));
        bar.Children.Add(BuildSegment(HeightOf(day.NormalUnits),     BrushKeyNormal));

        var totalLabel = new TextBlock
        {
            Text                = string.Format(CultureInfo.CurrentCulture, TotalLabelFormat, totalUnits),
            FontSize            = TotalLabelFontSize,
            Height              = TotalLabelHeight,
            TextAlignment       = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Bottom,
        };
        SetResourceRef(totalLabel, TextBlock.ForegroundProperty, TotalLabelBrushKey);

        column.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Children          = { totalLabel, bar },
        });
        column.ToolTip = BuildTooltip(day, limitUnits);
        return column;
    }

    private static Border BuildSegment(double height, string brushKey)
    {
        var segment = new Border { Height = height };
        SetResourceRef(segment, Border.BackgroundProperty, brushKey);
        return segment;
    }

    private static string BuildTooltip(ApiUsageDayEntry day, int limitUnits)
    {
        string Row(string label, int units)
            => string.Format(CultureInfo.CurrentCulture, TooltipRowFormat,
                label, units, limitUnits > 0 ? units * 100.0 / limitUnits : 0.0);

        var lines = new List<string>
        {
            string.Format(CultureInfo.CurrentCulture, TooltipHeaderFormat, day.DateLabel, day.WeekdayLabel),
            Row(LabelNormal,     day.NormalUnits),
            Row(LabelPending,    day.PendingUnits),
            Row(LabelLiveStatus, day.LiveStatusUnits),
            Row(LabelTotal,      day.TotalUnits),
        };
        return string.Join(Environment.NewLine, lines);
    }

    private static Line BuildHorizontalLine(string brushKey)
    {
        var line = new Line
        {
            X1              = 0,
            X2              = 1,
            Stretch         = Stretch.Fill,
            StrokeThickness = LineThickness,
            StrokeDashArray = new DoubleCollection { DashLength, DashGap },
            IsHitTestVisible = false,
        };
        SetResourceRef(line, Shape.StrokeProperty, brushKey);
        return line;
    }

    private static TextBlock BuildLimitLabel(int limitUnits)
    {
        var label = new TextBlock
        {
            Text              = string.Format(CultureInfo.CurrentCulture, LimitLabelFormat, limitUnits),
            FontSize          = LimitLabelFontSize,
            VerticalAlignment = VerticalAlignment.Top,
            Margin            = new Thickness(LimitLabelLeftMargin, LimitLabelTopMargin, 0, 0),
        };
        SetResourceRef(label, TextBlock.ForegroundProperty, LimitLabelBrushKey);
        return label;
    }

    private static Grid BuildDayLabels(ApiUsageSeries series)
    {
        var labels = new Grid { Margin = new Thickness(0, DayLabelTopMargin, 0, 0) };

        for (var index = 0; index < series.Days.Count; index++)
        {
            var day = series.Days[index];
            labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var weekdayText = new TextBlock
            {
                Text                = day.WeekdayLabel,
                FontSize            = WeekdayFontSize,
                FontWeight          = day.IsToday ? FontWeights.Bold : FontWeights.Normal,
                TextAlignment       = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            SetResourceRef(weekdayText, TextBlock.ForegroundProperty, day.IsToday ? TodayLabelBrushKey : DayLabelBrushKey);

            var dateText = new TextBlock
            {
                Text                = day.DateLabel,
                FontSize            = DateFontSize,
                TextAlignment       = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            SetResourceRef(dateText, TextBlock.ForegroundProperty, DayLabelBrushKey);

            var cell = new StackPanel
            {
                Opacity  = day.HasRecord ? 1.0 : NoRecordOpacity,
                Children = { weekdayText, dateText },
            };
            Grid.SetColumn(cell, index);
            labels.Children.Add(cell);
        }

        return labels;
    }

    private static void SetResourceRef(FrameworkElement element, DependencyProperty property, string resourceKey)
        => element.SetResourceReference(property, resourceKey);
}
