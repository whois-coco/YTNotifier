using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace YTNotifier.Services;

/// <summary>
/// プラグインを動かす別プロセス <c>PluginHost.exe</c> を、Windows の仕組みで締めて起動する。
///
/// <list type="bullet">
///   <item>整合性レベルを「低」へ引き下げたトークンで起動する（書き込める場所が大きく減る）</item>
///   <item>資源の枠（Job Object）へ入れ、メモリ上限・CPU 上限・子プロセス禁止を課す</item>
///   <item>本体が用意した名前付きパイプに「低」の必須ラベルを付け、低整合性のホストから通れるようにする</item>
/// </list>
///
/// いずれかが整えられなかった場合は例外を投げ、プラグイン機構を起動しない（締められないなら動かさない）。
/// Windows API の宣言・構造体・定数はこのファイルの中だけで使う。
/// </summary>
internal static class PluginSandbox
{
    // ── パイプの必須ラベル（対策C） ────────────────────────────────────────

    /// <summary>低整合性の必須ラベル。NoWriteUp のみを付け、読み取り・実行は制限しない。</summary>
    private const string LowIntegrityLabelSddl = "S:(ML;;NW;;;LW)";
    private const uint   SddlRevision1         = 1;
    private const int    LabelSecurityInformation = 0x00000010;

    // ── トークン（対策A） ──────────────────────────────────────────────────

    /// <summary>整合性レベル「低」を表す識別子（SECURITY_MANDATORY_LOW_RID = 0x1000）。</summary>
    private const string LowIntegritySid = "S-1-16-4096";

    private const uint TokenAssignPrimary  = 0x00000001;
    private const uint TokenDuplicate      = 0x00000002;
    private const uint TokenQuery          = 0x00000008;
    private const uint TokenAdjustDefault  = 0x00000080;
    private const uint TokenAdjustSessionId = 0x00000100;

    private const uint MaximumAllowed = 0x02000000;

    /// <summary>SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation</summary>
    private const int SecurityImpersonationLevel = 2;

    /// <summary>TOKEN_TYPE.TokenPrimary</summary>
    private const int TokenTypePrimary = 1;

    /// <summary>TOKEN_INFORMATION_CLASS.TokenIntegrityLevel</summary>
    private const int TokenIntegrityLevel = 25;

    private const uint SeGroupIntegrity = 0x00000020;

    /// <summary>CreateRestrictedToken に何も削らせないときの指定（フラグ・件数ともに0）。</summary>
    private const uint RestrictedTokenNoFlags = 0;
    private const uint RestrictedTokenNoEntries = 0;

    // ── プロセス生成（対策A） ──────────────────────────────────────────────

    private const uint CreateSuspended         = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow          = 0x08000000;

    /// <summary>ERROR_PRIVILEGE_NOT_HELD（この特権が無い環境では制限なしトークンを挟んで再挑戦する）。</summary>
    private const int ErrorPrivilegeNotHeld = 1314;

    /// <summary>ResumeThread が失敗したときの戻り値（(DWORD)-1）。</summary>
    private const uint ResumeThreadFailed = uint.MaxValue;

    /// <summary>枠の用意に失敗して起動を取りやめるとき、中断中のプロセスへ与える終了コード。</summary>
    private const uint SandboxSetupFailedExitCode = 1;

    /// <summary>起動時の引数の並び。実行ファイル・パイプ名・Plugins フォルダの順（空白を含み得るので括る）。</summary>
    private const string CommandLineFormat = "\"{0}\" \"{1}\" \"{2}\"";

    // ── 資源の枠（対策B） ──────────────────────────────────────────────────

    /// <summary>JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation</summary>
    private const int JobObjectExtendedLimitInformation = 9;

    /// <summary>JOBOBJECTINFOCLASS.JobObjectCpuRateControlInformation</summary>
    private const int JobObjectCpuRateControlInformation = 15;

    private const uint JobObjectLimitActiveProcess         = 0x00000008;
    private const uint JobObjectLimitJobMemory             = 0x00000200;
    private const uint JobObjectLimitDieOnUnhandledException = 0x00000400;
    private const uint JobObjectLimitKillOnJobClose        = 0x00002000;

    private const uint JobObjectCpuRateControlEnable  = 0x00000001;
    private const uint JobObjectCpuRateControlHardCap = 0x00000004;

    /// <summary>枠全体で使えるメモリの上限（512MiB）。正常な要約1回はこの値に遠く及ばない。</summary>
    private const long JobMemoryLimitBytes = 512L * 1024 * 1024;

    /// <summary>ホストは子プロセスを起動しない設計なので、動かせるプロセスは自分1つだけ。</summary>
    private const uint ActiveProcessLimit = 1;

    /// <summary>CPU の上限。全 CPU 合計の 1/100 % 単位で、5000 = 50.00%。</summary>
    private const uint CpuHardCapRate = 5000;

    // ── 失敗の知らせ方 ─────────────────────────────────────────────────────

    private const string FailureMessageFormat    = "{0} (0x{1:X8})";
    private const string FailureWithReasonFormat = "{0}: {1}";

    private const string StageOpenToken         = "本体のトークンを開けませんでした";
    private const string StageDuplicateToken    = "トークンの複製に失敗しました";
    private const string StageRestrictToken     = "トークンの作り直しに失敗しました";
    private const string StageLowIntegritySid   = "低整合性の識別子を作れませんでした";
    private const string StageLowIntegrityToken = "低整合性トークンの生成に失敗しました";
    private const string StagePipeLabelBuild    = "パイプのラベルの組み立てに失敗しました";
    private const string StagePipeLabelApply    = "パイプのラベル設定に失敗しました";
    private const string StageProcessStart      = "プロセスの起動に失敗しました";
    private const string StageProcessLookup     = "起動したプロセスを見つけられませんでした";
    private const string StageJobCreate         = "資源の枠の作成に失敗しました";
    private const string StageJobLimit          = "資源の上限の設定に失敗しました";
    private const string StageJobCpuRate        = "CPU の上限の設定に失敗しました";
    private const string StageJobAssign         = "資源の枠への割り当てに失敗しました";
    private const string StageResumeThread      = "プロセスの再開に失敗しました";

    // ── 公開する入り口 ─────────────────────────────────────────────────────

    /// <summary>
    /// カーネルオブジェクト（名前付きパイプ）へ低整合性の必須ラベルを付ける。
    /// これが無いと、低整合性で動くホストは中整合性のパイプへ接続できない。
    /// </summary>
    internal static void ApplyLowIntegrityLabel(SafeHandle objectHandle)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                LowIntegrityLabelSddl, SddlRevision1, out var rawSecurityDescriptor, out _))
        {
            throw Failure(StagePipeLabelBuild);
        }

        using var securityDescriptor = new SafeLocalMemoryHandle(rawSecurityDescriptor);

        if (!SetKernelObjectSecurity(objectHandle, LabelSecurityInformation,
                securityDescriptor.DangerousGetHandle()))
        {
            throw Failure(StagePipeLabelApply);
        }
    }

    /// <summary>
    /// 低整合性のトークンで <c>PluginHost.exe</c> を中断状態で起動し、資源の枠へ入れてから走らせる。
    /// 失敗した場合は例外を投げる（中途半端に緩い状態では起動しない）。
    /// </summary>
    internal static SandboxedHost Start(string exePath, string pipeName, string pluginsDir, string workingDirectory)
    {
        var commandLine = string.Format(CommandLineFormat, exePath, pipeName, pluginsDir);

        try
        {
            return StartSandboxed(exePath, commandLine, workingDirectory, useRestrictedToken: false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorPrivilegeNotHeld)
        {
            // 「主トークンの割り当て」特権を持たない環境向けの保険。何も削らない CreateRestrictedToken を
            // 一度挟むと、自分自身から派生したトークンとして扱われ特権なしでも起動できる。
            return StartSandboxed(exePath, commandLine, workingDirectory, useRestrictedToken: true);
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

    // ── 起動の本体 ─────────────────────────────────────────────────────────

    private static SandboxedHost StartSandboxed(string exePath, string commandLine,
        string workingDirectory, bool useRestrictedToken)
    {
        using var launchToken = CreateLowIntegrityToken(useRestrictedToken);

        var startupInformation = new STARTUPINFO { cb = (uint)Marshal.SizeOf<STARTUPINFO>() };
        var commandLineBuffer  = new StringBuilder(commandLine);

        // 環境変数は本体から引き継ぐ。単一ファイルのホストが低整合性で自分を展開できない場合は、
        // ここで DOTNET_BUNDLE_EXTRACT_BASE_DIR を差した環境ブロックを渡す（既定では付けない）。
        var environmentBlock = IntPtr.Zero;

        if (!CreateProcessAsUserW(launchToken, exePath, commandLineBuffer, IntPtr.Zero, IntPtr.Zero,
                inheritHandles: false,
                CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment,
                environmentBlock, workingDirectory, ref startupInformation, out var processInformation))
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

    // ── トークンの用意 ─────────────────────────────────────────────────────

    /// <summary>本体のトークンを複製し、整合性レベルだけを「低」へ引き下げたものを作る。</summary>
    private static SafeTokenHandle CreateLowIntegrityToken(bool useRestrictedToken)
    {
        using var processToken = OpenCurrentProcessToken();

        var duplicateToken = DuplicatePrimaryToken(processToken);

        SafeTokenHandle launchToken;
        if (useRestrictedToken)
        {
            try { launchToken = CreateUnfilteredRestrictedToken(duplicateToken); }
            finally { duplicateToken.Dispose(); }
        }
        else
        {
            launchToken = duplicateToken;
        }

        try
        {
            LowerToLowIntegrity(launchToken);
            return launchToken;
        }
        catch
        {
            launchToken.Dispose();
            throw;
        }
    }

    private static SafeTokenHandle OpenCurrentProcessToken()
    {
        const uint desiredAccess = TokenDuplicate | TokenQuery | TokenAssignPrimary
                                   | TokenAdjustDefault | TokenAdjustSessionId;

        if (!OpenProcessToken(GetCurrentProcess(), desiredAccess, out var rawToken))
            throw Failure(StageOpenToken);

        return new SafeTokenHandle(rawToken);
    }

    private static SafeTokenHandle DuplicatePrimaryToken(SafeTokenHandle sourceToken)
    {
        if (!DuplicateTokenEx(sourceToken, MaximumAllowed, IntPtr.Zero,
                SecurityImpersonationLevel, TokenTypePrimary, out var rawToken))
        {
            throw Failure(StageDuplicateToken);
        }

        return new SafeTokenHandle(rawToken);
    }

    /// <summary>権利を一切削らない作り直し。特権が足りない環境でも主トークンとして使えるようにするためだけに使う。</summary>
    private static SafeTokenHandle CreateUnfilteredRestrictedToken(SafeTokenHandle sourceToken)
    {
        if (!CreateRestrictedToken(sourceToken, RestrictedTokenNoFlags,
                RestrictedTokenNoEntries, IntPtr.Zero,
                RestrictedTokenNoEntries, IntPtr.Zero,
                RestrictedTokenNoEntries, IntPtr.Zero, out var rawToken))
        {
            throw Failure(StageRestrictToken);
        }

        return new SafeTokenHandle(rawToken);
    }

    private static void LowerToLowIntegrity(SafeTokenHandle token)
    {
        if (!ConvertStringSidToSidW(LowIntegritySid, out var rawSid))
            throw Failure(StageLowIntegritySid);

        using var lowIntegritySid = new SafeLocalMemoryHandle(rawSid);

        var mandatoryLabel = new TOKEN_MANDATORY_LABEL
        {
            Label = new SID_AND_ATTRIBUTES
            {
                Sid        = lowIntegritySid.DangerousGetHandle(),
                Attributes = SeGroupIntegrity,
            },
        };

        var labelSize = (uint)Marshal.SizeOf<TOKEN_MANDATORY_LABEL>()
                        + GetLengthSid(lowIntegritySid.DangerousGetHandle());

        if (!SetTokenInformation(token, TokenIntegrityLevel, ref mandatoryLabel, labelSize))
            throw Failure(StageLowIntegrityToken);
    }

    // ── 資源の枠の用意 ─────────────────────────────────────────────────────

    /// <summary>メモリ上限・CPU 上限・子プロセス禁止を設定した枠を作る。</summary>
    private static SafeJobHandle CreateSandboxJob()
    {
        var rawJob = CreateJobObjectW(IntPtr.Zero, null);
        if (rawJob == IntPtr.Zero) throw Failure(StageJobCreate);

        var jobHandle = new SafeJobHandle(rawJob);
        try
        {
            var extendedLimit = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            extendedLimit.BasicLimitInformation.LimitFlags =
                JobObjectLimitJobMemory | JobObjectLimitActiveProcess
                | JobObjectLimitKillOnJobClose | JobObjectLimitDieOnUnhandledException;
            extendedLimit.BasicLimitInformation.ActiveProcessLimit = ActiveProcessLimit;
            extendedLimit.JobMemoryLimit = (nuint)JobMemoryLimitBytes;

            if (!SetInformationJobObject(jobHandle, JobObjectExtendedLimitInformation,
                    ref extendedLimit, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            {
                throw Failure(StageJobLimit);
            }

            var cpuRateControl = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
            {
                ControlFlags = JobObjectCpuRateControlEnable | JobObjectCpuRateControlHardCap,
                CpuRate      = CpuHardCapRate,
            };

            if (!SetInformationJobObject(jobHandle, JobObjectCpuRateControlInformation,
                    ref cpuRateControl, (uint)Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>()))
            {
                throw Failure(StageJobCpuRate);
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

    private sealed class SafeTokenHandle : SafeKernelObjectHandle
    {
        internal SafeTokenHandle(IntPtr existingHandle) : base(existingHandle) { }
    }

    private sealed class SafeThreadHandle : SafeKernelObjectHandle
    {
        internal SafeThreadHandle(IntPtr existingHandle) : base(existingHandle) { }
    }

    private sealed class SafeJobHandle : SafeKernelObjectHandle
    {
        internal SafeJobHandle(IntPtr existingHandle) : base(existingHandle) { }
    }

    /// <summary>LocalFree で解放する領域（セキュリティ記述子・識別子）。</summary>
    private sealed class SafeLocalMemoryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeLocalMemoryHandle(IntPtr existingHandle) : base(ownsHandle: true)
            => SetHandle(existingHandle);

        protected override bool ReleaseHandle() => LocalFree(handle) == IntPtr.Zero;
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
    private struct SID_AND_ATTRIBUTES
    {
        public IntPtr Sid;
        public uint   Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_MANDATORY_LABEL
    {
        public SID_AND_ATTRIBUTES Label;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl, uint sddlRevision, out IntPtr securityDescriptor, out uint securityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(
        SafeHandle objectHandle, int securityInformation, IntPtr securityDescriptor);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSidToSidW(string stringSid, out IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetLengthSid(IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(SafeHandle existingToken, uint desiredAccess,
        IntPtr tokenAttributes, int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(SafeHandle existingToken, uint flags,
        uint disableSidCount, IntPtr sidsToDisable,
        uint deletePrivilegeCount, IntPtr privilegesToDelete,
        uint restrictedSidCount, IntPtr sidsToRestrict, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetTokenInformation(SafeHandle token, int tokenInformationClass,
        ref TOKEN_MANDATORY_LABEL tokenInformation, uint tokenInformationLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUserW(SafeHandle token, string? applicationName,
        StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags,
        IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

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
    private static extern bool SetInformationJobObject(SafeHandle job, int jobObjectInformationClass,
        ref JOBOBJECT_CPU_RATE_CONTROL_INFORMATION jobObjectInformation, uint jobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeHandle job, SafeHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
