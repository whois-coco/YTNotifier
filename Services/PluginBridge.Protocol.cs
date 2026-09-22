using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System.IO;
using YTNotifier.Constants;
using YTNotifier.Plugin;

namespace YTNotifier.Services;

// 部分クラス: メッセージの処理・待ち時間の上限
public partial class PluginBridge
{
    /// <summary>HTTP呼び出し中のハートビート間隔を無音タイムアウトの何分の1にするかの係数。無音タイムアウト値を変更してもハートビート間隔が自動追随する。</summary>
    private const int    HttpHeartbeatIntervalDivisor = 3;

    /// <summary>待ち時間の上限の判定結果をログへ載せるときの表記（PluginJobTimeout の {1}）</summary>
    private const string TimeoutKindOverall = "全体";
    private const string TimeoutKindIdle    = "無音";

    /// <summary>やりとりの版が一致しないときにホストへ返す理由（WelcomeMessage.Reason）。</summary>
    private const string ProtocolVersionMismatchReason = "やりとりの版が一致しません";

    private static readonly JsonSerializerSettings MessageSettings = new()
    {
        ContractResolver = new DefaultContractResolver
        {
            NamingStrategy = new CamelCaseNamingStrategy(processDictionaryKeys: false, overrideSpecifiedNames: true),
        },
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.None,
    };

    private void Dispatch(string line)
    {
        var messageType = JObject.Parse(line).Value<string>(PluginProtocol.TypeField) ?? string.Empty;

        switch (messageType)
        {
            case PluginProtocol.MessageTypes.Hello:
                OnHello(Deserialize<HelloMessage>(line));
                break;
            case PluginProtocol.MessageTypes.Plugins:
                OnPlugins(Deserialize<PluginsMessage>(line));
                break;
            case PluginProtocol.MessageTypes.Progress:
            {
                var progress = Deserialize<ProgressMessage>(line);
                if (_activeInvokes.TryGetValue(progress.Id, out var context)) context.MarkActivity();
                break;
            }
            case PluginProtocol.MessageTypes.Result:
            {
                var result = Deserialize<ResultMessage>(line);
                if (_activeInvokes.TryGetValue(result.Id, out var context)) context.Complete(result);
                break;
            }
            case PluginProtocol.MessageTypes.Call:
            {
                var call = Deserialize<CallMessage>(line);
                _ = Task.Run(() => OnCallAsync(call));
                break;
            }
        }
    }

    private void OnHello(HelloMessage hello)
    {
        if (hello.ProtocolVersion != PluginProtocol.ProtocolVersion)
        {
            TaskCompletionSource<bool>? startupCompletion;
            lock (_sync)
            {
                _disabled         = true;
                _stopping         = true;
                startupCompletion = _startupCompletion;
            }
            // 起動を待っている側を失敗として解放する
            startupCompletion?.TrySetResult(false);
            AppLogger.Log(LogMsg.PluginHostVersionMismatch, null, AppConstants.AppVersion, hello.HostVersion);
            _ = SendAsync(new WelcomeMessage
            {
                ProtocolVersion = PluginProtocol.ProtocolVersion,
                Accepted        = false,
                Reason          = ProtocolVersionMismatchReason,
            });
            return;
        }

        _ = SendAsync(new WelcomeMessage
        {
            ProtocolVersion = PluginProtocol.ProtocolVersion,
            Accepted        = true,
        });
    }

    private void OnPlugins(PluginsMessage message)
    {
        foreach (var error in message.Errors)
            AppLogger.Log(LogMsg.PluginManifestInvalid, null, error.Folder, error.Reason);

        TaskCompletionSource<bool>? startupCompletion;
        lock (_sync)
        {
            _plugins          = message.Items;
            _connected        = true;
            startupCompletion = _startupCompletion;
        }

        foreach (var descriptor in message.Items)
            AppLogger.Log(LogMsg.PluginDetected, null, descriptor.Name, descriptor.Folder);

        RecordDiscoveredPlugins(message.Items);

        AppLogger.Log(LogMsg.PluginHostStarted, null, message.Items.Count);

        // 起動を待っている側を解放する（一覧の記録とログが済んでから）
        startupCompletion?.TrySetResult(true);
    }

    private async Task OnCallAsync(CallMessage call)
    {
        var output = string.Empty;

        if (_activeInvokes.TryGetValue(call.Id, out var context))
        {
            context.MarkActivity();
            try
            {
                if (call.Api == PluginProtocol.Apis.Http)
                {
                    var input = Deserialize<HttpCallInput>(call.Input);

                    // Gemini API応答待ち等、HttpRequest が応答を待っている間は活動時刻の更新が起きないため、
                    // 定期的に MarkActivity() を呼び続けて無音タイムアウトの誤検知を防ぐ。
                    var heartbeatInterval = TimeSpan.FromSeconds(PluginIdleTimeoutSeconds) / HttpHeartbeatIntervalDivisor;
                    var heartbeatTimer = new System.Threading.Timer(_ => context.MarkActivity(), null, heartbeatInterval, heartbeatInterval);
                    try
                    {
                        output = context.HttpGateway.HttpRequest(input.Method, input.Url, input.HeadersJson, input.BodyText);
                    }
                    finally
                    {
                        heartbeatTimer.Dispose();
                    }
                }
                else if (call.Api == PluginProtocol.Apis.Log)
                {
                    var input = Deserialize<LogCallInput>(call.Input);
                    context.LogGateway.Log(input.Message);
                }
            }
            catch { /* 窓口の失敗は握りつぶし、下で空応答を返す */ }
            context.MarkActivity();
        }

        await SendAsync(new CallResultMessage { Id = call.Id, Output = output }).ConfigureAwait(false);
    }

    private async Task SendAsync(object message)
    {
        StreamWriter? writer;
        lock (_sync) writer = _writer;
        if (writer == null) return;

        var line = JsonConvert.SerializeObject(message, MessageSettings);

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(line).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        catch { /* パイプ断は受信ループ側で検知して後始末する */ }
        finally
        {
            _writeLock.Release();
        }
    }

    private static T Deserialize<T>(string line) where T : new()
        => JsonConvert.DeserializeObject<T>(line, MessageSettings) ?? new T();

    // ── 待ち時間の上限 ─────────────────────────────────────────────────────

    private static async Task<InvokeOutcome> AwaitResultAsync(InvokeContext context,
        TimeSpan overallTimeout, TimeSpan idleTimeout)
    {
        var overallDeadline = DateTime.UtcNow + overallTimeout;

        while (true)
        {
            var now = DateTime.UtcNow;
            if (now >= overallDeadline) return InvokeOutcome.Timeout(TimeoutKindOverall);

            var idleDeadline = context.LastActivityUtc + idleTimeout;
            if (now >= idleDeadline) return InvokeOutcome.Timeout(TimeoutKindIdle);

            var wait = overallDeadline - now < idleDeadline - now ? overallDeadline - now : idleDeadline - now;

            using var delayCts = new CancellationTokenSource();
            var finished = await Task.WhenAny(context.ResultTask, Task.Delay(wait, delayCts.Token)).ConfigureAwait(false);
            delayCts.Cancel(); // 結果が先に届いた場合に残る待ちタイマーを解除する（解除された待ちは await しない）
            if (finished != context.ResultTask) continue;

            try { return InvokeOutcome.Completed(await context.ResultTask.ConfigureAwait(false)); }
            catch { return InvokeOutcome.Timeout(TimeoutKindOverall); }
        }
    }

    private readonly struct InvokeOutcome
    {
        private InvokeOutcome(bool timedOut, string timeoutKind, ResultMessage? result)
        {
            TimedOut    = timedOut;
            TimeoutKind = timeoutKind;
            Result      = result;
        }

        public bool TimedOut { get; }
        public string TimeoutKind { get; }
        public ResultMessage? Result { get; }

        public static InvokeOutcome Timeout(string kind)      => new(true, kind, null);
        public static InvokeOutcome Completed(ResultMessage resultMessage) => new(false, string.Empty, resultMessage);
    }
}
