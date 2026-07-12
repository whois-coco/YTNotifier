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
    // D&D
    private ChannelInfo?  _dragSource      = null;
    private bool          _isDragging      = false;

    // D&Dアニメーション
    private int     _animDropIndex  = -1;
    private Border? _dragSourceRow  = null;

    // カテゴリなし表示モードでドラッグ対象を全チャンネルに広げるためのセンチネル値
    private const string AllChannelsCatSentinel = "\x00all";

    // ドラッグ時のカテゴリIDを返す（カテゴリなし表示モードでは全チャンネル対象のセンチネルを返す）
    private static string? GetDragCatId(ChannelInfo ch)
        => IsNoCategoryMode ? AllChannelsCatSentinel : ch.CategoryId;

    // catId がセンチネルの場合は全チャンネル行にマッチする
    private static bool MatchesCatId(ChannelInfo ch, string? catId)
        => catId == AllChannelsCatSentinel || ch.CategoryId == catId;

    private static Border BuildDragHandle(bool compact, bool editMode)
    {
        var dots = new TextBlock
        {
            Text = "⠿", FontSize = compact ? 13 : 15,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
        };
        SetDynamicBrush(dots, TextBlock.ForegroundProperty, "TextMutedBrush");
        return new Border
        {
            VerticalAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Center,
            Width = compact ? 18 : 20, Cursor = editMode ? Cursors.SizeAll : Cursors.Arrow, Tag = "DragHandle",
            Background = Brushes.Transparent, Opacity = editMode ? 1.0 : 0.0,
            ToolTip = "ドラッグして並び替え", Child = dots
        };
    }

    private void AttachHandleDragEvents(Border handle, Border row, ChannelInfo ch, StackPanel panel)
    {
        System.Windows.Point dragStartPos = default;
        bool dragReady = false;

        handle.MouseLeftButtonDown += (_, e) =>
        {
            if (_isDragging || !(panel == ChannelList ? _editMode : _dormantEditMode)) return;
            dragStartPos = e.GetPosition(panel);
            dragReady    = true;
            _dragSource  = ch;
            handle.CaptureMouse();
        };

        handle.MouseMove += (_, e) =>
        {
            if (!dragReady || e.LeftButton != MouseButtonState.Pressed) return;
            var pos  = e.GetPosition(panel);
            var diff = pos - dragStartPos;

            if (!_isDragging)
            {
                if (Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _isDragging    = true;
                _dragSourceRow = row;
                _animDropIndex = -1;
                Mouse.OverrideCursor = Cursors.SizeNS;
                InitRowTransforms(panel);
            }

            if (_dragSourceRow?.RenderTransform is TranslateTransform srcTt)
                srcTt.Y = pos.Y - dragStartPos.Y;

            var catId  = GetDragCatId(_dragSource!);
            var relIdx = CalcSwapIndex(pos.Y, catId, panel);
            UpdateSwapAnimation(relIdx, panel);
        };

        handle.MouseLeftButtonUp += (_, e) =>
        {
            if (!dragReady && !_isDragging) return;
            var wasDragging = _isDragging;
            dragReady   = false;
            _isDragging = false;
            handle.ReleaseMouseCapture();
            Mouse.OverrideCursor = null;

            if (wasDragging && _dragSourceRow != null)
            {
                var catId      = GetDragCatId(ch);
                var currentPos = e.GetPosition(panel);
                var relIdx     = CalcSwapIndex(currentPos.Y, catId, panel);
                CommitSwap(ch, catId, relIdx, panel);
            }
            else
            {
                ResetRowTransforms(false, panel);
                _dragSourceRow = null;
                _dragSource    = null;
                _animDropIndex = -1;
            }
        };

        handle.LostMouseCapture += (_, _) =>
        {
            if (!_isDragging) return;
            ResetRowTransforms(false, panel);
            _dragSourceRow = null; _dragSource = null;
            _animDropIndex = -1; _isDragging = false;
            Mouse.OverrideCursor = null;
        };
    }
    // ===== 入れ替えアニメーション =====

    // ドラッグ開始時に対象チャンネル全行へ TranslateTransform を付与
    private void InitRowTransforms(StackPanel panel)
    {
        var catId = GetDragCatId(_dragSource!);
        foreach (var b in panel.Children.OfType<Border>()
            .Where(b => b.Tag is ChannelInfo ch && MatchesCatId(ch, catId)))
        {
            if (b.RenderTransform is TranslateTransform tt) tt.Y = 0;
            else b.RenderTransform = new TranslateTransform(0, 0);
        }
    }

    // マウスY座標から「ドラッグ元が落ち着くべきインデックス」を返す
    private int CalcSwapIndex(double mouseY, string? catId, StackPanel panel)
    {
        var rows = panel.Children.OfType<Border>()
            .Where(b => b.Tag is ChannelInfo ch && MatchesCatId(ch, catId))
            .ToList();
        if (rows.Count == 0) return -1;

        // 各行の自然な中心Y（TranslateTransform を除いた位置）
        double cumY = 0;
        var centers = new List<double>();
        foreach (var child in panel.Children.OfType<Border>())
        {
            double rowH = child.ActualHeight + child.Margin.Top + child.Margin.Bottom;
            if (rows.Contains(child))
                centers.Add(cumY + rowH / 2);
            cumY += rowH;
        }

        // マウス位置に最も近い中心を持つ行のインデックス
        int best = 0; double bestDist = double.MaxValue;
        for (int i = 0; i < centers.Count; i++)
        {
            double d = Math.Abs(mouseY - centers[i]);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    // ドラッグ元以外の行を「ドラッグ元が best の位置にいる」ように見せるアニメーション
    private void UpdateSwapAnimation(int best, StackPanel panel)
    {
        if (best < 0 || best == _animDropIndex) return;
        _animDropIndex = best;

        if (_dragSourceRow == null) return;
        var catId = GetDragCatId((_dragSourceRow.Tag as ChannelInfo)!);
        var rows  = panel.Children.OfType<Border>()
            .Where(b => b.Tag is ChannelInfo ch && MatchesCatId(ch, catId))
            .ToList();

        var srcIdx = rows.IndexOf(_dragSourceRow);
        if (srcIdx < 0) return;

        var duration = new Duration(TimeSpan.FromMilliseconds(150));
        double rowH  = _dragSourceRow.ActualHeight + _dragSourceRow.Margin.Bottom;

        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i] == _dragSourceRow) continue;
            if (rows[i].RenderTransform is not TranslateTransform tt)
            {
                tt = new TranslateTransform(0, 0);
                rows[i].RenderTransform = tt;
            }

            // srcIdx → best に動いたとき、間にある行を反対方向へずらす
            double target = 0;
            if (srcIdx < best && i > srcIdx && i <= best) target = -rowH;
            else if (srcIdx > best && i >= best && i < srcIdx) target = rowH;

            if (Math.Abs(tt.Y - target) < 1) continue;
            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(tt.Y, target, duration)
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    // ドロップ確定または キャンセル時に全行リセット
    private void ResetRowTransforms(bool commit, StackPanel panel)
    {
        var src   = _dragSourceRow?.Tag as ChannelInfo;
        var catId = src != null ? GetDragCatId(src) : null;
        foreach (var b in panel.Children.OfType<Border>()
            .Where(b => b.Tag is ChannelInfo ch && MatchesCatId(ch, catId)))
        {
            if (b.RenderTransform is not TranslateTransform tt) continue;
            // 確定時は即時リセット（アニメーション中の状態でRefreshが走るのを防ぐ）
            tt.BeginAnimation(TranslateTransform.YProperty, null);
            tt.Y = 0;
        }
    }

    // データを実際に並び替えてリストを再描画
    private void CommitSwap(ChannelInfo srcCh, string? catId, int destRelIdx, StackPanel panel)
    {
        var channels  = SettingsService.Instance.Channels;
        var isDormant = panel == DormantChannelList;
        var sameCat   = channels
            .Select((c, i) => (c, i))
            .Where(t => MatchesCatId(t.c, catId) && t.c.IsDormant == isDormant)
            .ToList();

        var srcCatIdx = sameCat.FindIndex(t => t.c.ChannelId == srcCh.ChannelId);

        if (srcCatIdx < 0 || destRelIdx < 0 || destRelIdx == srcCatIdx)
        {
            ResetRowTransforms(false, panel);
            _dragSourceRow = null; _dragSource = null;
            _animDropIndex = -1; _isDragging = false;
            return;
        }

        var (moving, movingGlobal) = sameCat[srcCatIdx];
        channels.RemoveAt(movingGlobal);

        // RemoveAt後にインデックスを再取得
        var updated = channels.Select((c, i) => (c, i)).Where(t => MatchesCatId(t.c, catId) && t.c.IsDormant == isDormant).ToList();

        // destRelIdx は「移動先の行インデックス」（CalcSwapIndex が返す最近傍行）
        // srcより下に移動する場合: destの後ろに挿入（Remove後インデックスは1つずれる）
        // srcより上に移動する場合: destの前に挿入
        int insertIdx;
        if (destRelIdx > srcCatIdx)
        {
            // 下移動: destRelIdx行の後ろ → Remove後は destRelIdx-1 の後ろ
            var afterIdx = destRelIdx - 1;
            insertIdx = afterIdx < updated.Count ? updated[afterIdx].i + 1 : channels.Count;
        }
        else
        {
            // 上移動: destRelIdx行の前
            insertIdx = destRelIdx < updated.Count ? updated[destRelIdx].i : channels.Count;
        }
        channels.Insert(Math.Clamp(insertIdx, 0, channels.Count), moving);

        // Transform を即時リセットしてから再描画
        ResetRowTransforms(true, panel);
        _dragSourceRow = null; _dragSource = null;
        _animDropIndex = -1; _isDragging = false;

        // 確定後にデバッグログを削除してリフレッシュ
        SettingsService.Instance.MarkDirty();
        AppLogger.Log(LogMsg.ChannelReordered, null, srcCh.ChannelName);
        Action refresh = _currentNav == "Dormant" ? RefreshDormantChannelList : RefreshChannelList;
        Dispatcher.Invoke(refresh, System.Windows.Threading.DispatcherPriority.Render);
    }

    // ===== カテゴリのドラッグアンドドロップ共通処理（チャンネルリスト・休眠リストで共用） =====

    /// <summary>カテゴリD&Dのドラッグデータ形式（チャンネルリスト）</summary>
    private const string CategoryDragFormat = "CategoryDrag";
    /// <summary>カテゴリD&Dのドラッグデータ形式（休眠リスト）</summary>
    private const string DormantCategoryDragFormat = "DormantCategoryDrag";

    // カテゴリD&Dを構成するパネル・状態・対象リストの組（チャンネルリスト用と休眠リスト用の2セット）
    private sealed class CategoryDndUi
    {
        public required StackPanel Panel;
        public required string DragFormat;
        public required Func<List<CategoryInfo>> GetCategories;
        public required Action Refresh;
        public List<(double y, int childIndex)> Boundaries = new();
        public Border? DropIndicator;
        public int DropIndicatorIndex = -1;
    }

    private CategoryDndUi? _mainCategoryDnd;
    private CategoryDndUi? _dormantCategoryDnd;

    private CategoryDndUi MainCategoryDnd => _mainCategoryDnd ??= new CategoryDndUi
    {
        Panel         = ChannelList,
        DragFormat    = CategoryDragFormat,
        GetCategories = () => SettingsService.Instance.Categories,
        Refresh       = RefreshChannelList,
    };

    private CategoryDndUi DormantCategoryDnd => _dormantCategoryDnd ??= new CategoryDndUi
    {
        Panel         = DormantChannelList,
        DragFormat    = DormantCategoryDragFormat,
        GetCategories = () => SettingsService.Instance.DormantCategories,
        Refresh       = RefreshDormantChannelList,
    };

    // ドラッグ開始時にカテゴリ行の境界位置（Y座標と挿入先インデックス）を記録する
    private static void CacheCategoryBoundariesCore(CategoryDndUi ui)
    {
        ui.Boundaries.Clear();
        double cumY = 0;
        foreach (var child in ui.Panel.Children.OfType<Border>())
        {
            double rowH = child.ActualHeight + child.Margin.Top + child.Margin.Bottom;
            if (child.Tag is CategoryInfo)
                ui.Boundaries.Add((cumY, ui.Panel.Children.IndexOf(child)));
            cumY += rowH;
        }
        if (ui.Boundaries.Count > 0)
        {
            var last    = ui.Boundaries[^1];
            var lastRow = ui.Panel.Children[last.childIndex] as Border;
            if (lastRow != null)
            {
                double lastH = lastRow.ActualHeight + lastRow.Margin.Top + lastRow.Margin.Bottom;
                ui.Boundaries.Add((last.y + lastH, last.childIndex + 1));
            }
        }
    }

    private static void ShowDropIndicatorCore(CategoryDndUi ui, int insertIndex)
    {
        if (ui.DropIndicatorIndex == insertIndex) return;
        HideDropIndicatorCore(ui);
        ui.DropIndicatorIndex = insertIndex;
        ui.DropIndicator = new Border
        {
            Height = 2, HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible = false, Margin = new Thickness(8, 0, 8, 0)
        };
        SetDynamicBrush(ui.DropIndicator, Border.BackgroundProperty, "PrimaryBrush");
        ui.Panel.Children.Insert(Math.Min(insertIndex, ui.Panel.Children.Count), ui.DropIndicator);
    }

    private static void HideDropIndicatorCore(CategoryDndUi ui)
    {
        if (ui.DropIndicator != null && ui.Panel.Children.Contains(ui.DropIndicator))
            ui.Panel.Children.Remove(ui.DropIndicator);
        ui.DropIndicator = null; ui.DropIndicatorIndex = -1;
    }

    private static int CalcCategoryDropIndexCore(CategoryDndUi ui, double posY)
    {
        double cumY = 0; int count = 0;
        foreach (var child in ui.Panel.Children.OfType<FrameworkElement>())
        {
            if (child == ui.DropIndicator) continue;
            double rowH   = child.ActualHeight + child.Margin.Bottom;
            double rowTop = cumY; cumY += rowH;
            if (posY <= cumY) return posY > rowTop + rowH / 2 ? count + 1 : count;
            count++;
        }
        return count;
    }

    private static void CategoryListDragOverCore(CategoryDndUi ui, DragEventArgs e)
    {
        // カテゴリD&Dのみ処理（チャンネルD&DはマウスキャプチャーD&Dに移行）
        if (!e.Data.GetDataPresent(ui.DragFormat)) { e.Effects = DragDropEffects.None; return; }

        e.Effects = DragDropEffects.Move;
        var posY = e.GetPosition(ui.Panel).Y;
        if (ui.Boundaries.Count == 0) { e.Handled = true; return; }

        int bestIdx = ui.Boundaries[^1].childIndex; double bestDist = double.MaxValue;
        foreach (var (y, idx) in ui.Boundaries)
        {
            double dist = Math.Abs(posY - y);
            if (dist < bestDist) { bestDist = dist; bestIdx = idx; }
        }
        ShowDropIndicatorCore(ui, bestIdx);
        e.Handled = true;
    }

    private static void CategoryListDropCore(CategoryDndUi ui, DragEventArgs e)
    {
        HideDropIndicatorCore(ui);
        if (!e.Data.GetDataPresent(ui.DragFormat)) return;

        var catSrcId = e.Data.GetData(ui.DragFormat) as string;
        if (string.IsNullOrEmpty(catSrcId)) return;

        var categories = ui.GetCategories();
        var catSrcIdx  = categories.FindIndex(c => c.CategoryId == catSrcId);
        if (catSrcIdx < 0) return;

        int dropIdx   = CalcCategoryDropIndexCore(ui, e.GetPosition(ui.Panel).Y);
        var rows      = ui.Panel.Children.OfType<Border>().Where(b => b != ui.DropIndicator).ToList();
        int catDstIdx = categories.Count; int scanned = 0;

        foreach (var row in rows)
        {
            if (scanned >= dropIdx && row.Tag is CategoryInfo dropCat)
            { catDstIdx = categories.FindIndex(c => c.CategoryId == dropCat.CategoryId); break; }
            scanned++;
        }

        if (catDstIdx == catSrcIdx || catDstIdx == catSrcIdx + 1) return;
        var movingCat = categories[catSrcIdx];
        categories.RemoveAt(catSrcIdx);
        if (catDstIdx > catSrcIdx) catDstIdx--;
        catDstIdx = Math.Max(0, Math.Min(catDstIdx, categories.Count));
        categories.Insert(catDstIdx, movingCat);
        for (int i = 0; i < categories.Count; i++) categories[i].SortOrder = i;

        SettingsService.Instance.MarkDirty();
        AppLogger.Log(LogMsg.CategoryReordered, null, movingCat.CategoryName);
        ui.Refresh();
        e.Handled = true;
    }

    // ===== カテゴリD&Dイベントハンドラ（チャンネルリスト） =====
    private void ChannelList_DragOver(object sender, DragEventArgs e) => CategoryListDragOverCore(MainCategoryDnd, e);

    private void ChannelList_Drop(object sender, DragEventArgs e)     => CategoryListDropCore(MainCategoryDnd, e);
}
