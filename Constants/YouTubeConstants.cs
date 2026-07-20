namespace YTNotifier.Constants;

internal static class YouTubeConstants
{
    public const string WatchUrlBase = "https://www.youtube.com/watch?v=";

    /// <summary>channels.list の Id に一括指定するチャンネルIDの1回あたり最大件数（BAN・自主削除判定バッチ）</summary>
    public const int BanCheckChunkSize = 50;
}
