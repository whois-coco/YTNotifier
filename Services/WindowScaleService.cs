using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ComboBox = System.Windows.Controls.ComboBox;

namespace YTNotifier.Services;

/// <summary>
/// OSのディスプレイスケーリングとは独立した、アプリ内蔵のウィンドウ拡大率を各ウィンドウへ適用する共通サービス。
/// 拡大率が変わるたびに各所へ配り直さずに済むよう、<see cref="ScaleTransform"/> はウィンドウ1つにつき1個だけ生成し、
/// 以後は同じインスタンスの <see cref="ScaleTransform.ScaleX"/>／<see cref="ScaleTransform.ScaleY"/> だけを書き換えて使い回す。
/// </summary>
internal static class WindowScaleService
{
    /// <summary>現在の拡大率（例: 150% なら 1.5）を返す。</summary>
    internal static double GetScaleFactor()
        => SettingsService.Instance.Settings.WindowScalePercent / 100.0;

    /// <summary>
    /// 新規に開くウィンドウへ現在の拡大率を適用する。<c>InitializeComponent()</c> 完了直後（XAML基準値が入った状態）で呼び出すこと。
    /// Width／Height／MinWidth／MinHeight／MaxWidth／MaxHeight のうち未設定でない値（NaN・0・PositiveInfinity以外）にのみ
    /// 拡大率を掛けて上書きし、<see cref="ScaleTransform"/> を1個生成して <see cref="FrameworkElement.LayoutTransform"/> に設定する。
    /// あわせて <see cref="ApplyPopupScaling"/> を呼び、Popup への追従も行う。
    /// 拡大率が100%でも ScaleTransform 自体は生成・設定する（後から拡大率が変わった時に同じインスタンスを書き換えるだけで反映させるため）。
    /// </summary>
    /// <returns>このウィンドウに設定した <see cref="ScaleTransform"/>。ContextMenu にも使う場合のみ呼び出し元が受け取ればよい。</returns>
    internal static ScaleTransform ApplyToNewWindow(Window window)
    {
        var scaleFactor = GetScaleFactor();

        window.Width     = ScaleIfSet(window.Width,     scaleFactor);
        window.Height    = ScaleIfSet(window.Height,    scaleFactor);
        window.MinWidth  = ScaleIfSet(window.MinWidth,  scaleFactor);
        window.MinHeight = ScaleIfSet(window.MinHeight, scaleFactor);
        window.MaxWidth  = ScaleIfSet(window.MaxWidth,  scaleFactor);
        window.MaxHeight = ScaleIfSet(window.MaxHeight, scaleFactor);

        var transform = new ScaleTransform(scaleFactor, scaleFactor);
        if (window.Content is FrameworkElement content)
            content.LayoutTransform = transform;

        ApplyPopupScaling(window, transform);

        return transform;
    }

    private static double ScaleIfSet(double value, double scaleFactor)
    {
        if (double.IsNaN(value) || value == 0 || double.IsPositiveInfinity(value))
            return value;

        return value * scaleFactor;
    }

    /// <summary>
    /// Popup は Window の LayoutTransform の対象外（WPFの仕様上、別の描画レイヤーで開かれるため）なので個別に対応する。
    /// ウィンドウ内の ComboBox をすべて再帰的に探し、DropDownOpened のタイミングで PART_Popup の Child へ
    /// 同じ transform を設定する。XAML上に静的に存在する明示的な Popup 要素は、Loaded を待たずその場で設定する。
    /// </summary>
    internal static void ApplyPopupScaling(Window window, ScaleTransform transform)
    {
        foreach (var popup in FindLogicalDescendants<Popup>(window))
        {
            if (popup.Child is FrameworkElement popupChild)
                popupChild.LayoutTransform = transform;
        }

        window.Loaded += (_, _) =>
        {
            foreach (var comboBox in FindLogicalDescendants<ComboBox>(window))
            {
                comboBox.DropDownOpened += (_, _) =>
                {
                    if (comboBox.Template?.FindName("PART_Popup", comboBox) is Popup partPopup
                        && partPopup.Child is FrameworkElement partPopupChild)
                    {
                        partPopupChild.LayoutTransform = transform;
                    }
                };
            }
        };
    }

    /// <summary>
    /// ContextMenu は Window の子ではなく生成のたびに独立して開かれるため、生成箇所ごとに個別に呼び出す薄いヘルパー。
    /// </summary>
    internal static void ApplyScaleToContextMenu(ContextMenu menu, ScaleTransform transform)
        => menu.LayoutTransform = transform;

    private static IEnumerable<TElement> FindLogicalDescendants<TElement>(DependencyObject parent) where TElement : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is TElement typedChild)
                yield return typedChild;

            foreach (var descendant in FindLogicalDescendants<TElement>(child))
                yield return descendant;
        }
    }
}
