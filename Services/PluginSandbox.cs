using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace YTNotifier.Services;

/// <summary>
/// プラグインを動かす別プロセス <c>PluginHost.exe</c> を、孤児化しないように最小限の枠へ入れて起動する。
///
/// <list type="bullet">
///   <item>OS レベルの締めつけ（整合性レベルの引き下げ・資源上限・子プロセス禁止・パイプの必須ラベル）は行わない。
///         その責務は <c>PluginHost.exe</c> 側へ移した</item>
///   <item>本体がぶら下げるのは <c>KILL_ON_JOB_CLOSE</c> だけを設定した Job Object。
///         本体が落ちて枠のハンドルが閉じれば、残ったホストのプロセスも道連れに終了する</item>
///   <item>枠へ入る前に走り出さないよう <c>CREATE_SUSPENDED</c> で起動し、
///         <c>AssignProcessToJobObject</c> の後に <c>ResumeThread</c> で動かす</item>
/// </list>
///
/// 枠の作成・割り当て・プロセス起動のいずれかが失敗した場合は例外を投げ、プラグイン機構を起動しない。
/// Windows API の宣言・構造体・定数はこのファイルの中だけで使う。
/// </summary>
internal static class PluginSandbox
{
    // ── プロセス生成 ───────────────────────────────────────────────────────

    private const uint CreateSuspended         = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow          = 0x08000000;

    /// <summary>ResumeThread が失敗したときの戻り値（(DWORD)-1）。</summary>
    private const uint ResumeThreadFailed = uint.MaxValue;

    /// <summary>枠の用意に失敗して起動を取りやめるとき、中断中のプロセスへ与える終了コード。</summary>
    private const uint SandboxSetupFailedExitCode = 1;

    /// <summary>起動時の引数の並び。実行ファイル・パイプ名・Plugins フォルダの順（空白を含み得るので括る）。</summary>
    private const string CommandLineFormat = "\"{0}\" \"{1}\" \"{2}\"";

    // ── 資源の枠 ───────────────────────────────────────────────────────────

    /// <summary>JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation</summary>
    private const int JobObjectExtendedLimitInformation = 9;

    /// <summary>枠のハンドルが閉じたら中のプロセスも終了させる。孤児化防止のためだけに使う唯一の設定。</summary>
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    // ── 失敗の知らせ方 ─────────────────────────────────────────────────────

    private const string FailureMessageFormat    = "{0} (0x{1:X8})";
    private const string FailureWithReasonFormat = "{0}: {1}";

    private const string StageProcessStart  = "プロセスの起動に失敗しました";
    private const string StageProcessLookup = "起動したプロセスを見つけられませんでした";
    private const string StageJobCreate     = "資源の枠の作成に失敗しました";
    private const string StageJobLimit      = "資源の枠の設定に失敗しました";
    private const string StageJobAssign     = "資源の枠への割り当てに失敗しました";
    private const string StageResumeThread  = "プロセスの再開に失敗しました";

    // ── 公開する入り口 ─────────────────────────────────────────────────────

    /// <summary>
    /// <c>PluginHost.exe</c> を中断状態で起動し、最小の枠へ入れてから走らせる。
    /// 失敗した場合は例外を投げる（枠へ入れられないなら起動しない）。
    /// </summary>
    internal static SandboxedHost Start(string exePath, string pipeName, string pluginsDir, string workingDirectory)
    {
        var commandLine        = string.Format(CommandLineFormat, exePath, pipeName, pluginsDir);
        var startupInformation = new STARTUPINFO { cb = (uint)Marshal.SizeOf<STARTUPINFO>() };
        var commandLineBuffer  = new StringBuilder(commandLine);

        if (!CreateProcessW(exePath, commandLineBuffer, IntPtr.Zero, IntPtr.Zero,
                inheritHandles: false,
                CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment,
                IntPtr.Zero, workingDirectory, ref startupInformation, out var processInformation))
        {
            throw Failure(StageProcessStart);
        }

        using var processHandle = new SafeProcessHandle(processInformation.hProcess, ownsHandle: true);
        using var threadHandle  = new SafeThreadHandle(processInformation.hThread);

        var jobHandle = CreateSandboxJob();
        try
        {
            if (!AssignProcessToJobObject(jobHandle, processHandle)) throw Failure(StageJobAssign);

            var hostProcess = OpenStartedProcess(processInformation.dwProcessId);

            if (ResumeThread(threadHandle) == ResumeThreadFailed) throw Failure(StageResumeThread);

            return new SandboxedHost(hostProcess, jobHandle);
        }
        catch
        {
            // 中断したままのプロセスを残さない（枠へ入る前に失敗した場合は道連れにできないため明示的に止める）
            try { TerminateProcess(processHandle, SandboxSetupFailedExitCode); } catch { }
            jobHandle.Dispose();
            throw;
        }
    }

    /// <summary>起動したホストのプロセスと、それを閉じ込めている資源の枠。</summary>
    internal sealed class SandboxedHost
    {
        internal SandboxedHost(Process process, SafeHandle jobHandle)
        {
            Process   = process;
            JobHandle = jobHandle;
        }

        /// <summary>ホストのプロセス。</summary>
        internal Process Process { get; }

        /// <summary>資源の枠。閉じるとホストのプロセスも道連れに終了する。</summary>
        internal SafeHandle JobHandle { get; }
    }

    // ── 起動を支える処理 ───────────────────────────────────────────────────

    private static Process OpenStartedProcess(int processId)
    {
        try
        {
            return Process.GetProcessById(processId);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                string.Format(FailureWithReasonFormat, StageProcessLookup, ex.Message), ex);
        }
    }

    /// <summary><c>KILL_ON_JOB_CLOSE</c> だけを設定した枠を作る（孤児化防止のみ）。</summary>
    private static SafeJobHandle CreateSandboxJob()
    {
        var rawJob = CreateJobObjectW(IntPtr.Zero, null);
        if (rawJob == IntPtr.Zero) throw Failure(StageJobCreate);

        var jobHandle = new SafeJobHandle(rawJob);
        try
        {
            var extendedLimit = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            extendedLimit.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

            if (!SetInformationJobObject(jobHandle, JobObjectExtendedLimitInformation,
                    ref extendedLimit, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            {
                throw Failure(StageJobLimit);
            }

            return jobHandle;
        }
        catch
        {
            jobHandle.Dispose();
            throw;
        }
    }

    /// <summary>直前の Windows API の失敗を、段階が分かる文言と番号にして返す。</summary>
    private static Win32Exception Failure(string stage)
    {
        var errorCode = Marshal.GetLastWin32Error();
        return new Win32Exception(errorCode, string.Format(FailureMessageFormat, stage, errorCode));
    }

    // ── ハンドルの後始末 ───────────────────────────────────────────────────

    /// <summary>CloseHandle で閉じるカーネルオブジェクトのハンドル。</summary>
    private class SafeKernelObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeKernelObjectHandle(IntPtr existingHandle) : base(ownsHandle: true)
            => SetHandle(existingHandle);

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    private sealed class SafeThreadHandle : SafeKernelObjectHandle
    {
        internal SafeThreadHandle(IntPtr existingHandle) : base(existingHandle) { }
    }

    private sealed class SafeJobHandle : SafeKernelObjectHandle
    {
        internal SafeJobHandle(IntPtr existingHandle) : base(existingHandle) { }
    }

    // ── Windows API ────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public uint   cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public uint   dwX;
        public uint   dwY;
        public uint   dwXSize;
        public uint   dwYSize;
        public uint   dwXCountChars;
        public uint   dwYCountChars;
        public uint   dwFillAttribute;
        public uint   dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int    dwProcessId;
        public int    dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long  PerProcessUserTimeLimit;
        public long  PerJobUserTimeLimit;
        public uint  LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint  ActiveProcessLimit;
        public nuint Affinity;
        public uint  PriorityClass;
        public uint  SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? applicationName,
        StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags,
        IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeHandle process, uint exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeHandle job, int jobObjectInformationClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION jobObjectInformation, uint jobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeHandle job, SafeHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
