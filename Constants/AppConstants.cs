namespace YTNotifier.Constants;

internal static class AppConstants
{
    public const string AppName          = "YTNotifier";
    public const string AppVersion       = "0.9.5";
    public const string DirLogs          = "logs";
    public const string DirSounds        = "Sounds";
    public const string FileApiKey              = "api_key.dat";
    public const string FileGeminiApiKey        = "gemini_api_key.dat";

    /// <summary>設定ファイル群を置くフォルダ名（アプリデータフォルダ直下）</summary>
    public const string DirConf          = "conf";
    /// <summary>バックアップ関連ファイルを置くフォルダ名（アプリデータフォルダ直下）</summary>
    public const string DirBackup        = "bkup";
    public const string FileConfig             = "config.json";
    public const string FileChannels           = "channels.json";
    public const string FileCategories         = "categories.json";
    public const string FileDormantCategories  = "dormant_categories.json";
    public const string FileState              = "state.json";
    public const string FileRecentUploads      = "recent_uploads.json.gz";
    public const string FileReportHistory      = "report_history.json";
    /// <summary>終了時に自動保存するバックアップのファイル名</summary>
    public const string FileAutoBackup         = "auto_backup.ytbk";

    /// <summary>Gemini 要約に使用するモデル名</summary>
    public const string GeminiModelName = "gemini-3.5-flash-lite";
    public const string BackupFileFilter     = "YTNotifierバックアップ (*.ytbk)|*.ytbk";

    /// <summary>全曜日ビットマスク（bit0=日〜bit6=土）</summary>
    public const int AllDaysMask = 0b1111111;

    /// <summary>無効化されたコントロールの不透明度</summary>
    public const double DisabledControlOpacity = 0.3;

    /// <summary>種別タブ（動画・Short・ライブ）の数。FocusSlots の要素数とは一致しない（1種別に複数スロットを持てる）</summary>
    public const int KindSlotCount = 3;

    /// <summary>種別スロットの表示ラベル（動画・Short・ライブ）。
    /// 種別タブの並び順</summary>
    public static readonly string[] KindSlotLabels = { "動画", "Short", "ライブ" };

    /// <summary>種別スロットの種別。順序は KindSlotLabels と一致する</summary>
    public static readonly VideoKind[] KindSlotKinds =
        { VideoKind.Video, VideoKind.Short, VideoKind.Live };

    /// <summary>レポートの集計対象日数（履歴の保持期間と、レポート表示の日数の双方で使う）</summary>
    public const int ReportDays = 7;

    /// <summary>API使用量バーの端のセグメントに付ける角丸</summary>
    public const double QuotaBarCornerRadius = 4;

    /// <summary>チャンネル数表記の単位文字列（タイトルバー表示用）</summary>
    public const string ChannelCountUnitSuffix = "ch";

    /// <summary>ログ引数に使う、設定が有効な状態の表記</summary>
    public const string LogOnText = "ON";

    /// <summary>ログ引数に使う、設定が無効な状態の表記</summary>
    public const string LogOffText = "OFF";

    /// <summary>ログ引数に使う、折り畳んだ状態の表記（カテゴリ・サイドバー）</summary>
    public const string LogCollapsedText = "折り畳み";

    /// <summary>ログ引数に使う、展開した状態の表記（カテゴリ・サイドバー）</summary>
    public const string LogExpandedText = "展開";

    /// <summary>カテゴリに属さないチャンネルのまとまりの名称（見出し行・ログ引数）</summary>
    public const string UncategorizedLabel = "未分類";

    /// <summary>「カテゴリを移動」メニューの、未分類へ移す項目の表記（値は「（未分類）」）</summary>
    public const string UncategorizedMenuLabel = "（" + UncategorizedLabel + "）";

    /// <summary>
    /// プラグイン拡張リージョンID → 設定画面・ログで見せる日本語名。
    /// リージョンIDは Plugin.dll（<c>PluginProtocol.Regions</c>）が正。ここに無いIDは表示対象外。
    /// 動画詳細ポップアップ側とプラグイン設定ページの双方から参照するため共有定数として置く。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> PluginRegionDisplayNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [YTNotifier.Plugin.PluginProtocol.Regions.VideoDetailActions] = "動画詳細ポップアップ",
        };

    private static readonly TimeZoneInfo _pacificTz =
        TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");

    /// <summary>現在のクォータ日付キー（太平洋時間の日付）を返す</summary>
    public static string GetQuotaDayKey()
    {
        var nowPt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _pacificTz);
        return nowPt.Date.ToString("yyyy-MM-dd");
    }
}
