using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTNotifier.Constants;
using YTNotifier.Plugin;
using Button = System.Windows.Controls.Button;

namespace YTNotifier.Views;

/// <summary>
/// 動画詳細ポップアップの「プラグインアクション領域」（<c>video.detail.actions</c> リージョン）の汎用描画。
/// プラグイン固有の分岐は持たず、<c>plugin.json</c> の <c>contributes</c> 宣言だけで動く。
/// 将来 <c>channel.context.menu</c> 等を足すときに切り出せるよう、メソッドはリージョン非依存に書く。
/// </summary>
public partial class VideoSummaryPopupWindow
{
    // ── リージョン ─────────────────────────────────────────────────────────
    private const string ActionRegionId = PluginProtocol.Regions.VideoDetailActions;

    // ── 表示数・上限（このファイルでのみ参照するためローカル定数） ──────────
    private const int    InlineActionSlots             = 5;
    private const double ActionButtonMaxWidth          = 120;
    private const int    TitleWidthBudget              = 12;
    private const int    WideCharWidth                 = 2;
    private const int    NarrowCharWidth               = 1;
    private const int    SegmentsMaxItems              = 50;
    private const int    SegmentTitleMaxChars          = 80;
    private const int    SegmentBodyMaxChars           = 300;
    private const int    TextHeadlineMaxChars          = 200;
    private const int    TextDetailMaxChars            = 4000;
    private const int    ResultJsonMaxBytes            = 64 * 1024;
    private const int    MaxCommandsPerPluginPerRegion = 2;
    private const int    SecondsPerMinute              = 60;
    private const int    SecondsPerHour                = 3600;

    // ── 文字列（このファイルでのみ参照） ──────────────────────────────────
    private const string UiLangValue             = "ja";
    private const string PublishedAtIsoFormat    = "o";
    private const string TitleEllipsis           = "…";
    private const string OverflowButtonLabel     = "⋯";
    private const string OverflowButtonTooltip   = "その他";
    private const string SecondaryButtonStyleKey = "SecondaryButton";
    private const string TextPrimaryBrushKey     = "TextPrimaryBrush";
    private const string TextSecondaryBrushKey   = "TextSecondaryBrush";
    private const string SurfaceAltBrushKey      = "SurfaceAltBrush";
    private const string HttpsScheme             = "https://";
    private const string HttpScheme              = "http://";

    private const string StatusRunningText = "実行しています…";
    private const string StatusFailedText  = "実行できませんでした";
    private const string ResultNoneText    = "完了しました";

    private const string RejectReasonTooManyCommands = "1画面あたりの上限超過";
    private const string RejectReasonUnknownWhenKey  = "表示条件の項目が不明";

    // ── レイアウト（このファイルでのみ参照） ──────────────────────────────
    private const double ResultHeadlineFontSize = 13;
    private const double ResultDetailFontSize   = 12;
    private const double SegmentHeaderFontSize  = 12;
    private const double SegmentBodyFontSize    = 12;
    private const double SegmentRowCornerRadius = 4;

    private static readonly Thickness ActionButtonPadding = new(12, 6, 12, 6);
    private static readonly Thickness ActionButtonMargin  = new(0, 0, 8, 8);
    private static readonly Thickness ResultDetailMargin  = new(0, 6, 0, 0);
    private static readonly Thickness SegmentRowPadding   = new(8, 6, 8, 6);
    private static readonly Thickness SegmentRowMargin    = new(0, 0, 0, 6);
    private static readonly Thickness SegmentBodyMargin   = new(0, 2, 0, 0);

    // ── 右サイドパネル（このファイルでのみ参照するためローカル定数） ────────
    private const double LeftColumnMaxWidth      = 480;    // 左カラムの MaxWidth（現行の見た目幅）
    private const double SidePanelWidth          = 420;    // 右サイドパネルの固定幅
    private const double SidePanelHeightRatio    = 0.7;    // 右パネル ScrollViewer の MaxHeight ／ Owner 高さ
    private const double SidePanelHeightFallback = 520;    // Owner 高さが取れないときの MaxHeight 上限
    private const double SidePanelChromeAllowance = 100;   // 左右マージン・外枠・影ぶんの余白
    // 右パネル展開時のウィンドウ幅上限。SizeToContent により畳まれている間は内容幅に収まり、
    // この値は展開時の上限としてのみ効く。
    private const double WindowMaxWidthExpanded  =
        LeftColumnMaxWidth + SidePanelWidth + SidePanelChromeAllowance;

    /// <summary>
    /// 対象コマンドを集め、あれば「プラグインアクション領域」を組み立てて表示する。
    /// 要約セクションとは独立（APIキー不要・<c>When</c> のみで制御）。対象0なら領域ごと非表示のまま。
    /// </summary>
    private void SetupPluginActions()
    {
        try
        {
            var commands = CollectActionCommands();
            if (commands.Count == 0) return;

            RenderActionButtons(commands);
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.UiUpdateFailed, null, nameof(SetupPluginActions), ex.Message);
        }
    }

    // ── (a) 対象コマンドの収集と絞り込み ──────────────────────────────────

    private List<ActionCommand> CollectActionCommands()
    {
        var context = BuildContextValues();
        var result  = new List<ActionCommand>();

        foreach (var descriptor in OrderPluginsByPriority())
        {
            if (descriptor.Contributes?.Commands is not { Count: > 0 } declared) continue;
            if (!PluginBridge.Instance.IsPluginEnabled(descriptor.Folder)) continue;

            var acceptedForPlugin = 0;
            foreach (var command in declared)
            {
                if (!command.Regions.Contains(ActionRegionId, StringComparer.Ordinal)) continue;
                if (!descriptor.Jobs.Contains(command.Job, StringComparer.Ordinal)) continue;
                if (!EvaluateWhen(command.When, context, descriptor.Folder)) continue;

                if (acceptedForPlugin >= MaxCommandsPerPluginPerRegion)
                {
                    AppLogger.Log(LogMsg.PluginContributionRejected, null,
                        descriptor.Folder, RejectReasonTooManyCommands);
                    continue;
                }

                result.Add(new ActionCommand(command,
                    ResolveResultKind(command.Result),
                    ResolvePlacement(command.Placement)));
                acceptedForPlugin++;
            }
        }

        return result;
    }

    /// <summary>
    /// プラグインを設定「プラグイン」ページの表示・優先順（<see cref="PluginBridge.GetPluginOrder"/>）で並べる。
    /// 記録に無いプラグインは末尾（発見順を保つ安定ソート）。
    /// </summary>
    private static List<PluginDescriptor> OrderPluginsByPriority()
    {
        var order        = PluginBridge.Instance.GetPluginOrder();
        var rankByFolder = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < order.Count; i++)
            if (!rankByFolder.ContainsKey(order[i])) rankByFolder[order[i]] = i;

        return PluginBridge.Instance.Plugins
            .OrderBy(p => rankByFolder.TryGetValue(p.Folder, out var rank) ? rank : int.MaxValue)
            .ToList();
    }

    /// <summary><c>segments</c> / <c>text</c> はその種別、それ以外（<c>none</c>・未知値）は <c>none</c> 扱い。</summary>
    private static string ResolveResultKind(string declared) => declared switch
    {
        PluginProtocol.ResultKinds.Segments => PluginProtocol.ResultKinds.Segments,
        PluginProtocol.ResultKinds.Text     => PluginProtocol.ResultKinds.Text,
        _                                   => PluginProtocol.ResultKinds.None,
    };

    /// <summary><c>side</c> はそのまま、それ以外（<c>inline</c>・未指定・未知値）は <c>inline</c> 扱い（前方互換）。</summary>
    private static string ResolvePlacement(string declared) => declared switch
    {
        PluginProtocol.ResultPlacements.Side => PluginProtocol.ResultPlacements.Side,
        _                                   => PluginProtocol.ResultPlacements.Inline,
    };

    // ── (b) When 評価器（構造化フィルタ・パーサなし） ────────────────────

    /// <summary>
    /// キー＝コンテキスト項目名、値＝許可値リスト、複数キーは AND、空・null は常に true。
    /// コンテキストに存在しないキー（＝未知の項目名）は false 扱いにしてログへ残す。
    /// </summary>
    private bool EvaluateWhen(Dictionary<string, List<string>> when,
        IReadOnlyDictionary<string, string?> context, string folder)
    {
        if (when == null || when.Count == 0) return true;

        foreach (var (key, allowedValues) in when)
        {
            if (!context.TryGetValue(key, out var actualValue))
            {
                AppLogger.Log(LogMsg.PluginContributionRejected, null, folder, RejectReasonUnknownWhenKey);
                return false;
            }

            if (actualValue == null) return false;
            if (allowedValues == null || !allowedValues.Contains(actualValue, StringComparer.Ordinal))
                return false;
        }

        return true;
    }

    // ── (d) コマンド発火時の標準入力に使うコンテキスト値 ────────────────

    private IReadOnlyDictionary<string, string?> BuildContextValues() =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [PluginProtocol.ContributionContext.VideoUrl]     = YouTubeConstants.WatchUrlBase + _videoId,
            [PluginProtocol.ContributionContext.VideoId]      = _videoId,
            [PluginProtocol.ContributionContext.Kind]         = _contributionKind,
            [PluginProtocol.ContributionContext.ChannelId]    = _channel.ChannelId,
            [PluginProtocol.ContributionContext.ChannelTitle] = _channel.ChannelName,
            [PluginProtocol.ContributionContext.Title]        = _title,
            [PluginProtocol.ContributionContext.PublishedAt]  = FormatPublishedAtIso(_publishedAt),
            [PluginProtocol.ContributionContext.Lang]         = UiLangValue,
        };

    private static string ToKindValue(VideoKind kind) => kind switch
    {
        VideoKind.Video    => PluginProtocol.VideoKinds.Video,
        VideoKind.Short    => PluginProtocol.VideoKinds.Short,
        VideoKind.Live     => PluginProtocol.VideoKinds.Live,
        VideoKind.Premiere => PluginProtocol.VideoKinds.Premiere,
        _                  => PluginProtocol.VideoKinds.Video,
    };

    /// <summary>
    /// 内部種別と状態から、プラグインへ渡す種別文字列を決める。
    /// 配信中・公開中・公開予定のライブ／プレミアは <c>live</c> / <c>premiere</c> のまま。
    /// 終了して通常動画として視聴できるライブアーカイブ・公開済みプレミアは <c>video</c> に正規化する。
    /// </summary>
    private string NormalizeContributionKind(VideoKind kind, bool isPending)
    {
        if (kind is not (VideoKind.Live or VideoKind.Premiere)) return ToKindValue(kind);
        if (isPending) return ToKindValue(kind);
        if (IsInActiveOrPendingList(_videoId)) return ToKindValue(kind);
        return PluginProtocol.VideoKinds.Video;
    }

    private bool IsInActiveOrPendingList(string videoId)
    {
        bool Contains(List<PendingVideoEntry> entries) =>
            entries.Any(entry => string.Equals(entry.VideoId, videoId, StringComparison.Ordinal));

        return Contains(_channel.ActiveLives)
            || Contains(_channel.ActivePremieres)
            || Contains(_channel.PendingLives)
            || Contains(_channel.PendingPremieres);
    }

    private static string? FormatPublishedAtIso(DateTime? publishedAt)
    {
        if (publishedAt == null) return null;
        return DateTime.SpecifyKind(publishedAt.Value, DateTimeKind.Utc)
            .ToString(PublishedAtIsoFormat, CultureInfo.InvariantCulture);
    }

    private string BuildInvokeInput(PluginCommand command)
    {
        var context = new JObject
        {
            [PluginProtocol.ContributionContext.VideoUrl]     = YouTubeConstants.WatchUrlBase + _videoId,
            [PluginProtocol.ContributionContext.VideoId]      = _videoId,
            [PluginProtocol.ContributionContext.Kind]         = _contributionKind,
            [PluginProtocol.ContributionContext.ChannelId]    = _channel.ChannelId,
            [PluginProtocol.ContributionContext.ChannelTitle] = _channel.ChannelName,
            [PluginProtocol.ContributionContext.Title]        = _title,
            [PluginProtocol.ContributionContext.Lang]         = UiLangValue,
        };

        var publishedAtIso = FormatPublishedAtIso(_publishedAt);
        if (publishedAtIso != null)
            context[PluginProtocol.ContributionContext.PublishedAt] = publishedAtIso;

        var root = new JObject
        {
            [PluginProtocol.ContributionInput.Command] = command.Id,
            [PluginProtocol.ContributionInput.Context] = context,
        };
        return root.ToString(Formatting.None);
    }

    // ── (c) スロット／「⋯」オーバーフロー ───────────────────────────────

    private void RenderActionButtons(List<ActionCommand> commands)
    {
        PluginActionsButtons.Children.Clear();

        var inlineCount = Math.Min(commands.Count, InlineActionSlots);
        for (var i = 0; i < inlineCount; i++)
            PluginActionsButtons.Children.Add(CreateActionButton(commands[i]));

        if (commands.Count > InlineActionSlots)
            PluginActionsButtons.Children.Add(CreateOverflowButton(commands.Skip(InlineActionSlots).ToList()));
    }

    private Button CreateActionButton(ActionCommand entry)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text         = RoundTitleToWidthBudget(entry.Command.Title),
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
            Style    = (Style)FindResource(SecondaryButtonStyleKey),
            Padding  = ActionButtonPadding,
            Margin   = ActionButtonMargin,
            MaxWidth = ActionButtonMaxWidth,
            Tag      = entry,
        };
        button.Click += ActionButton_Click;
        return button;
    }

    private Button CreateOverflowButton(List<ActionCommand> overflow)
    {
        var menu = new ContextMenu();
        foreach (var entry in overflow)
        {
            var item = new MenuItem { Header = RoundTitleToWidthBudget(entry.Command.Title), Tag = entry };
            item.Click += ActionMenuItem_Click;
            menu.Items.Add(item);
        }

        var button = new Button
        {
            Content = OverflowButtonLabel,
            Style   = (Style)FindResource(SecondaryButtonStyleKey),
            Padding = ActionButtonPadding,
            Margin  = ActionButtonMargin,
            ToolTip = OverflowButtonTooltip,
        };
        button.Click += (_, _) =>
        {
            menu.PlacementTarget = button;
            menu.IsOpen          = true;
        };
        return button;
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ActionCommand entry })
            await RunActionAsync(entry);
    }

    private async void ActionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: ActionCommand entry })
            await RunActionAsync(entry);
    }

    private async Task RunActionAsync(ActionCommand entry)
    {
        var toSide = entry.Placement == PluginProtocol.ResultPlacements.Side;
        var target = toSide
            ? new ActionRenderTarget(SidePanelResultPanel, SidePanelStatusText, SidePanelBorder)
            : new ActionRenderTarget(PluginActionsResultPanel, PluginActionsStatusText, PluginActionsPanel);

        // 表示先が異なる結果は混在させない（片方を押したらもう片方を畳む）。
        if (toSide) OpenSidePanel(StripControlChars(entry.Command.Title));
        else        CloseSidePanel();

        ShowActionStatus(StatusRunningText, target);

        var output = await PluginBridge.Instance.InvokeAsync(entry.Command.Job, BuildInvokeInput(entry.Command));

        RenderActionResult(entry, output, target);
    }

    // ── 右サイドパネルの開閉 ─────────────────────────────────────────────

    /// <summary>右サイドパネルを開く。既存のインライン結果は消し、高さ上限を親ウィンドウ基準で設定する。</summary>
    private void OpenSidePanel(string header)
    {
        ClearInlineResult();

        SidePanelHeaderText.Text        = header;
        SidePanelResultPanel.Children.Clear();
        SidePanelResultPanel.Visibility = Visibility.Collapsed;
        SidePanelStatusText.Visibility  = Visibility.Collapsed;

        SidePanelScroll.MaxHeight = Owner is { ActualHeight: > 0 } owner
            ? owner.ActualHeight * SidePanelHeightRatio
            : SidePanelHeightFallback;

        SidePanelBorder.Visibility = Visibility.Visible;
        InvalidateMeasure();
    }

    /// <summary>右サイドパネルを畳み、ウィンドウを1列幅へ戻す。</summary>
    private void CloseSidePanel()
    {
        SidePanelBorder.Visibility      = Visibility.Collapsed;
        SidePanelResultPanel.Children.Clear();
        SidePanelResultPanel.Visibility = Visibility.Collapsed;
        SidePanelStatusText.Visibility  = Visibility.Collapsed;
        InvalidateMeasure();
    }

    private void ClearInlineResult()
    {
        PluginActionsResultPanel.Children.Clear();
        PluginActionsResultPanel.Visibility = Visibility.Collapsed;
        PluginActionsStatusText.Visibility  = Visibility.Collapsed;
        PluginActionsPanel.Visibility        = Visibility.Collapsed;
    }

    private void SidePanelClose_Click(object sender, RoutedEventArgs e) => CloseSidePanel();

    // ── (e) Title の丸め（全角換算12） ─────────────────────────────────

    private static string RoundTitleToWidthBudget(string? title)
    {
        var cleaned = StripControlChars(title ?? string.Empty);

        var width   = 0;
        var builder = new StringBuilder(cleaned.Length);
        foreach (var ch in cleaned)
        {
            var charWidth = IsWideChar(ch) ? WideCharWidth : NarrowCharWidth;
            if (width + charWidth > TitleWidthBudget)
            {
                builder.Append(TitleEllipsis);
                break;
            }
            width += charWidth;
            builder.Append(ch);
        }
        return builder.ToString();
    }

    private static string StripControlChars(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
            if (!char.IsControl(ch)) builder.Append(ch);
        return builder.ToString();
    }

    /// <summary>
    /// 東アジア文字幅 W/F 相当を幅2として扱う簡易判定（厳密な Unicode テーブルは用いない）。
    /// CJK統合漢字・かな・全角記号・ハングル音節などの主要ブロックを対象にする。
    /// </summary>
    private static bool IsWideChar(char ch) =>
        (ch >= 'ᄀ' && ch <= 'ᅟ') ||   // ハングル字母
        (ch >= '⺀' && ch <= '〾') ||   // CJK 部首・康熙部首・CJK 記号
        (ch >= 'ぁ' && ch <= '㏿') ||   // かな・カタカナ・注音・CJK 互換
        (ch >= '㐀' && ch <= '䶿') ||   // CJK 拡張 A
        (ch >= '一' && ch <= '鿿') ||   // CJK 統合漢字
        (ch >= 'ꀀ' && ch <= '꓏') ||   // イ文字
        (ch >= '가' && ch <= '힣') ||   // ハングル音節
        (ch >= '豈' && ch <= '﫿') ||   // CJK 互換漢字
        (ch >= '︰' && ch <= '﹏') ||   // CJK 互換形
        (ch >= '＀' && ch <= '｠') ||   // 全角英数記号
        (ch >= '￠' && ch <= '￦');     // 全角記号

    // ── (f) 結果種別のレンダリング ────────────────────────────────────

    private void RenderActionResult(ActionCommand entry, string? output, ActionRenderTarget target)
    {
        target.ResultPanel.Children.Clear();
        target.ResultPanel.Visibility = Visibility.Collapsed;

        if (string.IsNullOrWhiteSpace(output))
        {
            ShowActionStatus(StatusFailedText, target);
            return;
        }

        if (Encoding.UTF8.GetByteCount(output) > ResultJsonMaxBytes)
        {
            ShowActionStatus(StatusFailedText, target);
            return;
        }

        switch (entry.ResultKind)
        {
            case PluginProtocol.ResultKinds.Segments:
                RenderSegmentsResult(output, target);
                break;
            case PluginProtocol.ResultKinds.Text:
                RenderTextResult(output, target);
                break;
            default:
                ShowActionStatus(ResultNoneText, target);
                break;
        }
    }

    private void RenderSegmentsResult(string json, ActionRenderTarget target)
    {
        PluginSegmentsResult? parsed;
        try { parsed = JsonConvert.DeserializeObject<PluginSegmentsResult>(json); }
        catch { ShowActionStatus(StatusFailedText, target); return; }

        if (parsed?.Items == null || parsed.Items.Count == 0)
        {
            ShowActionStatus(StatusFailedText, target);
            return;
        }

        int? maxSeconds = _duration.HasValue
            ? Math.Max(0, (int)_duration.Value.TotalSeconds)
            : null;

        var rows = parsed.Items
            .Take(SegmentsMaxItems)
            .Select(item => new PluginSegmentItem
            {
                Start = ClampStart(item.Start, maxSeconds),
                Title = TrimToLength(item.Title, SegmentTitleMaxChars),
                Body  = TrimToLength(item.Body,  SegmentBodyMaxChars),
            })
            .OrderBy(item => item.Start)
            .ToList();

        target.ResultPanel.Children.Clear();
        foreach (var row in rows)
            target.ResultPanel.Children.Add(CreateSegmentRow(row));

        ShowResultPanel(target);
    }

    private static int ClampStart(int start, int? maxSeconds)
    {
        if (start < 0) start = 0;
        if (maxSeconds.HasValue && start > maxSeconds.Value) start = maxSeconds.Value;
        return start;
    }

    private Border CreateSegmentRow(PluginSegmentItem item)
    {
        var stack = new StackPanel();

        var header = new TextBlock
        {
            Text = string.IsNullOrEmpty(item.Title)
                ? FormatSegmentTimestamp(item.Start)
                : $"{FormatSegmentTimestamp(item.Start)}  {item.Title}",
            FontSize     = SegmentHeaderFontSize,
            TextWrapping = TextWrapping.Wrap,
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, TextPrimaryBrushKey);
        stack.Children.Add(header);

        if (!string.IsNullOrEmpty(item.Body))
        {
            var body = new TextBlock
            {
                Text         = item.Body,
                FontSize     = SegmentBodyFontSize,
                Margin       = SegmentBodyMargin,
                TextWrapping = TextWrapping.Wrap,
            };
            body.SetResourceReference(TextBlock.ForegroundProperty, TextSecondaryBrushKey);
            stack.Children.Add(body);
        }

        var row = new Border
        {
            Child        = stack,
            Padding      = SegmentRowPadding,
            Margin       = SegmentRowMargin,
            CornerRadius = new CornerRadius(SegmentRowCornerRadius),
            Cursor       = System.Windows.Input.Cursors.Hand,
        };
        row.SetResourceReference(Border.BackgroundProperty, SurfaceAltBrushKey);
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            OpenWatchUrlAtSeconds(item.Start);
        };
        return row;
    }

    private static string FormatSegmentTimestamp(int totalSeconds)
    {
        var hours   = totalSeconds / SecondsPerHour;
        var minutes = totalSeconds % SecondsPerHour / SecondsPerMinute;
        var seconds = totalSeconds % SecondsPerMinute;
        return hours > 0
            ? $"{hours}:{minutes:D2}:{seconds:D2}"
            : $"{minutes}:{seconds:D2}";
    }

    private void RenderTextResult(string json, ActionRenderTarget target)
    {
        PluginTextResult? parsed;
        try { parsed = JsonConvert.DeserializeObject<PluginTextResult>(json); }
        catch { ShowActionStatus(StatusFailedText, target); return; }

        if (parsed == null || string.IsNullOrEmpty(parsed.Detail))
        {
            ShowActionStatus(StatusFailedText, target);
            return;
        }

        target.ResultPanel.Children.Clear();

        if (!string.IsNullOrEmpty(parsed.Headline))
        {
            var headline = new TextBlock
            {
                Text         = TrimToLength(parsed.Headline, TextHeadlineMaxChars),
                FontSize     = ResultHeadlineFontSize,
                FontWeight   = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            };
            headline.SetResourceReference(TextBlock.ForegroundProperty, TextPrimaryBrushKey);
            target.ResultPanel.Children.Add(headline);
        }

        var detail = new TextBlock
        {
            Text         = TrimToLength(parsed.Detail, TextDetailMaxChars),
            FontSize     = ResultDetailFontSize,
            Margin       = ResultDetailMargin,
            TextWrapping = TextWrapping.Wrap,
        };
        detail.SetResourceReference(TextBlock.ForegroundProperty, TextPrimaryBrushKey);
        target.ResultPanel.Children.Add(detail);

        ShowResultPanel(target);
    }

    private static string TrimToLength(string? value, int maxChars)
    {
        var text = value ?? string.Empty;
        return text.Length <= maxChars ? text : text[..maxChars] + TitleEllipsis;
    }

    private static void ShowActionStatus(string text, ActionRenderTarget target)
    {
        target.ResultPanel.Children.Clear();
        target.ResultPanel.Visibility = Visibility.Collapsed;
        target.StatusText.Text        = text;
        target.StatusText.Visibility  = Visibility.Visible;
        target.Container.Visibility   = Visibility.Visible;
    }

    private static void ShowResultPanel(ActionRenderTarget target)
    {
        target.StatusText.Visibility  = Visibility.Collapsed;
        target.ResultPanel.Visibility = Visibility.Visible;
        target.Container.Visibility   = Visibility.Visible;
    }

    // ── (g) 時刻付き URL の組み立て（本体所有） ────────────────────────

    private void OpenWatchUrlAtSeconds(int clampedStart)
    {
        var url = YouTubeConstants.WatchUrlBase + _videoId
                  + YouTubeConstants.TimeParamPrefix + clampedStart + YouTubeConstants.TimeParamSuffix;
        OpenInDefaultBrowser(url);
    }

    private static void OpenInDefaultBrowser(string url)
    {
        if (!url.StartsWith(HttpsScheme, StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith(HttpScheme,  StringComparison.OrdinalIgnoreCase))
            return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    // ── 内部型 ────────────────────────────────────────────────────────

    /// <summary>描画対象として確定した1コマンド（宣言と、丸めた結果種別・表示先）。</summary>
    private sealed record ActionCommand(PluginCommand Command, string ResultKind, string Placement);

    /// <summary>結果の描画先（インライン＝本文下／サイド＝右パネル）。</summary>
    private sealed record ActionRenderTarget(
        System.Windows.Controls.Panel ResultPanel, TextBlock StatusText, UIElement Container);
}
