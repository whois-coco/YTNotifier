using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using YTNotifier.Plugin;

namespace YTNotifier.Services;

/// <summary>
/// 本体側で唯一プラグインを知る場所。プラグインは本体では読み込まず、別プロセス
/// <c>PluginHost.exe</c> に任せる。本体は名前付きパイプのサーバーとなり、通信（HTTP）と
/// ログ出力を代行する。要約用APIキーはこのプロセスから外へ出ない。
/// </summary>
public sealed partial class PluginBridge
{
    private static readonly Lazy<PluginBridge> _lazy = new(() => new PluginBridge());
    public static PluginBridge Instance => _lazy.Value;

    private const int    HostStartupWaitSeconds = 10;
    private const int    ShutdownWaitSeconds   = 5;
    private const int    PluginOverallTimeoutMinutes = 5;
    private const int    PluginIdleTimeoutSeconds = 30;

    /// <summary>依頼結果の Output が空だったときの失敗理由（PluginJobFailed の {1}）。</summary>
    private const string EmptyResultReason = "結果が空でした";

    /// <summary>ホストを起動できなかった、または待ち時間内に接続が完了しなかったときの依頼結果。</summary>
    private static readonly PluginInvokeResult HostUnavailableResult = new(Output: null, HostUnavailable: true);

    /// <summary>ホストは使えたが、プラグイン側の処理が失敗した（または停止処理中の）ときの依頼結果。</summary>
    private static readonly PluginInvokeResult InvokeFailedResult = new(Output: null, HostUnavailable: false);

    private readonly object _sync = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, InvokeContext> _activeInvokes = new();

    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Process? _hostProcess;

    /// <summary>ホストを閉じ込めている資源の枠。閉じると残っているホストのプロセスも終了する。</summary>
    private SafeHandle? _jobHandle;

    private CancellationTokenSource? _cts;

    private bool _started;
    private bool _connected;
    private bool _disabled;
    private bool _stopping;
    private int  _nextInvokeId;

    /// <summary>直近のホスト起動について、プラグイン一覧の受信完了（true）／やりとりの版の不一致（false）を知らせる待ち合わせ。起動のたびに作り直す。</summary>
    private TaskCompletionSource<bool>? _startupCompletion;

    private IReadOnlyList<PluginDescriptor> _plugins = Array.Empty<PluginDescriptor>();

    private PluginBridge() { }

    /// <summary>見つかったプラグインの一覧（第2弾の設定画面が参照する）。</summary>
    public IReadOnlyList<PluginDescriptor> Plugins
    {
        get { lock (_sync) return _plugins; }
    }

    /// <summary>
    /// <c>PluginHost.exe</c> が実行ファイルと同じフォルダにあり、かつ <c>Plugins\</c> に
    /// プラグインフォルダが1つ以上あるとき、パイプを用意してホストを起動する。
    /// どちらか欠ければ何もしない。本体の起動時に1回だけ呼ぶ（旧形式の設定ファイルの移行もここで1回だけ行う）。
    /// 依頼時の起動は <see cref="InvokeAsync"/> が <see cref="TryStartHost"/> で行う。
    /// </summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_started) return;

            MigrateLegacyPluginConfigIfNeeded();

            TryStartHost();
        }
    }

    /// <summary>
    /// <c>shutdown</c> を送り、一定時間内に終了しなければプロセスを強制終了する。
    /// </summary>
    public void Stop()
    {
        Process? hostProcess;
        CancellationTokenSource? cts;
        lock (_sync)
        {
            _stopping = true;
            if (!_started) return;
            hostProcess  = _hostProcess;
            cts          = _cts;
        }

        try { Task.Run(() => SendAsync(new ShutdownMessage())).Wait(TimeSpan.FromSeconds(ShutdownWaitSeconds)); }
        catch { /* 送信できなくても下で強制終了する */ }

        try
        {
            if (hostProcess is { HasExited: false } &&
                !hostProcess.WaitForExit((int)TimeSpan.FromSeconds(ShutdownWaitSeconds).TotalMilliseconds))
            {
                hostProcess.Kill(entireProcessTree: true);
            }
        }
        catch { /* すでに終了している場合など */ }

        try { cts?.Cancel(); } catch { }
        lock (_sync) { Cleanup(); }
    }

    /// <summary>
    /// 優先順に従ってプラグインへ仕事を依頼し、結果の JSON 文字列を返す。
    /// ホストが使える状態（通信路が有効でプラグイン一覧を受け取り済み）でなければ、起動してから依頼する。
    /// 全て失敗した場合は結果の文字列が <c>null</c> になる（呼び出し元が「フォールバックすべきサイン」として扱う）。
    /// ホストを起動できなかった場合は <see cref="PluginInvokeResult.HostUnavailable"/> が <c>true</c> になる。
    /// 例外は外に投げない。
    /// </summary>
    public async Task<PluginInvokeResult> InvokeAsync(string job, string inputJson)
    {
        try
        {
            // ロック内では「起動が必要か」の判定と待ち合わせの取得までを行い、待機はロックを抜けてから行う。
            Task<bool>? startupWait = null;
            lock (_sync)
            {
                if (_disabled) return HostUnavailableResult;
                if (_stopping) return InvokeFailedResult;

                if (!IsHostReady())
                {
                    if (!_started && !TryStartHost()) return HostUnavailableResult;
                    startupWait = _startupCompletion?.Task;
                }
            }

            if (startupWait != null && !await WaitForStartupAsync(startupWait).ConfigureAwait(false))
            {
                // 版の不一致（無効化済み）は停止処理中も同時に立つため、無効化の確認を先に行う
                lock (_sync)
                {
                    if (_disabled) return HostUnavailableResult;
                    if (_stopping) return InvokeFailedResult;
                }
                return HostUnavailableResult;
            }

            List<PluginDescriptor> candidates;
            lock (_sync)
            {
                if (_disabled) return HostUnavailableResult;
                if (_stopping) return InvokeFailedResult;
                if (!IsHostReady()) return HostUnavailableResult;

                candidates = ResolveOrderedEnabled(job);
            }
            if (candidates.Count == 0) return InvokeFailedResult;

            var apiKey = GeminiApiKeyService.Load(SettingsService.Instance.ConfDir) ?? string.Empty;
            var overallTimeout = TimeSpan.FromMinutes(PluginOverallTimeoutMinutes);
            var idleTimeout    = TimeSpan.FromSeconds(PluginIdleTimeoutSeconds);

            foreach (var plugin in candidates)
            {
                var invokeId = Interlocked.Increment(ref _nextInvokeId);
                var context  = new InvokeContext(invokeId, plugin.Folder, apiKey);
                _activeInvokes[invokeId] = context;
                try
                {
                    AppLogger.Log(LogMsg.PluginJobRequested, null, job, plugin.Folder);

                    await SendAsync(new InvokeMessage
                    {
                        Id            = invokeId,
                        Folder        = plugin.Folder,
                        Job           = job,
                        Input         = inputJson,
                        TimeoutMs     = (int)overallTimeout.TotalMilliseconds,
                        IdleTimeoutMs = (int)idleTimeout.TotalMilliseconds,
                    }).ConfigureAwait(false);

                    var outcome = await AwaitResultAsync(context, overallTimeout, idleTimeout).ConfigureAwait(false);

                    if (outcome.TimedOut)
                    {
                        AppLogger.Log(LogMsg.PluginJobTimeout, null, plugin.Folder, outcome.TimeoutKind);
                        continue;
                    }

                    var result = outcome.Result!;
                    if (!result.Ok || string.IsNullOrWhiteSpace(result.Output))
                    {
                        var reason = string.IsNullOrEmpty(result.Error) ? EmptyResultReason : result.Error;
                        AppLogger.Log(LogMsg.PluginJobFailed, null, job, reason);
                        continue;
                    }

                    return new PluginInvokeResult(Output: result.Output, HostUnavailable: false);
                }
                finally
                {
                    _activeInvokes.TryRemove(invokeId, out _);
                }
            }

            return InvokeFailedResult;
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.PluginInvokeError, null, job, ex.Message);
            return InvokeFailedResult;
        }
    }

    /// <summary>通信路が有効で、プラグイン一覧を受け取り済みか。プロセスが生きているかは見ない。呼び出し側で <see cref="_sync"/> を保持していること。</summary>
    private bool IsHostReady() => _connected && _pipe != null;

    /// <summary>起動完了の待ち合わせを最大 <see cref="HostStartupWaitSeconds"/> 秒待つ。プラグイン一覧を受け取れたときだけ <c>true</c>。</summary>
    private static async Task<bool> WaitForStartupAsync(Task<bool> startupWait)
    {
        try
        {
            return await startupWait.WaitAsync(TimeSpan.FromSeconds(HostStartupWaitSeconds)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private sealed class InvokeContext
    {
        private readonly TaskCompletionSource<ResultMessage> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private long _lastActivityTicks;

        public InvokeContext(int id, string folderName, string apiKey)
        {
            Id          = id;
            HttpGateway = new PluginHttpGateway(apiKey);
            LogGateway  = new PluginLogGateway(folderName);
            MarkActivity();
        }

        public int Id { get; }
        public PluginHttpGateway HttpGateway { get; }
        public PluginLogGateway  LogGateway  { get; }
        public Task<ResultMessage> ResultTask => _completion.Task;

        public DateTime LastActivityUtc =>
            new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);

        public void MarkActivity() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
        public void Complete(ResultMessage result) => _completion.TrySetResult(result);
        public void Fail() => _completion.TrySetCanceled();
    }
}
