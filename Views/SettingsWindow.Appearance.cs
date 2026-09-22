using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YTNotifier.Constants;
using YTNotifier.Models;
using YTNotifier.Services;
using Cursors = System.Windows.Input.Cursors;

namespace YTNotifier.Views;

// 部分クラス: ウィンドウ色・差し色（アクセント色）
public partial class SettingsWindow : Window
{
    private void WindowColorComboBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        if (WindowColorComboBox.SelectedItem is not WindowColorOption option) return;

        SettingsService.Instance.Settings.Theme = option.Theme;
        SettingsService.Instance.MarkDirty();
        AppLogger.Log(LogMsg.SettingWindowColor, null, option.Label);
        App.ApplyTheme(option.Theme);
    }

    // ウィンドウ色ドロップダウンの選択肢（テーマ・ラベル・色見本の色）。ドロップダウンの並び順はこの並びのとおり。
    // 色見本の色は各テーマの色を表す固定色（現在のテーマに依存しない）のため、テーマブラシ経由にしない
    private static readonly (AppTheme Theme, string Label, string SampleHex)[] WindowColorOptionTable =
    {
        (AppTheme.Light,      "ホワイト",       "#F5F5F5"),
        (AppTheme.Pink,       "ピンク",         "#DB2777"),
        (AppTheme.Gray,       "グレー",         "#52525B"),
        (AppTheme.Amber,      "アンバー",       "#C9761A"),
        (AppTheme.Green,      "グリーン",       "#1F8A4C"),
        (AppTheme.Teal,       "ティール",       "#0F8C87"),
        (AppTheme.Purple,     "パープル",       "#6D3FBF"),
        (AppTheme.Blue,       "ブルー",         "#2451D6"),
        (AppTheme.MatteBlack, "マットブラック", "#232323"),
        (AppTheme.Dark,       "ブラック",       "#0F172A"),
        (AppTheme.Wine,       "ワイン",         "#B85168"),
        (AppTheme.Plum,       "プラム",         "#9268B8"),
    };

    /// <summary>上記テーブルから、ウィンドウ色ドロップダウンの選択肢一覧を作る（色見本のブラシは Freeze 済み）。</summary>
    private static List<WindowColorOption> BuildWindowColorOptions()
    {
        var options = new List<WindowColorOption>();
        foreach (var (theme, label, sampleHex) in WindowColorOptionTable)
        {
            var sampleBrush = new SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(sampleHex)!);
            sampleBrush.Freeze();
            options.Add(new WindowColorOption(theme, label, sampleBrush));
        }
        return options;
    }

    // ===== 差し色（アクセント色）=====

    // 差し色の色見本（色相の流れ順：白→黄→オレンジ→赤→ピンク→紫→インディゴ→ブルー→水色→ティール→グリーン→黄緑→グレー）。
    // 追加する場合は色相の近い位置に "#RRGGBB" を足すだけ
    private static readonly string[] AccentColorPalette =
    {
        "#FFFFFF", "#F0A83C", "#F97316", "#EF4444", "#EC4899",
        "#A855F7", "#6366F1", "#3B82F6", "#0EA5E9", "#14B8A6",
        "#22C55E", "#84CC16", "#64748B",
    };

    private const double AccentSwatchSize            = 20;    // ドロップダウン内スウォッチ1辺(px)
    private const double AccentSwatchCornerRadius    = 3;     // スウォッチの角丸
    private const double AccentSwatchSpacing         = 4;     // スウォッチ余白(マージン)
    private const double AccentSwatchBorderThickness = 2;     // 選択枠の太さ
    private const double AccentCurrentSwatchWidth    = 32;    // 現在色スウォッチの幅(px)
    private const string AccentColorDefaultLogValue  = "既定";

    private void BuildAccentColorSwatches()
    {
        AccentColorCurrentSwatch.Width = AccentCurrentSwatchWidth;
        AccentColorGrid.Children.Clear();
        foreach (var paletteHex in AccentColorPalette)
        {
            var swatch = new Border
            {
                Width           = AccentSwatchSize,
                Height          = AccentSwatchSize,
                CornerRadius    = new CornerRadius(AccentSwatchCornerRadius),
                Margin          = new Thickness(AccentSwatchSpacing),
                Cursor          = Cursors.Hand,
                ToolTip         = paletteHex,
                Tag             = paletteHex,
                BorderThickness = new Thickness(0),
                Background      = new SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(paletteHex)!),
            };
            swatch.MouseLeftButtonUp += AccentColorSwatch_Selected;
            AccentColorGrid.Children.Add(swatch);
        }
        UpdateAccentColorSelection();
    }

    private void UpdateAccentColorSelection()
    {
        var currentHex = SettingsService.Instance.Settings.AccentColorOverride;
        foreach (var child in AccentColorGrid.Children)
        {
            if (child is not Border swatch) continue;
            var isSelected = !string.IsNullOrEmpty(currentHex) && (swatch.Tag as string) == currentHex;
            if (isSelected)
            {
                swatch.BorderThickness = new Thickness(AccentSwatchBorderThickness);
                MainWindow.SetDynamicBrush(swatch, Border.BorderBrushProperty, "TextPrimaryBrush");
            }
            else
            {
                swatch.BorderThickness = new Thickness(0);
                swatch.ClearValue(Border.BorderBrushProperty);
            }
        }

        var hasOverride = !string.IsNullOrEmpty(currentHex);
        if (hasOverride)
        {
            AccentColorCurrentSwatch.Background = new SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(currentHex)!);
        }
        else
        {
            MainWindow.SetDynamicBrush(AccentColorCurrentSwatch, Border.BackgroundProperty, "PrimaryBrush");
        }

        AccentColorResetRow.IsHitTestVisible = hasOverride;
        AccentColorResetRow.Opacity          = hasOverride ? 1.0 : AppConstants.DisabledControlOpacity;
    }

    private void AccentColorSplitButton_Click(object sender, MouseButtonEventArgs e)
    {
        if (_loadingSettings) return;
        AccentColorPopup.IsOpen = !AccentColorPopup.IsOpen;
    }

    private void AccentColorSwatch_Selected(object sender, MouseButtonEventArgs e)
    {
        if (_loadingSettings) return;
        var selectedHex = (sender as FrameworkElement)?.Tag as string;
        if (string.IsNullOrEmpty(selectedHex)) return;
        if (selectedHex == SettingsService.Instance.Settings.AccentColorOverride)
        {
            AccentColorPopup.IsOpen = false;
            return;
        }

        SettingsService.Instance.Settings.AccentColorOverride = selectedHex;
        SettingsService.Instance.MarkDirty();
        App.ApplyAccentOverride(selectedHex);
        AppLogger.Log(LogMsg.SettingAccentColor, null, selectedHex);
        AccentColorPopup.IsOpen = false;
        UpdateAccentColorSelection();
    }

    private void AccentColorResetRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (_loadingSettings) return;
        if (string.IsNullOrEmpty(SettingsService.Instance.Settings.AccentColorOverride))
        {
            AccentColorPopup.IsOpen = false;
            return;
        }

        SettingsService.Instance.Settings.AccentColorOverride = null;
        SettingsService.Instance.MarkDirty();
        App.ApplyAccentOverride(null);
        AppLogger.Log(LogMsg.SettingAccentColor, null, AccentColorDefaultLogValue);
        AccentColorPopup.IsOpen = false;
        UpdateAccentColorSelection();
    }
}
