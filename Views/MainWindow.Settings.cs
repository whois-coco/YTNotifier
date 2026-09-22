using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using YTNotifier.Constants;
using YTNotifier.Models;
using YTNotifier.Services;
using Application      = System.Windows.Application;
using Brush            = System.Windows.Media.Brush;
using Brushes          = System.Windows.Media.Brushes;
using Button           = System.Windows.Controls.Button;
using Cursors          = System.Windows.Input.Cursors;
using DataObject       = System.Windows.DataObject;
using DragDropEffects  = System.Windows.DragDropEffects;
using DragEventArgs    = System.Windows.DragEventArgs;
using Geometry         = System.Windows.Media.Geometry;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs     = System.Windows.Input.KeyEventArgs;
using Orientation      = System.Windows.Controls.Orientation;
using Path             = System.Windows.Shapes.Path;
using TextBox          = System.Windows.Controls.TextBox;

namespace YTNotifier.Views;

public partial class MainWindow : System.Windows.Window
{
    private const int    WindowMinWidth          = CompactTotalWidth; // コンパクト幅を下限とする

    // コンパクトモード
    private bool   _preCompactSidebarCollapsed = false;
    private bool   _applyingCompactMode        = false;

    // ===== コンパクトモード（設定タブのトグル）=====
    private void CompactModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || _applyingCompactMode) return;
        var enabled = CompactModeToggle.IsChecked == true;
        SettingsService.Instance.Settings.CompactMode = enabled;
        AppLogger.Log(LogMsg.SettingCompactMode, null, AppLogger.OnOffText(enabled));
        ApplyCompactMode(enabled);
    }

    private void ApplyCompactMode(bool enabled, bool skipRefresh = false, bool skipSave = false)
    {
        if (_applyingCompactMode) return;
        _applyingCompactMode = true;
        try
        {
            SettingsService.Instance.Settings.CompactMode = enabled;
            CompactModeToggle.IsChecked = enabled;

            if (enabled)
            {
                _preCompactSidebarCollapsed = _sidebarCollapsed;
                // 強制折り畳み
                if (!_sidebarCollapsed) CollapseSidebar(skipSave: true);
                // アイコン20pxに縮小
                SetSidebarIconSize(20);
                // 折り畳みボタンをグレーアウト・無効化
                SidebarToggleButton.IsEnabled = false;
                SidebarToggleButton.Opacity   = AppConstants.DisabledControlOpacity;

                Dispatcher.Invoke(() =>
                {
                    ContentColumn.Width = new GridLength(ContentWidthCompact); ContentColumn.MinWidth = ContentWidthCompact;
                    MaxWidth = MinWidth = Width = CompactTotalWidth;
                }, System.Windows.Threading.DispatcherPriority.Render);
            }
            else
            {
                // アイコンを通常サイズに戻す
                SetSidebarIconSize(SidebarIconSizeNormal);
                // 折り畳みボタンを有効化
                SidebarToggleButton.IsEnabled = true;
                SidebarToggleButton.Opacity   = 1.0;

                MaxWidth = double.PositiveInfinity; MinWidth = WindowMinWidth;
                ContentColumn.Width = new GridLength(ContentWidthNormal); ContentColumn.MinWidth = ContentWidthNormal;

                if (_sidebarCollapsed != _preCompactSidebarCollapsed)
                {
                    if (_preCompactSidebarCollapsed) CollapseSidebar(skipSave: true);
                    else                             ExpandSidebar(skipSave: true);
                }
                Dispatcher.Invoke(SyncWindowWidth, System.Windows.Threading.DispatcherPriority.Render);
            }

            UpdateCompactModeButton(enabled);
            if (!skipRefresh) { RefreshChannelList(); RefreshDormantChannelList(); }
        }
        finally
        {
            _applyingCompactMode = false;
            if (!skipSave) SettingsService.Instance.MarkDirty();
        }
    }

    private const int SidebarIconSizeNormal = 20;    // 通常モードのサイドバーアイコン1辺(px)

    private void SetSidebarIconSize(int size)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // NavWatch（x:Name="NavWatchIcon" で直接取得）
            if (NavWatch.Template?.FindName("NavWatchIcon", NavWatch) is Viewbox navWatchIconViewbox)
            { navWatchIconViewbox.Width = size; navWatchIconViewbox.Height = size; }

            // Border(Bd) > StackPanel > Viewbox の構造のボタン
            foreach (var btn in new Button[] { SidebarToggleButton, NavDebugTools, MuteButton, CompactModeButton, PinButton })
            {
                if (btn.Template?.FindName("Bd", btn) is Border buttonBorder &&
                    buttonBorder.Child is StackPanel buttonIconPanel &&
                    buttonIconPanel.Children.Count > 0 &&
                    buttonIconPanel.Children[0] is Viewbox buttonIconViewbox)
                { buttonIconViewbox.Width = size; buttonIconViewbox.Height = size; }
            }

            // NavSettings は Grid(Bd) > StackPanel > Viewbox の構造
            if (NavSettings.Template?.FindName("Bd", NavSettings) is System.Windows.Controls.Grid grid)
            {
                foreach (var child in grid.Children)
                    if (child is StackPanel settingsIconPanel && settingsIconPanel.Children.Count > 0 && settingsIconPanel.Children[0] is Viewbox settingsIconViewbox)
                    { settingsIconViewbox.Width = size; settingsIconViewbox.Height = size; break; }
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // サイドバー折畳の共通処理
    private void SetStatusTextVisibility(Visibility visibility)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (StatusBadge.Template?.FindName("StatusText", StatusBadge) is System.Windows.Controls.TextBlock txt)
                txt.Visibility = visibility;
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void SetSidebarLabels(Visibility visibility)
    {
        // テンプレート適用後に実行（起動直後はまだ適用されていない場合があるため遅延）
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var name in new[] { "LblNavWatch", "LblNavDormant", "LblNavReport", "LblNavDebugTools", "LblNavSettings", "LblMuteButton",
                                         "LblCompactModeButton", "LblPinButton", "LblSidebarToggleButton" })
            {
                var btnName = name[3..];
                var btn = FindSidebarButton(btnName);
                if (btn?.Template == null) continue;
                var lbl = btn.Template.FindName(name, btn) as System.Windows.Controls.TextBlock;
                if (lbl != null) lbl.Visibility = visibility;
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private Button? FindSidebarButton(string name) => name switch
    {
        "NavWatch"            => NavWatch,
        "NavDormant"          => NavDormant,
        "NavReport"           => NavReport,
        "NavDebugTools"       => NavDebugTools,
        "NavSettings"         => NavSettings,
        "MuteButton"          => MuteButton,
        "CompactModeButton"   => CompactModeButton,
        "PinButton"           => PinButton,
        "SidebarToggleButton" => SidebarToggleButton,
        _                     => null
    };

    private void CollapseSidebar(bool skipSave = false)
    {
        _sidebarCollapsed       = true;
        SidebarColumn.Width     = new GridLength(SidebarCollapsedWidth);
        StatusBadge.Visibility  = Visibility.Visible;
        StatusBadgeColumn.Width = new GridLength(1, GridUnitType.Star);
        SetSidebarLabels(Visibility.Collapsed);
        SetStatusTextVisibility(Visibility.Collapsed);
        UpdateToggleIcon("▶");
        UpdateToggleIconColor(MonitorService.Instance.IsRunning);
        UpdateNavWatchBadge();
        SettingsService.Instance.Settings.SidebarCollapsed = true;
        UpdateMinWidth();
        SyncWindowWidth();
        if (!skipSave) SettingsService.Instance.MarkDirty();
    }

    private void ExpandSidebar(bool skipSave = false)
    {
        _sidebarCollapsed       = false;
        SidebarColumn.Width     = new GridLength(SidebarExpandedWidth);
        StatusBadge.Visibility  = Visibility.Visible;
        StatusBadgeColumn.Width = new GridLength(1, GridUnitType.Star);
        SetSidebarLabels(Visibility.Visible);
        SetStatusTextVisibility(Visibility.Visible);
        UpdateToggleIcon("◀");
        UpdateToggleIconColor(false);
        UpdateMonitorStatus(MonitorService.Instance.IsRunning);
        UpdateNavWatchBadge();
        SettingsService.Instance.Settings.SidebarCollapsed = false;
        UpdateMinWidth();
        SyncWindowWidth();
        if (!skipSave) SettingsService.Instance.MarkDirty();
    }

    // ===== 常に前面（設定タブのトグル）=====
    private void AlwaysOnTopToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        var enabled = AlwaysOnTopToggle.IsChecked == true;
        Topmost = enabled;
        SettingsService.Instance.Settings.AlwaysOnTop = enabled;
        AppLogger.Log(LogMsg.SettingAlwaysOnTop, null, AppLogger.OnOffText(enabled));
        UpdatePinButton(enabled);
        SettingsService.Instance.MarkDirty();
    }

    private const string DebugDllName           = "YTNotifier.Debug.dll";
    private static readonly string DebugDllPath = System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(Environment.ProcessPath
            ?? System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
        DebugDllName);

    internal static bool IsDebugDllAvailable() => System.IO.File.Exists(DebugDllPath);

    private void OpenDebugWindow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var asm  = System.Reflection.Assembly.LoadFrom(DebugDllPath);
            var type = asm.GetType("YTNotifier.Debug.DebugWindow");
            if (type == null) { AppLogger.Log(LogMsg.DebugWindowNotFound); return; }
            var win  = (Window?)Activator.CreateInstance(type, this);
            win?.Show();
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.DevToolError, null, ex.Message);
        }
    }
}
