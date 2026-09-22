using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;

namespace YTNotifier.Services;

// 部分クラス: パイプとホストプロセス
public partial class PluginBridge
{
    private const string PluginsDirName        = "Plugins";
    private const string HostFileName          = "PluginHost.exe";
    private const string ManifestFileName      = "plugin.json";
    private const string PipeNamePrefix        = "YTNotifier.Plugin.";
    private const string NewLineForPipe = "\n";

    private static readonly string ExeDir =
        Path.GetDirectoryName(Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location) ?? string.Empty;

    private static bool HasAnyPluginFolder(string pluginsDir)
    {
        if (!Directory.Exists(pluginsDir)) return false;
        foreach (var folder in Directory.GetDirectories(pluginsDir))
            if (File.Exists(Path.Combine(folder, ManifestFileName))) return true;
        return false;
    }

    /// <summary>
    /// <c>PluginHost.exe</c> とプラグインのフォルダが揃っていれば、起動の待ち合わせを用意してホストを起動する。
    /// 起動を指示できたとき <c>true</c>。どちらかが欠けている、または起動に失敗したときは <c>false</c>（起動済みの記録は立てない）。
    /// 呼び出し側で <see cref="_sync"/> を保持していること。呼び出し前に <see cref="_started"/> が <c>false</c> であること。
    /// </summary>
    private bool TryStartHost()
    {
        var hostPath   = Path.Combine(ExeDir, HostFileName);
        var pluginsDir = Path.Combine(ExeDir, PluginsDirName);
        if (!File.Exists(hostPath) || !HasAnyPluginFolder(pluginsDir)) return false;

        _startupCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _started           = true;
        return StartHost(hostPath, pluginsDir);
    }

    /// <summary>呼び出し側で <see cref="_sync"/> を保持していること。起動に失敗したときは後始末をして起動済みの記録を戻し、<c>false</c> を返す。</summary>
    private bool StartHost(string hostPath, string pluginsDir)
    {
        try
        {
            var pipeName = PipeNamePrefix + Environment.ProcessId;
            var startCts = new CancellationTokenSource();
            _cts    = startCts;
            _pipe   = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            _reader = new StreamReader(_pipe, encoding);
            _writer = new StreamWriter(_pipe, encoding) { AutoFlush = false, NewLine = NewLineForPipe };

            var sandboxedHost = PluginSandbox.Start(hostPath, pipeName, pluginsDir, ExeDir);
            _hostProcess = sandboxedHost.Process;
            _jobHandle   = sandboxedHost.JobHandle;

            // ホストが終了したらこの起動の通信路を取り消し、接続待ち・受信中のどちらでも切断の後始末へ入れる。
            // 次の起動で _cts が差し替わっても巻き込まないよう、この起動で作ったものを直接使う
            sandboxedHost.Process.EnableRaisingEvents = true;
            sandboxedHost.Process.Exited += (_, _) =>
            {
                try { startCts.Cancel(); } catch { }
            };

            var token = _cts.Token;
            _ = Task.Run(() => RunAsync(token));
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.PluginHostStartFailed, null, ex.Message);
            Cleanup();
            _started = false;
            return false;
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        NamedPipeServerStream? pipe;
        StreamReader? reader;
        lock (_sync)
        {
            pipe   = _pipe;
            reader = _reader;
        }
        if (pipe == null || reader == null) return;

        try
        {
            await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
        }
        catch
        {
            HandleDisconnect();
            return;
        }

        while (!token.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(token).ConfigureAwait(false);
            }
            catch
            {
                break;
            }
            if (line == null) break;

            try { Dispatch(line); }
            catch { /* 1メッセージの処理失敗で受信ループを止めない */ }
        }

        HandleDisconnect();
    }

    private void HandleDisconnect()
    {
        TaskCompletionSource<bool>? startupCompletion;
        lock (_sync)
        {
            Cleanup();
            FailActiveInvokes();

            // どの経路でも必ず通る。次に依頼が来たとき、起動し直せる状態へ戻す（自動での再起動は行わない）
            _connected        = false;
            _started          = false;
            startupCompletion = _startupCompletion;

            if (!_stopping && !_disabled) AppLogger.Log(LogMsg.PluginHostExited);
        }

        // 接続前の切断でも、起動を待っている側を失敗として解放する（完了済みなら何も起きない）
        startupCompletion?.TrySetResult(false);
    }

    /// <summary>呼び出し側で <see cref="_sync"/> を保持していること。</summary>
    private void Cleanup()
    {
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        // 枠を閉じると、残っているホストのプロセスも道連れに終了する
        try { _jobHandle?.Dispose(); } catch { }
        _writer    = null;
        _reader    = null;
        _pipe      = null;
        _jobHandle = null;
    }

    private void FailActiveInvokes()
    {
        foreach (var context in _activeInvokes.Values) context.Fail();
    }
}
