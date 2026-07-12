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
    // 検索
    private string _searchQuery    = "";
    private bool   _searchExpanded = false;

    // 休眠リスト検索
    private string _dormantSearchQuery    = "";
    private bool   _dormantSearchExpanded = false;

    // ===== 検索UI共通処理（チャンネルリスト・休眠リストで共用） =====

    /// <summary>検索ボックス縮小時の幅</summary>
    private const double SearchBoxCollapsedWidth = 32;
    /// <summary>検索ボックス開閉アニメーションの時間（ミリ秒）</summary>
    private const int SearchAnimateDurationMs = 200;

    // 検索UIを構成するコントロール・状態・ログIDの組（チャンネルリスト用と休眠リスト用の2セット）
    private sealed class SearchUi
    {
        public required TextBox   Box;
        public required Border    Border;
        public required TextBlock Placeholder;
        public required Button    Button;
        public required Grid      AreaGrid;
        public required Button    CancelButton;
        public required Func<string>   GetQuery;
        public required Action<string> SetQuery;
        public required Func<bool>     GetExpanded;
        public required Action<bool>   SetExpanded;
        public required LogMsg ExecutedLog;
        public required LogMsg ClearedLog;
        public required Action Refresh;
    }

    private SearchUi? _mainSearchUi;
    private SearchUi? _dormantSearchUi;

    private SearchUi MainSearchUi => _mainSearchUi ??= new SearchUi
    {
        Box          = ChannelSearchBox,
        Border       = SearchBorder,
        Placeholder  = SearchPlaceholder,
        Button       = SearchButton,
        AreaGrid     = SearchAreaGrid,
        CancelButton = SearchCancelButton,
        GetQuery     = () => _searchQuery,
        SetQuery     = v => _searchQuery = v,
        GetExpanded  = () => _searchExpanded,
        SetExpanded  = v => _searchExpanded = v,
        ExecutedLog  = LogMsg.ChannelSearchExecuted,
        ClearedLog   = LogMsg.ChannelSearchCleared,
        Refresh      = RefreshChannelList,
    };

    private SearchUi DormantSearchUi => _dormantSearchUi ??= new SearchUi
    {
        Box          = DormantChannelSearchBox,
        Border       = DormantSearchBorder,
        Placeholder  = DormantSearchPlaceholder,
        Button       = DormantSearchButton,
        AreaGrid     = DormantSearchAreaGrid,
        CancelButton = DormantSearchCancelButton,
        GetQuery     = () => _dormantSearchQuery,
        SetQuery     = v => _dormantSearchQuery = v,
        GetExpanded  = () => _dormantSearchExpanded,
        SetExpanded  = v => _dormantSearchExpanded = v,
        ExecutedLog  = LogMsg.DormantSearchExecuted,
        ClearedLog   = LogMsg.DormantSearchCleared,
        Refresh      = RefreshDormantChannelList,
    };

    private static void SearchBoxGotFocusCore(SearchUi ui)
    {
        ui.Box.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextPrimaryBrush"];
        SetDynamicBrush(ui.Border, Border.BackgroundProperty, "SurfaceBrush");
        SetDynamicBrush(ui.Border, Border.BorderBrushProperty, "PrimaryBrush");
    }

    private void SearchBoxLostFocusCore(SearchUi ui)
    {
        ui.Box.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextPrimaryBrush"];
        SetDynamicBrush(ui.Border, Border.BackgroundProperty, "SurfaceBrush");
        SetDynamicBrush(ui.Border, Border.BorderBrushProperty, "BorderBrush");
        Dispatcher.BeginInvoke(() =>
        {
            if (string.IsNullOrEmpty(ui.Box.Text) &&
                string.IsNullOrEmpty(ui.GetQuery()) &&
                !ui.Button.IsKeyboardFocused)
                AnimateSearchCore(ui, false);
        }, DispatcherPriority.Input);
    }

    private static void SearchBoxTextChangedCore(SearchUi ui)
    {
        var hasText = !string.IsNullOrEmpty(ui.Box.Text);
        ui.Placeholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SearchBoxKeyDownCore(SearchUi ui, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)  ExecuteSearchCore(ui);
        if (e.Key == Key.Escape) ClearSearchCore(ui);
    }

    private void SearchButtonClickCore(SearchUi ui)
    {
        if (!ui.GetExpanded())
        {
            AnimateSearchCore(ui, true);
        }
        else if (!string.IsNullOrEmpty(ui.GetQuery()))
        {
            ClearSearchCore(ui);
        }
        else
        {
            ExecuteSearchCore(ui);
        }
    }

    private static void AnimateSearchCore(SearchUi ui, bool expand)
    {
        var targetWidth = expand ? ui.AreaGrid.ActualWidth : SearchBoxCollapsedWidth;
        var anim = new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(SearchAnimateDurationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        ui.Border.BeginAnimation(FrameworkElement.WidthProperty, anim);
        ui.Border.BorderThickness = expand ? new Thickness(1) : new Thickness(0);
        if (expand)
            SetDynamicBrush(ui.Border, Border.BackgroundProperty, "SurfaceBrush");
        else
            ui.Border.Background = System.Windows.Media.Brushes.Transparent;
        ui.SetExpanded(expand);
        ui.CancelButton.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        if (expand) ui.Box.Focus();
    }

    private void ExecuteSearchCore(SearchUi ui)
    {
        ui.SetQuery(ui.Box.Text.Trim());
        ui.Placeholder.Visibility = string.IsNullOrEmpty(ui.Box.Text) ? Visibility.Visible : Visibility.Collapsed;
        AppLogger.Log(ui.ExecutedLog, null, ui.GetQuery());
        if (ui == MainSearchUi && _channelStatusFilter.HasValue)
        {
            _channelStatusFilter = null;
            SetFilterButtonActive(false);
            AppLogger.Log(LogMsg.ChannelFilterCleared);
        }
        ui.Refresh();
    }

    private static void ClearSearchCore(SearchUi ui)
    {
        ui.Box.Text               = "";
        ui.SetQuery("");
        ui.Placeholder.Visibility = Visibility.Visible;
        ui.Box.Foreground         = (System.Windows.Media.Brush)Application.Current.Resources["TextPrimaryBrush"];
        SetDynamicBrush(ui.Border, Border.BackgroundProperty, "SurfaceBrush");
        AnimateSearchCore(ui, false);
        AppLogger.Log(ui.ClearedLog);
        ui.Refresh();
    }

    // ===== 検索UIイベントハンドラ（チャンネルリスト） =====
    private void ChannelSearchBox_GotFocus(object sender, RoutedEventArgs e)  => SearchBoxGotFocusCore(MainSearchUi);

    private void ChannelSearchBox_LostFocus(object sender, RoutedEventArgs e) => SearchBoxLostFocusCore(MainSearchUi);

    private void ChannelSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => SearchBoxTextChangedCore(MainSearchUi);

    private void ChannelSearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        => SearchBoxKeyDownCore(MainSearchUi, e);

    private void SearchButton_Click(object sender, RoutedEventArgs e)         => SearchButtonClickCore(MainSearchUi);

    private void SearchCancelButton_Click(object sender, RoutedEventArgs e)   => ClearSearchCore(MainSearchUi);
}
