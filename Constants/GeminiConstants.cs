namespace YTNotifier.Constants;

internal static class GeminiConstants
{
    /// <summary>gemini-3.5-flash-lite の無料枠 RPD（1日あたりリクエスト数）上限。
    /// 公式ドキュメントに記載がないため、2026-09-12 時点の Google AI Studio 実測値を採用。
    /// Google側の仕様変更で変わりうるため、ずれてきたら実測し直して更新すること</summary>
    public const int DailyRequestLimit = 500;
}
