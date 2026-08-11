namespace YTNotifier.Services;

/// <summary>
/// 要約スクリプト（Plugins\summary.js）へ公開する、ログ出力の窓口。
/// メッセージ文字列を受け取り AppLogger 経由でログに記録する。
/// 呼び出し回数・メッセージ長に上限を設け、スクリプトによるログ肥大化を防ぐ。
/// </summary>
public sealed class ScriptLogGateway
{
    private const int MaxMessageLength = 500;
    private const int MaxCallCount     = 20;

    private int _callCount;

    /// <summary>
    /// スクリプトから呼ばれるログ出力関数。呼び出し上限に達した場合は何もしない
    /// （例外を投げない。スクリプトの実行を止めないため）。
    /// </summary>
    public void Log(string message)
    {
        if (_callCount >= MaxCallCount) return;
        _callCount++;

        var trimmed = string.IsNullOrEmpty(message)
            ? string.Empty
            : message.Length > MaxMessageLength ? message.Substring(0, MaxMessageLength) : message;

        AppLogger.Log(LogMsg.SummaryScriptLog, null, trimmed);
    }
}
