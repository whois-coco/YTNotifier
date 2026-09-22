using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace YTNotifier.Services;

internal static class WindowTitleBarHelper
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION        = 2;

    /// <summary>
    /// タイトルバーの左ボタン押下でウィンドウを移動する（WPF 標準の移動）。
    /// </summary>
    internal static void DragMoveOnPress(Window window, MouseButtonEventArgs mouseArgs)
    {
        if (mouseArgs.ButtonState == MouseButtonState.Pressed)
        {
            window.DragMove();
        }
    }

    /// <summary>
    /// タイトルバーの左ボタン押下を、OS 標準のタイトルバー操作として扱う（画面端へのスナップ等が働く）。
    /// DragMoveOnPress とは方式が異なるため統一しない。
    /// </summary>
    internal static void CaptionDragOnPress(Window window, MouseButtonEventArgs mouseArgs)
    {
        if (mouseArgs.ButtonState == MouseButtonState.Pressed)
        {
            SendMessage(new WindowInteropHelper(window).Handle, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
        }
    }
}
