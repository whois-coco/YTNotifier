namespace YTNotifier.Constants;

internal static class AppConstants
{
    public const string AppName          = "YTNotifier";
    public const string AppVersion       = "0.9.2";
    public const string DirLogs          = "logs";
    public const string DirSounds        = "Sounds";
    public const string FileApiKey              = "api_key.dat";
    public const string FileGeminiApiKey        = "gemini_api_key.dat";
    public const string BackupFileFilter     = "YTNotifierバックアップ (*.ytbk)|*.ytbk";

    /// <summary>全曜日ビットマスク（bit0=日〜bit6=土）</summary>
    public const int AllDaysMask = 0b1111111;

    /// <summary>無効化されたコントロールの不透明度</summary>
    public const double DisabledControlOpacity = 0.3;

    /// <summary>種別スロット（動画・Short・ライブ）の数。FocusSlots の要素数と一致する</summary>
    public const int KindSlotCount = 3;

    /// <summary>種別スロットの表示ラベル（動画・Short・ライブ）。
    /// 順序は FocusSlots のインデックスと一致する</summary>
    public static readonly string[] KindSlotLabels = { "動画", "Short", "ライブ" };

    /// <summary>種別スロットの種別。順序は KindSlotLabels と一致する</summary>
    public static readonly VideoKind[] KindSlotKinds =
        { VideoKind.Video, VideoKind.Short, VideoKind.Live };

    /// <summary>API使用量バーの端のセグメントに付ける角丸</summary>
    public const double QuotaBarCornerRadius = 4;

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
