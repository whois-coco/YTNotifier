using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using YTNotifier.Models;
using YTNotifier.Services;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Point               = System.Windows.Point;
using Size                = System.Windows.Size;
using UserControl         = System.Windows.Controls.UserControl;
using VerticalAlignment   = System.Windows.VerticalAlignment;

namespace YTNotifier.Controls;

/// <summary>
/// API使用量を穴あきドーナツで表示する汎用の再利用部品。
/// 上限を1周（360°・12時起点／時計回り）とし、使用分だけ弧を描く。残りは薄いトラック。
/// 使用弧の内側は、渡された内訳セグメントの比率で分割して塗り分ける。
/// タイトル・中央パーセント・内訳・合計行を縦に並べる。計算ロジックは持たず、
/// <see cref="SetData"/> で受け取った値をそのまま描画する（呼び出しのたびに内部を再構築する）。
/// </summary>
public class QuotaDonut : UserControl
{
    // ===== 寸法・書式（この部品でしか参照しないためローカル定数）=====
    private const double TitleFontSize           = 11;
    private const double TitleBottomMargin       = 4;
    private const double DonutDiameter           = 108;
    private const double DonutRingThickness      = 13;
    private const double DonutVerticalMargin     = 6;
    private const double CenterPercentFontSize   = 20;
    private const double CenterSubFontSize       = 9;
    private const double CenterSubTopMargin      = 1;
    private const double BreakdownFontSize       = 10;
    private const double BreakdownRowTopMargin   = 2;
    private const double BreakdownSwatchSize     = 9;
    private const double BreakdownSwatchCornerRadius = 2;
    private const double BreakdownSwatchTopMargin    = 2;
    private const double BreakdownSwatchRightMargin  = 5;
    private const double BreakdownValueLeftMargin    = 6;
    private const double TotalRowFontSize        = 10;
    private const double TotalRowTopMargin       = 4;

    // 角度（度）
    private const double FullCircleDegrees        = 360.0;
    private const double StartAngleDegrees        = -90.0;  // 12時方向
    private const double LargeArcThresholdDegrees = 180.0;
    private const double NearFullCircleDegrees    = 359.9;  // これ以上は弧を閉じきらずわずかに隙間を残す

    // 書式文字列
    private const string PercentFormat        = "{0}%";
    private const string CenterSubFormat      = "{0:N0} / {1:N0}";
    private const string BreakdownValueFormat = "{0:N0}";
    private const string TotalRowFormat       = "{0:N0} / {1:N0} ユニット/日";
    // {0}=ラベル {1}=消費ユニット数 {2}=日次上限に対する割合(%)
    private const string SegTooltipFormat     = "{0}: {1:N0} ユニット（{2:F1}%）";

    // 固定ブラシキー（差し色非依存）
    private const string TrackBrushKey          = "SurfaceElevatedBrush";
    private const string TitleBrushKey          = "TextMutedBrush";
    private const string CenterSubBrushKey      = "TextMutedBrush";
    private const string TotalRowBrushKey       = "TextMutedBrush";
    private const string BreakdownLabelBrushKey = "TextMutedBrush";
    private const string BreakdownValueBrushKey = "TextPrimaryBrush";
    private const string PercentWarnHighBrushKey = "QuotaWarnHighBrush";
    private const string PercentWarnLowBrushKey  = "QuotaWarnLowBrush";
    private const string PercentOkBrushKey       = "SuccessBrush";

    /// <summary>
    /// 表示データを投入し、内部表示を再構築する。
    /// </summary>
    /// <param name="title">タイトル行の文字列</param>
    /// <param name="limitUnits">日次上限（1周＝360°に対応）</param>
    /// <param name="arcTotalUnits">使用弧の総量（中央パーセントの分子）。セグメント値の合計と一致しない場合がある</param>
    /// <param name="segments">使用弧を比率分割する内訳セグメント（ラベル・値・固定ブラシキー）</param>
    public void SetData(string title, int limitUnits, int arcTotalUnits, IReadOnlyList<QuotaDonutSegment> segments)
    {
        var root = new StackPanel();
        root.Children.Add(BuildTitle(title));
        root.Children.Add(BuildDonut(limitUnits, arcTotalUnits, segments));
        foreach (var segment in segments)
        {
            root.Children.Add(BuildBreakdownRow(segment));
        }
        root.Children.Add(BuildTotalRow(limitUnits, segments));
        Content = root;
    }

    private static TextBlock BuildTitle(string title)
    {
        var titleText = new TextBlock
        {
            Text                = title,
            FontSize            = TitleFontSize,
            TextAlignment       = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, 0, 0, TitleBottomMargin),
        };
        SetResourceRef(titleText, TextBlock.ForegroundProperty, TitleBrushKey);
        return titleText;
    }

    private static Grid BuildDonut(int limitUnits, int arcTotalUnits, IReadOnlyList<QuotaDonutSegment> segments)
    {
        var container = new Grid
        {
            Width               = DonutDiameter,
            Height              = DonutDiameter,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, DonutVerticalMargin, 0, DonutVerticalMargin),
        };

        var center = DonutDiameter / 2.0;
        var radius = (DonutDiameter - DonutRingThickness) / 2.0;

        var graphics = new Canvas();

        var track = new Ellipse
        {
            Width           = radius * 2.0,
            Height          = radius * 2.0,
            StrokeThickness = DonutRingThickness,
        };
        SetResourceRef(track, Shape.StrokeProperty, TrackBrushKey);
        Canvas.SetLeft(track, DonutRingThickness / 2.0);
        Canvas.SetTop(track, DonutRingThickness / 2.0);
        graphics.Children.Add(track);

        var segmentTotal = 0;
        foreach (var segment in segments) segmentTotal += segment.Units;

        var arcFraction = limitUnits > 0
            ? Math.Min(arcTotalUnits / (double)limitUnits, 1.0)
            : 0.0;
        var usedSweep = arcFraction * FullCircleDegrees;

        var cursorAngle = StartAngleDegrees;
        if (segmentTotal > 0 && usedSweep > 0)
        {
            foreach (var segment in segments)
            {
                var segmentSweep = Math.Min(usedSweep * segment.Units / segmentTotal, NearFullCircleDegrees);
                if (segmentSweep <= 0) continue;

                var arc = BuildArc(center, radius, cursorAngle, segmentSweep, segment.BrushKey);
                arc.ToolTip = string.Format(
                    CultureInfo.CurrentCulture, SegTooltipFormat,
                    segment.Label, segment.Units,
                    limitUnits > 0 ? segment.Units * 100.0 / limitUnits : 0.0);
                graphics.Children.Add(arc);
                cursorAngle += segmentSweep;
            }
        }

        container.Children.Add(graphics);
        container.Children.Add(BuildCenterText(limitUnits, arcTotalUnits));
        return container;
    }

    private static Path BuildArc(double center, double radius, double startDeg, double sweepDeg, string brushKey)
    {
        var startPoint = PointOnCircle(center, radius, startDeg);
        var endPoint   = PointOnCircle(center, radius, startDeg + sweepDeg);

        var figure = new PathFigure { StartPoint = startPoint, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment
        {
            Point          = endPoint,
            Size           = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc     = sweepDeg > LargeArcThresholdDegrees,
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        var arc = new Path
        {
            Data               = geometry,
            StrokeThickness    = DonutRingThickness,
            StrokeStartLineCap = PenLineCap.Flat,
            StrokeEndLineCap   = PenLineCap.Flat,
        };
        SetResourceRef(arc, Shape.StrokeProperty, brushKey);
        return arc;
    }

    private static Point PointOnCircle(double center, double radius, double angleDeg)
    {
        var angleRad = angleDeg * Math.PI / 180.0;
        return new Point(
            center + radius * Math.Cos(angleRad),
            center + radius * Math.Sin(angleRad));
    }

    private static StackPanel BuildCenterText(int limitUnits, int arcTotalUnits)
    {
        var percentValue = limitUnits > 0
            ? (int)Math.Round(arcTotalUnits * 100.0 / limitUnits)
            : 0;

        var percentText = new TextBlock
        {
            Text                = string.Format(CultureInfo.CurrentCulture, PercentFormat, percentValue),
            FontSize            = CenterPercentFontSize,
            FontWeight          = FontWeights.Bold,
            TextAlignment       = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Typography.SetNumeralAlignment(percentText, FontNumeralAlignment.Tabular);
        SetResourceRef(percentText, TextBlock.ForegroundProperty, PercentBrushKey(percentValue));

        var subText = new TextBlock
        {
            Text                = string.Format(CultureInfo.CurrentCulture, CenterSubFormat, arcTotalUnits, limitUnits),
            FontSize            = CenterSubFontSize,
            TextAlignment       = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, CenterSubTopMargin, 0, 0),
        };
        Typography.SetNumeralAlignment(subText, FontNumeralAlignment.Tabular);
        SetResourceRef(subText, TextBlock.ForegroundProperty, CenterSubBrushKey);

        return new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Children            = { percentText, subText },
        };
    }

    private static string PercentBrushKey(int percentValue)
        => percentValue >= ApiQuotaHelper.QuotaWarnHighThresholdPct ? PercentWarnHighBrushKey
         : percentValue >= ApiQuotaHelper.QuotaWarnLowThresholdPct  ? PercentWarnLowBrushKey
                                                                    : PercentOkBrushKey;

    private static Grid BuildBreakdownRow(QuotaDonutSegment segment)
    {
        var row = new Grid { Margin = new Thickness(0, BreakdownRowTopMargin, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var swatch = new Border
        {
            Width               = BreakdownSwatchSize,
            Height              = BreakdownSwatchSize,
            CornerRadius        = new CornerRadius(BreakdownSwatchCornerRadius),
            VerticalAlignment   = VerticalAlignment.Top,
            Margin              = new Thickness(0, BreakdownSwatchTopMargin, BreakdownSwatchRightMargin, 0),
        };
        SetResourceRef(swatch, Border.BackgroundProperty, segment.BrushKey);
        Grid.SetColumn(swatch, 0);

        var label = new TextBlock
        {
            Text              = segment.Label,
            FontSize          = BreakdownFontSize,
            TextWrapping      = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        SetResourceRef(label, TextBlock.ForegroundProperty, BreakdownLabelBrushKey);
        Grid.SetColumn(label, 1);

        var value = new TextBlock
        {
            Text                = string.Format(CultureInfo.CurrentCulture, BreakdownValueFormat, segment.Units),
            FontSize            = BreakdownFontSize,
            FontWeight          = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Center,
            Margin              = new Thickness(BreakdownValueLeftMargin, 0, 0, 0),
        };
        Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
        SetResourceRef(value, TextBlock.ForegroundProperty, BreakdownValueBrushKey);
        Grid.SetColumn(value, 2);

        row.Children.Add(swatch);
        row.Children.Add(label);
        row.Children.Add(value);
        return row;
    }

    private static TextBlock BuildTotalRow(int limitUnits, IReadOnlyList<QuotaDonutSegment> segments)
    {
        var segmentTotal = 0;
        foreach (var segment in segments) segmentTotal += segment.Units;

        var totalText = new TextBlock
        {
            Text                = string.Format(CultureInfo.CurrentCulture, TotalRowFormat, segmentTotal, limitUnits),
            FontSize            = TotalRowFontSize,
            TextAlignment       = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, TotalRowTopMargin, 0, 0),
        };
        SetResourceRef(totalText, TextBlock.ForegroundProperty, TotalRowBrushKey);
        return totalText;
    }

    private static void SetResourceRef(FrameworkElement element, DependencyProperty property, string resourceKey)
        => element.SetResourceReference(property, resourceKey);
}
