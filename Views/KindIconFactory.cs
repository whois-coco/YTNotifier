using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Brush   = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace YTNotifier.Views;

/// <summary>動画・Short・ライブの種別アイコン（Lucide 由来）を組み立てる。
/// チャンネル行・チャンネル追加・チャンネル詳細の3画面で共用する</summary>
internal static class KindIconFactory
{
    // Short: Lucide zap
    private const string ShortIconPath =
        "M4 14a1 1 0 0 1-.78-1.63l9.9-10.2a.5.5 0 0 1 .86.46l-1.92 6.02A1 1 0 0 0 13 10h7a1 1 0 0 1 .78 1.63l-9.9 10.2a.5.5 0 0 1-.86-.46l1.92-6.02A1 1 0 0 0 11 14z";

    // 動画: カメラ本体とレンズ
    private const string VideoBodyPath =
        "M2 6 L16 6 C17.105 6 18 6.895 18 8 L18 18 C18 19.105 17.105 20 16 20 L2 20 C0.895 20 0 19.105 0 18 L0 8 C0 6.895 0.895 6 2 6 Z";
    private const string VideoLensPath =
        "M16 13 L21.223 16.482 A0.5 0.5 0 0 0 22 16.066 L22 7.87 A0.5 0.5 0 0 0 21.248 7.438 L16 10.5 Z";

    // ライブ: 電波の弧4本＋スタンド2本
    private static readonly string[] LiveIconPaths =
    {
        "M4.9 16.1C1 12.2 1 5.8 4.9 1.9",
        "M7.8 4.7a6.14 6.14 0 0 0-.8 7.5",
        "M16.2 4.8c2 2 2.26 5.11.8 7.47",
        "M19.1 1.9a9.96 9.96 0 0 1 0 14.1",
        "M9.5 18h5",
        "m8 22 4-11 4 11"
    };

    private const double LiveEllipseSize     = 4;   // 中心の丸の直径
    private const double LiveEllipseLeft     = 10;  // Canvas 上の左位置
    private const double LiveEllipseTop      = 7;   // Canvas 上の上位置
    private const double IconStrokeThickness = 2;

    /// <summary>種別アイコンを組み立てて返す。色は setColor で後から変更できる</summary>
    /// <param name="kind">対象の種別（Video / Short / Live）</param>
    /// <param name="iconSize">Viewbox の一辺（px）</param>
    /// <param name="canvasSize">内部 Canvas の一辺（px）。Short では使用しない</param>
    internal static (UIElement icon, Action<Brush> setColor) Build(
        VideoKind kind, double iconSize, double canvasSize)
    {
        switch (kind)
        {
            case VideoKind.Short:
            {
                var path = new Path { Data = Geometry.Parse(ShortIconPath) };
                var viewbox = new Viewbox { Width = iconSize, Height = iconSize, Child = path };
                return (viewbox, color => path.Fill = color);
            }

            case VideoKind.Live:
            {
                var strokePaths = new List<Path>();
                var canvas = new Canvas { Width = canvasSize, Height = canvasSize };
                foreach (var d in LiveIconPaths)
                {
                    var path = new Path
                    {
                        Data               = Geometry.Parse(d),
                        Fill               = Brushes.Transparent,
                        StrokeThickness    = IconStrokeThickness,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap   = PenLineCap.Round,
                        StrokeLineJoin     = PenLineJoin.Round
                    };
                    strokePaths.Add(path);
                    canvas.Children.Add(path);
                }
                var ellipse = new Ellipse
                {
                    Width           = LiveEllipseSize,
                    Height          = LiveEllipseSize,
                    Fill            = Brushes.Transparent,
                    StrokeThickness = IconStrokeThickness
                };
                Canvas.SetLeft(ellipse, LiveEllipseLeft);
                Canvas.SetTop(ellipse, LiveEllipseTop);
                canvas.Children.Add(ellipse);

                var viewbox = new Viewbox { Width = iconSize, Height = iconSize, Child = canvas };
                return (viewbox, color =>
                {
                    foreach (var path in strokePaths) path.Stroke = color;
                    ellipse.Stroke = color;
                });
            }

            default: // VideoKind.Video
            {
                var body = new Path { Data = Geometry.Parse(VideoBodyPath) };
                var lens = new Path { Data = Geometry.Parse(VideoLensPath) };
                var canvas = new Canvas { Width = canvasSize, Height = canvasSize };
                canvas.Children.Add(body);
                canvas.Children.Add(lens);
                var viewbox = new Viewbox { Width = iconSize, Height = iconSize, Child = canvas };
                return (viewbox, color => { body.Fill = color; lens.Fill = color; });
            }
        }
    }
}
