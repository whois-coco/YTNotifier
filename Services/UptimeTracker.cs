namespace YTNotifier.Services;

/// <summary>
/// アプリの起動時間を記録する常駐サービス。
/// 1分ごとに前回の刻みからの経過秒をレポート履歴へ加算する。
/// 経過が閾値を超えた場合（PCスリープ・休止からの復帰）は加算せず捨てる（スリープ中を起動時間に含めない）。
/// 終了時の最後の1分未満は記録されない。
/// </summary>
public sealed class UptimeTracker : IDisposable
{
    /// <summary>刻み間隔（秒）</summary>
    private const int TickIntervalSeconds = 60;

    /// <summary>この秒数を超える経過は、スリープ・休止からの復帰とみなして加算しない</summary>
    private const int DiscardThresholdSeconds = 120;

    private const int MillisecondsPerSecond = 1000;

    private System.Threading.Timer? _timer;
    private DateTime _lastTickUtc;

    /// <summary>記録を開始する</summary>
    public void Start()
    {
        if (_timer != null) return;

        _lastTickUtc = DateTime.UtcNow;
        var intervalMilliseconds = TickIntervalSeconds * MillisecondsPerSecond;
        _timer = new System.Threading.Timer(OnTick, null, intervalMilliseconds, intervalMilliseconds);
    }

    private void OnTick(object? state)
    {
        try
        {
            var nowUtc         = DateTime.UtcNow;
            var elapsedSeconds = (nowUtc - _lastTickUtc).TotalSeconds;
            _lastTickUtc = nowUtc;

            // 時計の巻き戻し（0以下）とスリープ復帰（閾値超え）は加算しない
            if (elapsedSeconds <= 0 || elapsedSeconds > DiscardThresholdSeconds) return;

            SettingsService.Instance.UsageStats.AddUptimeSeconds((int)Math.Round(elapsedSeconds));
        }
        catch
        {
            // 常駐タイマーの例外でプロセスを落とさない（次の刻みで記録を続ける）
        }
    }

    /// <summary>記録を停止してタイマーを解放する</summary>
    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
