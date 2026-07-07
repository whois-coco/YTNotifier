namespace YTNotifier.Constants;

internal static class YouTubeConstants
{
    public const string WatchUrlBase = "https://www.youtube.com/watch?v=";

    /// <summary>APIキー有効性テスト用の固定チャンネルID（日本のYouTube公式チャンネル）</summary>
    public const string ApiKeyTestChannelId = "UCrXUsMBcfTVqwAS7DKg9C0Q";

    /// <summary>Short判定: 9:16（縦長）のアスペクト比</summary>
    public const double ShortAspectRatioVertical = 0.5625;

    /// <summary>Short判定: 1:1（正方形）のアスペクト比</summary>
    public const double ShortAspectRatioSquare = 1.0;

    /// <summary>Short判定のアスペクト比許容誤差（±10%）</summary>
    public const double ShortAspectRatioTolerance = 0.10;
}
