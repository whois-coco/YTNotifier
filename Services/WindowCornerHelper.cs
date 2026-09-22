using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace YTNotifier.Services;

internal static class WindowCornerHelper
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND                   = 2;

    /// <summary>
    /// 角丸はウィンドウのハンドルが生成された後でないと適用できないため、Loaded のときに適用するよう登録する。
    /// </summary>
    internal static void ApplyOnLoaded(Window window)
    {
        window.Loaded += (_, _) => Apply(window);
    }

    internal static void Apply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { }
    }
}
