namespace YTNotifier.Services;

/// <summary>インターネット接続の有無を判定する。
/// 接続テスト用 URL への到達を試し、失敗したら Ping で再確認する</summary>
internal static class NetworkStatusService
{
    /// <summary>接続テスト先（Windows のネットワーク接続確認と同じエンドポイント）</summary>
    private const string ConnectTestUrl = "http://www.msftconnecttest.com/connecttest.txt";

    /// <summary>接続テストの待ち時間（ミリ秒）</summary>
    private const int ConnectTestTimeoutMilliseconds = 3000;

    /// <summary>接続テストが失敗したときに Ping する先</summary>
    private const string FallbackPingTarget = "8.8.8.8";

    /// <summary>Ping の待ち時間（ミリ秒）</summary>
    private const int PingTimeoutMilliseconds = 1000;

    /// <summary>HttpClient 全体の待ち時間（秒）</summary>
    private const int HttpClientTimeoutSeconds = 10;

    // ソケット枯渇を避けるため使い回す
    private static readonly System.Net.Http.HttpClient _httpClient =
        new() { Timeout = TimeSpan.FromSeconds(HttpClientTimeoutSeconds) };

    /// <summary>インターネットに接続できるかを判定する</summary>
    internal static async Task<bool> IsOnlineAsync()
    {
        try
        {
            if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
                return false;

            using var cts = new System.Threading.CancellationTokenSource(ConnectTestTimeoutMilliseconds);
            var response = await _httpClient.GetAsync(ConnectTestUrl, cts.Token);
            var isAvailable = response.StatusCode == System.Net.HttpStatusCode.OK;

            if (!isAvailable)
            {
                using var ping = new System.Net.NetworkInformation.Ping();
                var reply = await ping.SendPingAsync(FallbackPingTarget, PingTimeoutMilliseconds);
                isAvailable = reply.Status == System.Net.NetworkInformation.IPStatus.Success;
            }

            return isAvailable;
        }
        catch
        {
            return false;
        }
    }
}
