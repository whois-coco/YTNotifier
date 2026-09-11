namespace YTNotifier.Constants;

internal static class YouTubeConstants
{
    public const string WatchUrlBase = "https://www.youtube.com/watch?v=";

    /// <summary>watch URL に再生開始秒を足すときの接頭辞（例：<c>&amp;t=</c>）</summary>
    public const string TimeParamPrefix = "&t=";

    /// <summary>watch URL の再生開始秒に付ける接尾辞（秒指定の <c>s</c>）</summary>
    public const string TimeParamSuffix = "s";

    /// <summary>channels.list の Id に一括指定するチャンネルIDの1回あたり最大件数（BAN・自主削除判定バッチ）</summary>
    public const int BanCheckChunkSize = 50;
}
